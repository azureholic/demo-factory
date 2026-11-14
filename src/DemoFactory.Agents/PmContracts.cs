using DemoFactory.Domain;

namespace DemoFactory.Agents;

public sealed record PublicSafeFlag(
    string Category,
    string Snippet,
    string Reason);

public sealed record PublicSafeCheckOutput(
    bool Passed,
    IReadOnlyList<PublicSafeFlag> Flags);

public sealed record ExtractedRequirement(
    string Id,
    string Text);

public sealed record SpecClarificationOutput(
    string ClarifiedSpecMarkdown,
    IReadOnlyList<ExtractedRequirement> Requirements,
    IReadOnlyList<string> ClarifyingQuestions,
    PublicSafeCheckOutput PublicSafe);

public sealed record PlannedBacklogItem(
    string Key,
    string Title,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> RequirementIds,
    ItemSize Size,
    PriorityLevel Priority,
    string Milestone,
    bool IsAcceptanceTest,
    IReadOnlyList<string> BlockedByKeys,
    IReadOnlyList<int> BlockedByExistingIssueNumbers,
    IReadOnlyList<string> PredictedTouchPaths,
    BoardStatus InitialStatus = BoardStatus.Ready);

public sealed record GeneratedPlanOutput(
    string Adr0001Markdown,
    IReadOnlyList<ExtractedRequirement> Requirements,
    IReadOnlyList<string> Milestones,
    IReadOnlyList<PlannedBacklogItem> Items,
    decimal EstimatedCostUsd,
    int EstimatedDurationMinutes);

public sealed record PlanIncrementOutput(
    string FeatureSlug,
    string? AdrMarkdown,
    IReadOnlyList<ExtractedRequirement> NewRequirements,
    IReadOnlyList<PlannedBacklogItem> Items,
    IReadOnlyList<string> OverlapWarnings,
    IReadOnlyList<int> AffectedInFlightIssueNumbers,
    decimal EstimatedCostUsd,
    int EstimatedDurationMinutes);

public sealed record BacklogSummaryItem(
    int IssueNumber,
    string Title,
    BoardStatus Status,
    PriorityLevel Priority,
    IReadOnlyList<string> RequirementIds);

public sealed record ProposedChangeItem(
    string Action, // add | update | close | change-request
    int? TargetIssueNumber,
    string Title,
    string Details,
    IReadOnlyList<string> RequirementIds);

public sealed record ChangeProposalOutput(
    string Summary,
    IReadOnlyList<ProposedChangeItem> Items,
    IReadOnlyList<int> AffectedInProgressIssues);

public sealed record UntrustedInboxIssue(
    int IssueNumber,
    string Author,
    string Title,
    string Body);

public sealed record InboxSuggestionItem(
    int IssueNumber,
    string SuggestedGroup,
    int? LikelyDuplicateOf);

public sealed record InboxGroupingOutput(
    IReadOnlyList<InboxSuggestionItem> Suggestions);

public sealed record DraftedFeatureOutput(
    string Title,
    string Slug,
    string FeatureSpecMarkdown,
    IReadOnlyList<int> SourceIssueNumbers);

public sealed record IssueSpecInput(
    int IssueNumber,
    string Title,
    string BodyMarkdown,
    IReadOnlyList<string> AcceptanceCriteria,
    ItemSize Size,
    IReadOnlyList<int> BlockedByIssueNumbers);

public sealed record SpecReviewOutput(
    string Verdict, // Ready | NeedsClarification
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Questions);

public sealed record TouchSetEstimateOutput(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Modules,
    double Confidence);

public sealed record ComposedInstructionsOutput(
    string CustomInstructions,
    IReadOnlyList<string> DefinitionOfDoneChecklist);

public sealed record FindingTriageItem(
    string FindingId,
    string Classification, // actionable | likely_false_positive
    string Reason);

public sealed record FindingTriageOutput(
    IReadOnlyList<FindingTriageItem> Items);

public sealed record SpecConformanceOutput(
    string Verdict, // pass | fail
    IReadOnlyList<string> UnmetCriteria);

public sealed record DecomposedSubIssue(
    string Title,
    string BodyMarkdown,
    IReadOnlyList<string> AcceptanceCriteria,
    ItemSize Size);

public sealed record DecompositionSuggestionOutput(
    IReadOnlyList<DecomposedSubIssue> SubIssues);

public sealed record ReorderOutput(
    IReadOnlyList<PmReorderSuggestion> Suggestions);

public sealed record ProjectIncrementContext(
    string ProjectName,
    string CurrentSpecMarkdown,
    int NextRequirementNumber,
    IReadOnlyList<BacklogSummaryItem> ExistingBacklog,
    IReadOnlyList<int> InFlightIssueNumbers,
    IReadOnlyList<string> RepoFileTree);
