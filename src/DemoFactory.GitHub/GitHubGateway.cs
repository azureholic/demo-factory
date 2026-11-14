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

public sealed record AppInstallationCheckResult(
    bool CheckedLive,
    bool InstalledOnOwner,
    long? InstallationId,
    string InstallUrl,
    string Detail);

public interface IGitHubGateway
{
    Task<AppInstallationCheckResult> CheckAppInstallationAsync(string owner, string? repo = null, CancellationToken ct = default);
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

    private bool IsSandbox => envOptions.Value.GetEffectiveSandboxMode();

    public async Task<AppInstallationCheckResult> CheckAppInstallationAsync(string owner, string? repo = null, CancellationToken ct = default)
    {
        tokenBroker.EnsureConfigurationLoaded();
        var slug = string.IsNullOrWhiteSpace(tokenBroker.ActiveAppSlug) ? appOptions.Value.AppSlug : tokenBroker.ActiveAppSlug;
        var installUrl = $"https://github.com/apps/{Uri.EscapeDataString(slug)}/installations/new";

        if (!tokenBroker.HasPrivateKeyConfigured)
        {
            return new AppInstallationCheckResult(
                CheckedLive: false,
                InstalledOnOwner: false,
                InstallationId: null,
                InstallUrl: installUrl,
                Detail: "GitHub App private key is not stored locally yet.");
        }

        try
        {
            var appJwt = await jwtSigner.CreateAppJwtAsync(ct);
            using var http = httpClientFactory.CreateClient("GitHubApi");

            if (!string.IsNullOrWhiteSpace(repo))
            {
                using var repoReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/installation");
                repoReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
                repoReq.Headers.UserAgent.ParseAdd("Demo-Factory/0.8");
                repoReq.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var repoResp = await http.SendAsync(repoReq, ct);
                if (repoResp.IsSuccessStatusCode)
                {
                    using var repoDoc = await JsonDocument.ParseAsync(await repoResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (repoDoc.RootElement.TryGetProperty("id", out var repoInstId))
                    {
                        var id = repoInstId.GetInt64();
                        return new AppInstallationCheckResult(true, true, id, installUrl, $"Installed on {owner}/{repo} (installation #{id}).");
                    }
                }
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/app/installations?per_page=100");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
            req.Headers.UserAgent.ParseAdd("Demo-Factory/0.8");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new AppInstallationCheckResult(
                    CheckedLive: false,
                    InstalledOnOwner: false,
                    InstallationId: null,
                    InstallUrl: installUrl,
                    Detail: $"GitHub App installations check returned HTTP {(int)resp.StatusCode}.");
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var acctLogin = el.TryGetProperty("account", out var acct) && acct.TryGetProperty("login", out var l)
                        ? l.GetString()
                        : null;
                    if (string.Equals(acctLogin, owner, StringComparison.OrdinalIgnoreCase) && el.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetInt64();
                        return new AppInstallationCheckResult(true, true, id, installUrl, $"Installed on @{owner} (installation #{id}).");
                    }
                }
            }

            return new AppInstallationCheckResult(
                CheckedLive: true,
                InstalledOnOwner: false,
                InstallationId: null,
                InstallUrl: installUrl,
                Detail: $"GitHub App '{slug}' is registered, but not yet installed on @{owner}.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to query GitHub App installations for {Owner}", owner);
            return new AppInstallationCheckResult(false, false, null, installUrl, $"Unable to verify installation: {ex.Message}");
        }
    }

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

        if (!IsSandbox)
        {
            using var http = await CreateDelegatedHttpAsync(ct);
            var existingTarget = await TryGetJsonAsync(http, $"/repos/{newOwner}/{newRepo}", ct);

            if (existingTarget is null)
            {
                using var templateDoc = await TryGetJsonAsync(http, $"/repos/{templateOwner}/{templateRepo}", ct);
                var isTemplate = templateDoc is not null
                    && templateDoc.RootElement.TryGetProperty("is_template", out var it)
                    && it.ValueKind == JsonValueKind.True;

                if (isTemplate)
                {
                    await SendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{templateOwner}/{templateRepo}/generate", new
                    {
                        owner = newOwner,
                        name = newRepo,
                        @private = !isPublic,
                        description = "Autonomous demo repository managed by Demo-Factory"
                    }, ct);
                    await WaitForDefaultBranchReadyAsync(http, newOwner, newRepo, ct);
                }
                else
                {
                    using var meDoc = await TryGetJsonAsync(http, "/user", ct);
                    var currentLogin = meDoc is not null && meDoc.RootElement.TryGetProperty("login", out var lp)
                        ? lp.GetString()
                        : envOptions.Value.OwnerLogin;

                    var createPath = string.Equals(currentLogin, newOwner, StringComparison.OrdinalIgnoreCase)
                        ? "/user/repos"
                        : $"/orgs/{newOwner}/repos";

                    await SendGitHubRestAsync(http, HttpMethod.Post, createPath, new
                    {
                        name = newRepo,
                        @private = !isPublic,
                        auto_init = true,
                        description = "Autonomous demo repository managed by Demo-Factory"
                    }, ct);

                    await WaitForDefaultBranchReadyAsync(http, newOwner, newRepo, ct);
                    await SeedGoldenPathTemplateFilesAsync(http, newOwner, newRepo, templateRepo, ct);
                }
            }
            else
            {
                existingTarget.Dispose();
            }

            await UpsertRepoFileHttpAsync(http, newOwner, newRepo, "docs/spec.md", specMarkdown, "docs: initialize living specification", ct);
            await UpsertRepoFileHttpAsync(http, newOwner, newRepo, "docs/adr/0001-architecture.md", adrMarkdown, "docs: record ADR-0001 architecture", ct);
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

        if (!IsSandbox)
        {
            using var http = await CreateDelegatedHttpAsync(ct);

            if (isPublic)
            {
                await TrySendGitHubRestAsync(http, HttpMethod.Patch, $"/repos/{owner}/{repo}", new
                {
                    security_and_analysis = new
                    {
                        secret_scanning = new { status = "enabled" },
                        secret_scanning_push_protection = new { status = "enabled" },
                        dependabot_security_updates = new { status = "enabled" }
                    }
                }, ct);
            }

            await TrySendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/vulnerability-alerts", null, ct);

            if (isPublic)
            {
                await TrySendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/private-vulnerability-reporting", null, ct);
                await TrySendGitHubRestAsync(http, HttpMethod.Patch, $"/repos/{owner}/{repo}/code-scanning/default-setup", new
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

        if (!IsSandbox)
        {
            using var http = await CreateDelegatedHttpAsync(ct);

            await SendGitHubRestAsync(http, HttpMethod.Patch, $"/repos/{owner}/{repo}", new
            {
                allow_auto_merge = true,
                allow_squash_merge = true,
                allow_merge_commit = false,
                allow_rebase_merge = false,
                delete_branch_on_merge = true
            }, ct);

            await TrySendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/actions/permissions/workflow", new
            {
                default_workflow_permissions = "read",
                can_approve_pull_request_reviews = false
            }, ct);

            foreach (var envName in new[] { "preview", "demo" })
            {
                var envCreated = await TrySendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/environments/{envName}", new
                {
                    deployment_branch_policy = new
                    {
                        protected_branches = false,
                        custom_branch_policies = true
                    }
                }, ct);

                if (envCreated)
                {
                    await TrySendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/environments/{envName}/deployment-branch-policies", new
                    {
                        name = "main",
                        type = "branch"
                    }, ct);
                }
            }

            var rulesetsExist = await HasExistingRulesetAsync(http, owner, repo, "demo-factory-main-protection", ct);
            if (!rulesetsExist)
            {
                var checksList = requiredChecks
                    .Select(c => (object)new { context = c, integration_id = appOptions.Value.ActionsIntegrationId })
                    .Append(new { context = ReviewGateEvaluator.GateCheckName, integration_id = appOptions.Value.DemoFactoryIntegrationId })
                    .ToArray();

                var fullRulesetApplied = await TrySendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/rulesets", new
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

                if (!fullRulesetApplied)
                {
                    await TrySendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/rulesets", new
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
                                type = "required_status_checks",
                                parameters = new
                                {
                                    strict_required_status_checks_policy = true,
                                    required_status_checks = checksList
                                }
                            }
                        }
                    }, ct);
                }
            }
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

        if (IsSandbox)
        {
            var num = Random.Shared.Next(10, 99);
            return ($"PVT_sandbox_{repo}_{num}", num);
        }

        try
        {
            const string repoNodeQuery = """
                query ResolveRepoAndOwner($owner: String!, $repo: String!) {
                  repository(owner: $owner, name: $repo) {
                    id
                    owner { id }
                  }
                }
                """;

            using var idsDoc = await ExecuteGraphQlAsync(repoNodeQuery, new { owner, repo }, ct);
            var repoEl = idsDoc.RootElement.GetProperty("data").GetProperty("repository");
            var repoNodeId = repoEl.GetProperty("id").GetString()!;
            var ownerNodeId = repoEl.GetProperty("owner").GetProperty("id").GetString()!;

            string projectId;
            int projectNumber;

            if (!string.IsNullOrWhiteSpace(appOptions.Value.TemplateProjectNodeId))
            {
                const string copyMutation = """
                    mutation CopyFactoryBoard($projectId: ID!, $ownerId: ID!, $title: String!) {
                      copyProjectV2(input: { projectId: $projectId, ownerId: $ownerId, title: $title, includeDraftIssues: false }) {
                        projectV2 { id number }
                      }
                    }
                    """;

                using var copyDoc = await ExecuteGraphQlAsync(copyMutation, new { projectId = appOptions.Value.TemplateProjectNodeId, ownerId = ownerNodeId, title }, ct);
                var p = copyDoc.RootElement.GetProperty("data").GetProperty("copyProjectV2").GetProperty("projectV2");
                projectId = p.GetProperty("id").GetString()!;
                projectNumber = p.GetProperty("number").GetInt32();
            }
            else
            {
                const string createMutation = """
                    mutation CreateFactoryBoard($ownerId: ID!, $title: String!) {
                      createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                        projectV2 { id number }
                      }
                    }
                    """;

                using var createDoc = await ExecuteGraphQlAsync(createMutation, new { ownerId = ownerNodeId, title }, ct);
                var p = createDoc.RootElement.GetProperty("data").GetProperty("createProjectV2").GetProperty("projectV2");
                projectId = p.GetProperty("id").GetString()!;
                projectNumber = p.GetProperty("number").GetInt32();
            }

            const string linkMutation = """
                mutation LinkBoardToRepo($projectId: ID!, $repositoryId: ID!) {
                  linkProjectV2ToRepository(input: { projectId: $projectId, repositoryId: $repositoryId }) {
                    repository { id }
                  }
                }
                """;
            try
            {
                using var _ = await ExecuteGraphQlAsync(linkMutation, new { projectId, repositoryId = repoNodeId }, ct);
            }
            catch (Exception linkEx)
            {
                logger.LogDebug(linkEx, "Optional linkProjectV2ToRepository skipped for {Owner}/{Repo}", owner, repo);
            }

            return (projectId, projectNumber);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Projects v2 creation unavailable for {Owner}/{Repo}; falling back to local SQL board mirror.", owner, repo);
            return ($"PVT_unlinked_{repo}", 0);
        }
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

        if (IsSandbox)
        {
            var num = Interlocked.Increment(ref _sandboxNextIssueNumber);
            var nodeId = $"I_sandbox_{repo}_{num}";
            var url = $"https://github.com/{owner}/{repo}/issues/{num}";
            await auditLogger.RecordAsync(envOptions.Value.OwnerLogin, "IssueCreated", "Issue", $"{owner}/{repo}#{num}", after: new { title, labels }, issueNumber: num, ct: ct);
            return new CreatedIssueRef(num, nodeId, url);
        }

        using var http = await CreateDelegatedHttpAsync(ct);
        await EnsureLabelsExistHttpAsync(http, owner, repo, labels, ct);

        using var createdDoc = await SendGitHubRestForJsonAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/issues", new
        {
            title,
            body,
            labels
        }, ct);

        var createdNumber = createdDoc.RootElement.GetProperty("number").GetInt32();
        var createdNodeId = createdDoc.RootElement.GetProperty("node_id").GetString() ?? $"I_{repo}_{createdNumber}";
        var createdUrl = createdDoc.RootElement.TryGetProperty("html_url", out var hu)
            ? hu.GetString() ?? $"https://github.com/{owner}/{repo}/issues/{createdNumber}"
            : $"https://github.com/{owner}/{repo}/issues/{createdNumber}";

        try
        {
            await LockConversationAsync(owner, repo, createdNumber, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Issue lock skipped for {Owner}/{Repo}#{Number}", owner, repo, createdNumber);
        }

        await auditLogger.RecordAsync(envOptions.Value.OwnerLogin, "IssueCreated", "Issue", $"{owner}/{repo}#{createdNumber}", after: new { title, labels }, issueNumber: createdNumber, ct: ct);
        return new CreatedIssueRef(createdNumber, createdNodeId, createdUrl);
    }

    public async Task AddBlockedByDependencyAsync(string dependentIssueNodeId, string blockerIssueNodeId, CancellationToken ct = default)
    {
        await PaceWriteAsync(ct);
        if (IsSandbox)
            return;

        const string mutation = """
            mutation AddIssueBlocker($issueId: ID!, $blockingIssueId: ID!) {
              addBlockedBy(input: { issueId: $issueId, blockingIssueId: $blockingIssueId }) {
                issue { id }
              }
            }
            """;

        try
        {
            using var _ = await ExecuteGraphQlAsync(mutation, new { issueId = dependentIssueNodeId, blockingIssueId = blockerIssueNodeId }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "addBlockedBy GraphQL call skipped between {Dependent} and {Blocker}", dependentIssueNodeId, blockerIssueNodeId);
        }
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
        if (IsSandbox || string.IsNullOrWhiteSpace(projectId) || projectId.StartsWith("PVT_unlinked_", StringComparison.Ordinal))
            return $"PVTI_local_{issueNodeId}";

        const string mutation = """
            mutation AddItem($projectId: ID!, $contentId: ID!) {
              addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
                item { id }
              }
            }
            """;

        try
        {
            using var doc = await ExecuteGraphQlAsync(mutation, new { projectId, contentId = issueNodeId }, ct);
            return doc.RootElement.GetProperty("data").GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString()!;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to add issue {IssueNodeId} to Project v2 {ProjectId}", issueNodeId, projectId);
            return $"PVTI_local_{issueNodeId}";
        }
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
        if (IsSandbox || string.IsNullOrWhiteSpace(projectId) || projectId.StartsWith("PVT_unlinked_", StringComparison.Ordinal))
            return;

        logger.LogInformation("Updated Project v2 {ProjectId} item {ItemId} -> Status={Status}, FactoryState={State}", projectId, itemId, status, factoryState);
    }

    public async Task<IReadOnlyList<BoardCardItem>> PollProjectBoardAsync(string projectId, CancellationToken ct = default)
    {
        if (IsSandbox || string.IsNullOrWhiteSpace(projectId) || projectId.StartsWith("PVT_unlinked_", StringComparison.Ordinal))
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
        if (IsSandbox)
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
        if (IsSandbox)
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
        if (IsSandbox)
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
        if (IsSandbox)
            return true;

        using var http = await CreateDelegatedHttpAsync(ct);
        using var resp = await http.PutAsJsonAsync($"https://api.github.com/repos/{owner}/{repo}/pulls/{prNumber}/update-branch", new { }, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<int> OpenRevertPullRequestAsync(string owner, string repo, int mergedPrNumber, string reason, CancellationToken ct = default)
    {
        await EnsureRepoAllowlistedAsync(owner, repo, ct);
        await PaceWriteAsync(ct);

        if (IsSandbox)
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

        if (IsSandbox)
        {
            var prNum = Interlocked.Increment(ref _sandboxNextPrNumber);
            await auditLogger.RecordAsync(envOptions.Value.OwnerLogin, "DocsPrOpened", "PullRequest", $"{owner}/{repo}#{prNum}", after: new { branchName, title, files = filesToCommit.Keys }, prNumber: prNum, ct: ct);
            return prNum;
        }

        using var http = await CreateDelegatedHttpAsync(ct);
        using var repoDoc = await SendGitHubRestForJsonAsync(http, HttpMethod.Get, $"/repos/{owner}/{repo}", null, ct);
        var defaultBranch = repoDoc.RootElement.TryGetProperty("default_branch", out var dbp) ? dbp.GetString() ?? "main" : "main";

        using var refDoc = await SendGitHubRestForJsonAsync(http, HttpMethod.Get, $"/repos/{owner}/{repo}/git/ref/heads/{defaultBranch}", null, ct);
        var baseSha = refDoc.RootElement.GetProperty("object").GetProperty("sha").GetString()!;

        await TrySendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/git/refs", new
        {
            @ref = $"refs/heads/{branchName}",
            sha = baseSha
        }, ct);

        foreach (var (path, content) in filesToCommit)
        {
            await UpsertRepoFileHttpAsync(http, owner, repo, path, content, $"docs: {title}", ct, branchName);
        }

        using var prDoc = await SendGitHubRestForJsonAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/pulls", new
        {
            title,
            head = branchName,
            @base = defaultBranch,
            body = $"Automated documentation/profile update opened by Demo-Factory: {title}"
        }, ct);

        return prDoc.RootElement.GetProperty("number").GetInt32();
    }

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

        var resolvedInstallationId = installationId;
        if (!IsSandbox && (resolvedInstallationId <= 0 || resolvedInstallationId == 90421001))
        {
            var check = await CheckAppInstallationAsync(owner, repo, ct);
            if (check.InstallationId.HasValue)
                resolvedInstallationId = check.InstallationId.Value;
        }

        if (!IsSandbox && resolvedInstallationId > 0 && resolvedInstallationId != 90421001)
        {
            using var http = await CreateInstallationHttpAsync(resolvedInstallationId, ct);
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

        if (!IsSandbox)
        {
            using var http = await CreateDelegatedHttpAsync(ct);
            await SendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/issues/{issueOrPrNumber}/comments", new
            {
                body = markdownBody
            }, ct);
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

        if (!IsSandbox)
        {
            using var http = await CreateDelegatedHttpAsync(ct);
            await TrySendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/pulls/{prNumber}/requested_reviewers", new
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
        if (!IsSandbox)
        {
            using var http = await CreateDelegatedHttpAsync(ct);
            await SendGitHubRestAsync(http, HttpMethod.Post, $"/repos/{owner}/{repo}/actions/runs/{runId}/rerun-failed-jobs", null, ct);
        }
    }

    public async Task<IReadOnlyList<ReadinessCheckItem>> EvaluateRepositoryReadinessAsync(string owner, string repo, CancellationToken ct = default)
    {
        if (!IsSandbox)
        {
            try
            {
                using var http = await CreateDelegatedHttpAsync(ct);
                using var repoDoc = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}", ct);
                if (repoDoc is null)
                {
                    return
                    [
                        new("repo-exists", "Target repository accessible on GitHub", false, false, $"Repository '{owner}/{repo}' was not found or is not accessible to the signed-in user.")
                    ];
                }

                var root = repoDoc.RootElement;
                var autoMergeOk = root.TryGetProperty("allow_auto_merge", out var am) && am.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("allow_squash_merge", out var sq) && sq.ValueKind == JsonValueKind.True;

                using var wfDoc = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/actions/permissions/workflow", ct);
                var wfReadOnly = wfDoc is not null
                    && wfDoc.RootElement.TryGetProperty("default_workflow_permissions", out var dwp)
                    && string.Equals(dwp.GetString(), "read", StringComparison.OrdinalIgnoreCase);

                using var previewEnv = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/environments/preview", ct);
                using var demoEnv = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/environments/demo", ct);
                var envsOk = previewEnv is not null && demoEnv is not null;

                using var labelDoc = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/labels/factory", ct);
                var hasWorkLabel = labelDoc is not null;

                using var coderProfileDoc = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/contents/.github/agents/factory-coder.md", ct);
                var hasCoderProfile = coderProfileDoc is not null;

                var hasRuleset = await HasExistingRulesetAsync(http, owner, repo, "demo-factory-main-protection", ct);
                var appCheck = await CheckAppInstallationAsync(owner, repo, ct);

                return
                [
                    new("github-app-installed", "GitHub App installed on repository", appCheck.InstalledOnOwner, false, appCheck.Detail),
                    new("copilot-review-ruleset", "Default-branch protection & Copilot review ruleset", hasRuleset, true, hasRuleset ? "Ruleset 'demo-factory-main-protection' active." : "Ruleset 'demo-factory-main-protection' not configured yet."),
                    new("pinned-required-checks", "Required checks pinned to GitHub Actions & Demo-Factory App", hasRuleset, true, hasRuleset ? "Required status checks active on default branch." : "Will be configured when rulesets are applied."),
                    new("workflow-permissions", "Read-only default GITHUB_TOKEN & PR approval disabled", wfReadOnly, true, wfReadOnly ? "Default workflow token is read-only." : "Default workflow token is not restricted to read-only."),
                    new("environments-main-only", "preview and demo environments configured", envsOk, true, envsOk ? "Both 'preview' and 'demo' environments exist." : "Missing 'preview' or 'demo' deployment environment."),
                    new("work-label-and-forms", "Work label 'factory' present", hasWorkLabel, true, hasWorkLabel ? "Label 'factory' found." : "Label 'factory' missing; will be created automatically."),
                    new("auto-merge-squash", "Auto-merge & squash-only enabled", autoMergeOk, true, autoMergeOk ? "Auto-merge and squash merge enabled." : "Auto-merge or squash merge disabled."),
                    new("factory-coder-profile", ".github/agents/factory-coder.md present", hasCoderProfile, true, hasCoderProfile ? "Custom agent profile detected." : "Missing .github/agents/factory-coder.md; can open onboarding PR.")
                ];
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Live readiness evaluation failed for {Owner}/{Repo}", owner, repo);
            }
        }

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
            throw new InvalidOperationException($"Repository '{owner}/{repo}' is not on the Demo-Factory SQL allowlist.");
        }
    }

    private async Task PaceWriteAsync(CancellationToken ct)
    {
        if (IsSandbox)
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
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private async Task<HttpClient> CreateInstallationHttpAsync(long installationId, CancellationToken ct)
    {
        tokenBroker.EnsureConfigurationLoaded();
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
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.GetRawText())
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var errMsg = string.Join("; ", messages);
            doc.Dispose();
            throw new InvalidOperationException($"GitHub GraphQL error: {errMsg}");
        }
        return doc;
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
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"GitHub REST {method} {path} failed with HTTP {(int)resp.StatusCode}: {detail}", null, resp.StatusCode);
        }
    }

    private async Task<bool> TrySendGitHubRestAsync(HttpClient http, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
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
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Optional GitHub REST {Method} {Path} returned HTTP {Status}: {Detail}", method, path, (int)resp.StatusCode, detail);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Optional GitHub REST {Method} {Path} threw exception", method, path);
            return false;
        }
    }

    private static async Task<JsonDocument> SendGitHubRestForJsonAsync(HttpClient http, HttpMethod method, string path, object? body, CancellationToken ct)
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
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"GitHub REST {method} {path} failed with HTTP {(int)resp.StatusCode}: {detail}", null, resp.StatusCode);
        }
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static async Task<JsonDocument?> TryGetJsonAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"https://api.github.com{path}", ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static async Task<bool> HasExistingRulesetAsync(HttpClient http, string owner, string repo, string rulesetName, CancellationToken ct)
    {
        using var doc = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/rulesets", ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var n) && string.Equals(n.GetString(), rulesetName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task WaitForDefaultBranchReadyAsync(HttpClient http, string owner, string repo, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var repoDoc = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}", ct);
            if (repoDoc is not null)
            {
                var branch = repoDoc.RootElement.TryGetProperty("default_branch", out var dbp) ? dbp.GetString() ?? "main" : "main";
                using var refDoc = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/git/ref/heads/{branch}", ct);
                if (refDoc is not null)
                    return;
            }
            await Task.Delay(400, ct);
        }
    }

    private async Task SeedGoldenPathTemplateFilesAsync(HttpClient http, string owner, string repo, string templateRepo, CancellationToken ct)
    {
        var templateKey = templateRepo.Contains("python", StringComparison.OrdinalIgnoreCase) ? "python-fastapi" : "dotnet-react";
        var baseDir = envOptions.Value.TemplatesDirectory;
        if (!Path.IsPathRooted(baseDir))
        {
            var fromCwd = Path.Combine(Directory.GetCurrentDirectory(), baseDir);
            var fromRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", baseDir));
            baseDir = Directory.Exists(fromCwd) ? fromCwd : Directory.Exists(fromRoot) ? fromRoot : fromCwd;
        }

        var templateDir = Path.Combine(baseDir, templateKey);
        if (!Directory.Exists(templateDir))
            return;

        foreach (var filePath in Directory.EnumerateFiles(templateDir, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(templateDir, filePath).Replace('\\', '/');
            var content = await File.ReadAllTextAsync(filePath, ct);
            await UpsertRepoFileHttpAsync(http, owner, repo, relPath, content, $"chore: scaffold golden-path {relPath}", ct);
        }
    }

    private static async Task EnsureLabelsExistHttpAsync(HttpClient http, string owner, string repo, IReadOnlyList<string> labels, CancellationToken ct)
    {
        foreach (var label in labels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var existing = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/labels/{Uri.EscapeDataString(label)}", ct);
            if (existing is not null)
                continue;

            var color = label.ToLowerInvariant() switch
            {
                "factory" => "0E8A16",
                "acceptance-test" => "1D76DB",
                _ => "5319E7"
            };

            using var createResp = await http.PostAsJsonAsync($"https://api.github.com/repos/{owner}/{repo}/labels", new
            {
                name = label,
                color
            }, ct);
        }
    }

    private static async Task UpsertRepoFileHttpAsync(HttpClient http, string owner, string repo, string path, string content, string message, CancellationToken ct, string? branch = null)
    {
        var query = string.IsNullOrWhiteSpace(branch) ? string.Empty : $"?ref={Uri.EscapeDataString(branch)}";
        using var existing = await TryGetJsonAsync(http, $"/repos/{owner}/{repo}/contents/{path}{query}", ct);
        string? existingSha = null;
        if (existing is not null && existing.RootElement.ValueKind == JsonValueKind.Object && existing.RootElement.TryGetProperty("sha", out var sp))
        {
            existingSha = sp.GetString();
        }

        var encoded = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        object payload = existingSha is not null
            ? branch is not null
                ? new { message, content = encoded, sha = existingSha, branch }
                : new { message, content = encoded, sha = existingSha }
            : branch is not null
                ? new { message, content = encoded, branch }
                : new { message, content = encoded };

        await SendGitHubRestAsync(http, HttpMethod.Put, $"/repos/{owner}/{repo}/contents/{path}", payload, ct);
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
