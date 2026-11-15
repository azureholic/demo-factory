namespace DemoFactory.Domain;

public enum BoardStatus
{
    Backlog,
    Ready,
    InProgress,
    InReview,
    Blocked,
    Done
}

public enum WorkItemStateKind
{
    Queued,
    SpecReview,
    NeedsClarification,
    Claimed,
    Implementing,
    PrOpen,
    Validating,
    Fixing,
    AwaitingApproval,
    AwaitingMerge,
    Merged,
    Blocked,
    Failed,
    Cancelled
}

public enum PriorityLevel
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum ItemSize
{
    Small = 1,
    Medium = 2,
    Large = 3,
    XLarge = 4
}

public enum FindingSeverity
{
    Low = 1,
    Medium = 2,
    High = 3
}

public enum FindingStatus
{
    Open,
    Addressed,
    Repeated,
    Contested,
    Waived
}

public enum AutonomyLevel
{
    L0 = 0,
    L1 = 1,
    L2 = 2
}

public enum GateProfile
{
    Feature,
    AcceptanceTest,
    Dependabot,
    SecurityFix,
    Revert,
    LineFix,
    Spec,
    TemplateUpgrade
}

public enum AgentSessionMode
{
    Implement,
    Fix,
    ResolveConflict
}

public enum CopilotTaskState
{
    Queued,
    InProgress,
    Completed,
    Failed,
    Idle,
    WaitingForUser,
    TimedOut,
    Cancelled
}

public enum ApprovalKind
{
    L1Risk,
    AcceptanceTest,
    TemplateUpgrade,
    ModelCheckEscalation,
    SpecChange
}

public enum ApprovalDecision
{
    Pending,
    Approved,
    ChangesRequested
}

public enum FeatureSpecState
{
    Draft,
    Clarifying,
    Planned,
    Approved,
    Applying,
    Applied,
    Completed,
    Withdrawn
}

public enum InboxItemState
{
    New,
    Dismissed,
    Planned,
    Done
}

public enum ChangeProposalKind
{
    SpecEdit,
    Feature
}

public enum ChangeProposalState
{
    Draft,
    PendingApproval,
    Applied,
    Dismissed
}

public enum LineStatus
{
    Running,
    LineStopped
}

public enum PreviewState
{
    Provisioning,
    Running,
    Failed,
    Deleted
}

public enum ProjectState
{
    Draft,
    Clarifying,
    Planned,
    Approved,
    Bootstrapping,
    BootstrapPaused,
    Running,
    Paused,
    Aborted
}

public enum UserRole
{
    Viewer = 1,
    Operator = 2,
    Admin = 3
}

public enum SettingsScope
{
    Global,
    Repo,
    WorkItem
}

public enum DeploymentProfile
{
    Lean,
    Reference
}

public enum DependabotUpdateType
{
    None,
    Security,
    Patch,
    Minor,
    Major
}

public enum RequirementCoverageStatus
{
    Planned,
    InProgress,
    TestsWritten,
    TestsPassing,
    Done
}
