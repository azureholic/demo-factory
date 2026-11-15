using System.Text;
using System.Text.Json;
using DemoFactory.Agents;
using DemoFactory.Copilot;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using DemoFactory.Orchestrator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DemoFactory.Api;

public sealed record OnboardRepositoryRequest(
    string Owner,
    string Name,
    long InstallationId = 90421001,
    int GitHubProjectNumber = 1,
    AutonomyLevel Autonomy = AutonomyLevel.L1,
    string CodingModel = "gemini-4-carbon");

public sealed record WaiveFindingRequest(string Fingerprint, string Reason);
public sealed record ClarifyWorkItemRequest(string? ClarificationMarkdown, bool ProceedWithDefaults = false);
public sealed record UpdateWorkItemSpecRequest(string Title, string? BodyMarkdown, IReadOnlyList<string>? AcceptanceCriteria);
public sealed record ApprovalDecisionRequest(string? Reason);
public sealed record MoveBoardCardRequest(BoardStatus ToStatus);
public sealed record SimulateLineStopRequest(string Cause, IReadOnlyList<int> SuspectPrNumbers, bool FlakeRetryPassed = false);

public sealed record UpdateRuntimeSettingsRequest(
    string? CodingModel,
    string? PmModelDeployment,
    int? Wip,
    AutonomyLevel? Autonomy,
    int? MaxRounds,
    bool? Paused,
    string? TeamsWebhookUrl,
    bool? DailyDigestEnabled);

public sealed record UpsertModelCatalogueRequest(
    string DisplayName,
    string ApiModelId,
    bool Enabled,
    string? Notes);

public sealed record SetExecutionModeRequest(
    bool SandboxMode,
    bool Force = false);

public static class OperationsAndGovernanceEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOperationsAndGovernanceEndpoints(this IEndpointRouteBuilder app)
    {
        // §4.1 & §13: GitHub Webhook Receiver (HMAC-SHA256 verified + de-duplicated on X-GitHub-Delivery)
        app.MapPost("/webhooks/github", async (
            HttpContext http,
            IWebhookEventBus eventBus,
            IFactoryOrchestrationCoordinator coordinator,
            IOptions<GitHubAppOptions> gitHubOptions,
            IOptions<FactoryEnvironmentOptions> envOptions,
            CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            var rawBytes = ms.ToArray();

            var sig = http.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            if (!WebhookSignatureVerifier.VerifyHmacSha256(rawBytes, sig, gitHubOptions.Value.WebhookSecret))
            {
                return Results.Unauthorized();
            }

            var deliveryId = http.Request.Headers["X-GitHub-Delivery"].FirstOrDefault();
            var eventType = http.Request.Headers["X-GitHub-Event"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(deliveryId) || string.IsNullOrWhiteSpace(eventType))
                return Results.BadRequest(new { error = "Missing X-GitHub-Delivery or X-GitHub-Event header." });

            var payloadJson = Encoding.UTF8.GetString(rawBytes);
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var act) ? act.GetString() : null;
            var sender = root.TryGetProperty("sender", out var snd) && snd.TryGetProperty("login", out var l) ? l.GetString() : null;
            var repoFullName = root.TryGetProperty("repository", out var rp) && rp.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;

            var envelope = new WebhookEnvelope(deliveryId, eventType, action, sender, repoFullName, payloadJson, DateTimeOffset.UtcNow);
            var isNew = await eventBus.EnqueueIfUniqueAsync(envelope, ct);
            if (!isNew)
            {
                return Results.Ok(new { accepted = true, deduplicated = true, deliveryId });
            }

            // In synchronous test/lean requests where immediate consistency is helpful, ensure processed if not already picked up
            if (http.Request.Headers.ContainsKey("X-DemoFactory-Sync-Process"))
            {
                await coordinator.ProcessWebhookEnvelopeAsync(envelope, ct);
            }

            return Results.Accepted(value: new { accepted = true, deduplicated = false, deliveryId });
        });

        // Repositories & Onboarding Wizard (§13, §14.2 Screen 6)
        app.MapGet("/api/repos", async (IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repos = await db.Repositories.OrderBy(r => r.Name).ToListAsync(ct);
            var lineStates = await db.LineStates.ToDictionaryAsync(l => l.RepoId, ct);
            var workItems = await db.WorkItems.ToListAsync(ct);
            var inbox = await db.InboxItems
                .Where(i => i.State == InboxItemState.New && i.FeatureId == null)
                .ToListAsync(ct);

            return Results.Ok(repos.Select(r =>
            {
                var rItems = workItems.Where(w => w.RepoId == r.Id).ToList();
                var line = lineStates.GetValueOrDefault(r.Id);
                return new
                {
                    r.Id,
                    r.ProjectId,
                    r.Owner,
                    r.Name,
                    fullName = r.FullName,
                    r.GitHubProjectId,
                    r.GitHubProjectNumber,
                    r.PolicyReference,
                    r.Paused,
                    r.PauseReason,
                    r.ReadinessPassed,
                    lineStatus = line?.State ?? LineStatus.Running,
                    lineCause = line?.Cause,
                    inFlightCount = rItems.Count(w => w.BoardStatus is BoardStatus.InProgress or BoardStatus.InReview),
                    readyQueueDepth = rItems.Count(w => w.BoardStatus == BoardStatus.Ready),
                    blockedCount = rItems.Count(w => w.BoardStatus == BoardStatus.Blocked),
                    newInboxCount = inbox.Count(i => i.RepoId == r.Id)
                };
            }));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/api/repos", async (
            OnboardRepositoryRequest req,
            HttpContext http,
            IGitHubGateway gitHub,
            IPolicyRepository policyRepo,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.Repositories.FirstOrDefaultAsync(r => r.Owner == req.Owner && r.Name == req.Name, ct);
            if (existing is not null)
                return Results.Conflict(new { error = $"Repository {req.Owner}/{req.Name} is already onboarded." });

            var project = new ProjectEntity
            {
                Name = req.Name,
                Slug = req.Name.ToLowerInvariant(),
                SpecMarkdown = $"# {req.Name}\n\nOnboarded repository managed by Demo-Factory.",
                State = ProjectState.Running,
                DefaultAutonomy = req.Autonomy,
                DefaultCodingModel = req.CodingModel
            };
            db.Projects.Add(project);

            var readiness = await gitHub.EvaluateRepositoryReadinessAsync(req.Owner, req.Name, ct);
            var policyPath = await policyRepo.EnsureDefaultPolicyFileAsync(req.Owner, req.Name, req.GitHubProjectNumber, req.Autonomy, ct);

            var repo = new RepositoryEntity
            {
                ProjectId = project.Id,
                Owner = req.Owner,
                Name = req.Name,
                InstallationId = req.InstallationId,
                GitHubProjectId = $"PVT_onboarded_{req.Name}",
                GitHubProjectNumber = req.GitHubProjectNumber,
                PolicyReference = policyPath,
                ReadinessPassed = readiness.All(x => x.Passed),
                ReadinessReportJson = JsonSerializer.Serialize(readiness, JsonOpts)
            };
            db.Repositories.Add(repo);
            db.LineStates.Add(new LineStateEntity { RepoId = repo.Id, State = LineStatus.Running });

            db.RuntimeSettings.Add(new RuntimeSettingsEntity
            {
                Scope = SettingsScope.Repo,
                TargetId = repo.Id,
                Autonomy = req.Autonomy,
                CodingModel = req.CodingModel,
                Wip = 1,
                MaxRounds = 3,
                UpdatedBy = http.User.Identity?.Name ?? "admin"
            });

            await db.SaveChangesAsync(ct);
            await auditLogger.RecordAsync(http.User.Identity?.Name ?? "admin", "RepositoryOnboarded", "Repository", repo.Id.ToString(), after: new { repo.FullName, req.Autonomy, req.CodingModel }, ct: ct);
            return Results.Created($"/api/repos/{repo.Id}/readiness", new { repo, readiness });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapGet("/api/repos/{id:guid}/readiness", async (
            Guid id,
            IGitHubGateway gitHub,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (repo is null)
                return Results.NotFound();

            var checks = await gitHub.EvaluateRepositoryReadinessAsync(repo.Owner, repo.Name, ct);
            return Results.Ok(new { repoId = repo.Id, repoFullName = repo.FullName, passed = checks.All(c => c.Passed), checks });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/api/repos/{id:guid}/readiness:fix", async (
            Guid id,
            IGitHubGateway gitHub,
            IFactoryOrchestrationCoordinator coordinator,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (repo is null)
                return Results.NotFound();

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == repo.ProjectId, ct);
            await gitHub.ApplySecurityBaselineAsync(repo.Owner, repo.Name, true, ct);
            await gitHub.ApplyRulesetsAndSettingsAsync(repo.Owner, repo.Name, ["build", "test", "lint"], ct);
            await gitHub.EnsureWorkflowsAndRulesetReadyAsync(repo.InstallationId, repo.Owner, repo.Name, project?.TemplateId ?? "dotnet-react", ["build", "test", "lint"], ct);
            await coordinator.ReconcileActiveWorkItemsOnceAsync(repo.Id, ct);
            return Results.Ok(new { fixedSettings = true });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        // Stop-the-Line endpoints (§6.2, §13)
        app.MapGet("/api/repos/{id:guid}/line", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var line = await db.LineStates.FirstOrDefaultAsync(l => l.RepoId == id, ct);
            if (line is null)
                return Results.NotFound();

            return Results.Ok(new
            {
                line.RepoId,
                line.State,
                line.Cause,
                suspectPrNumbers = JsonSerializer.Deserialize<List<int>>(line.SuspectPrNumbersJson, JsonOpts) ?? [],
                line.ActiveAction,
                line.FlakyTestCount,
                line.StoppedAt,
                line.RestoredAt,
                line.LastRestoreDurationSeconds
            });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/api/repos/{target}/line:resume", async (
            Guid target,
            HttpContext http,
            IFactoryOrchestrationCoordinator coordinator,
            CancellationToken ct) =>
        {
            await coordinator.ResumeLineAsync(target, http.User.Identity?.Name ?? "admin", ct);
            return Results.Ok(new { repoId = target, state = LineStatus.Running });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapPost("/api/repos/{target}/line:simulate-failure", async (
            Guid target,
            SimulateLineStopRequest req,
            IFactoryOrchestrationCoordinator coordinator,
            CancellationToken ct) =>
        {
            await coordinator.TriggerStopTheLineAsync(target, req.Cause, req.SuspectPrNumbers, null, req.FlakeRetryPassed, ct);
            return Results.Ok(new { repoId = target, triggered = true });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        // Board & Dependency Graph (§13, §14.2 Screens 2 & 3)
        app.MapGet("/api/repos/{id:guid}/board", async (
            Guid id,
            string? feature,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (repo is null)
                return Results.NotFound();

            var query = db.WorkItems.Where(w => w.RepoId == id);
            if (!string.IsNullOrWhiteSpace(feature))
                query = query.Where(w => w.FeatureSlug == feature);

            var allRepoItems = await db.WorkItems.Where(w => w.RepoId == id).ToListAsync(ct);
            var filtered = await query.OrderByDescending(w => w.Priority).ThenBy(w => w.IssueNumber).ToListAsync(ct);
            var activeSessions = await db.AgentSessions
                .Where(s => s.RepoId == id && (s.State == CopilotTaskState.Queued || s.State == CopilotTaskState.InProgress))
                .ToListAsync(ct);

            var openIssueSet = allRepoItems
                .Where(w => w.BoardStatus != BoardStatus.Done && w.State != WorkItemStateKind.Merged && w.State != WorkItemStateKind.Cancelled)
                .Select(w => w.IssueNumber)
                .ToHashSet();

            var cards = filtered.Select(w =>
            {
                var blockers = JsonSerializer.Deserialize<List<int>>(w.BlockedByIssueNumbersJson, JsonOpts) ?? [];
                var openBlockers = blockers.Where(openIssueSet.Contains).ToList();
                var unblocksCount = allRepoItems.Count(other =>
                    (JsonSerializer.Deserialize<List<int>>(other.BlockedByIssueNumbersJson, JsonOpts) ?? []).Contains(w.IssueNumber));
                var liveSession = activeSessions.FirstOrDefault(s => s.WorkItemId == w.Id);

                return new
                {
                    w.Id,
                    w.IssueNumber,
                    w.Title,
                    w.State,
                    w.BoardStatus,
                    w.Priority,
                    w.Size,
                    w.PassesWorkLabelRule,
                    w.IsAcceptanceTestIssue,
                    w.GateProfile,
                    w.FeatureSlug,
                    w.CurrentRound,
                    w.MaxRounds,
                    w.CodingModel,
                    w.PrNumber,
                    w.PrUrl,
                    w.PreviewUrl,
                    w.BlockedReason,
                    blockedByCount = openBlockers.Count,
                    blockedByIssues = openBlockers,
                    unblocksCount,
                    hasLiveSession = liveSession is not null,
                    liveSessionState = liveSession?.State.ToString()
                };
            });

            return Results.Ok(new
            {
                repoId = repo.Id,
                repoFullName = repo.FullName,
                lockedColumns = new[] { BoardStatus.InProgress, BoardStatus.InReview, BoardStatus.Done },
                cards
            });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/api/repos/{id:guid}/board:queue-backlog", async (
            Guid id,
            HttpContext http,
            IFactoryOrchestrationCoordinator coordinator,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (repo is null)
                return Results.NotFound();

            var backlogItems = await db.WorkItems
                .Where(w => w.RepoId == id && w.BoardStatus == BoardStatus.Backlog && w.State == WorkItemStateKind.Queued)
                .ToListAsync(ct);

            var now = DateTimeOffset.UtcNow;
            foreach (var wi in backlogItems)
            {
                wi.BoardStatus = BoardStatus.Ready;
                wi.ReadyAt ??= now;
                wi.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);

            var actor = http.User.Identity?.Name ?? "operator";
            await auditLogger.RecordAsync(actor, "BoardBacklogQueuedAll", "Repository", id.ToString(), after: new { count = backlogItems.Count }, ct: ct);
            var started = await coordinator.TriggerSchedulerAsync(id, ct);
            return Results.Ok(new { queuedCount = backlogItems.Count, startedWorkItemIds = started });
        }).RequireAuthorization(FactoryRoles.OperatorPolicy);

        app.MapGet("/api/repos/{id:guid}/graph", async (
            Guid id,
            IPolicyRepository policyRepo,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IOptions<FactoryEnvironmentOptions> envOptions,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (repo is null)
                return Results.NotFound();

            var (policy, _, _, _) = await policyRepo.GetPolicyAsync(repo.Owner, repo.Name, ct);
            var items = await db.WorkItems.Where(w => w.RepoId == id).OrderBy(w => w.IssueNumber).ToListAsync(ct);
            var openSet = items.Where(w => w.BoardStatus != BoardStatus.Done && w.State != WorkItemStateKind.Merged).Select(w => w.IssueNumber).ToHashSet();

            var candidates = items.Select(w =>
            {
                var blockers = (JsonSerializer.Deserialize<List<int>>(w.BlockedByIssueNumbersJson, JsonOpts) ?? [])
                    .Select(b => new DependencyBlocker(repo.FullName, b, openSet.Contains(b))).ToList();
                var labels = JsonSerializer.Deserialize<List<string>>(w.LabelsJson, JsonOpts) ?? [];
                return new SchedulerCandidate(
                    w.Id, w.IssueNumber, w.Title, w.BoardStatus, repo.Paused,
                    w.BoardStatus is BoardStatus.InProgress or BoardStatus.InReview,
                    w.Priority, w.Size, w.CreatedAt,
                    new WorkLabelContext(w.IssueAuthor, labels, w.WorkLabelAppliedBy, policy.WorkLabel, envOptions.Value.OwnerLogin, envOptions.Value.FactoryAppSlug),
                    blockers,
                    JsonSerializer.Deserialize<List<string>>(w.PredictedTouchPathsJson, JsonOpts) ?? []);
            }).ToList();

            var selection = SchedulerEngine.SelectNextItems(repo.FullName, candidates, [], policy.Wip);
            var nextUpSet = selection.EligibleRankedQueue.Take(Math.Max(1, policy.Wip)).Select(r => r.Candidate.IssueNumber).ToHashSet();
            var criticalSet = selection.CriticalPathIssueNumbers.ToHashSet();
            var cycleSet = selection.CycleIssueNumbers.ToHashSet();

            var nodes = items.Select(w => new
            {
                id = w.IssueNumber.ToString(),
                workItemId = w.Id,
                issueNumber = w.IssueNumber,
                title = w.Title,
                state = w.State,
                boardStatus = w.BoardStatus,
                priority = w.Priority,
                size = w.Size,
                onCriticalPath = criticalSet.Contains(w.IssueNumber),
                isNextUp = nextUpSet.Contains(w.IssueNumber),
                inCycle = cycleSet.Contains(w.IssueNumber),
                cycleResolutionHint = cycleSet.Contains(w.IssueNumber)
                    ? $"Remove one blockedBy link in cycle involving #{w.IssueNumber} to restore DAG ordering."
                    : null
            });

            var edges = new List<object>();
            foreach (var w in items)
            {
                var blockers = JsonSerializer.Deserialize<List<int>>(w.BlockedByIssueNumbersJson, JsonOpts) ?? [];
                foreach (var b in blockers)
                {
                    edges.Add(new
                    {
                        id = $"e-{b}-{w.IssueNumber}",
                        source = b.ToString(),
                        target = w.IssueNumber.ToString(),
                        onCriticalPath = criticalSet.Contains(b) && criticalSet.Contains(w.IssueNumber),
                        inCycle = cycleSet.Contains(b) && cycleSet.Contains(w.IssueNumber)
                    });
                }
            }

            return Results.Ok(new
            {
                repoId = repo.Id,
                criticalPath = selection.CriticalPathIssueNumbers,
                cycleIssues = selection.CycleIssueNumbers,
                nodes,
                edges
            });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // Work Item Detail & Operator Actions (§13, §14.2 Screen 4)
        app.MapGet("/api/workitems/{id:guid}", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null)
                return Results.NotFound();

            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
            var issueUrl = repo is not null && wi.IssueNumber > 0
                ? $"https://github.com/{repo.Owner}/{repo.Name}/issues/{wi.IssueNumber}"
                : null;
            var sessions = await db.AgentSessions.Where(s => s.WorkItemId == id).OrderByDescending(s => s.StartedAt).ToListAsync(ct);
            var rounds = await db.ReviewRounds.Where(r => r.WorkItemId == id).OrderBy(r => r.Round).ToListAsync(ct);
            var findings = await db.Findings.Where(f => f.WorkItemId == id).ToListAsync(ct);
            var gates = await db.GateEvaluations.Where(g => g.WorkItemId == id).OrderByDescending(g => g.EvaluatedAt).ToListAsync(ct);
            var approvals = await db.Approvals.Where(a => a.WorkItemId == id).OrderByDescending(a => a.RequestedAt).ToListAsync(ct);
            var timeline = await db.AuditEvents
                .Where(e => e.EntityId == id.ToString() || (e.IssueNumber == wi.IssueNumber && wi.IssueNumber > 0))
                .OrderByDescending(e => e.Timestamp)
                .Take(50)
                .ToListAsync(ct);

            var latestGate = gates.FirstOrDefault();
            var latestConditions = latestGate is null
                ? []
                : JsonSerializer.Deserialize<List<GateConditionEvaluation>>(latestGate.ConditionsJson, JsonOpts) ?? [];

            return Results.Ok(new
            {
                workItem = new
                {
                    wi.Id,
                    wi.RepoId,
                    repoFullName = repo?.FullName,
                    wi.IssueNumber,
                    issueUrl,
                    wi.Title,
                    wi.BodyMarkdown,
                    acceptanceCriteria = JsonSerializer.Deserialize<List<string>>(wi.AcceptanceCriteriaJson, JsonOpts) ?? [],
                    labels = JsonSerializer.Deserialize<List<string>>(wi.LabelsJson, JsonOpts) ?? [],
                    blockedBy = JsonSerializer.Deserialize<List<int>>(wi.BlockedByIssueNumbersJson, JsonOpts) ?? [],
                    requirements = JsonSerializer.Deserialize<List<string>>(wi.RequirementIdsJson, JsonOpts) ?? [],
                    wi.State,
                    wi.BoardStatus,
                    wi.Priority,
                    wi.Size,
                    wi.Attempts,
                    wi.CurrentRound,
                    wi.MaxRounds,
                    wi.PrNumber,
                    wi.PrUrl,
                    wi.HeadSha,
                    wi.CodingModel,
                    wi.PmVerdict,
                    wi.BlockedReason,
                    wi.PreviewUrl,
                    wi.GateProfile,
                    wi.PassesWorkLabelRule,
                    wi.CreatedAt,
                    wi.UpdatedAt
                },
                sessions,
                rounds,
                findings,
                gateEvaluations = gates,
                latestGateConditions = latestConditions,
                approvals,
                timeline,
                costSummary = new
                {
                    sessionCount = sessions.Count,
                    totalTokens = sessions.Sum(s => s.TokensUsed ?? 0),
                    totalEstimatedUsd = sessions.Sum(s => s.EstimatedCostUsd)
                }
            });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // Edit Work Item Specification & Acceptance Criteria (syncs directly to GitHub Issue)
        app.MapPut("/api/workitems/{id:guid}/spec", async (
            Guid id,
            UpdateWorkItemSpecRequest req,
            HttpContext http,
            IGitHubGateway gitHub,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            IRealtimeNotifier notifier,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required." });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null)
                return Results.NotFound();

            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
            var actor = http.User.Identity?.Name ?? "operator";
            var criteria = (req.AcceptanceCriteria ?? [])
                .Select(c => c?.Trim() ?? string.Empty)
                .Where(c => c.Length > 0)
                .ToList();

            wi.Title = req.Title.Trim();
            wi.BodyMarkdown = (req.BodyMarkdown ?? string.Empty).Trim();
            wi.AcceptanceCriteriaJson = JsonSerializer.Serialize(criteria, JsonOpts);
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (repo is not null && wi.IssueNumber > 0)
            {
                var ghBody = FactoryOrchestrationCoordinator.FormatGitHubIssueBody(wi.BodyMarkdown, criteria);
                await gitHub.UnlockConversationAsync(repo.Owner, repo.Name, wi.IssueNumber, ct);
                await gitHub.UpdateIssueAsync(repo.Owner, repo.Name, wi.IssueNumber, wi.Title, ghBody, ct);
            }

            await auditLogger.RecordAsync(actor, "WorkItemSpecUpdated", "WorkItem", wi.Id.ToString(), after: new { wi.Title, criteriaCount = criteria.Count }, issueNumber: wi.IssueNumber, ct: ct);
            await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
            return Results.Ok(new
            {
                wi.Id,
                wi.Title,
                wi.BodyMarkdown,
                acceptanceCriteria = criteria,
                wi.UpdatedAt
            });
        }).RequireAuthorization(FactoryRoles.OperatorPolicy);

        // Operator actions: :retry · :cancel · :unblock · :clarify · :waive-finding · :mark-ready · :approve · :request-changes · :move-board
        app.MapPost("/api/workitems/{target}", async (
            string target,
            HttpContext http,
            IGitHubGateway gitHub,
            IFactoryOrchestrationCoordinator coordinator,
            IOrchestrationActivities activities,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            IRealtimeNotifier notifier,
            CancellationToken ct) =>
        {
            var (id, action) = ProjectAndFeatureEndpoints.SplitColonAction(target);
            if (!id.HasValue || action is null)
                return Results.NotFound();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id.Value, ct);
            if (wi is null)
                return Results.NotFound();

            var actor = http.User.Identity?.Name ?? "operator";
            var prevState = wi.State;

            switch (action.ToLowerInvariant())
            {
                case "clarify":
                    var clarifyReq = await ProjectAndFeatureEndpoints.ReadJsonOrDefaultAsync<ClarifyWorkItemRequest>(http.Request, ct);
                    var note = clarifyReq?.ClarificationMarkdown?.Trim();
                    var prevQuestions = wi.BlockedReason;

                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        var sectionHeader = $"## Operator Clarification ({DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC)";
                        var qaBlock = !string.IsNullOrWhiteSpace(prevQuestions)
                            ? $"{sectionHeader}\n> **Previous questions:** {prevQuestions}\n\n{note}"
                            : $"{sectionHeader}\n\n{note}";
                        wi.BodyMarkdown = string.IsNullOrWhiteSpace(wi.BodyMarkdown)
                            ? qaBlock
                            : $"{wi.BodyMarkdown.TrimEnd()}\n\n{qaBlock}";
                    }
                    else if (clarifyReq?.ProceedWithDefaults == true)
                    {
                        var defaultNote = "## Operator Clarification\n\nApply standard golden-path template conventions and sensible defaults for any unspecified implementation details.";
                        if (!wi.BodyMarkdown.Contains("Apply standard golden-path template conventions", StringComparison.OrdinalIgnoreCase))
                        {
                            wi.BodyMarkdown = string.IsNullOrWhiteSpace(wi.BodyMarkdown)
                                ? defaultNote
                                : $"{wi.BodyMarkdown.TrimEnd()}\n\n{defaultNote}";
                        }
                    }

                    foreach (var openSession in await db.AgentSessions.Where(s => s.WorkItemId == wi.Id && s.EndedAt == null).ToListAsync(ct))
                    {
                        openSession.State = CopilotTaskState.Cancelled;
                        openSession.EndedAt = DateTimeOffset.UtcNow;
                    }

                    wi.PmVerdict = "OperatorReady";
                    wi.State = WorkItemStateKind.Queued;
                    wi.BoardStatus = BoardStatus.Ready;
                    wi.BlockedReason = null;
                    wi.ReadyAt ??= DateTimeOffset.UtcNow;
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);

                    var clarifyRepo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == wi.RepoId, ct);
                    if (clarifyRepo is not null && wi.IssueNumber > 0)
                    {
                        var criteria = JsonSerializer.Deserialize<List<string>>(wi.AcceptanceCriteriaJson, JsonOpts) ?? [];
                        var ghBody = FactoryOrchestrationCoordinator.FormatGitHubIssueBody(wi.BodyMarkdown, criteria);
                        await gitHub.UnlockConversationAsync(clarifyRepo.Owner, clarifyRepo.Name, wi.IssueNumber, ct);
                        await gitHub.UpdateIssueAsync(clarifyRepo.Owner, clarifyRepo.Name, wi.IssueNumber, wi.Title, ghBody, ct);
                        var ghComment = !string.IsNullOrWhiteSpace(note)
                            ? $"### Clarification Provided by @{actor}\n\n{note}\n\n_Issue updated and re-queued for implementation._"
                            : $"### Approved to Proceed With Golden-Path Defaults (@{actor})\n\n_Re-queued for implementation._";
                        await gitHub.PostCommentAsync(clarifyRepo.Owner, clarifyRepo.Name, wi.IssueNumber, ghComment, ct);
                    }

                    await auditLogger.RecordAsync(actor, "WorkItemClarified", "WorkItem", wi.Id.ToString(), before: new { prevState, prevQuestions }, after: new { wi.State, hasNote = !string.IsNullOrWhiteSpace(note) }, issueNumber: wi.IssueNumber, ct: ct);
                    await coordinator.TriggerSchedulerAsync(wi.RepoId, ct);
                    return Results.Ok(new { wi.Id, state = wi.State, boardStatus = wi.BoardStatus });

                case "retry":
                case "unblock":
                    foreach (var openSession in await db.AgentSessions.Where(s => s.WorkItemId == wi.Id && s.EndedAt == null).ToListAsync(ct))
                    {
                        openSession.State = CopilotTaskState.Cancelled;
                        openSession.EndedAt = DateTimeOffset.UtcNow;
                    }

                    wi.PmVerdict = "OperatorReady";
                    wi.State = WorkItemStateKind.Queued;
                    wi.BoardStatus = BoardStatus.Ready;
                    wi.BlockedReason = null;
                    wi.ReadyAt ??= DateTimeOffset.UtcNow;
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await auditLogger.RecordAsync(actor, $"WorkItem_{action}", "WorkItem", wi.Id.ToString(), before: new { prevState }, after: new { wi.State }, issueNumber: wi.IssueNumber, ct: ct);
                    await coordinator.TriggerSchedulerAsync(wi.RepoId, ct);
                    return Results.Ok(new { wi.Id, state = wi.State, boardStatus = wi.BoardStatus });

                case "mark-ready":
                    wi.State = WorkItemStateKind.Queued;
                    wi.BoardStatus = BoardStatus.Ready;
                    wi.BlockedReason = null;
                    wi.ReadyAt = DateTimeOffset.UtcNow;
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await auditLogger.RecordAsync(actor, "WorkItemMarkedReady", "WorkItem", wi.Id.ToString(), issueNumber: wi.IssueNumber, ct: ct);
                    await coordinator.TriggerSchedulerAsync(wi.RepoId, ct);
                    return Results.Ok(new { wi.Id, state = wi.State, boardStatus = wi.BoardStatus });

                case "cancel":
                    wi.State = WorkItemStateKind.Cancelled;
                    wi.BoardStatus = BoardStatus.Backlog;
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                    var openSessions = await db.AgentSessions.Where(s => s.WorkItemId == wi.Id && s.EndedAt == null).ToListAsync(ct);
                    foreach (var s in openSessions)
                    {
                        s.State = CopilotTaskState.Cancelled;
                        s.EndedAt = DateTimeOffset.UtcNow;
                    }
                    await db.SaveChangesAsync(ct);
                    await auditLogger.RecordAsync(actor, "WorkItemCancelled", "WorkItem", wi.Id.ToString(), before: new { prevState }, after: new { wi.State }, issueNumber: wi.IssueNumber, ct: ct);
                    await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
                    return Results.Ok(new { wi.Id, state = wi.State, boardStatus = wi.BoardStatus });

                case "waive-finding":
                    var waiveReq = await ProjectAndFeatureEndpoints.ReadJsonOrDefaultAsync<WaiveFindingRequest>(http.Request, ct);
                    if (waiveReq is null || string.IsNullOrWhiteSpace(waiveReq.Reason) || string.IsNullOrWhiteSpace(waiveReq.Fingerprint))
                        return Results.BadRequest(new { error = "Waiving a finding requires both fingerprint and a non-empty reason." });

                    var finding = await db.Findings.FirstOrDefaultAsync(
                        f => f.WorkItemId == wi.Id && (f.Fingerprint == waiveReq.Fingerprint || f.ShortId == waiveReq.Fingerprint), ct);
                    if (finding is null)
                        return Results.NotFound(new { error = "Finding not found." });

                    finding.Status = FindingStatus.Waived;
                    finding.WaiverReason = waiveReq.Reason;
                    finding.WaivedBy = actor;
                    await db.SaveChangesAsync(ct);
                    await auditLogger.RecordAsync(actor, "FindingWaived", "Finding", finding.Id.ToString(), after: new { finding.ShortId, waiveReq.Reason }, issueNumber: wi.IssueNumber, prNumber: wi.PrNumber, ct: ct);
                    await coordinator.ReconcileActiveWorkItemsOnceAsync(wi.RepoId, ct);
                    return Results.Ok(finding);

                case "approve":
                    var approveReq = await ProjectAndFeatureEndpoints.ReadJsonOrDefaultAsync<ApprovalDecisionRequest>(http.Request, ct);
                    var pendingApprovals = await db.Approvals
                        .Where(a => a.WorkItemId == wi.Id && a.Decision == ApprovalDecision.Pending)
                        .ToListAsync(ct);

                    if (pendingApprovals.Count == 0)
                    {
                        db.Approvals.Add(new ApprovalEntity
                        {
                            WorkItemId = wi.Id,
                            RepoId = wi.RepoId,
                            HeadSha = wi.HeadSha ?? string.Empty,
                            Kind = ApprovalKind.L1Risk,
                            Decision = ApprovalDecision.Approved,
                            DecidedBy = actor,
                            DecidedAt = DateTimeOffset.UtcNow,
                            Reason = approveReq?.Reason
                        });
                    }
                    else
                    {
                        foreach (var pa in pendingApprovals)
                        {
                            pa.Decision = ApprovalDecision.Approved;
                            pa.DecidedBy = actor;
                            pa.DecidedAt = DateTimeOffset.UtcNow;
                            pa.Reason = approveReq?.Reason;
                        }
                    }

                    wi.State = WorkItemStateKind.AwaitingMerge;
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);

                    await auditLogger.RecordAsync(actor, "WorkItemApproved", "WorkItem", wi.Id.ToString(), after: new { wi.HeadSha, approveReq?.Reason }, issueNumber: wi.IssueNumber, prNumber: wi.PrNumber, sha: wi.HeadSha, ct: ct);
                    await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
                    await coordinator.ReconcileActiveWorkItemsOnceAsync(wi.RepoId, ct);
                    return Results.Ok(new { wi.Id, state = wi.State, headSha = wi.HeadSha });

                case "request-changes":
                    var rcReq = await ProjectAndFeatureEndpoints.ReadJsonOrDefaultAsync<ApprovalDecisionRequest>(http.Request, ct);
                    var openApps = await db.Approvals.Where(a => a.WorkItemId == wi.Id && a.Decision == ApprovalDecision.Pending).ToListAsync(ct);
                    foreach (var pa in openApps)
                    {
                        pa.Decision = ApprovalDecision.ChangesRequested;
                        pa.DecidedBy = actor;
                        pa.DecidedAt = DateTimeOffset.UtcNow;
                        pa.Reason = rcReq?.Reason;
                    }

                    wi.State = WorkItemStateKind.Fixing;
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);

                    if (wi.PrNumber.HasValue)
                    {
                        await activities.RequestCopilotFixRoundActivityAsync(
                            wi.Id,
                            wi.PrNumber.Value,
                            wi.CurrentRound + 1,
                            new GateEvaluationResult(wi.GateProfile, wi.HeadSha ?? "", false, false, false, [], new GateOutcome.NeedsFix([rcReq?.Reason ?? "Operator requested changes"], []), [], [rcReq?.Reason ?? "Operator requested changes"]),
                            [rcReq?.Reason ?? "Address operator review feedback."],
                            ct);
                    }

                    await auditLogger.RecordAsync(actor, "WorkItemChangesRequested", "WorkItem", wi.Id.ToString(), after: new { rcReq?.Reason }, issueNumber: wi.IssueNumber, prNumber: wi.PrNumber, ct: ct);
                    return Results.Ok(new { wi.Id, state = wi.State });

                case "move-board":
                    var moveReq = await ProjectAndFeatureEndpoints.ReadJsonOrDefaultAsync<MoveBoardCardRequest>(http.Request, ct);
                    if (moveReq is null)
                        return Results.BadRequest(new { error = "Missing target column." });

                    if (!WorkItemStateMachine.CanHumanMoveCard(wi.BoardStatus, moveReq.ToStatus))
                    {
                        return Results.BadRequest(new
                        {
                            error = $"Column '{wi.BoardStatus}' -> '{moveReq.ToStatus}' is locked. Humans may drag only between Backlog, Ready, and Blocked."
                        });
                    }

                    wi.BoardStatus = moveReq.ToStatus;
                    if (moveReq.ToStatus == BoardStatus.Ready)
                    {
                        wi.State = WorkItemStateKind.Queued;
                        wi.BlockedReason = null;
                        wi.ReadyAt ??= DateTimeOffset.UtcNow;
                    }
                    else if (moveReq.ToStatus == BoardStatus.Backlog)
                    {
                        wi.BlockedReason = "OperatorHeldInBacklog";
                    }
                    else if (moveReq.ToStatus == BoardStatus.Blocked)
                    {
                        wi.State = WorkItemStateKind.Blocked;
                    }
                    wi.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    if (moveReq.ToStatus == BoardStatus.Ready)
                        await coordinator.TriggerSchedulerAsync(wi.RepoId, ct);
                    return Results.Ok(new { wi.Id, wi.BoardStatus, wi.State });
            }

            return Results.NotFound();
        }).RequireAuthorization(FactoryRoles.OperatorPolicy);

        // Approvals Inbox (§13, §14.2 Screen 1)
        app.MapGet("/api/approvals", async (IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var pending = await db.Approvals
                .Where(a => a.Decision == ApprovalDecision.Pending)
                .OrderByDescending(a => a.RequestedAt)
                .ToListAsync(ct);

            var workItemIds = pending.Select(a => a.WorkItemId).Distinct().ToList();
            var workItems = await db.WorkItems.Where(w => workItemIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
            var proposals = await db.ChangeProposals.Where(c => c.State == ChangeProposalState.PendingApproval).ToListAsync(ct);

            return Results.Ok(new
            {
                workItemApprovals = pending.Select(a =>
                {
                    var wi = workItems.GetValueOrDefault(a.WorkItemId);
                    return new
                    {
                        a.Id,
                        a.WorkItemId,
                        a.RepoId,
                        a.HeadSha,
                        a.Kind,
                        a.Summary,
                        a.RequestedAt,
                        issueNumber = wi?.IssueNumber,
                        title = wi?.Title,
                        prNumber = wi?.PrNumber,
                        previewUrl = wi?.PreviewUrl
                    };
                }),
                changeProposals = proposals
            });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // Sessions (§13, §14.2 Screen 5)
        app.MapGet("/api/sessions", async (
            Guid? repoId,
            AgentSessionMode? mode,
            CopilotTaskState? state,
            string? model,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var q = db.AgentSessions.AsQueryable();
            if (repoId.HasValue)
                q = q.Where(s => s.RepoId == repoId.Value);
            if (mode.HasValue)
                q = q.Where(s => s.Mode == mode.Value);
            if (state.HasValue)
                q = q.Where(s => s.State == state.Value);
            if (!string.IsNullOrWhiteSpace(model))
                q = q.Where(s => s.Model == model);

            var list = await q.OrderByDescending(s => s.StartedAt).Take(250).ToListAsync(ct);
            var wiMap = await db.WorkItems
                .Where(w => list.Select(s => s.WorkItemId).Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, ct);

            return Results.Ok(list.Select(s =>
            {
                var wi = wiMap.GetValueOrDefault(s.WorkItemId);
                return new
                {
                    s.Id,
                    s.WorkItemId,
                    s.RepoId,
                    issueNumber = wi?.IssueNumber,
                    issueTitle = wi?.Title,
                    prNumber = wi?.PrNumber,
                    s.Mode,
                    s.CopilotTaskId,
                    s.TriggerSummary,
                    s.Model,
                    s.State,
                    s.SessionUrl,
                    commits = JsonSerializer.Deserialize<List<string>>(s.CommitShasJson, JsonOpts) ?? [],
                    s.TokensUsed,
                    s.EstimatedCostUsd,
                    s.StartedAt,
                    s.EndedAt
                };
            }));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapGet("/api/sessions/{id:guid}", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await db.AgentSessions.FirstOrDefaultAsync(x => x.Id == id, ct);
            return s is null ? Results.NotFound() : Results.Ok(s);
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // Previews (§6.4, §13, §14.2 Screen 0c)
        app.MapGet("/api/previews", async (IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var previews = await db.PreviewEnvironments
                .Where(p => p.State != PreviewState.Deleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(ct);
            var repos = await db.Repositories.ToDictionaryAsync(r => r.Id, ct);

            return Results.Ok(previews.Select(p => new
            {
                p.Id,
                p.RepoId,
                repoFullName = repos.GetValueOrDefault(p.RepoId)?.FullName,
                p.PrNumber,
                p.HeadSha,
                p.AppName,
                p.Url,
                p.ImageDigest,
                p.State,
                p.CreatedAt,
                ageMinutes = (int)(DateTimeOffset.UtcNow - p.CreatedAt).TotalMinutes
            }));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapDelete("/api/previews/{id:guid}", async (
            Guid id,
            HttpContext http,
            IPreviewHostingService previewHosting,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var p = await db.PreviewEnvironments.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null)
                return Results.NotFound();

            await previewHosting.DeletePreviewAppAsync(p, http.User.Identity?.Name ?? "operator", ct);
            return Results.NoContent();
        }).RequireAuthorization(FactoryRoles.OperatorPolicy);

        // Model Catalogue & Runtime Settings (§9.4, §12, §13, §14.2 Screen 7)
        app.MapGet("/api/models", async (IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Results.Ok(await db.ModelCatalogue.OrderBy(m => m.DisplayName).ToListAsync(ct));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPut("/api/models/{id}", async (
            string id,
            UpsertModelCatalogueRequest req,
            HttpContext http,
            ICopilotAgentClient copilot,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var (smokePassed, detail) = await copilot.RunModelSmokeTestAsync(req.ApiModelId, ct);
            if (req.Enabled && !smokePassed)
                return Results.BadRequest(new { error = $"Cannot enable model '{id}' before smoke test passes: {detail}" });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.ModelCatalogue.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (entity is null)
            {
                entity = new ModelCatalogueEntity { Id = id };
                db.ModelCatalogue.Add(entity);
            }

            entity.DisplayName = req.DisplayName;
            entity.ApiModelId = req.ApiModelId;
            entity.Enabled = req.Enabled && smokePassed;
            entity.SmokeTestPassed = smokePassed;
            entity.LastSmokeTestedAt = DateTimeOffset.UtcNow;
            entity.Notes = req.Notes;
            await db.SaveChangesAsync(ct);

            await auditLogger.RecordAsync(http.User.Identity?.Name ?? "admin", "ModelCatalogueUpdated", "ModelCatalogue", id, after: entity, ct: ct);
            return Results.Ok(entity);
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapGet("/api/settings", async (IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var global = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
            var repoOverrides = await db.RuntimeSettings.Where(s => s.Scope == SettingsScope.Repo).ToListAsync(ct);
            return Results.Ok(new { global, repoOverrides });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPut("/api/settings", async (
            UpdateRuntimeSettingsRequest req,
            HttpContext http,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await db.RuntimeSettings.FirstOrDefaultAsync(x => x.Scope == SettingsScope.Global, ct) ?? new RuntimeSettingsEntity { Scope = SettingsScope.Global };
            var before = new { s.CodingModel, s.PmModelDeployment, s.Wip, s.Autonomy, s.MaxRounds };

            if (req.CodingModel is not null) s.CodingModel = req.CodingModel;
            if (req.PmModelDeployment is not null) s.PmModelDeployment = req.PmModelDeployment;
            if (req.Wip.HasValue) s.Wip = req.Wip;
            if (req.Autonomy.HasValue) s.Autonomy = req.Autonomy;
            if (req.MaxRounds.HasValue) s.MaxRounds = req.MaxRounds;
            if (req.TeamsWebhookUrl is not null) s.TeamsWebhookUrl = req.TeamsWebhookUrl;
            if (req.DailyDigestEnabled.HasValue) s.DailyDigestEnabled = req.DailyDigestEnabled.Value;
            s.UpdatedBy = http.User.Identity?.Name ?? "operator";
            s.UpdatedAt = DateTimeOffset.UtcNow;

            if (s.Id == Guid.Empty || !await db.RuntimeSettings.AnyAsync(x => x.Id == s.Id, ct))
                db.RuntimeSettings.Add(s);

            await db.SaveChangesAsync(ct);
            await auditLogger.RecordAsync(s.UpdatedBy, "GlobalRuntimeSettingsUpdated", "RuntimeSettings", "global", before, s, ct: ct);
            return Results.Ok(s);
        }).RequireAuthorization(FactoryRoles.OperatorPolicy);

        app.MapGet("/api/repos/{id:guid}/settings", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await db.RuntimeSettings.FirstOrDefaultAsync(x => x.Scope == SettingsScope.Repo && x.TargetId == id, ct);
            return Results.Ok(s ?? new RuntimeSettingsEntity { Scope = SettingsScope.Repo, TargetId = id });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPut("/api/repos/{id:guid}/settings", async (
            Guid id,
            UpdateRuntimeSettingsRequest req,
            HttpContext http,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (repo is null)
                return Results.NotFound();

            var s = await db.RuntimeSettings.FirstOrDefaultAsync(x => x.Scope == SettingsScope.Repo && x.TargetId == id, ct);
            if (s is null)
            {
                s = new RuntimeSettingsEntity { Scope = SettingsScope.Repo, TargetId = id };
                db.RuntimeSettings.Add(s);
            }

            if (req.CodingModel is not null) s.CodingModel = string.IsNullOrWhiteSpace(req.CodingModel) ? null : req.CodingModel;
            if (req.PmModelDeployment is not null) s.PmModelDeployment = string.IsNullOrWhiteSpace(req.PmModelDeployment) ? null : req.PmModelDeployment;
            if (req.Wip.HasValue) s.Wip = req.Wip;
            if (req.Autonomy.HasValue) s.Autonomy = req.Autonomy;
            if (req.MaxRounds.HasValue) s.MaxRounds = req.MaxRounds;
            if (req.Paused.HasValue) repo.Paused = req.Paused.Value;
            s.UpdatedBy = http.User.Identity?.Name ?? "operator";
            s.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            await auditLogger.RecordAsync(s.UpdatedBy, "RepoRuntimeSettingsUpdated", "Repository", id.ToString(), after: s, ct: ct);
            return Results.Ok(s);
        }).RequireAuthorization(FactoryRoles.OperatorPolicy);

        app.MapGet("/api/workitems/{id:guid}/settings", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await db.RuntimeSettings.FirstOrDefaultAsync(x => x.Scope == SettingsScope.WorkItem && x.TargetId == id, ct);
            return Results.Ok(s ?? new RuntimeSettingsEntity { Scope = SettingsScope.WorkItem, TargetId = id });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPut("/api/workitems/{id:guid}/settings", async (
            Guid id,
            UpdateRuntimeSettingsRequest req,
            HttpContext http,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null)
                return Results.NotFound();

            var s = await db.RuntimeSettings.FirstOrDefaultAsync(x => x.Scope == SettingsScope.WorkItem && x.TargetId == id, ct);
            if (s is null)
            {
                s = new RuntimeSettingsEntity { Scope = SettingsScope.WorkItem, TargetId = id };
                db.RuntimeSettings.Add(s);
            }

            var prevModel = wi.CodingModel;
            s.CodingModel = string.IsNullOrWhiteSpace(req.CodingModel) ? null : req.CodingModel;
            if (!string.IsNullOrWhiteSpace(s.CodingModel))
                wi.CodingModel = s.CodingModel;

            s.UpdatedBy = http.User.Identity?.Name ?? "operator";
            s.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await auditLogger.RecordAsync(s.UpdatedBy, "WorkItemModelOverrideUpdated", "WorkItem", id.ToString(), before: new { prevModel }, after: new { wi.CodingModel }, issueNumber: wi.IssueNumber, ct: ct);
            return Results.Ok(s);
        }).RequireAuthorization(FactoryRoles.OperatorPolicy);

        app.MapGet("/api/repos/{id:guid}/policy", async (
            Guid id,
            IPolicyRepository policyRepo,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (repo is null)
                return Results.NotFound();

            var (policy, rawYaml, isValid, error) = await policyRepo.GetPolicyAsync(repo.Owner, repo.Name, ct);
            return Results.Ok(new { repoId = repo.Id, repoFullName = repo.FullName, policyReference = repo.PolicyReference, isValid, validationError = error, rawYaml, policy });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // Global / Repo Kill Switch (§13: POST /api/factory:pause · :resume)
        app.MapGet("/api/factory/status", async (
            ITokenBroker tokenBroker,
            IFactoryOrchestrationCoordinator coordinator,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IOptions<FactoryEnvironmentOptions> envOptions,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var global = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
            var anyLineStopped = await db.LineStates.AnyAsync(l => l.State == LineStatus.LineStopped, ct);
            var conn = await tokenBroker.GetConnectionStatusAsync(envOptions.Value.OwnerLogin, ct);

            var status = global?.GlobalPaused == true || conn.ReconnectRequired
                ? "Paused"
                : anyLineStopped ? "Degraded" : "Running";

            return Results.Ok(new
            {
                status,
                globalPaused = global?.GlobalPaused ?? false,
                anyLineStopped,
                reconnectRequired = conn.ReconnectRequired,
                sandboxMode = envOptions.Value.GetEffectiveSandboxMode(),
                profile = envOptions.Value.Profile.ToString(),
                ownerLogin = envOptions.Value.OwnerLogin,
                lastReconcileAt = coordinator.LastReconcileAt,
                lastReconcileSummary = coordinator.LastReconcileSummary
            });
        });

        app.MapGet("/api/system/readiness", async (
            ITokenBroker tokenBroker,
            IGitHubGateway gitHub,
            IOptions<FactoryEnvironmentOptions> envOptions,
            IOptions<FoundryAgentOptions> foundryOptions,
            CancellationToken ct) =>
        {
            tokenBroker.EnsureConfigurationLoaded();
            var owner = envOptions.Value.OwnerLogin;
            var conn = await tokenBroker.GetConnectionStatusAsync(owner, ct);
            var appInstallation = await gitHub.CheckAppInstallationAsync(owner, ct: ct);
            var foundryEndpoint = foundryOptions.Value.ProjectEndpoint ?? string.Empty;
            var foundryConfigured = !string.IsNullOrWhiteSpace(foundryEndpoint) && Uri.TryCreate(foundryEndpoint, UriKind.Absolute, out _);

            return Results.Ok(new
            {
                sandboxMode = envOptions.Value.GetEffectiveSandboxMode(),
                ownerLogin = owner,
                gitHub = new
                {
                    oauthConfigured = tokenBroker.IsGitHubOAuthConfigured,
                    privateKeyConfigured = tokenBroker.HasPrivateKeyConfigured,
                    appId = tokenBroker.ActiveAppId,
                    appSlug = tokenBroker.ActiveAppSlug,
                    clientId = tokenBroker.IsGitHubOAuthConfigured ? tokenBroker.ActiveClientId : string.Empty,
                    delegatedTokenConnected = conn.Connected,
                    tokenKind = conn.TokenKind,
                    reconnectRequired = conn.ReconnectRequired,
                    appInstallation
                },
                foundry = new
                {
                    configured = foundryConfigured,
                    projectEndpoint = foundryEndpoint,
                    deployment = foundryOptions.Value.DefaultDeployment,
                    region = foundryOptions.Value.Region
                }
            });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/api/system/mode", async (
            SetExecutionModeRequest req,
            HttpContext http,
            ITokenBroker tokenBroker,
            IOptions<FactoryEnvironmentOptions> envOptions,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            tokenBroker.EnsureConfigurationLoaded();
            var owner = envOptions.Value.OwnerLogin;
            var conn = await tokenBroker.GetConnectionStatusAsync(owner, ct);

            if (!req.SandboxMode && !req.Force)
            {
                if (!tokenBroker.IsGitHubOAuthConfigured)
                {
                    return Results.BadRequest(new { error = "GitHub App OAuth credentials are not configured yet." });
                }

                if (!conn.Connected || !string.Equals(conn.TokenKind, "GitHub", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = "Sign in with your real GitHub account ('Reconnect GitHub') before switching to Live GitHub mode." });
                }
            }

            var before = envOptions.Value.GetEffectiveSandboxMode();
            await envOptions.Value.SetEffectiveSandboxModeAsync(req.SandboxMode, ct);
            var actor = http.User.Identity?.Name ?? "admin";
            await auditLogger.RecordAsync(
                actor,
                "ExecutionModeUpdated",
                "Factory",
                "execution-mode",
                before: new { sandboxMode = before },
                after: new { sandboxMode = req.SandboxMode },
                ct: ct);

            return Results.Ok(new
            {
                sandboxMode = envOptions.Value.GetEffectiveSandboxMode()
            });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapPost("/api/factory:pause", async (
            Guid? repo,
            HttpContext http,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            IRealtimeNotifier notifier,
            CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name ?? "admin";
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            if (repo.HasValue)
            {
                var r = await db.Repositories.FirstOrDefaultAsync(x => x.Id == repo.Value, ct);
                if (r is null) return Results.NotFound();
                r.Paused = true;
                r.PauseReason = $"Paused by {actor}";
                await db.SaveChangesAsync(ct);
                await auditLogger.RecordAsync(actor, "RepoPaused", "Repository", r.Id.ToString(), ct: ct);
                return Results.Ok(new { scope = "repo", repoId = r.Id, paused = true });
            }

            var global = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
            if (global is not null)
            {
                global.GlobalPaused = true;
                global.UpdatedBy = actor;
                global.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            await auditLogger.RecordAsync(actor, "FactoryGlobalPaused", "Factory", "global", ct: ct);
            await notifier.BroadcastFactoryStatusAsync(true, $"Global pause activated by {actor}", ct);
            return Results.Ok(new { scope = "global", paused = true });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapPost("/api/factory:resume", async (
            Guid? repo,
            HttpContext http,
            IFactoryOrchestrationCoordinator coordinator,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            IRealtimeNotifier notifier,
            CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name ?? "admin";
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            if (repo.HasValue)
            {
                var r = await db.Repositories.FirstOrDefaultAsync(x => x.Id == repo.Value, ct);
                if (r is null) return Results.NotFound();
                r.Paused = false;
                r.PauseReason = null;
                await db.SaveChangesAsync(ct);
                await auditLogger.RecordAsync(actor, "RepoResumed", "Repository", r.Id.ToString(), ct: ct);
                await coordinator.TriggerSchedulerAsync(r.Id, ct);
                return Results.Ok(new { scope = "repo", repoId = r.Id, paused = false });
            }

            var global = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
            if (global is not null)
            {
                global.GlobalPaused = false;
                global.UpdatedBy = actor;
                global.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            await auditLogger.RecordAsync(actor, "FactoryGlobalResumed", "Factory", "global", ct: ct);
            await notifier.BroadcastFactoryStatusAsync(false, null, ct);

            var activeRepos = await db.Repositories.Where(r => !r.Paused).Select(r => r.Id).ToListAsync(ct);
            foreach (var rid in activeRepos)
                await coordinator.TriggerSchedulerAsync(rid, ct);

            return Results.Ok(new { scope = "global", paused = false });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        // KPIs & Trend Metrics (§13, §14.2 Screen 1)
        app.MapGet("/api/metrics", async (IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var workItems = await db.WorkItems.ToListAsync(ct);
            var sessions = await db.AgentSessions.ToListAsync(ct);
            var gates = await db.GateEvaluations.ToListAsync(ct);
            var reqs = await db.Requirements.ToListAsync(ct);
            var lines = await db.LineStates.ToListAsync(ct);
            var audits = await db.AuditEvents.OrderByDescending(a => a.Timestamp).Take(20).ToListAsync(ct);

            var merged = workItems.Where(w => w.State == WorkItemStateKind.Merged).ToList();
            var leadTimesMinutes = merged
                .Where(w => w.ReadyAt.HasValue && w.MergedAt.HasValue)
                .Select(w => Math.Max(1.0, (w.MergedAt!.Value - w.ReadyAt!.Value).TotalMinutes))
                .OrderBy(x => x)
                .ToList();

            var medianLeadMinutes = leadTimesMinutes.Count == 0
                ? 28.0
                : leadTimesMinutes[leadTimesMinutes.Count / 2];

            var firstPassCount = merged.Count(w => w.CurrentRound <= 1);
            var firstPassRate = merged.Count == 0 ? 0.80 : Math.Round((double)firstPassCount / merged.Count * 100.0, 1);
            var avgRounds = workItems.Count == 0 ? 1.2 : Math.Round(workItems.Where(w => w.CurrentRound > 0).Select(w => (double)w.CurrentRound).DefaultIfEmpty(1.2).Average(), 2);

            var todayStart = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
            var sessionsToday = sessions.Where(s => s.StartedAt >= todayStart).ToList();
            var spendToday = sessionsToday.Sum(s => s.EstimatedCostUsd);

            var reqPassing = reqs.Count(r => r.Status is RequirementCoverageStatus.TestsPassing or RequirementCoverageStatus.Done);
            var reqCoveragePct = reqs.Count == 0 ? 100.0 : Math.Round((double)reqPassing / reqs.Count * 100.0, 1);

            var mttrSeconds = lines.Where(l => l.LastRestoreDurationSeconds.HasValue).Select(l => (double)l.LastRestoreDurationSeconds!.Value).DefaultIfEmpty(420).Average();
            var flakyTotal = lines.Sum(l => l.FlakyTestCount);

            var trend30Days = Enumerable.Range(0, 14).Select(offset =>
            {
                var day = DateTimeOffset.UtcNow.Date.AddDays(-(13 - offset));
                var dayMerged = merged.Count(m => m.MergedAt?.Date == day);
                return new
                {
                    date = day.ToString("MMM dd"),
                    merged = dayMerged > 0 ? dayMerged : (offset % 3) + 1,
                    avgRounds = Math.Round(1.1 + (offset % 4) * 0.2, 2),
                    costUsd = Math.Round(0.45m + (offset % 3) * 0.22m, 2)
                };
            }).ToList();

            return Results.Ok(new
            {
                throughputPerDay = Math.Round(Math.Max(1.0, merged.Count / 4.0), 1),
                medianLeadTimeMinutes = Math.Round(medianLeadMinutes, 1),
                firstPassGateRatePercent = firstPassRate,
                averageReviewRounds = avgRounds,
                blockedCount = workItems.Count(w => w.BoardStatus == BoardStatus.Blocked),
                sessionsToday = sessionsToday.Count,
                dailySessionBudget = 40,
                spendTodayUsd = spendToday,
                dailySpendBudgetUsd = 25.0m,
                mttrMinutes = Math.Round(mttrSeconds / 60.0, 1),
                flakyTestCount = flakyTotal,
                requirementCoveragePercent = reqCoveragePct,
                trend30Days,
                recentActivity = audits
            });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // Audit Log & CSV Export (§13, §14.2 Screen 8)
        app.MapGet("/api/audit", async (
            string? search,
            int? issueNumber,
            string? actor,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var q = db.AuditEvents.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                q = q.Where(a => a.Action.Contains(search) || a.EntityType.Contains(search) || a.Actor.Contains(search) || (a.AfterJson != null && a.AfterJson.Contains(search)));
            }
            if (issueNumber.HasValue)
                q = q.Where(a => a.IssueNumber == issueNumber.Value);
            if (!string.IsNullOrWhiteSpace(actor))
                q = q.Where(a => a.Actor == actor);

            var events = await q.OrderByDescending(a => a.Timestamp).Take(300).ToListAsync(ct);
            return Results.Ok(events);
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapGet("/api/audit/export.csv", async (
            string? search,
            IAuditLogger auditLogger,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var q = db.AuditEvents.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(a => a.Action.Contains(search) || a.EntityType.Contains(search) || a.Actor.Contains(search));

            var events = await q.OrderByDescending(a => a.Timestamp).Take(1000).ToListAsync(ct);
            var csv = auditLogger.FormatAsCsv(events);
            return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", "demo-factory-audit.csv");
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        return app;
    }
}
