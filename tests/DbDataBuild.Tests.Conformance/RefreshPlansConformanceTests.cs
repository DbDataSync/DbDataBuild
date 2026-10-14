using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Refresh plans on the real engines (docs/research/refresh-plans.md, step 1): a run by plan, a subset by selector, a load named directly with an input given at the command, and the same load twice in one plan; each recorded in the tracking tables.</summary>
public partial class ApplyConformanceTests
{
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_plan_runs_its_members_a_selection_runs_part_of_it_and_a_load_named_directly_takes_inputs_from_the_command(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            foreach (var f in new[] { "v_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "refresh:\n  plans:\n    hourly: { title: Orders, expected_every: 1h }\n    daily: { on_failure: continue, expected_every: 1d }\n");
            // fct_orders is in `hourly`, and twice in `daily` (the second time under another name); it also has a range load that is in no plan
            run.Write("models/marts/fct_orders.yml", FctYaml + "tags: [orders]\nloads:\n  default:\n    default: true\n    strategy: delete_insert_by_key\n  window:\n    strategy: delete_insert_by_range\n    column: order_id\n    params: {start: BIGINT, end: BIGINT}\n" +
                "refresh:\n  hourly: default\n  daily: [default, { load: default, as: again }]\n");
            run.Write("models/marts/totals.yml", "name: marts.totals\nkind: full\ntags: [report]\nrefresh: { daily: default }\ncolumns:\n  - {name: n, type: BIGINT}\n");
            run.Write("models/marts/totals.sql", "SELECT COUNT(*) AS n FROM marts.fct_orders\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            Assert.Equal(3, await CountAsync(run, "marts.fct_orders"));

            // no plan is called `default`: the command lists them and runs nothing
            var listed = run.Cli("connection", "refresh");
            Assert.Equal(2, listed.Exit);
            Assert.Contains("daily", listed.Out);
            Assert.Contains("hourly", listed.Out);

            // a plan: its members run in order, and the event records the plan, its hash and the plan's members
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            var hourly = run.Cli("connection", "refresh", name, "hourly");
            Ok(hourly, "refresh hourly");
            Assert.Contains("plan: hourly", hourly.Out);
            Assert.Equal(4, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'hourly' AND status = 'completed'"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("membership")} = 'default' AND status = 'ok' AND {run.Q("load_plan_hash")} IS NOT NULL AND {run.Q("attempt")} = 1 AND {run.Q("duration_ms")} IS NOT NULL AND rows_affected = 4"));
            Assert.Equal(1, await CountAsync(run, T("refresh_plan_member"), $"{run.Q("selected")} = 'selected' AND {run.Q("membership")} = 'default'"));

            // a selection: part of a plan. The event says which, and the monitor shows complete and partial runs apart
            var partial = run.Cli("connection", "refresh", name, "daily", "tag:report");
            Ok(partial, "refresh daily tag:report");
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} = 'daily' AND {run.Q("selection")} = 'tag:report' AND status = 'completed'"));
            Assert.Equal(3, await CountAsync(run, T("refresh_plan_member"), $"{run.Q("model")} IS NOT NULL AND {run.Q("plan_id")} IN (SELECT {run.Q("plan_id")} FROM {T("migration_log")} WHERE {run.Q("selection")} = 'tag:report')"));
            Assert.Equal(1, await CountAsync(run, T("refresh_plan_member"), $"{run.Q("selected")} = 'selected' AND {run.Q("model")} = 'marts.totals'"));
            var viewPartial = run.Cli("connection", "monitor", name, "daily");
            Ok(viewPartial, "monitor daily");
            Assert.Contains("only ever run in part", viewPartial.Out);
            Assert.Contains("partial 1/3", viewPartial.Out);

            // the whole plan: the same load twice under two names, then the report
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (5, 50.00)");
            var daily = run.Cli("connection", "refresh", name, "daily");
            Ok(daily, "refresh daily");
            Assert.Equal(5, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(["5"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.totals"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("membership")} = 'again' AND status = 'ok'"));
            var full = run.Cli("connection", "monitor", name, "daily", "--format", "json");
            Ok(full, "monitor daily json");
            Assert.Contains("\"last_complete\"", full.Out);

            // a load named directly, in no plan, with its inputs from the command: only the rows of the range are loaded again
            await engine.ExecAsync("DELETE FROM marts.fct_orders");
            var refused = run.Cli("connection", "refresh", name, "marts.fct_orders.window");
            Assert.Equal(2, refused.Exit);
            Assert.Contains("DDB-449", refused.Err);
            var adhoc = run.Cli("connection", "refresh", name, "marts.fct_orders.window", "--input", "start=2", "--input", "marts.fct_orders.window.end=4");
            Ok(adhoc, "a load named directly");
            Assert.Equal(["2", "3"], await engine.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) FROM marts.fct_orders ORDER BY order_id"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), $"{run.Q("membership")} = 'window' AND {run.Q("inputs")} LIKE '%\"source\":\"flag\"%' AND status = 'ok'"));
            Assert.Equal(1, await CountAsync(run, T("migration_log"), $"{run.Q("refresh_plan")} IS NULL AND {run.Q("lane")} = 'refresh' AND status = 'completed' AND {run.Q("selection")} = 'marts.fct_orders.window'"));

            // the views
            var status = run.Cli("connection", "status", name, "--format", "json");
            Ok(status, "status");
            Assert.Contains("\"plans\"", status.Out);
            var text = run.Cli("connection", "status", name, "hourly");
            Assert.Contains("Plan hourly", text.Out);
            Assert.Contains("COMPLETED", text.Out);
            Ok(run.Cli("connection", "monitor", name, "hourly"), "monitor hourly");
            Ok(run.Cli("project", "show", "refresh", "daily", "--format", "json"), "project show refresh");
            Ok(run.Cli("connection", "refresh", name, "hourly", "--format", "json"), "refresh json");
            Ok(run.Cli("connection", "refresh", name, "daily", "--dry-run", "--format", "json"), "dry run json");
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
