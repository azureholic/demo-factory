using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DemoFactory.Domain;

public sealed record RawReviewComment(
    string Author,
    string Path,
    int? Line,
    string? DiffHunk,
    string Body,
    FindingSeverity? ExplicitSeverity = null,
    string? RuleHint = null);

public sealed record TrackedFinding(
    string Id,
    string Fingerprint,
    FindingSeverity Severity,
    string Path,
    int? Line,
    string? DiffHunk,
    string? RuleHint,
    string Body,
    FindingStatus Status,
    int FirstSeenRound,
    int LastSeenRound,
    string? WaiverReason = null,
    string? ContestedJustification = null);

public static partial class FindingTracker
{
    public const int LineShiftTolerance = 10;

    /// <summary>
    /// Computes deterministic finding fingerprint (§8.4):
    /// sha1(path + normalized(ruleHint or first sentence) + severity)
    /// </summary>
    public static string ComputeFingerprint(string path, string? ruleHint, string body, FindingSeverity severity)
    {
        var normalizedPath = (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        var basis = !string.IsNullOrWhiteSpace(ruleHint)
            ? NormalizeText(ruleHint)
            : NormalizeText(ExtractFirstSentence(body));
        var payload = $"{normalizedPath}|{basis}|{severity.ToString().ToLowerInvariant()}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ShortFindingId(string fingerprint) =>
        $"F-{fingerprint[..Math.Min(4, fingerprint.Length)]}";

    public static FindingSeverity ParseSeverity(string body, FindingSeverity? explicitSeverity = null)
    {
        if (explicitSeverity.HasValue)
            return explicitSeverity.Value;

        if (string.IsNullOrWhiteSpace(body))
            return FindingSeverity.Medium;

        if (HighRegex().IsMatch(body))
            return FindingSeverity.High;

        if (LowRegex().IsMatch(body))
            return FindingSeverity.Low;

        return FindingSeverity.Medium;
    }

    /// <summary>
    /// Ingests raw PR review comments for a round (§8.4):
    /// - Filters strictly to comments authored by the Copilot reviewer (§11.4).
    /// - Correlates with prior rounds using fingerprint + line-shift tolerance (±10 lines or same hunk).
    /// - Transitions status across rounds: Open -> Addressed -> Repeated -> Contested (preserves Waived).
    /// </summary>
    public static IReadOnlyList<TrackedFinding> ReconcileRound(
        int round,
        IReadOnlyList<TrackedFinding> previousFindings,
        IReadOnlyList<RawReviewComment> currentComments,
        IReadOnlyList<string>? commitMessagesSinceLastRound = null)
    {
        var copilotComments = currentComments
            .Where(c => TrustBoundary.IsCopilotReviewer(c.Author))
            .ToList();

        var commitText = commitMessagesSinceLastRound is { Count: > 0 }
            ? string.Join("\n", commitMessagesSinceLastRound)
            : string.Empty;

        var result = new List<TrackedFinding>();
        var matchedPreviousIndices = new HashSet<int>();

        foreach (var comment in copilotComments)
        {
            var severity = ParseSeverity(comment.Body, comment.ExplicitSeverity);
            var fingerprint = ComputeFingerprint(comment.Path, comment.RuleHint, comment.Body, severity);
            var matchIndex = FindMatchingPrevious(previousFindings, matchedPreviousIndices, fingerprint, comment);

            if (matchIndex >= 0)
            {
                matchedPreviousIndices.Add(matchIndex);
                var prev = previousFindings[matchIndex];

                if (prev.Status == FindingStatus.Waived)
                {
                    result.Add(prev with { Line = comment.Line ?? prev.Line, LastSeenRound = round });
                    continue;
                }

                // §8.4:
                // - If addressed previously (or claimed addressed in commit message) and raised again -> Repeated.
                // - If already Repeated (or Contested) and raised again -> Contested (repeated twice).
                // - If Open from prior round, fix agent attempted a round and it reappeared -> Repeated.
                var claimedInCommit = !string.IsNullOrWhiteSpace(commitText)
                    && commitText.Contains(prev.Id, StringComparison.OrdinalIgnoreCase);

                var nextStatus = prev.Status switch
                {
                    FindingStatus.Contested => FindingStatus.Contested,
                    FindingStatus.Repeated => FindingStatus.Contested,
                    FindingStatus.Addressed => FindingStatus.Repeated,
                    FindingStatus.Open when round > prev.FirstSeenRound || claimedInCommit => FindingStatus.Repeated,
                    _ => FindingStatus.Open
                };

                var justification = nextStatus == FindingStatus.Contested && severity < FindingSeverity.High
                    ? prev.ContestedJustification ?? $"Auto-marked Contested in round {round} after repeated raises across review rounds."
                    : prev.ContestedJustification;

                result.Add(prev with
                {
                    Line = comment.Line ?? prev.Line,
                    DiffHunk = comment.DiffHunk ?? prev.DiffHunk,
                    Status = nextStatus,
                    LastSeenRound = round,
                    ContestedJustification = justification
                });
            }
            else
            {
                var shortId = ShortFindingId(fingerprint);
                result.Add(new TrackedFinding(
                    Id: shortId,
                    Fingerprint: fingerprint,
                    Severity: severity,
                    Path: comment.Path.Replace('\\', '/').TrimStart('/'),
                    Line: comment.Line,
                    DiffHunk: comment.DiffHunk,
                    RuleHint: comment.RuleHint,
                    Body: comment.Body,
                    Status: FindingStatus.Open,
                    FirstSeenRound: round,
                    LastSeenRound: round));
            }
        }

        // Any previously Open/Repeated finding NOT raised again in this round is now Addressed!
        for (var i = 0; i < previousFindings.Count; i++)
        {
            if (matchedPreviousIndices.Contains(i))
                continue;

            var prev = previousFindings[i];
            if (prev.Status is FindingStatus.Waived or FindingStatus.Addressed)
            {
                result.Add(prev);
            }
            else
            {
                result.Add(prev with { Status = FindingStatus.Addressed });
            }
        }

        return result;
    }

    private static int FindMatchingPrevious(
        IReadOnlyList<TrackedFinding> previous,
        HashSet<int> alreadyMatched,
        string fingerprint,
        RawReviewComment comment)
    {
        for (var i = 0; i < previous.Count; i++)
        {
            if (alreadyMatched.Contains(i))
                continue;

            var candidate = previous[i];
            if (!string.Equals(candidate.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                continue;

            // Within ±10 lines, same diff hunk, or missing line metadata
            if (!candidate.Line.HasValue || !comment.Line.HasValue)
                return i;

            if (Math.Abs(candidate.Line.Value - comment.Line.Value) <= LineShiftTolerance)
                return i;

            if (!string.IsNullOrWhiteSpace(candidate.DiffHunk)
                && !string.IsNullOrWhiteSpace(comment.DiffHunk)
                && string.Equals(NormalizeText(candidate.DiffHunk), NormalizeText(comment.DiffHunk), StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public static string ExtractFirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var stripped = SeverityPrefixRegex().Replace(text.Trim(), string.Empty).Trim();
        var endIndex = stripped.IndexOfAny(['.', '!', '?', '\r', '\n']);
        return endIndex > 0 ? stripped[..endIndex] : stripped;
    }

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return WhitespaceRegex().Replace(text.Trim().ToLowerInvariant(), " ");
    }

    [GeneratedRegex(@"(\[high\]|\*\*high\*\*|severity\s*:\s*high|\bhigh\s+severity\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HighRegex();

    [GeneratedRegex(@"(\[low\]|\*\*low\*\*|severity\s*:\s*low|\blow\s+severity\b|\bnit\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LowRegex();

    [GeneratedRegex(@"^\s*(\[(high|medium|low)\]|\*\*(high|medium|low)\*\*)\s*:?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SeverityPrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
