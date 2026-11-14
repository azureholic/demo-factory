namespace DemoFactory.Domain;

public sealed record DependencyBlocker(
    string RepoFullName,
    int IssueNumber,
    bool IsOpen);

public sealed record SchedulerCandidate(
    Guid WorkItemId,
    int IssueNumber,
    string Title,
    BoardStatus BoardStatus,
    bool IsPaused,
    bool AlreadyInOrchestration,
    PriorityLevel Priority,
    ItemSize Size,
    DateTimeOffset CreatedAt,
    WorkLabelContext WorkLabel,
    IReadOnlyList<DependencyBlocker> BlockedBy,
    IReadOnlyList<string> PredictedTouchPaths);

public sealed record InFlightWorkItem(
    Guid WorkItemId,
    int IssueNumber,
    IReadOnlyList<string> PredictedTouchPaths);

public sealed record PmReorderSuggestion(
    int IssueNumber,
    int PreferredRankWithinBand,
    string Reason);

public sealed record RankedWorkItem(
    SchedulerCandidate Candidate,
    int TransitiveUnblockCount,
    int Rank);

public sealed record SchedulerSelectionResult(
    IReadOnlyList<RankedWorkItem> SelectedToStart,
    IReadOnlyList<RankedWorkItem> EligibleRankedQueue,
    IReadOnlyList<int> CycleIssueNumbers,
    IReadOnlyDictionary<int, string> SkippedReasons,
    IReadOnlyList<int> CriticalPathIssueNumbers);

/// <summary>
/// Deterministic scheduler engine (§8.1):
/// 1) Work-label rule & Ready status filter
/// 2) Tarjan's SCC cycle detection -> CycleIssueNumbers (marked Blocked: DependencyCycle)
/// 3) Hard dependency filter (including cross-repository blockers)
/// 4) Deterministic ranking: Priority > TransitiveUnblockCount > Size (Small first) > Age (oldest first) > IssueNumber
///    + optional constrained PM reorder inside the same priority band only
/// 5) WIP limit + Touch-set overlap conflict avoidance when WIP > 1
/// </summary>
public static class SchedulerEngine
{
    public static SchedulerSelectionResult SelectNextItems(
        string repoFullName,
        IReadOnlyList<SchedulerCandidate> allItems,
        IReadOnlyList<InFlightWorkItem> inFlight,
        int wipLimit,
        double touchSetOverlapThreshold = 0.35,
        bool pmCanReorder = false,
        IReadOnlyList<PmReorderSuggestion>? pmSuggestions = null)
    {
        var skipped = new Dictionary<int, string>();

        // Build intra-repo dependency graph: blocker -> dependents
        var issueNumbers = allItems.Select(i => i.IssueNumber).ToHashSet();
        var blockersOf = new Dictionary<int, List<int>>();
        var dependentsOf = new Dictionary<int, List<int>>();

        foreach (var n in issueNumbers)
        {
            blockersOf[n] = [];
            dependentsOf[n] = [];
        }

        foreach (var item in allItems)
        {
            foreach (var dep in item.BlockedBy)
            {
                if (string.Equals(dep.RepoFullName, repoFullName, StringComparison.OrdinalIgnoreCase)
                    && issueNumbers.Contains(dep.IssueNumber))
                {
                    blockersOf[item.IssueNumber].Add(dep.IssueNumber);
                    dependentsOf[dep.IssueNumber].Add(item.IssueNumber);
                }
            }
        }

        // Step 3 (run across graph first): Tarjan's SCC cycle detection
        var cycleNodes = FindCycleNodesTarjan(issueNumbers, blockersOf);
        foreach (var c in cycleNodes)
        {
            skipped[c] = "DependencyCycle";
        }

        // Compute transitive unblock counts & critical path on the acyclic subgraph
        var unblockCounts = ComputeTransitiveUnblockCounts(issueNumbers, dependentsOf, cycleNodes);
        var criticalPath = ComputeCriticalPath(allItems, dependentsOf, cycleNodes);

        var suggestionMap = (pmSuggestions ?? [])
            .GroupBy(s => s.IssueNumber)
            .ToDictionary(g => g.Key, g => g.First().PreferredRankWithinBand);

        var eligible = new List<SchedulerCandidate>();

        foreach (var item in allItems)
        {
            if (cycleNodes.Contains(item.IssueNumber))
                continue;

            if (item.IsPaused)
            {
                skipped[item.IssueNumber] = "Paused";
                continue;
            }

            if (item.AlreadyInOrchestration)
            {
                skipped[item.IssueNumber] = "AlreadyInOrchestration";
                continue;
            }

            if (item.BoardStatus != BoardStatus.Ready)
            {
                skipped[item.IssueNumber] = $"Status={item.BoardStatus}";
                continue;
            }

            // Step 1: Work-label rule (§5.6)
            var labelCheck = TrustBoundary.EvaluateWorkLabelRule(item.WorkLabel);
            if (!labelCheck.Passed)
            {
                skipped[item.IssueNumber] = labelCheck.FailureCode ?? "WorkLabelFailed";
                continue;
            }

            // Step 2: Hard dependency filter (never overridden by PM suggestions!)
            var openBlockers = item.BlockedBy.Where(b => b.IsOpen).ToList();
            if (openBlockers.Count > 0)
            {
                skipped[item.IssueNumber] = $"BlockedBy({string.Join(",", openBlockers.Select(b => $"{b.RepoFullName}#{b.IssueNumber}"))})";
                continue;
            }

            eligible.Add(item);
        }

        // Step 4: Deterministic stable ranking
        // Note: PM reorder is allowed ONLY if pmCanReorder == true, and ONLY within the same Priority band (§8.1).
        var ordered = eligible
            .OrderByDescending(c => (int)c.Priority)
            .ThenBy(c => pmCanReorder && suggestionMap.TryGetValue(c.IssueNumber, out var pmRank) ? pmRank : int.MaxValue)
            .ThenByDescending(c => unblockCounts.GetValueOrDefault(c.IssueNumber, 0))
            .ThenBy(c => (int)c.Size)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.IssueNumber)
            .Select((c, idx) => new RankedWorkItem(c, unblockCounts.GetValueOrDefault(c.IssueNumber, 0), idx + 1))
            .ToList();

        // Step 5: Concurrency & Touch-set conflict filter
        var availableSlots = Math.Max(0, wipLimit - inFlight.Count);
        var selected = new List<RankedWorkItem>();
        var activeTouchSets = inFlight.Select(f => f.PredictedTouchPaths).ToList();

        foreach (var ranked in ordered)
        {
            if (selected.Count >= availableSlots)
                break;

            if (wipLimit > 1 && activeTouchSets.Count > 0)
            {
                var overlaps = activeTouchSets.Any(active =>
                    ComputeTouchSetOverlap(ranked.Candidate.PredictedTouchPaths, active) > touchSetOverlapThreshold);

                if (overlaps)
                {
                    skipped[ranked.Candidate.IssueNumber] = "TouchSetConflict";
                    continue;
                }
            }

            selected.Add(ranked);
            activeTouchSets.Add(ranked.Candidate.PredictedTouchPaths);
        }

        return new SchedulerSelectionResult(
            SelectedToStart: selected,
            EligibleRankedQueue: ordered,
            CycleIssueNumbers: cycleNodes.OrderBy(n => n).ToList(),
            SkippedReasons: skipped,
            CriticalPathIssueNumbers: criticalPath);
    }

    /// <summary>
    /// Tarjan's Strongly Connected Components algorithm ($O(V + E)$).
    /// Returns every node belonging to a cycle (SCC of size > 1, or SCC of size 1 with a self-edge).
    /// </summary>
    public static HashSet<int> FindCycleNodesTarjan(
        IReadOnlySet<int> nodes,
        IReadOnlyDictionary<int, List<int>> adjacency)
    {
        var index = 0;
        var indices = new Dictionary<int, int>();
        var lowLink = new Dictionary<int, int>();
        var onStack = new HashSet<int>();
        var stack = new Stack<int>();
        var cycleNodes = new HashSet<int>();

        foreach (var node in nodes.OrderBy(x => x))
        {
            if (!indices.ContainsKey(node))
                StrongConnect(node);
        }

        return cycleNodes;

        void StrongConnect(int v)
        {
            indices[v] = index;
            lowLink[v] = index;
            index++;
            stack.Push(v);
            onStack.Add(v);

            if (adjacency.TryGetValue(v, out var neighbors))
            {
                foreach (var w in neighbors)
                {
                    if (!nodes.Contains(w))
                        continue;

                    if (!indices.ContainsKey(w))
                    {
                        StrongConnect(w);
                        lowLink[v] = Math.Min(lowLink[v], lowLink[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        lowLink[v] = Math.Min(lowLink[v], indices[w]);
                    }
                }
            }

            if (lowLink[v] == indices[v])
            {
                var scc = new List<int>();
                int w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    scc.Add(w);
                } while (w != v);

                var hasSelfLoop = scc.Count == 1
                    && adjacency.TryGetValue(v, out var selfEdges)
                    && selfEdges.Contains(v);

                if (scc.Count > 1 || hasSelfLoop)
                {
                    foreach (var n in scc)
                        cycleNodes.Add(n);
                }
            }
        }
    }

    public static double ComputeTouchSetOverlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0.0;

        var normA = a.Select(PathGlobMatcher.NormalizePath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var normB = b.Select(PathGlobMatcher.NormalizePath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (normA.Count == 0 || normB.Count == 0)
            return 0.0;

        var matches = 0;
        foreach (var pa in normA)
        {
            if (normB.Any(pb => PathsOverlap(pa, pb)))
                matches++;
        }

        var denominator = Math.Min(normA.Count, normB.Count);
        return (double)matches / denominator;
    }

    private static bool PathsOverlap(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<int, int> ComputeTransitiveUnblockCounts(
        IReadOnlySet<int> nodes,
        IReadOnlyDictionary<int, List<int>> dependentsOf,
        IReadOnlySet<int> cycleNodes)
    {
        var counts = new Dictionary<int, int>();
        foreach (var n in nodes)
        {
            if (cycleNodes.Contains(n))
            {
                counts[n] = 0;
                continue;
            }

            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            if (dependentsOf.TryGetValue(n, out var direct))
            {
                foreach (var d in direct.Where(d => !cycleNodes.Contains(d)))
                {
                    if (visited.Add(d))
                        queue.Enqueue(d);
                }
            }

            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                if (dependentsOf.TryGetValue(curr, out var nextList))
                {
                    foreach (var next in nextList.Where(d => !cycleNodes.Contains(d)))
                    {
                        if (visited.Add(next))
                            queue.Enqueue(next);
                    }
                }
            }

            counts[n] = visited.Count;
        }

        return counts;
    }

    private static List<int> ComputeCriticalPath(
        IReadOnlyList<SchedulerCandidate> allItems,
        IReadOnlyDictionary<int, List<int>> dependentsOf,
        IReadOnlySet<int> cycleNodes)
    {
        var openNodes = allItems
            .Where(i => i.BoardStatus != BoardStatus.Done && !cycleNodes.Contains(i.IssueNumber))
            .Select(i => i.IssueNumber)
            .ToHashSet();

        if (openNodes.Count == 0)
            return [];

        var memoLength = new Dictionary<int, int>();
        var nextHop = new Dictionary<int, int?>();

        int LongestFrom(int u, HashSet<int> visiting)
        {
            if (memoLength.TryGetValue(u, out var cached))
                return cached;

            if (!visiting.Add(u))
                return 0;

            var bestLen = 1;
            int? bestNext = null;

            if (dependentsOf.TryGetValue(u, out var deps))
            {
                foreach (var v in deps.Where(openNodes.Contains).OrderBy(x => x))
                {
                    var candidateLen = 1 + LongestFrom(v, visiting);
                    if (candidateLen > bestLen)
                    {
                        bestLen = candidateLen;
                        bestNext = v;
                    }
                }
            }

            visiting.Remove(u);
            memoLength[u] = bestLen;
            nextHop[u] = bestNext;
            return bestLen;
        }

        var bestStart = openNodes.OrderByDescending(n => LongestFrom(n, [])).ThenBy(n => n).First();
        var path = new List<int>();
        int? cursor = bestStart;
        while (cursor.HasValue)
        {
            path.Add(cursor.Value);
            cursor = nextHop.GetValueOrDefault(cursor.Value);
        }

        return path;
    }
}
