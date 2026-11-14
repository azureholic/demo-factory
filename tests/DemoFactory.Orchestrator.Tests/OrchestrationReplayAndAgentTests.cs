using DemoFactory.Agents;
using DemoFactory.Copilot;
using DemoFactory.Domain;
using DemoFactory.GitHub;
using DemoFactory.Infrastructure;
using Microsoft.DurableTask;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DemoFactory.Orchestrator.Tests;

/// <summary>
/// Deterministic record-and-replay TaskOrchestrationContext harness (§3.2, §15, §18 AC 7).
/// Records activity/event/timer history on initial execution and replays recorded history against
/// WorkItemOrchestration & ProjectSchedulerOrchestration without re-executing completed activities.
/// </summary>
internal sealed class ReplayableOrchestrationHarness : TaskOrchestrationContext
{
    public sealed record HistoryTapeEntry(string Kind, string Name, object? Value);

    private readonly List<HistoryTapeEntry> _tape;
    private readonly Func<string, object?, Task<object?>> _activityExecutor;
    private readonly Queue<(string EventName, object Payload)> _externalEvents;
    private readonly bool _isReplayMode;
    private int _cursor;

    public ReplayableOrchestrationHarness(
        TaskName name,
        object input,
        List<HistoryTapeEntry> tape,
        Func<string, object?, Task<object?>> activityExecutor,
        IEnumerable<(string EventName, object Payload)> externalEvents,
        bool isReplayMode)
    {
        Name = name;
        _input = input;
        _tape = tape;
        _activityExecutor = activityExecutor;
        _externalEvents = new Queue<(string, object)>(externalEvents);
        _isReplayMode = isReplayMode;
    }

    private readonly object _input;
    public int LiveActivityInvocations { get; private set; }
    public object? ContinuedAsNewInput { get; private set; }

    public override TaskName Name { get; }
    public override string InstanceId => "test-orchestration-instance";
    public override ParentOrchestrationInstance? Parent => null;
    public override DateTime CurrentUtcDateTime => new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
    public override bool IsReplaying => _isReplayMode && _cursor < _tape.Count;
    protected override ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

    public override T GetInput<T>() => (T)_input;

    public override async Task<TResult> CallActivityAsync<TResult>(TaskName name, object? input = null, TaskOptions? options = null)
    {
        if (_isReplayMode && _cursor < _tape.Count)
        {
            var entry = _tape[_cursor++];
            if (entry.Kind != "Activity" || entry.Name != name.Name)
                throw new InvalidOperationException($"Non-deterministic replay divergence: expected {entry.Kind}:{entry.Name}, got Activity:{name.Name}");

            return (TResult)entry.Value!;
        }

        LiveActivityInvocations++;
        var res = await _activityExecutor(name.Name, input);
        _tape.Add(new HistoryTapeEntry("Activity", name.Name, res));
        _cursor++;
        return (TResult)res!;
    }

    public override Task<TResult> CallSubOrchestratorAsync<TResult>(TaskName orchestratorName, object? input = null, TaskOptions? options = null)
    {
        if (_isReplayMode && _cursor < _tape.Count)
        {
            var entry = _tape[_cursor++];
            if (entry.Kind != "SubOrchestrator" || entry.Name != orchestratorName.Name)
                throw new InvalidOperationException($"Replay divergence on SubOrchestrator {orchestratorName.Name}");

            return Task.FromResult((TResult)entry.Value!);
        }

        object? defaultResult = default(TResult);
        _tape.Add(new HistoryTapeEntry("SubOrchestrator", orchestratorName.Name, defaultResult));
        _cursor++;
        return Task.FromResult((TResult)defaultResult!);
    }

    public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken = default)
    {
        if (_isReplayMode && _cursor < _tape.Count && _tape[_cursor].Kind == "Event" && _tape[_cursor].Name == eventName)
        {
            var recorded = _tape[_cursor++];
            return Task.FromResult((T)recorded.Value!);
        }

        var items = _externalEvents.ToList();
        var matchIdx = items.FindIndex(x => string.Equals(x.EventName, eventName, StringComparison.OrdinalIgnoreCase));
        if (matchIdx >= 0)
        {
            var match = items[matchIdx];
            items.RemoveAt(matchIdx);
            _externalEvents.Clear();
            foreach (var rem in items)
                _externalEvents.Enqueue(rem);

            _tape.Add(new HistoryTapeEntry("Event", eventName, match.Payload));
            _cursor++;
            return Task.FromResult((T)match.Payload);
        }

        return new TaskCompletionSource<T>().Task;
    }

    public override void SendEvent(string instanceId, string eventName, object payload) { }
    public override void SetCustomStatus(object? customStatus) { }

    public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true)
    {
        ContinuedAsNewInput = newInput;
    }

    public override Guid NewGuid() => Guid.Parse("99999999-8888-7777-6666-555555555555");
}

public sealed class OrchestrationReplayAndAgentTests
{
    [Fact]
    public async Task WorkItemOrchestration_Executes_FixRound_To_Merged_And_Replays_History_With_Zero_Duplicate_Activities()
    {
        // §3.2, §8.2, §18 AC 7: Record history across 1 fix round + green Round 2 + merge, then replay deterministically!
        var workItemId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        var input = new WorkItemOrchestrationInput(workItemId, repoId, MaxRounds: 3, Version: "1.0");
        var tape = new List<ReplayableOrchestrationHarness.HistoryTapeEntry>();

        var policy = new RepoPolicy();
        var round1FailGate = new GateEvaluationResult(
            GateProfile.Feature, "sha_r1", Passed: false, OnlyApprovalMissing: false, HasContestedHigh: false,
            ProtectedPathsTouched: [], new GateOutcome.NeedsFix(["[C4] 1 Medium finding"], []), [], ["[C4] 1 Medium finding"]);

        var round2PassGate = new GateEvaluationResult(
            GateProfile.Feature, "sha_r2", Passed: true, OnlyApprovalMissing: false, HasContestedHigh: false,
            ProtectedPathsTouched: [], new GateOutcome.Pass(), [], []);

        Task<object?> FakeActivities(string name, object? arg) => Task.FromResult<object?>(name switch
        {
            nameof(RunSpecReviewActivity) => new SpecReviewOutput("Ready", [], []),
            nameof(ClaimAndAssignActivity) => "gemini-4-carbon",
            nameof(ArmPullRequestActivity) => true,
            nameof(EvaluateAndPostGateActivity) => ((EvaluateGateActivityInput)arg!).Round == 1 ? round1FailGate : round2PassGate,
            nameof(RequestCopilotFixRoundActivity) => true,
            nameof(CompleteMergedWorkItemActivity) => true,
            _ => true
        });

        var events = new (string, object)[]
        {
            (ExternalEvents.PrOpened, new PrOpenedEvent(42, "sha_r1", false)),
            (ExternalEvents.CopilotWorkFinished, new CopilotFinishedEvent("sha_r1", CopilotTaskState.Completed)),
            (ExternalEvents.ChecksCompleted, new RoundSignalsEvent("sha_r1", true, true, [], [], [])),
            (ExternalEvents.CopilotWorkFinished, new CopilotFinishedEvent("sha_r2", CopilotTaskState.Completed)),
            (ExternalEvents.ChecksCompleted, new RoundSignalsEvent("sha_r2", true, true, [], [], [])),
            (ExternalEvents.PrMerged, new PrMergePhaseEvent("Merged", "merge_sha_final"))
        };

        var orchestrator = new WorkItemOrchestration();

        // Run 1: Live execution records history tape
        var liveCtx = new ReplayableOrchestrationHarness(nameof(WorkItemOrchestration), input, tape, FakeActivities, events, isReplayMode: false);
        var liveOutput = await orchestrator.RunAsync(liveCtx, input);

        Assert.Equal(WorkItemStateKind.Merged, liveOutput.FinalState);
        Assert.Equal(2, liveOutput.RoundsExecuted);
        Assert.True(liveCtx.LiveActivityInvocations >= 7);

        // Run 2: Deterministic replay from recorded tape — MUST produce identical output with ZERO live activity calls!
        var replayCtx = new ReplayableOrchestrationHarness(nameof(WorkItemOrchestration), input, tape, FakeActivities, [], isReplayMode: true);
        var replayOutput = await orchestrator.RunAsync(replayCtx, input);

        Assert.Equal(0, replayCtx.LiveActivityInvocations);
        Assert.Equal(liveOutput, replayOutput);
    }

    [Fact]
    public async Task ProjectSchedulerOrchestration_Upgrades_Version_On_ContinueAsNew()
    {
        // §3.2: Eternal ProjectScheduler running on older version "0.9" upgrades to OrchestrationVersions.Current at ContinueAsNew boundary
        var repoId = Guid.NewGuid();
        var oldInput = new ProjectSchedulerInput(
            repoId,
            Version: "0.9",
            IterationCount: ProjectSchedulerOrchestration.MaxHistoryIterationsBeforeContinueAsNew - 1);

        var tape = new List<ReplayableOrchestrationHarness.HistoryTapeEntry>();
        var ctx = new ReplayableOrchestrationHarness(
            nameof(ProjectSchedulerOrchestration),
            oldInput,
            tape,
            (_, _) => Task.FromResult<object?>( (IReadOnlyList<Guid>)Array.Empty<Guid>() ),
            [(ExternalEvents.StatusChanged, "CardReady")],
            isReplayMode: false);

        var scheduler = new ProjectSchedulerOrchestration();
        await scheduler.RunAsync(ctx, oldInput);

        var nextInput = Assert.IsType<ProjectSchedulerInput>(ctx.ContinuedAsNewInput);
        Assert.Equal(OrchestrationVersions.Current, nextInput.Version);
        Assert.Equal(0, nextInput.IterationCount);
    }

    private sealed class FlakyJsonModelTransport(params string[] responses) : IProjectManagerModelTransport
    {
        private int _calls;
        public int CallCount => _calls;

        public Task<string> InvokeJsonAsync(string systemInstructions, string userPrompt, string deploymentName, CancellationToken ct = default)
        {
            var idx = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(idx < responses.Length ? responses[idx] : "not-valid-json");
        }
    }

    [Fact]
    public async Task ProjectManagerAgent_Validates_JsonSchema_Retries_Once_And_Uses_Deterministic_Fallback()
    {
        // §3.2: Schema fail on attempt 1 -> retries once -> valid schema on attempt 2 succeeds
        var retryTransport = new FlakyJsonModelTransport(
            """{"wrongField": 123}""",
            """{"verdict": "Ready", "missing": [], "questions": []}""");

        var pm1 = new ProjectManagerAgent(
            retryTransport,
            new NullPmTraceSink(),
            Options.Create(new FoundryAgentOptions()),
            NullLogger<ProjectManagerAgent>.Instance);

        var res1 = await pm1.ReviewSpecAsync(new IssueSpecInput(1, "Test", "Valid spec body with details", ["AC1"], ItemSize.Small, []));
        Assert.Equal(2, retryTransport.CallCount);
        Assert.Equal("Ready", res1.Verdict);

        // MAI-Thinking-1 chain-of-thought (<think>...</think>) before JSON is stripped cleanly on first attempt
        var thinkingTransport = new FlakyJsonModelTransport(
            "<think>\nLet me analyze the acceptance criteria { internal braces } carefully.\n</think>\n```json\n{\"verdict\": \"Ready\", \"missing\": [], \"questions\": []}\n```");
        var pmThink = new ProjectManagerAgent(
            thinkingTransport,
            new NullPmTraceSink(),
            Options.Create(new FoundryAgentOptions()),
            NullLogger<ProjectManagerAgent>.Instance);
        Assert.Equal("orchestrator-model", new FoundryAgentOptions().DefaultDeployment);
        var resThink = await pmThink.ReviewSpecAsync(new IssueSpecInput(3, "MAI Thinking Test", "Detailed spec", ["AC1"], ItemSize.Small, []));
        Assert.Equal(1, thinkingTransport.CallCount);
        Assert.Equal("Ready", resThink.Verdict);

        // Both attempts invalid -> deterministic fallback kicks in cleanly
        var brokenTransport = new FlakyJsonModelTransport("broken-1", """{"verdict": "INVALID_ENUM"}""");
        var pm2 = new ProjectManagerAgent(
            brokenTransport,
            new NullPmTraceSink(),
            Options.Create(new FoundryAgentOptions()),
            NullLogger<ProjectManagerAgent>.Instance);

        var res2 = await pm2.ReviewSpecAsync(new IssueSpecInput(2, "Empty issue", "", [], ItemSize.Small, []));
        Assert.Equal(2, brokenTransport.CallCount);
        Assert.Equal("NeedsClarification", res2.Verdict);
        Assert.NotEmpty(res2.Questions);

        // Public-safe check catches GitHub PATs, internal corporate URLs, and personal emails
        var publicCheck = pm2.CheckPublicSafeDeterministic(
            "Connect to https://api.corp.microsoft.com with token ghp_1234567890abcdefghijklmnopqrstuv and email alice@contoso.com");
        Assert.False(publicCheck.Passed);
        Assert.True(publicCheck.Flags.Count >= 3);
    }

    [Fact]
    public async Task CopilotAgentClient_Prevents_Concurrent_Sessions_On_Same_WorkItem()
    {
        // §8.2: Only ONE Copilot session per PR/work-item may be active at any time
        var dbPath = Path.Combine(Path.GetTempPath(), $"df-orch-test-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(null);
            builder.Configuration["ConnectionStrings:Sqlite"] = $"Data Source={dbPath}";
            builder.Configuration["Factory:SandboxMode"] = "true";
            var config = builder.Configuration;

            services.AddLogging();
            services.AddDemoFactoryInfrastructure(config);
            services.AddDemoFactoryGitHub(config);
            services.AddDemoFactoryCopilot();

            await using var sp = services.BuildServiceProvider();
            var dbFactory = sp.GetRequiredService<IDbContextFactory<DemoFactoryDbContext>>();
            await DemoFactorySeeder.EnsureSeededAsync(dbFactory, "azureholic", seedDemoData: false);

            var copilotClient = sp.GetRequiredService<ICopilotAgentClient>();
            var wiId = Guid.NewGuid();
            var repoId = Guid.NewGuid();

            await copilotClient.AssignIssueAsync(new CopilotAssignmentRequest(
                wiId, repoId, "azureholic", "sample", 1, "main", "Implement #1", "gemini-4-carbon"));

            // Second concurrent assignment or @copilot comment while session is InProgress MUST throw
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                copilotClient.PostBatchedFixCommentAsync(new BatchedFixRequest(
                    wiId, repoId, "azureholic", "sample", 10, "gemini-4-carbon", [], ["Build failed"], [])));
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }
}
