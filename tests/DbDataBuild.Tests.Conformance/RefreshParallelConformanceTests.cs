using System.Text.Json.Nodes;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Refresh plans, step 5 (docs/research/refresh-plans.md, section 16) on the real engines: loads that do not read each other run together, a plan run with `--parallel 3` ends with the same data and the same states as
/// one at a time, a failure under parallelism follows the same rules, and `max_parallel` on the destination holds the loads back. Overlap is read from the tracking tables (the start and end of the hook that pauses each load).
/// </summary>
public partial class ApplyConformanceTests
{
    /// <summary>Three loads that pause 3 seconds before they load (p1 to p3), two plain ones (z1, z2), and a chain that reads p1 (d1, then d2): the plan `par`, which asks for 3 at once.</summary>
    private static async Task<Run> ParallelSetUp(string name)
    {
        var run = await SetUp(name);
        foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
        Hooks(run, name);
        run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "refresh:\n  plans:\n    par: { parallel: 3 }\n");
        const string refresh = "refresh: { par: default }\n";
        foreach (var m in new[] { "p1", "p2", "p3" })
            Model(run, $"marts.{m}", OrdersColumns, "SELECT order_id, amount FROM staging.orders\n", "hooks:\n  - {name: pause, event: pre_load, script: hooks/pause3.sql, effect: data}\n" + refresh);
        foreach (var m in new[] { "z1", "z2" }) Model(run, $"marts.{m}", CountColumns, "SELECT COUNT(*) AS n FROM staging.orders\n", refresh);
        Model(run, "marts.d1", CountColumns, "SELECT COUNT(*) AS n FROM marts.p1\n", refresh);
        Model(run, "marts.d2", CountColumns, "SELECT COUNT(*) AS n FROM marts.d1\n", refresh);
        Ok(run.Cli("connection", "init", "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
        Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "deploy");
        return run;
    }

    private static readonly string[] ParallelTables = ["p1", "p2", "p3", "z1", "z2", "d1", "d2"];

    private static async Task<string> SnapshotAsync(Run run)
    {
        var parts = new List<string>();
        foreach (var t in ParallelTables)           // one connection, one query at a time
            parts.Add($"{t}={string.Join(",", await run.Engine.RowsAsync($"SELECT CAST({(t.StartsWith('p') ? "order_id" : "n")} AS VARCHAR(20)) FROM marts.{t} ORDER BY 1"))}");
        return string.Join(";", parts);
    }

    /// <summary>How many pairs of the three pausing hooks of one run overlapped in time.</summary>
    private static async Task<int> OverlappingPausesAsync(Run run, string planId)
    {
        var rl = $"{run.Q("dbdatabuild")}.{run.Q("run_log")}";
        var rows = await run.Engine.RowsAsync($"SELECT COUNT(*) FROM {rl} a JOIN {rl} b ON a.plan_id = b.plan_id AND a.model < b.model WHERE a.plan_id = '{planId}' AND a.operation = 'pre_load' AND b.operation = 'pre_load' AND a.model IN ('marts.p1', 'marts.p2', 'marts.p3') AND b.model IN ('marts.p1', 'marts.p2', 'marts.p3') AND a.started_utc < b.ended_utc AND b.started_utc < a.ended_utc");
        return int.Parse(rows.Single());
    }

    private static string Shape(JsonNode data) =>
        string.Join("|", data["results"]!.AsArray().Select(r => $"{(string)r!["model"]!}.{(string)r["member"]!}={(string)r["status"]!}")) + "/" + (string)data["outcome"]!;

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_plan_run_with_parallel_3_ends_with_the_same_data_and_states_as_one_at_a_time_and_the_independent_loads_overlap(string name)
    {
        var run = await ParallelSetUp(name);
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");

            // the flag beats the plan's `parallel: 3`: one at a time, in the order of the plan
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var one = run.Cli("connection", "refresh", name, "par", "--parallel", "1", "--format", "json");
            var oneTime = clock.Elapsed;
            Ok(one, "parallel 1");
            var oneDoc = JsonNode.Parse(one.Out)!["data"]!;
            Assert.Equal(1, (int)oneDoc["parallel"]!);
            Assert.Equal(1, (int)oneDoc["parallel_effective"]!);
            Assert.Equal("completed", (string)oneDoc["outcome"]!);
            Assert.True(oneTime >= TimeSpan.FromSeconds(9), $"three pauses of 3 s one after another took {oneTime}");
            var dataOne = await SnapshotAsync(run);
            Assert.Equal("p1=1,2,3,4;p2=1,2,3,4;p3=1,2,3,4;z1=4;z2=4;d1=4;d2=1", dataOne);
            Assert.Equal(0, await OverlappingPausesAsync(run, (string)oneDoc["plan_id"]!));

            // the plan's own setting, no flag: three at once, and the same data and the same states, in the same order
            clock.Restart();
            var three = run.Cli("connection", "refresh", name, "par", "--format", "json");
            var threeTime = clock.Elapsed;
            Ok(three, "parallel from the plan");
            var threeDoc = JsonNode.Parse(three.Out)!["data"]!;
            Assert.Equal(3, (int)threeDoc["parallel"]!);
            Assert.Equal(3, (int)threeDoc["parallel_effective"]!);
            Assert.Equal(Shape(oneDoc), Shape(threeDoc));
            Assert.Equal(dataOne, await SnapshotAsync(run));
            Assert.True(threeTime < oneTime - TimeSpan.FromSeconds(3), $"parallel 3 took {threeTime}, one at a time {oneTime}");
            Assert.Equal(3, await OverlappingPausesAsync(run, (string)threeDoc["plan_id"]!));                         // all three pairs
            Assert.Contains("a dry run goes one load at a time", run.Cli("connection", "refresh", name, "par", "--dry-run").Out);

            // what reads a table starts after it: d1 after p1, d2 after d1, in the record
            var rl = T("run_log");
            Assert.Equal(0, await CountAsync(run, $"{rl} d JOIN {rl} p ON d.plan_id = p.plan_id", $"d.model = 'marts.d1' AND p.model = 'marts.p1' AND d.operation = 'default' AND p.operation = 'default' AND d.started_utc < p.ended_utc"));
            Assert.Equal(0, await CountAsync(run, $"{rl} d JOIN {rl} p ON d.plan_id = p.plan_id", $"d.model = 'marts.d2' AND p.model = 'marts.d1' AND d.operation = 'default' AND p.operation = 'default' AND d.started_utc < p.ended_utc"));

            // every load of every run has its row, the events completed, and the sessions that ran them are gone
            Assert.Equal(2, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'par' AND status = 'completed'"));
            Assert.Equal(14, await CountAsync(run, rl, "operation = 'default' AND status = 'ok' AND plan_id LIKE 'ref-%'"));

            // --parallel 2 is also the same, and a flag above 64 or below 1 is a usage error
            var two = run.Cli("connection", "refresh", name, "par", "--parallel", "2", "--format", "json");
            Ok(two, "parallel 2");
            Assert.Equal(Shape(oneDoc), Shape(JsonNode.Parse(two.Out)!["data"]!));
            Assert.Equal(2, run.Cli("connection", "refresh", name, "par", "--parallel", "0").Exit);
            Assert.Equal(2, run.Cli("connection", "refresh", name, "par", "--parallel", "65").Exit);

            // the scheduler reads the graph from loads.yml, not from the project: with every model file gone after compile the run still goes 3 at a time, with the same result
            var modelsDir = Path.Combine(run.Dir, "models"); var parked = modelsDir + ".parked";
            Directory.Move(modelsDir, parked);
            try
            {
                var noProject = run.Cli("connection", "refresh", name, "par", "--parallel", "3", "--format", "json");
                Ok(noProject, "parallel 3 without the project");
                var noProjectDoc = JsonNode.Parse(noProject.Out)!["data"]!;
                Assert.Equal(3, (int)noProjectDoc["parallel_effective"]!);
                Assert.Equal(Shape(oneDoc), Shape(noProjectDoc));
                // the selectors of the grammar (a pattern, a kind, an exclusion, `+model`) read the facts in loads.yml, so they work with the models folder gone too, at the same effective parallel
                foreach (var selector in new[] { "marts.p*", "kind:full", "+marts.d1", "exclude:marts.d*" })
                {
                    var picked = run.Cli("connection", "refresh", name, "par", selector, "--parallel", "3", "--format", "json");
                    Ok(picked, $"parallel 3 without the project, {selector}");
                    var pickedDoc = JsonNode.Parse(picked.Out)!["data"]!;
                    Assert.Equal("completed", (string)pickedDoc["outcome"]!);
                    Assert.Equal(3, (int)pickedDoc["parallel_effective"]!);
                    Assert.NotEmpty(pickedDoc["results"]!.AsArray());
                }
            }
            finally { Directory.Move(parked, modelsDir); }
            // a loads file compiled before the graph was carried: one load at a time, and the output says so
            var loadsPath = Path.Combine(run.Dir, "rendered", name, "loads.yml");
            var withGraph = File.ReadAllText(loadsPath);
            var loadsFile = DbDataBuild.Planning.LoadsDocument.Parse(withGraph, loadsPath, []);
            Assert.NotNull(loadsFile?.Dependencies);
            File.WriteAllText(loadsPath, DbDataBuild.Planning.LoadsDocument.Serialize(loadsFile! with { Dependencies = null }));
            try
            {
                var oldFile = run.Cli("connection", "refresh", name, "par", "--parallel", "3", "--format", "json");
                Ok(oldFile, "parallel 3 with a loads file that has no graph");
                var oldDoc = JsonNode.Parse(oldFile.Out)!["data"]!;
                Assert.Equal(3, (int)oldDoc["parallel"]!);
                Assert.Equal(1, (int)oldDoc["parallel_effective"]!);
                Assert.Contains("the loads file has no dependency graph", oldDoc["warnings"]!.ToJsonString());
                Assert.Equal(Shape(oneDoc), Shape(oldDoc));
            }
            finally { File.WriteAllText(loadsPath, withGraph); }

            // max_parallel on the connection caps the destination: asked for 3, the loads go one at a time
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + $"connections:\n  {name}: {{ max_parallel: 1 }}\n");
            clock.Restart();
            var capped = run.Cli("connection", "refresh", name, "par");
            var cappedTime = clock.Elapsed;
            Ok(capped, "capped");
            Assert.Contains($"`max_parallel: 1` on `{name}`", capped.Out);
            var cappedPlanId = System.Text.RegularExpressions.Regex.Match(capped.Out, @"Refresh (ref-\S+) \(plan par\)").Groups[1].Value;
            Assert.NotEmpty(cappedPlanId);
            Assert.Equal(0, await OverlappingPausesAsync(run, cappedPlanId));
            Assert.True(cappedTime >= TimeSpan.FromSeconds(9), $"max_parallel 1 let them overlap: {cappedTime}");
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_failure_under_parallel_follows_the_same_rules_continue_skips_only_dependents_and_stop_lets_the_running_finish(string name)
    {
        var run = await FailureSetUp(name, "    p: { on_failure: continue }\n");
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            await engine.ExecAsync("INSERT INTO staging.flag VALUES (1)");

            // continue: a fails, b (reads a) is skipped, z (independent) runs beside a
            var failed = run.Cli("connection", "refresh", name, "p", "--parallel", "3", "--format", "json");
            Assert.Equal(1, failed.Exit);
            var doc = JsonNode.Parse(failed.Out)!["data"]!;
            Assert.Equal(3, (int)doc["parallel"]!);
            Assert.Equal("completed_with_failures", (string)doc["outcome"]!);
            Assert.Equal(["marts.a_orders", "marts.b_totals", "marts.z_other"], doc["results"]!.AsArray().Select(r => (string)r!["model"]!).ToList());          // the order of the plan
            var results = doc["results"]!.AsArray().ToDictionary(r => (string)r!["model"]!, r => r!);
            Assert.Equal("failed", (string)results["marts.a_orders"]["status"]!);
            Assert.Equal("skipped: upstream failed", (string)results["marts.b_totals"]["status"]!);
            Assert.Equal("ok", (string)results["marts.z_other"]["status"]!);
            Assert.Equal(["3"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.b_totals"));          // b was not touched
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.z_other"));
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'p' AND status = 'completed_with_failures'"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.b_totals' AND status = 'skipped: upstream failed'"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.z_other' AND status = 'ok' AND plan_id LIKE 'ref-%'"));

            // only-failed, in parallel as well: with the cause gone, a and b run, z does not
            await engine.ExecAsync("DELETE FROM staging.flag");
            var again = run.Cli("connection", "refresh", name, "p", "--only-failed", "--parallel", "3");
            Ok(again, "only failed in parallel");
            Assert.Contains("2 of its 3 load(s)", again.Out);
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.b_totals"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("model")} = 'marts.z_other' AND status = 'ok' AND plan_id LIKE 'ref-%'"));

            // stop: a fails at once; z was already running beside it and finishes, b never starts
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")).Replace("on_failure: continue", "on_failure: stop"));
            Ok(run.Cli("project", "compile"), "compile with stop");
            await engine.ExecAsync("INSERT INTO staging.flag VALUES (1)");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (5, 50.00)");
            var stopped = run.Cli("connection", "refresh", name, "p", "--parallel", "3", "--format", "json");
            Assert.Equal(1, stopped.Exit);
            var stopDoc = JsonNode.Parse(stopped.Out)!["data"]!;
            Assert.Equal("stopped", (string)stopDoc["outcome"]!);
            var stopResults = stopDoc["results"]!.AsArray().ToDictionary(r => (string)r!["model"]!, r => (string)r!["status"]!);
            Assert.Equal("failed", stopResults["marts.a_orders"]);
            Assert.Equal("not run", stopResults["marts.b_totals"]);
            Assert.Equal("ok", stopResults["marts.z_other"]);
            Assert.Equal(["5"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.z_other"));          // the one beside it finished
            Assert.Equal(["4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.b_totals"));
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'p' AND status = 'stopped'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Two_refreshes_with_parallel_at_once_lock_per_load_on_their_own_sessions(string name)
    {
        var run = await ParallelSetUp(name);
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            var first = Task.Run(() => run.Cli("connection", "refresh", name, "par", "--parallel", "3"));
            await Task.Delay(1500);                                                                                   // the first is inside the pauses of p1 to p3, each holding its lock
            var second = run.Cli("connection", "refresh", name, "par", "--parallel", "3");
            Ok(second, "a second refresh");
            Ok(await first, "the first refresh");
            // the pausing loads were busy for the second run (a busy skip is not a failure); the plain ones ran
            Assert.Equal(3, await CountAsync(run, T("run_log"), $"{run.Q("status")} = 'skipped: busy' AND {run.Q("model")} IN ('marts.p1', 'marts.p2', 'marts.p3')"));
            Assert.Contains("skipped: marts.p1.default (busy)", second.Out);
            Assert.Equal(2, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'par' AND status = 'completed'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
