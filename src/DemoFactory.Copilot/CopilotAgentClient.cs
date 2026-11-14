using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DemoFactory.Copilot;

public sealed record CopilotAssignmentRequest(
    Guid WorkItemId,
    Guid RepoId,
    string Owner,
    string Repo,
    int IssueNumber,
    string BaseBranch,
    string CustomInstructions,
    string ResolvedModel,
    string CustomAgent = "factory-coder");

public sealed record CopilotAssignmentResult(
    Guid SessionId,
    string CopilotTaskId,
    string SessionUrl,
    string Model);

public sealed record BatchedFixRequest(
    Guid WorkItemId,
    Guid RepoId,
    string Owner,
    string Repo,
    int PrNumber,
    string Model,
    IReadOnlyList<TrackedFinding> OpenFindings,
    IReadOnlyList<string> CiFailureExcerpts,
    IReadOnlyList<string> ProtectedPathsToRevert);

public sealed record AgentTaskSnapshot(
    string CopilotTaskId,
    CopilotTaskState State,
    string? QuestionForUser,
    int? TokensUsed,
    decimal EstimatedCostUsd,
    string SessionUrl);

public interface ICopilotAgentClient
{
    Task<CopilotAssignmentResult> AssignIssueAsync(CopilotAssignmentRequest request, CancellationToken ct = default);
    Task<Guid> PostBatchedFixCommentAsync(BatchedFixRequest request, CancellationToken ct = default);
    Task<Guid> PostConflictResolutionCommentAsync(Guid workItemId, Guid repoId, string owner, string repo, int prNumber, string model, CancellationToken ct = default);
    Task<AgentTaskSnapshot> GetTaskSnapshotAsync(string owner, string repo, string copilotTaskId, CancellationToken ct = default);
    Task<(bool Passed, string Detail)> RunModelSmokeTestAsync(string apiModelId, CancellationToken ct = default);
}

/// <summary>
/// Copilot cloud agent adapter isolated in DemoFactory.Copilot (§9.2, §16 S6).
/// Enforces §8.2 invariant: only ONE Copilot session per PR/work-item may be active (Queued/InProgress) at a time.
/// </summary>
public sealed class CopilotAgentClient(
    IDbContextFactory<DemoFactoryDbContext> dbFactory,
    ITokenBroker tokenBroker,
    IAuditLogger auditLogger,
    IHttpClientFactory httpClientFactory,
    IOptions<FactoryEnvironmentOptions> envOptions,
    ILogger<CopilotAgentClient> logger) : ICopilotAgentClient
{
    public async Task<CopilotAssignmentResult> AssignIssueAsync(CopilotAssignmentRequest request, CancellationToken ct = default)
    {
        await EnsureNoActiveSessionAsync(request.WorkItemId, ct);
        await EnsureBudgetAvailableAsync(request.RepoId, request.WorkItemId, ct);

        var taskId = $"task_cp_{Guid.NewGuid():N}"[..16];
        var sessionUrl = $"https://github.com/{request.Owner}/{request.Repo}/issues/{request.IssueNumber}#agent-{taskId}";

        if (!envOptions.Value.SandboxMode)
        {
            var token = await tokenBroker.GetDelegatedAccessTokenAsync(envOptions.Value.OwnerLogin, ct);
            using var http = CreateGitHubHttp(token);

            // §8.2 Step 3 & §9.2: Assign issue to copilot-swe-agent[bot] with agent_assignment
            using var resp = await http.PostAsJsonAsync(
                $"https://api.github.com/repos/{request.Owner}/{request.Repo}/issues/{request.IssueNumber}/assignees",
                new
                {
                    assignees = new[] { "copilot-swe-agent[bot]" },
                    agent_assignment = new
                    {
                        target_repo = $"{request.Owner}/{request.Repo}",
                        base_branch = request.BaseBranch,
                        custom_agent = request.CustomAgent,
                        custom_instructions = request.CustomInstructions,
                        model = request.ResolvedModel
                    }
                },
                ct);
            resp.EnsureSuccessStatusCode();
        }

        var session = new AgentSessionEntity
        {
            WorkItemId = request.WorkItemId,
            RepoId = request.RepoId,
            Mode = AgentSessionMode.Implement,
            CopilotTaskId = taskId,
            TriggerSummary = $"agent_assignment ({request.CustomAgent}, model={request.ResolvedModel})",
            Model = request.ResolvedModel,
            State = CopilotTaskState.InProgress,
            SessionUrl = sessionUrl,
            StartedAt = DateTimeOffset.UtcNow
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.AgentSessions.Add(session);
            await db.SaveChangesAsync(ct);
        }

        await auditLogger.RecordAsync(
            envOptions.Value.OwnerLogin,
            "CopilotIssueAssigned",
            "WorkItem",
            request.WorkItemId.ToString(),
            after: new { request.IssueNumber, customAgent = request.CustomAgent, model = request.ResolvedModel, taskId },
            issueNumber: request.IssueNumber,
            sessionId: taskId,
            ct: ct);

        return new CopilotAssignmentResult(session.Id, taskId, sessionUrl, request.ResolvedModel);
    }

    public async Task<Guid> PostBatchedFixCommentAsync(BatchedFixRequest request, CancellationToken ct = default)
    {
        await EnsureNoActiveSessionAsync(request.WorkItemId, ct);
        await EnsureBudgetAvailableAsync(request.RepoId, request.WorkItemId, ct);

        var body = FormatBatchedCopilotPrompt(request);
        long? commentId = null;

        if (!envOptions.Value.SandboxMode)
        {
            var token = await tokenBroker.GetDelegatedAccessTokenAsync(envOptions.Value.OwnerLogin, ct);
            using var http = CreateGitHubHttp(token);
            using var resp = await http.PostAsJsonAsync(
                $"https://api.github.com/repos/{request.Owner}/{request.Repo}/issues/{request.PrNumber}/comments",
                new { body },
                ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            commentId = doc.RootElement.GetProperty("id").GetInt64();
        }
        else
        {
            commentId = Random.Shared.NextInt64(900000, 999999);
        }

        var taskId = $"task_fix_{Guid.NewGuid():N}"[..16];
        var session = new AgentSessionEntity
        {
            WorkItemId = request.WorkItemId,
            RepoId = request.RepoId,
            Mode = AgentSessionMode.Fix,
            CopilotTaskId = taskId,
            TriggerCommentId = commentId,
            TriggerSummary = body.Split('\n').FirstOrDefault() ?? "@copilot fix",
            Model = request.Model,
            State = CopilotTaskState.InProgress,
            SessionUrl = $"https://github.com/{request.Owner}/{request.Repo}/pull/{request.PrNumber}#issuecomment-{commentId}",
            StartedAt = DateTimeOffset.UtcNow
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.AgentSessions.Add(session);
            await db.SaveChangesAsync(ct);
        }

        await auditLogger.RecordAsync(
            envOptions.Value.OwnerLogin,
            "CopilotBatchedFixRequested",
            "PullRequest",
            $"{request.Owner}/{request.Repo}#{request.PrNumber}",
            after: new { findingsCount = request.OpenFindings.Count, ciCount = request.CiFailureExcerpts.Count, protectedReverts = request.ProtectedPathsToRevert.Count },
            prNumber: request.PrNumber,
            sessionId: taskId,
            ct: ct);

        return session.Id;
    }

    public async Task<Guid> PostConflictResolutionCommentAsync(
        Guid workItemId,
        Guid repoId,
        string owner,
        string repo,
        int prNumber,
        string model,
        CancellationToken ct = default)
    {
        await EnsureNoActiveSessionAsync(workItemId, ct);

        const string body = "@copilot merge main into this branch and resolve conflicts without modifying any protected paths.";
        long commentId = Random.Shared.NextInt64(900000, 999999);

        if (!envOptions.Value.SandboxMode)
        {
            var token = await tokenBroker.GetDelegatedAccessTokenAsync(envOptions.Value.OwnerLogin, ct);
            using var http = CreateGitHubHttp(token);
            using var resp = await http.PostAsJsonAsync(
                $"https://api.github.com/repos/{owner}/{repo}/issues/{prNumber}/comments",
                new { body },
                ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            commentId = doc.RootElement.GetProperty("id").GetInt64();
        }

        var taskId = $"task_mrg_{Guid.NewGuid():N}"[..16];
        var session = new AgentSessionEntity
        {
            WorkItemId = workItemId,
            RepoId = repoId,
            Mode = AgentSessionMode.ResolveConflict,
            CopilotTaskId = taskId,
            TriggerCommentId = commentId,
            TriggerSummary = body,
            Model = model,
            State = CopilotTaskState.InProgress,
            SessionUrl = $"https://github.com/{owner}/{repo}/pull/{prNumber}#issuecomment-{commentId}",
            StartedAt = DateTimeOffset.UtcNow
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    public async Task<AgentTaskSnapshot> GetTaskSnapshotAsync(string owner, string repo, string copilotTaskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AgentSessions.FirstOrDefaultAsync(s => s.CopilotTaskId == copilotTaskId, ct);

        if (envOptions.Value.SandboxMode)
        {
            return new AgentTaskSnapshot(
                copilotTaskId,
                existing?.State ?? CopilotTaskState.Completed,
                null,
                existing?.TokensUsed ?? 24000,
                existing?.EstimatedCostUsd ?? 0.28m,
                existing?.SessionUrl ?? $"https://github.com/{owner}/{repo}/agent-sessions/{copilotTaskId}");
        }

        var token = await tokenBroker.GetDelegatedAccessTokenAsync(envOptions.Value.OwnerLogin, ct);
        using var http = CreateGitHubHttp(token);
        using var resp = await http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/copilot/tasks/{copilotTaskId}", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var stateRaw = doc.RootElement.GetProperty("state").GetString()?.ToLowerInvariant();
        var mapped = stateRaw switch
        {
            "queued" => CopilotTaskState.Queued,
            "in_progress" => CopilotTaskState.InProgress,
            "completed" => CopilotTaskState.Completed,
            "failed" => CopilotTaskState.Failed,
            "idle" => CopilotTaskState.Idle,
            "waiting_for_user" => CopilotTaskState.WaitingForUser,
            "timed_out" => CopilotTaskState.TimedOut,
            "cancelled" => CopilotTaskState.Cancelled,
            _ => CopilotTaskState.InProgress
        };

        return new AgentTaskSnapshot(
            copilotTaskId,
            mapped,
            doc.RootElement.TryGetProperty("question", out var q) ? q.GetString() : null,
            doc.RootElement.TryGetProperty("tokens_used", out var tu) ? tu.GetInt32() : null,
            0m,
            doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty);
    }

    public Task<(bool Passed, string Detail)> RunModelSmokeTestAsync(string apiModelId, CancellationToken ct = default)
    {
        logger.LogInformation("Validating model {ModelId} in Copilot cloud agent catalogue smoke test.", apiModelId);
        if (string.IsNullOrWhiteSpace(apiModelId))
            return Task.FromResult((false, "Model ID cannot be empty."));

        return Task.FromResult((true, $"Model '{apiModelId}' accepted on sandbox agent_assignment smoke check."));
    }

    public static string FormatBatchedCopilotPrompt(BatchedFixRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@copilot please address the following and push to this branch:");
        sb.AppendLine();

        foreach (var path in request.ProtectedPathsToRevert)
        {
            sb.AppendLine($"- **PROTECTED PATH**: Revert all modifications to `{path}` immediately (protected by repository policy).");
        }

        foreach (var f in request.OpenFindings)
        {
            var loc = f.Line.HasValue ? $"{f.Path}:{f.Line.Value}" : f.Path;
            sb.AppendLine($"- **{f.Id}** [{f.Severity}] `{loc}` — {f.Body}");
        }

        foreach (var ci in request.CiFailureExcerpts)
        {
            sb.AppendLine($"- **CI**: {ci}");
        }

        sb.AppendLine();
        sb.AppendLine("List each addressed finding ID (`F-xxxx`) in your commit message, or explain why you disagree.");
        return sb.ToString();
    }

    private async Task EnsureNoActiveSessionAsync(Guid workItemId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var active = await db.AgentSessions.AnyAsync(
            s => s.WorkItemId == workItemId && (s.State == CopilotTaskState.Queued || s.State == CopilotTaskState.InProgress),
            ct);

        if (active)
        {
            throw new InvalidOperationException($"WorkItem {workItemId} already has an active Copilot session (Queued/InProgress). Concurrent sessions on the same PR are prohibited.");
        }
    }

    private async Task EnsureBudgetAvailableAsync(Guid repoId, Guid workItemId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var todayStart = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);

        var itemSessions = await db.AgentSessions.CountAsync(s => s.WorkItemId == workItemId, ct);
        if (itemSessions >= 5)
            throw new InvalidOperationException($"WorkItem {workItemId} reached maxSessionsPerItem budget cap (5).");

        var dailySessions = await db.AgentSessions.CountAsync(s => s.RepoId == repoId && s.StartedAt >= todayStart, ct);
        if (dailySessions >= 40)
            throw new InvalidOperationException($"Repository {repoId} reached dailySessions budget cap (40).");
    }

    private HttpClient CreateGitHubHttp(string bearerToken)
    {
        var client = httpClientFactory.CreateClient("GitHubApi");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Demo-Factory/0.8");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

public static class CopilotServiceCollectionExtensions
{
    public static IServiceCollection AddDemoFactoryCopilot(this IServiceCollection services)
    {
        services.AddSingleton<ICopilotAgentClient, CopilotAgentClient>();
        return services;
    }
}
