using DbDataBuild.Apply;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;

namespace DbDataBuild.Tests.Unit;

/// <summary>Refresh plans, step 5 (docs/research/refresh-plans.md, section 16): the scheduler that starts loads together, the engine under it with a fake load runner, and the cap on sessions at one connection.</summary>
public class RefreshParallelTests
{
    // ---------------------------------------------------------------- the scheduler on its own

    private sealed class Trace
    {
        private readonly object gate = new();
        public List<string> Events { get; } = [];
        public int Running, MostRunning;
        public void Started(int i) { lock (gate) { Events.Add($"start {i}"); MostRunning = Math.Max(MostRunning, ++Running); } }
        public void Ended(int i) { lock (gate) { Events.Add($"end {i}"); Running--; } }
    }

    private static Task Schedule(int count, int parallel, Func<int, int[]> deps, Trace trace, Func<int, Task>? work = null, Func<bool>? halted = null, Func<int, Task>? finished = null, List<int>? immediate = null) =>
        RefreshScheduler.RunAsync(count, parallel, i => deps(i), halted ?? (() => false),
            i =>
            {
                if (halted?.Invoke() == true) { immediate?.Add(i); return Task.FromResult<Func<Task>?>(null); }
                return Task.FromResult<Func<Task>?>(async () => { trace.Started(i); try { await (work?.Invoke(i) ?? Task.Delay(5)); } finally { trace.Ended(i); } });
            },
            finished ?? (_ => Task.CompletedTask), CancellationToken.None);

    [Fact]
    public async Task One_at_a_time_is_the_order_of_the_plan()
    {
        var trace = new Trace();
        await Schedule(6, 1, i => Enumerable.Range(0, i).ToArray(), trace);
        Assert.Equal(Enumerable.Range(0, 6).SelectMany(i => new[] { $"start {i}", $"end {i}" }), trace.Events);
        Assert.Equal(1, trace.MostRunning);
    }

    [Fact]
    public async Task Independent_units_run_together_up_to_the_cap()
    {
        var trace = new Trace();
        await Schedule(12, 4, _ => [], trace, work: _ => Task.Delay(60));
        Assert.Equal(4, trace.MostRunning);
        Assert.Equal(12, trace.Events.Count(e => e.StartsWith("end", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_unit_starts_only_after_everything_it_waits_for_has_ended()
    {
        // 0 and 1 are free; 2 reads 0; 3 reads 0 and 1; 4 reads 2 and 3
        int[][] deps = [[], [], [0], [0, 1], [2, 3]];
        var trace = new Trace();
        await Schedule(5, 3, i => deps[i], trace, work: i => Task.Delay(i == 1 ? 80 : 10));
        int At(string e) => trace.Events.IndexOf(e);
        for (var i = 0; i < 5; i++)
            foreach (var d in deps[i]) Assert.True(At($"end {d}") < At($"start {i}"), $"{i} started before {d} ended: {string.Join(", ", trace.Events)}");
    }

    [Fact]
    public async Task A_halt_starts_nothing_more_admits_the_rest_as_not_run_and_lets_the_running_finish()
    {
        var halted = false;
        var trace = new Trace();
        var immediate = new List<int>();
        var finished = new List<int>();
        await Schedule(8, 2, _ => [], trace, work: async i => { await Task.Delay(i == 0 ? 10 : 80); }, halted: () => halted, immediate: immediate,
            finished: i => { finished.Add(i); if (i == 0) halted = true; return Task.CompletedTask; });
        Assert.Contains("start 1", trace.Events);                                                    // 1 was running when 0 ended and halted the run
        Assert.True(trace.Events.Count(e => e.StartsWith("end", StringComparison.Ordinal)) <= 3);
        Assert.Equal(8, finished.Count);                                                             // every unit is settled exactly once
        Assert.Equal(8, finished.Distinct().Count());
        Assert.NotEmpty(immediate);
        Assert.Equal(immediate.Order().ToList(), immediate);                                         // the ones that did not run were settled in the order of the plan
    }

    [Fact]
    public async Task An_exception_in_the_work_ends_the_run_after_the_running_units_ended()
    {
        var trace = new Trace();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Schedule(6, 3, _ => [], trace, work: async i => { if (i == 0) { await Task.Delay(5); throw new InvalidOperationException("boom"); } await Task.Delay(60); }));
        Assert.Equal("boom", ex.Message);
        Assert.Equal(0, trace.Running);                                                              // nothing is left running when the exception arrives
    }

    [Fact]
    public async Task Random_plans_under_stress_keep_the_order_the_cap_and_settle_every_unit_once()
    {
        for (var round = 0; round < 25; round++)
        {
            var random = new Random(round);
            var count = random.Next(20, 80);
            var deps = Enumerable.Range(0, count).Select(i => Enumerable.Range(0, i).Where(_ => random.Next(100) < 8).ToArray()).ToArray();
            var delays = Enumerable.Range(0, count).Select(_ => random.Next(0, 6)).ToArray();
            var parallel = random.Next(1, 9);
            var trace = new Trace();
            var finished = new List<int>();
            await Schedule(count, parallel, i => deps[i], trace, work: i => Task.Delay(delays[i]), finished: i => { finished.Add(i); return Task.CompletedTask; });
            Assert.True(trace.MostRunning <= parallel);
            Assert.Equal(Enumerable.Range(0, count), finished.Order());
            var order = trace.Events.Select((e, k) => (e, k)).ToDictionary(x => x.e, x => x.k);
            for (var i = 0; i < count; i++) foreach (var d in deps[i]) Assert.True(order[$"end {d}"] < order[$"start {i}"]);
        }
    }

    // ---------------------------------------------------------------- the engine with a fake load runner

    /// <summary>The write side of a fake connection: a statement `work:&lt;model&gt;:&lt;ms&gt;:&lt;ok|fail&gt;` takes that long and ends as it says; how many ran at once, across every session of the run, is counted.</summary>
    private sealed class Fleet
    {
        public int Running, MostRunning, Opened;
        public readonly List<string> Locks = [];
        public readonly List<string> Order = [];
        private readonly object gate = new();
        public void Enter(string name) { lock (gate) { MostRunning = Math.Max(MostRunning, ++Running); Order.Add("start " + name); } }
        public void Leave(string name) { lock (gate) { Running--; Order.Add("end " + name); } }
        public void Lock(string resource) { lock (gate) Locks.Add(resource); }
    }

    private sealed class FakeConnection(Fleet fleet) : IWriteExecutor
    {
        public async Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
        {
            var parts = text.Split(':');
            fleet.Enter(parts[1]);
            try
            {
                await Task.Delay(int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), ct);
                if (parts[3] == "fail") throw new InvalidOperationException("the load failed");
                return 7;
            }
            finally { fleet.Leave(parts[1]); }
        }
        public Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct) { fleet.Lock(resource + (shared ? "/shared" : "/exclusive")); return Task.FromResult(true); }
        public Task UnlockAsync(string resource, bool shared, CancellationToken ct) => Task.CompletedTask;
        public Task RecoverAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Spec(string Model, int Ms = 20, bool Fails = false, string OnFailure = OnFailureModes.Continue, string[]? Reads = null);

    private static async Task<(ApplyResult Result, Fleet Fleet, MemoryStatementLog Log)> RunAsync(Spec[] specs, int parallel)
    {
        var fleet = new Fleet();
        var log = new MemoryStatementLog();
        var units = specs.Select((s, i) => new RefreshUnit(i + 1, "default", s.Model, "default", s.Model + ".default", "h" + i, null, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), s.OnFailure, OnBusyModes.Skip, TimeSpan.Zero, null, null, null)).ToList();
        // a model reads what the ones it reads read: the closure, as the project graph gives
        IReadOnlySet<string> Downstream(string model)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal); var todo = new Queue<string>();
            todo.Enqueue(model);
            while (todo.Count > 0)
            {
                var m = todo.Dequeue();
                foreach (var s in specs.Where(s => (s.Reads ?? []).Contains(m)))
                    if (seen.Add(s.Model)) todo.Enqueue(s.Model);
            }
            return seen;
        }
        PrepareLoad prepare = (unit, session, ct) =>
        {
            var s = specs[unit.Position - 1];
            var step = new PlanStep(unit.LoadStepId, StepType.Load, s.Model, "load", $"work:{s.Model}:{s.Ms}:{(s.Fails ? "fail" : "ok")}", RiskClass.Safe, [], null, []);
            return Task.FromResult(new PreparedLoad([step], "[]"));
        };
        var options = new ApplyOptions(false, false, new HashSet<string>(), null, false, "test", Lane: "refresh", Refresh: true, RecordShapes: false, Execution: new RefreshExecution(units, prepare, Downstream), Parallel: parallel);
        var gate = MutationGate.ForTesting(new FakeConnection(fleet), "apply", StatementKind.Tracking | StatementKind.Data | StatementKind.Ddl, log);
        var plan = new Plan("p1", "sqlserver", null, false, "t", [], [], [], []);
        Func<CancellationToken, Task<RefreshWorker>> open = _ =>
        {
            Interlocked.Increment(ref fleet.Opened);
            return Task.FromResult(new RefreshWorker(ReadSession.ForTesting(null!), gate.SiblingForTesting(new FakeConnection(fleet)), Tracker.None));
        };
        var result = await RefreshEngine.RunAsync(plan, "", "plan-hash", ReadSession.ForTesting(null!), Tracker.None, gate, open, options, Guid.NewGuid(), "lock", _ => { }, CancellationToken.None);
        return (result, fleet, log);
    }

    private static string[] Models(int count) => Enumerable.Range(0, count).Select(i => $"m{i}").ToArray();

    [Fact]
    public async Task Independent_loads_run_together_and_the_results_stay_in_the_order_of_the_plan()
    {
        var specs = Models(8).Select((m, i) => new Spec(m, Ms: 80 - i * 8)).ToArray();           // the first ones are the slowest, so they end last
        var (result, fleet, _) = await RunAsync(specs, 3);
        Assert.Equal(RefreshSummary.Completed, result.Refresh!.Outcome);
        Assert.Equal(3, fleet.MostRunning);
        Assert.Equal(specs.Select(s => s.Model), result.Refresh.Loads.Select(l => l.Unit.Model));
        Assert.All(result.Refresh.Loads, l => Assert.Equal(LoadStatus.Done, l.Status));
        Assert.All(result.Refresh.Loads, l => Assert.Equal(7, l.Rows));
        Assert.Equal(3, fleet.Opened);                                                             // one session set per load running together, never more
    }

    [Fact]
    public async Task Parallel_1_goes_one_at_a_time_on_the_runs_own_session()
    {
        var (result, fleet, _) = await RunAsync(Models(5).Select(m => new Spec(m, Ms: 5)).ToArray(), 1);
        Assert.Equal(RefreshSummary.Completed, result.Refresh!.Outcome);
        Assert.Equal(1, fleet.MostRunning);
        Assert.Equal(0, fleet.Opened);
        Assert.Equal(Models(5).SelectMany(m => new[] { "start " + m, "end " + m }), fleet.Order);
    }

    [Fact]
    public async Task A_load_waits_for_what_it_reads_and_two_loads_of_one_model_do_not_overlap()
    {
        var specs = new[] { new Spec("a", 60), new Spec("b", 5), new Spec("c", 5, Reads: ["a"]), new Spec("a", 20) };
        var (result, fleet, _) = await RunAsync(specs, 4);
        Assert.Equal(RefreshSummary.Completed, result.Refresh!.Outcome);
        Assert.True(fleet.Order.IndexOf("end a") < fleet.Order.IndexOf("start c"));
        Assert.Equal(2, fleet.Order.Count(e => e == "start a"));
        var firstA = fleet.Order.IndexOf("end a");
        Assert.True(fleet.Order.IndexOf("start a", fleet.Order.IndexOf("start a") + 1) > firstA);       // the second load of `a` began after the first ended
    }

    [Fact]
    public async Task Under_continue_a_failure_skips_only_what_reads_it_whatever_runs_beside_it()
    {
        var specs = new[] { new Spec("a", 10, Fails: true), new Spec("b", 40), new Spec("c", 5, Reads: ["a"]), new Spec("d", 5, Reads: ["c"]), new Spec("e", 5, Reads: ["b"]) };
        var (result, _, _) = await RunAsync(specs, 3);
        var byModel = result.Refresh!.Loads.ToDictionary(l => l.Unit.Model);
        Assert.Equal("failed", byModel["a"].State);
        Assert.Equal(LoadStatus.Done, byModel["b"].Status);
        Assert.Equal(LoadStatus.SkippedUpstreamFailed, byModel["c"].Status);
        Assert.Equal(LoadStatus.SkippedUpstreamFailed, byModel["d"].Status);
        Assert.Equal(LoadStatus.Done, byModel["e"].Status);
        Assert.Equal(RefreshSummary.CompletedWithFailures, result.Refresh.Outcome);
    }

    [Fact]
    public async Task Under_stop_the_loads_in_flight_finish_and_nothing_else_starts()
    {
        // a fails at once under stop; b and c are running beside it and finish; d to g never start
        var specs = new[] { new Spec("a", 10, Fails: true, OnFailure: OnFailureModes.Stop), new Spec("b", 120), new Spec("c", 120), new Spec("d", 5), new Spec("e", 5), new Spec("f", 5), new Spec("g", 5) };
        var (result, fleet, _) = await RunAsync(specs, 3);
        var byModel = result.Refresh!.Loads.ToDictionary(l => l.Unit.Model);
        Assert.Equal(RefreshSummary.Stopped, result.Refresh.Outcome);
        Assert.Equal("failed", byModel["a"].State);
        Assert.Equal(LoadStatus.Done, byModel["b"].Status);
        Assert.Equal(LoadStatus.Done, byModel["c"].Status);
        Assert.All(new[] { "d", "e", "f", "g" }, m => Assert.Equal(LoadStatus.NotRun, byModel[m].Status));
        Assert.DoesNotContain("start d", fleet.Order);
    }

    [Fact]
    public async Task Under_ignore_a_failure_is_a_warning_and_what_reads_it_runs()
    {
        var specs = new[] { new Spec("a", 5, Fails: true, OnFailure: OnFailureModes.Ignore), new Spec("b", 5, Reads: ["a"]) };
        var (result, _, _) = await RunAsync(specs, 2);
        Assert.Equal(RefreshSummary.Completed, result.Refresh!.Outcome);
        Assert.Single(result.Refresh.Warnings);
        Assert.Equal(LoadStatus.Done, result.Refresh.Loads[1].Status);
    }

    [Fact]
    public async Task Each_load_takes_its_own_lock_on_its_own_session_and_the_log_is_one_ordered_sequence()
    {
        var (result, fleet, log) = await RunAsync(Models(10).Select(m => new Spec(m, Ms: 15)).ToArray(), 4);
        Assert.Equal(RefreshSummary.Completed, result.Refresh!.Outcome);
        Assert.All(Models(10), m => Assert.Contains($"lock:{m}/exclusive", fleet.Locks));
        Assert.Equal(10, fleet.Locks.Count(l => l == "lock/shared"));
        // the opening line of every statement of every session: each ordinal once, in the order they were taken
        var begins = log.Entries.Where(e => e.Phase is "begin" or "recover").Select(e => e.Ordinal).ToList();
        Assert.Equal(begins.Count, begins.Distinct().Count());
        Assert.Equal(begins.Order().ToList(), begins);
        Assert.Equal(Enumerable.Range(1, begins.Max()).Count(), begins.Count);
    }

    [Fact]
    public async Task The_same_plan_gives_the_same_results_at_every_parallel()
    {
        var specs = new[] { new Spec("a", 10), new Spec("b", 30, Fails: true), new Spec("c", 5, Reads: ["b"]), new Spec("d", 5, Reads: ["a"]), new Spec("e", 15) };
        string Shape(ApplyResult r) => string.Join("|", r.Refresh!.Loads.Select(l => $"{l.Unit.Display}={l.Status}")) + "/" + r.Refresh.Outcome;
        var one = Shape((await RunAsync(specs, 1)).Result);
        foreach (var n in new[] { 2, 3, 5 }) Assert.Equal(one, Shape((await RunAsync(specs, n)).Result));
    }

    // ---------------------------------------------------------------- the cap on sessions at one connection

    [Fact]
    public async Task A_connection_with_max_parallel_never_has_more_sessions_open_than_that()
    {
        var limits = new SessionLimits(new Dictionary<string, int> { ["shop"] = 2 });
        var live = 0; var most = 0;
        async Task Use(int ms)
        {
            using var lease = await limits.AcquireAsync("shop", CancellationToken.None);
            var now = Interlocked.Increment(ref live);
            lock (limits) most = Math.Max(most, now);
            await Task.Delay(ms);
            Interlocked.Decrement(ref live);
        }
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Use(25)));
        Assert.Equal(2, most);
        Assert.Equal(2, limits.MostInUse("shop"));
        Assert.Equal(2, limits.CapOf("shop"));
        Assert.Null(limits.CapOf("other"));
        using (await limits.AcquireAsync("other", CancellationToken.None)) { }                      // no cap, no wait, still counted
        Assert.Equal(1, limits.MostInUse("other"));
    }

    [Fact]
    public async Task A_place_is_given_back_when_the_lease_is_disposed_even_twice()
    {
        var limits = new SessionLimits(new Dictionary<string, int> { ["shop"] = 1 });
        var first = await limits.AcquireAsync("shop", CancellationToken.None);
        var waiting = limits.AcquireAsync("shop", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        first.Dispose(); first.Dispose();
        (await waiting).Dispose();
        Assert.Equal(1, limits.MostInUse("shop"));
    }

    // ---------------------------------------------------------------- a loads file with no graph

    [Fact]
    public void A_loads_file_with_no_graph_makes_a_parallel_run_sequential_and_says_so()
    {
        var (parallel, warning) = DbDataBuild.Cli.RefreshCommand.ParallelFor(8, hasGraph: false);
        Assert.Equal(1, parallel);
        Assert.StartsWith("parallel: 1 (the loads file has no dependency graph", warning);
        Assert.Contains("8 was asked for", warning);
    }

    [Fact]
    public void A_loads_file_with_a_graph_keeps_the_asked_for_number_and_a_sequential_run_needs_no_graph()
    {
        Assert.Equal((4, (string?)null), DbDataBuild.Cli.RefreshCommand.ParallelFor(4, hasGraph: true));
        Assert.Equal((1, (string?)null), DbDataBuild.Cli.RefreshCommand.ParallelFor(1, hasGraph: false));
    }

    // ---------------------------------------------------------------- the settings

    [Fact]
    public void A_connection_reads_max_parallel_from_one_to_sixty_four()
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.Load("connections:\n  shop: { engine: postgres, max_parallel: 3 }\n  hq: { engine: sqlserver }\n", "dbdatabuild.yml", diags);
        Assert.Empty(diags.Where(d => d.Severity == Severity.Error));
        Assert.Equal(3, config!.Connections["shop"].MaxParallel);
        Assert.Null(config.Connections["hq"].MaxParallel);
        foreach (var bad in new[] { "0", "65", "many", "-1", "1.5" })
        {
            var d = new List<Diagnostic>();
            ProjectConfigLoader.Load($"connections:\n  shop: {{ engine: postgres, max_parallel: {bad} }}\n", "dbdatabuild.yml", d);
            Assert.Contains(d, x => x.Severity == Severity.Error && x.Found.Contains("max_parallel", StringComparison.Ordinal));
        }
    }
}
