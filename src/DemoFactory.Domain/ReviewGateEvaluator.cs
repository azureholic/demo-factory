namespace DemoFactory.Domain;

public sealed record CheckRunSnapshot(
    string Name,
    string Sha,
    string Status,
    string? Conclusion,
    string AppSlug);

public sealed record ModelCheckVerdict(
    bool Executed,
    bool Passed,
    int FailedRounds,
    IReadOnlyList<string> Reasons)
{
    public static ModelCheckVerdict NotApplicable() => new(false, true, 0, []);
    public static ModelCheckVerdict Pass() => new(true, true, 0, []);
    public static ModelCheckVerdict Fail(int failedRounds, params string[] reasons) => new(true, false, failedRounds, reasons);
}

public sealed record AcceptanceTestJUnitSummary(
    bool HarnessSmokePassed,
    int NewTestCount,
    int FailureCount,
    int ErrorCount,
    int PassedCount);

public sealed record FeatureAcceptanceSummary(
    string IssueKey,
    bool SeparateAtIssueRequired,
    bool SuitePassedOnHeadSha,
    bool AtFilesByteIdenticalToMain,
    IReadOnlyList<string> AddedToPendingJson,
    IReadOnlyList<string> RemovedFromPendingJson);

public sealed record PreviewWorkflowSummary(
    bool Exists,
    string WorkflowFile,
    string EventName,
    string RefBranch,
    string HeadSha,
    bool Succeeded,
    string? PreviewUrl = null);

public sealed record DependabotMetadata(
    DependabotUpdateType UpdateType,
    string Ecosystem,
    bool DependencyReviewClean);

public sealed record RecordedApproval(
    ApprovalKind Kind,
    string HeadSha,
    ApprovalDecision Decision,
    string DecidedBy);

public sealed record GateInput(
    GateProfile Profile,
    string EvaluatedSha,
    string CurrentPrHeadSha,
    RepoPolicy Policy,
    AutonomyLevel EffectiveAutonomy,
    IReadOnlyList<CheckRunSnapshot> CheckRuns,
    bool HasCopilotReviewOnHeadSha,
    bool CarryOverCopilotReviewFromUnchangedDiff,
    IReadOnlyList<TrackedFinding> Findings,
    IReadOnlyList<ChangedFileDiff> ChangedFiles,
    IReadOnlyList<string> MergedAcceptanceTestIssueKeys,
    IReadOnlyList<string> Labels,
    ModelCheckVerdict SpecConformanceCheck,
    ModelCheckVerdict AtMappingCheck,
    ModelCheckVerdict PublicSafeCheck,
    bool CodeScanningCompletedOnHeadSha,
    int NewCodeScanningAlertsAboveThreshold,
    bool DependencyReviewPassed,
    DependabotMetadata? Dependabot,
    AcceptanceTestJUnitSummary? AtJUnit,
    FeatureAcceptanceSummary? FeatureAcceptance,
    PreviewWorkflowSummary? PreviewRun,
    LineStatus LineStatus,
    IReadOnlyList<RecordedApproval> Approvals,
    TrustedOriginContext Origin);

public sealed record GateConditionEvaluation(
    int Number,
    string Name,
    bool Applicable,
    bool Passed,
    bool IsPending,
    string Summary,
    string? Evidence = null);

/// <summary>
/// Closed hierarchy representing the deterministic outcome of the review gate (§8.3).
/// </summary>
public abstract record GateOutcome
{
    private GateOutcome() { }

    public sealed record Pass : GateOutcome;
    public sealed record Pending(IReadOnlyList<string> WaitingOn) : GateOutcome;
    public sealed record OnlyApprovalMissing(IReadOnlyList<ApprovalKind> MissingApprovals) : GateOutcome;
    public sealed record NeedsFix(IReadOnlyList<string> Reasons, IReadOnlyList<string> ProtectedPathsTouched) : GateOutcome;
    public sealed record Blocked(IReadOnlyList<string> EscalationReasons) : GateOutcome;
}

public sealed record GateEvaluationResult(
    GateProfile Profile,
    string EvaluatedSha,
    bool Passed,
    bool OnlyApprovalMissing,
    bool HasContestedHigh,
    IReadOnlyList<string> ProtectedPathsTouched,
    GateOutcome Outcome,
    IReadOnlyList<GateConditionEvaluation> Conditions,
    IReadOnlyList<string> FailureReasons);

/// <summary>
/// Pure deterministic review-gate evaluator (§8.3).
/// Design invariant (§1, §8.3, §18 AC 21):
/// Model checks (condition 8, AT mapping check, public-safe check) can ONLY block/escalate;
/// a passing model check never overrides any failing deterministic condition.
/// </summary>
public static class ReviewGateEvaluator
{
    public const string GateCheckName = "demo-factory/review-gate";
    public const string ExpectedActionsAppSlug = "github-actions";

    public static GateEvaluationResult Evaluate(GateInput input)
    {
        var conditions = new List<GateConditionEvaluation>(15);
        var protectedViolations = new List<string>();
        var requiredApprovals = DetermineRequiredApprovals(input);

        // Condition 1: Evaluated SHA == current PR head SHA (Applies to all profiles)
        var c1Pass = !string.IsNullOrWhiteSpace(input.EvaluatedSha)
            && string.Equals(input.EvaluatedSha, input.CurrentPrHeadSha, StringComparison.OrdinalIgnoreCase);
        conditions.Add(new GateConditionEvaluation(
            1, "Head SHA match", true, c1Pass, false,
            c1Pass ? $"Evaluated SHA {ShortSha(input.EvaluatedSha)} matches PR head." : $"Evaluated SHA '{input.EvaluatedSha}' != current head '{input.CurrentPrHeadSha}'."));

        // Condition 2: Every required check is success on that SHA, pinned to GitHub Actions (§8.3, §10.2, §18 AC 19)
        var (c2Pass, c2Pending, c2Msg) = EvaluateRequiredChecks(input);
        conditions.Add(new GateConditionEvaluation(2, "Required CI checks (source-pinned)", true, c2Pass, c2Pending, c2Msg));

        // Condition 3: Copilot review exists on head SHA (or carried over after diff-preserving branch update)
        var c3Applicable = input.Profile is GateProfile.Feature or GateProfile.SecurityFix or GateProfile.LineFix or GateProfile.Dependabot;
        var c3Pass = !c3Applicable || input.HasCopilotReviewOnHeadSha || input.CarryOverCopilotReviewFromUnchangedDiff;
        conditions.Add(new GateConditionEvaluation(
            3, "Copilot code review present", c3Applicable, c3Pass, c3Applicable && !c3Pass,
            !c3Applicable ? "Not required for profile." :
            input.HasCopilotReviewOnHeadSha ? "Copilot review recorded on head SHA." :
            input.CarryOverCopilotReviewFromUnchangedDiff ? "Copilot review carried over after unchanged-diff branch update." :
            "Waiting for Copilot review on head SHA."));

        // Condition 4: Count of Open/Repeated findings >= blocking severity == 0
        var c4Applicable = input.Profile is GateProfile.Feature or GateProfile.SecurityFix or GateProfile.LineFix or GateProfile.Dependabot;
        var threshold = input.Profile == GateProfile.Dependabot
            ? FindingSeverity.High // §5.4: No open High Copilot review findings
            : input.Policy.Review.BlockingSeverity;
        var blockingFindings = c4Applicable
            ? input.Findings.Where(f =>
                f.Status is FindingStatus.Open or FindingStatus.Repeated
                && f.Severity >= threshold).ToList()
            : [];
        var c4Pass = !c4Applicable || blockingFindings.Count == 0;
        conditions.Add(new GateConditionEvaluation(
            4, "Blocking review findings", c4Applicable, c4Pass, false,
            !c4Applicable ? "Advisory only for profile." :
            c4Pass ? $"0 open findings at or above {threshold}." :
            $"{blockingFindings.Count} open/repeated finding(s) >= {threshold}: {string.Join(", ", blockingFindings.Select(f => $"{f.Id} [{f.Severity}]"))}."));

        // Condition 5: No Contested High findings
        var c5Applicable = input.Profile is GateProfile.Feature or GateProfile.SecurityFix or GateProfile.LineFix or GateProfile.Dependabot;
        var contestedHigh = input.Findings.Where(f => f.Status == FindingStatus.Contested && f.Severity == FindingSeverity.High).ToList();
        var hasContestedHigh = c5Applicable && contestedHigh.Count > 0;
        var c5Pass = !hasContestedHigh;
        conditions.Add(new GateConditionEvaluation(
            5, "No contested High findings", c5Applicable, c5Pass, false,
            !c5Applicable ? "Not applicable." :
            c5Pass ? "No contested High findings." :
            $"Contested High finding(s) require human resolution: {string.Join(", ", contestedHigh.Select(f => f.Id))}."));

        // Condition 6: Protected paths
        var (c6Applicable, c6Pass, c6Violations, c6Msg) = EvaluateProtectedPaths(input);
        protectedViolations.AddRange(c6Violations);
        conditions.Add(new GateConditionEvaluation(6, "Protected paths untouched", c6Applicable, c6Pass, false, c6Msg));

        // Condition 7: Diff size <= policy.maxChangedLines
        var c7Applicable = input.Profile is GateProfile.Feature or GateProfile.AcceptanceTest or GateProfile.SecurityFix or GateProfile.LineFix;
        var totalChangedLines = input.ChangedFiles.Sum(f => f.Additions + f.Deletions);
        var c7Pass = !c7Applicable || totalChangedLines <= input.Policy.Gate.MaxChangedLines;
        conditions.Add(new GateConditionEvaluation(
            7, "Diff size within limit", c7Applicable, c7Pass, false,
            !c7Applicable ? "Not applicable." :
            c7Pass ? $"{totalChangedLines} changed lines <= {input.Policy.Gate.MaxChangedLines}." :
            $"Diff has {totalChangedLines} changed lines, exceeding limit {input.Policy.Gate.MaxChangedLines}."));

        // Condition 8: Spec conformance (model check — can only block, never approve)
        var c8Applicable = input.Policy.Gate.SpecConformance
            && input.Profile is GateProfile.Feature or GateProfile.SecurityFix or GateProfile.LineFix;
        var hasModelEscalationApproval = HasValidApproval(input.Approvals, ApprovalKind.ModelCheckEscalation, input.CurrentPrHeadSha);
        var c8Pass = !c8Applicable || !input.SpecConformanceCheck.Executed || input.SpecConformanceCheck.Passed || hasModelEscalationApproval;
        conditions.Add(new GateConditionEvaluation(
            8, "Spec conformance (model check)", c8Applicable, c8Pass, false,
            !c8Applicable ? "Not enabled for profile." :
            c8Pass ? (hasModelEscalationApproval && !input.SpecConformanceCheck.Passed
                ? "Model check overridden by human approval."
                : "No conformance blockers raised.") :
            $"Spec conformance check blocked: {string.Join("; ", input.SpecConformanceCheck.Reasons)}"));

        // Condition 9: Code scanning complete on head SHA with 0 new alerts >= threshold
        var c9Applicable = input.Profile is not (GateProfile.Revert or GateProfile.Spec);
        var c9Pending = c9Applicable && !input.CodeScanningCompletedOnHeadSha;
        var c9Pass = !c9Applicable || (input.CodeScanningCompletedOnHeadSha && input.NewCodeScanningAlertsAboveThreshold == 0);
        conditions.Add(new GateConditionEvaluation(
            9, "Code scanning clean", c9Applicable, c9Pass, c9Pending,
            !c9Applicable ? "Not applicable." :
            c9Pending ? "Waiting for CodeQL analysis on head SHA." :
            c9Pass ? "Code scanning complete with 0 new high/error alerts." :
            $"Code scanning found {input.NewCodeScanningAlertsAboveThreshold} new alert(s) at or above threshold."));

        // Condition 10: Dependency review & Dependabot policy (§5.4)
        var (c10Applicable, c10Pass, c10Blocked, c10Msg) = EvaluateDependencyCondition(input);
        conditions.Add(new GateConditionEvaluation(10, "Dependency review & policy", c10Applicable, c10Pass, false, c10Msg));

        // Condition 11: Acceptance tests first (§6.1) — Feature profile & AcceptanceTest profile
        var (c11Applicable, c11Pass, c11Msg) = EvaluateAcceptanceTestsCondition(input, hasModelEscalationApproval);
        conditions.Add(new GateConditionEvaluation(11, "Acceptance tests first", c11Applicable, c11Pass, false, c11Msg));

        // Condition 12: Trusted preview deployment (§6.4)
        var c12Applicable = input.Policy.HasDeployTarget
            && input.Profile is GateProfile.Feature or GateProfile.SecurityFix or GateProfile.LineFix;
        var (c12Pass, c12Pending, c12Msg) = EvaluatePreviewCondition(c12Applicable, input);
        conditions.Add(new GateConditionEvaluation(12, "Trusted preview deployment", c12Applicable, c12Pass, c12Pending, c12Msg));

        // Condition 13: Line not stopped (§6.2 — Revert and LineFix are exempt!)
        var c13Applicable = input.Profile is not (GateProfile.Revert or GateProfile.LineFix);
        var c13Pending = c13Applicable && input.LineStatus == LineStatus.LineStopped;
        var c13Pass = !c13Applicable || input.LineStatus == LineStatus.Running;
        conditions.Add(new GateConditionEvaluation(
            13, "Line not stopped", c13Applicable, c13Pass, c13Pending,
            !c13Applicable ? $"Exempt for '{input.Profile}' profile." :
            c13Pass ? "Main line is green." : "pending: line stopped (main is red)."));

        // Condition 14: Human approval in factory UI for head SHA (Plus L0 Shadow mode check §11.3 + Spec public-safe check §6.3)
        var (c14Applicable, c14Pass, missingApprovals, isL0ShadowHold, c14Msg) = EvaluateApprovalAndAutonomyCondition(input, requiredApprovals);
        conditions.Add(new GateConditionEvaluation(14, "Operator approval & autonomy gate", c14Applicable, c14Pass, isL0ShadowHold, c14Msg));

        // Condition 15: Trusted origin (§11.4, §5.6)
        var (c15Pass, c15Reason) = TrustBoundary.EvaluateTrustedPrOrigin(input.Origin);
        conditions.Add(new GateConditionEvaluation(15, "Trusted PR origin & work label", true, c15Pass, false, c15Reason));

        // Determine overall deterministic outcome
        var applicableConditions = conditions.Where(c => c.Applicable).ToList();
        var failedConditions = applicableConditions.Where(c => !c.Passed).ToList();
        var nonApprovalFailures = failedConditions.Where(c => c.Number != 14).ToList();

        var failureReasons = failedConditions.Select(c => $"[C{c.Number}] {c.Summary}").ToList();

        GateOutcome outcome;
        var onlyApprovalMissing = false;

        if (!c15Pass || hasContestedHigh || c10Blocked)
        {
            outcome = new GateOutcome.Blocked(failureReasons);
        }
        else if (failedConditions.Count == 0)
        {
            outcome = new GateOutcome.Pass();
        }
        else if (nonApprovalFailures.Count == 0 && missingApprovals.Count > 0 && !isL0ShadowHold)
        {
            onlyApprovalMissing = true;
            outcome = new GateOutcome.OnlyApprovalMissing(missingApprovals);
        }
        else if (nonApprovalFailures.All(c => c.IsPending) && (c14Pass || isL0ShadowHold))
        {
            outcome = new GateOutcome.Pending(failedConditions.Select(c => c.Summary).ToList());
        }
        else
        {
            outcome = new GateOutcome.NeedsFix(failureReasons, protectedViolations);
        }

        var passed = outcome is GateOutcome.Pass && applicableConditions.All(c => c.Passed);

        return new GateEvaluationResult(
            Profile: input.Profile,
            EvaluatedSha: input.EvaluatedSha,
            Passed: passed,
            OnlyApprovalMissing: onlyApprovalMissing,
            HasContestedHigh: hasContestedHigh,
            ProtectedPathsTouched: protectedViolations,
            Outcome: outcome,
            Conditions: conditions,
            FailureReasons: failureReasons);
    }

    private static (bool Pass, bool Pending, string Message) EvaluateRequiredChecks(GateInput input)
    {
        var required = input.Policy.Gate.RequiredChecks;
        if (required.Count == 0)
            return (true, false, "No required checks configured.");

        var missingOrPending = new List<string>();
        var failedOrSpoofed = new List<string>();

        foreach (var checkName in required)
        {
            var matching = input.CheckRuns
                .Where(c => string.Equals(c.Name, checkName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(c.Sha, input.EvaluatedSha, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count == 0)
            {
                missingOrPending.Add(checkName);
                continue;
            }

            // §10.2 & §18 AC 19: Required checks must come from pinned source (GitHub Actions), never spoofed
            var trustedRuns = matching
                .Where(c => string.Equals(c.AppSlug, ExpectedActionsAppSlug, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (trustedRuns.Count == 0)
            {
                failedOrSpoofed.Add($"{checkName} (untrusted source '{matching[0].AppSlug}')");
                continue;
            }

            if (trustedRuns.Any(c => !string.Equals(c.Status, "completed", StringComparison.OrdinalIgnoreCase)))
            {
                missingOrPending.Add(checkName);
                continue;
            }

            if (!trustedRuns.All(c => string.Equals(c.Conclusion, "success", StringComparison.OrdinalIgnoreCase)))
            {
                var bad = trustedRuns.First(c => !string.Equals(c.Conclusion, "success", StringComparison.OrdinalIgnoreCase));
                failedOrSpoofed.Add($"{checkName} ({bad.Conclusion ?? "unknown"})");
            }
        }

        if (failedOrSpoofed.Count > 0)
            return (false, false, $"Required checks failed: {string.Join(", ", failedOrSpoofed)}.");

        if (missingOrPending.Count > 0)
            return (false, true, $"Waiting on required checks: {string.Join(", ", missingOrPending)}.");

        return (true, false, $"All {required.Count} source-pinned required checks passed.");
    }

    private static (bool Applicable, bool Pass, IReadOnlyList<string> Violations, string Message) EvaluateProtectedPaths(GateInput input)
    {
        if (input.Profile is GateProfile.TemplateUpgrade or GateProfile.Revert)
        {
            return (false, true, [], $"Exempt for '{input.Profile}' profile.");
        }

        if (input.Profile == GateProfile.Spec)
        {
            var nonDocs = input.ChangedFiles
                .Where(f => !PathGlobMatcher.IsDocsOnlyPath(f.Path))
                .Select(f => f.Path)
                .ToList();

            return nonDocs.Count == 0
                ? (true, true, [], "Spec PR modifies only docs/spec.md, docs/features/**, or docs/adr/**.")
                : (true, false, nonDocs, $"Spec PR touched non-doc paths: {string.Join(", ", nonDocs)}.");
        }

        if (input.Profile == GateProfile.Dependabot)
        {
            if (input.Dependabot is { Ecosystem: var eco }
                && string.Equals(eco, "github-actions", StringComparison.OrdinalIgnoreCase))
            {
                var validBump = PathGlobMatcher.IsAllowedGitHubActionsShaBump(input.ChangedFiles, input.Policy.AllowedGitHubActions);
                return validBump
                    ? (true, true, [], "Allowlisted GitHub Actions full-SHA pin bump verified.")
                    : (true, false, input.ChangedFiles.Select(f => f.Path).ToList(), "GitHub Actions workflow change is not a pure allowlisted 40-char SHA bump.");
            }
        }

        var patterns = new List<string>(input.Policy.ProtectedPaths);
        foreach (var mergedAtKey in input.MergedAcceptanceTestIssueKeys)
        {
            patterns.Add($"tests/acceptance/{mergedAtKey}/**");
        }

        // AcceptanceTest PRs are allowed to edit tests/acceptance/pending.json and their own new tests/acceptance/<NEW-ID>/**,
        // while _harness/** and already-merged AT directories remain strictly protected (§6.1 steps 2 & 4).
        var violations = input.ChangedFiles
            .Where(f => PathGlobMatcher.MatchesAny(f.Path, patterns))
            .Select(f => f.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return violations.Count == 0
            ? (true, true, [], "No protected paths modified.")
            : (true, false, violations, $"Protected path violation: {string.Join(", ", violations)}.");
    }

    private static (bool Applicable, bool Pass, bool Blocked, string Message) EvaluateDependencyCondition(GateInput input)
    {
        if (input.Profile is GateProfile.Revert or GateProfile.Spec)
            return (false, true, false, "Not applicable.");

        if (!input.DependencyReviewPassed)
            return (true, false, false, "Dependency review failed (vulnerable dependency or disallowed license).");

        if (input.Profile == GateProfile.Dependabot)
        {
            if (input.Dependabot is null)
                return (true, false, false, "Missing Dependabot metadata.");

            if (!input.Dependabot.DependencyReviewClean)
                return (true, false, false, "Dependabot dependency review reported vulnerabilities or disallowed licenses.");

            if (input.Dependabot.UpdateType == DependabotUpdateType.Major)
            {
                // §5.4: Major Dependabot PRs are closed and converted into a Copilot migration issue
                return (true, false, true, "Major version Dependabot update blocked by policy; convert to migration issue.");
            }

            if (input.Dependabot.UpdateType == DependabotUpdateType.None)
                return (true, false, false, "Unknown Dependabot update type.");
        }

        return (true, true, false, "Dependency review passed.");
    }

    private static (bool Applicable, bool Pass, string Message) EvaluateAcceptanceTestsCondition(
        GateInput input,
        bool hasModelEscalationApproval)
    {
        if (input.Profile == GateProfile.AcceptanceTest)
        {
            if (input.AtJUnit is null)
                return (true, false, "Missing JUnit report for acceptance-test PR.");

            if (!input.AtJUnit.HarnessSmokePassed)
                return (true, false, "Acceptance harness smoke test failed.");

            if (input.AtJUnit.NewTestCount <= 0)
                return (true, false, "Acceptance-test PR contains 0 new acceptance tests.");

            // §6.1 Step 3: Every new test reports a failure (assertion) and none reports an error (crash/timeout/missing fixture)
            if (input.AtJUnit.ErrorCount > 0 || input.AtJUnit.PassedCount > 0 || input.AtJUnit.FailureCount != input.AtJUnit.NewTestCount)
            {
                return (true, false,
                    $"AT tests must fail on assertions only before implementation (new={input.AtJUnit.NewTestCount}, failures={input.AtJUnit.FailureCount}, errors={input.AtJUnit.ErrorCount}, passed={input.AtJUnit.PassedCount}).");
            }

            if (input.AtMappingCheck.Executed && !input.AtMappingCheck.Passed && !hasModelEscalationApproval)
            {
                return (true, false, $"PM AT mapping check flagged missing criteria coverage: {string.Join("; ", input.AtMappingCheck.Reasons)}");
            }

            return (true, true, $"All {input.AtJUnit.NewTestCount} new acceptance tests fail cleanly on assertions; harness smoke passed.");
        }

        if (input.Profile == GateProfile.Feature)
        {
            if (input.FeatureAcceptance is null)
                return (true, false, "Missing feature acceptance-test evaluation summary.");

            var fa = input.FeatureAcceptance;
            if (!fa.SuitePassedOnHeadSha)
                return (true, false, $"Acceptance test suite for {fa.IssueKey} failed on head SHA.");

            if (fa.SeparateAtIssueRequired)
            {
                if (!fa.AtFilesByteIdenticalToMain)
                    return (true, false, $"Locked acceptance-test files under tests/acceptance/{fa.IssueKey}/ differ from main.");

                var onlyOwnIdRemoved = fa.AddedToPendingJson.Count == 0
                    && fa.RemovedFromPendingJson.All(id => string.Equals(id, fa.IssueKey, StringComparison.OrdinalIgnoreCase));

                if (!onlyOwnIdRemoved)
                    return (true, false, $"Invalid modification to tests/acceptance/pending.json (only '{fa.IssueKey}' may be removed).");
            }

            return (true, true, $"Acceptance suite for {fa.IssueKey} passed and test integrity verified.");
        }

        return (false, true, "Exempt for profile.");
    }

    private static (bool Pass, bool Pending, string Message) EvaluatePreviewCondition(bool applicable, GateInput input)
    {
        if (!applicable)
            return (true, false, "Preview check not applicable.");

        if (input.PreviewRun is null || !input.PreviewRun.Exists)
            return (false, true, "Waiting for trusted preview.yml workflow_run.");

        var pr = input.PreviewRun;
        // §6.4: Accept run ONLY if workflow file is preview.yml, event is workflow_run, and ref is main
        var isTrustedWorkflow = string.Equals(PathGlobMatcher.NormalizePath(pr.WorkflowFile), ".github/workflows/preview.yml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pr.WorkflowFile, "preview.yml", StringComparison.OrdinalIgnoreCase);

        if (!isTrustedWorkflow
            || !string.Equals(pr.EventName, "workflow_run", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pr.RefBranch, "main", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pr.HeadSha, input.EvaluatedSha, StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, $"Untrusted or mismatched preview run (workflow='{pr.WorkflowFile}', event='{pr.EventName}', ref='{pr.RefBranch}').");
        }

        return pr.Succeeded
            ? (true, false, $"Trusted preview deployed and verified ({pr.PreviewUrl ?? "url recorded"}).")
            : (false, false, "Preview deployment or preview acceptance run failed.");
    }

    private static List<ApprovalKind> DetermineRequiredApprovals(GateInput input)
    {
        var required = new List<ApprovalKind>();

        if (input.Profile == GateProfile.TemplateUpgrade)
            required.Add(ApprovalKind.TemplateUpgrade);

        if (input.EffectiveAutonomy == AutonomyLevel.L1)
        {
            if (input.Profile == GateProfile.AcceptanceTest)
                required.Add(ApprovalKind.AcceptanceTest);

            var hasRiskLabel = input.Labels.Any(l =>
                input.Policy.RiskLabels.Any(r => string.Equals(l, r, StringComparison.OrdinalIgnoreCase)));
            if (hasRiskLabel)
                required.Add(ApprovalKind.L1Risk);
        }

        // §8.3 Model checks: 1 fix round on failure; if it fails again (FailedRounds >= 2), require human approval
        var modelCheckEscalated =
            (input.SpecConformanceCheck is { Executed: true, Passed: false, FailedRounds: >= 2 })
            || (input.AtMappingCheck is { Executed: true, Passed: false, FailedRounds: >= 2 })
            || (input.Profile == GateProfile.Spec && input.PublicSafeCheck is { Executed: true, Passed: false });

        if (modelCheckEscalated)
            required.Add(ApprovalKind.ModelCheckEscalation);

        if (input.Profile == GateProfile.Spec)
            required.Add(ApprovalKind.SpecChange);

        return required.Distinct().ToList();
    }

    private static (bool Applicable, bool Pass, IReadOnlyList<ApprovalKind> Missing, bool IsL0ShadowHold, string Message)
        EvaluateApprovalAndAutonomyCondition(GateInput input, IReadOnlyList<ApprovalKind> requiredApprovals)
    {
        // §11.3: L0 Shadow opens PRs but NEVER enables auto-merge
        if (input.EffectiveAutonomy == AutonomyLevel.L0)
        {
            return (true, false, [], true, "Autonomy L0 (Shadow mode): auto-merge is disabled.");
        }

        // Spec profile public-safe check (§6.3, §8.3)
        if (input.Profile == GateProfile.Spec && input.PublicSafeCheck is { Executed: true, Passed: false })
        {
            if (!HasValidApproval(input.Approvals, ApprovalKind.ModelCheckEscalation, input.CurrentPrHeadSha)
                && !HasValidApproval(input.Approvals, ApprovalKind.SpecChange, input.CurrentPrHeadSha))
            {
                return (true, false, [ApprovalKind.SpecChange], false,
                    $"Public-safe check flagged sensitive items requiring explicit owner dismissal: {string.Join("; ", input.PublicSafeCheck.Reasons)}");
            }
        }

        if (requiredApprovals.Count == 0)
            return (true, true, [], false, "No additional UI approval required for current autonomy and profile.");

        var missing = requiredApprovals
            .Where(kind => !HasValidApproval(input.Approvals, kind, input.CurrentPrHeadSha))
            .ToList();

        return missing.Count == 0
            ? (true, true, [], false, $"Recorded UI approval(s) verified for SHA {ShortSha(input.CurrentPrHeadSha)}.")
            : (true, false, missing, false, $"Waiting for operator approval in UI ({string.Join(", ", missing)}) on SHA {ShortSha(input.CurrentPrHeadSha)}.");
    }

    private static bool HasValidApproval(IReadOnlyList<RecordedApproval> approvals, ApprovalKind kind, string headSha) =>
        approvals.Any(a =>
            a.Kind == kind
            && a.Decision == ApprovalDecision.Approved
            && string.Equals(a.HeadSha, headSha, StringComparison.OrdinalIgnoreCase));

    private static string ShortSha(string sha) =>
        string.IsNullOrWhiteSpace(sha) ? "(empty)" : sha[..Math.Min(7, sha.Length)];
}
