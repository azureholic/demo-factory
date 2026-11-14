using DemoFactory.Agents;
using DemoFactory.Domain;

namespace DemoFactory.Orchestrator;

public static class OrchestrationVersions
{
    public const string Current = "1.0";
}

public static class ExternalEvents
{
    public const string PrOpened = "PrOpened";
    public const string PrReadyForReview = "PrReadyForReview";
    public const string CopilotWorkStarted = "CopilotWorkStarted";
    public const string CopilotWorkFinished = "CopilotWorkFinished";
    public const string ChecksCompleted = "ChecksCompleted";
    public const string CopilotReviewSubmitted = "CopilotReviewSubmitted";
    public const string OperatorApprovalDecision = "OperatorApprovalDecision";
    public const string PrMerged = "PrMerged";
    public const string IssueClosed = "IssueClosed";
    public const string DependencyChanged = "DependencyChanged";
    public const string StatusChanged = "StatusChanged";
    public const string SlotFreed = "SlotFreed";
    public const string BootstrapResumeOrAbort = "BootstrapResumeOrAbort";
}

public interface IRealtimeNotifier
{
    Task BroadcastWorkItemChangedAsync(Guid workItemId, Guid repoId, string state, CancellationToken ct = default);
    Task BroadcastSessionEventAsync(Guid sessionId, Guid workItemId, string state, CancellationToken ct = default);
    Task BroadcastGateEvaluatedAsync(Guid workItemId, string headSha, string outcome, CancellationToken ct = default);
    Task BroadcastApprovalRequestedAsync(Guid approvalId, Guid workItemId, string kind, CancellationToken ct = default);
    Task BroadcastLineStateChangedAsync(Guid repoId, string status, string? cause, CancellationToken ct = default);
    Task BroadcastFactoryStatusAsync(bool paused, string? reason, CancellationToken ct = default);
}

public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task BroadcastWorkItemChangedAsync(Guid workItemId, Guid repoId, string state, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastSessionEventAsync(Guid sessionId, Guid workItemId, string state, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastGateEvaluatedAsync(Guid workItemId, string headSha, string outcome, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastApprovalRequestedAsync(Guid approvalId, Guid workItemId, string kind, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastLineStateChangedAsync(Guid repoId, string status, string? cause, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastFactoryStatusAsync(bool paused, string? reason, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed record ProjectSchedulerInput(
    Guid RepoId,
    string Version = OrchestrationVersions.Current,
    int IterationCount = 0);

public sealed record WorkItemOrchestrationInput(
    Guid WorkItemId,
    Guid RepoId,
    int MaxRounds = 3,
    string Version = OrchestrationVersions.Current);

public sealed record WorkItemOrchestrationOutput(
    Guid WorkItemId,
    WorkItemStateKind FinalState,
    int RoundsExecuted,
    string? Detail);

public sealed record PrOpenedEvent(int PrNumber, string HeadSha, bool IsDraft);
public sealed record CopilotFinishedEvent(string HeadSha, CopilotTaskState State, string? QuestionForUser = null);
public sealed record RoundSignalsEvent(
    string HeadSha,
    bool ChecksAllGreen,
    bool HasCopilotReview,
    IReadOnlyList<RawReviewComment> ReviewComments,
    IReadOnlyList<ChangedFileDiff> ChangedFiles,
    IReadOnlyList<string> CiFailureExcerpts,
    bool SpecConformancePassed = true);

public sealed record OperatorDecisionEvent(
    ApprovalDecision Decision,
    string DecidedBy,
    string? Reason);

public sealed record PrMergePhaseEvent(
    string Kind, // Merged | NewPush | BehindMain | MergeConflict
    string Sha,
    bool OwnDiffUnchanged = true);

public sealed record BootstrapStepRecord(
    int StepNumber,
    string Name,
    string Status, // Pending | Running | Completed | Failed
    string? Error = null);

public sealed record ProjectBootstrapInput(
    Guid ProjectId,
    string OwnerLogin,
    string Version = OrchestrationVersions.Current);

public sealed record FeatureIntakeInput(
    Guid FeatureId,
    string OwnerLogin,
    string Version = OrchestrationVersions.Current);

public sealed record StopTheLineInput(
    Guid RepoId,
    long FailedWorkflowRunId,
    string Cause,
    IReadOnlyList<int> SuspectMergedPrNumbers,
    string Version = OrchestrationVersions.Current);

public interface IOrchestrationActivities
{
    Task<SpecReviewOutput> RunSpecReviewActivityAsync(Guid workItemId, CancellationToken ct = default);
    Task MarkNeedsClarificationActivityAsync(Guid workItemId, IReadOnlyList<string> questions, CancellationToken ct = default);
    Task<string> ClaimAndAssignToCopilotActivityAsync(Guid workItemId, int attempt, CancellationToken ct = default);
    Task ArmPullRequestActivityAsync(Guid workItemId, int prNumber, string headSha, CancellationToken ct = default);
    Task<GateEvaluationResult> EvaluateAndPostGateActivityAsync(Guid workItemId, int round, RoundSignalsEvent signals, bool carryOverReview, CancellationToken ct = default);
    Task RequestHumanApprovalActivityAsync(Guid workItemId, string headSha, IReadOnlyList<ApprovalKind> kinds, CancellationToken ct = default);
    Task RequestCopilotFixRoundActivityAsync(Guid workItemId, int prNumber, int round, GateEvaluationResult gateResult, IReadOnlyList<string> ciFailures, CancellationToken ct = default);
    Task MarkBlockedActivityAsync(Guid workItemId, string reason, CancellationToken ct = default);
    Task MarkFailedActivityAsync(Guid workItemId, string error, CancellationToken ct = default);
    Task CompleteMergedWorkItemActivityAsync(Guid workItemId, int prNumber, string mergeSha, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SelectAndStartScheduledItemsActivityAsync(Guid repoId, CancellationToken ct = default);
}
