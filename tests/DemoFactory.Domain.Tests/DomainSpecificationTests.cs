using DemoFactory.Domain;
using FsCheck;
using FsCheck.Xunit;

namespace DemoFactory.Domain.Tests;

public sealed class DomainSpecificationTests
{
    private static GateInput CreatePassingFeatureGateInput()
    {
        var policy = new RepoPolicy();
        const string sha = "abc1234567890def";
        var checks = policy.Gate.RequiredChecks
            .Select(name => new CheckRunSnapshot(name, sha, "completed", "success", "github-actions"))
            .ToList();

        return new GateInput(
            Profile: GateProfile.Feature,
            EvaluatedSha: sha,
            CurrentPrHeadSha: sha,
            Policy: policy,
            EffectiveAutonomy: AutonomyLevel.L2,
            CheckRuns: checks,
            HasCopilotReviewOnHeadSha: true,
            CarryOverCopilotReviewFromUnchangedDiff: false,
            Findings: [],
            ChangedFiles: [new ChangedFileDiff("src/App/Feature.cs", 40, 10)],
            MergedAcceptanceTestIssueKeys: ["ISS-4"],
            Labels: ["factory"],
            SpecConformanceCheck: ModelCheckVerdict.Pass(),
            AtMappingCheck: ModelCheckVerdict.Pass(),
            PublicSafeCheck: ModelCheckVerdict.Pass(),
            CodeScanningCompletedOnHeadSha: true,
            NewCodeScanningAlertsAboveThreshold: 0,
            DependencyReviewPassed: true,
            Dependabot: null,
            AtJUnit: null,
            FeatureAcceptance: new FeatureAcceptanceSummary(
                "ISS-5",
                SeparateAtIssueRequired: true,
                SuitePassedOnHeadSha: true,
                AtFilesByteIdenticalToMain: true,
                AddedToPendingJson: [],
                RemovedFromPendingJson: ["ISS-5"]),
            PreviewRun: new PreviewWorkflowSummary(
                true, ".github/workflows/preview.yml", "workflow_run", "main", sha, true, "https://pr-105.example.io"),
            LineStatus: LineStatus.Running,
            Approvals: [],
            Origin: new TrustedOriginContext(
                "azureholic/demo",
                "azureholic/demo",
                IsFork: false,
                PrAuthor: "copilot-swe-agent[bot]",
                OwnerLogin: "azureholic",
                FactoryAppSlug: "demo-factory",
                LinkedIssueWorkLabel: new WorkLabelContext(
                    "azureholic", ["factory"], "azureholic", "factory", "azureholic", "demo-factory")));
    }

    [Fact]
    public void Gate_Passes_When_All_15_Conditions_Hold()
    {
        var input = CreatePassingFeatureGateInput();
        var result = ReviewGateEvaluator.Evaluate(input);

        Assert.True(result.Passed);
        Assert.IsType<GateOutcome.Pass>(result.Outcome);
        Assert.Empty(result.FailureReasons);
    }

    /// <summary>
    /// §1, §8.3, §18 AC 21: Property test proving that model checks NEVER make the gate pass
    /// when ANY deterministic condition fails.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Property_ModelCheckPass_Never_Overrides_Deterministic_Failure(PositiveInt mutationSelector)
    {
        var baseInput = CreatePassingFeatureGateInput();
        var mode = mutationSelector.Get % 8;

        var brokenInput = mode switch
        {
            0 => baseInput with { EvaluatedSha = "stale0000" }, // C1
            1 => baseInput with { CheckRuns = [new CheckRunSnapshot("build", baseInput.EvaluatedSha, "completed", "failure", "github-actions")] }, // C2
            2 => baseInput with { HasCopilotReviewOnHeadSha = false }, // C3
            3 => baseInput with { ChangedFiles = [new ChangedFileDiff(".github/workflows/ci.yml", 2, 1)] }, // C6 protected path
            4 => baseInput with { ChangedFiles = [new ChangedFileDiff("src/Huge.cs", 1400, 300)] }, // C7 diff size > 1500
            5 => baseInput with { NewCodeScanningAlertsAboveThreshold = 1 }, // C9
            6 => baseInput with { LineStatus = LineStatus.LineStopped }, // C13
            _ => baseInput with { Origin = baseInput.Origin with { IsFork = true, HeadRepoFullName = "attacker/demo" } } // C15
        };

        // Even with every single model check explicitly returning Pass()
        var withAllModelChecksGreen = brokenInput with
        {
            SpecConformanceCheck = ModelCheckVerdict.Pass(),
            AtMappingCheck = ModelCheckVerdict.Pass(),
            PublicSafeCheck = ModelCheckVerdict.Pass()
        };

        var eval = ReviewGateEvaluator.Evaluate(withAllModelChecksGreen);
        return !eval.Passed;
    }

    [Fact]
    public void Gate_Rejects_Spoofed_Check_Source_And_Locked_Acceptance_Tests()
    {
        // §18 AC 19: Check posted by non-GitHub-Actions source does not satisfy Condition 2
        var spoofedChecksInput = CreatePassingFeatureGateInput() with
        {
            CheckRuns =
            [
                new CheckRunSnapshot("build", "abc1234567890def", "completed", "success", "untrusted-third-party-app"),
                new CheckRunSnapshot("test", "abc1234567890def", "completed", "success", "github-actions"),
                new CheckRunSnapshot("lint", "abc1234567890def", "completed", "success", "github-actions")
            ]
        };
        var spoofEval = ReviewGateEvaluator.Evaluate(spoofedChecksInput);
        Assert.False(spoofEval.Passed);
        Assert.False(spoofEval.Conditions.Single(c => c.Number == 2).Passed);

        // §18 AC 4 & AC 13: Modifying locked acceptance test directory ISS-4 fails Condition 6
        var lockedAtModified = CreatePassingFeatureGateInput() with
        {
            ChangedFiles = [new ChangedFileDiff("tests/acceptance/ISS-4/alert.spec.ts", 1, 1)]
        };
        var lockedEval = ReviewGateEvaluator.Evaluate(lockedAtModified);
        Assert.False(lockedEval.Passed);
        Assert.Contains("tests/acceptance/ISS-4/alert.spec.ts", lockedEval.ProtectedPathsTouched);
    }

    [Fact]
    public void Gate_LineStopped_Allows_Revert_And_Blocks_Feature_Prs()
    {
        // §6.2 & §18 AC 20
        var featureDuringStop = CreatePassingFeatureGateInput() with { LineStatus = LineStatus.LineStopped };
        var featureEval = ReviewGateEvaluator.Evaluate(featureDuringStop);
        Assert.False(featureEval.Passed);
        Assert.IsType<GateOutcome.Pending>(featureEval.Outcome);

        var revertDuringStop = CreatePassingFeatureGateInput() with
        {
            Profile = GateProfile.Revert,
            LineStatus = LineStatus.LineStopped
        };
        var revertEval = ReviewGateEvaluator.Evaluate(revertDuringStop);
        Assert.True(revertEval.Passed);
    }

    [Fact]
    public void Gate_AcceptanceTest_Profile_Requires_Assertion_Failures_With_Zero_Errors()
    {
        // §6.1 Step 3
        var atPass = CreatePassingFeatureGateInput() with
        {
            Profile = GateProfile.AcceptanceTest,
            EffectiveAutonomy = AutonomyLevel.L2,
            AtJUnit = new AcceptanceTestJUnitSummary(HarnessSmokePassed: true, NewTestCount: 3, FailureCount: 3, ErrorCount: 0, PassedCount: 0)
        };
        Assert.True(ReviewGateEvaluator.Evaluate(atPass).Passed);

        var atCrashError = atPass with
        {
            AtJUnit = new AcceptanceTestJUnitSummary(HarnessSmokePassed: true, NewTestCount: 3, FailureCount: 2, ErrorCount: 1, PassedCount: 0)
        };
        Assert.False(ReviewGateEvaluator.Evaluate(atCrashError).Passed);
    }

    [Fact]
    public void Gate_L1_RiskLabel_Yields_OnlyApprovalMissing_Until_Approved_On_Exact_HeadSha()
    {
        // §8.3 Condition 14 & §11.3
        var l1Risk = CreatePassingFeatureGateInput() with
        {
            EffectiveAutonomy = AutonomyLevel.L1,
            Labels = ["factory", "area:auth"]
        };

        var unapproved = ReviewGateEvaluator.Evaluate(l1Risk);
        Assert.False(unapproved.Passed);
        Assert.True(unapproved.OnlyApprovalMissing);

        // Stale SHA approval does not count
        var staleApproval = l1Risk with
        {
            Approvals = [new RecordedApproval(ApprovalKind.L1Risk, "old_sha", ApprovalDecision.Approved, "azureholic")]
        };
        Assert.False(ReviewGateEvaluator.Evaluate(staleApproval).Passed);

        // Matching SHA approval satisfies C14
        var matchingApproval = l1Risk with
        {
            Approvals = [new RecordedApproval(ApprovalKind.L1Risk, l1Risk.CurrentPrHeadSha, ApprovalDecision.Approved, "azureholic")]
        };
        Assert.True(ReviewGateEvaluator.Evaluate(matchingApproval).Passed);
    }

    [Fact]
    public void WorkLabel_And_TrustBoundary_Enforce_All_Public_Repo_Rules()
    {
        // §5.6 & §18 AC 25–26
        var missingLabel = TrustBoundary.EvaluateWorkLabelRule(new WorkLabelContext(
            "azureholic", ["bug"], null, "factory", "azureholic", "demo-factory"));
        Assert.False(missingLabel.Passed);
        Assert.Equal("MissingWorkLabel", missingLabel.FailureCode);

        // Even if owner labels an outside user's issue, it CANNOT be built directly
        var outsideAuthor = TrustBoundary.EvaluateWorkLabelRule(new WorkLabelContext(
            "random-contributor", ["factory"], "azureholic", "factory", "azureholic", "demo-factory"));
        Assert.False(outsideAuthor.Passed);
        Assert.Equal("OutsideAuthorRequiresFeature", outsideAuthor.FailureCode);

        // Issue form / GitHub Actions bot applying the label is rejected
        var botLabeler = TrustBoundary.EvaluateWorkLabelRule(new WorkLabelContext(
            "azureholic", ["factory"], "github-actions[bot]", "factory", "azureholic", "demo-factory"));
        Assert.False(botLabeler.Passed);
        Assert.Equal("UntrustedLabeler", botLabeler.FailureCode);

        // Owner issue + owner/factory label -> allowed
        var valid = TrustBoundary.EvaluateWorkLabelRule(new WorkLabelContext(
            "azureholic", ["factory"], "demo-factory[bot]", "factory", "azureholic", "demo-factory"));
        Assert.True(valid.Passed);

        // §6.1 Step 6: AT-DEFECT only counts from Copilot coding agent
        Assert.True(TrustBoundary.IsValidAtDefectComment("copilot-swe-agent[bot]", "Found AT-DEFECT in selector"));
        Assert.False(TrustBoundary.IsValidAtDefectComment("outside-user", "AT-DEFECT please change test"));
    }

    [Fact]
    public void FindingTracker_Handles_LineShift_And_Transitions_Open_Repeated_Contested()
    {
        // §8.4: Non-Copilot review comments are ignored; +/-10 line shift keeps same fingerprint & advances status
        var r1 = FindingTracker.ReconcileRound(1, [],
        [
            new RawReviewComment("copilot-pull-request-reviewer[bot]", "src/Auth.cs", 42, null, "[Medium] Validate token audience before parsing.", FindingSeverity.Medium, "jwt-aud"),
            new RawReviewComment("random-human", "src/Auth.cs", 10, null, "[High] Rewrite everything.", FindingSeverity.High, null)
        ]);
        Assert.Single(r1);
        Assert.Equal(FindingStatus.Open, r1[0].Status);

        // Round 2: same comment shifted +6 lines -> Repeated
        var r2 = FindingTracker.ReconcileRound(2, r1,
        [
            new RawReviewComment("copilot-pull-request-reviewer[bot]", "src/Auth.cs", 48, null, "[Medium] Validate token audience before parsing.", FindingSeverity.Medium, "jwt-aud")
        ]);
        Assert.Single(r2);
        Assert.Equal(FindingStatus.Repeated, r2[0].Status);

        // Round 3: raised again -> Contested
        var r3 = FindingTracker.ReconcileRound(3, r2,
        [
            new RawReviewComment("copilot-pull-request-reviewer[bot]", "src/Auth.cs", 50, null, "[Medium] Validate token audience before parsing.", FindingSeverity.Medium, "jwt-aud")
        ]);
        Assert.Single(r3);
        Assert.Equal(FindingStatus.Contested, r3[0].Status);
    }

    [Fact]
    public void SchedulerEngine_Detects_Tarjan_Cycles_Ranks_Critical_Path_And_Avoids_TouchSet_Overlap()
    {
        var label = new WorkLabelContext("azureholic", ["factory"], "azureholic", "factory", "azureholic", "demo-factory");
        var now = DateTimeOffset.UtcNow;

        var candidates = new List<SchedulerCandidate>
        {
            // Cycle between #20 and #21
            new(Guid.NewGuid(), 20, "Cycle A", BoardStatus.Ready, false, false, PriorityLevel.Critical, ItemSize.Small, now, label,
                [new DependencyBlocker("o/r", 21, true)], ["src/A/"]),
            new(Guid.NewGuid(), 21, "Cycle B", BoardStatus.Ready, false, false, PriorityLevel.Critical, ItemSize.Small, now, label,
                [new DependencyBlocker("o/r", 20, true)], ["src/B/"]),

            // #10 unblocks #11 and #12 (unblockCount = 2), same Priority as #15 (unblockCount = 0) -> #10 must rank ahead!
            new(Guid.NewGuid(), 15, "Standalone High", BoardStatus.Ready, false, false, PriorityLevel.High, ItemSize.Small, now.AddHours(-5), label,
                [], ["src/Standalone/"]),
            new(Guid.NewGuid(), 10, "Root Blocker High", BoardStatus.Ready, false, false, PriorityLevel.High, ItemSize.Large, now, label,
                [], ["src/Core/"]),
            new(Guid.NewGuid(), 11, "Dependent 1", BoardStatus.Ready, false, false, PriorityLevel.High, ItemSize.Small, now, label,
                [new DependencyBlocker("o/r", 10, true)], ["src/Dep1/"]),
            new(Guid.NewGuid(), 12, "Dependent 2", BoardStatus.Ready, false, false, PriorityLevel.Medium, ItemSize.Small, now, label,
                [new DependencyBlocker("o/r", 11, true)], ["src/Dep2/"])
        };

        var res = SchedulerEngine.SelectNextItems("o/r", candidates, [], wipLimit: 1);

        Assert.Equal([20, 21], res.CycleIssueNumbers);
        Assert.Single(res.SelectedToStart);
        Assert.Equal(10, res.SelectedToStart[0].Candidate.IssueNumber);
        Assert.Equal(2, res.SelectedToStart[0].TransitiveUnblockCount);

        // WIP=2 with touch-set overlap: if #10 is in flight touching src/Core/, another item touching src/Core/Auth.cs is skipped
        var overlapping = new SchedulerCandidate(
            Guid.NewGuid(), 16, "Overlapping Core", BoardStatus.Ready, false, false, PriorityLevel.Critical, ItemSize.Small, now, label,
            [], ["src/Core/Auth.cs"]);

        var wip2 = SchedulerEngine.SelectNextItems(
            "o/r",
            [overlapping, candidates[2]],
            [new InFlightWorkItem(Guid.NewGuid(), 10, ["src/Core/"])],
            wipLimit: 2);

        Assert.Single(wip2.SelectedToStart);
        Assert.Equal(15, wip2.SelectedToStart[0].Candidate.IssueNumber);
        Assert.Equal("TouchSetConflict", wip2.SkippedReasons[16]);
    }

    [Fact]
    public void StateMachine_Enforces_Board_Columns_And_Human_Drag_Boundaries()
    {
        Assert.True(WorkItemStateMachine.CanHumanMoveCard(BoardStatus.Backlog, BoardStatus.Ready));
        Assert.True(WorkItemStateMachine.CanHumanMoveCard(BoardStatus.Blocked, BoardStatus.Ready));
        Assert.False(WorkItemStateMachine.CanHumanMoveCard(BoardStatus.Ready, BoardStatus.InProgress));
        Assert.False(WorkItemStateMachine.CanHumanMoveCard(BoardStatus.InReview, BoardStatus.Done));

        var s0 = new WorkItemState.Queued();
        var t1 = Assert.IsType<TransitionOutcome.Allowed>(WorkItemStateMachine.Transition(s0, new WorkItemTrigger.SelectForSpecReview()));
        Assert.Equal(BoardStatus.Ready, t1.BoardStatus);

        var t2 = Assert.IsType<TransitionOutcome.Allowed>(WorkItemStateMachine.Transition(t1.NextState, new WorkItemTrigger.ClaimSlot()));
        Assert.Equal(BoardStatus.InProgress, t2.BoardStatus);
    }
}
