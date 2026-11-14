using System.Text.Json;
using DemoFactory.Agents;
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

public sealed record CreateProjectRequest(
    string Name,
    string SpecMarkdown,
    string TemplateId = "dotnet-react",
    AutonomyLevel Autonomy = AutonomyLevel.L1,
    string CodingModel = "gemini-4-carbon",
    string DeployTarget = "aca-previews-and-demo",
    bool IsPublic = true);

public sealed record ClarifyAnswersRequest(IReadOnlyDictionary<string, string>? Answers, bool DismissPublicSafeFlags = false);

public sealed record CreateFeatureRequest(
    string Title,
    string SpecMarkdown,
    PriorityLevel Priority = PriorityLevel.Medium,
    string? Milestone = null,
    string? CodingModelOverride = null,
    IReadOnlyList<int>? SourceIssueNumbers = null);

public sealed record FeaturesFromIssuesRequest(IReadOnlyList<int> IssueNumbers);
public sealed record UpdateSpecRequest(string SpecMarkdown, bool DismissPublicSafeFlags = false);

public static class ProjectAndFeatureEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapProjectAndFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        // Golden-path templates (§5.2, §13)
        app.MapGet("/api/templates", () => Results.Ok(new[]
        {
            new
            {
                id = "dotnet-react",
                name = ".NET 11 API + React 19 / Fluent UI v9",
                version = "v1.2.0",
                repository = "demo-factory-template-dotnet-react",
                description = "Full-stack .NET 11 Minimal API + Aspire + React 19 Fluent 2 SPA with Playwright acceptance harness, CodeQL, Dependabot, and OIDC ACA preview/demo workflows."
            },
            new
            {
                id = "python-fastapi",
                name = "Python 3.13 FastAPI + React / Fluent UI v9",
                version = "v1.1.0",
                repository = "demo-factory-template-python-fastapi",
                description = "Python FastAPI service with pytest + Playwright acceptance harness, ruff/mypy quality gate, and OIDC ACA preview/demo workflows."
            }
        })).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // List & get projects
        app.MapGet("/api/projects", async (IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var projects = await db.Projects.OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
            var repos = await db.Repositories.ToListAsync(ct);
            var inboxCounts = await db.InboxItems
                .Where(i => i.State == InboxItemState.New)
                .GroupBy(i => i.ProjectId)
                .Select(g => new { ProjectId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct);

            return Results.Ok(projects.Select(p =>
            {
                var repo = repos.FirstOrDefault(r => r.ProjectId == p.Id);
                return new
                {
                    p.Id,
                    p.Name,
                    p.Slug,
                    p.State,
                    p.CurrentSpecVersion,
                    p.TemplateId,
                    p.TemplateVersion,
                    p.DefaultAutonomy,
                    p.DefaultCodingModel,
                    p.IsPublic,
                    p.DemoUrl,
                    p.CreatedAt,
                    repoId = repo?.Id,
                    repoFullName = repo?.FullName,
                    newInboxCount = inboxCounts.GetValueOrDefault(p.Id, 0)
                };
            }));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapGet("/api/projects/{id:guid}", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
            return p is null ? Results.NotFound() : Results.Ok(p);
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // Step 1 & 2: New project intake + PM spec analysis & public-safe check (§5.1, §13)
        app.MapPost("/api/projects", async (
            CreateProjectRequest req,
            HttpContext http,
            IProjectManagerAgent pmAgent,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.SpecMarkdown))
                return Results.BadRequest(new { error = "Project name and specification are required." });

            var analysis = await pmAgent.AnalyzeSpecForIntakeAsync(req.SpecMarkdown, ct: ct);
            var slug = ProjectManagerAgent.Slugify(req.Name);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.Projects.AnyAsync(p => p.Slug == slug, ct))
                slug = $"{slug}-{Random.Shared.Next(100, 999)}";

            var project = new ProjectEntity
            {
                Name = req.Name.Trim(),
                Slug = slug,
                SpecMarkdown = analysis.ClarifiedSpecMarkdown,
                TemplateId = req.TemplateId,
                DefaultAutonomy = req.Autonomy,
                DefaultCodingModel = req.CodingModel,
                DeployTarget = req.DeployTarget,
                IsPublic = req.IsPublic,
                ClarifyingQuestionsJson = JsonSerializer.Serialize(analysis.ClarifyingQuestions, JsonOpts),
                PublicSafeFlagsJson = JsonSerializer.Serialize(analysis.PublicSafe, JsonOpts),
                State = ProjectState.Clarifying
            };

            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);

            await auditLogger.RecordAsync(http.User.Identity?.Name ?? "admin", "ProjectCreated", "Project", project.Id.ToString(), after: new { project.Name, project.Slug, publicSafePassed = analysis.PublicSafe.Passed }, ct: ct);
            return Results.Created($"/api/projects/{project.Id}", new
            {
                project,
                clarifyingQuestions = analysis.ClarifyingQuestions,
                requirements = analysis.Requirements,
                publicSafe = analysis.PublicSafe
            });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        // Clarify & generate plan (§5.1 Steps 2–3, §13: POST /api/projects/{id}:clarify)
        app.MapPost("/api/projects/{target}", async (
            string target,
            HttpContext http,
            IProjectManagerAgent pmAgent,
            IFactoryOrchestrationCoordinator coordinator,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var (id, action) = SplitColonAction(target);
            if (!id.HasValue || action is null)
                return Results.NotFound();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id.Value, ct);
            if (project is null)
                return Results.NotFound();

            var actor = http.User.Identity?.Name ?? "admin";

            if (string.Equals(action, "clarify", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadJsonOrDefaultAsync<ClarifyAnswersRequest>(http.Request, ct) ?? new ClarifyAnswersRequest(null);
                if (body.Answers is { Count: > 0 })
                {
                    var notes = string.Join("\n", body.Answers.Select(kv => $"- **{kv.Key}**: {kv.Value}"));
                    project.SpecMarkdown = $"{project.SpecMarkdown.TrimEnd()}\n\n## Clarifications\n{notes}\n";
                    project.ClarificationAnswersJson = JsonSerializer.Serialize(body.Answers, JsonOpts);
                }

                var publicCheck = pmAgent.CheckPublicSafeDeterministic(project.SpecMarkdown);
                if (!publicCheck.Passed && project.IsPublic && !body.DismissPublicSafeFlags)
                {
                    return Results.UnprocessableEntity(new
                    {
                        error = "Public-safe check flagged sensitive tokens, internal URLs, or personal data. Remove them or explicitly dismiss false positives.",
                        publicSafe = publicCheck
                    });
                }

                var plan = await pmAgent.GeneratePlanAsync(project.Name, project.SpecMarkdown, project.TemplateId, ct: ct);
                project.PlanJson = JsonSerializer.Serialize(plan, JsonOpts);
                project.State = ProjectState.Planned;
                project.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                return Results.Ok(new { projectId = project.Id, state = project.State, plan });
            }

            if (string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(project.PlanJson))
                {
                    var autoPlan = await pmAgent.GeneratePlanAsync(project.Name, project.SpecMarkdown, project.TemplateId, ct: ct);
                    project.PlanJson = JsonSerializer.Serialize(autoPlan, JsonOpts);
                }

                // Validate plan before approval (no cycles in planned items)
                var planObj = JsonSerializer.Deserialize<GeneratedPlanOutput>(project.PlanJson, JsonOpts)!;
                var keyToIndex = planObj.Items.Select((item, idx) => (item.Key, Idx: idx + 1)).ToDictionary(x => x.Key, x => x.Idx, StringComparer.OrdinalIgnoreCase);
                var adj = planObj.Items.ToDictionary(
                    it => keyToIndex[it.Key],
                    it => it.BlockedByKeys.Where(keyToIndex.ContainsKey).Select(k => keyToIndex[k]).ToList());
                var cycles = SchedulerEngine.FindCycleNodesTarjan(keyToIndex.Values.ToHashSet(), adj);
                if (cycles.Count > 0)
                {
                    return Results.BadRequest(new { error = "Cannot approve plan containing dependency cycles.", cycleNodes = cycles });
                }

                project.PlanApprovedBy = actor;
                project.PlanApprovedAt = DateTimeOffset.UtcNow;
                project.State = ProjectState.Approved;
                await db.SaveChangesAsync(ct);

                await auditLogger.RecordAsync(actor, "ProjectPlanApproved", "Project", project.Id.ToString(), ct: ct);
                var steps = await coordinator.RunProjectBootstrapAsync(project.Id, actor, ct);
                return Results.Ok(new { projectId = project.Id, state = ProjectState.Running, bootstrapSteps = steps });
            }

            return Results.NotFound();
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapGet("/api/projects/{id:guid}/plan", async (Guid id, IProjectManagerAgent pmAgent, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(p.PlanJson))
            {
                var generated = await pmAgent.GeneratePlanAsync(p.Name, p.SpecMarkdown, p.TemplateId, ct: ct);
                p.PlanJson = JsonSerializer.Serialize(generated, JsonOpts);
                await db.SaveChangesAsync(ct);
                return Results.Ok(generated);
            }

            return Results.Ok(JsonSerializer.Deserialize<GeneratedPlanOutput>(p.PlanJson, JsonOpts));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPut("/api/projects/{id:guid}/plan", async (Guid id, GeneratedPlanOutput updatedPlan, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null)
                return Results.NotFound();

            var recalculated = updatedPlan with
            {
                EstimatedCostUsd = Math.Round(updatedPlan.Items.Count * 0.65m, 2),
                EstimatedDurationMinutes = updatedPlan.Items.Count * 14
            };
            p.PlanJson = JsonSerializer.Serialize(recalculated, JsonOpts);
            p.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(recalculated);
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapGet("/api/projects/{id:guid}/bootstrap", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null)
                return Results.NotFound();

            var steps = string.IsNullOrWhiteSpace(p.BootstrapStepsJson)
                ? []
                : JsonSerializer.Deserialize<List<BootstrapStepRecord>>(p.BootstrapStepsJson, JsonOpts) ?? [];
            return Results.Ok(new { projectId = p.Id, state = p.State, instanceId = p.BootstrapInstanceId, steps });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // §5.5 Feature Specs for existing projects
        app.MapGet("/api/projects/{id:guid}/features", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var features = await db.FeatureSpecs.Where(f => f.ProjectId == id).OrderByDescending(f => f.CreatedAt).ToListAsync(ct);
            var workItems = await db.WorkItems.Where(w => w.FeatureId != null).ToListAsync(ct);
            var requirements = await db.Requirements.Where(r => r.ProjectId == id && r.FeatureId != null).ToListAsync(ct);

            return Results.Ok(features.Select(f =>
            {
                var fItems = workItems.Where(w => w.FeatureId == f.Id).ToList();
                var fReqs = requirements.Where(r => r.FeatureId == f.Id).ToList();
                return new
                {
                    f.Id,
                    f.ProjectId,
                    f.Title,
                    f.Slug,
                    f.Path,
                    f.State,
                    f.SpecVersion,
                    f.EpicIssueNumber,
                    f.Priority,
                    f.Milestone,
                    f.CreatedAt,
                    totalIssues = fItems.Count,
                    doneIssues = fItems.Count(w => w.State == WorkItemStateKind.Merged),
                    requirementCount = fReqs.Count,
                    passingRequirementCount = fReqs.Count(r => r.Status is RequirementCoverageStatus.TestsPassing or RequirementCoverageStatus.Done)
                };
            }));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/api/projects/{id:guid}/features", async (
            Guid id,
            CreateFeatureRequest req,
            IProjectManagerAgent pmAgent,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null)
                return Results.NotFound();

            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.ProjectId == id, ct);
            if (repo is { Paused: true })
                return Results.BadRequest(new { error = "Project repository is currently paused." });

            var analysis = await pmAgent.AnalyzeSpecForIntakeAsync(req.SpecMarkdown, ct: ct);
            var slug = ProjectManagerAgent.Slugify(req.Title);
            if (await db.FeatureSpecs.AnyAsync(f => f.ProjectId == id && f.Slug == slug, ct))
                slug = $"{slug}-{Random.Shared.Next(10, 99)}";

            var feature = new FeatureSpecEntity
            {
                ProjectId = id,
                Title = req.Title.Trim(),
                Slug = slug,
                Path = $"docs/features/{project.CurrentSpecVersion:D3}-{slug}.md",
                SpecMarkdown = req.SpecMarkdown,
                ClarifyingQuestionsJson = JsonSerializer.Serialize(analysis.ClarifyingQuestions, JsonOpts),
                Priority = req.Priority,
                Milestone = req.Milestone,
                CodingModelOverride = req.CodingModelOverride,
                SourceIssueNumbersJson = JsonSerializer.Serialize(req.SourceIssueNumbers ?? [], JsonOpts),
                State = FeatureSpecState.Clarifying
            };

            db.FeatureSpecs.Add(feature);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/features/{feature.Id}/status", new
            {
                feature,
                clarifyingQuestions = analysis.ClarifyingQuestions,
                publicSafe = analysis.PublicSafe
            });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        // POST /api/features/{id}:clarify · :approve · :withdraw
        app.MapPost("/api/features/{target}", async (
            string target,
            HttpContext http,
            IProjectManagerAgent pmAgent,
            IFactoryOrchestrationCoordinator coordinator,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var (id, action) = SplitColonAction(target);
            if (!id.HasValue || action is null)
                return Results.NotFound();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var feature = await db.FeatureSpecs.FirstOrDefaultAsync(f => f.Id == id.Value, ct);
            if (feature is null)
                return Results.NotFound();

            var project = await db.Projects.FirstAsync(p => p.Id == feature.ProjectId, ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.ProjectId == project.Id, ct);
            var actor = http.User.Identity?.Name ?? "admin";

            if (string.Equals(action, "clarify", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadJsonOrDefaultAsync<ClarifyAnswersRequest>(http.Request, ct) ?? new ClarifyAnswersRequest(null);
                if (body.Answers is { Count: > 0 })
                {
                    var notes = string.Join("\n", body.Answers.Select(kv => $"- **{kv.Key}**: {kv.Value}"));
                    feature.SpecMarkdown = $"{feature.SpecMarkdown.TrimEnd()}\n\n## Clarifications\n{notes}\n";
                }

                var publicSafe = pmAgent.CheckPublicSafeDeterministic(feature.SpecMarkdown);
                if (!publicSafe.Passed && project.IsPublic && !body.DismissPublicSafeFlags)
                    return Results.UnprocessableEntity(new { error = "Public-safe check failed.", publicSafe });

                var existingReqCount = await db.Requirements.CountAsync(r => r.ProjectId == project.Id, ct);
                var existingItems = repo is null
                    ? []
                    : await db.WorkItems.Where(w => w.RepoId == repo.Id).ToListAsync(ct);

                var ctx = new ProjectIncrementContext(
                    project.Name,
                    project.SpecMarkdown,
                    existingReqCount + 1,
                    existingItems.Select(w => new BacklogSummaryItem(w.IssueNumber, w.Title, w.BoardStatus, w.Priority, [])).ToList(),
                    existingItems.Where(w => w.BoardStatus is BoardStatus.InProgress or BoardStatus.InReview).Select(w => w.IssueNumber).ToList(),
                    ["src/", "web/", "tests/acceptance/"]);

                var increment = await pmAgent.PlanIncrementAsync(feature.Title, feature.SpecMarkdown, ctx, ct: ct);
                feature.PlanIncrementJson = JsonSerializer.Serialize(increment, JsonOpts);
                feature.State = FeatureSpecState.Planned;
                feature.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { featureId = feature.Id, state = feature.State, increment });
            }

            if (string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(feature.PlanIncrementJson))
                {
                    var existingReqCount = await db.Requirements.CountAsync(r => r.ProjectId == project.Id, ct);
                    var inc = await pmAgent.PlanIncrementAsync(feature.Title, feature.SpecMarkdown,
                        new ProjectIncrementContext(project.Name, project.SpecMarkdown, existingReqCount + 1, [], [], []), ct: ct);
                    feature.PlanIncrementJson = JsonSerializer.Serialize(inc, JsonOpts);
                }

                feature.State = FeatureSpecState.Approved;
                await db.SaveChangesAsync(ct);

                var steps = await coordinator.ApplyFeatureIncrementAsync(feature.Id, actor, ct);
                return Results.Ok(new { featureId = feature.Id, state = FeatureSpecState.Applied, steps });
            }

            if (string.Equals(action, "withdraw", StringComparison.OrdinalIgnoreCase))
            {
                await coordinator.WithdrawFeatureAsync(feature.Id, actor, ct);
                return Results.Ok(new { featureId = feature.Id, state = FeatureSpecState.Withdrawn });
            }

            return Results.NotFound();
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapGet("/api/features/{id:guid}/plan", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var f = await db.FeatureSpecs.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null)
                return Results.NotFound();

            return string.IsNullOrWhiteSpace(f.PlanIncrementJson)
                ? Results.Ok(new { items = Array.Empty<object>() })
                : Results.Ok(JsonSerializer.Deserialize<PlanIncrementOutput>(f.PlanIncrementJson, JsonOpts));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPut("/api/features/{id:guid}/plan", async (Guid id, PlanIncrementOutput updated, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var f = await db.FeatureSpecs.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null)
                return Results.NotFound();

            f.PlanIncrementJson = JsonSerializer.Serialize(updated, JsonOpts);
            f.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(updated);
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapGet("/api/features/{id:guid}/status", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var f = await db.FeatureSpecs.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null)
                return Results.NotFound();

            var steps = string.IsNullOrWhiteSpace(f.ApplyStepsJson)
                ? []
                : JsonSerializer.Deserialize<List<BootstrapStepRecord>>(f.ApplyStepsJson, JsonOpts) ?? [];
            return Results.Ok(new { f.Id, f.Title, f.Slug, f.State, f.EpicIssueNumber, f.SpecVersion, steps });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        // §5.6 Issue Inbox & Create Feature from Issues
        app.MapGet("/api/projects/{id:guid}/inbox", async (
            Guid id,
            IProjectManagerAgent pmAgent,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var items = await db.InboxItems
                .Where(i => i.ProjectId == id && i.State != InboxItemState.Dismissed)
                .OrderByDescending(i => i.ReactionsCount)
                .ThenByDescending(i => i.CreatedAt)
                .ToListAsync(ct);

            var ungrouped = items.Where(i => string.IsNullOrWhiteSpace(i.SuggestedGroup)).ToList();
            if (ungrouped.Count > 0)
            {
                var grouped = await pmAgent.GroupInboxAsync(
                    ungrouped.Select(i => new UntrustedInboxIssue(i.IssueNumber, i.Author, i.Title, i.Body)).ToList(), ct: ct);

                foreach (var s in grouped.Suggestions)
                {
                    var match = ungrouped.FirstOrDefault(u => u.IssueNumber == s.IssueNumber);
                    if (match is not null)
                    {
                        match.SuggestedGroup = s.SuggestedGroup;
                        match.DuplicateOfIssueNumber = s.LikelyDuplicateOf;
                    }
                }
                await db.SaveChangesAsync(ct);
            }

            var features = await db.FeatureSpecs.Where(f => f.ProjectId == id).ToDictionaryAsync(f => f.Id, ct);

            return Results.Ok(items.Select(i =>
            {
                var feat = i.FeatureId.HasValue && features.TryGetValue(i.FeatureId.Value, out var f) ? f : null;
                return new
                {
                    i.Id,
                    i.IssueNumber,
                    i.Title,
                    i.Body,
                    i.Author,
                    i.ReactionsCount,
                    labels = JsonSerializer.Deserialize<List<string>>(i.LabelsJson, JsonOpts) ?? [],
                    i.OwnerAttemptedDirectWorkLabel,
                    i.State,
                    i.SuggestedGroup,
                    i.DuplicateOfIssueNumber,
                    i.HtmlUrl,
                    i.CreatedAt,
                    linkedFeature = feat is null ? null : new { feat.Id, feat.Title, feat.Slug, feat.State }
                };
            }));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/api/inbox/{target}", async (
            string target,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var (id, action) = SplitColonAction(target);
            if (!id.HasValue || !string.Equals(action, "dismiss", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var item = await db.InboxItems.FirstOrDefaultAsync(i => i.Id == id.Value, ct);
            if (item is null)
                return Results.NotFound();

            item.State = InboxItemState.Dismissed;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { item.Id, item.State });
        }).RequireAuthorization(FactoryRoles.OperatorPolicy);

        app.MapPost("/api/projects/{id:guid}/features:from-issues", async (
            Guid id,
            FeaturesFromIssuesRequest req,
            IProjectManagerAgent pmAgent,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null)
                return Results.NotFound();

            var sourceItems = await db.InboxItems
                .Where(i => i.ProjectId == id && req.IssueNumbers.Contains(i.IssueNumber))
                .ToListAsync(ct);

            if (sourceItems.Count == 0)
                return Results.BadRequest(new { error = "Select at least one inbox issue." });

            // §5.6 Step 2: Untrusted issue text passed strictly as quoted data
            var drafted = await pmAgent.DraftFeatureFromIssuesAsync(
                sourceItems.Select(i => new UntrustedInboxIssue(i.IssueNumber, i.Author, i.Title, i.Body)).ToList(),
                ct: ct);

            var slug = drafted.Slug;
            if (await db.FeatureSpecs.AnyAsync(f => f.ProjectId == id && f.Slug == slug, ct))
                slug = $"{slug}-{Random.Shared.Next(10, 99)}";

            var feature = new FeatureSpecEntity
            {
                ProjectId = id,
                Title = drafted.Title,
                Slug = slug,
                Path = $"docs/features/{project.CurrentSpecVersion:D3}-{slug}.md",
                SpecMarkdown = drafted.FeatureSpecMarkdown,
                SourceIssueNumbersJson = JsonSerializer.Serialize(drafted.SourceIssueNumbers, JsonOpts),
                State = FeatureSpecState.Draft
            };

            db.FeatureSpecs.Add(feature);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/features/{feature.Id}/status", feature);
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        // §6.3 Living Spec, Requirements & Change Proposals
        app.MapGet("/api/projects/{id:guid}/spec", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null)
                return Results.NotFound();

            var versions = await db.SpecVersions.Where(v => v.ProjectId == id).OrderByDescending(v => v.Version).ToListAsync(ct);
            return Results.Ok(new { project.Id, project.CurrentSpecVersion, project.SpecMarkdown, versions });
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPut("/api/projects/{id:guid}/spec", async (
            Guid id,
            UpdateSpecRequest req,
            HttpContext http,
            IProjectManagerAgent pmAgent,
            IGitHubGateway gitHub,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null)
                return Results.NotFound();

            var publicSafe = pmAgent.CheckPublicSafeDeterministic(req.SpecMarkdown);
            if (!publicSafe.Passed && project.IsPublic && !req.DismissPublicSafeFlags)
            {
                return Results.UnprocessableEntity(new
                {
                    error = "Public-safe check flagged sensitive content in spec edit.",
                    publicSafe
                });
            }

            var oldSpec = project.SpecMarkdown;
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.ProjectId == id, ct);
            var backlog = repo is null
                ? []
                : await db.WorkItems.Where(w => w.RepoId == repo.Id)
                    .Select(w => new BacklogSummaryItem(w.IssueNumber, w.Title, w.BoardStatus, w.Priority, Array.Empty<string>()))
                    .ToListAsync(ct);

            var delta = await pmAgent.ProposeChangesAsync(oldSpec, req.SpecMarkdown, backlog, ct: ct);
            var nextVersion = project.CurrentSpecVersion + 1;

            if (repo is not null)
            {
                await gitHub.OpenDocsPullRequestAsync(
                    repo.Owner,
                    repo.Name,
                    $"spec/v{nextVersion}",
                    $"docs(spec): update specification to spec-v{nextVersion}",
                    new Dictionary<string, string> { ["docs/spec.md"] = req.SpecMarkdown },
                    ct);
            }

            project.CurrentSpecVersion = nextVersion;
            project.SpecMarkdown = req.SpecMarkdown;
            project.UpdatedAt = DateTimeOffset.UtcNow;

            db.SpecVersions.Add(new SpecVersionEntity
            {
                ProjectId = id,
                Version = nextVersion,
                GitSha = Guid.NewGuid().ToString("N")[..12],
                Tag = $"spec-v{nextVersion}",
                SpecMarkdown = req.SpecMarkdown,
                PublicSafeResultJson = JsonSerializer.Serialize(publicSafe, JsonOpts)
            });

            var proposal = new ChangeProposalEntity
            {
                ProjectId = id,
                Kind = ChangeProposalKind.SpecEdit,
                FromSpecVersion = nextVersion - 1,
                ToSpecVersion = nextVersion,
                Summary = delta.Summary,
                ItemsJson = JsonSerializer.Serialize(delta.Items, JsonOpts),
                State = ChangeProposalState.PendingApproval
            };
            db.ChangeProposals.Add(proposal);
            await db.SaveChangesAsync(ct);

            await auditLogger.RecordAsync(http.User.Identity?.Name ?? "admin", "SpecEdited", "Project", id.ToString(), after: new { version = nextVersion, proposalId = proposal.Id }, ct: ct);
            return Results.Ok(new { specVersion = nextVersion, changeProposal = proposal, delta });
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapGet("/api/projects/{id:guid}/requirements", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var reqs = await db.Requirements.Where(r => r.ProjectId == id).OrderBy(r => r.Code).ToListAsync(ct);
            return Results.Ok(reqs.Select(r => new
            {
                r.Id,
                r.Code,
                r.FeatureId,
                r.Text,
                r.IntroducedInVersion,
                r.Status,
                linkedIssues = JsonSerializer.Deserialize<List<int>>(r.LinkedIssueNumbersJson, JsonOpts) ?? [],
                linkedAcceptanceTests = JsonSerializer.Deserialize<List<string>>(r.LinkedAcceptanceTestsJson, JsonOpts) ?? []
            }));
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapGet("/api/projects/{id:guid}/change-proposals", async (Guid id, IDbContextFactory<DemoFactoryDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var proposals = await db.ChangeProposals.Where(c => c.ProjectId == id).OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
            return Results.Ok(proposals);
        }).RequireAuthorization(FactoryRoles.ViewerPolicy);

        app.MapPost("/api/projects/{id:guid}/change-proposals", async (
            Guid id,
            UpdateSpecRequest req,
            IProjectManagerAgent pmAgent,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null)
                return Results.NotFound();

            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.ProjectId == id, ct);
            var backlog = repo is null
                ? []
                : await db.WorkItems.Where(w => w.RepoId == repo.Id)
                    .Select(w => new BacklogSummaryItem(w.IssueNumber, w.Title, w.BoardStatus, w.Priority, Array.Empty<string>()))
                    .ToListAsync(ct);

            var delta = await pmAgent.ProposeChangesAsync(project.SpecMarkdown, req.SpecMarkdown, backlog, ct: ct);
            var proposal = new ChangeProposalEntity
            {
                ProjectId = id,
                Kind = ChangeProposalKind.SpecEdit,
                FromSpecVersion = project.CurrentSpecVersion,
                ToSpecVersion = project.CurrentSpecVersion + 1,
                Summary = delta.Summary,
                ItemsJson = JsonSerializer.Serialize(delta.Items, JsonOpts),
                State = ChangeProposalState.PendingApproval
            };
            db.ChangeProposals.Add(proposal);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/projects/{id}/change-proposals", proposal);
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        app.MapPost("/api/change-proposals/{target}", async (
            string target,
            HttpContext http,
            IGitHubGateway gitHub,
            IDbContextFactory<DemoFactoryDbContext> dbFactory,
            IAuditLogger auditLogger,
            IOptions<FactoryEnvironmentOptions> envOptions,
            CancellationToken ct) =>
        {
            var (id, action) = SplitColonAction(target);
            if (!id.HasValue || !string.Equals(action, "apply", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var proposal = await db.ChangeProposals.FirstOrDefaultAsync(c => c.Id == id.Value, ct);
            if (proposal is null)
                return Results.NotFound();

            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.ProjectId == proposal.ProjectId, ct);
            var items = JsonSerializer.Deserialize<List<ProposedChangeItem>>(proposal.ItemsJson, JsonOpts) ?? [];
            var actor = http.User.Identity?.Name ?? envOptions.Value.OwnerLogin;

            if (repo is not null)
            {
                foreach (var item in items)
                {
                    if (item.Action is "add" or "change-request")
                    {
                        var created = await gitHub.CreateIssueAsync(repo.Owner, repo.Name, item.Title, item.Details, ["factory"], ct);
                        db.WorkItems.Add(new WorkItemEntity
                        {
                            RepoId = repo.Id,
                            IssueNumber = created.Number,
                            IssueNodeId = created.NodeId,
                            ProjectItemId = $"PVTI_{created.Number}",
                            Title = item.Title,
                            BodyMarkdown = item.Details,
                            AcceptanceCriteriaJson = JsonSerializer.Serialize(new[] { item.Details }, JsonOpts),
                            RequirementIdsJson = JsonSerializer.Serialize(item.RequirementIds, JsonOpts),
                            IssueAuthor = envOptions.Value.OwnerLogin,
                            WorkLabelAppliedBy = envOptions.Value.OwnerLogin,
                            PassesWorkLabelRule = true,
                            BoardStatus = BoardStatus.Ready,
                            State = WorkItemStateKind.Queued,
                            SpecVersionPlanned = proposal.ToSpecVersion
                        });
                    }
                    else if (item.Action == "close" && item.TargetIssueNumber.HasValue)
                    {
                        var targetWi = await db.WorkItems.FirstOrDefaultAsync(w => w.RepoId == repo.Id && w.IssueNumber == item.TargetIssueNumber.Value, ct);
                        if (targetWi is not null && targetWi.State != WorkItemStateKind.Merged)
                        {
                            targetWi.State = WorkItemStateKind.Cancelled;
                            targetWi.BoardStatus = BoardStatus.Backlog;
                        }
                    }
                    else if (item.Action == "update" && item.TargetIssueNumber.HasValue)
                    {
                        var targetWi = await db.WorkItems.FirstOrDefaultAsync(w => w.RepoId == repo.Id && w.IssueNumber == item.TargetIssueNumber.Value, ct);
                        if (targetWi is not null && targetWi.State == WorkItemStateKind.Queued)
                        {
                            targetWi.BodyMarkdown = $"{targetWi.BodyMarkdown}\n\nUpdated for spec-v{proposal.ToSpecVersion}: {item.Details}";
                            targetWi.SpecVersionPlanned = proposal.ToSpecVersion;
                        }
                    }
                }
            }

            proposal.State = ChangeProposalState.Applied;
            proposal.AppliedBy = actor;
            proposal.AppliedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await auditLogger.RecordAsync(actor, "ChangeProposalApplied", "ChangeProposal", proposal.Id.ToString(), after: new { count = items.Count }, ct: ct);
            return Results.Ok(proposal);
        }).RequireAuthorization(FactoryRoles.AdminPolicy);

        return app;
    }

    public static (Guid? Id, string? Action) SplitColonAction(string segment)
    {
        var idx = segment.IndexOf(':');
        if (idx <= 0 || idx >= segment.Length - 1)
            return (null, null);

        return Guid.TryParse(segment[..idx], out var guid)
            ? (guid, segment[(idx + 1)..])
            : (null, null);
    }

    public static async Task<T?> ReadJsonOrDefaultAsync<T>(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or 0)
            return default;
        try
        {
            return await request.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch
        {
            return default;
        }
    }
}
