using System.Text.RegularExpressions;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Refresh plans, step 2 (docs/research/refresh-plans.md, section 6) on the real engines: the failure rules and their dependents, `--only-failed`, a lock per load with `on_busy` (two refreshes at once, and a deploy kept out), `max_runtime`,
/// and the `expected_every` warnings. Each asserts what the tracking tables and the tables of the target hold afterwards, not just the exit code.
/// </summary>
public partial class ApplyConformanceTests
{
    private static string Hooks(Run run, string name)
    {
        // a hook that fails while the table staging.flag has a row (division by zero on both engines), and one that pauses
        run.Write("hooks/gate.sql", "SELECT 1 / (SELECT 1 - COUNT(*) FROM staging.flag);\n");
        run.Write("hooks/pause.sql", name == "postgres" ? "SELECT pg_sleep(6);\n" : "WAITFOR DELAY '00:00:06';\n");
        run.Write("hooks/pause3.sql", name == "postgres" ? "SELECT pg_sleep(3);\n" : "WAITFOR DELAY '00:00:03';\n");
        return name;
    }

    private static void Model(Run run, string table, string columns, string sql, string extra = "")
    {
        var schema = table.Split('.')[0];
        run.Write($"models/{schema}/{table.Split('.')[1]}.yml", $"name: {table}\nkind: full\ncolumns:\n{columns}{extra}");
        run.Write($"models/{schema}/{table.Split('.')[1]}.sql", sql);
    }

    private const string OrdersColumns = "  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string CountColumns = "  - {name: n, type: BIGINT}\n";

    /// <summary>a_orders (a hook can make it fail), b_totals (reads it), z_other (reads only staging); all in the plan `p`.</summary>
    private static async Task<Run> FailureSetUp(string name, string plans, string aRefresh = "refresh: { p: default }\n")
    {
        var run = await SetUp(name);
        foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
        Hooks(run, name);
        await run.Engine.ExecAsync("CREATE TABLE staging.flag (x INT)");
        run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "refresh:\n  plans:\n" + plans);
        Model(run, "marts.a_orders", OrdersColumns, "SELECT order_id, amount FROM staging.orders\n", "hooks:\n  - {name: gate, event: pre_load, script: hooks/gate.sql, effect: data}\n" + aRefresh);
        Model(run, "marts.b_totals", CountColumns, "SELECT COUNT(*) AS n FROM marts.a_orders\n", "refresh: { p: default }\n");
        Model(run, "marts.z_other", CountColumns, "SELECT COUNT(*) AS n FROM staging.orders\n", "refresh: { p: default }\n");
        Ok(run.Cli("connection", "init", "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
        Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");
        return run;
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_failed_load_skips_what_reads_it_under_continue_stops_everything_under_stop_and_is_a_warning_under_ignore_and_only_failed_repeats_the_rest(string name)
    {
        var run = await FailureSetUp(name, "    p: { on_failure: continue }\n");
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            var statusOf = async (string model, string where = "") => string.Join("|", await engine.RowsAsync($"SELECT {run.Q("status")} FROM {T("run_log")} WHERE {run.Q("model")} = '{model}' AND {run.Q("plan_id")} LIKE 'ref-%' {where}"));
            Assert.Equal(["3", "3", "3"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.b_totals UNION ALL SELECT CAST(n AS VARCHAR(10)) FROM marts.z_other UNION ALL SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM marts.a_orders"));

            // continue: a fails, what reads it (b) is skipped, the independent z runs; the run ended with failures and says so
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            await engine.ExecAsync("INSERT INTO staging.flag VALUES (1)");
            var failed = run.Cli("connection", "refresh", name, "p", "--format", "json");
            Assert.Equal(1, failed.Exit);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(failed.Out)!["data"]!;
            Assert.Equal("completed_with_failures", (string)doc["outcome"]!);
            var results = doc["results"]!.AsArray().ToDictionary(r => (string)r!["model"]!, r => r!);
            Assert.Equal(("failed", "failed", (string?)null), ((string)results["marts.a_orders"]["state"]!, (string)results["marts.a_orders"]["status"]!, (string?)null));
            Assert.Equal(("skipped", "upstream failed"), ((string)results["marts.b_totals"]["state"]!, (string)results["marts.b_totals"]["reason"]!));
            Assert.Equal("done", (string)results["marts.z_other"]["state"]!);
            var text = run.Cli("connection", "refresh", name, "p");                                                    // the same again, to read the words
            Assert.Equal(1, text.Exit);
            Assert.Contains("completed with failures", text.Out);
            Assert.Contains("failed: marts.a_orders.default", text.Out);
            Assert.Contains("skipped: marts.b_totals.default (upstream failed)", text.Out);
            Assert.Contains("--only-failed", text.Out);
            Assert.Equal(["3"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.b_totals"));          // b was not touched
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.z_other"));           // z was
            Assert.Equal(2, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'p' AND status = 'completed_with_failures'"));
            Assert.Equal("failed|failed|failed|failed", await statusOf("marts.a_orders", $"AND {run.Q("operation")} IN ('default', 'pre_load') AND {run.Q("started_utc")} IS NOT NULL ORDER BY {run.Q("started_utc")}"));
            Assert.Equal(2, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.b_totals' AND status = 'skipped: upstream failed' AND {run.Q("membership")} = 'default'"));

            // only-failed: with the cause gone, only what failed or was skipped runs again (z does not)
            await engine.ExecAsync("DELETE FROM staging.flag");
            var zBefore = await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.z_other' AND status = 'ok'");
            var again = run.Cli("connection", "refresh", name, "p", "--only-failed");
            Ok(again, "only failed");
            Assert.Contains("--only-failed", again.Out);
            Assert.Contains("2 of its 3 load(s)", again.Out);
            Assert.Equal(zBefore, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.z_other' AND status = 'ok'"));
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.b_totals"));
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'p' AND status = 'completed' AND {run.Q("selection")} = '--only-failed'"));
            var nothing = run.Cli("connection", "refresh", name, "p", "--only-failed");
            Ok(nothing, "only failed with nothing left");
            Assert.Contains("Nothing to do", nothing.Out);
            Assert.Equal(2, run.Cli("connection", "refresh", name, "--only-failed").Exit == 0 ? 0 : 2);                // `default` has no run of its own (it is not a plan of this project): a usage error, not a run

            // stop: a fails, nothing after it starts
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")).Replace("on_failure: continue", "on_failure: stop"));
            Ok(run.Cli("project", "compile"), "compile with stop");
            await engine.ExecAsync("INSERT INTO staging.flag VALUES (1)");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (5, 50.00)");
            var stopped = run.Cli("connection", "refresh", name, "p");
            Assert.Equal(1, stopped.Exit);
            Assert.Contains("stopped", stopped.Out);
            Assert.Contains("not run: marts.b_totals.default", stopped.Out);
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'p' AND status = 'stopped'"));
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.z_other"));            // z did not run either
            Assert.True(await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.z_other' AND status = 'not run'") >= 1);

            // ignore (a membership overrides the plan): a fails and is a warning, what reads it runs on what it had, the run completed
            Model(run, "marts.a_orders", OrdersColumns, "SELECT order_id, amount FROM staging.orders\n", "hooks:\n  - {name: gate, event: pre_load, script: hooks/gate.sql, effect: data}\nrefresh: { p: { load: default, on_failure: ignore } }\n");
            Ok(run.Cli("project", "compile"), "compile with ignore");
            var ignored = run.Cli("connection", "refresh", name, "p");
            Ok(ignored, "ignore");
            Assert.Contains("warning: marts.a_orders.default", ignored.Out);
            Assert.Equal(["5"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.z_other"));
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.b_totals"));           // b ran, on the stale a (4 rows)
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'p' AND status = 'completed' AND {run.Q("plan_id")} IN (SELECT {run.Q("plan_id")} FROM {T("run_log")} WHERE status = 'failed')"));

            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")).Replace("on_failure: stop", "on_failure: continue"));
            // a failed check is a failure of the loads of that object (kind check): the others run
            await engine.ExecAsync("DELETE FROM staging.flag");
            Model(run, "marts.z_other", CountColumns + "  - {name: m, type: BIGINT}\n", "SELECT COUNT(*) AS n, CAST(1 AS BIGINT) AS m FROM staging.orders\n", "refresh: { p: default }\n");
            Ok(run.Cli("project", "compile"), "compile with a changed z");
            var checkFailed = run.Cli("connection", "refresh", name, "p");
            Assert.Equal(1, checkFailed.Exit);
            Assert.True(checkFailed.Out.Contains("completed with failures"), checkFailed.Out + checkFailed.Err);
            Assert.True((checkFailed.Err + checkFailed.Out).Contains("DDB-447"), checkFailed.Out + checkFailed.Err);
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.z_other' AND status = 'failed: check'"));
            Assert.Equal(["5"], await engine.RowsAsync("SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM marts.a_orders"));       // a ran
            // and the monitor says where it stands
            var monitor = run.Cli("connection", "monitor", name, "p");
            Assert.Contains("failed: check", monitor.Out + run.Cli("connection", "monitor", name).Out);
            Assert.Contains("ended completed with failures", run.Cli("connection", "monitor", name).Out);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Two_refreshes_at_once_lock_per_load_and_on_busy_decides_what_the_second_does_and_a_deploy_is_kept_out(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            Hooks(run, name);
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) +
                "refresh:\n  plans:\n    q: { on_busy: skip }\n    qf: { on_busy: fail }\n    qw: { on_busy: wait, max_wait: 1s }\n    qwl: { on_busy: wait }\n");
            const string slowRefresh = "refresh: { q: default, qf: default, qw: default, qwl: default }\n";
            Model(run, "marts.s_slow", OrdersColumns, "SELECT order_id, amount FROM staging.orders\n", "hooks:\n  - {name: pause, event: pre_load, script: hooks/pause.sql, effect: data}\n" + slowRefresh);
            Model(run, "marts.d_dep", CountColumns, "SELECT COUNT(*) AS n FROM marts.s_slow\n", slowRefresh);
            Model(run, "marts.x_other", CountColumns, "SELECT COUNT(*) AS n FROM staging.orders\n", slowRefresh);
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var first = run.Cli("connection", "deploy", "--write-plan"); Ok(first, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(first.Out)), "deploy");
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";

            // a second deploy, planned now and applied while a refresh holds a load
            Model(run, "marts.extra", CountColumns, "SELECT COUNT(*) AS n FROM staging.orders\n");
            Ok(run.Cli("project", "compile"), "compile with extra");
            var second = run.Cli("connection", "deploy", "--write-plan"); Ok(second, "plan 2");
            var secondFile = run.PlanFile(second.Out);

            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            var slow = Task.Run(() => run.Cli("connection", "refresh", name, "q"));
            await Task.Delay(2500);                                                                                // the first is inside the hook of s_slow, holding its lock
            var busy = run.Cli("connection", "refresh", name, "q");
            Ok(busy, "a refresh that finds a load busy");                                                              // a busy skip is not a failure
            Assert.Contains("skipped: marts.s_slow.default (busy)", busy.Out);
            Assert.Contains("d_dep.default ran: upstream skipped: busy", busy.Out);
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.s_slow' AND status = 'skipped: busy'"));
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.x_other"));          // the independent load ran beside the slow one
            Assert.Equal(["3"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.d_dep"));            // d ran on the last committed data of s_slow (3 rows: the slow load had not finished)
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'q' AND status = 'completed' AND {run.Q("plan_id")} IN (SELECT {run.Q("plan_id")} FROM {T("run_log")} WHERE status = 'skipped: busy')"));

            // the deploy needs the whole connection, which a running load keeps from it: busy (exit 4), nothing created
            var blocked = run.Cli("connection", "deploy", "--apply-plan", secondFile);
            Assert.Equal(4, blocked.Exit);
            Assert.Contains("DDB-439", blocked.Err);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'extra'"));

            // on_busy fail: the load fails, and what reads it is skipped (a failure, not a busy skip)
            var failedBusy = run.Cli("connection", "refresh", name, "qf");
            Assert.Equal(1, failedBusy.Exit);
            Assert.Contains("failed: marts.s_slow.default (busy)", failedBusy.Out);
            Assert.Contains("skipped: marts.d_dep.default (upstream failed)", failedBusy.Out);
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.s_slow' AND status = 'failed: busy'"));

            // wait with a max_wait: it waits that long, then the load fails
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var waited = run.Cli("connection", "refresh", name, "qw");
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1), $"waited {clock.Elapsed}");
            Assert.Equal(1, waited.Exit);
            Assert.Contains("failed: marts.s_slow.default (busy)", waited.Out);

            // wait without a limit: it is let in when the first run lets go, and runs its load
            var patient = Task.Run(() => run.Cli("connection", "refresh", name, "qwl"));
            var done = await slow;
            Ok(done, "the slow refresh");
            Assert.Contains("completed", done.Out);
            var patientDone = await patient;
            Ok(patientDone, "the patient refresh");
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.d_dep"));
            Assert.Equal(2, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.s_slow' AND status = 'ok' AND {run.Q("operation")} = 'default' AND {run.Q("plan_id")} LIKE 'ref-%'"));

            // and with nothing running the deploy gets in
            Ok(run.Cli("connection", "deploy", "--apply-plan", secondFile), "the deploy once nothing runs");
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'extra'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_load_past_its_max_runtime_is_cancelled_rolled_back_and_failed_and_lets_go_of_its_lock(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            await engine.ExecAsync("CREATE TABLE staging.big (i BIGINT NOT NULL)");
            await engine.ExecAsync("INSERT INTO staging.big VALUES (1), (2), (3)");
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "refresh:\n  plans:\n    m: { max_runtime: 1h }\n");
            // the load's own max_runtime wins over the plan's
            run.Write("models/staging/big.yml", "name: staging.big\nkind: mapped\ngrain: [i]\ncolumns:\n  - {name: i, type: BIGINT, nullable: false}\n");
            run.Write("models/marts/heavy.yml", "name: marts.heavy\nkind: full\ncolumns:\n  - {name: n, type: BIGINT}\nloads:\n  default: { default: true, strategy: full_replace, max_runtime: 2s }\nrefresh: { m: default }\n");
            run.Write("models/marts/heavy.sql", "SELECT COUNT(*) AS n FROM staging.big a CROSS JOIN staging.big b CROSS JOIN staging.big c CROSS JOIN staging.big d\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");
            Assert.Equal(["81"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(20)) FROM marts.heavy"));
            Assert.Contains("max_runtime: \"2s\"", File.ReadAllText(Path.Combine(run.Dir, "rendered", name, "loads.yml")));

            await engine.ExecAsync(name == "postgres" ? "INSERT INTO staging.big SELECT generate_series(4, 600)" : "INSERT INTO staging.big SELECT TOP 597 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) + 3 FROM sys.all_objects a CROSS JOIN sys.all_objects b");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var cut = run.Cli("connection", "refresh", name, "m");
            clock.Stop();
            Assert.Equal(1, cut.Exit);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(40), $"took {clock.Elapsed}");
            Assert.Contains("failed: marts.heavy.default (max runtime)", cut.Out);
            Assert.Equal(["81"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(20)) FROM marts.heavy"));           // the transaction rolled back: the table is as it was
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.heavy' AND status = 'failed: max runtime' AND {run.Q("duration_ms")} >= 1500"));
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'm' AND status = 'failed'"));

            // the lock was let go and the connection is usable: with the extra rows gone it loads
            await engine.ExecAsync("DELETE FROM staging.big WHERE i > 3");
            var ok = run.Cli("connection", "refresh", name, "m");
            Ok(ok, "after the cancelled load");
            Assert.Equal(["81"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(20)) FROM marts.heavy"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.heavy' AND status = 'ok' AND {run.Q("plan_id")} LIKE 'ref-%'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Status_and_monitor_warn_when_a_plan_is_overdue_or_slow_and_name_the_slowest_load(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            Hooks(run, name);
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "refresh:\n  plans:\n    e: { expected_every: 2s }\n    relaxed: { expected_every: 1d }\n");
            Model(run, "marts.s_slow", OrdersColumns, "SELECT order_id, amount FROM staging.orders\n", "hooks:\n  - {name: pause, event: pre_load, script: hooks/pause3.sql, effect: data}\nrefresh: { e: default, relaxed: default }\n");
            Model(run, "marts.x_other", CountColumns, "SELECT COUNT(*) AS n FROM staging.orders\n", "refresh: { e: default, relaxed: default }\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");

            // never run: overdue
            var never = run.Cli("connection", "monitor", name, "e");
            Assert.Contains("OVERDUE: plan e is meant to run every 2s and has never run", never.Out);
            Ok(run.Cli("connection", "refresh", name, "e"), "refresh e");
            Ok(run.Cli("connection", "refresh", name, "relaxed"), "refresh relaxed");

            // slow: the run took longer than expected_every, and the slowest load is named (its hook counts as part of it)
            var monitor = run.Cli("connection", "monitor", name, "e");
            Assert.Contains("SLOW: the last run of plan e took", monitor.Out);
            Assert.Matches(new Regex(@"the slowest load was marts\.s_slow\.default"), monitor.Out);
            var status = run.Cli("connection", "status", name, "--format", "json");
            var plans = System.Text.Json.Nodes.JsonNode.Parse(status.Out)!["data"]!["plans"]!.AsArray().ToDictionary(p => (string)p!["plan"]!, p => p!["health"]!);
            Assert.True((bool)plans["e"]["slow"]!);
            Assert.Equal("marts.s_slow.default", (string)plans["e"]["slowest_load"]!);
            Assert.False((bool)plans["relaxed"]["slow"]! || (bool)plans["relaxed"]["overdue"]!);                     // within a day, and it did not take a day
            Assert.DoesNotContain("relaxed is meant", monitor.Out);

            // overdue: it is meant to run every 2s and the last run began longer ago than that
            await Task.Delay(2500);
            var late = run.Cli("connection", "status", name);
            Assert.Contains("OVERDUE: plan e is meant to run every 2s", late.Out);
            Assert.DoesNotContain("OVERDUE: plan relaxed", late.Out);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
