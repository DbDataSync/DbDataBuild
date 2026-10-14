using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Macros on the real engines: one macro, a live table and its snapshot, and for each the SQL the engine runs is the one written for that table.</summary>
[Trait("Group", "apply")]
public class MacroConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private const string Macros = """
        CREATE MACRO snapshot_at(tbl, col) AS TABLE
          SELECT * EXCLUDE (__none), CASE WHEN col IS NULL THEN current_date ELSE COLUMNS(lambda c: c = coalesce(col, '__none')) END AS as_of_date
          FROM (SELECT *, NULL::DATE AS __none FROM query_table(tbl));

        CREATE MACRO status_totals(tbl, col) AS TABLE
          SELECT as_of_date, status, sum(amount) AS total FROM snapshot_at(tbl, col) GROUP BY as_of_date, status;
        """;

    private static string Config(string name) =>
        $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name} }}\n" +
        (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "");

    private const string Cols = "grain: [as_of_date, status]\ncolumns:\n  - {name: as_of_date, type: DATE, nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: total, type: \"DECIMAL(38,2)\", nullable: false}\n";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task One_macro_gives_each_table_its_own_query_and_the_engine_returns_the_rows_DuckDB_would(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-macro-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        try
        {
            await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA IF NOT EXISTS src" : "IF SCHEMA_ID('src') IS NULL EXEC('CREATE SCHEMA src')");
            await engine.ExecAsync("DROP TABLE IF EXISTS src.orders");
            await engine.ExecAsync("DROP TABLE IF EXISTS src.orders_snap");
            await engine.ExecAsync("CREATE TABLE src.orders (order_id bigint NOT NULL, amount decimal(18,2) NOT NULL, status varchar(20) NOT NULL)");
            await engine.ExecAsync("CREATE TABLE src.orders_snap (order_id bigint NOT NULL, amount decimal(18,2) NOT NULL, status varchar(20) NOT NULL, snap_date date NOT NULL)");
            await engine.ExecAsync("INSERT INTO src.orders VALUES (1, 10.50, 'open'), (2, 20.25, 'open'), (3, 7.00, 'shipped')");
            await engine.ExecAsync("INSERT INTO src.orders_snap VALUES (1, 10.00, 'open', '2026-01-01'), (2, 5.00, 'open', '2026-01-01'), (1, 12.00, 'shipped', '2026-02-01'), (2, 6.00, 'open', '2026-02-01')");

            Write("dbdatabuild.yml", Config(name));
            Write("macros/snapshots.sql", Macros);
            Write("models/src/orders.yml", "name: src.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n");
            Write("models/src/orders_snap.yml", "name: src.orders_snap\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: snap_date, type: DATE, nullable: false}\n");
            Write("models/marts/live_totals.yml", "name: marts.live_totals\nkind: full\n" + Cols);
            Write("models/marts/live_totals.sql", "SELECT * FROM status_totals('src.orders', NULL)\n");
            Write("models/marts/snap_totals.yml", "name: marts.snap_totals\nkind: full\n" + Cols);
            Write("models/marts/snap_totals.sql", "SELECT * FROM status_totals('src.orders_snap', 'snap_date')\n");

            Ok(Cli("connection", "init", name, "--apply"), "init");
            Ok(Cli("project", "compile"), "render");
            // what each engine is given is the query for the table: no choice left in the text
            var snapScript = File.ReadAllText(Path.Combine(dir, "rendered", name, "marts.snap_totals", "load.default.sql"));
            var liveScript = File.ReadAllText(Path.Combine(dir, "rendered", name, "marts.live_totals", "load.default.sql"));
            Assert.Contains("orders_snap", snapScript); Assert.DoesNotContain("CASE", snapScript); Assert.DoesNotContain("UNION", snapScript);
            Assert.DoesNotContain("orders_snap", liveScript); Assert.DoesNotContain("snap_date", liveScript); Assert.DoesNotContain("UNION", liveScript);
            var plan = Cli("connection", "deploy", name, "--write-plan");
            Ok(plan, "plan");
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(plan.Out)), "apply");
            // the snapshot: the rows DuckDB gives for the same query (the sums per snapshot date and status)
            Assert.Equal(["2026-01-01|open|15.00", "2026-02-01|open|6.00", "2026-02-01|shipped|12.00"],
                await engine.RowsAsync("SELECT CONCAT(CONVERT(VARCHAR(10), as_of_date, 23), '|', status, '|', CAST(total AS VARCHAR(20))) FROM marts.snap_totals ORDER BY 1".Replace("CONVERT(VARCHAR(10), as_of_date, 23)", name == "postgres" ? "to_char(as_of_date, 'YYYY-MM-DD')" : "CONVERT(VARCHAR(10), as_of_date, 23)")));
            // the live table: as of today, by the engine's own clock
            var today = (await engine.RowsAsync(name == "postgres" ? "SELECT to_char(CURRENT_DATE, 'YYYY-MM-DD')" : "SELECT CONVERT(VARCHAR(10), CAST(GETDATE() AS DATE), 23)")).Single();
            Assert.Equal([$"{today}|open|30.75", $"{today}|shipped|7.00"],
                await engine.RowsAsync("SELECT CONCAT(to_char(as_of_date, 'YYYY-MM-DD'), '|', status, '|', CAST(total AS VARCHAR(20))) FROM marts.live_totals ORDER BY 1".Replace("to_char(as_of_date, 'YYYY-MM-DD')", name == "postgres" ? "to_char(as_of_date, 'YYYY-MM-DD')" : "CONVERT(VARCHAR(10), as_of_date, 23)")));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_name_a_connection_gives_the_macro_makes_each_connection_run_the_query_for_its_own_table()
    {
        // two connections of one engine: a live system, and a copy of it that keeps snapshots, each in its own database
        var server = EngineEnv.Require("sqlserver");
        await server.StartAsync();
        await using var _ = server;
        var snapDb = "ddb_snap_" + Guid.NewGuid().ToString("N")[..8];
        var snapConnection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = snapDb }.ConnectionString;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-macro-names-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v is "DBDATABUILD_LIVE_READ" or "DBDATABUILD_LIVE_WRITE" ? server.ConnectionString : v is "DBDATABUILD_SNAP_READ" or "DBDATABUILD_SNAP_WRITE" ? snapConnection : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        async Task<List<string>> Rows(string connection, string sql)
        {
            await using var c = new Microsoft.Data.SqlClient.SqlConnection(connection);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var rows = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) rows.Add(Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
            return rows;
        }
        async Task Exec(string connection, string sql) => await Rows(connection, sql + "; SELECT 1");
        try
        {
            await server.ExecAsync($"CREATE DATABASE [{snapDb}]");
            await server.ExecAsync("IF SCHEMA_ID('src') IS NULL EXEC('CREATE SCHEMA src')");
            await server.ExecAsync("DROP TABLE IF EXISTS src.orders");
            await server.ExecAsync("CREATE TABLE src.orders (order_id bigint NOT NULL, amount decimal(18,2) NOT NULL, status varchar(20) NOT NULL)");
            await server.ExecAsync("INSERT INTO src.orders VALUES (1, 10.50, 'open'), (2, 20.25, 'open'), (3, 7.00, 'shipped')");
            await Exec(snapConnection, "EXEC('CREATE SCHEMA src')");
            await Exec(snapConnection, "CREATE TABLE src.orders_snap (order_id bigint NOT NULL, amount decimal(18,2) NOT NULL, status varchar(20) NOT NULL, snap_date date NOT NULL)");
            await Exec(snapConnection, "INSERT INTO src.orders_snap VALUES (1, 10.00, 'open', '2026-01-01'), (2, 5.00, 'open', '2026-01-01'), (1, 12.00, 'shipped', '2026-02-01')");

            Write("dbdatabuild.yml", """
                defaults: {connections: [live, snap]}
                tracking: none
                connections:
                  live:
                    engine: sqlserver
                    parameters:
                      table: { type: NAME, value: src.orders }
                      date_col: { type: NAME, value: "" }
                  snap:
                    engine: sqlserver
                    parameters:
                      table: { type: NAME, value: src.orders_snap }
                      date_col: { type: NAME, value: snap_date }
                """);
            Write("macros/snapshots.sql", Macros);
            Write("models/src/orders.yml", "name: src.orders\nkind: mapped\nconnections=: [live]\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n");
            Write("models/src/orders_snap.yml", "name: src.orders_snap\nkind: mapped\nconnections=: [snap]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: snap_date, type: DATE, nullable: false}\n");
            Write("models/marts/totals.yml", "name: marts.totals\nkind: full\n" + Cols);
            Write("models/marts/totals.sql", "SELECT * FROM status_totals(${connection.table}, ${connection.date_col})\n");

            Ok(Cli("project", "compile"), "validate");                                                                         // neither connection is told it reads a table that is on the other
            Ok(Cli("project", "compile"), "render");
            Assert.Contains("orders_snap", File.ReadAllText(Path.Combine(dir, "rendered", "snap", "marts.totals", "load.default.sql")));
            Assert.DoesNotContain("snap_date", File.ReadAllText(Path.Combine(dir, "rendered", "live", "marts.totals", "load.default.sql")));
            Ok(Cli("project", "compile", "--check"), "render --check");
            foreach (var connection in new[] { "live", "snap" })
            {
                var plan = Cli("connection", "deploy", connection, "--write-plan");
                Ok(plan, $"plan {connection}");
                Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(plan.Out)), $"apply {connection}");
            }
            var sql = "SELECT CONCAT(CONVERT(VARCHAR(10), as_of_date, 23), '|', status, '|', CAST(total AS VARCHAR(20))) FROM marts.totals ORDER BY 1";
            var today = (await Rows(server.ConnectionString, "SELECT CONVERT(VARCHAR(10), CAST(GETDATE() AS DATE), 23)")).Single();
            Assert.Equal([$"{today}|open|30.75", $"{today}|shipped|7.00"], await Rows(server.ConnectionString, sql));
            Assert.Equal(["2026-01-01|open|15.00", "2026-02-01|shipped|12.00"], await Rows(snapConnection, sql));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { Microsoft.Data.SqlClient.SqlConnection.ClearAllPools(); await server.ExecAsync($"ALTER DATABASE [{snapDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{snapDb}]"); } catch (Exception) { }
        }
    }
}
