using DemoFactory.Agents;
using DemoFactory.Domain;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DemoFactory.Orchestrator;

/// <summary>
/// Eternal deterministic ProjectScheduler orchestration (§8.1, §3.2).
/// Uses replay-safe APIs exclusively and upgrades orchestration version cleanly on ContinueAsNew.
/// </summary>
public sealed class ProjectSchedulerOrchestration : TaskOrchestrator<ProjectSchedulerInput, string>
{
    public const int MaxHistoryIterationsBeforeContinueAsNew = 25;

    public override async Task<string> RunAsync(TaskOrchestrationContext context, ProjectSchedulerInput input)
    {
        var logger = context.CreateReplaySafeLogger<ProjectSchedulerOrchestration>();
        logger.LogInformation("ProjectScheduler running for repo {RepoId} (version={Version}, iter={Iter})",
            input.RepoId, input.Version, input.IterationCount);

        var startedWorkItemIds = await context.CallActivityAsync<IReadOnlyList<Guid>>(
            nameof(SelectScheduledItemsActivity),
            input.RepoId);

        foreach (var workItemId in startedWorkItemIds)
        {
            await context.CallSubOrchestratorAsync<WorkItemOrchestrationOutput>(
                nameof(WorkItemOrchestration),
                new WorkItemOrchestrationInput(workItemId, input.RepoId, 3, input.Version),
                new SubOrchestrationOptions { InstanceId = $"workitem-{workItemId:N}-v{input.Version}" });
        }

        // Wait for any scheduler trigger event or periodic 2-minute sweep (§18 AC 1, AC 5)
        using var cts = new CancellationTokenSource();
        var timerTask = context.CreateTimer(context.CurrentUtcDateTime.AddMinutes(2), cts.Token);
        var statusChanged = context.WaitForExternalEvent<string>(ExternalEvents.StatusChanged);
        var depChanged = context.WaitForExternalEvent<string>(ExternalEvents.DependencyChanged);
        var prMerged = context.WaitForExternalEvent<string>(ExternalEvents.PrMerged);
        var slotFreed = context.WaitForExternalEvent<string>(ExternalEvents.SlotFreed);

        var completed = await Task.WhenAny(timerTask, statusChanged, depChanged, prMerged, slotFreed);
        if (completed != timerTask)
            cts.Cancel();

        // §3.2: Eternal ProjectScheduler switches to latest OrchestrationVersions.Current on ContinueAsNew
        var nextIter = input.IterationCount + 1;
        var nextVersion = nextIter >= MaxHistoryIterationsBeforeContinueAsNew
            ? OrchestrationVersions.Current
            : input.Version;

        context.ContinueAsNew(new ProjectSchedulerInput(
            input.RepoId,
            nextVersion,
            nextIter >= MaxHistoryIterationsBeforeContinueAsNew ? 0 : nextIter));

        return "Continued";
    }
}

/// <summary>
/// Deterministic WorkItemOrchestration (§8.2, §3.2).
/// Implements SpecReview -> Claim -> Assign -> Arm -> Review/Gate Loop -> Merge Serialization -> Done.
/// </summary>
public sealed class WorkItemOrchestration : TaskOrchestrator<WorkItemOrchestrationInput, WorkItemOrchestrationOutput>
{
    public override async Task<WorkItemOrchestrationOutput> RunAsync(
        TaskOrchestrationContext context,
        WorkItemOrchestrationInput input)
    {
        var logger = context.CreateReplaySafeLogger<WorkItemOrchestration>();
        logger.LogInformation("WorkItemOrchestration started for {WorkItemId} (v={Version})", input.WorkItemId, input.Version);

        // Step 1: SpecReview via PM agent
        var specReview = await context.CallActivityAsync<SpecReviewOutput>(
            nameof(RunSpecReviewActivity),
            input.WorkItemId);

        if (string.Equals(specReview.Verdict, "NeedsClarification", StringComparison.OrdinalIgnoreCase))
        {
            await context.CallActivityAsync(
                nameof(MarkNeedsClarificationActivity),
                new ClarificationActivityInput(input.WorkItemId, specReview.Questions));

            return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.NeedsClarification, 0, "Spec needs clarification.");
        }

        // Step 2 & 3: Claim WIP slot & Assign to Copilot (with single retry on session failure/timeout §8.5)
        PrOpenedEvent? prOpened = null;
        CopilotFinishedEvent? workFinished = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await context.CallActivityAsync<string>(
                nameof(ClaimAndAssignActivity),
                new AssignActivityInput(input.WorkItemId, attempt));

            using var assignCts = new CancellationTokenSource();
            var timeout = context.CreateTimer(context.CurrentUtcDateTime.AddMinutes(90), assignCts.Token);
            var prTask = context.WaitForExternalEvent<PrOpenedEvent>(ExternalEvents.PrOpened);
            var finTask = context.WaitForExternalEvent<CopilotFinishedEvent>(ExternalEvents.CopilotWorkFinished);

            var first = await Task.WhenAny(timeout, Task.WhenAll(prTask, finTask));
            if (first == timeout)
            {
                if (attempt == 2)
                {
                    await context.CallActivityAsync(nameof(MarkFailedActivity), new FailActivityInput(input.WorkItemId, "Copilot assignment timed out twice."));
                    return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.Failed, 0, "Timed out waiting for Copilot PR.");
                }
                continue;
            }

            assignCts.Cancel();
            prOpened = await prTask;
            workFinished = await finTask;

            if (workFinished.State == CopilotTaskState.WaitingForUser)
            {
                var q = string.IsNullOrWhiteSpace(workFinished.QuestionForUser)
                    ? "Copilot requested clarification before completing implementation."
                    : workFinished.QuestionForUser;
                await context.CallActivityAsync(
                    nameof(MarkNeedsClarificationActivity),
                    new ClarificationActivityInput(input.WorkItemId, [q]));
                return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.NeedsClarification, 0, q);
            }

            if (workFinished.State is CopilotTaskState.Failed or CopilotTaskState.TimedOut)
            {
                if (attempt == 2)
                {
                    await context.CallActivityAsync(nameof(MarkFailedActivity), new FailActivityInput(input.WorkItemId, $"Copilot session ended in {workFinished.State} after retry."));
                    return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.Failed, 0, "Copilot session failed after retry.");
                }
                continue;
            }

            break;
        }

        if (prOpened is null || workFinished is null)
        {
            await context.CallActivityAsync(nameof(MarkFailedActivity), new FailActivityInput(input.WorkItemId, "Missing PR signal."));
            return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.Failed, 0, "Missing PR signal.");
        }

        // Step 4: Arm auto-merge (squash) & move board to In Review
        var currentSha = workFinished.HeadSha;
        await context.CallActivityAsync(
            nameof(ArmPullRequestActivity),
            new ArmPrActivityInput(input.WorkItemId, prOpened.PrNumber, currentSha));

        // Step 5 & 6: Validation, Batched Fix Rounds, Human Approval, and Merge Loop
        var maxRounds = Math.Max(1, input.MaxRounds);
        var carryOverReview = false;
        var currentRound = 1;

        while (currentRound <= maxRounds)
        {
            var signals = await context.WaitForExternalEvent<RoundSignalsEvent>(ExternalEvents.ChecksCompleted);
            currentSha = signals.HeadSha;

            var gate = await context.CallActivityAsync<GateEvaluationResult>(
                nameof(EvaluateAndPostGateActivity),
                new EvaluateGateActivityInput(input.WorkItemId, currentRound, signals, carryOverReview));
            carryOverReview = false;

            if (gate.Passed)
            {
                // Step 6: Await PrMerged (or NewPush / BehindMain / MergeConflict)
                var mergeEvent = await context.WaitForExternalEvent<PrMergePhaseEvent>(ExternalEvents.PrMerged);
                if (string.Equals(mergeEvent.Kind, "Merged", StringComparison.OrdinalIgnoreCase))
                {
                    await context.CallActivityAsync(
                        nameof(CompleteMergedWorkItemActivity),
                        new CompleteMergeActivityInput(input.WorkItemId, prOpened.PrNumber, mergeEvent.Sha));

                    return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.Merged, currentRound, mergeEvent.Sha);
                }

                if (string.Equals(mergeEvent.Kind, "BehindMain", StringComparison.OrdinalIgnoreCase))
                {
                    carryOverReview = mergeEvent.OwnDiffUnchanged;
                    continue;
                }

                continue;
            }

            if (gate.OnlyApprovalMissing && gate.Outcome is GateOutcome.OnlyApprovalMissing missing)
            {
                await context.CallActivityAsync(
                    nameof(RequestHumanApprovalActivity),
                    new RequestApprovalActivityInput(input.WorkItemId, currentSha, missing.MissingApprovals));

                var decision = await context.WaitForExternalEvent<OperatorDecisionEvent>(ExternalEvents.OperatorApprovalDecision);
                if (decision.Decision == ApprovalDecision.Approved)
                {
                    var mergeEvent = await context.WaitForExternalEvent<PrMergePhaseEvent>(ExternalEvents.PrMerged);
                    if (string.Equals(mergeEvent.Kind, "Merged", StringComparison.OrdinalIgnoreCase))
                    {
                        await context.CallActivityAsync(
                            nameof(CompleteMergedWorkItemActivity),
                            new CompleteMergeActivityInput(input.WorkItemId, prOpened.PrNumber, mergeEvent.Sha));

                        return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.Merged, currentRound, mergeEvent.Sha);
                    }
                    continue;
                }
            }

            if (gate.HasContestedHigh || gate.Outcome is GateOutcome.Blocked || currentRound >= maxRounds)
            {
                var blockReason = gate.HasContestedHigh
                    ? "Contested High severity finding requires human review."
                    : $"Gate did not pass within MaxRounds ({maxRounds}): {string.Join("; ", gate.FailureReasons)}";

                await context.CallActivityAsync(
                    nameof(MarkBlockedActivity),
                    new BlockActivityInput(input.WorkItemId, blockReason));

                return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.Blocked, currentRound, blockReason);
            }

            // Set PR to draft, post ONE batched @copilot comment, wait for CopilotWorkFinished, mark ready & re-arm
            await context.CallActivityAsync(
                nameof(RequestCopilotFixRoundActivity),
                new FixRoundActivityInput(input.WorkItemId, prOpened.PrNumber, currentRound, gate, signals.CiFailureExcerpts));

            var fixDone = await context.WaitForExternalEvent<CopilotFinishedEvent>(ExternalEvents.CopilotWorkFinished);
            currentSha = fixDone.HeadSha;
            await context.CallActivityAsync(
                nameof(ArmPullRequestActivity),
                new ArmPrActivityInput(input.WorkItemId, prOpened.PrNumber, currentSha));

            currentRound++;
        }

        await context.CallActivityAsync(nameof(MarkBlockedActivity), new BlockActivityInput(input.WorkItemId, "MaxRounds exhausted."));
        return new WorkItemOrchestrationOutput(input.WorkItemId, WorkItemStateKind.Blocked, maxRounds, "MaxRounds exhausted.");
    }
}

// Activity payload records & TaskActivity wrappers for Durable Task Worker registration
public sealed record ClarificationActivityInput(Guid WorkItemId, IReadOnlyList<string> Questions);
public sealed record AssignActivityInput(Guid WorkItemId, int Attempt);
public sealed record ArmPrActivityInput(Guid WorkItemId, int PrNumber, string HeadSha);
public sealed record EvaluateGateActivityInput(Guid WorkItemId, int Round, RoundSignalsEvent Signals, bool CarryOverReview);
public sealed record RequestApprovalActivityInput(Guid WorkItemId, string HeadSha, IReadOnlyList<ApprovalKind> Kinds);
public sealed record FixRoundActivityInput(Guid WorkItemId, int PrNumber, int Round, GateEvaluationResult GateResult, IReadOnlyList<string> CiFailures);
public sealed record BlockActivityInput(Guid WorkItemId, string Reason);
public sealed record FailActivityInput(Guid WorkItemId, string Error);
public sealed record CompleteMergeActivityInput(Guid WorkItemId, int PrNumber, string MergeSha);

public sealed class SelectScheduledItemsActivity(IOrchestrationActivities impl) : TaskActivity<Guid, IReadOnlyList<Guid>>
{
    public override Task<IReadOnlyList<Guid>> RunAsync(TaskActivityContext context, Guid repoId) =>
        impl.SelectAndStartScheduledItemsActivityAsync(repoId);
}

public sealed class RunSpecReviewActivity(IOrchestrationActivities impl) : TaskActivity<Guid, SpecReviewOutput>
{
    public override Task<SpecReviewOutput> RunAsync(TaskActivityContext context, Guid workItemId) =>
        impl.RunSpecReviewActivityAsync(workItemId);
}

public sealed class MarkNeedsClarificationActivity(IOrchestrationActivities impl) : TaskActivity<ClarificationActivityInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, ClarificationActivityInput input)
    {
        await impl.MarkNeedsClarificationActivityAsync(input.WorkItemId, input.Questions);
        return true;
    }
}

public sealed class ClaimAndAssignActivity(IOrchestrationActivities impl) : TaskActivity<AssignActivityInput, string>
{
    public override Task<string> RunAsync(TaskActivityContext context, AssignActivityInput input) =>
        impl.ClaimAndAssignToCopilotActivityAsync(input.WorkItemId, input.Attempt);
}

public sealed class ArmPullRequestActivity(IOrchestrationActivities impl) : TaskActivity<ArmPrActivityInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, ArmPrActivityInput input)
    {
        await impl.ArmPullRequestActivityAsync(input.WorkItemId, input.PrNumber, input.HeadSha);
        return true;
    }
}

public sealed class EvaluateAndPostGateActivity(IOrchestrationActivities impl) : TaskActivity<EvaluateGateActivityInput, GateEvaluationResult>
{
    public override Task<GateEvaluationResult> RunAsync(TaskActivityContext context, EvaluateGateActivityInput input) =>
        impl.EvaluateAndPostGateActivityAsync(input.WorkItemId, input.Round, input.Signals, input.CarryOverReview);
}

public sealed class RequestHumanApprovalActivity(IOrchestrationActivities impl) : TaskActivity<RequestApprovalActivityInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, RequestApprovalActivityInput input)
    {
        await impl.RequestHumanApprovalActivityAsync(input.WorkItemId, input.HeadSha, input.Kinds);
        return true;
    }
}

public sealed class RequestCopilotFixRoundActivity(IOrchestrationActivities impl) : TaskActivity<FixRoundActivityInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, FixRoundActivityInput input)
    {
        await impl.RequestCopilotFixRoundActivityAsync(input.WorkItemId, input.PrNumber, input.Round, input.GateResult, input.CiFailures);
        return true;
    }
}

public sealed class MarkBlockedActivity(IOrchestrationActivities impl) : TaskActivity<BlockActivityInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, BlockActivityInput input)
    {
        await impl.MarkBlockedActivityAsync(input.WorkItemId, input.Reason);
        return true;
    }
}

public sealed class MarkFailedActivity(IOrchestrationActivities impl) : TaskActivity<FailActivityInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, FailActivityInput input)
    {
        await impl.MarkFailedActivityAsync(input.WorkItemId, input.Error);
        return true;
    }
}

public sealed class CompleteMergedWorkItemActivity(IOrchestrationActivities impl) : TaskActivity<CompleteMergeActivityInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, CompleteMergeActivityInput input)
    {
        await impl.CompleteMergedWorkItemActivityAsync(input.WorkItemId, input.PrNumber, input.MergeSha);
        return true;
    }
}
