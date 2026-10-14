using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>What the Flight SQL origin tests share: a project whose `proxy` connection is `readonly flightsql`, run through the command surface into SQL Server or PostgreSQL.</summary>
internal sealed class FlightRun(Engine destination, string name, string dir, string originLogin) : IDisposable
{
    public Engine Destination => destination;
    public string Name => name;
    public string Dir => dir;
    public string OriginLogin { get; set; } = originLogin;

    public string? Env(string v) =>
        v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? destination.ConnectionString
        : v == "DBDATABUILD_PROXY_READ" ? OriginLogin : null;

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

    public static string Sensitive(string name) => name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "";

    /// <summary>
    /// A DuckDB-backed server reports DuckDB's names for types in `information_schema`; the dialect says which are which (`double` is not one of the standard names). A real proxy's dialect is whatever the owner needs.
    /// </summary>
    public const string Dialects = "dialects:\n  duck: { extends: ansi, types: { double: DOUBLE, \"timestamp with time zone\": \"TIMESTAMP WITH TIME ZONE\" } }\n";

    public static async Task<FlightRun> SetUpAsync(string destination, string originLogin, string extraConfig = "", string dialect = "duck")
    {
        var to = EngineEnv.Require(destination);
        await to.StartAsync();
        var run = new FlightRun(to, destination, Path.Combine(Path.GetTempPath(), "ddb-flight-" + Guid.NewGuid().ToString("N")), originLogin);
        Directory.CreateDirectory(run.Dir);
        run.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\n{Dialects}connections:\n  proxy: {{ engine: readonly flightsql {dialect} }}\n{extraConfig}" + Sensitive(destination));
        return run;
    }

    public static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

    public static string PlanOf(FlightRun run, string output) => Path.Combine(run.Dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);

    public void Deploy()
    {
        Ok(Cli("connection", "init", name, "--apply"), "init");
        Ok(Cli("project", "compile"), "compile");
        var plan = Cli("connection", "deploy", name, "--write-plan");
        Ok(plan, "plan");
        Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(this, plan.Out)), "deploy");
    }

    public const string Columns =
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

    public const string ItemsDdl =
        "CREATE TABLE {s}.items (id BIGINT NOT NULL PRIMARY KEY, small SMALLINT, n INTEGER, ratio DOUBLE, flag BOOLEAN, born DATE, seen TIMESTAMP, price DECIMAL(18,3), big DECIMAL(28,6), code VARCHAR, notes VARCHAR)";

    public const string ItemsRows =
        "INSERT INTO {s}.items VALUES " +
        "(1, 7, 100000, 3.5, true, DATE '2024-02-29', TIMESTAMP '2024-02-29 13:14:15.123456', 12345.678, 12345678901234567890.123456, 'café ☕', 'long text'), " +
        "(2, -32768, -2147483648, -0.25, false, DATE '0001-01-01', TIMESTAMP '1999-12-31 23:59:59.999999', -0.001, -0.000001, '', ''), " +
        "(3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL), " +
        "(4, 32767, 2147483647, 1024.5, true, DATE '9999-12-31', TIMESTAMP '2000-01-01 00:00:00', 0, 0, 'it''s \"quoted\"', 'line1' || chr(10) || 'line2')";

    /// <summary>What the four rows of <see cref="ItemsRows"/> are, as <see cref="ComparableAsync"/> writes a row: the same text on every engine.</summary>
    public static readonly string[] ItemsComparable =
    [
        "1|7|100000|3.5|1|2024-02-29|2024-02-29 13:14:15.123456|12345.678|12345678901234567890.123456|café ☕|long text",
        "2|-32768|-2147483648|-0.25|0|0001-01-01|1999-12-31 23:59:59.999999|-0.001|-0.000001||",
        "3|<null>|<null>|<null>|<null>|<null>|<null>|<null>|<null>|<null>|<null>",
        "4|32767|2147483647|1024.5|1|9999-12-31|2000-01-01 00:00:00.000000|0.000|0.000000|it's \"quoted\"|line1\nline2",
    ];

    /// <summary>Every row as text that does not depend on the engine's own formatting, the same on both engines.</summary>
    public static async Task<List<string>> ComparableAsync(Engine e, string table)
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
}

/// <summary>
/// Read-only origins, step 4 (docs/research/read-origins.md): an Arrow Flight SQL server is the origin of a copy into the two real engines, through the command surface. The server here is the one of this process
/// (<see cref="InProcessFlightSqlServer"/>, DuckDB behind it, typed prepared-statement parameters, a bearer token or a user and password, no metadata calls), so no container is needed. Rows arrive intact, an
/// incremental refresh binds the watermark typed and by position, a server that is not there or a token it refuses fail the copy as `failed: origin` in words that quote nothing, a `source` resolver runs at the
/// origin, a native query binds a value used twice, `project import` reads the catalog through `information_schema`, `max_parallel` caps the sessions and a deadline stops a statement.
/// </summary>
[Trait("Group", "apply")]
public class FlightSqlOriginConformanceTests
{
    public static TheoryData<string> Destinations => new() { "sqlserver", "postgres" };

    private static void Ok((int Exit, string Out, string Err) r, string what) => FlightRun.Ok(r, what);

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task The_rows_of_a_flight_sql_server_arrive_intact_with_a_user_and_password_and_the_next_deploy_loads_again(string destination)
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        using var run = await FlightRun.SetUpAsync(destination, server.Login("password"));
        await using var to = run.Destination;
        run.Write("models/src/items.yml", $"name: src.items\nkind: mapped\nconnections=: [proxy]\ngrain: [id]\n{FlightRun.Columns}");
        run.Write("models/dst/items.yml", "name: dst.items\nkind: {copy: {from: src.items}}\nindexes:\n  - {name: ix_items_code, columns: [code]}\n");
        server.Exec("CREATE SCHEMA src", FlightRun.ItemsDdl.Replace("{s}", "src"), FlightRun.ItemsRows.Replace("{s}", "src"));

        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var inspect = run.Cli("connection", "inspect", "proxy");
        Ok(inspect, "inspect the origin");
        Assert.Contains("Flight SQL server 127.0.0.1:", inspect.Out);
        Assert.Contains("DBDATABUILD_PROXY_READ", inspect.Out);
        Assert.DoesNotContain(InProcessFlightSqlServer.Password, inspect.Out + inspect.Err);                       // the login's value is never printed
        Assert.Contains("basic", string.Join(",", server.Handshakes).ToLowerInvariant());                         // the user and password were traded for a token by the server's handshake

        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        var planFile = FlightRun.PlanOf(run, plan.Out);
        var text = File.ReadAllText(planFile);
        Assert.Contains("origin: \"proxy\"", text);
        Assert.Contains("SELECT \\\"id\\\", \\\"small\\\"", text);                                                 // the dialect's quoting, in the plan
        Assert.DoesNotContain(InProcessFlightSqlServer.Password, text);
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile, "--dry-run"), "dry run");
        Assert.Empty(await to.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_schema = 'dst'"));

        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        var copied = await FlightRun.ComparableAsync(to, "dst.items");
        Assert.Equal(string.Join("\n", FlightRun.ItemsComparable), string.Join("\n", copied));
        Assert.Empty(await to.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_name LIKE 'stg_dst%'"));
        Assert.True(server.OpenPrepared == 0, string.Join(",", server.Actions));                                                                     // no prepared statement is left open at the server

        server.Exec("INSERT INTO src.items (id) VALUES (5)");
        var again = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(again, "second plan");
        Ok(run.Cli("connection", "deploy", "--apply-plan", FlightRun.PlanOf(run, again.Out)), "second apply");
        Assert.Equal(5, (await FlightRun.ComparableAsync(to, "dst.items")).Count);
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task An_incremental_refresh_binds_the_watermark_typed_and_by_position_and_an_unreachable_server_or_a_refused_token_fails_the_copy_as_origin(string destination)
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        using var run = await FlightRun.SetUpAsync(destination, server.Login("token"), "refresh:\n  plans:\n    hourly: { on_failure: continue }\n");
        await using var to = run.Destination;
        const string events = "columns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: day, type: DATE, nullable: false}\n  - {name: v, type: \"VARCHAR(20)\"}\n";
        run.Write("models/src/events.yml", $"name: src.events\nkind: mapped\nconnections=: [proxy]\ngrain: [id]\n{events}");
        run.Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events, unique_key: [id], watermark: {column: day, lookback: 1 days}}}\nrefresh: { hourly: default }\n");
        run.Write("models/dst/summary.yml", "name: dst.summary\nkind: full\ncolumns:\n  - {name: day, type: DATE, nullable: false}\n  - {name: n, type: BIGINT}\nrefresh: { hourly: default }\n");
        run.Write("models/dst/summary.sql", "SELECT day, COUNT(*) AS n FROM dst.events GROUP BY day\n");
        server.Exec("CREATE SCHEMA src", "CREATE TABLE src.events (id BIGINT NOT NULL, day DATE NOT NULL, v VARCHAR)", "INSERT INTO src.events VALUES (1, '2026-03-01', 'a'), (2, '2026-03-02', 'b'), (3, '2026-03-03', 'c')");
        run.Deploy();
        async Task<List<string>> Rows() => await to.RowsAsync(destination == "postgres" ? "SELECT CAST(id AS VARCHAR(10)) || ':' || v FROM dst.events ORDER BY id" : "SELECT CAST(id AS VARCHAR(10)) + ':' + v FROM dst.events ORDER BY id");
        Assert.Equal(["1:a", "2:b", "3:c"], await Rows());

        var loads = File.ReadAllText(Path.Combine(run.Dir, "rendered", destination, "loads.yml"));
        Assert.Contains("engine: \"flightsql\"", loads);
        Assert.Contains("WHERE \\\"day\\\" >= ?", loads);                                                        // the bound is a positional value, and the plan says which
        Assert.Contains("parameter_order:", loads);
        Assert.DoesNotContain(InProcessFlightSqlServer.Token, loads);

        server.Exec("INSERT INTO src.events VALUES (4, '2026-03-05', 'd')", "UPDATE src.events SET v = 'c2' WHERE id = 3");
        Ok(run.Cli("connection", "refresh", destination, "hourly", "--dry-run"), "dry run");
        Assert.Equal(["1:a", "2:b", "3:c"], await Rows());
        server.ClearLog();
        var refresh = run.Cli("connection", "refresh", destination, "hourly", "--format", "json");
        Ok(refresh, "refresh");
        Assert.Equal("completed", (string)System.Text.Json.Nodes.JsonNode.Parse(refresh.Out)!["data"]!["outcome"]!);
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await Rows());                                                // the watermark moved the read: row 1 was not re-read, rows 3 and 4 were
        Assert.Equal(["4"], await to.RowsAsync("SELECT CAST(SUM(n) AS VARCHAR(10)) FROM dst.summary"));
        // what the server was asked: one prepared read, its single parameter a DATE (typed, not text), the lookback applied to the stored watermark
        var read = server.Bound.Single(b => b.Sql.Contains("\"src\".\"events\"", StringComparison.Ordinal));
        Assert.Equal(["date32"], read.Types);
        Assert.Equal([new DateOnly(2026, 3, 2)], read.Values);
        Assert.True(server.OpenPrepared == 0, string.Join(",", server.Actions));

        // a server that is not there: the copy fails with the reason, what reads it is skipped, the table keeps what it had; nothing the login said is repeated
        server.Exec("INSERT INTO src.events VALUES (5, '2026-03-06', 'e')");
        run.OriginLogin = "grpc://127.0.0.1:1;token=never-printed-secret-token";
        var failed = run.Cli("connection", "refresh", destination, "hourly", "--format", "json");
        Assert.Equal(1, failed.Exit);
        var results = System.Text.Json.Nodes.JsonNode.Parse(failed.Out)!["data"]!["results"]!.AsArray().ToDictionary(r => (string)r!["model"]!, r => r!);
        Assert.Equal("failed: origin", (string)results["dst.events"]["status"]!);
        Assert.Equal("skipped: upstream failed", (string)results["dst.summary"]["status"]!);
        var detail = results["dst.events"].ToJsonString();
        Assert.True(detail.Contains("could not be reached"), detail);
        Assert.True(detail.Contains("Unavailable"), detail);
        Assert.DoesNotContain("never-printed", failed.Out + failed.Err);
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await Rows());

        // a token the server refuses: the same status, named by its kind
        run.OriginLogin = $"grpc://127.0.0.1:{server.Port};token=never-printed-secret-token";
        var refused = run.Cli("connection", "refresh", destination, "hourly", "--format", "json");
        Assert.Equal(1, refused.Exit);
        var refusedResults = System.Text.Json.Nodes.JsonNode.Parse(refused.Out)!["data"]!["results"]!.AsArray().ToDictionary(r => (string)r!["model"]!, r => r!);
        Assert.Equal("failed: origin", (string)refusedResults["dst.events"]["status"]!);
        Assert.True(refusedResults["dst.events"].ToJsonString().Contains("refused the credentials"), refusedResults["dst.events"].ToJsonString());
        Assert.DoesNotContain("never-printed", refused.Out + refused.Err);
        Assert.DoesNotContain("must never reach a person", refused.Out + refused.Err);                           // the server's own words are not repeated either
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await Rows());

        // the right login again: only what failed runs
        run.OriginLogin = server.Login("token");
        Ok(run.Cli("connection", "refresh", destination, "hourly", "--only-failed"), "only failed");
        Assert.Equal(["1:a", "2:b", "3:c2", "4:d", "5:e"], await Rows());

        // inspect says the same of an unreachable server and of a bad login value, in words of ours
        run.OriginLogin = "grpc://127.0.0.1:1;token=never-printed-secret-token";
        var inspect = run.Cli("connection", "inspect", "proxy");
        Assert.Contains("the Flight SQL server could not be reached", inspect.Out);
        Assert.DoesNotContain("never-printed", inspect.Out + inspect.Err);
        run.OriginLogin = "this is not a uri, password=never-printed-secret-token";
        var invalid = run.Cli("connection", "inspect", "proxy");
        Assert.Contains("The Flight SQL login", invalid.Out);
        Assert.DoesNotContain("never-printed", invalid.Out + invalid.Err);
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task A_source_resolver_runs_at_the_flight_sql_origin_and_its_value_bounds_the_range_the_copy_loads(string destination)
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        using var run = await FlightRun.SetUpAsync(destination, server.Login("token"));
        await using var to = run.Destination;
        run.Write("models/src/events.yml", "name: src.events\nkind: mapped\nconnections=: [proxy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: day, type: DATE, nullable: false}\n");
        run.Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events}}\n" +
            "loads:\n  window:\n    strategy: delete_insert_by_range\n    column: day\n    params: {start: DATE, end: DATE}\n    inputs:\n" +
            "      start: {resolver: {source: \"SELECT min(day) FROM src.events WHERE day >= DATE '2026-03-01'\"}}\n" +
            "      end: {resolver: {expression: \"DATE '2026-03-04'\"}}\n");
        server.Exec("CREATE SCHEMA src", "CREATE TABLE src.events (id BIGINT NOT NULL, day DATE NOT NULL)", "INSERT INTO src.events SELECT i + 1, DATE '2026-02-27' + CAST(i AS INTEGER) FROM range(0, 8) t(i)");
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", destination, "--write-plan", "--op", "dst.events=window");
        Ok(plan, "plan");
        var planFile = FlightRun.PlanOf(run, plan.Out);
        var text = File.ReadAllText(planFile);
        Assert.Contains("planned_source: \"resolver:source\"", text);
        Assert.Contains("planned_value: \"2026-03-01\"", text);                                                   // the origin's own answer, before anything is applied
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        Assert.Equal(["2026-03-01", "2026-03-02", "2026-03-03"], await to.RowsAsync("SELECT CAST(day AS VARCHAR(10)) FROM dst.events ORDER BY day"));
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task A_native_query_on_a_flight_sql_origin_binds_a_value_used_twice_by_position_and_is_described_by_the_server(string destination)
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        using var run = await FlightRun.SetUpAsync(destination, server.Login("token"), "parameters:\n  min_id: { type: BIGINT, value: \"3\" }\n");
        await using var to = run.Destination;
        run.Write("models/src/picked.yml", "name: src.picked\nkind:\n  native:\n    query: SELECT id, upper(code) AS code FROM src.items WHERE id >= ${project.min_id} AND (id = ${project.min_id} OR id > ${project.min_id})\nconnections=: [proxy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: code, type: VARCHAR}\n");
        run.Write("models/dst/picked.yml", "name: dst.picked\nkind: {copy: {from: src.picked}}\n");
        server.Exec("CREATE SCHEMA src", "CREATE TABLE src.items (id BIGINT NOT NULL, code VARCHAR)", "INSERT INTO src.items VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd')");
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        var planFile = FlightRun.PlanOf(run, plan.Out);
        var text = File.ReadAllText(planFile);
        Assert.Contains("id >= ? AND (id = ? OR id > ?)", text);
        Assert.Matches(@"parameter_order:", text);
        server.ClearLog();
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        Assert.Equal(["3:C", "4:D"], await to.RowsAsync(destination == "postgres" ? "SELECT CAST(id AS VARCHAR(10)) || ':' || code FROM dst.picked ORDER BY id" : "SELECT CAST(id AS VARCHAR(10)) + ':' + code FROM dst.picked ORDER BY id"));
        var read = server.Bound.Single(b => b.Sql.Contains("upper(code)", StringComparison.Ordinal) && !b.Sql.Contains("LIMIT 0", StringComparison.Ordinal));
        Assert.Equal(["int64", "int64", "int64"], read.Types);                                          // a value used three times is three parameters, in the order the text has them
        Assert.Equal([3L, 3L, 3L], read.Values);

        // the native text is described by the server without running it, and a declaration that disagrees is named
        run.Write("models/src/bad.yml", "name: src.bad\nkind:\n  native:\n    query: SELECT id, upper(code) AS code FROM src.items WHERE id >= ${project.min_id}\nconnections=: [proxy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: code, type: INTEGER}\n");
        run.Write("models/dst/bad.yml", "name: dst.bad\nkind: {copy: {from: src.bad}}\n");
        Ok(run.Cli("project", "compile"), "compile again");
        var wrongPlan = run.Cli("connection", "deploy", destination, "--write-plan");
        var wrong = run.Cli("connection", "deploy", "--apply-plan", FlightRun.PlanOf(run, wrongPlan.Out));
        Assert.NotEqual(0, wrong.Exit);
        Assert.True((wrong.Out + wrong.Err).Contains("DDB-230"), wrongPlan.Out + wrongPlan.Err + wrong.Out + wrong.Err);
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task Project_import_reads_the_catalog_through_information_schema_when_the_server_has_no_metadata_calls_and_a_changed_origin_table_is_named(string destination)
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        using var run = await FlightRun.SetUpAsync(destination, server.Login("token"));
        await using var to = run.Destination;
        server.Exec("CREATE SCHEMA src", "CREATE TABLE src.parents (id BIGINT NOT NULL PRIMARY KEY, name VARCHAR NOT NULL)",
            "CREATE TABLE src.kids (id BIGINT NOT NULL PRIMARY KEY, parent_id BIGINT NOT NULL REFERENCES src.parents (id), born DATE, price DECIMAL(10,2), payload BLOB, tags INTEGER[])");
        run.Write("models/src/parents.yml", "name: src.parents\nkind: mapped\nconnections=: [proxy]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: name, type: VARCHAR, nullable: false}\n");
        var check = run.Cli("project", "import", "proxy", "src.*", "--check");
        Assert.True(check.Exit is 0 or 1, check.Out + check.Err);
        Assert.Contains("src.parents", check.Out);
        Assert.Contains("src.kids", check.Out);
        var imported = run.Cli("project", "import", "proxy", "src.kids", "--write");
        Ok(imported, "import");
        var descriptor = File.ReadAllText(Directory.GetFiles(Path.Combine(run.Dir, "models"), "kids.yml", SearchOption.AllDirectories).Single());
        Assert.Contains("parent_id", descriptor);
        Assert.Contains("DECIMAL(10, 2)", descriptor);
        Assert.True(descriptor.Contains("grain"), descriptor);                                                                       // the primary key seeds it
        Assert.DoesNotContain("tags", descriptor);                                                                  // an array has no logical type, and is said not to be guessed

        run.Write("models/dst/parents.yml", "name: dst.parents\nkind: {copy: {from: src.parents}}\n");
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        server.Exec("INSERT INTO src.parents VALUES (1, 'a')");
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        server.Exec("DROP TABLE src.kids", "ALTER TABLE src.parents ALTER COLUMN name TYPE INTEGER USING 0");
        var apply = run.Cli("connection", "deploy", "--apply-plan", FlightRun.PlanOf(run, plan.Out));
        Assert.NotEqual(0, apply.Exit);
        Assert.Contains("DDB-230", apply.Out + apply.Err);
        Assert.Contains("name", apply.Out + apply.Err);
    }

    [SkippableFact]
    public async Task Max_parallel_on_a_flight_sql_origin_keeps_the_copies_that_run_together_to_that_many_sessions_there_and_a_deadline_stops_a_statement()
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        using var run = await FlightRun.SetUpAsync("sqlserver", server.Login("token"), "refresh:\n  plans:\n    fan: {}\n");
        await using var to = run.Destination;
        server.Exec("CREATE SCHEMA src");
        foreach (var n in new[] { "1", "2", "3" })
        {
            server.Exec($"CREATE TABLE src.slow{n} AS SELECT CAST({n} AS BIGINT) AS id");                      // a table named slow*: the server takes a second and a half to answer for it
            run.Write($"models/src/slow{n}.yml", $"name: src.slow{n}\nkind: mapped\nconnections=: [proxy]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT}}\n");
            run.Write($"models/dst/copy{n}.yml", $"name: dst.copy{n}\nkind: {{copy: {{from: src.slow{n}}}}}\nrefresh: {{ fan: default }}\n");
        }
        run.Deploy();
        SessionLimits? seen = null;
        RefreshCommand.LimitsObserver = l => seen = l;
        try
        {
            server.ClearLog();
            Ok(run.Cli("connection", "refresh", "sqlserver", "fan", "--parallel", "3"), "uncapped");
            Assert.Equal(3, seen!.MostInUse("proxy"));
            Assert.True(server.MostConcurrent >= 2, $"the server saw {server.MostConcurrent} statements at once");
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")).Replace("proxy: { engine: readonly flightsql duck }", "proxy: { engine: readonly flightsql duck, max_parallel: 1 }"));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Ok(run.Cli("connection", "refresh", "sqlserver", "fan", "--parallel", "3"), "capped");
            Assert.Equal(1, seen!.MostInUse("proxy"));
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(4.5), $"three reads of 1.5 s, one at a time, took {clock.Elapsed}");
            Assert.Equal(["1", "2", "3"], await to.RowsAsync("SELECT CAST(id AS VARCHAR(10)) FROM (SELECT id FROM dst.copy1 UNION ALL SELECT id FROM dst.copy2 UNION ALL SELECT id FROM dst.copy3) x ORDER BY id"));
        }
        finally { RefreshCommand.LimitsObserver = null; }

        // the call's deadline is the command timeout: a statement the server holds for twenty seconds is stopped after one
        var login = LoginSettings.FromEnvironment("proxy", "flightsql", Login.Read, _ => server.Login("token")).Settings!;
        await using var session = await ReadSession.OpenAsync(login);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.QueryFirstRowsAsync("SELECT 1 /*slow*/", 1, 1));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), $"the 1 s limit stopped the statement after {timer.Elapsed}");
        Assert.Equal(Grpc.Core.StatusCode.DeadlineExceeded, DriverFailure.Rpc(ex)!.StatusCode);
        Assert.Contains("did not answer in time", DriverFailure.Describe(ex));
    }

    [SkippableFact]
    public async Task A_server_that_gives_no_schema_for_a_prepared_statement_is_still_read_and_the_native_text_is_described_by_a_zero_row_read()
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        server.Exec("CREATE TABLE t AS SELECT * FROM (VALUES (1, 'a'), (2, 'b')) v(id, s)");
        var login = LoginSettings.FromEnvironment("proxy", "flightsql", Login.Read, _ => server.Login("token")).Settings!;
        await using var session = await ReadSession.OpenAsync(login);
        var rows = await session.QueryAsync("SELECT id, s FROM t WHERE id >= ? /*noschema*/", [new("w", System.Data.DbType.Int64, 2L)]);
        Assert.Equal([[2, "b"]], rows.Select(r => r.Select(v => v is long l ? (object)(int)l : v).ToArray()).ToArray());
        Assert.True(server.OpenPrepared == 0, string.Join(",", server.Actions));
        // the server will not say the schema of the statement as it is, so the tool asks for no rows of it in the dialect's limit style (ansi: FETCH FIRST 0 ROWS ONLY) and takes the schema of the answer
        server.ClearLog();
        var viaZeroRows = await session.DescribeAsync("SELECT id, s FROM t WHERE id >= ? /*noschema*/", "flightsql", [new("w", System.Data.DbType.Int64, 2L)]);
        Assert.Equal(["id", "s"], viaZeroRows!.Select(c => c.Name).ToArray());
        Assert.Contains(server.Texts, t => t.Contains("AS ddb_described FETCH FIRST 0 ROWS ONLY"));
        Assert.True(server.OpenPrepared == 0, string.Join(",", server.Actions));
        var described = await session.DescribeAsync("SELECT id, s FROM t WHERE id >= ?", "flightsql", [new("w", System.Data.DbType.Int64, 2L)]);
        Assert.Equal(["integer", "character varying"], described!.Select(c => c.Type).ToArray());
        Assert.True(server.OpenPrepared == 0, string.Join(",", server.Actions));
    }
}
