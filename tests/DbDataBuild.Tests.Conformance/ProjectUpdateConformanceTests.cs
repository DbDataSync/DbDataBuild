using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DuckDB.NET.Data;
using Npgsql;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// `project update` against real origins (docs/research/read-origins.md section 10): a DuckDB file, PostgreSQL through psqlodbc, and a Flight SQL server that answers GetSqlInfo (the in-process one, DuckDB behind it).
/// The dialect is asked, committed and carried into the plans; a server that later reports another dialect fails the origin of a deploy in words that say what differs; a missing login is skipped; `--check` shows
/// the difference and writes nothing; and the mapped models are brought up to date from their tables, keys and indexes included.
/// </summary>
[Trait("Group", "apply")]
public class ProjectUpdateConformanceTests
{
    private static JsonNode Json((int Exit, string Out, string Err) r) => JsonNode.Parse(r.Out)!;

    private static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

    // ---- a DuckDB file ----

    [Fact]
    public void A_duckdb_file_origin_is_compared_with_its_mapped_model_and_check_writes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-update-duck-" + Guid.NewGuid().ToString("N")[..10]);
        var db = Path.Combine(dir, "analytics.duckdb").Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(dir, "models", "src"));
        try
        {
            using (var c = new DuckDBConnection($"Data Source={db}"))
            {
                c.Open();
                foreach (var sql in new[] { "CREATE SCHEMA src", "CREATE TABLE src.parent (id BIGINT NOT NULL PRIMARY KEY)", "CREATE TABLE src.child (id BIGINT NOT NULL PRIMARY KEY, parent_id BIGINT, label VARCHAR, amount DECIMAL(18,2))", "CREATE INDEX ix_child_label ON src.child (label)" })
                { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            }
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\nconnections:\n  analytics: { engine: readonly duckdb }\n");
            File.WriteAllText(Path.Combine(dir, "models", "src", "child.yml"), "name: src.child\nkind: mapped\nconnections=: [analytics]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: label, type: VARCHAR}\n");
            (int, string, string) Cli(params string[] args)
            {
                var o = new StringWriter(); var e = new StringWriter();
                var exit = CliApp.Run([.. args, "--project", dir, "--format", "json"], o, e, environment: v => v == "DBDATABUILD_ANALYTICS_READ" ? db : null);
                return (exit, o.ToString(), e.ToString());
            }
            var before = File.ReadAllText(Path.Combine(dir, "models", "src", "child.yml"));
            var check = Cli("project", "update", "--check");
            Assert.Equal(1, check.Item1);
            Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "models", "src", "child.yml")));
            Ok(Cli("project", "update"), "update");
            var text = File.ReadAllText(Path.Combine(dir, "models", "src", "child.yml"));
            Assert.Contains("grain: [id]", text);                                                       // the primary key is the grain
            Assert.Contains("amount", text);
            Assert.Contains("ix_child_label", text);
            Ok(Cli("project", "update", "--check"), "check after update");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---- PostgreSQL through psqlodbc ----

    [SkippableFact]
    public async Task An_odbc_origin_is_brought_up_to_date_with_its_keys_and_indexes()
    {
        var driver = EngineEnv.Get("DBDATABUILD_TEST_ODBC_PG_DRIVER");
        Skip.If(driver == null, "Set DBDATABUILD_TEST_ODBC_PG_DRIVER to the psqlodbc driver (scripts/fetch-psqlodbc.sh, then eval \"$(scripts/test-engines.sh env)\").");
        var from = new PostgresEngine();
        await from.StartAsync();
        await using var _ = from;
        var b = new NpgsqlConnectionStringBuilder(from.ConnectionString);
        var login = $"Driver={{{driver}}};Server={b.Host};Port={b.Port};Database={b.Database};Uid={b.Username};Pwd={b.Password};";
        try { await using var probe = await ReadSession.OpenAsync(LoginSettings.FromEnvironment("legacy", "odbc", Login.Read, v => v == "DBDATABUILD_LEGACY_READ" ? login : null).Settings!); }
        catch (Exception ex) when (DriverFailure.Prerequisite(ex) is { } why) { Skip.If(true, $"The ODBC driver manager or psqlodbc cannot be used on this machine: {why}"); }
        var dir = Path.Combine(Path.GetTempPath(), "ddb-update-odbc-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path.Combine(dir, "models", "upd"));
        try
        {
            await from.ExecAsync("CREATE SCHEMA upd; CREATE TABLE upd.parent (id BIGINT NOT NULL PRIMARY KEY); CREATE TABLE upd.child (id BIGINT NOT NULL PRIMARY KEY, parent_id BIGINT REFERENCES upd.parent (id), label VARCHAR(40), amount NUMERIC(18,2)); CREATE INDEX ix_child_label ON upd.child (label)");
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\nconnections:\n  legacy: { engine: readonly odbc }\n");
            File.WriteAllText(Path.Combine(dir, "models", "upd", "child.yml"), "name: upd.child\nkind: mapped\nconnections=: [legacy]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: label, type: \"VARCHAR(40)\"}\n");
            (int Exit, string Out, string Err) Cli(params string[] args)
            {
                var o = new StringWriter(); var e = new StringWriter();
                var exit = CliApp.Run([.. args, "--project", dir, "--format", "json"], o, e, environment: v => v == "DBDATABUILD_LEGACY_READ" ? login : null);
                return (exit, o.ToString(), e.ToString());
            }
            Assert.Equal(1, Cli("project", "update", "--check").Exit);
            Ok(Cli("project", "update"), "update");
            var text = File.ReadAllText(Path.Combine(dir, "models", "upd", "child.yml"));
            Assert.Contains("grain: [id]", text);
            Assert.Contains("amount", text);
            Assert.Contains("ix_child_label", text);                                                    // indexes come from the driver's statistics
            Assert.Contains("foreign_keys", text);
            Ok(Cli("project", "update", "--check"), "check after update");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---- Flight SQL: the dialect from the server ----

    private static Dictionary<uint, object> Says(string product, string quote = "\"") => new() { [0] = product, [1] = "1.0", [3] = false, [503] = 1L, [504] = quote, [505] = 1L };

    [SkippableFact]
    public async Task A_flight_sql_servers_dialect_is_committed_carried_into_the_plan_and_a_change_fails_the_origin()
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        server.SayAboutItself(Says("DuckDB"));
        using var run = await FlightRun.SetUpAsync("postgres", server.Login("token"), dialect: "");
        await using var to = run.Destination;
        run.Write("models/src/items.yml", $"name: src.items\nkind: mapped\nconnections=: [proxy]\ngrain: [id]\n{FlightRun.Columns}");
        run.Write("models/dst/items.yml", "name: dst.items\nkind: {copy: {from: src.items}}\n");
        server.Exec("CREATE SCHEMA src", FlightRun.ItemsDdl.Replace("{s}", "src"), FlightRun.ItemsRows.Replace("{s}", "src"));

        var before = run.Cli("project", "compile");
        Ok(before, "compile without a committed dialect");
        Assert.Contains("DDB-458", before.Out + before.Err);                                            // says to run project update
        Assert.Contains("project update", before.Out + before.Err);

        var update = run.Cli("project", "update", "--format", "json");
        Ok(update, "update");
        var row = Json(update)["data"]!["dialects"]!.AsArray().Single()!;
        Assert.True((string)row["status"]! == "new", row.ToJsonString());
        Assert.Equal(("new", "duckdb", "DuckDB"), ((string)row["status"]!, (string)row["dialect"]!, (string)row["server"]!));
        var lockText = File.ReadAllText(Path.Combine(run.Dir, "dialects.lock.yml"));
        Assert.Contains("dialect: duckdb", lockText);
        Ok(run.Cli("project", "update", "--check"), "check right after update");

        Ok(run.Cli("connection", "init", "postgres", "--apply"), "init");
        var after = run.Cli("project", "compile");
        Ok(after, "compile");
        Assert.DoesNotContain("DDB-458", after.Out + after.Err);
        Assert.Contains("dialect: \"duckdb\"", File.ReadAllText(Path.Combine(run.Dir, "rendered", "postgres", "loads.yml")));
        var plan = run.Cli("connection", "deploy", "postgres", "--write-plan");
        Ok(plan, "plan");
        var planFile = FlightRun.PlanOf(run, plan.Out);
        Assert.Contains("dialect: \"duckdb\"", File.ReadAllText(planFile));

        // the server now reports another system: --check names the difference, and the origin of the apply fails instead of using text written for the old one
        server.SayAboutItself(Says("Microsoft SQL Server"));
        var changed = run.Cli("project", "update", "--check", "--format", "json");
        Assert.Equal(1, changed.Exit);
        Assert.Equal("changed", (string)Json(changed)["data"]!["dialects"]!.AsArray().Single()!["status"]!);
        Assert.Contains("DDB-461", changed.Out);
        Assert.Equal(lockText, File.ReadAllText(Path.Combine(run.Dir, "dialects.lock.yml")));         // --check wrote nothing
        var apply = run.Cli("connection", "deploy", "--apply-plan", planFile);
        Assert.NotEqual(0, apply.Exit);
        Assert.Contains("a different dialect than the one committed in dialects.lock.yml", apply.Out + apply.Err);
        Assert.Equal(["0"], await to.RowsAsync("SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM dst.items"));      // the table was made, no row of the old text's read arrived

        // update accepts the change; a login that is missing is a note and the committed file stays
        Ok(run.Cli("project", "update"), "update to the new dialect");
        Assert.Contains("dialect: sqlserver", File.ReadAllText(Path.Combine(run.Dir, "dialects.lock.yml")));
        run.OriginLogin = "";
        var skipped = run.Cli("project", "update", "--format", "json");
        Ok(skipped, "update without the origin's login");
        Assert.Equal("skipped", (string)Json(skipped)["data"]!["dialects"]!.AsArray().Single()!["status"]!);
        Assert.Contains("DBDATABUILD_PROXY_READ", skipped.Out);
    }

    [SkippableFact]
    public async Task A_server_that_does_not_implement_getsqlinfo_is_ansi_and_says_so()
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        using var run = await FlightRun.SetUpAsync("postgres", server.Login("token"), dialect: "");
        await using var to = run.Destination;
        var update = run.Cli("project", "update", "--format", "json");
        Ok(update, "update");
        var row = Json(update)["data"]!["dialects"]!.AsArray().Single()!;
        Assert.Equal("ansi", (string)row["dialect"]!);
        Assert.Contains("does not implement GetSqlInfo", (string)row["note"]!);
    }
}
