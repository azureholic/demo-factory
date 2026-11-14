using System.Text.RegularExpressions;

namespace DemoFactory.Domain;

public static class PathGlobMatcher
{
    public static bool IsMatch(string filePath, string globPattern)
    {
        var normalizedPath = NormalizePath(filePath);
        var normalizedPattern = NormalizePath(globPattern);

        if (string.IsNullOrEmpty(normalizedPattern))
            return false;

        // Convert glob (** / * / ?) into regex
        var regexBuilder = new System.Text.StringBuilder("^");
        for (var i = 0; i < normalizedPattern.Length; i++)
        {
            var ch = normalizedPattern[i];
            if (ch == '*')
            {
                var isDoubleStar = i + 1 < normalizedPattern.Length && normalizedPattern[i + 1] == '*';
                if (isDoubleStar)
                {
                    var hasFollowingSlash = i + 2 < normalizedPattern.Length && normalizedPattern[i + 2] == '/';
                    if (hasFollowingSlash)
                    {
                        regexBuilder.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        regexBuilder.Append(".*");
                        i += 1;
                    }
                }
                else
                {
                    regexBuilder.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                regexBuilder.Append("[^/]");
            }
            else
            {
                regexBuilder.Append(Regex.Escape(ch.ToString()));
            }
        }
        regexBuilder.Append('$');

        return Regex.IsMatch(normalizedPath, regexBuilder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool MatchesAny(string filePath, IEnumerable<string> patterns) =>
        patterns.Any(p => IsMatch(filePath, p));

    public static bool IsDocsOnlyPath(string filePath)
    {
        var p = NormalizePath(filePath);
        return p.Equals("docs/spec.md", StringComparison.OrdinalIgnoreCase)
            || IsMatch(p, "docs/features/**")
            || IsMatch(p, "docs/adr/**");
    }

    /// <summary>
    /// §5.4: For Dependabot github-actions PRs, changes to .github/workflows/*.yml are allowed automatically
    /// ONLY when every changed line in workflow files is a 40-hex commit SHA-pin bump of an allowlisted action.
    /// </summary>
    public static bool IsAllowedGitHubActionsShaBump(
        IReadOnlyList<ChangedFileDiff> changedFiles,
        IReadOnlyList<string> allowedActions)
    {
        if (changedFiles.Count == 0)
            return false;

        foreach (var file in changedFiles)
        {
            var path = NormalizePath(file.Path);
            if (!IsMatch(path, ".github/workflows/*.yml") && !IsMatch(path, ".github/workflows/*.yaml"))
                return false;

            if (string.IsNullOrWhiteSpace(file.Patch))
                return false;

            var lines = file.Patch.Split('\n');
            var sawUsesPin = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("+++", StringComparison.Ordinal)
                    || line.StartsWith("---", StringComparison.Ordinal)
                    || line.StartsWith("@@", StringComparison.Ordinal)
                    || !line.StartsWith('+') && !line.StartsWith('-'))
                {
                    continue;
                }

                var content = line[1..].Trim();
                // Allow comment-only version bumps accompanying SHA pin or `uses: org/repo@<40-hex-sha>`
                var match = Regex.Match(content, @"^(?:-\s*)?uses:\s*([A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)?)@([0-9a-fA-F]{40})(?:\s*#.*)?$");
                if (!match.Success)
                    return false;

                var actionRepo = match.Groups[1].Value;
                var allowed = allowedActions.Any(a =>
                    actionRepo.Equals(a, StringComparison.OrdinalIgnoreCase)
                    || actionRepo.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase));

                if (!allowed)
                    return false;

                sawUsesPin = true;
            }

            if (!sawUsesPin)
                return false;
        }

        return true;
    }

    public static string NormalizePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
}

public sealed record ChangedFileDiff(
    string Path,
    int Additions,
    int Deletions,
    string? Patch = null,
    bool ByteIdenticalToMain = false);
