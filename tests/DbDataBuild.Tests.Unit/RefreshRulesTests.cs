using DbDataBuild.Apply;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Refresh plans, step 2 (docs/research/refresh-plans.md, section 6): the settings that act in a run, the states a load ends in, and the lock that can be shared and waited for. The runs themselves are in the conformance tests.</summary>
public class RefreshRulesTests
{
    [Fact]
    public void A_span_has_seconds_to_weeks_and_nothing_else()
    {
        Assert.Equal(30, EveryDuration.TryParse("30s")!.Seconds);
        Assert.Equal(5400, EveryDuration.TryParse("90m")!.Seconds);
        Assert.Equal(86400, EveryDuration.TryParse("1d")!.Seconds);
        Assert.Equal(60, EveryDuration.TryParse("1h")!.Minutes);
        foreach (var bad in new[] { "", "0s", "1.5h", "h", "10", "1y", "-1m", "1 h" }) Assert.Null(EveryDuration.TryParse(bad));
    }

    [Fact]
    public void A_status_text_says_the_state_and_the_reason()
    {
        Assert.Equal(("done", null), (LoadStatus.StateOf("ok"), LoadStatus.ReasonOf("ok")));
        Assert.Equal(("failed", "check"), (LoadStatus.StateOf(LoadStatus.FailedCheck), LoadStatus.ReasonOf(LoadStatus.FailedCheck)));
        Assert.Equal(("failed", null), (LoadStatus.StateOf("failed"), LoadStatus.ReasonOf("failed")));
        Assert.Equal(("skipped", "busy"), (LoadStatus.StateOf(LoadStatus.SkippedBusy), LoadStatus.ReasonOf(LoadStatus.SkippedBusy)));
        Assert.Equal(("not run", null), (LoadStatus.StateOf(LoadStatus.NotRun), LoadStatus.ReasonOf(LoadStatus.NotRun)));
        Assert.False(LoadStatus.NeedsAnotherRun("ok"));
        Assert.All(new[] { "failed", LoadStatus.SkippedBusy, LoadStatus.NotRun, LoadStatus.SkippedUpstreamFailed }, s => Assert.True(LoadStatus.NeedsAnotherRun(s)));
    }

    [Fact]
    public void The_plan_settings_that_act_are_read_validated_and_kept_in_the_plan_file()
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.Load("refresh:\n  plans:\n    p: { on_failure: stop, on_busy: wait, max_wait: 10m, max_runtime: 30s, expected_every: 1h }\n", "dbdatabuild.yml", diags);
        Assert.NotNull(config);
        var p = config!.RefreshPlans["p"];
        Assert.Equal((600, 30), (p.MaxWait!.Seconds, p.MaxRuntime!.Seconds));
        var text = RefreshPlanSettingsText.From(p);
        Assert.Equal(("10m", "30s"), (text.MaxWait, text.MaxRuntime));
        foreach (var bad in new[] { "max_wait: soon", "max_runtime: 0s", "max_runtime: [1]" })
        {
            var d = new List<Diagnostic>();
            Assert.Null(ProjectConfigLoader.Load($"refresh:\n  plans:\n    p: {{ {bad} }}\n", "dbdatabuild.yml", d));
            Assert.NotEmpty(d);
        }
    }

    [Fact]
    public void A_load_declares_its_max_runtime_and_a_bad_one_is_a_diagnostic()
    {
        var good = Load(ValidModel.Replace("connections: [sqlserver, fabric]\n", "") + "\nloads:\n  default: { default: true, strategy: full_replace, max_runtime: 2m }\n");
        Assert.Empty(good.Diags);
        Assert.Equal(120, good.Def!.Loads.Single().MaxRuntime!.Seconds);
        var bad = Load(ValidModel.Replace("connections: [sqlserver, fabric]\n", "") + "\nloads:\n  default: { default: true, strategy: full_replace, max_runtime: soon }\n");
        Assert.NotEmpty(bad.Diags);
    }

    private sealed class Probe : IWriteExecutor, IStatementLog
    {
        public List<string> Events { get; } = [];
        public int Refusals { get; set; }
        public Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => Task.FromResult(0L);
        public void Append(StatementLogEntry entry) { }
        public Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct)
        {
            Events.Add($"try:{resource}:{(shared ? "shared" : "exclusive")}");
            return Task.FromResult(Refusals-- <= 0);
        }
        public Task UnlockAsync(string resource, bool shared, CancellationToken ct) { Events.Add($"unlock:{resource}:{(shared ? "shared" : "exclusive")}"); return Task.CompletedTask; }
        public Task RecoverAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_lock_can_be_shared_and_waited_for_up_to_a_limit()
    {
        var p = new Probe();
        var gate = MutationGate.ForTesting(p, "refresh", StatementKind.Tracking, p);
        Assert.True(await gate.AcquireApplicationLockAsync("c", shared: true, TimeSpan.Zero));
        await gate.ReleaseApplicationLockAsync("c", shared: true);
        Assert.Equal(["try:c:shared", "unlock:c:shared"], p.Events);

        var busy = new Probe { Refusals = 100 };
        var waiting = MutationGate.ForTesting(busy, "refresh", StatementKind.Tracking, busy);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await waiting.AcquireApplicationLockAsync("c", shared: false, TimeSpan.FromMilliseconds(500)));
        Assert.InRange(clock.ElapsedMilliseconds, 450, 30000);
        Assert.True(busy.Events.Count >= 2);                                                                 // it tried again while it waited

        var later = new Probe { Refusals = 2 };
        Assert.True(await MutationGate.ForTesting(later, "refresh", StatementKind.Tracking, later).AcquireApplicationLockAsync("c", shared: false, Timeout.InfiniteTimeSpan));
        Assert.Equal(3, later.Events.Count);

        var once = new Probe { Refusals = 1 };
        Assert.False(await MutationGate.ForTesting(once, "refresh", StatementKind.Tracking, once).TryAcquireApplicationLockAsync("c"));     // no wait: one try
        Assert.Single(once.Events);
    }
}
