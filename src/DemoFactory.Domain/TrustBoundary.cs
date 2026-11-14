namespace DemoFactory.Domain;

public sealed record WorkLabelContext(
    string IssueAuthor,
    IReadOnlyList<string> CurrentLabels,
    string? WorkLabelAppliedBy,
    string ExpectedWorkLabel,
    string OwnerLogin,
    string FactoryAppSlug);

public sealed record WorkLabelCheckResult(
    bool Passed,
    string? FailureCode,
    string Reason)
{
    public static WorkLabelCheckResult Allowed() =>
        new(true, null, "Issue carries the work label applied by a trusted owner/factory identity and was authored by owner/factory.");
}

public sealed record TrustedOriginContext(
    string BaseRepoFullName,
    string HeadRepoFullName,
    bool IsFork,
    string PrAuthor,
    string OwnerLogin,
    string FactoryAppSlug,
    WorkLabelContext? LinkedIssueWorkLabel = null);

public static class TrustBoundary
{
    private static readonly HashSet<string> CopilotCodingLogins = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot-swe-agent",
        "copilot-swe-agent[bot]",
        "Copilot"
    };

    private static readonly HashSet<string> CopilotReviewerLogins = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot-pull-request-reviewer",
        "copilot-pull-request-reviewer[bot]"
    };

    private static readonly HashSet<string> DependabotLogins = new(StringComparer.OrdinalIgnoreCase)
    {
        "dependabot",
        "dependabot[bot]"
    };

    public static bool IsOwnerOrFactory(string? login, string ownerLogin, string factoryAppSlug)
    {
        if (string.IsNullOrWhiteSpace(login))
            return false;

        return string.Equals(login, ownerLogin, StringComparison.OrdinalIgnoreCase)
            || string.Equals(login, factoryAppSlug, StringComparison.OrdinalIgnoreCase)
            || string.Equals(login, $"{factoryAppSlug}[bot]", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCopilotCodingAgent(string? login) =>
        !string.IsNullOrWhiteSpace(login) && CopilotCodingLogins.Contains(login);

    public static bool IsCopilotReviewer(string? login) =>
        !string.IsNullOrWhiteSpace(login) && CopilotReviewerLogins.Contains(login);

    public static bool IsDependabot(string? login) =>
        !string.IsNullOrWhiteSpace(login) && DependabotLogins.Contains(login);

    public static bool IsTrustedActor(string? login, string ownerLogin, string factoryAppSlug) =>
        IsOwnerOrFactory(login, ownerLogin, factoryAppSlug)
        || IsCopilotCodingAgent(login)
        || IsCopilotReviewer(login)
        || IsDependabot(login);

    /// <summary>
    /// Enforces §5.6 and §18 AC 25–26:
    /// 1) Issue carries the configured work label.
    /// 2) The work label was applied by the owner or the factory (not issue forms, GitHub Actions, or outside users).
    /// 3) The issue was created by the owner or the factory (outside issues must go through Create Feature).
    /// </summary>
    public static WorkLabelCheckResult EvaluateWorkLabelRule(WorkLabelContext context)
    {
        var workLabel = string.IsNullOrWhiteSpace(context.ExpectedWorkLabel) ? "factory" : context.ExpectedWorkLabel;

        var hasLabel = context.CurrentLabels.Any(l => string.Equals(l, workLabel, StringComparison.OrdinalIgnoreCase));
        if (!hasLabel)
        {
            return new WorkLabelCheckResult(
                false,
                "MissingWorkLabel",
                $"Issue does not carry the '{workLabel}' work label.");
        }

        if (!IsOwnerOrFactory(context.IssueAuthor, context.OwnerLogin, context.FactoryAppSlug))
        {
            return new WorkLabelCheckResult(
                false,
                "OutsideAuthorRequiresFeature",
                $"Issue author '{context.IssueAuthor}' is outside the factory trust boundary. Use 'Create feature' from the issue inbox instead of building directly.");
        }

        if (!IsOwnerOrFactory(context.WorkLabelAppliedBy, context.OwnerLogin, context.FactoryAppSlug))
        {
            return new WorkLabelCheckResult(
                false,
                "UntrustedLabeler",
                $"Work label '{workLabel}' was applied by '{context.WorkLabelAppliedBy ?? "unknown"}', not by '{context.OwnerLogin}' or '{context.FactoryAppSlug}'.");
        }

        return WorkLabelCheckResult.Allowed();
    }

    /// <summary>
    /// Enforces §8.3 condition 15, §6.4, §11.4, and §18 AC 17:
    /// PR head branch is in the target repo itself (not a fork), PR author is Copilot coding agent, Dependabot, or owner,
    /// and any linked issue still passes the work-label rule.
    /// </summary>
    public static (bool Passed, string Reason) EvaluateTrustedPrOrigin(TrustedOriginContext context)
    {
        if (context.IsFork || !string.Equals(context.BaseRepoFullName, context.HeadRepoFullName, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"PR originates from fork or external repository '{context.HeadRepoFullName}'.");
        }

        var authorAllowed = IsOwnerOrFactory(context.PrAuthor, context.OwnerLogin, context.FactoryAppSlug)
            || IsCopilotCodingAgent(context.PrAuthor)
            || IsDependabot(context.PrAuthor);

        if (!authorAllowed)
        {
            return (false, $"PR author '{context.PrAuthor}' is not a trusted PR author (owner, Copilot coding agent, or Dependabot).");
        }

        if (context.LinkedIssueWorkLabel is not null)
        {
            var labelResult = EvaluateWorkLabelRule(context.LinkedIssueWorkLabel);
            if (!labelResult.Passed)
            {
                return (false, $"Linked issue failed work-label check: {labelResult.Reason}");
            }
        }

        return (true, "PR originates from an in-repo branch by a trusted actor.");
    }

    /// <summary>
    /// §6.1 Step 6: AT-DEFECT escape hatch comments count only when written by the Copilot coding agent.
    /// </summary>
    public static bool IsValidAtDefectComment(string? commentAuthor, string? body) =>
        IsCopilotCodingAgent(commentAuthor)
        && !string.IsNullOrWhiteSpace(body)
        && body.Contains("AT-DEFECT", StringComparison.Ordinal);

    /// <summary>
    /// §11.4: Clarification answers count only from the repository owner.
    /// </summary>
    public static bool IsValidClarificationReply(string? commentAuthor, string ownerLogin) =>
        !string.IsNullOrWhiteSpace(commentAuthor)
        && string.Equals(commentAuthor, ownerLogin, StringComparison.OrdinalIgnoreCase);
}
