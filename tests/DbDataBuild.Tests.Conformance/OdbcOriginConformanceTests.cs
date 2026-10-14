using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Npgsql;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Read-only origins, step 3 (docs/research/read-origins.md): an ODBC source is the origin of a copy into the two real engines, through the command surface. The source is PostgreSQL 17 reached through psqlodbc
/// (unixODBC and the driver are the machine's: scripts/fetch-psqlodbc.sh, or `apt install unixodbc odbc-postgresql`; without them every test here is skipped, with the reason). Rows arrive intact, an incremental
/// refresh binds the watermark by position, a driver that is not installed fails the copy as `failed: origin` in words that quote nothing, a `source` resolver runs at the origin, a native query binds a value used
/// twice, and `project import` and the structure check read the catalog through `information_schema`.
/// </summary>
[Trait("Group", "apply")]
public class OdbcOriginConformanceTests
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

    /// <summary>The driver of the test machine: the file of psqlodbc, or the name of an installed one. Skips (visibly) when there is none, or when the driver manager cannot load it.</summary>
    private static string RequireDriver()
    {
        var driver = EngineEnv.Get("DBDATABUILD_TEST_ODBC_PG_DRIVER");
        Skip.If(driver == null, "Set DBDATABUILD_TEST_ODBC_PG_DRIVER to the psqlodbc driver (run scripts/fetch-psqlodbc.sh, then eval \"$(scripts/test-engines.sh env)\"; or install unixodbc and odbc-postgresql). The ODBC origin tests need a driver manager and a driver on this machine.");
        return driver!;
    }

    private static string OdbcValue(string driver, Engine origin, string extra = "")
    {
        var b = new NpgsqlConnectionStringBuilder(origin.ConnectionString);
        return $"Driver={{{driver}}};Server={b.Host};Port={b.Port};Database={b.Database};Uid={b.Username};Pwd={b.Password};{extra}";
    }

    private sealed class Run(Engine destination, Engine origin, string driver, string name, string dir) : IDisposable
    {
        public Engine Destination => destination;
        public Engine Origin => origin;
        public string Name => name;
        public string Dir => dir;
        public string? LegacyOverride { get; set; }
        public string Legacy => LegacyOverride ?? OdbcValue(driver, origin);

        public string? Env(string v) =>
            v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? destination.ConnectionString
            : v == "DBDATABUILD_LEGACY_READ" ? Legacy : null;

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

        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static string Sensitive(string name) => name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "";

    private static async Task<Run> SetUp(string destination, string extraConfig = "", string legacyEngine = "readonly odbc")
    {
        var driver = RequireDriver();
        var to = EngineEnv.Require(destination);
        await to.StartAsync();
        var from = new PostgresEngine();
        await from.StartAsync();
        var run = new Run(to, from, driver, destination, Path.Combine(Path.GetTempPath(), "ddb-odbc-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(run.Dir);
        run.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\nconnections:\n  legacy: {{ engine: {legacyEngine} }}\n{extraConfig}" + Sensitive(destination));
        // the driver manager and the driver have to work before anything is asked of the tool
        try { await from.RowsAsync("SELECT 1"); await using var probe = await ReadSession.OpenAsync(LoginSettings.FromEnvironment("legacy", "odbc", Login.Read, v => v == "DBDATABUILD_LEGACY_READ" ? run.Legacy : null).Settings!); }
        catch (Exception ex) when (DriverFailure.Prerequisite(ex) is { } why) { await from.DisposeAsync(); await to.DisposeAsync(); Skip.If(true, $"The ODBC driver manager or the psqlodbc driver cannot be used on this machine: {why}."); }
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

    /// <summary>Every row as text that does not depend on the engine's own formatting, the same on all engines.</summary>
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

    private const string ItemsDdl =
        "CREATE SCHEMA src; CREATE TABLE src.items (id BIGINT NOT NULL PRIMARY KEY, small SMALLINT, n INTEGER, ratio DOUBLE PRECISION, flag BOOLEAN, born DATE, seen TIMESTAMP(6), price NUMERIC(18,3), big NUMERIC(28,6), code VARCHAR(40), notes TEXT)";

    private const string ItemsRows =
        "INSERT INTO src.items VALUES " +
        "(1, 7, 100000, 3.5, true, DATE '2024-02-29', TIMESTAMP '2024-02-29 13:14:15.123456', 12345.678, 12345678901234567890.123456, 'café ☕', 'long text'), " +
        "(2, -32768, -2147483648, -0.25, false, DATE '0001-01-01', TIMESTAMP '1999-12-31 23:59:59.999999', -0.001, -0.000001, '', ''), " +
        "(3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL), " +
        "(4, 32767, 2147483647, 1024.5, true, DATE '9999-12-31', TIMESTAMP '2000-01-01 00:00:00', 0, 0, 'it''s \"quoted\"', 'line1' || chr(10) || 'line2')";

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task The_rows_of_an_odbc_source_arrive_intact_in_the_engine_and_the_next_deploy_loads_again(string destination)
    {
        using var run = await SetUp(destination);
        await using var to = run.Destination;
        await using var from = run.Origin;
        run.Write("models/src/items.yml", $"name: src.items\nkind: mapped\nconnections=: [legacy]\ngrain: [id]\n{Columns}");
        run.Write("models/dst/items.yml", "name: dst.items\nkind: {copy: {from: src.items}}\nindexes:\n  - {name: ix_items_code, columns: [code]}\n");
        await from.ExecAsync(ItemsDdl);
        await from.ExecAsync(ItemsRows);

        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var inspect = run.Cli("connection", "inspect", "legacy");
        Ok(inspect, "inspect the origin");
        Assert.Contains("ODBC", inspect.Out);
        Assert.DoesNotContain("ddbconf", inspect.Out + inspect.Err);                                                  // the login's value is never printed
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        var planFile = PlanOf(run, plan.Out);
        var text = File.ReadAllText(planFile);
        Assert.Contains("origin: \"legacy\"", text);
        Assert.Contains("SELECT \\\"id\\\", \\\"small\\\"", text);                                                     // the ansi quoting, in the plan
        Assert.DoesNotContain("ddbconf", text);
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile, "--dry-run"), "dry run");
        Assert.Empty(await to.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_schema = 'dst'"));

        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        var copied = await ComparableAsync(to, "dst.items");
        Assert.Equal(string.Join("\n", await ComparableAsync(from, "src.items")), string.Join("\n", copied));
        Assert.Equal(4, copied.Count);

        await from.ExecAsync("INSERT INTO src.items (id) VALUES (5)");
        var again = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(again, "second plan");
        Ok(run.Cli("connection", "deploy", "--apply-plan", PlanOf(run, again.Out)), "second apply");
        Assert.Equal(5, (await ComparableAsync(to, "dst.items")).Count);
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task An_incremental_refresh_binds_the_watermark_by_position_and_a_missing_odbc_driver_fails_the_copy_as_origin(string destination)
    {
        using var run = await SetUp(destination, "refresh:\n  plans:\n    hourly: { on_failure: continue }\n");
        await using var to = run.Destination;
        await using var from = run.Origin;
        const string events = "columns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: day, type: DATE, nullable: false}\n  - {name: v, type: \"VARCHAR(20)\"}\n";
        run.Write("models/src/events.yml", $"name: src.events\nkind: mapped\nconnections=: [legacy]\ngrain: [id]\n{events}");
        run.Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events, unique_key: [id], watermark: {column: day, lookback: 1 days}}}\nrefresh: { hourly: default }\n");
        run.Write("models/dst/summary.yml", "name: dst.summary\nkind: full\ncolumns:\n  - {name: day, type: DATE, nullable: false}\n  - {name: n, type: BIGINT}\nrefresh: { hourly: default }\n");
        run.Write("models/dst/summary.sql", "SELECT day, COUNT(*) AS n FROM dst.events GROUP BY day\n");
        await from.ExecAsync("CREATE SCHEMA src; CREATE TABLE src.events (id BIGINT NOT NULL, day DATE NOT NULL, v VARCHAR(20)); INSERT INTO src.events VALUES (1, '2026-03-01', 'a'), (2, '2026-03-02', 'b'), (3, '2026-03-03', 'c')");
        Deploy(run);
        async Task<List<string>> Rows() => await to.RowsAsync(destination == "postgres" ? "SELECT CAST(id AS VARCHAR(10)) || ':' || v FROM dst.events ORDER BY id" : "SELECT CAST(id AS VARCHAR(10)) + ':' + v FROM dst.events ORDER BY id");
        Assert.Equal(["1:a", "2:b", "3:c"], await Rows());

        var loads = File.ReadAllText(Path.Combine(run.Dir, "rendered", destination, "loads.yml"));
        Assert.Contains("engine: \"odbc\"", loads);
        Assert.Contains("WHERE \\\"day\\\" >= ?", loads);                                                      // the bound is a positional value, and the plan says which
        Assert.Contains("parameter_order:", loads);

        await from.ExecAsync("INSERT INTO src.events VALUES (4, '2026-03-05', 'd'); UPDATE src.events SET v = 'c2' WHERE id = 3");
        Ok(run.Cli("connection", "refresh", destination, "hourly", "--dry-run"), "dry run");
        Assert.Equal(["1:a", "2:b", "3:c"], await Rows());
        var refresh = run.Cli("connection", "refresh", destination, "hourly", "--format", "json");
        Ok(refresh, "refresh");
        Assert.Equal("completed", (string)System.Text.Json.Nodes.JsonNode.Parse(refresh.Out)!["data"]!["outcome"]!);
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await Rows());                                              // the watermark moved the read: row 1 and 2 were not re-read, row 3 and the new row were
        Assert.Equal(["4"], await to.RowsAsync("SELECT CAST(SUM(n) AS VARCHAR(10)) FROM dst.summary"));

        // a driver that is not installed: the copy fails with the reason, what reads it is skipped, the table keeps what it had; nothing the login said is repeated
        await from.ExecAsync("INSERT INTO src.events VALUES (5, '2026-03-06', 'e')");
        run.LegacyOverride = "Driver={No Such ODBC Driver};Server=127.0.0.1;Uid=secretuser;Pwd=secretpassword";
        var failed = run.Cli("connection", "refresh", destination, "hourly", "--format", "json");
        Assert.Equal(1, failed.Exit);
        var results = System.Text.Json.Nodes.JsonNode.Parse(failed.Out)!["data"]!["results"]!.AsArray().ToDictionary(r => (string)r!["model"]!, r => r!);
        Assert.Equal("failed: origin", (string)results["dst.events"]["status"]!);
        Assert.Equal("skipped: upstream failed", (string)results["dst.summary"]["status"]!);
        var detail = results["dst.events"].ToJsonString();
        Assert.True(detail.Contains("ODBC driver"), detail);
        Assert.DoesNotContain("secret", failed.Out + failed.Err);
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await Rows());
        run.LegacyOverride = null;
        Ok(run.Cli("connection", "refresh", destination, "hourly", "--only-failed"), "only failed");
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d", "5:e"], await Rows());

        // inspect says which prerequisite is missing, in the same words
        run.LegacyOverride = "Driver={No Such ODBC Driver};Server=127.0.0.1;Uid=secretuser;Pwd=secretpassword";
        var inspect = run.Cli("connection", "inspect", "legacy");
        Assert.Contains("the ODBC driver named in the login is not installed", inspect.Out);
        Assert.DoesNotContain("secretpassword", inspect.Out + inspect.Err);
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task A_source_resolver_runs_at_the_odbc_origin_and_its_value_bounds_the_range_the_copy_loads(string destination)
    {
        using var run = await SetUp(destination);
        await using var to = run.Destination;
        await using var from = run.Origin;
        run.Write("models/src/events.yml", "name: src.events\nkind: mapped\nconnections=: [legacy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: day, type: DATE, nullable: false}\n");
        run.Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events}}\n" +
            "loads:\n  window:\n    strategy: delete_insert_by_range\n    column: day\n    params: {start: DATE, end: DATE}\n    inputs:\n" +
            "      start: {resolver: {source: \"SELECT min(day) FROM src.events WHERE day >= DATE '2026-03-01'\"}}\n" +
            "      end: {resolver: {expression: \"DATE '2026-03-04'\"}}\n");
        await from.ExecAsync("CREATE SCHEMA src; CREATE TABLE src.events (id BIGINT NOT NULL, day DATE NOT NULL); INSERT INTO src.events SELECT i + 1, DATE '2026-02-27' + i FROM generate_series(0, 7) AS t(i)");
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
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
    public async Task A_native_query_on_an_odbc_origin_binds_a_value_used_twice_by_position(string destination)
    {
        using var run = await SetUp(destination, "parameters:\n  min_id: { type: BIGINT, value: \"3\" }\n");
        await using var to = run.Destination;
        await using var from = run.Origin;
        run.Write("models/src/picked.yml", "name: src.picked\nkind:\n  native:\n    query: SELECT id, upper(code) AS code FROM src.items WHERE id >= ${project.min_id} AND (id = ${project.min_id} OR id > ${project.min_id})\nconnections=: [legacy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: code, type: \"VARCHAR(40)\"}\n");
        run.Write("models/dst/picked.yml", "name: dst.picked\nkind: {copy: {from: src.picked}}\n");
        await from.ExecAsync("CREATE SCHEMA src; CREATE TABLE src.items (id BIGINT NOT NULL, code VARCHAR(40)); INSERT INTO src.items VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd')");
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        var planFile = PlanOf(run, plan.Out);
        var text = File.ReadAllText(planFile);
        Assert.Contains("id >= ? AND (id = ? OR id > ?)", text);
        Assert.Matches(@"parameter_order:", text);
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        Assert.Equal(["3:C", "4:D"], await to.RowsAsync(destination == "postgres" ? "SELECT CAST(id AS VARCHAR(10)) || ':' || code FROM dst.picked ORDER BY id" : "SELECT CAST(id AS VARCHAR(10)) + ':' + code FROM dst.picked ORDER BY id"));

        // the native text is described by the driver without running it, and a declaration that disagrees is named
        run.Write("models/src/bad.yml", "name: src.bad\nkind:\n  native:\n    query: SELECT id, upper(code) AS code FROM src.items WHERE id >= ${project.min_id}\nconnections=: [legacy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: code, type: INTEGER}\n");
        run.Write("models/dst/bad.yml", "name: dst.bad\nkind: {copy: {from: src.bad}}\n");
        Ok(run.Cli("project", "compile"), "compile again");
        var wrongPlan = run.Cli("connection", "deploy", destination, "--write-plan");
        var wrong = run.Cli("connection", "deploy", "--apply-plan", PlanOf(run, wrongPlan.Out));
        Assert.NotEqual(0, wrong.Exit);
        Assert.True((wrong.Out + wrong.Err).Contains("DDB-230"), wrongPlan.Out + wrongPlan.Err + wrong.Out + wrong.Err);
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task Project_import_reads_the_catalog_through_information_schema_and_a_changed_origin_table_is_named_before_its_transfer(string destination) =>
        await ImportAndCheck(destination, "", "readonly odbc");

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task The_same_import_and_structure_check_work_when_the_catalog_is_read_with_the_drivers_own_functions(string destination) =>
        await ImportAndCheck(destination, "dialects:\n  driver_catalog: { extends: postgres, catalog: odbc }\n", "readonly odbc driver_catalog");

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task A_declared_table_stored_in_the_other_case_is_found_through_identifier_case_and_the_declaration_is_asked_to_match(string destination)
    {
        using var run = await SetUp(destination, "dialects:\n  shouting: { extends: ansi, identifier_case: upper }\n", "readonly odbc shouting");
        await using var to = run.Destination;
        await using var from = run.Origin;
        await from.ExecAsync("CREATE SCHEMA \"SRC\"; CREATE TABLE \"SRC\".\"ITEMS\" (id BIGINT NOT NULL PRIMARY KEY, label VARCHAR(20)); INSERT INTO \"SRC\".\"ITEMS\" VALUES (1, 'a')");
        run.Write("models/src/items.yml", "name: src.items\nkind: mapped\nconnections=: [legacy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: label, type: \"VARCHAR(20)\"}\n");
        run.Write("models/dst/items.yml", "name: dst.items\nkind: {copy: {from: src.items}}\n");
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        var apply = run.Cli("connection", "deploy", "--apply-plan", PlanOf(run, plan.Out));
        Assert.NotEqual(0, apply.Exit);
        Assert.Contains("DDB-230", apply.Out + apply.Err);
        Assert.Contains("stored as `SRC.ITEMS`", apply.Out + apply.Err);
        Assert.Contains("declare it as `SRC.ITEMS`", apply.Out + apply.Err);
    }

    private static async Task ImportAndCheck(string destination, string dialects, string legacyEngine)
    {
        using var run = await SetUp(destination, dialects, legacyEngine);
        await using var to = run.Destination;
        await using var from = run.Origin;
        await from.ExecAsync("CREATE SCHEMA src; CREATE TABLE src.parents (id BIGINT NOT NULL PRIMARY KEY, name VARCHAR(30) NOT NULL); " +
            "CREATE TABLE src.kids (id BIGINT NOT NULL, parent_id BIGINT NOT NULL, born DATE, price NUMERIC(10,2), payload BYTEA, CONSTRAINT pk_kids PRIMARY KEY (id), CONSTRAINT fk_kids_parent FOREIGN KEY (parent_id) REFERENCES src.parents (id)); CREATE INDEX ix_kids_born ON src.kids (born)");
        run.Write("models/src/parents.yml", "name: src.parents\nkind: mapped\nconnections=: [legacy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: name, type: \"VARCHAR(30)\", nullable: false}\n");
        var check = run.Cli("project", "import", "legacy", "src.*", "--check");
        Assert.True(check.Exit is 0 or 1, check.Out + check.Err);
        Assert.Contains("src.parents", check.Out);
        Assert.Contains("src.kids", check.Out);
        var imported = run.Cli("project", "import", "legacy", "src.kids", "--write");
        Ok(imported, "import");
        var descriptor = File.ReadAllText(Directory.GetFiles(Path.Combine(run.Dir, "models"), "kids.yml", SearchOption.AllDirectories).Single());
        Assert.Contains("parent_id", descriptor);
        Assert.Contains("fk_kids_parent", descriptor);
        Assert.Contains("DECIMAL(10, 2)", descriptor);
        if (dialects.Length == 0) Assert.DoesNotContain("payload", descriptor);                                        // bytea has no logical type in the ansi profile's information_schema names, and is said not to be guessed
        else Assert.Contains("BLOB", descriptor);                                                                      // the dialect `postgres` knows the driver's own name for it
        Assert.Contains("ix_kids_born", descriptor);                                                                   // the index, from the driver's statistics whichever way the columns were read

        run.Write("models/dst/parents.yml", "name: dst.parents\nkind: {copy: {from: src.parents}}\n");
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        await from.ExecAsync("INSERT INTO src.parents VALUES (1, 'a')");
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        await from.ExecAsync("ALTER TABLE src.kids DROP CONSTRAINT fk_kids_parent; ALTER TABLE src.parents ALTER COLUMN name TYPE INTEGER USING 0");
        var apply = run.Cli("connection", "deploy", "--apply-plan", PlanOf(run, plan.Out));
        Assert.NotEqual(0, apply.Exit);
        Assert.Contains("DDB-230", apply.Out + apply.Err);
        Assert.Contains("name", apply.Out + apply.Err);
    }

    [SkippableFact]
    public async Task Max_parallel_on_an_odbc_origin_keeps_the_copies_that_run_together_to_that_many_sessions_there()
    {
        using var run = await SetUp("sqlserver", "refresh:\n  plans:\n    fan: {}\n");
        await using var to = run.Destination;
        await using var from = run.Origin;
        await from.ExecAsync("CREATE SCHEMA src");
        foreach (var n in new[] { "1", "2", "3" })
        {
            await from.ExecAsync($"CREATE VIEW src.slow{n} AS SELECT CAST({n} AS BIGINT) AS id FROM pg_sleep(1.5)");          // a view that takes a second and a half to answer
            run.Write($"models/src/slow{n}.yml", $"name: src.slow{n}\nkind: mapped\nconnections=: [legacy]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT}}\n");
            run.Write($"models/dst/copy{n}.yml", $"name: dst.copy{n}\nkind: {{copy: {{from: src.slow{n}}}}}\nrefresh: {{ fan: default }}\n");
        }
        Deploy(run);
        SessionLimits? seen = null;
        RefreshCommand.LimitsObserver = l => seen = l;
        try
        {
            Ok(run.Cli("connection", "refresh", "sqlserver", "fan", "--parallel", "3"), "uncapped");
            Assert.Equal(3, seen!.MostInUse("legacy"));
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")).Replace("legacy: { engine: readonly odbc }", "legacy: { engine: readonly odbc, max_parallel: 1 }"));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var capped = run.Cli("connection", "refresh", "sqlserver", "fan", "--parallel", "3");
            Ok(capped, "capped");
            Assert.Equal(1, seen!.MostInUse("legacy"));
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(4.5), $"three reads of 1.5 s, one at a time, took {clock.Elapsed}");
            Assert.Equal(["1", "2", "3"], await to.RowsAsync("SELECT CAST(id AS VARCHAR(10)) FROM (SELECT id FROM dst.copy1 UNION ALL SELECT id FROM dst.copy2 UNION ALL SELECT id FROM dst.copy3) x ORDER BY id"));
        }
        finally { RefreshCommand.LimitsObserver = null; }
    }

    [SkippableFact]
    public async Task A_statement_that_runs_past_its_timeout_is_stopped_by_the_driver()
    {
        var driver = RequireDriver();
        await using var from = new PostgresEngine();
        await from.StartAsync();
        var login = LoginSettings.FromEnvironment("legacy", "odbc", Login.Read, _ => OdbcValue(driver, from)).Settings!;
        await using var session = await ReadSession.OpenAsync(login);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.QueryFirstRowsAsync("SELECT count(*) FROM pg_sleep(8)", 1, 1));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"the 1 s limit stopped the statement after {clock.Elapsed}");
        Assert.IsType<System.Data.Odbc.OdbcException>(ex);
    }
}
