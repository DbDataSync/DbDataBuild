using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DuckDB.NET.Data;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Read-only origins, step 2 (docs/research/read-origins.md): a DuckDB file is the origin of a copy into the two real engines, through the command surface. Its rows arrive intact (every logical type, NULLs, unicode), an
/// incremental refresh reads from the watermark, a `source` resolver runs at the origin in DuckDB's own text, a native query over a Parquet file reads through a `:memory:` origin, and a file that is not there fails the copy as
/// `failed: origin`. The origin needs no container; the destinations are SQL Server 2022 and PostgreSQL 17.
/// </summary>
[Trait("Group", "apply")]
public class DuckDbOriginConformanceTests
{
    public static TheoryData<string> Destinations => new() { "sqlserver", "postgres" };

    private const string Columns =
        "columns:\n" +
        "  - {name: id, type: BIGINT, nullable: false}\n" +
        "  - {name: small, type: SMALLINT}\n" +
        "  - {name: n, type: INTEGER}\n" +
        "  - {name: ratio, type: DOUBLE}\n" +
        "  - {name: flag, type: BOOLEAN}\n" +
        "  - {name: born, type: DATE}\n" +
        "  - {name: seen, type: TIMESTAMP}\n" +
        "  - {name: price, type: \"DECIMAL(18, 3)\"}\n" +
        "  - {name: big, type: \"DECIMAL(28, 6)\"}\n" +
        "  - {name: code, type: \"VARCHAR(40)\"}\n" +
        "  - {name: notes, type: VARCHAR}\n";

    private sealed class Run(Engine destination, string name, string dir) : IDisposable
    {
        public Engine Destination => destination;
        public string Name => name;
        public string Dir => dir;
        public string DbFile => Path.Combine(dir, "analytics.duckdb").Replace('\\', '/');
        public string Lake { get; set; } = ":memory:";
        public bool OriginReachable { get; set; } = true;

        public string? Env(string v) =>
            v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? destination.ConnectionString
            : v == "DBDATABUILD_ANALYTICS_READ" ? (OriginReachable ? DbFile : Path.Combine(dir, "gone.duckdb")) : v == "DBDATABUILD_LAKE_READ" ? Lake : null;

        public (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }

        public void Write(string rel, string text)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Origin(params string[] sql)
        {
            using var c = new DuckDBConnection($"Data Source={DbFile}");
            c.Open();
            foreach (var s in sql) { using var cmd = c.CreateCommand(); cmd.CommandText = s; cmd.ExecuteNonQuery(); }
        }

        public List<string> OriginRows(string sql)
        {
            using var c = new DuckDBConnection($"Data Source={DbFile};access_mode=READ_ONLY");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var rd = cmd.ExecuteReader();
            var rows = new List<string>();
            while (rd.Read()) rows.Add(Convert.ToString(rd.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
            return rows;
        }

        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static string Sensitive(string name) => name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "";

    private static async Task<Run> SetUp(string destination, string extraConfig = "")
    {
        var engine = EngineEnv.Require(destination);
        await engine.StartAsync();
        var run = new Run(engine, destination, Path.Combine(Path.GetTempPath(), "ddb-duck-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(run.Dir);
        run.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\nconnections:\n  analytics: {{ engine: readonly duckdb }}\n  lake: {{ engine: readonly duckdb }}\n{extraConfig}" + Sensitive(destination));
        return run;
    }

    private static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

    private static string PlanOf(Run run, string output) => Path.Combine(run.Dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);

    private static void Deploy(Run run)
    {
        Ok(run.Cli("connection", "init", run.Name, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", run.Name, "--write-plan");
        Ok(plan, "plan");
        Ok(run.Cli("connection", "deploy", "--apply-plan", PlanOf(run, plan.Out)), "deploy");
    }

    /// <summary>Every row as text that does not depend on the engine's own formatting, the same on all three systems.</summary>
    private static async Task<List<string>> ComparableAsync(Engine e, string table)
    {
        var q = e.QuoteIdent;
        string Text(string col, string type) => (e.Name, type) switch
        {
            ("postgres", "BOOLEAN") => $"CASE WHEN {q(col)} IS NULL THEN NULL WHEN {q(col)} THEN '1' ELSE '0' END",
            ("postgres", "TIMESTAMP") => $"to_char({q(col)}, 'YYYY-MM-DD HH24:MI:SS.US')",
            ("postgres", "DATE") => $"to_char({q(col)}, 'YYYY-MM-DD')",
            ("sqlserver", "BOOLEAN") => $"CAST({q(col)} AS VARCHAR(1))",
            ("sqlserver", "TIMESTAMP") => $"CONVERT(VARCHAR(30), {q(col)}, 121)",
            ("sqlserver", "DATE") => $"CONVERT(VARCHAR(10), {q(col)}, 23)",
            ("sqlserver", _) => $"CAST({q(col)} AS NVARCHAR(100))",
            _ => $"CAST({q(col)} AS VARCHAR(100))",
        };
        string[] cols = ["id", "small", "n", "ratio", "flag", "born", "seen", "price", "big", "code", "notes"];
        string[] types = ["BIGINT", "SMALLINT", "INTEGER", "DOUBLE", "BOOLEAN", "DATE", "TIMESTAMP", "DECIMAL", "DECIMAL", "VARCHAR", "VARCHAR"];
        var parts = cols.Select((c, i) => $"COALESCE({Text(c, types[i])}, '<null>')");
        var concat = e.Name == "postgres" ? string.Join(" || '|' || ", parts) : string.Join(" + '|' + ", parts);
        return await e.RowsAsync($"SELECT {concat} FROM {table} ORDER BY id");
    }

    /// <summary>The same text, computed by DuckDB from the origin's file.</summary>
    private const string OriginComparable =
        "SELECT concat_ws('|', COALESCE(CAST(id AS VARCHAR), '<null>'), COALESCE(CAST(small AS VARCHAR), '<null>'), COALESCE(CAST(n AS VARCHAR), '<null>'), COALESCE(CAST(ratio AS VARCHAR), '<null>'), " +
        "COALESCE(CASE WHEN flag IS NULL THEN NULL WHEN flag THEN '1' ELSE '0' END, '<null>'), COALESCE(strftime(born, '%Y-%m-%d'), '<null>'), COALESCE(strftime(seen, '%Y-%m-%d %H:%M:%S.%f'), '<null>'), " +
        "COALESCE(CAST(price AS VARCHAR), '<null>'), COALESCE(CAST(big AS VARCHAR), '<null>'), COALESCE(code, '<null>'), COALESCE(notes, '<null>')) FROM src.items ORDER BY id";

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task The_rows_of_a_duckdb_file_arrive_intact_in_the_engine_and_the_next_deploy_loads_again(string destination)
    {
        using var run = await SetUp(destination);
        await using var to = run.Destination;
        run.Write("models/src/items.yml", $"name: src.items\nkind: mapped\nconnections=: [analytics]\ngrain: [id]\n{Columns}");
        run.Write("models/dst/items.yml", "name: dst.items\nkind: {copy: {from: src.items}}\nindexes:\n  - {name: ix_items_code, columns: [code]}\n");
        run.Origin("CREATE SCHEMA src",
            "CREATE TABLE src.items (id BIGINT NOT NULL PRIMARY KEY, small SMALLINT, n INTEGER, ratio DOUBLE, flag BOOLEAN, born DATE, seen TIMESTAMP, price DECIMAL(18,3), big DECIMAL(28,6), code VARCHAR, notes VARCHAR)",
            "INSERT INTO src.items VALUES " +
            "(1, 7, 100000, 3.5, true, DATE '2024-02-29', TIMESTAMP '2024-02-29 13:14:15.123456', 12345.678, 12345678901234567890.123456, 'café ☕', 'long text'), " +
            "(2, -32768, -2147483648, -0.25, false, DATE '0001-01-01', TIMESTAMP '1999-12-31 23:59:59.999999', -0.001, -0.000001, '', ''), " +
            "(3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL), " +
            "(4, 32767, 2147483647, 1024.5, true, DATE '9999-12-31', TIMESTAMP '2000-01-01 00:00:00', 0, 0, 'it''s \"quoted\"', 'line1' || chr(10) || 'line2')");

        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        Ok(run.Cli("connection", "status", destination), "status");
        Ok(run.Cli("connection", "inspect", "analytics"), "inspect the origin");
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        var planFile = PlanOf(run, plan.Out);
        var text = File.ReadAllText(planFile);
        Assert.Contains("type: transfer", text);
        Assert.Contains("origin: \"analytics\"", text);
        Assert.Contains("SELECT \\\"id\\\", \\\"small\\\"", text);                                                     // DuckDB's quoting, in the plan
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile, "--dry-run"), "dry run");
        Assert.Empty(await to.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_schema = 'dst'"));

        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        var copied = await ComparableAsync(to, "dst.items");
        Assert.Equal(string.Join("\n", run.OriginRows(OriginComparable)), string.Join("\n", copied));
        Assert.Equal(4, copied.Count);
        Assert.Empty(await to.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_name LIKE 'stg_dst%'"));        // the staging table is gone

        // the origin changes; the next plan has no structure to change and loads again (a copy is a full replace)
        run.Origin("INSERT INTO src.items (id) VALUES (5)");
        var again = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(again, "second plan");
        Assert.DoesNotContain("create table", File.ReadAllText(PlanOf(run, again.Out)));
        Ok(run.Cli("connection", "deploy", "--apply-plan", PlanOf(run, again.Out)), "second apply");
        Assert.Equal(5, (await ComparableAsync(to, "dst.items")).Count);
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task A_copy_from_a_duckdb_file_is_a_load_of_a_refresh_reads_from_the_watermark_and_a_missing_file_fails_it_as_origin(string destination)
    {
        using var run = await SetUp(destination, "refresh:\n  plans:\n    hourly: { on_failure: continue }\n");
        await using var to = run.Destination;
        const string events = "columns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: day, type: DATE, nullable: false}\n  - {name: v, type: \"VARCHAR(20)\"}\n";
        run.Write("models/src/events.yml", $"name: src.events\nkind: mapped\nconnections=: [analytics]\ngrain: [id]\n{events}");
        run.Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events, unique_key: [id], watermark: {column: day, lookback: 1 days}}}\nrefresh: { hourly: default }\n");
        run.Write("models/dst/summary.yml", "name: dst.summary\nkind: full\ncolumns:\n  - {name: day, type: DATE, nullable: false}\n  - {name: n, type: BIGINT}\nrefresh: { hourly: default }\n");
        run.Write("models/dst/summary.sql", "SELECT day, COUNT(*) AS n FROM dst.events GROUP BY day\n");
        run.Origin("CREATE SCHEMA src", "CREATE TABLE src.events (id BIGINT NOT NULL, day DATE NOT NULL, v VARCHAR)", "INSERT INTO src.events VALUES (1, '2026-03-01', 'a'), (2, '2026-03-02', 'b'), (3, '2026-03-03', 'c')");
        Deploy(run);
        async Task<List<string>> Rows() => await to.RowsAsync(destination == "postgres" ? "SELECT CAST(id AS VARCHAR(10)) || ':' || v FROM dst.events ORDER BY id" : "SELECT CAST(id AS VARCHAR(10)) + ':' + v FROM dst.events ORDER BY id");
        Assert.Equal(["1:a", "2:b", "3:c"], await Rows());

        var loads = File.ReadAllText(Path.Combine(run.Dir, "rendered", destination, "loads.yml"));
        Assert.Contains("engine: \"duckdb\"", loads);
        Assert.Contains("WHERE \\\"day\\\" >= ?", loads);                                                      // the bound is a positional value, and the plan says which
        Assert.Contains("parameter_order:", loads);

        // new rows at the origin, and a change in the newest day: the incremental read replaces what it re-reads
        run.Origin("INSERT INTO src.events VALUES (4, '2026-03-05', 'd')", "UPDATE src.events SET v = 'c2' WHERE id = 3");
        Ok(run.Cli("connection", "refresh", destination, "hourly", "--dry-run"), "dry run");
        Assert.Equal(["1:a", "2:b", "3:c"], await Rows());
        var refresh = run.Cli("connection", "refresh", destination, "hourly", "--format", "json");
        Ok(refresh, "refresh");
        Assert.Equal("completed", (string)System.Text.Json.Nodes.JsonNode.Parse(refresh.Out)!["data"]!["outcome"]!);
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await Rows());
        Assert.Equal(["4"], await to.RowsAsync("SELECT CAST(SUM(n) AS VARCHAR(10)) FROM dst.summary"));
        Assert.Equal(["from analytics"], await to.RowsAsync("SELECT DISTINCT load_name FROM dbdatabuild.run_log WHERE operation = 'transfer' AND plan_id LIKE 'ref-%'"));

        // a file that is not there: the copy fails with the reason, what reads it is skipped, the table keeps what it had, and the file is not created
        run.Origin("INSERT INTO src.events VALUES (5, '2026-03-06', 'e')");
        run.OriginReachable = false;
        var failed = run.Cli("connection", "refresh", destination, "hourly", "--format", "json");
        Assert.Equal(1, failed.Exit);
        var results = System.Text.Json.Nodes.JsonNode.Parse(failed.Out)!["data"]!["results"]!.AsArray().ToDictionary(r => (string)r!["model"]!, r => r!);
        Assert.Equal("failed: origin", (string)results["dst.events"]["status"]!);
        Assert.Equal("skipped: upstream failed", (string)results["dst.summary"]["status"]!);
        Assert.False(File.Exists(Path.Combine(run.Dir, "gone.duckdb")));
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await Rows());
        run.OriginReachable = true;
        Ok(run.Cli("connection", "refresh", destination, "hourly", "--only-failed"), "only failed");
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d", "5:e"], await Rows());
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task A_source_resolver_runs_at_the_duckdb_origin_in_its_own_text_and_its_value_bounds_the_range_the_copy_loads(string destination)
    {
        using var run = await SetUp(destination);
        await using var to = run.Destination;
        run.Write("models/src/events.yml", "name: src.events\nkind: mapped\nconnections=: [analytics]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: day, type: DATE, nullable: false}\n");
        run.Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events}}\n" +
            "loads:\n  window:\n    strategy: delete_insert_by_range\n    column: day\n    params: {start: DATE, end: DATE}\n    inputs:\n" +
            "      start: {resolver: {source: \"SELECT min(day) FROM src.events WHERE day >= DATE '2026-03-01'\"}}\n" +
            "      end: {resolver: {expression: \"DATE '2026-03-04'\"}}\n");
        run.Origin("CREATE SCHEMA src", "CREATE TABLE src.events (id BIGINT NOT NULL, day DATE NOT NULL)",
            "INSERT INTO src.events SELECT i + 1, DATE '2026-02-27' + CAST(i AS INTEGER) FROM range(0, 8) t(i)");           // 02-27 .. 03-06
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        Assert.Contains("engine: \"duckdb\"", File.ReadAllText(Path.Combine(run.Dir, "rendered", destination, "loads.yml")));
        var plan = run.Cli("connection", "deploy", destination, "--write-plan", "--op", "dst.events=window");
        Ok(plan, "plan");
        var planFile = PlanOf(run, plan.Out);
        var text = File.ReadAllText(planFile);
        Assert.Contains("planned_source: \"resolver:source\"", text);
        Assert.Contains("planned_value: \"2026-03-01\"", text);                                                          // the origin's own answer, before anything is applied
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        Assert.Equal(["2026-03-01", "2026-03-02", "2026-03-03"], await to.RowsAsync("SELECT CAST(day AS VARCHAR(10)) FROM dst.events ORDER BY day"));
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task A_native_query_over_a_parquet_file_is_copied_through_a_memory_origin_with_bound_values(string destination)
    {
        var parquet = Path.Combine(Path.GetTempPath(), "ddb-duck-" + Guid.NewGuid().ToString("N") + ".parquet").Replace('\\', '/');
        await using (var w = new DuckDBConnection("Data Source=:memory:"))
        {
            w.Open();
            using var cmd = w.CreateCommand();
            cmd.CommandText = $"COPY (SELECT i AS id, 'o' || i AS label FROM range(1, 7) t(i)) TO '{parquet}' (FORMAT PARQUET)";
            cmd.ExecuteNonQuery();
        }
        try
        {
            using var run = await SetUp(destination, $"parameters:\n  file: \"{parquet}\"\n  min_id: {{ type: BIGINT, value: \"3\" }}\n");
            await using var to = run.Destination;
            run.Write("models/src/orders.yml", "name: src.orders\nkind:\n  native:\n    query: SELECT id, label FROM read_parquet(${project.file}) WHERE id >= ${project.min_id}\nconnections=: [lake]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: label, type: \"VARCHAR(20)\"}\n");
            run.Write("models/dst/orders.yml", "name: dst.orders\nkind: {copy: {from: src.orders}}\n");
            Ok(run.Cli("connection", "init", destination, "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", destination, "--write-plan");
            Ok(plan, "plan");
            var planFile = PlanOf(run, plan.Out);
            var text = File.ReadAllText(planFile);
            Assert.Contains("read_parquet(?)", text);
            Assert.Contains("parameter_order:", text);
            Assert.DoesNotContain(parquet, text.Replace($"value: \"{parquet}\"", ""));                                    // the file name is a bound value, never in the read text
            Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
            Assert.Equal(["3:o3", "4:o4", "5:o5", "6:o6"], await to.RowsAsync(destination == "postgres" ? "SELECT CAST(id AS VARCHAR(10)) || ':' || label FROM dst.orders ORDER BY id" : "SELECT CAST(id AS VARCHAR(10)) + ':' + label FROM dst.orders ORDER BY id"));
        }
        finally { try { File.Delete(parquet); } catch (IOException) { } }
    }
}
