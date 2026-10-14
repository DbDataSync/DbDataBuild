using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The `fan_in_fan_out` template on four real databases, through the commands a person would type: two PostgreSQL shop systems (store_east, store_west) seeded on their own, a SQL Server warehouse and a PostgreSQL
/// reporting database. The fan-in copy lands both shops in `warehouse.orders` on both engines, the fan-out model `marts.daily_sales` is built on both, and its rows are what the origin tables give when they are
/// grouped here (not by either engine under test). A refresh moves the rows too: the copy is a member of `hourly` and `daily`, before the report that reads it.
/// </summary>
[Trait("Group", "templates")]
public partial class FanInFanOutTemplateConformanceTests
{
    [GeneratedRegex(@"plan:\s+(\S+\.plan\.yml)")]
    private static partial Regex PlanPath();

    [SkippableFact]
    public async Task Two_shop_systems_land_in_one_table_on_both_engines_and_the_report_built_on_both_agrees_with_the_origins()
    {
        var east = EngineEnv.Require("postgres");
        var west = EngineEnv.Require("postgres");
        var warehouse = EngineEnv.Require("sqlserver");
        var reporting = EngineEnv.Require("postgres");
        await east.StartAsync(); await west.StartAsync(); await warehouse.StartAsync(); await reporting.StartAsync();
        await using var _e = east; await using var _w = west; await using var _wh = warehouse; await using var _r = reporting;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-stores-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v switch
        {
            "DBDATABUILD_STORE_EAST_READ" or "DBDATABUILD_STORE_EAST_WRITE" => east.ConnectionString,
            "DBDATABUILD_STORE_WEST_READ" or "DBDATABUILD_STORE_WEST_WRITE" => west.ConnectionString,
            "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" => warehouse.ConnectionString,
            "DBDATABUILD_POSTGRES_READ" or "DBDATABUILD_POSTGRES_WRITE" => reporting.ConnectionString,
            _ => null,
        };
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, .. (args[0] == "project" && args[1] == "create" ? [] : new[] { "--project", dir })], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");
        string PlanOf(string output) => Path.Combine(dir, PlanPath().Match(output).Groups[1].Value);
        void Deploy(string connection)
        {
            var plan = Cli("connection", "deploy", connection, "--write-plan");
            Ok(plan, $"plan {connection}");
            Ok(Cli("connection", "deploy", connection, "--apply-plan", PlanOf(plan.Out)), $"apply {connection}");
        }
        try
        {
            Ok(Cli("project", "create", "fan_in_fan_out", dir), "create");
            // the shops are `readonly`: the tool refuses to seed one (DDB-455). Demo shops are made the way the README says: `readonly` out while they are seeded, back in after
            var config = Path.Combine(dir, "dbdatabuild.yml");
            var readOnlyText = File.ReadAllText(config);
            Assert.Contains("store_east: { engine: readonly postgres", readOnlyText);
            var refused = Cli("connection", "seed", "store_east", "--seed", "1", "--apply");
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("DDB-455", refused.Err);
            File.WriteAllText(config, readOnlyText.Replace("engine: readonly postgres", "engine: postgres"));
            Ok(Cli("connection", "seed", "store_east", "--seed", "1", "--apply"), "seed east");
            Ok(Cli("connection", "seed", "store_west", "--seed", "2", "--apply"), "seed west");
            File.WriteAllText(config, readOnlyText);
            Ok(Cli("project", "compile"), "compile");
            // a shop system keeps no records of its own: what is missing is the warehouse's tracking tables, and that is where `init` is advised
            var before = Cli("connection", "inspect", "store_east");
            Assert.Equal(1, before.Exit);
            Assert.Contains("schema dbdatabuild on sqlserver: not there yet", before.Out);
            Assert.Contains("connection init sqlserver --apply", before.Out);
            Ok(Cli("connection", "init", "sqlserver", "--apply"), "init");
            Ok(Cli("connection", "inspect", "store_east"), "inspect once the tracking tables are there");     // the one tracking connection serves every connection of the project

            foreach (var connection in new[] { "sqlserver", "postgres" })
            {
                var loads = File.ReadAllText(Path.Combine(dir, "rendered", connection, "loads.yml"));
                Assert.Contains("model: \"marts.daily_sales\"", loads);
                Assert.DoesNotContain("excluded:", loads);                                                       // the copy is a load like any other
                Assert.Contains("origins:", loads);
                foreach (var plan in new[] { "hourly", "daily" })
                {
                    var text = File.ReadAllText(Path.Combine(dir, "rendered", connection, "refresh", plan + ".yml"));
                    Assert.True(text.IndexOf("warehouse.orders", StringComparison.Ordinal) is var at and >= 0 && text.IndexOf("marts.daily_sales", StringComparison.Ordinal) > at, plan + ": the copy comes before the report");
                }
            }

            Deploy("sqlserver");
            Deploy("postgres");

            // fan-in: both shops, on both engines, one table, rows kept apart by the slice
            foreach (var (engine, sql) in new[] { (warehouse, "SELECT store_id + '|' + CAST(COUNT(*) AS VARCHAR(10)) FROM warehouse.orders GROUP BY store_id"), (reporting, "SELECT store_id || '|' || COUNT(*) FROM warehouse.orders GROUP BY store_id") })
                Assert.Equal(["E01|60", "W01|60"], await engine.RowsAsync(sql));

            // fan-out: the report on each engine is what the origins give when grouped here
            var expected = new List<string>();
            foreach (var (shop, id) in new[] { (east, "E01"), (west, "W01") })
            {
                var rows = await shop.RowsAsync("SELECT to_char(ordered_on, 'YYYY-MM-DD') || '|' || channel || '|' || CAST(amount AS TEXT) FROM pos.orders");
                expected.AddRange(rows.Select(r => r.Split('|')).GroupBy(p => (p[0], p[1])).Select(g => $"{id}|{g.Key.Item1}|{g.Key.Item2}|{g.Count()}|{g.Sum(p => decimal.Parse(p[2], CultureInfo.InvariantCulture)).ToString("0.00", CultureInfo.InvariantCulture)}"));
            }
            expected.Sort(StringComparer.Ordinal);
            Assert.NotEmpty(expected);
            var onSqlServer = await warehouse.RowsAsync("SELECT store_id + '|' + CONVERT(VARCHAR(10), ordered_on, 23) + '|' + channel + '|' + CAST(orders AS VARCHAR(20)) + '|' + CAST(amount AS VARCHAR(40)) FROM marts.daily_sales");
            var onPostgres = await reporting.RowsAsync("SELECT store_id || '|' || to_char(ordered_on, 'YYYY-MM-DD') || '|' || channel || '|' || orders || '|' || amount FROM marts.daily_sales");
            Assert.Equal(expected, onSqlServer.Order(StringComparer.Ordinal));
            Assert.Equal(expected, onPostgres.Order(StringComparer.Ordinal));
            Ok(Cli("connection", "compare", "sqlserver", "marts.daily_sales", "--against-connection", "postgres"), "compare the two engines");

            // a refresh moves rows between connections too: a new order at a shop reaches the warehouse with the `hourly` refresh, and the report built on it follows in the same run
            await east.ExecAsync("INSERT INTO pos.orders VALUES (1000, '2024-03-20', 'web', 99.99)");
            var hourly = Cli("connection", "refresh", "sqlserver", "hourly");
            Ok(hourly, "refresh sqlserver hourly");
            Assert.Contains("completed", hourly.Out);
            Assert.Equal(["E01|61", "W01|60"], await warehouse.RowsAsync("SELECT store_id + '|' + CAST(COUNT(*) AS VARCHAR(10)) FROM warehouse.orders GROUP BY store_id"));
            Assert.Equal(["E01|60", "W01|60"], await reporting.RowsAsync("SELECT store_id || '|' || COUNT(*) FROM warehouse.orders GROUP BY store_id"));    // the other engine waits for its own refresh
            Assert.Contains("2024-03-20", string.Join(",", await warehouse.RowsAsync("SELECT CONVERT(VARCHAR(10), ordered_on, 23) FROM marts.daily_sales")));
            Assert.Empty(await warehouse.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_name LIKE 'stg_warehouse%'"));                           // the staging table is gone
            // the event: each origin was read, and the transfer rows say which
            var origins = await warehouse.RowsAsync("SELECT load_name FROM dbdatabuild.run_log WHERE operation = 'transfer' AND plan_id = (SELECT MAX(plan_id) FROM dbdatabuild.run_log WHERE membership IS NOT NULL) ORDER BY load_name");
            Assert.Contains("from store_east", origins);
            Assert.Contains("from store_west", origins);
            var daily = Cli("connection", "refresh", "postgres", "daily");
            Ok(daily, "refresh postgres daily");
            Assert.Equal(["E01|61", "W01|60"], await reporting.RowsAsync("SELECT store_id || '|' || COUNT(*) FROM warehouse.orders GROUP BY store_id"));

            // a shop that cannot be reached fails the copy (`failed: origin`) and the report that reads it is skipped; the rest of the plan goes on
            await east.ExecAsync("INSERT INTO pos.orders VALUES (1001, '2024-03-21', 'web', 10.00)");
            string? Broken(string v) => v == "DBDATABUILD_STORE_WEST_READ" ? null : Env(v);
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run(["connection", "refresh", "sqlserver", "hourly", "--project", dir], o, e, environment: Broken);
            Assert.Equal(1, exit);
            Assert.Contains("failed", o.ToString());
            Assert.Contains("origin", o.ToString() + e.ToString());
            Assert.Contains("skipped", o.ToString());
            Assert.Equal(["E01|62", "W01|60"], await warehouse.RowsAsync("SELECT store_id + '|' + CAST(COUNT(*) AS VARCHAR(10)) FROM warehouse.orders GROUP BY store_id"));   // each origin is its own unit: the east shop's new order landed although the west shop could not be reached

            // the plan `weekly` runs the copy's `full` load: each shop is read in full and everything of that shop replaced, so an order deleted at a shop goes (the incremental load never sees a delete)
            await east.ExecAsync("DELETE FROM pos.orders WHERE order_id = 1000");
            Ok(Cli("connection", "refresh", "sqlserver", "hourly"), "refresh sqlserver hourly after a delete");
            Assert.Equal(["E01|62", "W01|60"], await warehouse.RowsAsync("SELECT store_id + '|' + CAST(COUNT(*) AS VARCHAR(10)) FROM warehouse.orders GROUP BY store_id"));   // still there
            var weekly = Cli("connection", "refresh", "sqlserver", "weekly");
            Ok(weekly, "refresh sqlserver weekly");
            Assert.Equal(["E01|61", "W01|60"], await warehouse.RowsAsync("SELECT store_id + '|' + CAST(COUNT(*) AS VARCHAR(10)) FROM warehouse.orders GROUP BY store_id"));   // gone, and the other shop's rows kept
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
