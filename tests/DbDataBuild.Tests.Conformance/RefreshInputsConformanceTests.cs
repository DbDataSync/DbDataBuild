using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Inputs and resolvers on the real engines (docs/research/refresh-plans.md, section 8): an expression on a frozen clock, a `target` resolver run on the read login, one resolver that provides two inputs, NULL and a value that does not fit, and an override that stops the resolver from running.</summary>
public partial class ApplyConformanceTests
{
    private const string DaysStaging = "name: staging.days\nkind:\n  type: mapped\ngrain: [day]\ncolumns:\n  - {name: day, type: DATE, nullable: false}\n  - {name: n, type: BIGINT, nullable: false}\n";
    private const string DaysYaml = "name: marts.fct_days\nkind: {type: incremental_by_unique_key, unique_key: [day]}\ngrain: [day]\ncolumns:\n  - {name: day, type: DATE, nullable: false}\n  - {name: n, type: BIGINT, nullable: false}\n";
    private const string DaysSql = "SELECT d.day, d.n FROM staging.days d\n";

    private static string DaysLoads(string extra = "") =>
        "loads:\n  default:\n    default: true\n    strategy: delete_insert_by_key\n" +
        "  window:\n    strategy: delete_insert_by_range\n    column: day\n    inputs:\n      days: {type: INTEGER, default: 3}\n      start: {resolver: {expression: 'current_date - days'}}\n      end: {resolver: {expression: 'current_date + 1'}}\n" +
        "  by_target:\n    strategy: delete_insert_by_range\n    column: day\n    inputs:\n      end: {resolver: {target: 'SELECT max(day) + 1 FROM marts.fct_days'}}\n      start: {resolver: {expression: '\"end\" - 2'}}\n" +
        "  grouped:\n    strategy: delete_insert_by_range\n    column: day\n    inputs:\n      w: {resolver: {expression: 'SELECT current_date - 2 AS start, current_date AS \"end\"'}, provides: [start, end]}\n" +
        "  nothing:\n    strategy: delete_insert_by_range\n    column: day\n    inputs:\n      end: {resolver: {target: \"SELECT max(day) FROM marts.fct_days WHERE day > DATE '2100-01-01'\"}, on_null: skip_load}\n      start: {resolver: {expression: '\"end\" - 2'}}\n" +
        "  nulls:\n    strategy: delete_insert_by_range\n    column: day\n    inputs:\n      end: {resolver: {target: \"SELECT max(day) FROM marts.fct_days WHERE day > DATE '2100-01-01'\"}}\n      start: {resolver: {expression: '\"end\" - 2'}}\n" +
        "  badtype:\n    strategy: delete_insert_by_range\n    column: day\n    inputs:\n      start: {resolver: {expression: 'now()'}}\n      end: {resolver: {expression: 'current_date + 1'}}\n" + extra;

    private static async Task<Run> DaysProject(string name)
    {
        var run = await SetUp(name);
        foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
        File.Delete(Path.Combine(run.Dir, "models/staging/orders.yml"));
        run.Write("models/staging/days.yml", DaysStaging);
        run.Write("models/marts/fct_days.yml", DaysYaml + DaysLoads() + "refresh:\n  daily: window\n  hourly: [grouped, by_target]\n  odd: [nothing, default]\n  broken: [nulls]\n  typed: [badtype]\n");
        run.Write("models/marts/fct_days.sql", DaysSql);
        await run.Engine.ExecAsync($"CREATE TABLE staging.days ({run.Q("day")} DATE NOT NULL, {run.Q("n")} BIGINT NOT NULL)");
        // 2026-02-20 .. 2026-03-02: the days around the frozen clock
        await run.Engine.ExecAsync("INSERT INTO staging.days VALUES " + string.Join(", ", Enumerable.Range(0, 11).Select(i => $"('{new DateTime(2026, 2, 20).AddDays(i):yyyy-MM-dd}', 1)")));
        return run;
    }

    private static Task<List<string>> DaysAsync(Run run) => run.Engine.RowsAsync("SELECT CONCAT(CAST(day AS VARCHAR(10)), '=', CAST(n AS VARCHAR(10))) FROM marts.fct_days ORDER BY day");

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Inputs_are_computed_by_an_expression_on_a_frozen_clock_and_a_target_resolver_and_a_group_and_a_value_given_stops_the_resolver_from_running(string name)
    {
        var run = await DaysProject(name);
        await using var engine = run.Engine;
        var previousClock = DbDataBuild.Cli.RefreshCommand.Now;
        DbDataBuild.Cli.RefreshCommand.Now = () => new DateTimeOffset(2026, 3, 1, 23, 30, 15, TimeSpan.Zero);
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            Assert.Equal(11, await CountAsync(run, "marts.fct_days"));

            // the source changes everywhere; a refresh of `daily` reloads the window the expression computes: 3 days back from the frozen day, to the end of it
            await engine.ExecAsync("UPDATE staging.days SET n = 2");
            var daily = run.Cli("connection", "refresh", name, "daily");
            Ok(daily, "refresh daily");
            var rows = await DaysAsync(run);
            Assert.Equal(["2026-02-28=2", "2026-03-01=2", "2026-03-02=1"], rows.Skip(8));                                         // [03-01 - 3 days, 03-02): 02-26 .. 03-01
            Assert.Equal(["2026-02-26=2", "2026-02-27=2"], rows.Skip(6).Take(2));
            Assert.Equal("1", rows[5].Split('=')[1]);
            var inputs = await engine.RowsAsync($"SELECT {run.Q("inputs")} FROM {T("run_log")} WHERE {run.Q("membership")} = 'window' AND status = 'ok'");
            var json = Assert.Single(inputs);
            Assert.Contains("\"name\":\"start\"", json);
            Assert.Contains("\"source\":\"resolver:expression\"", json);
            Assert.Contains("\"value\":\"2026-02-26\"", json);
            Assert.Contains("\"resolver_hash\":\"", json);
            Assert.Contains("\"name\":\"days\"", json);
            Assert.Contains("\"source\":\"default\"", json);
            Assert.Contains("\"name\":\"run_started\"", json);
            Assert.Contains("\"value\":\"2026-03-01T23:30:15Z\"", json);                                                               // one frozen instant, recorded

            // --input days=1 flows into the expression that names it; the resolver of `days` is not what runs
            await engine.ExecAsync("UPDATE staging.days SET n = 3");
            Ok(run.Cli("connection", "refresh", name, "daily", "--input", "days=1"), "refresh daily --input days=1");
            rows = await DaysAsync(run);
            Assert.Equal(["2026-02-27=2", "2026-02-28=3", "2026-03-01=3"], new[] { rows[7], rows[8], rows[9] });            // one day back from the frozen day: [02-28, 03-02)
            var flagged = await engine.RowsAsync($"SELECT {run.Q("inputs")} FROM {T("run_log")} WHERE {run.Q("membership")} = 'window' AND {run.Q("inputs")} LIKE '%\"name\":\"days\",\"type\":\"INTEGER\",\"source\":\"flag\"%'");
            Assert.Single(flagged);

            // a start given at the command replaces its resolver: that resolver is not run (it would give another day), the end is still computed
            await engine.ExecAsync("UPDATE staging.days SET n = 4");
            Ok(run.Cli("connection", "refresh", name, "marts.fct_days.window", "--input", "start=2026-02-20"), "refresh window --input start");
            rows = await DaysAsync(run);
            Assert.Equal(["2026-02-20=4", "2026-03-01=4", "2026-03-02=1"], new[] { rows[0], rows[9], rows[10] });

            // a target resolver: the newest day in the target plus one is the end, and the expression for start names it; one resolver provides both in `grouped`
            await engine.ExecAsync("UPDATE staging.days SET n = 5");
            var hourly = run.Cli("connection", "refresh", name, "hourly");
            Ok(hourly, "refresh hourly");
            rows = await DaysAsync(run);
            Assert.Equal(["2026-02-28=5", "2026-03-01=5", "2026-03-02=5"], rows.Skip(8));                                             // grouped: [02-27, 03-01) then by_target: [03-01, 03-03)
            var byTarget = Assert.Single(await engine.RowsAsync($"SELECT {run.Q("inputs")} FROM {T("run_log")} WHERE {run.Q("membership")} = 'by_target' AND status = 'ok'"));
            Assert.Contains("\"name\":\"end\",\"type\":\"DATE\",\"source\":\"resolver:target\",\"value\":\"2026-03-03\"", byTarget);
            Assert.Contains("\"name\":\"start\",\"type\":\"DATE\",\"source\":\"resolver:expression\",\"value\":\"2026-03-01\"", byTarget);
            var grouped = Assert.Single(await engine.RowsAsync($"SELECT {run.Q("inputs")} FROM {T("run_log")} WHERE {run.Q("membership")} = 'grouped' AND status = 'ok'"));
            Assert.Contains("\"name\":\"start\",\"type\":\"DATE\",\"source\":\"resolver:expression\",\"value\":\"2026-02-27\"", grouped);

            // NULL: skip_load does not run the load and is not a failure; the default plan member after it still runs
            await engine.ExecAsync("UPDATE staging.days SET n = 6");
            var odd = run.Cli("connection", "refresh", name, "odd");
            Ok(odd, "refresh odd");
            Assert.Contains("skipped: no value", odd.Out);
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("membership")} = 'nothing' AND status = 'skipped: no value'"));
            Assert.All(await DaysAsync(run), r => Assert.EndsWith("=6", r));                                                          // `default` reloaded everything

            // NULL with on_null fail, and a value that does not fit: the load fails, the message names the input and the resolver, and nothing was loaded
            var nulls = run.Cli("connection", "refresh", name, "broken");
            Assert.NotEqual(0, nulls.Exit);
            Assert.Contains("failed: input", nulls.Out + nulls.Err);
            Assert.Contains("DDB-453", nulls.Out + nulls.Err);
            Assert.Contains("`end`", nulls.Out + nulls.Err);
            Assert.Contains("--input marts.fct_days.nulls.end=", nulls.Out + nulls.Err);
            var typed = run.Cli("connection", "refresh", name, "typed");
            Assert.NotEqual(0, typed.Exit);
            Assert.Contains("time of day", typed.Out + typed.Err);
            Assert.Contains("`start`", typed.Out + typed.Err);
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("membership")} = 'nulls' AND status = 'failed: input'"));
            // a broken resolver stands in nobody's way: the value is given at the command
            Ok(run.Cli("connection", "refresh", name, "marts.fct_days.badtype", "--input", "start=2026-03-01"), "refresh badtype --input start");
        }
        finally { DbDataBuild.Cli.RefreshCommand.Now = previousClock; if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
