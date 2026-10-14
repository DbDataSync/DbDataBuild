using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// A copy between two real engines, both ways, through the command surface: the rows of a table on one connection arrive in a table on the other with the values and the NULLs intact. The two
/// connections here are the two engines (`sqlserver`, `postgres`), each with its own login.
/// </summary>
[Trait("Group", "apply")]
public partial class CopyConformanceTests
{
    public static TheoryData<string, string> Directions => new() { { "postgres", "sqlserver" }, { "sqlserver", "postgres" } };

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

    private sealed class Pair(Engine origin, Engine destination, string dir)
    {
        public Engine Origin => origin;
        public Engine Destination => destination;
        public string Dir => dir;

        public Func<string, string?> Env => v =>
            v == LoginSettings.VariableName(origin.Name, Login.Read) ? origin.ConnectionString
            : v == LoginSettings.VariableName(destination.Name, Login.Read) || v == LoginSettings.VariableName(destination.Name, Login.Write) ? destination.ConnectionString
            : null;

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
    }

    private static string Sensitive(string name) => name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "";

    private static async Task<Pair> SetUp(string origin, string destination)
    {
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync();
        await to.StartAsync();
        var pair = new Pair(from, to, Path.Combine(Path.GetTempPath(), "ddb-copy-" + Guid.NewGuid().ToString("N")));
        pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\n" + Sensitive(destination));
        pair.Write("models/src/items.yml", $"name: src.items\nkind: mapped\nconnections=: [{origin}]\ngrain: [id]\n{Columns}");
        pair.Write("models/dst/items.yml", "name: dst.items\nkind: {copy: {from: src.items}}\nindexes:\n  - {name: ix_items_code, columns: [code]}\n");
        await from.ExecAsync(origin == "postgres" ? "CREATE SCHEMA src" : "EXEC('CREATE SCHEMA src')");
        var q = from.QuoteIdent;
        await from.ExecAsync($"CREATE TABLE src.items ({q("id")} BIGINT NOT NULL, {q("small")} SMALLINT, {q("n")} INTEGER, {q("ratio")} {(origin == "postgres" ? "DOUBLE PRECISION" : "FLOAT(53)")}, {q("flag")} {(origin == "postgres" ? "BOOLEAN" : "BIT")}, " +
                             $"{q("born")} DATE, {q("seen")} {from.ColumnType("TIMESTAMP")}, {q("price")} {from.ColumnType("DECIMAL(18,3)")}, {q("big")} {from.ColumnType("DECIMAL(28,6)")}, {q("code")} {from.ColumnType("VARCHAR(40)")}, {q("notes")} {(origin == "postgres" ? "TEXT" : "NVARCHAR(MAX)")})");
        await from.ExecAsync("INSERT INTO src.items VALUES " +
            $"(1, 7, 100000, 3.5, {(origin == "postgres" ? "TRUE" : "1")}, '2024-02-29', '2024-02-29 13:14:15.123456', 12345.678, 12345678901234567890.123456, N'café ☕', N'long text'), " +
            $"(2, -32768, -2147483648, -0.25, {(origin == "postgres" ? "FALSE" : "0")}, '0001-01-01', '1999-12-31 23:59:59.999999', -0.001, -0.000001, N'', N''), " +
            "(3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL), " +
            $"(4, 32767, 2147483647, 1e100, {(origin == "postgres" ? "TRUE" : "1")}, '9999-12-31', '2000-01-01 00:00:00', 0, 0, N'it''s \"quoted\"', N'line1{(origin == "postgres" ? "\n" : "' + CHAR(10) + N'")}line2')".Replace("1e100", origin == "postgres" ? "1e100" : "1e100"));
        return pair;
    }

    private static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

    /// <summary>Every row as the same text on both engines: each value converted in SQL to a string that does not depend on the engine's own formatting.</summary>
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
        return await e.RowsAsync($"SELECT {concat} FROM {table}");
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task The_rows_of_a_table_on_one_engine_arrive_intact_on_the_other(string origin, string destination)
    {
        var pair = await SetUp(origin, destination);
        await using var fromEngine = pair.Origin;
        await using var toEngine = pair.Destination;
        try
        {
            Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "render --write");
            Ok(pair.Cli("connection", "status", destination), "check");
            var plan = pair.Cli("connection", "deploy", destination, "--write-plan");
            Ok(plan, "plan");
            var planFile = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            var text = File.ReadAllText(planFile);
            Assert.Contains("type: transfer", text);
            Assert.Contains($"origin: \"{origin}\"", text);

            var dry = pair.Cli("connection", "deploy", "--apply-plan", planFile, "--dry-run");
            Ok(dry, "apply --dry-run");
            Assert.Empty(await toEngine.RowsAsync($"SELECT 1 FROM information_schema.tables WHERE table_schema = 'dst'"));        // nothing was created

            Ok(pair.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
            var expected = await ComparableAsync(fromEngine, "src.items");
            var copied = await ComparableAsync(toEngine, "dst.items");
            Assert.Equal(string.Join("\n", expected), string.Join("\n", copied));
            Assert.Equal(4, copied.Count);
            Assert.Empty(await toEngine.RowsAsync($"SELECT 1 FROM information_schema.tables WHERE table_name LIKE 'stg_dst%'"));    // the staging table is gone

            // the next plan has nothing to change in the structure and loads again: a copy is a full replace
            var again = pair.Cli("connection", "deploy", destination, "--write-plan");
            Ok(again, "second plan");
            var second = File.ReadAllText(Path.Combine(pair.Dir, Regex.Match(again.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value));
            Assert.DoesNotContain("create table", second);
            await fromEngine.ExecAsync("INSERT INTO src.items (id) VALUES (5)");
            Ok(pair.Cli("connection", "deploy", "--apply-plan", Path.Combine(pair.Dir, Regex.Match(again.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value)), "second apply");
            Assert.Equal(5, (await ComparableAsync(toEngine, "dst.items")).Count);
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_missing_origin_login_stops_the_apply_at_that_origins_transfer_and_a_value_that_cannot_be_held_stops_the_copy()
    {
        var pair = await SetUp("postgres", "sqlserver");
        await using var fromEngine = pair.Origin;
        await using var toEngine = pair.Destination;
        try
        {
            Ok(pair.Cli("connection", "init", "sqlserver", "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "render --write");
            var plan = pair.Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(plan, "plan");
            var planFile = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);

            // no login for the origin: the apply stops at the transfer from it (the steps before it, the DDL of the destination, have run)
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run(["connection", "deploy", "--apply-plan", planFile, "--project", pair.Dir], o, e, environment: v => v == LoginSettings.VariableName("sqlserver", Login.Read) || v == LoginSettings.VariableName("sqlserver", Login.Write) ? toEngine.ConnectionString : null);
            Assert.NotEqual(0, exit);
            Assert.Contains("DBDATABUILD_POSTGRES_READ", e.ToString() + o.ToString());
            Assert.Empty(await toEngine.RowsAsync("SELECT 1 FROM dst.items"));                                  // and nothing was copied

            // a decimal wider than the declared type cannot be held: the transfer stops, the destination table is not loaded
            await fromEngine.ExecAsync("DELETE FROM src.items; INSERT INTO src.items (id, big) VALUES (1, 1)");
            await fromEngine.ExecAsync("ALTER TABLE src.items ALTER COLUMN big TYPE NUMERIC(38, 6)");
            await fromEngine.ExecAsync("UPDATE src.items SET big = 99999999999999999999999999999999.123456");
            var failed = pair.Cli("connection", "deploy", "--apply-plan", planFile);
            Assert.NotEqual(0, failed.Exit);
            Assert.Empty(await toEngine.RowsAsync("SELECT 1 FROM dst.items"));
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task A_table_of_a_few_hundred_thousand_rows_moves_in_bulk(string origin, string destination)
    {
        var pair = await SetUp(origin, destination);
        await using var fromEngine = pair.Origin;
        await using var toEngine = pair.Destination;
        try
        {
            var Rows = int.TryParse(Environment.GetEnvironmentVariable("DDB_SCALE_ROWS"), out var scaled) && Environment.GetEnvironmentVariable("DDB_SCALE") == "1" ? scaled : 300_000;       // DDB_SCALE=1 DDB_SCALE_ROWS=3000000 for a larger run
            await fromEngine.ExecAsync("DELETE FROM src.items");
            await fromEngine.ExecAsync(origin == "postgres"
                ? $"INSERT INTO src.items (id, n, price, code, notes, seen) SELECT g, g % 1000, g / 7.0, 'code-' || g, repeat('x', 100), TIMESTAMP '2024-01-01' + g * INTERVAL '1 second' FROM generate_series(1, {Rows}) g"
                : $"INSERT INTO src.items (id, n, price, code, notes, seen) SELECT TOP ({Rows}) n, n % 1000, n / 7.0, 'code-' + CAST(n AS NVARCHAR(20)), REPLICATE(N'x', 100), DATEADD(SECOND, n, '2024-01-01') FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) t");
            Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "render --write");
            var plan = pair.Cli("connection", "deploy", destination, "--write-plan");
            Ok(plan, "plan");
            var planFile = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Ok(pair.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
            clock.Stop();
            Assert.Equal(Rows.ToString(), (await toEngine.RowsAsync("SELECT COUNT(*) FROM dst.items")).Single());
            Assert.Equal((await fromEngine.RowsAsync("SELECT SUM(n) FROM src.items")).Single(), (await toEngine.RowsAsync("SELECT SUM(n) FROM dst.items")).Single());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30 + Rows / 5_000), $"{Rows} rows took {clock.Elapsed.TotalSeconds:0.0}s");
            Console.WriteLine($"copy {origin} -> {destination}: {Rows} rows in {clock.Elapsed.TotalSeconds:0.0}s, peak working set of the test process {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1_048_576} MB");
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task One_application_on_several_systems_lands_in_one_table_one_origin_at_a_time()
    {
        var store17 = EngineEnv.Require("postgres");
        var store18 = EngineEnv.Require("postgres");
        var warehouse = EngineEnv.Require("sqlserver");
        await store17.StartAsync(); await store18.StartAsync(); await warehouse.StartAsync();
        await using var _17 = store17; await using var _18 = store18; await using var _wh = warehouse;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-fanin-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) =>
            v == "DBDATABUILD_STORE_17_READ" ? store17.ConnectionString : v == "DBDATABUILD_STORE_18_READ" ? store18.ConnectionString
            : v is "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" ? warehouse.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var path = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        try
        {
            foreach (var (store, rows) in new[] { (store17, "(1, 10.00), (2, 20.00)"), (store18, "(1, 11.00), (3, 33.00)") })
            {
                await store.ExecAsync("CREATE SCHEMA pos");
                await store.ExecAsync("CREATE TABLE pos.orders (order_id BIGINT NOT NULL, total NUMERIC(10,2))");
                await store.ExecAsync($"INSERT INTO pos.orders VALUES {rows}");
            }
            Write("dbdatabuild.yml", "connections:\n  store_17: { engine: postgres, parameters: { store_id: \"017\" } }\n  store_18: { engine: postgres, parameters: { store_id: \"018\" } }\ndefaults:\n  connections: [sqlserver]\ntracking: { connection: sqlserver }\n");
            Write("models/pos/orders.yml", "name: pos.orders\nkind: mapped\nconnections=: [store_17, store_18]\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: total, type: \"DECIMAL(10, 2)\"}\n");
            Write("models/warehouse/orders.yml", "name: warehouse.orders\nkind:\n  copy:\n    from: pos.orders\n    slice: {column: store_id, value: \"${origin.store_id}\", type: \"VARCHAR(10)\"}\n");

            Ok(Cli("connection", "init", "sqlserver", "--apply"), "init");
            Ok(Cli("project", "compile"), "render --write");
            var plan = Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(plan, "plan");
            var text = File.ReadAllText(PlanOf(plan.Out));
            Assert.Equal(2, Regex.Matches(text, "type: transfer").Count);                                         // one transfer per origin
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(plan.Out)), "apply");
            Assert.Equal(["1|017|10.00", "1|018|11.00", "2|017|20.00", "3|018|33.00"], await warehouse.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) + '|' + store_id + '|' + CAST(total AS VARCHAR(20)) FROM warehouse.orders"));

            // a row that belongs to no origin of this copy is not touched by a run, and each origin's run replaces only its own rows
            await warehouse.ExecAsync("INSERT INTO warehouse.orders (order_id, total, store_id) VALUES (9, 90.00, '999')");
            await store17.ExecAsync("DELETE FROM pos.orders WHERE order_id = 1; INSERT INTO pos.orders VALUES (4, 40.00)");
            var again = Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(again, "second plan");
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(again.Out)), "second apply");
            Assert.Equal(["1|018|11.00", "2|017|20.00", "3|018|33.00", "4|017|40.00", "9|999|90.00"], await warehouse.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) + '|' + store_id + '|' + CAST(total AS VARCHAR(20)) FROM warehouse.orders"));

            // version skew: store 18 lost a column. The plan is made (an origin is checked when its own transfer's turn comes, not before), and the apply stops at that origin: it names the origin and says nothing
            // about the other, whose rows were loaded before it (each origin is its own unit)
            await store18.ExecAsync("ALTER TABLE pos.orders DROP COLUMN total");
            await store17.ExecAsync("INSERT INTO pos.orders VALUES (6, 60.00)");
            // a plan's id comes from its content, and an applied plan is applied once: each variant below gets an index of its own, so that its plan is not the plan of an earlier apply
            string Variant(string onMismatch, params string[] indexes) => "name: warehouse.orders\nkind:\n  copy:\n    from: pos.orders\n    on_mismatch: " + onMismatch + "\n    slice: {column: store_id, value: \"${origin.store_id}\", type: \"VARCHAR(10)\"}\nindexes:\n" + string.Concat(indexes.Select(i => $"  - {{name: ix_orders_{i}, columns: [{i}]}}\n"));
            Write("models/warehouse/orders.yml", Variant("fail", "order_id"));
            Ok(Cli("project", "compile"), "render --write");
            var skew = Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(skew, "plan with a drifted origin");
            var skewApply = Cli("connection", "deploy", "--apply-plan", PlanOf(skew.Out));
            Assert.NotEqual(0, skewApply.Exit);
            Assert.True(skewApply.Err.Contains("DDB-230"), skewApply.Err + skewApply.Out);
            Assert.Contains("`pos.orders` on `store_18` differs from its declaration: column `total` is gone", skewApply.Err);
            Assert.DoesNotContain("store_17` differs", skewApply.Err);
            Assert.Equal(["1|018|11.00", "2|017|20.00", "3|018|33.00", "4|017|40.00", "6|017|60.00", "9|999|90.00"], await warehouse.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) + '|' + store_id + '|' + CAST(total AS VARCHAR(20)) FROM warehouse.orders"));   // store 17 loaded before store 18 was reached

            // with on_mismatch: skip the other origin still loads, and the skipped one's rows stay as they were
            Write("models/warehouse/orders.yml", Variant("skip", "order_id", "store_id"));
            Ok(Cli("project", "compile"), "render --write");
            await store17.ExecAsync("INSERT INTO pos.orders VALUES (5, 50.00)");
            var skipped = Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(skipped, "plan with a drifted origin that may be skipped");
            Assert.Equal(2, Regex.Matches(File.ReadAllText(PlanOf(skipped.Out)), "type: transfer").Count);       // both origins are in the plan: the choice is made when the origin's turn comes
            var skippedApply = Cli("connection", "deploy", "--apply-plan", PlanOf(skipped.Out));
            Ok(skippedApply, "apply with a skipped origin");
            Assert.Contains("left out of this apply", skippedApply.Err);
            Assert.Contains("skipped", skippedApply.Out);
            Assert.Equal(["1|018|11.00", "2|017|20.00", "3|018|33.00", "4|017|40.00", "5|017|50.00", "6|017|60.00", "9|999|90.00"], await warehouse.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) + '|' + store_id + '|' + CAST(total AS VARCHAR(20)) FROM warehouse.orders"));

            // an origin that cannot be reached does not hold back the one before it: store 17 loads, the apply stops at store 18 (no read login)
            await store17.ExecAsync("INSERT INTO pos.orders VALUES (7, 70.00)");
            Write("models/warehouse/orders.yml", Variant("skip", "order_id", "store_id", "total"));
            Ok(Cli("project", "compile"), "render --write");
            var down = Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(down, "plan before an origin goes away");
            var o = new StringWriter(); var e = new StringWriter();
            var downExit = CliApp.Run(["connection", "deploy", "--apply-plan", PlanOf(down.Out), "--project", dir], o, e, environment: v => v == "DBDATABUILD_STORE_18_READ" ? null : Env(v));
            Assert.NotEqual(0, downExit);
            Assert.Contains("store_18", o.ToString() + e.ToString());
            Assert.Contains("7|017|70.00", await warehouse.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) + '|' + store_id + '|' + CAST(total AS VARCHAR(20)) FROM warehouse.orders"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task An_incremental_copy_reads_only_what_changed_since_the_newest_row_it_holds(string origin, string destination)
    {
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync(); await to.StartAsync();
        await using var _f = from; await using var _t = to;
        var pair = new Pair(from, to, Path.Combine(Path.GetTempPath(), "ddb-incr-" + Guid.NewGuid().ToString("N")));
        try
        {
            pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\n" + Sensitive(destination));
            pair.Write("models/src/events.yml", $"name: src.events\nkind: mapped\nconnections=: [{origin}]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT, nullable: false}}\n  - {{name: note, type: \"VARCHAR(40)\"}}\n  - {{name: updated_at, type: TIMESTAMP, nullable: false}}\n");
            pair.Write("models/dst/events.yml", "name: dst.events\nkind:\n  copy:\n    from: src.events\n    unique_key: [id]\n    watermark: {column: updated_at, lookback: 1 hour}\nloads:\n  default: {default: true, strategy: delete_insert_by_key}\n  full: {strategy: full_replace}\n");
            await from.ExecAsync(origin == "postgres" ? "CREATE SCHEMA src" : "EXEC('CREATE SCHEMA src')");
            var q = from.QuoteIdent;
            await from.ExecAsync($"CREATE TABLE src.events ({q("id")} BIGINT NOT NULL, {q("note")} {from.ColumnType("VARCHAR(40)")}, {q("updated_at")} {from.ColumnType("TIMESTAMP")} NOT NULL)");
            await from.ExecAsync("INSERT INTO src.events VALUES (1, N'one', '2024-01-01 10:00:00'), (2, N'two', '2024-01-01 11:00:00'), (3, N'three', '2024-01-01 12:00:00')".Replace("N'", origin == "postgres" ? "'" : "N'"));

            Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "render");
            async Task<string> Applied(string what)
            {
                var plan = pair.Cli("connection", "deploy", destination, "--write-plan");
                Ok(plan, what + " plan");
                var file = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
                var text = File.ReadAllText(file);
                Ok(pair.Cli("connection", "deploy", "--apply-plan", file), what + " apply");
                return text;
            }
            async Task<List<string>> Dest() => await to.RowsAsync($"SELECT CAST({to.QuoteIdent("id")} AS VARCHAR(10)) + '|' + {to.QuoteIdent("note")} FROM dst.events".Replace(" + '|' + ", destination == "postgres" ? " || '|' || " : " + '|' + ").Replace("VARCHAR(10)", destination == "postgres" ? "VARCHAR(10)" : "NVARCHAR(10)"));

            var first = await Applied("first");
            Assert.DoesNotContain("@watermark", first);                                                      // nothing in the destination yet: everything is read
            Assert.Equal(["1|one", "2|two", "3|three"], await Dest());

            // a row changes (within the lookback of the newest one), one is added, one is deleted at the origin
            await from.ExecAsync("UPDATE src.events SET note = 'three, edited', updated_at = '2024-01-01 12:30:00' WHERE id = 3");
            await from.ExecAsync("INSERT INTO src.events VALUES (4, 'four', '2024-01-01 13:00:00')");
            await from.ExecAsync("UPDATE src.events SET note = 'one, edited' WHERE id = 1");                 // old: its updated_at did not move, so it is not read again
            var second = await Applied("second");
            Assert.Contains("@watermark", second);
            Assert.Contains("value: \"2024-01-01 11:00:00", second);                                          // the newest row held (12:00) less the lookback (1 hour)
            Assert.Equal(["1|one", "2|two", "3|three, edited", "4|four"], await Dest());                     // read: 2 (11:00 is at the bound), 3, 4. Not read: 1

            // the copy's `full` load reads the origin from the start and replaces everything: row 1, whose updated_at never moved, comes through now, and row 2, deleted at the origin, goes
            await from.ExecAsync("DELETE FROM src.events WHERE id = 2");
            var refreshed = pair.Cli("connection", "deploy", destination, "--write-plan", "--op", "dst.events=full");
            Ok(refreshed, "full load plan");
            var refreshFile = Path.Combine(pair.Dir, Regex.Match(refreshed.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            Assert.DoesNotContain("@watermark", File.ReadAllText(refreshFile));
            Ok(pair.Cli("connection", "deploy", "--apply-plan", refreshFile), "full load apply");
            Assert.Equal(["1|one, edited", "3|three, edited", "4|four"], await Dest());
            Assert.NotEqual(0, pair.Cli("connection", "deploy", destination, "--write-plan", "--op", "no.such_model=full").Exit);    // not a model of the project
            Assert.NotEqual(0, pair.Cli("connection", "deploy", destination, "--write-plan", "--full-refresh", "dst.events").Exit);  // the flag is gone: a person picks the load

            // `report` says which origin copied well last
            var report = pair.Cli("connection", "monitor", destination);
            Ok(report, "report");
            Assert.Contains("Copy origins", report.Out);
            Assert.Matches($@"dst\.events\s+{origin}\s+\d{{4}}-\d\d-\d\d", report.Out);
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_copy_of_a_model_built_on_the_origin_is_checked_against_that_models_columns()
    {
        // two connections of one engine, each in its own database: the system that builds a model, and the warehouse that copies it
        var server = EngineEnv.Require("sqlserver");
        await server.StartAsync();
        await using var _ = server;
        var warehouseDb = "ddb_wh_" + Guid.NewGuid().ToString("N")[..8];
        var warehouse = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = warehouseDb }.ConnectionString;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-built-origin-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v is "DBDATABUILD_ORIGIN_READ" or "DBDATABUILD_ORIGIN_WRITE" ? server.ConnectionString : v is "DBDATABUILD_WH_READ" or "DBDATABUILD_WH_WRITE" ? warehouse : null;
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
        try
        {
            await server.ExecAsync($"CREATE DATABASE [{warehouseDb}]");
            await server.ExecAsync("IF SCHEMA_ID('src') IS NULL EXEC('CREATE SCHEMA src')");
            await server.ExecAsync("DROP TABLE IF EXISTS src.base_t");
            await server.ExecAsync("DROP TABLE IF EXISTS marts.fct");
            await server.ExecAsync("CREATE TABLE src.base_t (id bigint NOT NULL, name varchar(20) NOT NULL)");
            await server.ExecAsync("INSERT INTO src.base_t VALUES (1, 'a'), (2, 'b')");
            Write("dbdatabuild.yml", "defaults: {connections: [wh]}\ntracking: none\nconnections:\n  origin: {engine: sqlserver}\n  wh: {engine: sqlserver}\n");
            Write("models/src/base_t.yml", "name: src.base_t\nkind: mapped\nconnections=: [origin]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: name, type: \"VARCHAR(20)\", nullable: false}\n");
            Write("models/marts/fct.yml", "name: marts.fct\nkind: full\nconnections=: [origin]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: name, type: \"VARCHAR(20)\", nullable: false}\n");
            Write("models/marts/fct.sql", "SELECT id, name FROM src.base_t\n");
            Write("models/dst/fct.yml", "name: dst.fct\nkind:\n  copy:\n    from: marts.fct\n");
            Ok(Cli("project", "compile"), "render");

            // the model is not built on its connection yet: the copy is planned, with a note that the origin was not checked
            var first = Cli("connection", "deploy", "wh", "--write-plan");
            Ok(first, "first plan");
            var firstApply = Cli("connection", "deploy", "--apply-plan", PlanOf(first.Out));
            Assert.Contains("is not built on `origin` yet", firstApply.Err + firstApply.Out);                   // the origin is looked at when its own transfer's turn comes
            Assert.NotEqual(0, firstApply.Exit);                                                                  // and there is no table to read yet

            var build = Cli("connection", "deploy", "origin", "--write-plan");
            Ok(build, "plan of the origin");
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(build.Out)), "apply on the origin");
            var second = Cli("connection", "deploy", "wh", "--write-plan");
            Ok(second, "second plan");
            Assert.DoesNotContain("is not built on", second.Err + second.Out);
            Assert.DoesNotContain("DDB-230", second.Err + second.Out);
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(second.Out)), "apply the copy");
            Assert.Equal(["1|a", "2|b"], await Rows(warehouse, "SELECT CAST(id AS VARCHAR(10)) + '|' + name FROM dst.fct ORDER BY 1"));

            // the table on the origin is changed outside the tool: the copy is stopped before it reads a table that no longer has what the model declares
            await server.ExecAsync("ALTER TABLE marts.fct DROP COLUMN name");
            var third = Cli("connection", "deploy", "wh", "--write-plan");
            Ok(third, "plan against a changed origin");
            var thirdApply = Cli("connection", "deploy", "--apply-plan", PlanOf(third.Out));
            Assert.NotEqual(0, thirdApply.Exit);
            Assert.Contains("DDB-230", thirdApply.Err + thirdApply.Out);
            Assert.Contains("column `name` is gone", thirdApply.Err + thirdApply.Out);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { Microsoft.Data.SqlClient.SqlConnection.ClearAllPools(); await server.ExecAsync($"ALTER DATABASE [{warehouseDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{warehouseDb}]"); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Types the main copy test does not cover (binary, uuid, time of day, timestamp with a zone), checked by the tool itself: after the copy, `diff --against-connection` of the origin table against the destination table
    /// must find them identical (the digests are computed in each engine, so this is a comparison of what each engine holds, not of what the transfer meant to write).
    /// </summary>
    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task Binary_uuid_time_and_zoned_timestamp_values_copy_exactly_and_the_diff_agrees(string origin, string destination)
    {
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync(); await to.StartAsync();
        await using var _f = from; await using var _t = to;
        var pair = new Pair(from, to, Path.Combine(Path.GetTempPath(), "ddb-copy-types-" + Guid.NewGuid().ToString("N")));
        try
        {
            pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\n" + Sensitive(destination));
            pair.Write("models/src/typed.yml", $"name: src.typed\nkind: mapped\nconnections=: [{origin}]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT, nullable: false}}\n  - {{name: blob, type: BLOB}}\n  - {{name: uid, type: UUID}}\n  - {{name: tm, type: TIME}}\n  - {{name: tz, type: TIMESTAMP WITH TIME ZONE}}\n");
            pair.Write("models/dst/typed.yml", "name: dst.typed\nkind: {copy: {from: src.typed}}\n");
            var pg = origin == "postgres";
            await from.ExecAsync(pg ? "CREATE SCHEMA src" : "EXEC('CREATE SCHEMA src')");
            await from.ExecAsync(pg
                ? "CREATE TABLE src.typed (id BIGINT NOT NULL, blob BYTEA, uid UUID, tm TIME(6), tz TIMESTAMPTZ(6))"
                : "CREATE TABLE src.typed (id BIGINT NOT NULL, blob VARBINARY(MAX), uid UNIQUEIDENTIFIER, tm TIME(6), tz DATETIMEOFFSET(6))");
            await from.ExecAsync(pg
                ? "INSERT INTO src.typed VALUES (1, '\\x00ff10'::bytea, '0e984725-c51c-4bf4-9960-e1c80e27aba0', '13:14:15.123456', '2024-03-10 08:30:00.123456+02'), (2, ''::bytea, 'ffffffff-ffff-ffff-ffff-ffffffffffff', '00:00:00', '2024-01-01 00:00:00+00'), (3, NULL, NULL, NULL, NULL), (4, decode(repeat('ab', 100000), 'hex'), '00000000-0000-0000-0000-000000000000', '23:59:59.999999', '9999-12-31 23:59:59.999999+00')"
                : "INSERT INTO src.typed VALUES (1, 0x00FF10, '0e984725-c51c-4bf4-9960-e1c80e27aba0', '13:14:15.123456', '2024-03-10 08:30:00.123456 +02:00'), (2, 0x, 'ffffffff-ffff-ffff-ffff-ffffffffffff', '00:00:00', '2024-01-01 00:00:00 +00:00'), (3, NULL, NULL, NULL, NULL), (4, CAST(REPLICATE(CAST('AB' AS VARCHAR(MAX)), 100000) AS VARBINARY(MAX)), '00000000-0000-0000-0000-000000000000', '23:59:59.999999', '9999-12-31 23:59:59.999999 +00:00')");
            Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "render");
            var plan = pair.Cli("connection", "deploy", destination, "--write-plan"); Ok(plan, "plan");
            var planFile = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            Ok(pair.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
            Assert.Equal("4", (await to.RowsAsync("SELECT COUNT(*) FROM dst.typed")).Single());
            // the same table on the two connections: src.typed on the origin, dst.typed on the destination
            var diff = pair.Cli("connection", "compare", origin, "src.typed", "--against-connection", destination, "--against", "dst.typed", "--key", "id");
            Assert.True(diff.Exit == 0, diff.Out + diff.Err);
            Assert.Contains("The tables are identical", diff.Out);
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task Text_with_edge_values_copies_exactly_and_the_diff_agrees(string origin, string destination)
    {
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync(); await to.StartAsync();
        await using var _f = from; await using var _t = to;
        var pair = new Pair(from, to, Path.Combine(Path.GetTempPath(), "ddb-copy-text-" + Guid.NewGuid().ToString("N")));
        try
        {
            pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\n" + Sensitive(destination));
            pair.Write("models/src/words.yml", $"name: src.words\nkind: mapped\nconnections=: [{origin}]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT, nullable: false}}\n  - {{name: w, type: \"VARCHAR(4000)\"}}\n  - {{name: u, type: VARCHAR}}\n");
            pair.Write("models/dst/words.yml", "name: dst.words\nkind: {copy: {from: src.words}}\n");
            var pg = origin == "postgres";
            await from.ExecAsync(pg ? "CREATE SCHEMA src" : "EXEC('CREATE SCHEMA src')");
            await from.ExecAsync(pg ? "CREATE TABLE src.words (id BIGINT NOT NULL, w VARCHAR(4000), u TEXT)" : "CREATE TABLE src.words (id BIGINT NOT NULL, w NVARCHAR(4000), u NVARCHAR(MAX))");
            // each value is built from code points, so neither the shell nor the test file's encoding can change it
            string[] values =
            [
                "a  ",                          // trailing spaces
                "  a",                          // leading spaces
                "",                             // empty
                "tab\there",                    // a tab
                "cr\r\nlf\nlone",              // line endings
                "e\u0301 vs \u00e9",            // combining accent next to the composed one
                "\ud83d\ude00\ud83c\udde9\ud83c\uddea",   // an emoji and a flag (surrogate pairs)
                "\u05e9\u05dc\u05d5\u05dd \u0645\u0631\u062d\u0628\u0627",      // right-to-left
                "\u00df \u0130 \u03c2 \u212b",   // sharp s, dotted I, final sigma, angstrom
                "\u200b\u00a0\u2028",          // zero-width space, no-break space, line separator
                "'quoted' \"double\" \\ back",  // quotes and a backslash
                new string('x', 3999),          // long
            ];
            string Literal(string v) => (pg ? "'" : "N'") + v.Replace("'", "''") + "'";
            var rows = values.Select((v, k) => $"({k + 1}, {Literal(v)}, {Literal(v)})").Append("(100, NULL, NULL)");
            await from.ExecAsync("INSERT INTO src.words VALUES " + string.Join(", ", rows));
            Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "render");
            var plan = pair.Cli("connection", "deploy", destination, "--write-plan"); Ok(plan, "plan");
            var planFile = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            Ok(pair.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
            Assert.Equal((values.Length + 1).ToString(), (await to.RowsAsync("SELECT COUNT(*) FROM dst.words")).Single());
            var diff = pair.Cli("connection", "compare", origin, "src.words", "--against-connection", destination, "--against", "dst.words", "--key", "id");
            Assert.True(diff.Exit == 0, diff.Out + diff.Err);
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }
}
