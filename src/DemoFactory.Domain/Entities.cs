namespace DemoFactory.Domain;

/// <summary>
/// Read-model & audit entities (§7.2).
/// </summary>
public sealed class ProjectEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string SpecMarkdown { get; set; } = string.Empty;
    public int CurrentSpecVersion { get; set; } = 1;
    public string TemplateId { get; set; } = "dotnet-react";
    public string TemplateVersion { get; set; } = "v1.0.0";
    public string? PlanJson { get; set; }
    public string? ClarifyingQuestionsJson { get; set; }
    public string? ClarificationAnswersJson { get; set; }
    public string? PublicSafeFlagsJson { get; set; }
    public string? PlanApprovedBy { get; set; }
    public DateTimeOffset? PlanApprovedAt { get; set; }
    public string? BootstrapInstanceId { get; set; }
    public string? BootstrapStepsJson { get; set; }
    public ProjectState State { get; set; } = ProjectState.Draft;
    public AutonomyLevel DefaultAutonomy { get; set; } = AutonomyLevel.L1;
    public string DefaultCodingModel { get; set; } = "gemini-4-carbon";
    public string DeployTarget { get; set; } = "aca-previews-and-demo";
    public bool IsPublic { get; set; } = true;
    public string? DemoUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RepositoryEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FullName => $"{Owner}/{Name}";
    public long InstallationId { get; set; }
    public string GitHubProjectId { get; set; } = string.Empty;
    public int GitHubProjectNumber { get; set; } = 1;
    public string PolicyReference { get; set; } = string.Empty;
    public bool Paused { get; set; }
    public string? PauseReason { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public string? ReadinessReportJson { get; set; }
    public bool ReadinessPassed { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RuntimeSettingsEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SettingsScope Scope { get; set; } = SettingsScope.Global;
    public Guid? TargetId { get; set; }
    public string? CodingModel { get; set; }
    public string? PmModelDeployment { get; set; }
    public int? Wip { get; set; }
    public AutonomyLevel? Autonomy { get; set; }
    public int? MaxRounds { get; set; }
    public bool? GlobalPaused { get; set; }
    public string? TeamsWebhookUrl { get; set; }
    public bool DailyDigestEnabled { get; set; }
    public string UpdatedBy { get; set; } = "system";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkItemEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepoId { get; set; }
    public Guid? FeatureId { get; set; }
    public string? FeatureSlug { get; set; }
    public int IssueNumber { get; set; }
    public string IssueNodeId { get; set; } = string.Empty;
    public string ProjectItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;
    public string AcceptanceCriteriaJson { get; set; } = "[]";
    public string LabelsJson { get; set; } = "[\"factory\"]";
    public string IssueAuthor { get; set; } = string.Empty;
    public string? WorkLabelAppliedBy { get; set; }
    public bool PassesWorkLabelRule { get; set; } = true;
    public bool IsAcceptanceTestIssue { get; set; }
    public GateProfile GateProfile { get; set; } = GateProfile.Feature;
    public WorkItemStateKind State { get; set; } = WorkItemStateKind.Queued;
    public BoardStatus BoardStatus { get; set; } = BoardStatus.Ready;
    public PriorityLevel Priority { get; set; } = PriorityLevel.Medium;
    public ItemSize Size { get; set; } = ItemSize.Medium;
    public int Attempts { get; set; }
    public int CurrentRound { get; set; }
    public int MaxRounds { get; set; } = 3;
    public int? PrNumber { get; set; }
    public string? PrUrl { get; set; }
    public string? HeadSha { get; set; }
    public string CodingModel { get; set; } = "gemini-4-carbon";
    public string? OrchestrationInstanceId { get; set; }
    public string OrchestrationVersion { get; set; } = "1.0";
    public string BlockedByIssueNumbersJson { get; set; } = "[]";
    public string PredictedTouchPathsJson { get; set; } = "[]";
    public string RequirementIdsJson { get; set; } = "[]";
    public string? PmVerdict { get; set; }
    public string? BlockedReason { get; set; }
    public string? PreviewUrl { get; set; }
    public int SpecVersionPlanned { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
}

public sealed class AgentSessionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public Guid RepoId { get; set; }
    public AgentSessionMode Mode { get; set; } = AgentSessionMode.Implement;
    public string CopilotTaskId { get; set; } = string.Empty;
    public long? TriggerCommentId { get; set; }
    public string? TriggerSummary { get; set; }
    public string Model { get; set; } = "gemini-4-carbon";
    public CopilotTaskState State { get; set; } = CopilotTaskState.Queued;
    public string SessionUrl { get; set; } = string.Empty;
    public string CommitShasJson { get; set; } = "[]";
    public int? TokensUsed { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class ReviewRoundEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public int Round { get; set; }
    public string HeadSha { get; set; } = string.Empty;
    public long CopilotReviewId { get; set; }
    public string ReviewerLogin { get; set; } = "copilot-pull-request-reviewer[bot]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FindingEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public Guid ReviewRoundId { get; set; }
    public string ShortId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public FindingSeverity Severity { get; set; } = FindingSeverity.Medium;
    public string Path { get; set; } = string.Empty;
    public int? Line { get; set; }
    public string? RuleHint { get; set; }
    public string Body { get; set; } = string.Empty;
    public FindingStatus Status { get; set; } = FindingStatus.Open;
    public string? PmTriageClassification { get; set; }
    public string? PmTriageReason { get; set; }
    public string? WaiverReason { get; set; }
    public string? WaivedBy { get; set; }
    public int FirstSeenRound { get; set; } = 1;
    public int LastSeenRound { get; set; } = 1;
}

public sealed class GateEvaluationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public string HeadSha { get; set; } = string.Empty;
    public GateProfile Profile { get; set; } = GateProfile.Feature;
    public bool Passed { get; set; }
    public string OutcomeKind { get; set; } = string.Empty;
    public string ConditionsJson { get; set; } = "[]";
    public string ReasonsJson { get; set; } = "[]";
    public string? EvidenceBlobUri { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ApprovalEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public Guid RepoId { get; set; }
    public string HeadSha { get; set; } = string.Empty;
    public ApprovalKind Kind { get; set; } = ApprovalKind.L1Risk;
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public string Summary { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class InboxEventEntity
{
    public string DeliveryId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public bool TrustedActor { get; set; } = true;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
}

public sealed class BoardSnapshotEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string GitHubProjectId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public BoardStatus Status { get; set; }
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SpecVersionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public int Version { get; set; }
    public string GitSha { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string SpecMarkdown { get; set; } = string.Empty;
    public string PublicSafeResultJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RequirementEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty; // REQ-001
    public Guid ProjectId { get; set; }
    public Guid? FeatureId { get; set; }
    public string Text { get; set; } = string.Empty;
    public int IntroducedInVersion { get; set; } = 1;
    public RequirementCoverageStatus Status { get; set; } = RequirementCoverageStatus.Planned;
    public string LinkedIssueNumbersJson { get; set; } = "[]";
    public string LinkedAcceptanceTestsJson { get; set; } = "[]";
}

public sealed class FeatureSpecEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string SpecMarkdown { get; set; } = string.Empty;
    public string? ClarifyingQuestionsJson { get; set; }
    public string? PlanIncrementJson { get; set; }
    public FeatureSpecState State { get; set; } = FeatureSpecState.Draft;
    public int SpecVersion { get; set; } = 1;
    public int? EpicIssueNumber { get; set; }
    public Guid? ChangeProposalId { get; set; }
    public string? IntakeInstanceId { get; set; }
    public string? ApplyStepsJson { get; set; }
    public string SourceIssueNumbersJson { get; set; } = "[]";
    public string? CodingModelOverride { get; set; }
    public PriorityLevel Priority { get; set; } = PriorityLevel.Medium;
    public string? Milestone { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class InboxItemEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepoId { get; set; }
    public Guid ProjectId { get; set; }
    public int IssueNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int ReactionsCount { get; set; }
    public string LabelsJson { get; set; } = "[]";
    public bool OwnerAttemptedDirectWorkLabel { get; set; }
    public InboxItemState State { get; set; } = InboxItemState.New;
    public Guid? FeatureId { get; set; }
    public string? SuggestedGroup { get; set; }
    public int? DuplicateOfIssueNumber { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChangeProposalEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public ChangeProposalKind Kind { get; set; } = ChangeProposalKind.SpecEdit;
    public int FromSpecVersion { get; set; }
    public int ToSpecVersion { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string ItemsJson { get; set; } = "[]";
    public ChangeProposalState State { get; set; } = ChangeProposalState.PendingApproval;
    public string? AppliedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AppliedAt { get; set; }
}

public sealed class LineStateEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepoId { get; set; }
    public LineStatus State { get; set; } = LineStatus.Running;
    public string? Cause { get; set; }
    public string SuspectPrNumbersJson { get; set; } = "[]";
    public string? ActiveAction { get; set; }
    public int FlakyTestCount { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
    public int? LastRestoreDurationSeconds { get; set; }
}

public sealed class PreviewEnvironmentEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepoId { get; set; }
    public int PrNumber { get; set; }
    public string HeadSha { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ImageDigest { get; set; } = string.Empty;
    public PreviewState State { get; set; } = PreviewState.Running;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class AuditEventEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public int? IssueNumber { get; set; }
    public string? SessionId { get; set; }
    public int? PrNumber { get; set; }
    public string? Sha { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ModelCatalogueEntity
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ApiModelId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool SmokeTestPassed { get; set; } = true;
    public DateTimeOffset? LastSmokeTestedAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class AllowlistUserEntity
{
    public string GitHubLogin { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Admin;
    public string ThemePreference { get; set; } = "system";
    public string DensityPreference { get; set; } = "comfortable";
    public string? CopilotPlan { get; set; } = "Copilot Pro+";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EncryptedUserTokenEntity
{
    public string GitHubLogin { get; set; } = string.Empty;
    public string EncryptedRefreshToken { get; set; } = string.Empty;
    public string KeyId { get; set; } = "local-dev-key";
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
