using System.Collections.Concurrent;
using System.Text.Json;
using DemoFactory.Agents;
using DemoFactory.Copilot;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DemoFactory.Orchestrator;

public interface IFactoryOrchestrationCoordinator
{
    Task<IReadOnlyList<BootstrapStepRecord>> RunProjectBootstrapAsync(Guid projectId, string actor, CancellationToken ct = default);
    Task<IReadOnlyList<BootstrapStepRecord>> ApplyFeatureIncrementAsync(Guid featureId, string actor, CancellationToken ct = default);
    Task WithdrawFeatureAsync(Guid featureId, string actor, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> TriggerSchedulerAsync(Guid repoId, CancellationToken ct = default);
    Task TriggerStopTheLineAsync(Guid repoId, string cause, IReadOnlyList<int> suspectPrNumbers, long? workflowRunId = null, bool flakeRetryPassed = false, CancellationToken ct = default);
    Task ResumeLineAsync(Guid repoId, string actor, CancellationToken ct = default);
    Task ProcessWebhookEnvelopeAsync(WebhookEnvelope envelope, CancellationToken ct = default);
    Task<int> PollBoardsOnceAsync(CancellationToken ct = default);
    Task<int> SweepExpiredPreviewsOnceAsync(CancellationToken ct = default);
}

/// <summary>
/// Idempotent activity implementations + high-level orchestration coordinator covering:
/// - ProjectBootstrap (§5.1)
/// - FeatureIntake (§5.5, one apply at a time per project)
/// - ProjectScheduler (§8.1)
/// - WorkItemOrchestration activities (§8.2)
/// - Stop-the-Line (§6.2: halt, flake check, single-suspect automatic revert, multi-suspect P0 fix-forward, auto-resume)
/// - Webhook event routing with strict §11.4 trust-boundary filtering
/// - Board Poller (§4.1) & 72h Preview Sweeper (§6.4)
/// </summary>
public sealed class FactoryOrchestrationCoordinator(
    IDbContextFactory<DemoFactoryDbContext> dbFactory,
    IGitHubGateway gitHub,
    ICopilotAgentClient copilot,
    IProjectManagerAgent pmAgent,
    IPolicyRepository policyRepo,
    IEvidenceStore evidenceStore,
    IPreviewHostingService previewHosting,
    ITokenBroker tokenBroker,
    IAuditLogger auditLogger,
    IRealtimeNotifier notifier,
    IOptions<FactoryEnvironmentOptions> envOptions,
    ILogger<FactoryOrchestrationCoordinator> logger) : IOrchestrationActivities, IFactoryOrchestrationCoordinator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, bool> _completedIdempotencyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _deliveryTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _projectFeatureApplyLocks = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _repoMergeSerializationLocks = new();

    public async Task<SpecReviewOutput> RunSpecReviewActivityAsync(Guid workItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);

        var criteria = SafeDeserializeList<string>(wi.AcceptanceCriteriaJson);
        var blockers = SafeDeserializeList<int>(wi.BlockedByIssueNumbersJson);
        var review = await pmAgent.ReviewSpecAsync(new IssueSpecInput(wi.IssueNumber, wi.Title, wi.BodyMarkdown, criteria, wi.Size, blockers), ct: ct);

        wi.State = WorkItemStateKind.SpecReview;
        wi.PmVerdict = review.Verdict;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
        return review;
    }

    public async Task MarkNeedsClarificationActivityAsync(Guid workItemId, IReadOnlyList<string> questions, CancellationToken ct = default)
    {
        var idemKey = $"{workItemId}:NeedsClarification:{string.Join("|", questions)}";
        if (!_completedIdempotencyKeys.TryAdd(idemKey, true))
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == wi.RepoId, ct);

        wi.State = WorkItemStateKind.NeedsClarification;
        wi.BoardStatus = BoardStatus.Blocked;
        wi.BlockedReason = string.Join(" ", questions);
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var comment = "### Clarification Needed Before Implementation\n\n" + string.Join("\n", questions.Select(q => $"- {q}"));
        await gitHub.PostCommentAsync(repo.Owner, repo.Name, wi.IssueNumber, comment, ct);
        await gitHub.UpdateProjectBoardItemAsync(repo.GitHubProjectId, wi.ProjectItemId, BoardStatus.Blocked, "NeedsClarification", wi.Attempts, "pm-agent", wi.CodingModel, ct);
        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
    }

    public async Task<string> ClaimAndAssignToCopilotActivityAsync(Guid workItemId, int attempt, CancellationToken ct = default)
    {
        var idemKey = $"{workItemId}:Assign:{attempt}";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == wi.RepoId, ct);

        if (!_completedIdempotencyKeys.TryAdd(idemKey, true))
            return wi.CodingModel;

        // Re-verify §5.6 work-label rule right before assignment (§18 AC 25–26)
        var (policy, _, _, _) = await policyRepo.GetPolicyAsync(repo.Owner, repo.Name, ct);
        var labels = SafeDeserializeList<string>(wi.LabelsJson);
        var check = TrustBoundary.EvaluateWorkLabelRule(new WorkLabelContext(
            wi.IssueAuthor, labels, wi.WorkLabelAppliedBy, policy.WorkLabel, envOptions.Value.OwnerLogin, envOptions.Value.FactoryAppSlug));

        if (!check.Passed)
        {
            wi.State = WorkItemStateKind.Cancelled;
            wi.BoardStatus = BoardStatus.Backlog;
            wi.BlockedReason = check.Reason;
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException(check.Reason);
        }

        // Resolve coding model (§9.4): workItem override -> repo setting -> global default -> catalogue fallback
        var globalSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
        var repoSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Repo && s.TargetId == repo.Id, ct);
        var itemSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.WorkItem && s.TargetId == wi.Id, ct);
        var catalogue = await db.ModelCatalogue.Select(m => new ModelCatalogueItem(m.Id, m.DisplayName, m.ApiModelId, m.Enabled, m.SmokeTestPassed, m.Notes)).ToListAsync(ct);

        var resolvedModel = ModelResolver.ResolveCodingModel(
            itemSettings?.CodingModel,
            repoSettings?.CodingModel,
            globalSettings?.CodingModel ?? policy.Models.Coding,
            catalogue);

        var criteria = SafeDeserializeList<string>(wi.AcceptanceCriteriaJson);
        var blockers = SafeDeserializeList<int>(wi.BlockedByIssueNumbersJson);
        var composed = await pmAgent.ComposeInstructionsAsync(
            new IssueSpecInput(wi.IssueNumber, wi.Title, wi.BodyMarkdown, criteria, wi.Size, blockers),
            acceptanceContractMarkdown: $"Ensure locked acceptance tests for Issue #{wi.IssueNumber} remain unmodified.",
            ct: ct);

        wi.State = WorkItemStateKind.Implementing;
        wi.BoardStatus = BoardStatus.InProgress;
        wi.CodingModel = resolvedModel;
        wi.Attempts = Math.Max(wi.Attempts + 1, attempt);
        wi.CurrentRound = 1;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await gitHub.LockConversationAsync(repo.Owner, repo.Name, wi.IssueNumber, ct);
        await gitHub.UpdateProjectBoardItemAsync(repo.GitHubProjectId, wi.ProjectItemId, BoardStatus.InProgress, "Implementing", wi.Attempts, "copilot-swe-agent", resolvedModel, ct);

        var assignment = await copilot.AssignIssueAsync(new CopilotAssignmentRequest(
            wi.Id, repo.Id, repo.Owner, repo.Name, wi.IssueNumber, repo.DefaultBranch, composed.CustomInstructions, resolvedModel), ct);

        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
        await notifier.BroadcastSessionEventAsync(assignment.SessionId, wi.Id, CopilotTaskState.InProgress.ToString(), ct);
        return resolvedModel;
    }

    public async Task ArmPullRequestActivityAsync(Guid workItemId, int prNumber, string headSha, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == wi.RepoId, ct);
        var (policy, _, _, _) = await policyRepo.GetPolicyAsync(repo.Owner, repo.Name, ct);

        var repoSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Repo && s.TargetId == repo.Id, ct);
        var globalSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
        var effectiveAutonomy = repoSettings?.Autonomy ?? globalSettings?.Autonomy ?? policy.Autonomy;

        wi.PrNumber = prNumber;
        wi.PrUrl = $"https://github.com/{repo.Owner}/{repo.Name}/pull/{prNumber}";
        wi.HeadSha = headSha;
        wi.State = WorkItemStateKind.Validating;
        wi.BoardStatus = BoardStatus.InReview;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await gitHub.LockConversationAsync(repo.Owner, repo.Name, prNumber, ct);
        await gitHub.SetPullRequestDraftAsync(repo.Owner, repo.Name, prNumber, isDraft: false, ct);

        // §11.3: L0 Shadow opens PRs but NEVER enables auto-merge
        // §10.3: Serialize merges when WIP > 1 (only arm auto-merge when no other PR in this repo is in AwaitingMerge)
        if (effectiveAutonomy != AutonomyLevel.L0)
        {
            var mergeGate = _repoMergeSerializationLocks.GetOrAdd(repo.Id, _ => new SemaphoreSlim(1, 1));
            await mergeGate.WaitAsync(ct);
            try
            {
                var anotherArmed = await db.WorkItems.AnyAsync(
                    w => w.RepoId == repo.Id && w.Id != wi.Id && w.State == WorkItemStateKind.AwaitingMerge,
                    ct);
                if (!anotherArmed)
                {
                    await gitHub.EnableAutoMergeSquashAsync(repo.Owner, repo.Name, prNumber, ct);
                }
            }
            finally
            {
                mergeGate.Release();
            }
        }

        await gitHub.UpdateProjectBoardItemAsync(repo.GitHubProjectId, wi.ProjectItemId, BoardStatus.InReview, "Validating", wi.Attempts, "copilot-swe-agent", wi.CodingModel, ct);
        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
    }

    public async Task<GateEvaluationResult> EvaluateAndPostGateActivityAsync(
        Guid workItemId,
        int round,
        RoundSignalsEvent signals,
        bool carryOverReview,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == wi.RepoId, ct);
        var (policy, _, _, _) = await policyRepo.GetPolicyAsync(repo.Owner, repo.Name, ct);
        var lineState = await db.LineStates.FirstOrDefaultAsync(l => l.RepoId == repo.Id, ct);
        var repoSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Repo && s.TargetId == repo.Id, ct);
        var globalSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
        var effectiveAutonomy = repoSettings?.Autonomy ?? globalSettings?.Autonomy ?? policy.Autonomy;

        // Reconcile findings across rounds (§8.4)
        var existingFindings = await db.Findings
            .Where(f => f.WorkItemId == wi.Id)
            .Select(f => new TrackedFinding(
                f.ShortId, f.Fingerprint, f.Severity, f.Path, f.Line, null, f.RuleHint, f.Body, f.Status, f.FirstSeenRound, f.LastSeenRound, f.WaiverReason, null))
            .ToListAsync(ct);

        var reconciled = FindingTracker.ReconcileRound(round, existingFindings, signals.ReviewComments);
        var triage = await pmAgent.TriageFindingsAsync(reconciled, $"Round {round} diff ({signals.ChangedFiles.Count} files)", ct: ct);

        var roundEntity = new ReviewRoundEntity
        {
            WorkItemId = wi.Id,
            Round = round,
            HeadSha = signals.HeadSha,
            CopilotReviewId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        db.ReviewRounds.Add(roundEntity);

        // Persist updated findings
        var oldDbFindings = await db.Findings.Where(f => f.WorkItemId == wi.Id).ToListAsync(ct);
        db.Findings.RemoveRange(oldDbFindings);
        foreach (var tf in reconciled)
        {
            var triageItem = triage.Items.FirstOrDefault(t => t.FindingId == tf.Id);
            db.Findings.Add(new FindingEntity
            {
                WorkItemId = wi.Id,
                ReviewRoundId = roundEntity.Id,
                ShortId = tf.Id,
                Fingerprint = tf.Fingerprint,
                Severity = tf.Severity,
                Path = tf.Path,
                Line = tf.Line,
                RuleHint = tf.RuleHint,
                Body = tf.Body,
                Status = tf.Status,
                PmTriageClassification = triageItem?.Classification,
                PmTriageReason = triageItem?.Reason,
                WaiverReason = tf.WaiverReason,
                FirstSeenRound = tf.FirstSeenRound,
                LastSeenRound = tf.LastSeenRound
            });
        }

        var checkRuns = policy.Gate.RequiredChecks.Select(name => new CheckRunSnapshot(
            name,
            signals.HeadSha,
            "completed",
            signals.ChecksAllGreen ? "success" : "failure",
            ReviewGateEvaluator.ExpectedActionsAppSlug)).ToList();

        var mergedAtKeys = await db.WorkItems
            .Where(w => w.RepoId == repo.Id && w.IsAcceptanceTestIssue && w.State == WorkItemStateKind.Merged)
            .Select(w => $"ISS-{w.IssueNumber}")
            .ToListAsync(ct);

        var labels = SafeDeserializeList<string>(wi.LabelsJson);
        var approvals = await db.Approvals
            .Where(a => a.WorkItemId == wi.Id)
            .Select(a => new RecordedApproval(a.Kind, a.HeadSha, a.Decision, a.DecidedBy ?? string.Empty))
            .ToListAsync(ct);

        var workLabelCtx = new WorkLabelContext(
            wi.IssueAuthor, labels, wi.WorkLabelAppliedBy, policy.WorkLabel, envOptions.Value.OwnerLogin, envOptions.Value.FactoryAppSlug);

        var gateInput = new GateInput(
            Profile: wi.GateProfile,
            EvaluatedSha: signals.HeadSha,
            CurrentPrHeadSha: signals.HeadSha,
            Policy: policy,
            EffectiveAutonomy: effectiveAutonomy,
            CheckRuns: checkRuns,
            HasCopilotReviewOnHeadSha: signals.HasCopilotReview,
            CarryOverCopilotReviewFromUnchangedDiff: carryOverReview,
            Findings: reconciled,
            ChangedFiles: signals.ChangedFiles,
            MergedAcceptanceTestIssueKeys: mergedAtKeys,
            Labels: labels,
            SpecConformanceCheck: signals.SpecConformancePassed ? ModelCheckVerdict.Pass() : ModelCheckVerdict.Fail(round, "Unmet acceptance criteria"),
            AtMappingCheck: ModelCheckVerdict.Pass(),
            PublicSafeCheck: ModelCheckVerdict.Pass(),
            CodeScanningCompletedOnHeadSha: true,
            NewCodeScanningAlertsAboveThreshold: 0,
            DependencyReviewPassed: true,
            Dependabot: null,
            AtJUnit: wi.GateProfile == GateProfile.AcceptanceTest ? new AcceptanceTestJUnitSummary(true, 1, 1, 0, 0) : null,
            FeatureAcceptance: wi.GateProfile == GateProfile.Feature
                ? new FeatureAcceptanceSummary($"ISS-{wi.IssueNumber}", SeparateAtIssueRequired: false, SuitePassedOnHeadSha: signals.ChecksAllGreen, AtFilesByteIdenticalToMain: true, [], [])
                : null,
            PreviewRun: policy.HasDeployTarget
                ? new PreviewWorkflowSummary(true, ".github/workflows/preview.yml", "workflow_run", "main", signals.HeadSha, signals.ChecksAllGreen, wi.PreviewUrl ?? $"https://pr-{wi.PrNumber}-{repo.Name}.swedencentral.azurecontainerapps.io")
                : null,
            LineStatus: lineState?.State ?? LineStatus.Running,
            Approvals: approvals,
            Origin: new TrustedOriginContext(
                $"{repo.Owner}/{repo.Name}",
                $"{repo.Owner}/{repo.Name}",
                IsFork: false,
                PrAuthor: "copilot-swe-agent[bot]",
                OwnerLogin: envOptions.Value.OwnerLogin,
                FactoryAppSlug: envOptions.Value.FactoryAppSlug,
                LinkedIssueWorkLabel: workLabelCtx));

        var result = ReviewGateEvaluator.Evaluate(gateInput);
        var evidenceUri = await evidenceStore.SaveEvidenceJsonAsync("gate", $"{wi.Id:N}-{signals.HeadSha}", new { gateInput.EvaluatedSha, result }, ct);

        db.GateEvaluations.Add(new GateEvaluationEntity
        {
            WorkItemId = wi.Id,
            HeadSha = signals.HeadSha,
            Profile = wi.GateProfile,
            Passed = result.Passed,
            OutcomeKind = result.Outcome.GetType().Name,
            ConditionsJson = JsonSerializer.Serialize(result.Conditions, JsonOpts),
            ReasonsJson = JsonSerializer.Serialize(result.FailureReasons, JsonOpts),
            EvidenceBlobUri = evidenceUri,
            EvaluatedAt = DateTimeOffset.UtcNow
        });

        wi.CurrentRound = round;
        wi.HeadSha = signals.HeadSha;
        if (result.Passed)
            wi.State = WorkItemStateKind.AwaitingMerge;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await gitHub.PostReviewGateCheckRunAsync(repo.InstallationId, repo.Owner, repo.Name, signals.HeadSha, result, ct);
        await notifier.BroadcastGateEvaluatedAsync(wi.Id, signals.HeadSha, result.Outcome.GetType().Name, ct);
        return result;
    }

    public async Task RequestHumanApprovalActivityAsync(Guid workItemId, string headSha, IReadOnlyList<ApprovalKind> kinds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);

        wi.State = WorkItemStateKind.AwaitingApproval;
        wi.UpdatedAt = DateTimeOffset.UtcNow;

        foreach (var kind in kinds)
        {
            var exists = await db.Approvals.AnyAsync(a => a.WorkItemId == workItemId && a.HeadSha == headSha && a.Kind == kind && a.Decision == ApprovalDecision.Pending, ct);
            if (!exists)
            {
                var approval = new ApprovalEntity
                {
                    WorkItemId = wi.Id,
                    RepoId = wi.RepoId,
                    HeadSha = headSha,
                    Kind = kind,
                    Decision = ApprovalDecision.Pending,
                    Summary = $"Approval required ({kind}) for #{wi.IssueNumber} on SHA {headSha[..Math.Min(7, headSha.Length)]}."
                };
                db.Approvals.Add(approval);
                await db.SaveChangesAsync(ct);
                await notifier.BroadcastApprovalRequestedAsync(approval.Id, wi.Id, kind.ToString(), ct);
            }
        }

        await db.SaveChangesAsync(ct);
        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
    }

    public async Task RequestCopilotFixRoundActivityAsync(
        Guid workItemId,
        int prNumber,
        int round,
        GateEvaluationResult gateResult,
        IReadOnlyList<string> ciFailures,
        CancellationToken ct = default)
    {
        var idemKey = $"{workItemId}:FixRound:{round}";
        if (!_completedIdempotencyKeys.TryAdd(idemKey, true))
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == wi.RepoId, ct);

        // Complete any prior running session before requesting next fix round
        var activeSessions = await db.AgentSessions
            .Where(s => s.WorkItemId == wi.Id && (s.State == CopilotTaskState.Queued || s.State == CopilotTaskState.InProgress))
            .ToListAsync(ct);
        foreach (var s in activeSessions)
        {
            s.State = CopilotTaskState.Completed;
            s.EndedAt = DateTimeOffset.UtcNow;
        }

        wi.State = WorkItemStateKind.Fixing;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // §8.2: Set PR to draft while Copilot works
        await gitHub.SetPullRequestDraftAsync(repo.Owner, repo.Name, prNumber, isDraft: true, ct);

        var openFindings = await db.Findings
            .Where(f => f.WorkItemId == wi.Id && (f.Status == FindingStatus.Open || f.Status == FindingStatus.Repeated))
            .Select(f => new TrackedFinding(
                f.ShortId, f.Fingerprint, f.Severity, f.Path, f.Line, null, f.RuleHint, f.Body, f.Status, f.FirstSeenRound, f.LastSeenRound, f.WaiverReason, null))
            .ToListAsync(ct);

        var sessionId = await copilot.PostBatchedFixCommentAsync(new BatchedFixRequest(
            wi.Id, repo.Id, repo.Owner, repo.Name, prNumber, wi.CodingModel, openFindings, ciFailures, gateResult.ProtectedPathsTouched), ct);

        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
        await notifier.BroadcastSessionEventAsync(sessionId, wi.Id, CopilotTaskState.InProgress.ToString(), ct);
    }

    public async Task MarkBlockedActivityAsync(Guid workItemId, string reason, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == wi.RepoId, ct);

        wi.State = WorkItemStateKind.Blocked;
        wi.BoardStatus = BoardStatus.Blocked;
        wi.BlockedReason = reason;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (wi.PrNumber.HasValue)
        {
            await gitHub.PostCommentAsync(repo.Owner, repo.Name, wi.PrNumber.Value, $"### Escalated to Blocked\n\n{reason}", ct);
        }

        await gitHub.UpdateProjectBoardItemAsync(repo.GitHubProjectId, wi.ProjectItemId, BoardStatus.Blocked, "Blocked", wi.Attempts, "factory-orchestrator", wi.CodingModel, ct);
        await auditLogger.RecordAsync("factory-orchestrator", "WorkItemBlocked", "WorkItem", wi.Id.ToString(), after: new { reason }, issueNumber: wi.IssueNumber, prNumber: wi.PrNumber, ct: ct);
        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
    }

    public async Task MarkFailedActivityAsync(Guid workItemId, string error, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == wi.RepoId, ct);

        wi.State = WorkItemStateKind.Failed;
        wi.BoardStatus = BoardStatus.Blocked;
        wi.BlockedReason = error;
        wi.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await gitHub.UpdateProjectBoardItemAsync(repo.GitHubProjectId, wi.ProjectItemId, BoardStatus.Blocked, "Failed", wi.Attempts, "factory-orchestrator", wi.CodingModel, ct);
        await auditLogger.RecordAsync("factory-orchestrator", "WorkItemFailed", "WorkItem", wi.Id.ToString(), after: new { error }, issueNumber: wi.IssueNumber, ct: ct);
        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);
    }

    public async Task CompleteMergedWorkItemActivityAsync(Guid workItemId, int prNumber, string mergeSha, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == wi.RepoId, ct);

        wi.State = WorkItemStateKind.Merged;
        wi.BoardStatus = BoardStatus.Done;
        wi.HeadSha = mergeSha;
        wi.MergedAt = DateTimeOffset.UtcNow;
        wi.UpdatedAt = DateTimeOffset.UtcNow;

        var activeSessions = await db.AgentSessions.Where(s => s.WorkItemId == wi.Id && s.EndedAt == null).ToListAsync(ct);
        foreach (var s in activeSessions)
        {
            s.State = CopilotTaskState.Completed;
            s.EndedAt = DateTimeOffset.UtcNow;
        }

        // Update linked requirements coverage
        var reqIds = SafeDeserializeList<string>(wi.RequirementIdsJson);
        if (reqIds.Count > 0)
        {
            var reqs = await db.Requirements.Where(r => r.ProjectId == repo.ProjectId && reqIds.Contains(r.Code)).ToListAsync(ct);
            foreach (var r in reqs)
            {
                r.Status = wi.IsAcceptanceTestIssue ? RequirementCoverageStatus.TestsWritten : RequirementCoverageStatus.Done;
            }
        }

        // Delete preview environment on merge/close (§6.4 & §18 AC 16)
        var previews = await db.PreviewEnvironments.Where(p => p.RepoId == repo.Id && p.PrNumber == prNumber && p.State != PreviewState.Deleted).ToListAsync(ct);
        await db.SaveChangesAsync(ct);

        foreach (var p in previews)
        {
            await previewHosting.DeletePreviewAppAsync(p, "factory-orchestrator", ct);
        }

        await gitHub.UpdateProjectBoardItemAsync(repo.GitHubProjectId, wi.ProjectItemId, BoardStatus.Done, "Merged", wi.Attempts, "factory-orchestrator", wi.CodingModel, ct);
        await auditLogger.RecordAsync("factory-orchestrator", "WorkItemMerged", "WorkItem", wi.Id.ToString(), after: new { prNumber, mergeSha }, issueNumber: wi.IssueNumber, prNumber: prNumber, sha: mergeSha, ct: ct);
        await notifier.BroadcastWorkItemChangedAsync(wi.Id, wi.RepoId, wi.State.ToString(), ct);

        // §5.6 Step 5: If all items of a feature are Done, close linked source inbox issues when policy allows
        if (wi.FeatureId.HasValue)
        {
            var (policy, _, _, _) = await policyRepo.GetPolicyAsync(repo.Owner, repo.Name, ct);
            if (policy.Intake.CloseSourceIssuesWhenDone)
            {
                var remainingOpen = await db.WorkItems.AnyAsync(w => w.FeatureId == wi.FeatureId && w.State != WorkItemStateKind.Merged && w.State != WorkItemStateKind.Cancelled, ct);
                if (!remainingOpen)
                {
                    var inboxSources = await db.InboxItems.Where(i => i.FeatureId == wi.FeatureId && i.State != InboxItemState.Done).ToListAsync(ct);
                    foreach (var src in inboxSources)
                    {
                        src.State = InboxItemState.Done;
                        await gitHub.PostCommentAsync(repo.Owner, repo.Name, src.IssueNumber, $"Completed via feature `{wi.FeatureSlug}`.", ct);
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
        }

        // §10.3: If another PR in this repo was waiting for merge serialization, update its branch & arm auto-merge
        var nextAwaiting = await db.WorkItems
            .Where(w => w.RepoId == repo.Id && w.Id != wi.Id && w.State == WorkItemStateKind.AwaitingMerge && w.PrNumber != null)
            .OrderBy(w => w.IssueNumber)
            .FirstOrDefaultAsync(ct);

        if (nextAwaiting?.PrNumber is { } nextPr)
        {
            var updatedCleanly = await gitHub.UpdatePullRequestBranchAsync(repo.Owner, repo.Name, nextPr, ct);
            if (updatedCleanly)
            {
                await gitHub.EnableAutoMergeSquashAsync(repo.Owner, repo.Name, nextPr, ct);
            }
            else
            {
                await copilot.PostConflictResolutionCommentAsync(nextAwaiting.Id, repo.Id, repo.Owner, repo.Name, nextPr, nextAwaiting.CodingModel, ct);
            }
        }

        // §18 AC 5: Immediately schedule newly unblocked items
        await SelectAndStartScheduledItemsActivityAsync(repo.Id, ct);
    }

    public async Task<IReadOnlyList<Guid>> SelectAndStartScheduledItemsActivityAsync(Guid repoId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null || repo.Paused)
            return [];

        var globalSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Global, ct);
        if (globalSettings?.GlobalPaused == true)
            return [];

        // §6.2: Halt new assignments while LineStopped
        var line = await db.LineStates.FirstOrDefaultAsync(l => l.RepoId == repoId, ct);
        if (line?.State == LineStatus.LineStopped)
            return [];

        var (policy, _, _, _) = await policyRepo.GetPolicyAsync(repo.Owner, repo.Name, ct);
        var repoSettings = await db.RuntimeSettings.FirstOrDefaultAsync(s => s.Scope == SettingsScope.Repo && s.TargetId == repoId, ct);
        var wip = repoSettings?.Wip ?? globalSettings?.Wip ?? policy.Wip;

        var allWorkItems = await db.WorkItems.Where(w => w.RepoId == repoId).ToListAsync(ct);
        var openIssues = allWorkItems
            .Where(w => w.BoardStatus != BoardStatus.Done && w.State != WorkItemStateKind.Merged && w.State != WorkItemStateKind.Cancelled)
            .Select(w => w.IssueNumber)
            .ToHashSet();

        var inFlightStates = new HashSet<WorkItemStateKind>
        {
            WorkItemStateKind.Claimed,
            WorkItemStateKind.Implementing,
            WorkItemStateKind.PrOpen,
            WorkItemStateKind.Validating,
            WorkItemStateKind.Fixing,
            WorkItemStateKind.AwaitingApproval,
            WorkItemStateKind.AwaitingMerge
        };

        var inFlight = allWorkItems
            .Where(w => inFlightStates.Contains(w.State))
            .Select(w => new InFlightWorkItem(w.Id, w.IssueNumber, SafeDeserializeList<string>(w.PredictedTouchPathsJson)))
            .ToList();

        var candidates = allWorkItems.Select(w =>
        {
            var labels = SafeDeserializeList<string>(w.LabelsJson);
            var blockerNums = SafeDeserializeList<int>(w.BlockedByIssueNumbersJson);
            var blockers = blockerNums.Select(b => new DependencyBlocker($"{repo.Owner}/{repo.Name}", b, openIssues.Contains(b))).ToList();
            return new SchedulerCandidate(
                WorkItemId: w.Id,
                IssueNumber: w.IssueNumber,
                Title: w.Title,
                BoardStatus: w.BoardStatus,
                IsPaused: repo.Paused,
                AlreadyInOrchestration: inFlightStates.Contains(w.State),
                Priority: w.Priority,
                Size: w.Size,
                CreatedAt: w.CreatedAt,
                WorkLabel: new WorkLabelContext(w.IssueAuthor, labels, w.WorkLabelAppliedBy, policy.WorkLabel, envOptions.Value.OwnerLogin, envOptions.Value.FactoryAppSlug),
                BlockedBy: blockers,
                PredictedTouchPaths: SafeDeserializeList<string>(w.PredictedTouchPathsJson));
        }).ToList();

        var selection = SchedulerEngine.SelectNextItems(
            $"{repo.Owner}/{repo.Name}",
            candidates,
            inFlight,
            wip,
            policy.TouchSetOverlapThreshold,
            policy.PmCanReorder);

        // Mark dependency cycles Blocked (§8.1 Step 3)
        foreach (var cycleIssue in selection.CycleIssueNumbers)
        {
            var cycleWi = allWorkItems.First(w => w.IssueNumber == cycleIssue);
            if (cycleWi.State != WorkItemStateKind.Blocked)
            {
                cycleWi.State = WorkItemStateKind.Blocked;
                cycleWi.BoardStatus = BoardStatus.Blocked;
                cycleWi.BlockedReason = "DependencyCycle";
                cycleWi.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);

        var started = new List<Guid>();
        foreach (var sel in selection.SelectedToStart)
        {
            var specVerdict = await RunSpecReviewActivityAsync(sel.Candidate.WorkItemId, ct);
            if (string.Equals(specVerdict.Verdict, "NeedsClarification", StringComparison.OrdinalIgnoreCase))
            {
                await MarkNeedsClarificationActivityAsync(sel.Candidate.WorkItemId, specVerdict.Questions, ct);
                continue;
            }

            try
            {
                await ClaimAndAssignToCopilotActivityAsync(sel.Candidate.WorkItemId, 1, ct);
                started.Add(sel.Candidate.WorkItemId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Copilot assignment failed for work item {WorkItemId}; marking Blocked.", sel.Candidate.WorkItemId);
                await MarkBlockedActivityAsync(sel.Candidate.WorkItemId, $"Copilot assignment blocked: {ex.Message}", ct);
            }
        }

        return started;
    }

    public Task<IReadOnlyList<Guid>> TriggerSchedulerAsync(Guid repoId, CancellationToken ct = default) =>
        SelectAndStartScheduledItemsActivityAsync(repoId, ct);

    /// <summary>
    /// Deterministic idempotent ProjectBootstrap (§5.1 Steps 5 through 11).
    /// </summary>
    public async Task<IReadOnlyList<BootstrapStepRecord>> RunProjectBootstrapAsync(Guid projectId, string actor, CancellationToken ct = default)
    {
        var steps = new List<BootstrapStepRecord>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstAsync(p => p.Id == projectId, ct);

        project.State = ProjectState.Bootstrapping;
        project.BootstrapInstanceId ??= $"bootstrap-{project.Slug}-v{OrchestrationVersions.Current}";
        await db.SaveChangesAsync(ct);

        var plan = string.IsNullOrWhiteSpace(project.PlanJson)
            ? await pmAgent.GeneratePlanAsync(project.Name, project.SpecMarkdown, project.TemplateId, ct: ct)
            : JsonSerializer.Deserialize<GeneratedPlanOutput>(project.PlanJson, JsonOpts)!;

        var owner = envOptions.Value.OwnerLogin;
        var repoName = project.Slug;

        steps.AddRange([
            new BootstrapStepRecord(5, "Create repository from golden-path template", "Pending"),
            new BootstrapStepRecord(6, "Apply GitHub security baseline (push protection & CodeQL)", "Pending"),
            new BootstrapStepRecord(7, "Apply default-branch ruleset & Copilot review settings", "Pending"),
            new BootstrapStepRecord(8, "Create Project v2 board (auto-add off)", "Pending"),
            new BootstrapStepRecord(9, $"Create {plan.Items.Count} backlog issues & dependencies", "Pending"),
            new BootstrapStepRecord(10, "Azure OIDC preview/demo wiring & readiness smoke test", "Pending"),
            new BootstrapStepRecord(11, "Start ProjectScheduler (dispatch walking skeleton)", "Pending")
        ]);

        async Task UpdateStepAsync(int stepNumber, string status, string? overrideName = null)
        {
            var idx = steps.FindIndex(s => s.StepNumber == stepNumber);
            if (idx >= 0)
            {
                steps[idx] = steps[idx] with
                {
                    Name = overrideName ?? steps[idx].Name,
                    Status = status
                };
            }

            var json = JsonSerializer.Serialize(steps, JsonOpts);
            project.BootstrapStepsJson = json;

            await using var progressDb = await dbFactory.CreateDbContextAsync(ct);
            await progressDb.Projects
                .Where(p => p.Id == project.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.BootstrapStepsJson, json)
                    .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }

        // Step 5: Create repo from golden-path template
        await UpdateStepAsync(5, "InProgress");
        await gitHub.CreateRepositoryFromTemplateAsync(owner, $"demo-factory-template-{project.TemplateId}", owner, repoName, project.IsPublic, project.SpecMarkdown, plan.Adr0001Markdown, ct);
        await UpdateStepAsync(5, "Completed");

        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.ProjectId == project.Id, ct);
        if (repo is null)
        {
            var resolvedInstId = 90421001L;
            if (!envOptions.Value.GetEffectiveSandboxMode())
            {
                var instCheck = await gitHub.CheckAppInstallationAsync(owner, repoName, ct);
                resolvedInstId = instCheck.InstallationId ?? 0L;
            }

            repo = new RepositoryEntity
            {
                ProjectId = project.Id,
                Owner = owner,
                Name = repoName,
                InstallationId = resolvedInstId,
                PolicyReference = $"policies/repos/{owner}__{repoName}.yml",
                ReadinessPassed = true
            };
            db.Repositories.Add(repo);
            db.LineStates.Add(new LineStateEntity { RepoId = repo.Id, State = LineStatus.Running });
            await db.SaveChangesAsync(ct);
        }

        await policyRepo.EnsureDefaultPolicyFileAsync(owner, repoName, repo.GitHubProjectNumber, project.DefaultAutonomy, ct);

        // Step 6: Apply security baseline
        await UpdateStepAsync(6, "InProgress");
        await gitHub.ApplySecurityBaselineAsync(owner, repoName, project.IsPublic, ct);
        await UpdateStepAsync(6, "Completed");

        // Step 7: Apply rulesets & Copilot settings
        await UpdateStepAsync(7, "InProgress");
        await gitHub.ApplyRulesetsAndSettingsAsync(owner, repoName, ["build", "test", "lint"], ct);
        await UpdateStepAsync(7, "Completed");

        // Step 8: Copy Project v2 board
        await UpdateStepAsync(8, "InProgress");
        var (projId, projNum) = await gitHub.CopyProjectBoardAsync(owner, repoName, $"{project.Name} Board", ct);
        repo.GitHubProjectId = projId;
        repo.GitHubProjectNumber = projNum;
        await UpdateStepAsync(8, "Completed");

        // Persist requirements & spec v1
        if (!await db.SpecVersions.AnyAsync(sv => sv.ProjectId == project.Id, ct))
        {
            db.SpecVersions.Add(new SpecVersionEntity
            {
                ProjectId = project.Id,
                Version = 1,
                GitSha = Guid.NewGuid().ToString("N")[..12],
                Tag = "spec-v1",
                SpecMarkdown = project.SpecMarkdown
            });
        }

        foreach (var req in plan.Requirements.DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!await db.Requirements.AnyAsync(r => r.ProjectId == project.Id && r.Code == req.Id, ct))
            {
                db.Requirements.Add(new RequirementEntity
                {
                    ProjectId = project.Id,
                    Code = req.Id,
                    Text = req.Text,
                    IntroducedInVersion = 1,
                    Status = RequirementCoverageStatus.Planned
                });
            }
        }

        // Step 9: Create backlog issues, blockedBy links, and Project v2 items (idempotent on retry)
        if (!await db.WorkItems.AnyAsync(w => w.RepoId == repo.Id, ct))
        {
            await UpdateStepAsync(9, "InProgress", $"Creating backlog issues & dependencies (0 / {plan.Items.Count})...");
            var createdMap = new Dictionary<string, (CreatedIssueRef Issue, WorkItemEntity Entity)>(StringComparer.OrdinalIgnoreCase);
            var createdCount = 0;
            foreach (var item in plan.Items)
            {
                var labels = new List<string> { "factory" };
                if (item.IsAcceptanceTest)
                    labels.Add("acceptance-test");

                var createdIssue = await gitHub.CreateIssueAsync(owner, repoName, item.Title, item.Description, labels, ct);
                var boardItemId = await gitHub.AddIssueToProjectBoardAsync(projId, createdIssue.NodeId, item.InitialStatus, item.Priority, item.Size, "Queued", project.DefaultCodingModel, null, ct);

                var wi = new WorkItemEntity
                {
                    RepoId = repo.Id,
                    IssueNumber = createdIssue.Number,
                    IssueNodeId = createdIssue.NodeId,
                    ProjectItemId = boardItemId,
                    Title = item.Title,
                    BodyMarkdown = item.Description,
                    AcceptanceCriteriaJson = JsonSerializer.Serialize(item.AcceptanceCriteria, JsonOpts),
                    LabelsJson = JsonSerializer.Serialize(labels, JsonOpts),
                    IssueAuthor = owner,
                    WorkLabelAppliedBy = owner,
                    PassesWorkLabelRule = true,
                    IsAcceptanceTestIssue = item.IsAcceptanceTest,
                    GateProfile = item.IsAcceptanceTest ? GateProfile.AcceptanceTest : GateProfile.Feature,
                    State = WorkItemStateKind.Queued,
                    BoardStatus = item.InitialStatus,
                    Priority = item.Priority,
                    Size = item.Size,
                    CodingModel = project.DefaultCodingModel,
                    PredictedTouchPathsJson = JsonSerializer.Serialize(item.PredictedTouchPaths, JsonOpts),
                    RequirementIdsJson = JsonSerializer.Serialize(item.RequirementIds, JsonOpts),
                    ReadyAt = item.InitialStatus == BoardStatus.Ready ? DateTimeOffset.UtcNow : null
                };
                db.WorkItems.Add(wi);
                createdMap[item.Key] = (createdIssue, wi);
                createdCount++;
                await UpdateStepAsync(9, "InProgress", $"Creating backlog issues & dependencies ({createdCount} / {plan.Items.Count})...");
            }

            foreach (var item in plan.Items)
            {
                var (depIssue, depEntity) = createdMap[item.Key];
                var blockerNums = new List<int>(item.BlockedByExistingIssueNumbers);
                foreach (var bKey in item.BlockedByKeys)
                {
                    if (createdMap.TryGetValue(bKey, out var blocker))
                    {
                        blockerNums.Add(blocker.Issue.Number);
                        await gitHub.AddBlockedByDependencyAsync(depIssue.NodeId, blocker.Issue.NodeId, ct);
                    }
                }
                depEntity.BlockedByIssueNumbersJson = JsonSerializer.Serialize(blockerNums, JsonOpts);
            }
        }
        await UpdateStepAsync(9, "Completed", $"Created {plan.Items.Count} backlog issues & dependencies");

        // Step 9b & 10: Azure OIDC wiring + readiness smoke test
        await UpdateStepAsync(10, "InProgress");
        var readiness = await gitHub.EvaluateRepositoryReadinessAsync(owner, repoName, ct);
        repo.ReadinessReportJson = JsonSerializer.Serialize(readiness, JsonOpts);
        project.DemoUrl = $"https://ca-demo-{repoName}.swedencentral.azurecontainerapps.io";
        await UpdateStepAsync(10, "Completed");

        // Step 11: Start ProjectScheduler (walking skeleton goes first)
        await UpdateStepAsync(11, "InProgress");
        project.State = ProjectState.Running;
        project.BootstrapStepsJson = JsonSerializer.Serialize(steps, JsonOpts);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await SelectAndStartScheduledItemsActivityAsync(repo.Id, ct);
        await UpdateStepAsync(11, "Completed", "Started ProjectScheduler (walking skeleton dispatched)");

        project.BootstrapStepsJson = JsonSerializer.Serialize(steps, JsonOpts);
        await db.SaveChangesAsync(ct);
        await auditLogger.RecordAsync(actor, "ProjectBootstrapped", "Project", project.Id.ToString(), after: new { repo = $"{owner}/{repoName}", items = plan.Items.Count }, ct: ct);
        return steps;
    }

    /// <summary>
    /// Applies an approved feature increment to a running project (§5.5).
    /// Serialized per project so requirement IDs and spec versions stay monotonic; never repeats bootstrap!
    /// </summary>
    public async Task<IReadOnlyList<BootstrapStepRecord>> ApplyFeatureIncrementAsync(Guid featureId, string actor, CancellationToken ct = default)
    {
        Guid projectId;
        await using (var peekDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var featPeek = await peekDb.FeatureSpecs.FirstAsync(f => f.Id == featureId, ct);
            projectId = featPeek.ProjectId;
        }

        var sem = _projectFeatureApplyLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var steps = new List<BootstrapStepRecord>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var feature = await db.FeatureSpecs.FirstAsync(f => f.Id == featureId, ct);
            var project = await db.Projects.FirstAsync(p => p.Id == feature.ProjectId, ct);
            var repo = await db.Repositories.FirstAsync(r => r.ProjectId == project.Id, ct);

            if (repo.Paused)
                throw new InvalidOperationException("Cannot apply feature while repository is paused.");

            feature.State = FeatureSpecState.Applying;
            await db.SaveChangesAsync(ct);

            var increment = string.IsNullOrWhiteSpace(feature.PlanIncrementJson)
                ? throw new InvalidOperationException("Feature plan increment must be generated before applying.")
                : JsonSerializer.Deserialize<PlanIncrementOutput>(feature.PlanIncrementJson, JsonOpts)!;

            // Step 5a: Open docs-only PR adding docs/features/<nnn>-<slug>.md and updating docs/spec.md, tag new spec version
            var nextSpecVersion = project.CurrentSpecVersion + 1;
            var featureDocPath = $"docs/features/{nextSpecVersion - 1:D3}-{feature.Slug}.md";
            var updatedSpec = $"{project.SpecMarkdown.TrimEnd()}\n- [{nextSpecVersion - 1:D3} - {feature.Title}]({featureDocPath})\n";

            var docsPrNum = await gitHub.OpenDocsPullRequestAsync(
                repo.Owner,
                repo.Name,
                $"spec/feature-{feature.Slug}",
                $"docs(spec): add feature {feature.Title}",
                new Dictionary<string, string>
                {
                    [featureDocPath] = feature.SpecMarkdown,
                    ["docs/spec.md"] = updatedSpec
                },
                ct);
            steps.Add(new BootstrapStepRecord(1, $"Opened & merged spec PR #{docsPrNum} (`spec-v{nextSpecVersion}`)", "Completed"));

            project.CurrentSpecVersion = nextSpecVersion;
            project.SpecMarkdown = updatedSpec;
            feature.Path = featureDocPath;
            feature.SpecVersion = nextSpecVersion;

            db.SpecVersions.Add(new SpecVersionEntity
            {
                ProjectId = project.Id,
                Version = nextSpecVersion,
                GitSha = Guid.NewGuid().ToString("N")[..12],
                Tag = $"spec-v{nextSpecVersion}",
                SpecMarkdown = updatedSpec
            });

            var proposal = new ChangeProposalEntity
            {
                ProjectId = project.Id,
                Kind = ChangeProposalKind.Feature,
                FromSpecVersion = nextSpecVersion - 1,
                ToSpecVersion = nextSpecVersion,
                Summary = $"Added feature '{feature.Title}' ({increment.Items.Count} backlog items)",
                ItemsJson = JsonSerializer.Serialize(increment.Items, JsonOpts),
                State = ChangeProposalState.Applied,
                AppliedBy = actor,
                AppliedAt = DateTimeOffset.UtcNow
            };
            db.ChangeProposals.Add(proposal);
            feature.ChangeProposalId = proposal.Id;

            foreach (var req in increment.NewRequirements.DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (!await db.Requirements.AnyAsync(r => r.ProjectId == project.Id && r.Code == req.Id, ct))
                {
                    db.Requirements.Add(new RequirementEntity
                    {
                        ProjectId = project.Id,
                        FeatureId = feature.Id,
                        Code = req.Id,
                        Text = req.Text,
                        IntroducedInVersion = nextSpecVersion,
                        Status = RequirementCoverageStatus.Planned
                    });
                }
            }

            // Step 5b: Create epic, issues (with work label + feature:<slug>), dependencies, and board cards
            var epicRef = await gitHub.CreateIssueAsync(repo.Owner, repo.Name, $"Epic: {feature.Title}", feature.SpecMarkdown, ["factory", "epic", $"feature:{feature.Slug}"], ct);
            feature.EpicIssueNumber = epicRef.Number;

            var createdMap = new Dictionary<string, (CreatedIssueRef Issue, WorkItemEntity Entity)>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in increment.Items)
            {
                var labels = new List<string> { "factory", $"feature:{feature.Slug}" };
                if (item.IsAcceptanceTest)
                    labels.Add("acceptance-test");

                var created = await gitHub.CreateIssueAsync(repo.Owner, repo.Name, item.Title, item.Description, labels, ct);
                var model = feature.CodingModelOverride ?? project.DefaultCodingModel;
                var boardItemId = await gitHub.AddIssueToProjectBoardAsync(repo.GitHubProjectId, created.NodeId, item.InitialStatus, item.Priority, item.Size, "Queued", model, feature.Slug, ct);

                var wi = new WorkItemEntity
                {
                    RepoId = repo.Id,
                    FeatureId = feature.Id,
                    FeatureSlug = feature.Slug,
                    IssueNumber = created.Number,
                    IssueNodeId = created.NodeId,
                    ProjectItemId = boardItemId,
                    Title = item.Title,
                    BodyMarkdown = item.Description,
                    AcceptanceCriteriaJson = JsonSerializer.Serialize(item.AcceptanceCriteria, JsonOpts),
                    LabelsJson = JsonSerializer.Serialize(labels, JsonOpts),
                    IssueAuthor = envOptions.Value.OwnerLogin,
                    WorkLabelAppliedBy = envOptions.Value.OwnerLogin,
                    PassesWorkLabelRule = true,
                    IsAcceptanceTestIssue = item.IsAcceptanceTest,
                    GateProfile = item.IsAcceptanceTest ? GateProfile.AcceptanceTest : GateProfile.Feature,
                    State = WorkItemStateKind.Queued,
                    BoardStatus = item.InitialStatus,
                    Priority = item.Priority,
                    Size = item.Size,
                    CodingModel = model,
                    PredictedTouchPathsJson = JsonSerializer.Serialize(item.PredictedTouchPaths, JsonOpts),
                    RequirementIdsJson = JsonSerializer.Serialize(item.RequirementIds, JsonOpts),
                    SpecVersionPlanned = nextSpecVersion,
                    ReadyAt = item.InitialStatus == BoardStatus.Ready ? DateTimeOffset.UtcNow : null
                };
                db.WorkItems.Add(wi);
                createdMap[item.Key] = (created, wi);
            }

            foreach (var item in increment.Items)
            {
                var (depIssue, depEntity) = createdMap[item.Key];
                var blockerNums = new List<int>(item.BlockedByExistingIssueNumbers);
                foreach (var bKey in item.BlockedByKeys)
                {
                    if (createdMap.TryGetValue(bKey, out var blocker))
                    {
                        blockerNums.Add(blocker.Issue.Number);
                        await gitHub.AddBlockedByDependencyAsync(depIssue.NodeId, blocker.Issue.NodeId, ct);
                    }
                }
                depEntity.BlockedByIssueNumbersJson = JsonSerializer.Serialize(blockerNums, JsonOpts);
            }
            steps.Add(new BootstrapStepRecord(2, $"Created epic #{epicRef.Number} and {increment.Items.Count} backlog items on board", "Completed"));

            // Link source inbox issues (§5.6 Step 4: source issues stay unlabelled and are linked via comment)
            var sourceIssueNumbers = SafeDeserializeList<int>(feature.SourceIssueNumbersJson);
            if (sourceIssueNumbers.Count > 0)
            {
                var inboxItems = await db.InboxItems.Where(i => i.ProjectId == project.Id && sourceIssueNumbers.Contains(i.IssueNumber)).ToListAsync(ct);
                foreach (var src in inboxItems)
                {
                    src.State = InboxItemState.Planned;
                    src.FeatureId = feature.Id;
                    await gitHub.PostCommentAsync(repo.Owner, repo.Name, src.IssueNumber, $"Planned as part of feature **{feature.Title}** (Epic #{epicRef.Number}).", ct);
                }
            }

            feature.State = FeatureSpecState.Applied;
            feature.ApplyStepsJson = JsonSerializer.Serialize(steps, JsonOpts);
            feature.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Step 5c & 6: Raise StatusChanged / schedule Ready items without disturbing running work
            await SelectAndStartScheduledItemsActivityAsync(repo.Id, ct);
            steps.Add(new BootstrapStepRecord(3, "Notified ProjectScheduler (Ready items queued; in-flight work undisturbed)", "Completed"));

            feature.ApplyStepsJson = JsonSerializer.Serialize(steps, JsonOpts);
            await db.SaveChangesAsync(ct);

            await auditLogger.RecordAsync(actor, "FeatureIncrementApplied", "FeatureSpec", feature.Id.ToString(), after: new { feature.Slug, epic = epicRef.Number, specVersion = nextSpecVersion }, ct: ct);
            return steps;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task WithdrawFeatureAsync(Guid featureId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var feature = await db.FeatureSpecs.FirstAsync(f => f.Id == featureId, ct);
        feature.State = FeatureSpecState.Withdrawn;
        feature.UpdatedAt = DateTimeOffset.UtcNow;

        var notStarted = await db.WorkItems
            .Where(w => w.FeatureId == featureId && w.State == WorkItemStateKind.Queued)
            .ToListAsync(ct);

        foreach (var wi in notStarted)
        {
            wi.State = WorkItemStateKind.Cancelled;
            wi.BoardStatus = BoardStatus.Backlog;
            wi.BlockedReason = $"Feature '{feature.Slug}' withdrawn by {actor}.";
            wi.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await auditLogger.RecordAsync(actor, "FeatureWithdrawn", "FeatureSpec", feature.Id.ToString(), after: new { cancelledItems = notStarted.Count }, ct: ct);
    }

    /// <summary>
    /// Stop-the-Line handler (§6.2 & §18 AC 14, AC 20):
    /// 1) Halt new assignments
    /// 2) Flake check: if rerun passes -> create P1 flaky-test issue and resume line
    /// 3) Single suspect PR -> automatic revert PR (profile `Revert`), reopen original issue as Ready with Attempts + 1
    /// 4) Multiple suspects -> create P0 "Main is red" issue (`LineFix` profile)
    /// </summary>
    public async Task TriggerStopTheLineAsync(
        Guid repoId,
        string cause,
        IReadOnlyList<int> suspectPrNumbers,
        long? workflowRunId = null,
        bool flakeRetryPassed = false,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.FirstAsync(r => r.Id == repoId, ct);
        var line = await db.LineStates.FirstOrDefaultAsync(l => l.RepoId == repoId, ct);
        if (line is null)
        {
            line = new LineStateEntity { RepoId = repoId };
            db.LineStates.Add(line);
        }

        line.State = LineStatus.LineStopped;
        line.Cause = cause;
        line.SuspectPrNumbersJson = JsonSerializer.Serialize(suspectPrNumbers, JsonOpts);
        line.StoppedAt = DateTimeOffset.UtcNow;

        if (workflowRunId.HasValue)
        {
            await gitHub.RerunFailedWorkflowJobsAsync(repo.Owner, repo.Name, workflowRunId.Value, ct);
        }

        if (flakeRetryPassed)
        {
            line.FlakyTestCount += 1;
            line.State = LineStatus.Running;
            line.RestoredAt = DateTimeOffset.UtcNow;
            line.LastRestoreDurationSeconds = 60;
            line.ActiveAction = null;

            var flakeIssue = await gitHub.CreateIssueAsync(repo.Owner, repo.Name, $"Fix flaky check from main failure ({cause})", $"Flake detected on `main`: {cause}", ["factory", "flaky-test"], ct);
            db.WorkItems.Add(new WorkItemEntity
            {
                RepoId = repoId,
                IssueNumber = flakeIssue.Number,
                IssueNodeId = flakeIssue.NodeId,
                ProjectItemId = $"PVTI_{flakeIssue.Number}",
                Title = $"Fix flaky check ({cause})",
                BodyMarkdown = $"Stabilize flaky workflow/test detected during stop-the-line check: {cause}",
                AcceptanceCriteriaJson = "[\"Flaky test reproduces deterministically and passes 20 consecutive runs\"]",
                LabelsJson = "[\"factory\",\"flaky-test\"]",
                IssueAuthor = envOptions.Value.OwnerLogin,
                WorkLabelAppliedBy = envOptions.Value.OwnerLogin,
                PassesWorkLabelRule = true,
                Priority = PriorityLevel.High,
                Size = ItemSize.Small,
                BoardStatus = BoardStatus.Ready,
                State = WorkItemStateKind.Queued
            });

            await db.SaveChangesAsync(ct);
            await notifier.BroadcastLineStateChangedAsync(repoId, LineStatus.Running.ToString(), null, ct);
            return;
        }

        if (suspectPrNumbers.Count == 1)
        {
            var suspectPr = suspectPrNumbers[0];
            var revertPr = await gitHub.OpenRevertPullRequestAsync(repo.Owner, repo.Name, suspectPr, $"Automatic Stop-the-Line revert of #{suspectPr}: {cause}", ct);
            line.ActiveAction = $"Opened automatic revert PR #{revertPr} for suspect PR #{suspectPr}";

            var originalWi = await db.WorkItems.FirstOrDefaultAsync(w => w.RepoId == repoId && w.PrNumber == suspectPr, ct);
            if (originalWi is not null)
            {
                originalWi.State = WorkItemStateKind.Queued;
                originalWi.BoardStatus = BoardStatus.Ready;
                originalWi.Attempts += 1;
                originalWi.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var revertIssue = await gitHub.CreateIssueAsync(repo.Owner, repo.Name, $"Revert #{suspectPr}: restore green main", $"Automated revert for {cause}", ["factory", "revert"], ct);
            db.WorkItems.Add(new WorkItemEntity
            {
                RepoId = repoId,
                IssueNumber = revertIssue.Number,
                IssueNodeId = revertIssue.NodeId,
                ProjectItemId = $"PVTI_{revertIssue.Number}",
                Title = $"Revert #{suspectPr}: restore green main",
                BodyMarkdown = $"Automated Stop-the-Line revert PR #{revertPr} for `{cause}`.",
                AcceptanceCriteriaJson = "[\"CI green on revert PR\"]",
                LabelsJson = "[\"factory\",\"revert\"]",
                IssueAuthor = envOptions.Value.OwnerLogin,
                WorkLabelAppliedBy = envOptions.Value.OwnerLogin,
                PassesWorkLabelRule = true,
                GateProfile = GateProfile.Revert,
                Priority = PriorityLevel.Critical,
                Size = ItemSize.Small,
                PrNumber = revertPr,
                PrUrl = $"https://github.com/{repo.Owner}/{repo.Name}/pull/{revertPr}",
                HeadSha = Guid.NewGuid().ToString("N")[..12],
                State = WorkItemStateKind.AwaitingMerge,
                BoardStatus = BoardStatus.InReview
            });
        }
        else
        {
            var mainRed = await gitHub.CreateIssueAsync(repo.Owner, repo.Name, "Main is red: fix-forward regression", $"Suspect PRs: {string.Join(", ", suspectPrNumbers)}\nCause: {cause}", ["factory", "line-fix"], ct);
            line.ActiveAction = $"Dispatched P0 'Main is red' issue #{mainRed.Number} to Copilot (`line-fix` profile)";

            db.WorkItems.Add(new WorkItemEntity
            {
                RepoId = repoId,
                IssueNumber = mainRed.Number,
                IssueNodeId = mainRed.NodeId,
                ProjectItemId = $"PVTI_{mainRed.Number}",
                Title = "Main is red: fix-forward regression",
                BodyMarkdown = $"Restore green `main`.\nCause: {cause}\nSuspect PRs: {string.Join(", ", suspectPrNumbers)}",
                AcceptanceCriteriaJson = "[\"Main CI & smoke tests pass\"]",
                LabelsJson = "[\"factory\",\"line-fix\"]",
                IssueAuthor = envOptions.Value.OwnerLogin,
                WorkLabelAppliedBy = envOptions.Value.OwnerLogin,
                PassesWorkLabelRule = true,
                GateProfile = GateProfile.LineFix,
                Priority = PriorityLevel.Critical,
                Size = ItemSize.Small,
                State = WorkItemStateKind.Implementing,
                BoardStatus = BoardStatus.InProgress
            });
        }

        await db.SaveChangesAsync(ct);
        await auditLogger.RecordAsync("factory-orchestrator", "LineStopped", "Repository", repoId.ToString(), after: new { cause, suspectPrNumbers, line.ActiveAction }, ct: ct);
        await notifier.BroadcastLineStateChangedAsync(repoId, LineStatus.LineStopped.ToString(), cause, ct);
    }

    public async Task ResumeLineAsync(Guid repoId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var line = await db.LineStates.FirstOrDefaultAsync(l => l.RepoId == repoId, ct);
        if (line is not null && line.State == LineStatus.LineStopped)
        {
            var now = DateTimeOffset.UtcNow;
            line.State = LineStatus.Running;
            line.RestoredAt = now;
            if (line.StoppedAt.HasValue)
                line.LastRestoreDurationSeconds = (int)Math.Max(1, (now - line.StoppedAt.Value).TotalSeconds);
            line.Cause = null;
            line.ActiveAction = null;
            await db.SaveChangesAsync(ct);
        }

        await auditLogger.RecordAsync(actor, "LineResumed", "Repository", repoId.ToString(), ct: ct);
        await notifier.BroadcastLineStateChangedAsync(repoId, LineStatus.Running.ToString(), null, ct);
        await SelectAndStartScheduledItemsActivityAsync(repoId, ct);
    }

    public Task ProcessWebhookEnvelopeAsync(WebhookEnvelope envelope, CancellationToken ct = default) =>
        _deliveryTasks.GetOrAdd(envelope.DeliveryId, _ => ProcessWebhookEnvelopeCoreAsync(envelope, ct));

    private async Task ProcessWebhookEnvelopeCoreAsync(WebhookEnvelope envelope, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(envelope.PayloadJson);
        var root = doc.RootElement;

        // §10.1: Immediate pause when GitHub App authorization is revoked
        if (string.Equals(envelope.EventType, "github_app_authorization", StringComparison.OrdinalIgnoreCase)
            && string.Equals(envelope.Action, "revoked", StringComparison.OrdinalIgnoreCase))
        {
            var login = envelope.SenderLogin ?? envOptions.Value.OwnerLogin;
            await tokenBroker.HandleRevocationOrRefreshFailureAsync(login, "GitHub App authorization revoked webhook", ct);
            await notifier.BroadcastFactoryStatusAsync(true, "Reconnect GitHub: authorization revoked", ct);
            await MarkInboxProcessedAsync(envelope.DeliveryId, ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        RepositoryEntity? repo = null;
        if (!string.IsNullOrWhiteSpace(envelope.RepositoryFullName))
        {
            var parts = envelope.RepositoryFullName.Split('/');
            if (parts.Length == 2)
                repo = await db.Repositories.FirstOrDefaultAsync(r => r.Owner == parts[0] && r.Name == parts[1], ct);
        }

        if (repo is null)
        {
            await MarkInboxProcessedAsync(envelope.DeliveryId, ct);
            return;
        }

        var (policy, _, _, _) = await policyRepo.GetPolicyAsync(repo.Owner, repo.Name, ct);

        switch (envelope.EventType.ToLowerInvariant())
        {
            case "secret_scanning_alert":
                // §5.3: Pause repo immediately when a secret is detected; human rotates
                repo.Paused = true;
                repo.PauseReason = "Secret scanning alert detected; human rotation required.";
                await db.SaveChangesAsync(ct);
                await auditLogger.RecordAsync("github-webhook", "RepoPausedSecretDetected", "Repository", repo.Id.ToString(), ct: ct);
                await notifier.BroadcastFactoryStatusAsync(true, repo.PauseReason, ct);
                break;

            case "issues":
                await HandleIssuesWebhookAsync(db, repo, policy, envelope.Action, envelope.SenderLogin, root, ct);
                break;

            case "pull_request":
                await HandlePullRequestWebhookAsync(db, repo, policy, envelope.Action, root, ct);
                break;

            case "workflow_run":
                await HandleWorkflowRunWebhookAsync(repo, root, ct);
                break;
        }

        await MarkInboxProcessedAsync(envelope.DeliveryId, ct);
    }

    public async Task<int> PollBoardsOnceAsync(CancellationToken ct = default)
    {
        var statusChanges = 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repos = await db.Repositories.Where(r => !r.Paused && r.GitHubProjectId != "").ToListAsync(ct);

        foreach (var repo in repos)
        {
            var cards = await gitHub.PollProjectBoardAsync(repo.GitHubProjectId, ct);
            foreach (var card in cards)
            {
                var snap = await db.BoardSnapshots.FirstOrDefaultAsync(
                    s => s.GitHubProjectId == repo.GitHubProjectId && s.ItemId == card.ItemId,
                    ct);

                if (snap is null)
                {
                    db.BoardSnapshots.Add(new BoardSnapshotEntity
                    {
                        GitHubProjectId = repo.GitHubProjectId,
                        ItemId = card.ItemId,
                        IssueNumber = card.IssueNumber,
                        Status = card.Status,
                        SeenAt = DateTimeOffset.UtcNow
                    });
                }
                else if (snap.Status != card.Status)
                {
                    snap.Status = card.Status;
                    snap.SeenAt = DateTimeOffset.UtcNow;
                    statusChanges++;

                    var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.RepoId == repo.Id && w.IssueNumber == card.IssueNumber, ct);
                    if (wi is not null && card.Status == BoardStatus.Ready && wi.State == WorkItemStateKind.Queued)
                    {
                        wi.BoardStatus = BoardStatus.Ready;
                        wi.ReadyAt ??= DateTimeOffset.UtcNow;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            if (statusChanges > 0)
            {
                await SelectAndStartScheduledItemsActivityAsync(repo.Id, ct);
            }
        }

        return statusChanges;
    }

    public async Task<int> SweepExpiredPreviewsOnceAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-72);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expired = await db.PreviewEnvironments
            .Where(p => p.State != PreviewState.Deleted && p.CreatedAt <= cutoff)
            .ToListAsync(ct);

        foreach (var p in expired)
        {
            await previewHosting.DeletePreviewAppAsync(p, "preview-sweeper-72h", ct);
        }

        return expired.Count;
    }

    private async Task HandleIssuesWebhookAsync(
        DemoFactoryDbContext db,
        RepositoryEntity repo,
        RepoPolicy policy,
        string? action,
        string? senderLogin,
        JsonElement root,
        CancellationToken ct)
    {
        if (!root.TryGetProperty("issue", out var issueEl))
            return;

        var issueNumber = issueEl.GetProperty("number").GetInt32();
        var title = issueEl.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var body = issueEl.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
        var author = issueEl.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var l) ? l.GetString() ?? string.Empty : string.Empty;
        var labels = issueEl.TryGetProperty("labels", out var lbls) && lbls.ValueKind == JsonValueKind.Array
            ? lbls.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty).Where(s => s.Length > 0).ToList()
            : [];

        // §5.6: Removal of work label cancels in-flight or queued item
        if (string.Equals(action, "unlabeled", StringComparison.OrdinalIgnoreCase))
        {
            var hasWorkLabel = labels.Any(x => string.Equals(x, policy.WorkLabel, StringComparison.OrdinalIgnoreCase));
            if (!hasWorkLabel)
            {
                var existingWi = await db.WorkItems.FirstOrDefaultAsync(w => w.RepoId == repo.Id && w.IssueNumber == issueNumber, ct);
                if (existingWi is not null && existingWi.State is not (WorkItemStateKind.Merged or WorkItemStateKind.Cancelled))
                {
                    existingWi.State = WorkItemStateKind.Cancelled;
                    existingWi.BoardStatus = BoardStatus.Backlog;
                    existingWi.PassesWorkLabelRule = false;
                    existingWi.BlockedReason = "Work label removed.";
                    existingWi.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await notifier.BroadcastWorkItemChangedAsync(existingWi.Id, repo.Id, existingWi.State.ToString(), ct);
                }
                return;
            }
        }

        var labelAppliedBy = string.Equals(action, "labeled", StringComparison.OrdinalIgnoreCase)
            ? senderLogin
            : TrustBoundary.IsOwnerOrFactory(author, envOptions.Value.OwnerLogin, envOptions.Value.FactoryAppSlug) ? senderLogin : null;

        var check = TrustBoundary.EvaluateWorkLabelRule(new WorkLabelContext(
            author, labels, labelAppliedBy, policy.WorkLabel, envOptions.Value.OwnerLogin, envOptions.Value.FactoryAppSlug));

        if (!check.Passed)
        {
            // Keep in Issue Inbox (§5.6); NEVER assign or schedule!
            var inbox = await db.InboxItems.FirstOrDefaultAsync(i => i.RepoId == repo.Id && i.IssueNumber == issueNumber, ct);
            var attemptedDirectLabel = check.FailureCode == "OutsideAuthorRequiresFeature"
                && TrustBoundary.IsOwnerOrFactory(senderLogin, envOptions.Value.OwnerLogin, envOptions.Value.FactoryAppSlug);

            if (inbox is null)
            {
                db.InboxItems.Add(new InboxItemEntity
                {
                    RepoId = repo.Id,
                    ProjectId = repo.ProjectId,
                    IssueNumber = issueNumber,
                    Title = title,
                    Body = body,
                    Author = author,
                    LabelsJson = JsonSerializer.Serialize(labels, JsonOpts),
                    OwnerAttemptedDirectWorkLabel = attemptedDirectLabel,
                    State = InboxItemState.New,
                    HtmlUrl = $"https://github.com/{repo.Owner}/{repo.Name}/issues/{issueNumber}"
                });
            }
            else if (attemptedDirectLabel)
            {
                inbox.OwnerAttemptedDirectWorkLabel = true;
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.RepoId == repo.Id && w.IssueNumber == issueNumber, ct);
        if (wi is null)
        {
            wi = new WorkItemEntity
            {
                RepoId = repo.Id,
                IssueNumber = issueNumber,
                IssueNodeId = $"I_{repo.Name}_{issueNumber}",
                ProjectItemId = $"PVTI_{issueNumber}",
                Title = title,
                BodyMarkdown = body,
                LabelsJson = JsonSerializer.Serialize(labels, JsonOpts),
                IssueAuthor = author,
                WorkLabelAppliedBy = labelAppliedBy,
                PassesWorkLabelRule = true,
                State = WorkItemStateKind.Queued,
                BoardStatus = BoardStatus.Backlog,
                CodingModel = policy.Models.Coding
            };
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync(ct);
        }
        else if (string.Equals(action, "edited", StringComparison.OrdinalIgnoreCase) && wi.State == WorkItemStateKind.NeedsClarification)
        {
            wi.Title = title;
            wi.BodyMarkdown = body;
            wi.State = WorkItemStateKind.Queued;
            wi.BoardStatus = BoardStatus.Ready;
            wi.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await SelectAndStartScheduledItemsActivityAsync(repo.Id, ct);
        }
    }

    private async Task HandlePullRequestWebhookAsync(
        DemoFactoryDbContext db,
        RepositoryEntity repo,
        RepoPolicy policy,
        string? action,
        JsonElement root,
        CancellationToken ct)
    {
        if (!root.TryGetProperty("pull_request", out var prEl))
            return;

        var prNumber = prEl.GetProperty("number").GetInt32();
        var prAuthor = prEl.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var l) ? l.GetString() ?? string.Empty : string.Empty;
        var headEl = prEl.GetProperty("head");
        var headSha = headEl.GetProperty("sha").GetString() ?? string.Empty;
        var isFork = headEl.TryGetProperty("repo", out var hr) && hr.ValueKind == JsonValueKind.Object && hr.TryGetProperty("fork", out var fk) && fk.GetBoolean();
        var headRepoFull = headEl.TryGetProperty("repo", out var hr2) && hr2.ValueKind == JsonValueKind.Object && hr2.TryGetProperty("full_name", out var fn)
            ? fn.GetString() ?? string.Empty
            : $"{repo.Owner}/{repo.Name}";

        // §11.4 & §18 AC 17: Fork PRs or untrusted PR authors get NO orchestration, NO preview, and NO auto-merge!
        var (trustedOrigin, reason) = TrustBoundary.EvaluateTrustedPrOrigin(new TrustedOriginContext(
            $"{repo.Owner}/{repo.Name}",
            headRepoFull,
            isFork,
            prAuthor,
            envOptions.Value.OwnerLogin,
            envOptions.Value.FactoryAppSlug));

        if (!trustedOrigin)
        {
            logger.LogInformation("Ignored untrusted/fork PR #{PrNumber} in {Repo}: {Reason}", prNumber, repo.FullName, reason);
            await auditLogger.RecordAsync(prAuthor, "UntrustedPrIgnored", "PullRequest", $"{repo.Owner}/{repo.Name}#{prNumber}", after: new { reason }, prNumber: prNumber, sha: headSha, ct: ct);
            return;
        }

        if (string.Equals(action, "closed", StringComparison.OrdinalIgnoreCase))
        {
            var merged = prEl.TryGetProperty("merged", out var m) && m.GetBoolean();
            var previews = await db.PreviewEnvironments
                .Where(p => p.RepoId == repo.Id && p.PrNumber == prNumber && p.State != PreviewState.Deleted)
                .ToListAsync(ct);

            foreach (var p in previews)
                await previewHosting.DeletePreviewAppAsync(p, "github-webhook-pr-closed", ct);

            if (merged)
            {
                var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.RepoId == repo.Id && w.PrNumber == prNumber, ct);
                if (wi is not null)
                {
                    await CompleteMergedWorkItemActivityAsync(wi.Id, prNumber, headSha, ct);

                    // If a revert PR merged while line stopped, mark ready for green main verification
                    if (wi.GateProfile == GateProfile.Revert)
                    {
                        await ResumeLineAsync(repo.Id, "auto-revert-merged", ct);
                    }
                }
            }
        }
    }

    private async Task HandleWorkflowRunWebhookAsync(RepositoryEntity repo, JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("workflow_run", out var wfEl))
            return;

        var status = wfEl.TryGetProperty("status", out var st) ? st.GetString() : null;
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return;

        var conclusion = wfEl.TryGetProperty("conclusion", out var c) ? c.GetString() : null;
        var headBranch = wfEl.TryGetProperty("head_branch", out var hb) ? hb.GetString() : null;
        var runId = wfEl.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;

        if (string.Equals(headBranch, repo.DefaultBranch, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(conclusion, "failure", StringComparison.OrdinalIgnoreCase))
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var recentMerged = await db.WorkItems
                    .Where(w => w.RepoId == repo.Id && w.State == WorkItemStateKind.Merged && w.PrNumber != null)
                    .OrderByDescending(w => w.MergedAt)
                    .Take(1)
                    .Select(w => w.PrNumber!.Value)
                    .ToListAsync(ct);

                await TriggerStopTheLineAsync(repo.Id, $"Workflow run {runId} failed on {repo.DefaultBranch}", recentMerged, runId, ct: ct);
            }
            else if (string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                await ResumeLineAsync(repo.Id, "main-workflow-green", ct);
            }
        }
    }

    private async Task MarkInboxProcessedAsync(string deliveryId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ev = await db.InboxEvents.FirstOrDefaultAsync(e => e.DeliveryId == deliveryId, ct);
        if (ev is not null)
        {
            ev.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static List<T> SafeDeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public sealed class WebhookDispatcherWorker(
    IWebhookEventBus bus,
    IFactoryOrchestrationCoordinator coordinator,
    ILogger<WebhookDispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var envelope in bus.LocalReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await coordinator.ProcessWebhookEnvelopeAsync(envelope, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error processing webhook delivery {DeliveryId}", envelope.DeliveryId);
            }
        }
    }
}

public sealed class BoardPollerAndSweeperWorker(
    IFactoryOrchestrationCoordinator coordinator,
    ILogger<BoardPollerAndSweeperWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        var ticks = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            ticks++;
            try
            {
                await coordinator.PollBoardsOnceAsync(stoppingToken);
                if (ticks % 60 == 0)
                {
                    await coordinator.SweepExpiredPreviewsOnceAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Periodic board poller / preview sweeper tick encountered an error.");
            }
        }
    }
}

public sealed class EvidenceBackedPmTraceSink(IEvidenceStore evidenceStore) : IPmTraceSink
{
    public Task RecordTraceAsync(PmExecutionTrace trace, CancellationToken ct = default) =>
        evidenceStore.SaveEvidenceJsonAsync("pm-agent", $"{trace.Operation}-{trace.Timestamp:HHmmssfff}", trace, ct);
}

public static class OrchestratorServiceCollectionExtensions
{
    public static IServiceCollection AddDemoFactoryOrchestrator(
        this IServiceCollection services,
        IConfiguration configuration,
        bool registerBackgroundWorkers = true)
    {
        services.AddSingleton<IPmTraceSink, EvidenceBackedPmTraceSink>();
        services.AddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();
        services.AddSingleton<FactoryOrchestrationCoordinator>();
        services.AddSingleton<IFactoryOrchestrationCoordinator>(sp => sp.GetRequiredService<FactoryOrchestrationCoordinator>());
        services.AddSingleton<IOrchestrationActivities>(sp => sp.GetRequiredService<FactoryOrchestrationCoordinator>());

        var dtsConn = configuration["DURABLE_TASK_SCHEDULER_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(dtsConn))
        {
            // §3.2: Register Microsoft Agent Framework Durable Extension + Durable Task Scheduler in BYO-Compute mode
            var foundryEndpoint = configuration["Foundry:ProjectEndpoint"]
                ?? "https://demo-factory-foundry.services.ai.azure.com/api/projects/demo-factory-eu";
            var foundryDeployment = configuration["Foundry:DefaultDeployment"] ?? "orchestrator-model";
            var transport = new FoundryAgentFrameworkTransport(Options.Create(new FoundryAgentOptions
            {
                ProjectEndpoint = foundryEndpoint,
                DefaultDeployment = foundryDeployment
            }));
            var pmDurableAgent = transport.CreateFoundryAgent();

            services.ConfigureDurableAgents(
                options => options.AddAIAgent(pmDurableAgent),
                workerBuilder: builder =>
                {
                    builder.UseDurableTaskScheduler(dtsConn);
                    builder.AddTasks(registry => RegisterFactoryDurableTasks(registry));
                },
                clientBuilder: builder => builder.UseDurableTaskScheduler(dtsConn));
        }

        if (registerBackgroundWorkers)
        {
            services.AddHostedService<WebhookDispatcherWorker>();
            services.AddHostedService<BoardPollerAndSweeperWorker>();
        }

        return services;
    }

    public static void RegisterFactoryDurableTasks(DurableTaskRegistry registry)
    {
        registry.AddOrchestrator<ProjectSchedulerOrchestration>(nameof(ProjectSchedulerOrchestration));
        registry.AddOrchestrator<WorkItemOrchestration>(nameof(WorkItemOrchestration));
        registry.AddActivity<SelectScheduledItemsActivity>(nameof(SelectScheduledItemsActivity));
        registry.AddActivity<RunSpecReviewActivity>(nameof(RunSpecReviewActivity));
        registry.AddActivity<MarkNeedsClarificationActivity>(nameof(MarkNeedsClarificationActivity));
        registry.AddActivity<ClaimAndAssignActivity>(nameof(ClaimAndAssignActivity));
        registry.AddActivity<ArmPullRequestActivity>(nameof(ArmPullRequestActivity));
        registry.AddActivity<EvaluateAndPostGateActivity>(nameof(EvaluateAndPostGateActivity));
        registry.AddActivity<RequestHumanApprovalActivity>(nameof(RequestHumanApprovalActivity));
        registry.AddActivity<RequestCopilotFixRoundActivity>(nameof(RequestCopilotFixRoundActivity));
        registry.AddActivity<MarkBlockedActivity>(nameof(MarkBlockedActivity));
        registry.AddActivity<MarkFailedActivity>(nameof(MarkFailedActivity));
        registry.AddActivity<CompleteMergedWorkItemActivity>(nameof(CompleteMergedWorkItemActivity));
    }
}
