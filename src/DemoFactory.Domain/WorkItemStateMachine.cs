namespace DemoFactory.Domain;

/// <summary>
/// Closed hierarchy of factory work-item states (§7.1).
/// Private base constructor guarantees exhaustive pattern matching across all 14 states.
/// </summary>
public abstract record WorkItemState
{
    private WorkItemState() { }

    public abstract WorkItemStateKind Kind { get; }

    public BoardStatus BoardColumn => WorkItemStateMachine.ToBoardStatus(this);

    public bool IsTerminal => this is Merged or Cancelled;

    public sealed record Queued : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Queued;
    }

    public sealed record SpecReview : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.SpecReview;
    }

    public sealed record NeedsClarification(IReadOnlyList<string> Questions) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.NeedsClarification;
    }

    public sealed record Claimed : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Claimed;
    }

    public sealed record Implementing(string Model, string? CopilotTaskId = null) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Implementing;
    }

    public sealed record PrOpen(int PrNumber, string HeadSha) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.PrOpen;
    }

    public sealed record Validating(int PrNumber, string HeadSha, int Round) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Validating;
    }

    public sealed record Fixing(int PrNumber, string HeadSha, int Round) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Fixing;
    }

    public sealed record AwaitingApproval(int PrNumber, string HeadSha, ApprovalKind ApprovalKind) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.AwaitingApproval;
    }

    public sealed record AwaitingMerge(int PrNumber, string HeadSha) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.AwaitingMerge;
    }

    public sealed record Merged(int PrNumber, string MergeSha) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Merged;
    }

    public sealed record Blocked(string Reason) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Blocked;
    }

    public sealed record Failed(string Error) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Failed;
    }

    public sealed record Cancelled(string Reason) : WorkItemState
    {
        public override WorkItemStateKind Kind => WorkItemStateKind.Cancelled;
    }

    public static WorkItemState FromKind(WorkItemStateKind kind, int? prNumber = null, string? headSha = null, int round = 1, string? reason = null) =>
        kind switch
        {
            WorkItemStateKind.Queued => new Queued(),
            WorkItemStateKind.SpecReview => new SpecReview(),
            WorkItemStateKind.NeedsClarification => new NeedsClarification(reason is null ? [] : [reason]),
            WorkItemStateKind.Claimed => new Claimed(),
            WorkItemStateKind.Implementing => new Implementing("gemini-4-carbon"),
            WorkItemStateKind.PrOpen => new PrOpen(prNumber ?? 0, headSha ?? string.Empty),
            WorkItemStateKind.Validating => new Validating(prNumber ?? 0, headSha ?? string.Empty, round),
            WorkItemStateKind.Fixing => new Fixing(prNumber ?? 0, headSha ?? string.Empty, round),
            WorkItemStateKind.AwaitingApproval => new AwaitingApproval(prNumber ?? 0, headSha ?? string.Empty, ApprovalKind.L1Risk),
            WorkItemStateKind.AwaitingMerge => new AwaitingMerge(prNumber ?? 0, headSha ?? string.Empty),
            WorkItemStateKind.Merged => new Merged(prNumber ?? 0, headSha ?? string.Empty),
            WorkItemStateKind.Blocked => new Blocked(reason ?? "Blocked"),
            WorkItemStateKind.Failed => new Failed(reason ?? "Failed"),
            WorkItemStateKind.Cancelled => new Cancelled(reason ?? "Cancelled"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown state kind")
        };
}

/// <summary>
/// Closed hierarchy of deterministic state transitions (§7.1).
/// </summary>
public abstract record WorkItemTrigger
{
    private WorkItemTrigger() { }

    public sealed record SelectForSpecReview : WorkItemTrigger;
    public sealed record ClarifySpec(IReadOnlyList<string> Questions) : WorkItemTrigger;
    public sealed record ClaimSlot : WorkItemTrigger;
    public sealed record IssueEdited : WorkItemTrigger;
    public sealed record AssignToCopilot(string Model, string? CopilotTaskId = null) : WorkItemTrigger;
    public sealed record PrReady(int PrNumber, string HeadSha) : WorkItemTrigger;
    public sealed record StartValidation(int Round = 1) : WorkItemTrigger;
    public sealed record RequestFix(string NextHeadSha, int NextRound) : WorkItemTrigger;
    public sealed record FixFinished(string NewHeadSha) : WorkItemTrigger;
    public sealed record RequireApproval(ApprovalKind Kind) : WorkItemTrigger;
    public sealed record HumanApproved : WorkItemTrigger;
    public sealed record HumanRequestedChanges : WorkItemTrigger;
    public sealed record GateGreen : WorkItemTrigger;
    public sealed record NewPushOnPr(string NewHeadSha, int Round) : WorkItemTrigger;
    public sealed record PrMerged(string MergeSha) : WorkItemTrigger;
    public sealed record Block(string Reason) : WorkItemTrigger;
    public sealed record Fail(string Error) : WorkItemTrigger;
    public sealed record RequeueByHuman : WorkItemTrigger;
    public sealed record Cancel(string Reason) : WorkItemTrigger;
}

public abstract record TransitionOutcome
{
    private TransitionOutcome() { }

    public sealed record Allowed(WorkItemState NextState, BoardStatus BoardStatus) : TransitionOutcome;
    public sealed record Rejected(WorkItemState CurrentState, string Reason) : TransitionOutcome;
}

public static class WorkItemStateMachine
{
    public static BoardStatus ToBoardStatus(WorkItemState state) => state switch
    {
        WorkItemState.Queued => BoardStatus.Ready,
        WorkItemState.SpecReview => BoardStatus.Ready,
        WorkItemState.NeedsClarification => BoardStatus.Blocked,
        WorkItemState.Claimed => BoardStatus.InProgress,
        WorkItemState.Implementing => BoardStatus.InProgress,
        WorkItemState.PrOpen => BoardStatus.InReview,
        WorkItemState.Validating => BoardStatus.InReview,
        WorkItemState.Fixing => BoardStatus.InReview,
        WorkItemState.AwaitingApproval => BoardStatus.InReview,
        WorkItemState.AwaitingMerge => BoardStatus.InReview,
        WorkItemState.Merged => BoardStatus.Done,
        WorkItemState.Blocked => BoardStatus.Blocked,
        WorkItemState.Failed => BoardStatus.Blocked,
        WorkItemState.Cancelled => BoardStatus.Backlog,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static BoardStatus ToBoardStatus(WorkItemStateKind kind) =>
        ToBoardStatus(WorkItemState.FromKind(kind));

    /// <summary>
    /// Humans may drag cards only between Backlog, Ready, and Blocked (§14.2 Screen 2).
    /// Agent-owned columns (InProgress, InReview, Done) are locked.
    /// </summary>
    public static bool CanHumanMoveCard(BoardStatus from, BoardStatus to)
    {
        static bool IsHumanColumn(BoardStatus s) =>
            s is BoardStatus.Backlog or BoardStatus.Ready or BoardStatus.Blocked;

        return IsHumanColumn(from) && IsHumanColumn(to);
    }

    public static TransitionOutcome Transition(WorkItemState current, WorkItemTrigger trigger)
    {
        // Any non-terminal state can be cancelled (e.g., operator cancel or work label removed §5.6 / §7.1)
        if (trigger is WorkItemTrigger.Cancel c && !current.IsTerminal)
        {
            var cancelled = new WorkItemState.Cancelled(c.Reason);
            return new TransitionOutcome.Allowed(cancelled, ToBoardStatus(cancelled));
        }

        WorkItemState? next = (current, trigger) switch
        {
            (WorkItemState.Queued, WorkItemTrigger.SelectForSpecReview)
                => new WorkItemState.SpecReview(),

            (WorkItemState.Queued, WorkItemTrigger.Block b)
                => new WorkItemState.Blocked(b.Reason),

            (WorkItemState.SpecReview, WorkItemTrigger.ClarifySpec cs)
                => new WorkItemState.NeedsClarification(cs.Questions),

            (WorkItemState.SpecReview, WorkItemTrigger.ClaimSlot)
                => new WorkItemState.Claimed(),

            (WorkItemState.NeedsClarification, WorkItemTrigger.IssueEdited)
                => new WorkItemState.Queued(),

            (WorkItemState.NeedsClarification, WorkItemTrigger.RequeueByHuman)
                => new WorkItemState.Queued(),

            (WorkItemState.Claimed, WorkItemTrigger.AssignToCopilot a)
                => new WorkItemState.Implementing(a.Model, a.CopilotTaskId),

            (WorkItemState.Claimed, WorkItemTrigger.Fail f)
                => new WorkItemState.Failed(f.Error),

            (WorkItemState.Implementing, WorkItemTrigger.PrReady pr)
                => new WorkItemState.PrOpen(pr.PrNumber, pr.HeadSha),

            (WorkItemState.Implementing, WorkItemTrigger.ClarifySpec cs)
                => new WorkItemState.NeedsClarification(cs.Questions),

            (WorkItemState.Implementing, WorkItemTrigger.Fail f)
                => new WorkItemState.Failed(f.Error),

            (WorkItemState.PrOpen pr, WorkItemTrigger.StartValidation sv)
                => new WorkItemState.Validating(pr.PrNumber, pr.HeadSha, sv.Round),

            (WorkItemState.Validating v, WorkItemTrigger.GateGreen)
                => new WorkItemState.AwaitingMerge(v.PrNumber, v.HeadSha),

            (WorkItemState.Validating v, WorkItemTrigger.RequireApproval ra)
                => new WorkItemState.AwaitingApproval(v.PrNumber, v.HeadSha, ra.Kind),

            (WorkItemState.Validating v, WorkItemTrigger.RequestFix rf)
                => new WorkItemState.Fixing(v.PrNumber, rf.NextHeadSha, rf.NextRound),

            (WorkItemState.Validating, WorkItemTrigger.Block b)
                => new WorkItemState.Blocked(b.Reason),

            (WorkItemState.Validating, WorkItemTrigger.Fail f)
                => new WorkItemState.Failed(f.Error),

            (WorkItemState.Fixing fx, WorkItemTrigger.FixFinished ff)
                => new WorkItemState.Validating(fx.PrNumber, ff.NewHeadSha, fx.Round),

            (WorkItemState.Fixing, WorkItemTrigger.ClarifySpec cs)
                => new WorkItemState.NeedsClarification(cs.Questions),

            (WorkItemState.Fixing, WorkItemTrigger.Fail f)
                => new WorkItemState.Failed(f.Error),

            (WorkItemState.Fixing, WorkItemTrigger.Block b)
                => new WorkItemState.Blocked(b.Reason),

            (WorkItemState.AwaitingApproval aa, WorkItemTrigger.HumanApproved)
                => new WorkItemState.AwaitingMerge(aa.PrNumber, aa.HeadSha),

            (WorkItemState.AwaitingApproval aa, WorkItemTrigger.HumanRequestedChanges)
                => new WorkItemState.Fixing(aa.PrNumber, aa.HeadSha, 1),

            (WorkItemState.AwaitingApproval aa, WorkItemTrigger.NewPushOnPr np)
                => new WorkItemState.Validating(aa.PrNumber, np.NewHeadSha, np.Round),

            (WorkItemState.AwaitingMerge am, WorkItemTrigger.PrMerged pm)
                => new WorkItemState.Merged(am.PrNumber, pm.MergeSha),

            (WorkItemState.AwaitingMerge am, WorkItemTrigger.NewPushOnPr np)
                => new WorkItemState.Validating(am.PrNumber, np.NewHeadSha, np.Round),

            (WorkItemState.AwaitingMerge, WorkItemTrigger.Block b)
                => new WorkItemState.Blocked(b.Reason),

            (WorkItemState.Blocked, WorkItemTrigger.RequeueByHuman)
                => new WorkItemState.Queued(),

            (WorkItemState.Failed, WorkItemTrigger.RequeueByHuman)
                => new WorkItemState.Queued(),

            _ => null
        };

        return next is not null
            ? new TransitionOutcome.Allowed(next, ToBoardStatus(next))
            : new TransitionOutcome.Rejected(current, $"Transition {trigger.GetType().Name} is invalid from state {current.Kind}.");
    }
}
