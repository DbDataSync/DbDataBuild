using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The `stores` template on four real databases, through the commands a person would type: two PostgreSQL shop systems (store_east, store_west) seeded on their own, a SQL Server warehouse and a PostgreSQL
/// reporting database. The fan-in copy lands both shops in `warehouse.orders` on both engines, the fan-out model `marts.daily_sales` is built on both, and its rows are what the origin tables give when they are
/// grouped here (not by either engine under test). The refresh plan of each engine leaves the copy to a deploy, and a refresh does not move rows between connections.
/// </summary>
[Trait("Group", "templates")]
public partial class StoresTemplateConformanceTests
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
            Ok(Cli("project", "create", "stores", dir), "create");
            Ok(Cli("connection", "seed", "store_east", "--seed", "1", "--apply"), "seed east");
            Ok(Cli("connection", "seed", "store_west", "--seed", "2", "--apply"), "seed west");
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
                var refreshPlan = File.ReadAllText(Path.Combine(dir, "rendered", connection, "refresh.plan.yml"));
                Assert.Contains("model: \"marts.daily_sales\"", refreshPlan);
                Assert.Contains("excluded:", refreshPlan);
                Assert.Contains("a copy moves rows between connections; a deploy plans it", refreshPlan);
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

            // a refresh runs what is routine and leaves the copy alone: a new order at a shop reaches the warehouse with a deploy, not before
            await east.ExecAsync("INSERT INTO pos.orders VALUES (1000, '2024-03-02', 'web', 99.99)");
            foreach (var connection in new[] { "sqlserver", "postgres" })
            {
                var refresh = Cli("connection", "refresh", connection);
                Ok(refresh, $"refresh {connection}");
                Assert.Contains("warehouse.orders", refresh.Out.Split('\n').Single(l => l.Contains("not routine")));
            }
            Assert.Equal(["120"], await warehouse.RowsAsync("SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM warehouse.orders"));
            Deploy("sqlserver");
            Assert.Equal(["E01|61", "W01|60"], await warehouse.RowsAsync("SELECT store_id + '|' + CAST(COUNT(*) AS VARCHAR(10)) FROM warehouse.orders GROUP BY store_id"));
            Assert.Equal(["E01|60", "W01|60"], await reporting.RowsAsync("SELECT store_id || '|' || COUNT(*) FROM warehouse.orders GROUP BY store_id"));    // the other engine waits for its own deploy
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
