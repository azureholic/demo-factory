using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DemoFactory.Orchestrator;

public sealed partial class FactoryOrchestrationCoordinator
{
    private sealed record WatchdogRecovery(string Kind, long? RunId);

    private async Task<bool> WatchWorkItemProgressAsync(
        DemoFactoryDbContext db, RepositoryEntity repo, WorkItemEntity wi, RepoPolicy policy,
        AutonomyLevel autonomy, LineStatus lineStatus, LivePullRequestSnapshot? pr,
        LivePullRequestGateSignals? signals, CancellationToken ct)
    {
        var settings = policy.PrWatchdog;
        if (!settings.Enabled || lineStatus == LineStatus.LineStopped || !wi.PassesWorkLabelRule)
            return false;

        var label = TrustBoundary.EvaluateWorkLabelRule(new WorkLabelContext(
            wi.IssueAuthor, SafeDeserializeList<string>(wi.LabelsJson), wi.WorkLabelAppliedBy,
            policy.WorkLabel, envOptions.Value.OwnerLogin, envOptions.Value.FactoryAppSlug));
        if (!label.Passed)
            return false;

        var now = DateTimeOffset.UtcNow;
        var headSha = signals?.HeadSha ?? pr?.HeadSha ?? wi.HeadSha ?? string.Empty;
        var signature = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            wi.State,
            Head = headSha,
            pr?.IsDraft,
            PrState = pr?.State,
            pr?.CopilotWorkInProgress,
            pr?.LastCopilotFinishedAt,
            signals?.MergeableState,
            Checks = signals?.CheckRuns
                .Where(c => !string.Equals(c.Name, ReviewGateEvaluator.GateCheckName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Status, StringComparer.Ordinal)
                .ThenBy(c => c.Conclusion, StringComparer.Ordinal)
                .ToList(),
            signals?.HasCopilotReviewOnHeadSha,
            Reviews = signals?.ReviewComments
        }, JsonOpts)));
        var entityId = wi.Id.ToString();
        var observation = await db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityId == entityId && a.Action == "PrProgressObserved")
            .OrderByDescending(a => a.Timestamp).FirstOrDefaultAsync(ct);

        var serializedSignature = JsonSerializer.Serialize(signature);
        var progressChanged = observation?.AfterJson != serializedSignature;
        if (progressChanged)
        {
            db.AuditEvents.Add(new AuditEventEntity
            {
                Actor = "factory-watchdog", Action = "PrProgressObserved", EntityType = "WorkItem",
                EntityId = entityId, AfterJson = serializedSignature, Sha = headSha,
                IssueNumber = wi.IssueNumber, PrNumber = pr?.Number, Timestamp = now
            });
            await db.SaveChangesAsync(ct);
        }

        var recoveries = await db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityId == entityId && a.Action == "PrWatchdogRecovery" && a.Sha == headSha)
            .OrderByDescending(a => a.Timestamp).ToListAsync(ct);
        var lastAction = recoveries.FirstOrDefault();
        var lastRecovery = lastAction?.AfterJson is { } json
            ? JsonSerializer.Deserialize<WatchdogRecovery>(json, JsonOpts)
                ?? throw new InvalidOperationException("Invalid persisted PR watchdog recovery.")
            : null;
        var awaitingRestart = lastRecovery?.Kind == "CancelWorkflow";
        var timeoutMinutes = pr?.CopilotWorkInProgress == true
            || wi.State is WorkItemStateKind.Claimed or WorkItemStateKind.Implementing or WorkItemStateKind.Fixing
                ? Math.Max(1, settings.AgentNoProgressMinutes)
                : Math.Max(1, settings.NoProgressMinutes);
        var stalled = !progressChanged && observation is not null
            && now - observation.Timestamp >= TimeSpan.FromMinutes(timeoutMinutes);
        var cooldownExpired = lastAction is null
            || now - lastAction.Timestamp >= TimeSpan.FromMinutes(Math.Max(1, settings.RetryCooldownMinutes));
        if (!cooldownExpired)
            return awaitingRestart;
        if (!stalled && !awaitingRestart)
            return false;
        if (!stalled && pr?.CopilotWorkInProgress == true)
            return false;

        if (wi.State == WorkItemStateKind.AwaitingApproval || autonomy == AutonomyLevel.L0
            || string.Equals(repo.FullName, "azureholic/demo-factory", StringComparison.OrdinalIgnoreCase))
        {
            var note = wi.State == WorkItemStateKind.AwaitingApproval
                ? "Waiting for human approval. The watchdog will not approve or bypass this requirement."
                : "No progress detected; automatic recovery is disabled for this repository/autonomy level.";
            if (await ReserveWatchdogEventAsync(db, wi, headSha, $"attention:{signature}", "PrWatchdogAttention", note, ct))
            {
                await gitHub.PostCommentAsync(repo.Owner, repo.Name, pr?.Number ?? wi.IssueNumber, $"### PR Watchdog\n\n{note}", ct);
                return true;
            }
            return false;
        }

        if (recoveries.Count >= Math.Max(0, settings.MaxRecoveryActionsPerHead))
        {
            await MarkBlockedActivityAsync(wi.Id, $"PR watchdog: no progress after {recoveries.Count} automatic recovery actions on head {headSha}. Human investigation required.", ct);
            return true;
        }

        string? kind = null;
        long? runId = null;
        string? escalation = null;
        if (pr is null || pr.State != "open")
            escalation = pr is null ? "Copilot did not produce an open PR within the progress timeout." : "The linked PR was closed without merging.";
        else if (pr.CopilotWorkInProgress || wi.State == WorkItemStateKind.Fixing)
            escalation = "Copilot has not made observable progress within the agent timeout; no duplicate agent session was started.";
        else if (string.Equals(signals?.MergeableState, "dirty", StringComparison.OrdinalIgnoreCase))
            kind = "ResolveConflict";
        else if (string.Equals(signals?.MergeableState, "behind", StringComparison.OrdinalIgnoreCase))
            kind = "UpdateBranch";
        else if (signals is not null)
        {
            var pendingChecks = policy.Gate.RequiredChecks.Where(name => !signals.CheckRuns.Any(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Sha, headSha, StringComparison.OrdinalIgnoreCase)
                && c.Status == "completed" && c.Conclusion == "success"
                && c.AppSlug == ReviewGateEvaluator.ExpectedActionsAppSlug)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (pendingChecks.Count > 0 || awaitingRestart)
            {
                IReadOnlyList<PullRequestWorkflowRun> runs;
                try
                {
                    runs = await gitHub.GetPullRequestWorkflowRunsAsync(repo.Owner, repo.Name, pr.Number, headSha, ct);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogError(ex, "Cannot inspect workflow runs for PR watchdog on {Repository} PR {PrNumber}", repo.FullName, pr.Number);
                    await MarkBlockedActivityAsync(wi.Id, $"PR watchdog: workflow inspection failed: {ex.Message}. Check Actions permissions/configuration.", ct);
                    return true;
                }
                var run = awaitingRestart
                    ? runs.FirstOrDefault(r => r.Id == lastRecovery!.RunId)
                    : runs.OrderByDescending(r => r.Id).FirstOrDefault(r => r.JobNames.Any(pendingChecks.Contains));
                if (run is null)
                    escalation = $"Required checks have not progressed ({string.Join(", ", pendingChecks)}); no safely identifiable PR workflow run can be retried. Check workflow triggers, job names, and Actions permissions.";
                else if (run.Status is "queued" or "in_progress")
                {
                    if (awaitingRestart)
                    {
                        if (!stalled)
                            return false;
                        escalation = $"Workflow run {run.Id} did not finish cancelling; human investigation required.";
                    }
                    else
                    {
                        kind = "CancelWorkflow";
                        runId = run.Id;
                    }
                }
                else if (run.Status == "completed" && run.Conclusion is "cancelled" or "timed_out" or "startup_failure" or "failure")
                {
                    kind = "RerunWorkflow";
                    runId = run.Id;
                }
                else
                    escalation = $"Workflow run {run.Id} cannot satisfy the required checks; verify required check names and workflow conditions.";
            }
            else if (!signals.HasCopilotReviewOnHeadSha)
                kind = "RequestReview";
            else
                escalation = $"PR has not progressed despite completed checks/review (merge state: {signals.MergeableState ?? "unknown"}). Check branch protection and required approvals.";
        }
        else
            escalation = "PR has not left draft/implementation state within the progress timeout.";

        if (escalation is not null)
        {
            await MarkBlockedActivityAsync(wi.Id, $"PR watchdog: {escalation}", ct);
            return true;
        }
        if (kind is null)
            return false;

        // Reserve before GitHub writes so restarts or competing workers cannot repeat an action.
        if (!await ReserveWatchdogEventAsync(db, wi, headSha, $"recovery:{recoveries.Count + 1}",
            "PrWatchdogRecovery", JsonSerializer.Serialize(new WatchdogRecovery(kind, runId), JsonOpts), ct))
            return false;
        try
        {
            switch (kind)
            {
                case "CancelWorkflow":
                case "RerunWorkflow":
                    if (!await gitHub.TryRecoverWorkflowRunAsync(repo.Owner, repo.Name, pr!.Number, headSha, runId!.Value, kind == "CancelWorkflow", ct))
                    {
                        await auditLogger.RecordAsync("factory-watchdog", "PrWatchdogRecoverySkipped", "WorkItem", entityId,
                            after: new { kind, runId, reason = "PR or workflow state changed before dispatch." }, ct: ct);
                        return false;
                    }
                    break;
                case "RequestReview":
                    if (!await gitHub.RequestCopilotReviewWatchdogAsync(repo.Owner, repo.Name, pr!.Number, ct))
                        throw new InvalidOperationException("Copilot review request was rejected by GitHub; verify reviewer availability and permissions.");
                    break;
                case "UpdateBranch":
                    if (!await gitHub.UpdatePullRequestBranchAsync(repo.Owner, repo.Name, pr!.Number, ct))
                        throw new InvalidOperationException("GitHub could not update the PR branch.");
                    break;
                case "ResolveConflict":
                    var nextRound = Math.Max(1, wi.CurrentRound) + 1;
                    if (nextRound > wi.MaxRounds || await db.AgentSessions.AnyAsync(s => s.WorkItemId == wi.Id && s.EndedAt == null, ct))
                    {
                        await MarkBlockedActivityAsync(wi.Id, "PR watchdog: conflict requires human attention (fix-round limit or active agent session).", ct);
                        return true;
                    }
                    var reason = "Merge main into this branch and resolve merge conflicts without modifying protected paths. Rerun all required checks.";
                    var gate = new GateEvaluationResult(wi.GateProfile, headSha, false, false, false, [],
                        new GateOutcome.NeedsFix([reason], []), [], [reason]);
                    await RequestCopilotFixRoundActivityAsync(wi.Id, pr!.Number, nextRound, gate, [reason], ct);
                    break;
            }
            await auditLogger.RecordAsync("factory-watchdog", "PrWatchdogRecoveryDispatched", "WorkItem", entityId,
                after: new { kind, runId }, issueNumber: wi.IssueNumber, prNumber: pr?.Number, sha: headSha, ct: ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "PR watchdog recovery {Kind} failed for work item {WorkItemId}", kind, wi.Id);
            await MarkBlockedActivityAsync(wi.Id, $"PR watchdog: {kind} failed: {ex.Message}. Check permissions/configuration before retrying.", ct);
            return true;
        }
    }

    private static async Task<bool> ReserveWatchdogEventAsync(
        DemoFactoryDbContext db, WorkItemEntity wi, string headSha, string key, string action, string details, CancellationToken ct)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{wi.Id}:{headSha}:{key}"));
        var id = new Guid(hash.AsSpan(0, 16));
        if (await db.AuditEvents.AnyAsync(a => a.Id == id, ct))
            return false;

        var entry = db.AuditEvents.Add(new AuditEventEntity
        {
            Id = id, Actor = "factory-watchdog", Action = action, EntityType = "WorkItem",
            EntityId = wi.Id.ToString(), AfterJson = details, Sha = headSha,
            IssueNumber = wi.IssueNumber, PrNumber = wi.PrNumber
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            entry.State = EntityState.Detached;
            if (await db.AuditEvents.AnyAsync(a => a.Id == id, ct))
                return false;
            throw;
        }
    }
}
