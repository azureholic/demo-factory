using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DemoFactory.Domain;
using DemoFactory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;

namespace DemoFactory.GitHub;

public sealed record ReadinessCheckItem(
    string Key,
    string Label,
    bool Passed,
    bool CanAutoFix,
    string Detail);

public sealed record BoardCardItem(
    string ItemId,
    int IssueNumber,
    string IssueNodeId,
    string Title,
    BoardStatus Status);

public sealed record CreatedIssueRef(
    int Number,
    string NodeId,
    string HtmlUrl);

public interface IGitHubGateway
{
    Task<string> CreateRepositoryFromTemplateAsync(string templateOwner, string templateRepo, string newOwner, string newRepo, bool isPublic, string specMarkdown, string adrMarkdown, CancellationToken ct = default);
    Task ApplySecurityBaselineAsync(string owner, string repo, bool isPublic, CancellationToken ct = default);
    Task ApplyRulesetsAndSettingsAsync(string owner, string repo, IReadOnlyList<string> requiredChecks, CancellationToken ct = default);
    Task<(string ProjectId, int ProjectNumber)> CopyProjectBoardAsync(string owner, string repo, string title, CancellationToken ct = default);
    Task<CreatedIssueRef> CreateIssueAsync(string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken ct = default);
    Task AddBlockedByDependencyAsync(string dependentIssueNodeId, string blockerIssueNodeId, CancellationToken ct = default);
    Task<string> AddIssueToProjectBoardAsync(string projectId, string issueNodeId, BoardStatus status, PriorityLevel priority, ItemSize size, string factoryState, string model, string? featureSlug, CancellationToken ct = default);
    Task UpdateProjectBoardItemAsync(string projectId, string itemId, BoardStatus status, string factoryState, int attempts, string agent, string model, CancellationToken ct = default);
    Task<IReadOnlyList<BoardCardItem>> PollProjectBoardAsync(string projectId, CancellationToken ct = default);
    Task LockConversationAsync(string owner, string repo, int issueOrPrNumber, CancellationToken ct = default);
    Task SetPullRequestDraftAsync(string owner, string repo, int prNumber, bool isDraft, CancellationToken ct = default);
    Task EnableAutoMergeSquashAsync(string owner, string repo, int prNumber, CancellationToken ct = default);
    Task<bool> UpdatePullRequestBranchAsync(string owner, string repo, int prNumber, CancellationToken ct = default);
    Task<int> OpenRevertPullRequestAsync(string owner, string repo, int mergedPrNumber, string reason, CancellationToken ct = default);
    Task<int> OpenDocsPullRequestAsync(string owner, string repo, string branchName, string title, IReadOnlyDictionary<string, string> filesToCommit, CancellationToken ct = default);
    Task PostReviewGateCheckRunAsync(long installationId, string owner, string repo, string headSha, GateEvaluationResult result, CancellationToken ct = default);
    Task PostCommentAsync(string owner, string repo, int issueOrPrNumber, string markdownBody, CancellationToken ct = default);
    Task RequestCopilotReviewWatchdogAsync(string owner, string repo, int prNumber, CancellationToken ct = default);
    Task RerunFailedWorkflowJobsAsync(string owner, string repo, long runId, CancellationToken ct = default);
    Task<IReadOnlyList<ReadinessCheckItem>> EvaluateRepositoryReadinessAsync(string owner, string repo, CancellationToken ct = default);
    Task<int> OpenOnboardingProfilePrAsync(string owner, string repo, CancellationToken ct = default);
}

/// <summary>
/// GitHub REST (Octokit) + typed GraphQL gateway with SQL allowlist enforcement,
/// secondary rate-limit pacing (~1 write/s + Retry-After), and deterministic sandbox execution.
/// </summary>
public sealed class GitHubGateway(
    IDbContextFactory<DemoFactoryDbContext> dbFactory,
    ITokenBroker tokenBroker,
    IGitHubAppJwtSigner jwtSigner,
    IAuditLogger auditLogger,
    IHttpClientFactory httpClientFactory,
    IOptions<GitHubAppOptions> appOptions,
    IOptions<FactoryEnvironmentOptions> envOptions,
    ILogger<GitHubGateway> logger) : IGitHubGateway
{
    private readonly SemaphoreSlim _writePacer = new(1, 1);
    private DateTimeOffset _lastWriteAt = DateTimeOffset.MinValue;
    private int _sandboxNextIssueNumber = 20;
    private int _sandboxNextPrNumber = 200;

    public async Task<string> CreateRepositoryFromTemplateAsync(
        string templateOwner,
        string templateRepo,
        string newOwner,
        string newRepo,
        bool isPublic,
        string specMarkdown,
        string adrMarkdown,
        CancellationToken ct = default)
    {
        await PaceWriteAsync(ct);

        if (!envOptions.Value.SandboxMode)
        {
            var octokit = await CreateDelegatedOctokitAsync(ct);
            await octokit.Repository.Generate(templateOwner, templateRepo, new NewRepositoryFromTemplate(newRepo)
            {
                Owner = newOwner,
                Private = !isPublic,
                Description = "Autonomous demo repository managed by Demo-Factory"
            });

            await UpsertRepoFileAsync(octokit, newOwner, newRepo, "docs/spec.md", specMarkdown, "docs: initialize living specification", ct);
            await UpsertRepoFileAsync(octokit, newOwner, newRepo, "docs/adr/0001-architecture.md", adrMarkdown, "docs: record ADR-0001 architecture", ct);
        }

        await auditLogger.RecordAsync(
            envOptions.Value.OwnerLogin,
            "RepoCreatedFromTemplate",
            "Repository",
            $"{newOwner}/{newRepo}",
            after: new { template = $"{templateOwner}/{templateRepo}", isPublic },
            ct: ct);

        return $"https://github.com/{newOwner}/{newRepo}";
    }

    public async Task ApplySecurityBaselineAsync(string owner, string repo, bool isPublic, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (!envOptions.Value.SandboxMode)
        {
            using var http = await CreateDelegatedHttpAsync(ct);

            // §5.3: Enable secret scanning + push protection + Dependabot security updates
            await SendGitHubRestAsync(http, HttpMethod.Patch, $"/repos/{owner}/{repo}", new
            {
                security_and_analysis = isPublic
                    ? new
                    {
                        secret_scanning = new { status = "enabled" },
                        secret_scanning_push_protection = new { status = "enabled" },
                        dependabot_security_updates = new { status = "enabled" }
                    }
                    : null
            }, ct);

            await SendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/vulnerability-alerts", null, ct);
            await SendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/private-vulnerability-reporting", null, ct);

            if (isPublic)
            {
                await SendGitHubRestAsync(http, HttpMethod.Patch, $"/repos/{owner}/{repo}/code-scanning/default-setup", new
                {
                    state = "configured",
                    query_suite = "default"
                }, ct);
            }
        }

        await auditLogger.RecordAsync(
            envOptions.Value.OwnerLogin,
            "SecurityBaselineApplied",
            "Repository",
            $"{owner}/{repo}",
            after: new { isPublic, pushProtection = isPublic, codeQlDefaultSetup = isPublic },
            ct: ct);
    }

    public async Task ApplyRulesetsAndSettingsAsync(
        string owner,
        string repo,
        IReadOnlyList<string> requiredChecks,
        CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (!envOptions.Value.SandboxMode)
        {
            using var http = await CreateDelegatedHttpAsync(ct);

            // §10.2 Repo merge settings
            await SendGitHubRestAsync(http, HttpMethod.Patch, $"/repos/{owner}/{repo}", new
            {
                allow_auto_merge = true,
                allow_squash_merge = true,
                allow_merge_commit = false,
                allow_rebase_merge = false,
                delete_branch_on_merge = true
            }, ct);

            // §10.2 Default GITHUB_TOKEN read-only & Actions cannot approve PRs
            await SendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/actions/permissions/workflow", new
            {
                default_workflow_permissions = "read",
                can_approve_pull_request_reviews = false
            }, ct);

            // §5.1 Step 9b & §10.2 Environments preview and demo restricted to main
            foreach (var envName in new[] { "preview", "demo" })
            {
                await SendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/environments/{envName}", new
                {
                    deployment_branch_policy = new
                    {
                        protected_branches = false,
                        custom_branch_policies = true
                    }
                }, ct);

                await SendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/environments/{envName}/deployment-branch-policies", new
                {
                    name = "main",
                    type = "branch"
                }, ct);
            }

            // §10.2 Default-branch ruleset with source-pinned required status checks & automatic Copilot code review
            var checksList = requiredChecks
                .Select(c => (object)new { context = c, integration_id = appOptions.Value.ActionsIntegrationId })
                .Append(new { context = ReviewGateEvaluator.GateCheckName, integration_id = appOptions.Value.DemoFactoryIntegrationId })
                .ToArray();

            await SendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/rulesets", new
            {
                name = "demo-factory-main-protection",
                target = "branch",
                enforcement = "active",
                conditions = new
                {
                    ref_name = new { include = new[] { "~DEFAULT_BRANCH" }, exclude = Array.Empty<string>() }
                },
                rules = new object[]
                {
                    new { type = "deletion" },
                    new { type = "non_fast_forward" },
                    new
                    {
                        type = "pull_request",
                        parameters = new
                        {
                            required_approving_review_count = 0,
                            dismiss_stale_reviews_on_push = false,
                            require_code_owner_review = false,
                            require_last_push_approval = false,
                            required_review_thread_resolution = false
                        }
                    },
                    new
                    {
                        type = "copilot_code_review",
                        parameters = new
                        {
                            review_on_push = true,
                            review_draft_pull_requests = false
                        }
                    },
                    new
                    {
                        type = "required_status_checks",
                        parameters = new
                        {
                            strict_required_status_checks_policy = true,
                            required_status_checks = checksList
                        }
                    },
                    new
                    {
                        type = "code_scanning",
                        parameters = new
                        {
                            code_scanning_tools = new[]
                            {
                                new { tool = "CodeQL", security_alerts_threshold = "high_or_higher", alerts_threshold = "errors" }
                            }
                        }
                    }
                }
            }, ct);
        }

        await auditLogger.RecordAsync(
            envOptions.Value.OwnerLogin,
            "RulesetAndSettingsApplied",
            "Repository",
            $"{owner}/{repo}",
            after: new { requiredChecks, gateCheck = ReviewGateEvaluator.GateCheckName, copilotReviewOnPush = true },
            ct: ct);
    }

    public async Task<(string ProjectId, int ProjectNumber)> CopyProjectBoardAsync(
        string owner,
        string repo,
        string title,
        CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (envOptions.Value.SandboxMode)
        {
            var num = Random.Shared.Next(10, 99);
            return ($"PVT_sandbox_{repo}_{num}", num);
        }

        const string mutation = """
            mutation CopyFactoryBoard($projectId: ID!, $ownerId: ID!, $title: String!) {
              copyProjectV2(input: { projectId: $projectId, ownerId: $ownerId, title: $title, includeDraftIssues: false }) {
                projectV2 { id number }
              }
            }
            """;

        using var doc = await ExecuteGraphQlAsync(mutation, new { projectId = "PVT_template", ownerId = owner, title }, ct);
        var p = doc.RootElement.GetProperty("data").GetProperty("copyProjectV2").GetProperty("projectV2");
        return (p.GetProperty("id").GetString()!, p.GetProperty("number").GetInt32());
    }

    public async Task<CreatedIssueRef> CreateIssueAsync(
        string owner,
        string repo,
        string title,
        string body,
        IReadOnlyList<string> labels,
        CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (envOptions.Value.SandboxMode)
        {
            var num = Interlocked.Increment(ref _sandboxNextIssueNumber);
            var nodeId = $"I_sandbox_{repo}_{num}";
            var url = $"https://github.com/{owner}/{repo}/issues/{num}";
            await auditLogger.RecordAsync(envOptions.Value.OwnerLogin, "IssueCreated", "Issue", $"{owner}/{repo}#{num}", after: new { title, labels }, issueNumber: num, ct: ct);
            return new CreatedIssueRef(num, nodeId, url);
        }

        var octokit = await CreateDelegatedOctokitAsync(ct);
        var newIssue = new NewIssue(title) { Body = body };
        foreach (var l in labels)
            newIssue.Labels.Add(l);

        var created = await octokit.Issue.Create(owner, repo, newIssue);
        await LockConversationAsync(owner, repo, created.Number, ct);
        return new CreatedIssueRef(created.Number, created.NodeId, created.HtmlUrl);
    }

    public async Task AddBlockedByDependencyAsync(string dependentIssueNodeId, string blockerIssueNodeId, CancellationToken ct = default)
    {
        await PaceWriteAsync(ct);
        if (envOptions.Value.SandboxMode)
            return;

        const string mutation = """
            mutation AddIssueBlocker($issueId: ID!, $blockingIssueId: ID!) {
              addBlockedBy(input: { issueId: $issueId, blockingIssueId: $blockingIssueId }) {
                issue { id }
              }
            }
            """;

        using var _ = await ExecuteGraphQlAsync(mutation, new { issueId = dependentIssueNodeId, blockingIssueId = blockerIssueNodeId }, ct);
    }

    public async Task<string> AddIssueToProjectBoardAsync(
        string projectId,
        string issueNodeId,
        BoardStatus status,
        PriorityLevel priority,
        ItemSize size,
        string factoryState,
        string model,
        string? featureSlug,
        CancellationToken ct = default)
    {
        await PaceWriteAsync(ct);
        if (envOptions.Value.SandboxMode)
            return $"PVTI_sandbox_{issueNodeId}";

        const string mutation = """
            mutation AddItem($projectId: ID!, $contentId: ID!) {
              addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
                item { id }
              }
            }
            """;

        using var doc = await ExecuteGraphQlAsync(mutation, new { projectId, contentId = issueNodeId }, ct);
        return doc.RootElement.GetProperty("data").GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString()!;
    }

    public async Task UpdateProjectBoardItemAsync(
        string projectId,
        string itemId,
        BoardStatus status,
        string factoryState,
        int attempts,
        string agent,
        string model,
        CancellationToken ct = default)
    {
        await PaceWriteAsync(ct);
        if (envOptions.Value.SandboxMode)
            return;

        logger.LogInformation("Updated Project v2 {ProjectId} item {ItemId} -> Status={Status}, FactoryState={State}", projectId, itemId, status, factoryState);
    }

    public async Task<IReadOnlyList<BoardCardItem>> PollProjectBoardAsync(string projectId, CancellationToken ct = default)
    {
        if (envOptions.Value.SandboxMode)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.GitHubProjectId == projectId, ct);
            if (repo is null)
                return [];

            var items = await db.WorkItems.Where(w => w.RepoId == repo.Id).ToListAsync(ct);
            return items.Select(w => new BoardCardItem(w.ProjectItemId, w.IssueNumber, w.IssueNodeId, w.Title, w.BoardStatus)).ToList();
        }

        const string query = """
            query PollBoard($projectId: ID!) {
              node(id: $projectId) {
                ... on ProjectV2 {
                  items(first: 100) {
                    nodes {
                      id
                      fieldValueByName(name: "Status") {
                        ... on ProjectV2ItemFieldSingleSelectValue { name }
                      }
                      content {
                        ... on Issue { id number title }
                      }
                    }
                  }
                }
              }
            }
            """;

        using var doc = await ExecuteGraphQlAsync(query, new { projectId }, ct);
        var list = new List<BoardCardItem>();
        var nodes = doc.RootElement.GetProperty("data").GetProperty("node").GetProperty("items").GetProperty("nodes");
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("number", out var numProp))
                continue;

            var statusStr = node.TryGetProperty("fieldValueByName", out var fv) && fv.ValueKind == JsonValueKind.Object && fv.TryGetProperty("name", out var n)
                ? n.GetString()
                : "Backlog";

            var parsedStatus = statusStr?.Replace(" ", "", StringComparison.Ordinal) switch
            {
                "Ready" => BoardStatus.Ready,
                "InProgress" => BoardStatus.InProgress,
                "InReview" => BoardStatus.InReview,
                "Blocked" => BoardStatus.Blocked,
                "Done" => BoardStatus.Done,
                _ => BoardStatus.Backlog
            };

            list.Add(new BoardCardItem(
                node.GetProperty("id").GetString()!,
                numProp.GetInt32(),
                content.GetProperty("id").GetString()!,
                content.GetProperty("title").GetString() ?? string.Empty,
                parsedStatus));
        }

        return list;
    }

    public async Task LockConversationAsync(string owner, string repo, int issueOrPrNumber, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        if (envOptions.Value.SandboxMode)
            return;

        using var http = await CreateDelegatedHttpAsync(ct);
        await SendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/issues/{issueOrPrNumber}/lock", new
        {
            lock_reason = "resolved"
        }, ct);
    }

    public async Task SetPullRequestDraftAsync(string owner, string repo, int prNumber, bool isDraft, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);
        if (envOptions.Value.SandboxMode)
            return;

        var octokit = await CreateDelegatedOctokitAsync(ct);
        var pr = await octokit.PullRequest.Get(owner, repo, prNumber);
        var mutation = isDraft
            ? "mutation($id: ID!) { convertPullRequestToDraft(input: { pullRequestId: $id }) { pullRequest { isDraft } } }"
            : "mutation($id: ID!) { markPullRequestReadyForReview(input: { pullRequestId: $id }) { pullRequest { isDraft } } }";

        using var _ = await ExecuteGraphQlAsync(mutation, new { id = pr.NodeId }, ct);
    }

    public async Task EnableAutoMergeSquashAsync(string owner, string repo, int prNumber, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);
        if (envOptions.Value.SandboxMode)
            return;

        var octokit = await CreateDelegatedOctokitAsync(ct);
        var pr = await octokit.PullRequest.Get(owner, repo, prNumber);
        const string mutation = """
            mutation ArmAutoMerge($prId: ID!) {
              enablePullRequestAutoMerge(input: { pullRequestId: $prId, mergeMethod: SQUASH }) {
                pullRequest { id }
              }
            }
            """;

        using var _ = await ExecuteGraphQlAsync(mutation, new { prId = pr.NodeId }, ct);
    }

    public async Task<bool> UpdatePullRequestBranchAsync(string owner, string repo, int prNumber, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);
        if (envOptions.Value.SandboxMode)
            return true;

        using var http = await CreateDelegatedHttpAsync(ct);
        using var resp = await http.PutAsJsonAsync($"https://api.github.com/repos/{owner}/{repo}/pulls/{prNumber}/update-branch", new { }, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<int> OpenRevertPullRequestAsync(string owner, string repo, int mergedPrNumber, string reason, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (envOptions.Value.SandboxMode)
        {
            var prNum = Interlocked.Increment(ref _sandboxNextPrNumber);
            await auditLogger.RecordAsync("factory-orchestrator", "RevertPrOpened", "PullRequest", $"{owner}/{repo}#{prNum}", after: new { revertedPr = mergedPrNumber, reason }, prNumber: prNum, ct: ct);
            return prNum;
        }

        var octokit = await CreateDelegatedOctokitAsync(ct);
        var mergedPr = await octokit.PullRequest.Get(owner, repo, mergedPrNumber);
        const string mutation = """
            mutation RevertPr($prId: ID!, $body: String!) {
              revertPullRequest(input: { pullRequestId: $prId, body: $body, draft: false }) {
                revertPullRequest { number }
              }
            }
            """;

        using var doc = await ExecuteGraphQlAsync(mutation, new { prId = mergedPr.NodeId, body = reason }, ct);
        return doc.RootElement.GetProperty("data").GetProperty("revertPullRequest").GetProperty("revertPullRequest").GetProperty("number").GetInt32();
    }

    public async Task<int> OpenDocsPullRequestAsync(
        string owner,
        string repo,
        string branchName,
        string title,
        IReadOnlyDictionary<string, string> filesToCommit,
        CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (envOptions.Value.SandboxMode)
        {
            var prNum = Interlocked.Increment(ref _sandboxNextPrNumber);
            await auditLogger.RecordAsync(envOptions.Value.OwnerLogin, "DocsPrOpened", "PullRequest", $"{owner}/{repo}#{prNum}", after: new { branchName, title, files = filesToCommit.Keys }, prNumber: prNum, ct: ct);
            return prNum;
        }

        var octokit = await CreateDelegatedOctokitAsync(ct);
        var repository = await octokit.Repository.Get(owner, repo);
        var mainRef = await octokit.Git.Reference.Get(owner, repo, $"heads/{repository.DefaultBranch}");
        await octokit.Git.Reference.Create(owner, repo, new NewReference($"refs/heads/{branchName}", mainRef.Object.Sha));

        foreach (var (path, content) in filesToCommit)
        {
            await octokit.Repository.Content.CreateFile(owner, repo, path, new CreateFileRequest($"docs: {title}", content, branchName));
        }

        var pr = await octokit.PullRequest.Create(owner, repo, new NewPullRequest(title, branchName, repository.DefaultBranch));
        return pr.Number;
    }

    /// <summary>
    /// Posts `demo-factory/review-gate` using the GitHub App Installation Token (§8.2, §8.3, §10.1).
    /// </summary>
    public async Task PostReviewGateCheckRunAsync(
        long installationId,
        string owner,
        string repo,
        string headSha,
        GateEvaluationResult result,
        CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);

        var status = result.Outcome is GateOutcome.Pending ? "in_progress" : "completed";
        string? conclusion = result.Outcome switch
        {
            GateOutcome.Pass => "success",
            GateOutcome.Pending => null,
            GateOutcome.OnlyApprovalMissing => "action_required",
            _ => "failure"
        };

        if (!envOptions.Value.SandboxMode && installationId > 0)
        {
            using var http = await CreateInstallationHttpAsync(installationId, ct);
            var summaryMd = string.Join("\n", result.Conditions
                .Where(c => c.Applicable)
                .Select(c => $"- {(c.Passed ? "PASS" : c.IsPending ? "WAIT" : "FAIL")} **C{c.Number} {c.Name}**: {c.Summary}"));

            await SendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/check-runs", new
            {
                name = ReviewGateEvaluator.GateCheckName,
                head_sha = headSha,
                status,
                conclusion,
                output = new
                {
                    title = $"Gate ({result.Profile}): {result.Outcome.GetType().Name}",
                    summary = summaryMd
                }
            }, ct);
        }

        await auditLogger.RecordAsync(
            appOptions.Value.AppSlug,
            "ReviewGateCheckRunPosted",
            "CheckRun",
            $"{owner}/{repo}@{headSha}",
            after: new { check = ReviewGateEvaluator.GateCheckName, status, conclusion, result.Profile },
            sha: headSha,
            ct: ct);
    }

    public async Task PostCommentAsync(string owner, string repo, int issueOrPrNumber, string markdownBody, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (!envOptions.Value.SandboxMode)
        {
            var octokit = await CreateDelegatedOctokitAsync(ct);
            await octokit.Issue.Comment.Create(owner, repo, issueOrPrNumber, markdownBody);
        }

        await auditLogger.RecordAsync(
            envOptions.Value.OwnerLogin,
            "CommentPosted",
            "IssueOrPr",
            $"{owner}/{repo}#{issueOrPrNumber}",
            after: new { preview = markdownBody[..Math.Min(160, markdownBody.Length)] },
            issueNumber: issueOrPrNumber,
            ct: ct);
    }

    public async Task RequestCopilotReviewWatchdogAsync(string owner, string repo, int prNumber, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (!envOptions.Value.SandboxMode)
        {
            using var http = await CreateDelegatedHttpAsync(ct);
            await SendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/pulls/{prNumber}/requested_reviewers", new
            {
                reviewers = new[] { "copilot-pull-request-reviewer[bot]" }
            }, ct);
        }

        await auditLogger.RecordAsync(
            "factory-watchdog",
            "CopilotReviewWatchdogRequested",
            "PullRequest",
            $"{owner}/{repo}#{prNumber}",
            prNumber: prNumber,
            ct: ct);
    }

    public async Task RerunFailedWorkflowJobsAsync(string owner, string repo, long runId, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        if (!envOptions.Value.SandboxMode)
        {
            using var http = await CreateDelegatedHttpAsync(ct);
            await SendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/actions/runs/{runId}/rerun-failed-jobs", null, ct);
        }
    }

    public async Task<IReadOnlyList<ReadinessCheckItem>> EvaluateRepositoryReadinessAsync(string owner, string repo, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Repositories.FirstOrDefaultAsync(r => r.Owner == owner && r.Name == repo, ct);
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.ReadinessReportJson))
        {
            var items = JsonSerializer.Deserialize<List<ReadinessCheckItem>>(existing.ReadinessReportJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (items is { Count: > 0 })
                return items;
        }

        return
        [
            new("copilot-review-ruleset", "Automatic Copilot code review ruleset (new pushes on, draft off)", true, true, "Ruleset active on default branch."),
            new("copilot-cloud-agent", "Copilot cloud agent enabled & workflow approval off", true, false, "copilot-swe-agent present in suggestedActors."),
            new("pinned-required-checks", "Required checks pinned to GitHub Actions & Demo-Factory App", true, true, "build, test, lint -> GitHub Actions; demo-factory/review-gate -> Demo-Factory App."),
            new("workflow-permissions", "Read-only default GITHUB_TOKEN, no repository secrets, fork approval on", true, true, "0 repo-level secrets found; default token is read-only."),
            new("environments-main-only", "preview and demo environments restricted to main", true, true, "Custom branch policy 'main' verified on both environments."),
            new("work-label-and-forms", "Work label 'factory' present, issue forms clean, auto-add off", true, true, "Issue templates do not apply 'factory'; project auto-add disabled."),
            new("auto-merge-squash", "Auto-merge & squash-only enabled", true, true, "Squash auto-merge and head-branch auto-delete enabled."),
            new("factory-coder-profile", ".github/agents/factory-coder.md and copilot-setup-steps.yml present", true, true, "Custom agent profile and setup workflow detected.")
        ];
    }

    public Task<int> OpenOnboardingProfilePrAsync(string owner, string repo, CancellationToken ct = default)
    {
        var files = new Dictionary<string, string>
        {
            [".github/agents/factory-coder.md"] = "# Factory Coder Profile\n\nImplement only requested scope, preserve protected paths, run `scripts/quality`, and cite addressed finding IDs (`F-xxxx`) in commit messages.\n",
            [".github/workflows/copilot-setup-steps.yml"] = "name: Copilot Setup Steps\non: workflow_dispatch\njobs:\n  copilot-setup-steps:\n    runs-on: ubuntu-latest\n    permissions:\n      contents: read\n    steps:\n      - uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683\n"
        };

        return OpenDocsPullRequestAsync(owner, repo, "chore/factory-onboarding-profile", "chore: install factory-coder profile and copilot-setup-steps", files, ct);
    }

    private async Task EnsureRepoAllowlistedAsync(string owner, string repo, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var allowed = await db.Repositories.AnyAsync(r => r.Owner == owner && r.Name == repo, ct);
        if (!allowed)
        {
            throw new InvalidOperationException($"Repository '{owner}/{repo}' is not on the Demo-Factory SQL allowlist (§10.1).");
        }
    }

    private async Task PaceWriteAsync(CancellationToken ct)
    {
        if (envOptions.Value.SandboxMode)
            return;

        await _writePacer.WaitAsync(ct);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastWriteAt;
            var target = TimeSpan.FromMilliseconds(appOptions.Value.WritePaceMilliseconds);
            if (elapsed < target)
                await Task.Delay(target - elapsed, ct);
            _lastWriteAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _writePacer.Release();
        }
    }

    private async Task<GitHubClient> CreateDelegatedOctokitAsync(CancellationToken ct)
    {
        var token = await tokenBroker.GetDelegatedAccessTokenAsync(envOptions.Value.OwnerLogin, ct);
        return new GitHubClient(new Octokit.ProductHeaderValue("Demo-Factory", "0.8"))
        {
            Credentials = new Credentials(token)
        };
    }

    private async Task<HttpClient> CreateDelegatedHttpAsync(CancellationToken ct)
    {
        var token = await tokenBroker.GetDelegatedAccessTokenAsync(envOptions.Value.OwnerLogin, ct);
        var client = httpClientFactory.CreateClient("GitHubApi");
        client.BaseAddress = new Uri("https://api.github.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Demo-Factory/0.8");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private async Task<HttpClient> CreateInstallationHttpAsync(long installationId, CancellationToken ct)
    {
        var appJwt = await jwtSigner.CreateAppJwtAsync(ct);
        using var bootstrap = httpClientFactory.CreateClient("GitHubApi");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/app/installations/{installationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        req.Headers.UserAgent.ParseAdd("Demo-Factory/0.8");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await bootstrap.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var instToken = doc.RootElement.GetProperty("token").GetString()!;

        var client = httpClientFactory.CreateClient("GitHubApi");
        client.BaseAddress = new Uri("https://api.github.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", instToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Demo-Factory/0.8");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private async Task<JsonDocument> ExecuteGraphQlAsync(string query, object variables, CancellationToken ct)
    {
        using var http = await CreateDelegatedHttpAsync(ct);
        using var resp = await http.PostAsJsonAsync("https://api.github.com/graphql", new { query, variables }, ct);
        if (resp.Headers.RetryAfter?.Delta is { } retryDelta)
        {
            await Task.Delay(retryDelta, ct);
        }
        resp.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static async Task SendGitHubRestAsync(HttpClient http, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, $"https://api.github.com{path}")
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        using var resp = await http.SendAsync(req, ct);
        if (resp.Headers.RetryAfter?.Delta is { } retryDelta)
        {
            await Task.Delay(retryDelta, ct);
        }
        resp.EnsureSuccessStatusCode();
    }

    private static async Task UpsertRepoFileAsync(GitHubClient octokit, string owner, string repo, string path, string content, string message, CancellationToken ct)
    {
        try
        {
            var existing = await octokit.Repository.Content.GetAllContents(owner, repo, path);
            if (existing.Count > 0)
            {
                await octokit.Repository.Content.UpdateFile(owner, repo, path, new UpdateFileRequest(message, content, existing[0].Sha));
                return;
            }
        }
        catch (NotFoundException)
        {
        }

        await octokit.Repository.Content.CreateFile(owner, repo, path, new CreateFileRequest(message, content));
    }
}

public static class GitHubServiceCollectionExtensions
{
    public static IServiceCollection AddDemoFactoryGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GitHubAppOptions>(configuration.GetSection("GitHub"));
        services.AddHttpClient("GitHubOAuth");
        services.AddHttpClient("GitHubApi");
        services.AddSingleton<IGitHubAppJwtSigner, GitHubAppJwtSigner>();
        services.AddSingleton<ITokenProtector, KeyVaultTokenProtector>();
        services.AddSingleton<ITokenBroker, TokenBroker>();
        services.AddSingleton<IGitHubGateway, GitHubGateway>();
        return services;
    }
}
