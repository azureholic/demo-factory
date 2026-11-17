using System.Net;
using System.Net.Http.Json;
using DemoFactory.Agents;
using DemoFactory.Copilot;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DemoFactory.Orchestrator.Tests;

public sealed class PrProgressWatchdogTests
{
    [Fact]
    public async Task StalledChecks_CancelThenRerun_WithPersistentBudgetAndCooldown()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.TickAsync();
        Assert.Empty(await fixture.ReadAsync(db => db.GateEvaluations.ToListAsync()));
        await fixture.AgeProgressAsync();

        await fixture.TickAsync();
        Assert.Equal(1, fixture.CancelCount);
        await fixture.TickAsync();
        Assert.Equal(0, fixture.RerunCount);
        Assert.Equal(WorkItemStateKind.Validating, await fixture.StateAsync());
        await fixture.AgeRecoveryAsync(4);
        await fixture.TickAsync();
        Assert.Equal(0, fixture.RerunCount);

        await fixture.AgeRecoveryAsync();
        await fixture.TickAsync();
        Assert.Equal(1, fixture.RerunCount);
        await fixture.TickAsync();
        await fixture.AgeProgressAsync();
        await fixture.AgeRecoveryAsync();
        await fixture.TickAsync();
        Assert.Equal(WorkItemStateKind.Blocked, await fixture.StateAsync());
        Assert.Equal(2, (await fixture.ReadAsync(db => db.AuditEvents.Where(a => a.Action == "PrWatchdogRecovery").ToListAsync())).Count);
        await fixture.TickAsync();
        Assert.Equal(1, fixture.CancelCount);
        Assert.Equal(1, fixture.RerunCount);
    }

    [Fact]
    public async Task RealCheckProgress_ResetsInactivity_NotOrdinaryPolling()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CheckStatus = "queued";
        await fixture.TickAsync();
        await fixture.AgeProgressAsync(19);
        await fixture.TickAsync();
        Assert.Equal(0, fixture.CancelCount);
        await fixture.AgeProgressAsync();
        fixture.CheckStatus = "in_progress";
        await fixture.TickAsync();
        Assert.Equal(0, fixture.CancelCount);
        await fixture.AgeProgressAsync();
        await fixture.TickAsync();
        Assert.Equal(1, fixture.CancelCount);
    }

    [Fact]
    public async Task MissingRequiredJob_EscalatesWithoutRerunningUnrelatedWorkflow()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.JobName = "unrelated-job";
        await fixture.TickAsync();
        await fixture.AgeProgressAsync();
        await fixture.TickAsync();
        Assert.Equal(WorkItemStateKind.Blocked, await fixture.StateAsync());
        Assert.Equal(0, fixture.CancelCount);
        Assert.Equal(0, fixture.RerunCount);
    }

    [Theory]
    [InlineData(WorkItemStateKind.AwaitingApproval, AutonomyLevel.L1)]
    [InlineData(WorkItemStateKind.Validating, AutonomyLevel.L0)]
    public async Task ApprovalAndObserveOnlyMode_NotifyOnceWithoutRecovery(WorkItemStateKind state, AutonomyLevel autonomy)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.WriteAsync(db => db.WorkItems.Single().State = state);
        await fixture.WriteAsync(db => db.RuntimeSettings.Add(new RuntimeSettingsEntity
        {
            Scope = SettingsScope.Repo, TargetId = fixture.RepoId, Autonomy = autonomy
        }));
        await fixture.TickAsync();
        await fixture.AgeProgressAsync();
        await fixture.TickAsync();
        await fixture.TickAsync();
        Assert.Equal(state, await fixture.StateAsync());
        Assert.Equal(1, fixture.CommentCount);
        Assert.Equal(0, fixture.CancelCount);
        Assert.Empty(await fixture.ReadAsync(db => db.Approvals.ToListAsync()));
    }

    [Fact]
    public async Task RecoveryPermissionFailure_IsVisibleAndEscalates()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.DenyRecovery = true;
        await fixture.TickAsync();
        await fixture.AgeProgressAsync();
        await fixture.TickAsync();
        var item = await fixture.ReadAsync(db => db.WorkItems.SingleAsync());
        Assert.Equal(WorkItemStateKind.Blocked, item.State);
        Assert.Contains("failed", item.BlockedReason);
        Assert.True(fixture.CommentCount > 0);
    }

    [Fact]
    public async Task GatewayRefusesRecoveryAfterHeadChangesOrForUnrelatedRun()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gateway = fixture.Services.GetRequiredService<IGitHubGateway>();
        Assert.False(await gateway.TryRecoverWorkflowRunAsync("azureholic", "watchdog-test", 10, "old-sha", 123, true));
        fixture.RunPrNumber = 999;
        Assert.False(await gateway.TryRecoverWorkflowRunAsync("azureholic", "watchdog-test", 10, "head-sha", 123, true));
        Assert.Equal(0, fixture.CancelCount);
    }

    [Fact]
    public async Task PendingReviewer_DoesNotStartUnnecessaryCodingSession_AndIsRetriedWhenStalled()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.CheckStatus = fixture.RunStatus = "completed";
        fixture.Conclusion = "success";
        fixture.ReviewerRequested = true;
        await fixture.TickAsync();
        Assert.Empty(await fixture.ReadAsync(db => db.AgentSessions.ToListAsync()));
        await fixture.AgeProgressAsync();
        await fixture.TickAsync();
        Assert.Equal(1, fixture.ReviewRequestCount);
        Assert.Empty(await fixture.ReadAsync(db => db.AgentSessions.ToListAsync()));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"df-watchdog-{Guid.NewGuid():N}");
        public ServiceProvider Services { get; private set; } = null!;
        private IDbContextFactory<DemoFactoryDbContext> DbFactory => Services.GetRequiredService<IDbContextFactory<DemoFactoryDbContext>>();
        public Guid RepoId { get; private set; }
        public string CheckStatus { get; set; } = "in_progress";
        public string? Conclusion { get; set; }
        public string RunStatus { get; set; } = "in_progress";
        public string JobName { get; set; } = "build";
        public int RunPrNumber { get; set; } = 10;
        public bool DenyRecovery { get; set; }
        public bool ReviewerRequested { get; set; }
        public int ReviewRequestCount { get; private set; }
        public int CancelCount { get; private set; }
        public int RerunCount { get; private set; }
        public int CommentCount { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture._directory);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={Path.Combine(fixture._directory, "test.db")};Pooling=False",
                ["Factory:SandboxMode"] = "false",
                ["Factory:LocalDataDirectory"] = fixture._directory,
                ["Factory:PoliciesDirectory"] = fixture._directory,
                ["Foundry:ProjectEndpoint"] = "",
                ["GitHub:WritePaceMilliseconds"] = "1"
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddDemoFactoryInfrastructure(configuration);
            services.AddDemoFactoryGitHub(configuration);
            services.AddDemoFactoryCopilot();
            services.AddDemoFactoryAgents(configuration);
            services.AddDemoFactoryOrchestrator(configuration, registerBackgroundWorkers: false);
            services.AddHttpClient("GitHubApi").ConfigurePrimaryHttpMessageHandler(() => new Handler(fixture.Respond));
            fixture.Services = services.BuildServiceProvider();
            await DemoFactorySeeder.EnsureSeededAsync(fixture.DbFactory, seedDemoData: false);
            await fixture.Services.GetRequiredService<ITokenBroker>().StoreRotatedTokensAsync(
                "azureholic", "test-token", TimeSpan.FromHours(1), "test-refresh", TimeSpan.FromDays(1));
            var policy = new RepoPolicy { Repository = "azureholic/watchdog-test", HasDeployTarget = false };
            policy.Gate.RequiredChecks = ["build"];
            await File.WriteAllTextAsync(Path.Combine(fixture._directory, "azureholic__watchdog-test.yml"), policy.ToYaml());
            await fixture.WriteAsync(db =>
            {
                var repo = new RepositoryEntity { Owner = "azureholic", Name = "watchdog-test" };
                fixture.RepoId = repo.Id;
                db.Repositories.Add(repo);
                db.WorkItems.Add(new WorkItemEntity
                {
                    RepoId = repo.Id, IssueNumber = 1, IssueNodeId = "factory-created-issue",
                    IssueAuthor = "azureholic", WorkLabelAppliedBy = "azureholic",
                    State = WorkItemStateKind.Validating, PrNumber = 10, PrUrl = "https://github.com/azureholic/watchdog-test/pull/10",
                    HeadSha = "head-sha", BoardStatus = BoardStatus.InReview
                });
            });
            return fixture;
        }

        // A fresh coordinator on every tick also exercises recovery across process-local state loss.
        public Task<int> TickAsync() => ActivatorUtilities.CreateInstance<FactoryOrchestrationCoordinator>(Services)
            .ReconcileActiveWorkItemsOnceAsync(RepoId);
        public Task<WorkItemStateKind> StateAsync() => ReadAsync(db => db.WorkItems.Select(w => w.State).SingleAsync());
        public Task AgeProgressAsync(int minutes = 21) => AgeAsync("PrProgressObserved", minutes);
        public Task AgeRecoveryAsync(int minutes = 6) => AgeAsync("PrWatchdogRecovery", minutes);
        private Task AgeAsync(string action, int minutes) => WriteAsync(db =>
        {
            foreach (var item in db.AuditEvents.Where(a => a.Action == action))
                item.Timestamp = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        });
        public async Task<T> ReadAsync<T>(Func<DemoFactoryDbContext, Task<T>> read)
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            return await read(db);
        }
        public async Task WriteAsync(Action<DemoFactoryDbContext> write)
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            write(db);
            await db.SaveChangesAsync();
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            object Pr() => new
            {
                number = 10, node_id = "PR_10", title = "Implementation", body = "Fixes #1",
                draft = false, state = "open", merged = false, mergeable_state = "clean",
                requested_reviewers = ReviewerRequested ? new[] { new { login = "copilot-pull-request-reviewer[bot]" } } : Array.Empty<object>(),
                user = new { login = "azureholic" },
                head = new { @ref = "copilot/test", sha = "head-sha", repo = new { fork = false, full_name = "azureholic/watchdog-test" } },
                @base = new { @ref = "main" }
            };
            object Run() => new
            {
                id = 123, head_sha = "head-sha", @event = "pull_request", status = RunStatus, conclusion = Conclusion,
                head_repository = new { full_name = "azureholic/watchdog-test" }, pull_requests = new[] { new { number = RunPrNumber } }
            };
            if (request.Method == HttpMethod.Post && (path.EndsWith("/cancel") || path.EndsWith("/rerun")))
            {
                if (DenyRecovery)
                    return Json(new { message = "Actions permission denied" }, HttpStatusCode.Forbidden);
                if (path.EndsWith("/cancel"))
                {
                    CancelCount++;
                    RunStatus = CheckStatus = "completed";
                    Conclusion = "cancelled";
                }
                else
                {
                    RerunCount++;
                    RunStatus = CheckStatus = "queued";
                    Conclusion = null;
                }
                return Json(new { });
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/comments"))
                CommentCount++;
            if (request.Method == HttpMethod.Post && path.EndsWith("/requested_reviewers"))
                ReviewRequestCount++;
            if (path.EndsWith("/pulls")) return Json(new[] { Pr() });
            if (path.EndsWith("/pulls/10")) return Json(Pr());
            if (path.EndsWith("/actions/runs")) return Json(new { workflow_runs = new[] { Run() } });
            if (path.EndsWith("/actions/runs/123")) return Json(Run());
            if (path.EndsWith("/jobs")) return Json(new { jobs = new[] { new { name = JobName } } });
            if (path.EndsWith("/check-runs")) return Json(new
            {
                check_runs = new[] { new { name = "build", status = CheckStatus, conclusion = Conclusion, app = new { slug = "github-actions" } } }
            });
            if (path.EndsWith("/timeline") || path.EndsWith("/rulesets") || path.EndsWith("/reviews") || path.EndsWith("/comments") || path.EndsWith("/files"))
                return Json(Array.Empty<object>());
            return Json(new { ok = true });
        }

        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value) };
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
