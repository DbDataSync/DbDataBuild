using System.Text.Json;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DuckDB.NET.Data;
using Npgsql;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Read-only origins, step 5 (docs/research/read-origins.md section 8c): the catalog on the new origins, through `project import`. A DuckDB file (no container) gives its indexes and foreign keys; an ODBC source
/// (PostgreSQL 17 through psqlodbc) is read through the driver's own catalog functions (`catalog: odbc`: SQLTables, SQLColumns, SQLPrimaryKeys, SQLForeignKeys, SQLStatistics) and, with the default `ansi` dialect,
/// still gets its indexes from SQLStatistics; a Flight SQL server with no metadata calls says plainly that it has no indexes to read; the zero-row read of each `limit` style is valid on the engine that takes it.
/// </summary>
public class OriginCatalogConformanceTests
{
    private sealed class Project(string dir, Func<string, string?> env) : IDisposable
    {
        public string Dir => dir;

        public (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: env);
            return (exit, o.ToString(), e.ToString());
        }

        public static Project Create(string config, Func<string, string?> env)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ddb-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
            return new Project(dir, env);
        }

        public string Descriptor(string file) => File.ReadAllText(Directory.GetFiles(Path.Combine(dir, "models"), file, SearchOption.AllDirectories).Single());

        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

    // ---- DuckDB: no container ----

    private static void Exec(string file, params string[] sql)
    {
        using var c = new DuckDBConnection($"Data Source={file}");
        c.Open();
        foreach (var s in sql) { using var cmd = c.CreateCommand(); cmd.CommandText = s; cmd.ExecuteNonQuery(); }
    }

    [Fact]
    public void A_duckdb_files_indexes_and_foreign_keys_are_imported_and_an_expression_index_is_said_to_be_left_out()
    {
        var file = Path.Combine(Path.GetTempPath(), "ddb-catalog-" + Guid.NewGuid().ToString("N") + ".duckdb");
        try
        {
            Exec(file, "CREATE SCHEMA sales",
                "CREATE TABLE sales.customers (id BIGINT NOT NULL PRIMARY KEY, name VARCHAR)",
                "CREATE TABLE sales.orders (id BIGINT NOT NULL, cust_id BIGINT NOT NULL, placed DATE, total DECIMAL(10,2), PRIMARY KEY (cust_id, id), FOREIGN KEY (cust_id) REFERENCES sales.customers (id))",
                "CREATE INDEX ix_orders_placed ON sales.orders (placed, total)",
                "CREATE UNIQUE INDEX ux_orders_total ON sales.orders (total)",
                "CREATE INDEX ix_orders_expr ON sales.orders ((total * 2))",
                "CREATE VIEW sales.big_orders AS SELECT * FROM sales.orders WHERE total > 100");
            using var project = Project.Create("connections:\n  analytics: { engine: readonly duckdb }\n", v => v == "DBDATABUILD_ANALYTICS_READ" ? file : null);
            var imported = project.Cli("project", "import", "analytics", "sales.*", "--write");
            Ok(imported, "import");
            var orders = project.Descriptor("orders.yml");
            Assert.Contains("ix_orders_placed", orders);
            Assert.Contains("ux_orders_total", orders);
            Assert.DoesNotContain("ix_orders_expr", orders);
            Assert.Contains("fk_", orders.Replace("foreign_keys", "") + "fk_");                       // a key is there whatever DuckDB calls it
            Assert.Contains("sales.customers", orders);                                              // the schema the key points to
            Assert.Contains("ix_orders_expr", imported.Out);                                         // the one left out is named
            Assert.Contains("expression", imported.Out);
            Assert.Contains("DECIMAL(10, 2)", orders);
            Assert.Contains("big_orders", imported.Out);

            // a second run changes nothing
            Assert.Contains("already match", project.Cli("project", "import", "analytics", "sales.*", "--write").Out);

            var json = project.Cli("project", "import", "analytics", "sales.*", "--format", "json");
            using var doc = JsonDocument.Parse(json.Out);
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(0, data.GetProperty("catalog_notes").GetArrayLength());
            var ordersJson = data.GetProperty("mapped_models").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "sales.orders");
            Assert.Equal(["ix_orders_placed", "ux_orders_total"], ordersJson.GetProperty("indexes").EnumerateArray().Select(i => i.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
            Assert.Equal("sales.customers", ordersJson.GetProperty("foreign_keys")[0].GetProperty("references").GetProperty("table").GetString());
        }
        finally { try { File.Delete(file); File.Delete(file + ".wal"); } catch (IOException) { } }
    }

    [Fact]
    public void DuckDB_refuses_a_foreign_key_across_schemas_so_the_referenced_schema_is_the_keys_own()
    {
        var file = Path.Combine(Path.GetTempPath(), "ddb-catalog-" + Guid.NewGuid().ToString("N") + ".duckdb");
        try
        {
            Exec(file, "CREATE SCHEMA a", "CREATE SCHEMA b", "CREATE TABLE a.p (id BIGINT PRIMARY KEY)");
            // If a later DuckDB allows it, the reader already takes the schema from the constraint text (SourceCatalogReader.DuckDbReferencedSchema) and this test is the place to learn that it changed
            var ex = Assert.ThrowsAny<Exception>(() => Exec(file, "CREATE TABLE b.c (pid BIGINT, FOREIGN KEY (pid) REFERENCES a.p (id))"));
            Assert.Contains("different schemas", ex.Message);
        }
        finally { try { File.Delete(file); File.Delete(file + ".wal"); } catch (IOException) { } }
    }

    // ---- Flight SQL: an in-process server with no metadata calls ----

    [Fact]
    public async Task A_flight_sql_server_with_a_built_in_dialect_is_imported_by_information_schema_and_says_it_has_no_indexes_to_read()
    {
        await using var server = await InProcessFlightSqlServer.StartAsync();
        server.Exec("CREATE SCHEMA src", "CREATE TABLE src.items (id BIGINT NOT NULL PRIMARY KEY, ratio DOUBLE, price DECIMAL(10,2), code VARCHAR, flag BOOLEAN, tiny TINYINT)", "CREATE INDEX ix_items_code ON src.items (code)");
        using var project = Project.Create("connections:\n  proxy: { engine: readonly flightsql duckdb }\n", v => v == "DBDATABUILD_PROXY_READ" ? server.Login("token") : null);
        var imported = project.Cli("project", "import", "proxy", "src.items", "--write");
        Ok(imported, "import");
        var items = project.Descriptor("items.yml");
        Assert.Contains("DOUBLE", items);                                                            // the built-in duckdb dialect names DuckDB's own types
        Assert.Contains("SMALLINT", items);
        Assert.Contains("DECIMAL(10, 2)", items);
        Assert.DoesNotContain("ix_items_code", items);
        Assert.Contains("Flight SQL has no metadata call for indexes", imported.Out);
        var json = project.Cli("project", "import", "proxy", "src.items", "--format", "json");
        using var doc = JsonDocument.Parse(json.Out);
        Assert.Contains(doc.RootElement.GetProperty("data").GetProperty("catalog_notes").EnumerateArray().Select(n => n.GetString()), n => n!.Contains("no metadata call for indexes"));
    }

    // ---- ODBC through psqlodbc on PostgreSQL ----

    private static string RequireDriver()
    {
        var driver = EngineEnv.Get("DBDATABUILD_TEST_ODBC_PG_DRIVER");
        Skip.If(driver == null, "Set DBDATABUILD_TEST_ODBC_PG_DRIVER to the psqlodbc driver (scripts/fetch-psqlodbc.sh, then eval \"$(scripts/test-engines.sh env)\").");
        return driver!;
    }

    private static string OdbcValue(string driver, Engine origin)
    {
        var b = new NpgsqlConnectionStringBuilder(origin.ConnectionString);
        return $"Driver={{{driver}}};Server={b.Host};Port={b.Port};Database={b.Database};Uid={b.Username};Pwd={b.Password};";
    }

    private const string OriginDdl =
        "CREATE SCHEMA src; " +
        "CREATE TABLE src.parents (id BIGINT NOT NULL PRIMARY KEY, name VARCHAR(30) NOT NULL); " +
        "CREATE TABLE src.kids (id BIGINT NOT NULL, parent_id BIGINT NOT NULL, born DATE, price NUMERIC(10,2), flag BOOLEAN, payload BYTEA, PRIMARY KEY (parent_id, id), CONSTRAINT fk_kids_parent FOREIGN KEY (parent_id) REFERENCES src.parents (id)); " +
        "CREATE INDEX ix_kids_born ON src.kids (born DESC, price); " +
        "CREATE UNIQUE INDEX ux_kids_price ON src.kids (price, id); " +
        "CREATE INDEX ix_kids_expr ON src.kids ((price * 2)); " +
        "CREATE VIEW src.kid_view AS SELECT id, born FROM src.kids";

    private static async Task<(Engine Origin, Project Project)> OdbcProject(string dialects, string legacyEngine)
    {
        var driver = RequireDriver();
        var from = new PostgresEngine();
        await from.StartAsync();
        var login = OdbcValue(driver, from);
        var project = Project.Create($"{dialects}connections:\n  legacy: {{ engine: {legacyEngine} }}\n", v => v == "DBDATABUILD_LEGACY_READ" ? login : null);
        try { await using var probe = await ReadSession.OpenAsync(LoginSettings.FromEnvironment("legacy", "odbc", Login.Read, v => v == "DBDATABUILD_LEGACY_READ" ? login : null).Settings!); }
        catch (Exception ex) when (DriverFailure.Prerequisite(ex) is { } why) { project.Dispose(); await from.DisposeAsync(); Skip.If(true, $"The ODBC driver manager or the psqlodbc driver cannot be used on this machine: {why}"); }
        return (from, project);
    }

    [SkippableFact]
    public async Task With_catalog_odbc_the_import_reads_tables_keys_foreign_keys_and_indexes_through_the_drivers_own_functions()
    {
        var (from, project) = await OdbcProject("dialects:\n  driver_catalog: { extends: postgres, catalog: odbc }\n", "readonly odbc driver_catalog");
        using var _ = project;
        await using var __ = from;
        await from.ExecAsync(OriginDdl);

        var imported = project.Cli("project", "import", "legacy", "src.*", "--write");
        Ok(imported, "import");
        var kids = project.Descriptor("kids.yml");
        Assert.Matches(@"grain:\s*\[parent_id, id\]", kids);                                           // in key order, not table order
        Assert.Contains("fk_kids_parent", kids);
        Assert.Contains("src.parents", kids);
        Assert.Contains("ix_kids_born", kids);
        Assert.Contains("ux_kids_price", kids);
        Assert.DoesNotContain("ix_kids_expr", kids);                                                    // an expression index is not exported, and is named in the output
        Assert.Contains("ix_kids_expr", imported.Out);
        Assert.Contains("DECIMAL(10, 2)", kids);
        Assert.Contains("BOOLEAN", kids);
        Assert.Contains("BLOB", kids);                                                                  // bytea: the dialect `postgres` knows the driver's own name for it
        Assert.Contains("kid_view", imported.Out);
        Assert.Contains("(view", imported.Out);
        Assert.Contains("src.parents", imported.Out);
        Assert.DoesNotContain("pg_catalog", imported.Out + imported.Err);
        Assert.DoesNotContain("information_schema", imported.Out + imported.Err);

        // the same, again: nothing to write
        Assert.Contains("already match", project.Cli("project", "import", "legacy", "src.*", "--write").Out);
    }

    [SkippableFact]
    public async Task The_default_ansi_dialect_still_reads_through_information_schema_and_gets_its_indexes_from_the_drivers_statistics()
    {
        var (from, project) = await OdbcProject("", "readonly odbc");
        using var _ = project;
        await using var __ = from;
        await from.ExecAsync(OriginDdl);
        var imported = project.Cli("project", "import", "legacy", "src.kids", "--write");
        Ok(imported, "import");
        var kids = project.Descriptor("kids.yml");
        Assert.Contains("ix_kids_born", kids);
        Assert.Contains("ux_kids_price", kids);
        Assert.Contains("fk_kids_parent", kids);
        Assert.DoesNotContain("payload", kids);                                                         // bytea has no standard name in information_schema: still not guessed
        Assert.DoesNotContain("indexes were not read", imported.Out);
    }

    [SkippableFact]
    public async Task Connection_inspect_connects_through_the_driver_and_reads_no_schema_names_of_its_own_for_an_origin_no_model_is_built_on()
    {
        var (from, project) = await OdbcProject("dialects:\n  driver_catalog: { extends: postgres, catalog: odbc }\n", "readonly odbc driver_catalog");
        using var _ = project;
        await using var __ = from;
        var inspect = project.Cli("connection", "inspect", "legacy");
        Assert.Contains("connects; ODBC driver", inspect.Out);
        Assert.Contains("no model declares this connection", inspect.Out);                               // an origin has no model built on it: the schema names are those of the models, and there are none
        Assert.DoesNotContain("not read", inspect.Out);
    }

    [SkippableFact]
    public async Task A_catalog_function_the_driver_cannot_answer_for_a_schema_that_is_not_there_is_an_empty_import_not_a_failure()
    {
        var (from, project) = await OdbcProject("dialects:\n  driver_catalog: { extends: postgres, catalog: odbc }\n", "readonly odbc driver_catalog");
        using var _ = project;
        await using var __ = from;
        var imported = project.Cli("project", "import", "legacy", "nosuch.*");
        Assert.Equal(0, imported.Exit);
        Assert.Contains("No table or view matched", imported.Out);
    }

    // ---- limit: the zero-row read is valid where its style is the engine's ----

    [SkippableTheory]
    [InlineData("sqlserver", "top")]
    [InlineData("postgres", "limit")]
    [InlineData("postgres", "fetch")]
    public async Task The_zero_row_read_of_each_limit_style_runs_on_the_engine_that_takes_it(string engine, string style)
    {
        await using var e = EngineEnv.Require(engine);
        await e.StartAsync();
        var profile = DialectProfiles.Build("x", DialectProfiles.Ansi, "ansi", null, null, null, null, null, style, null).Profile!;
        var rows = await e.RowsAsync(profile.Bounded("SELECT 1 AS a, 'x' AS b", 0));
        Assert.Empty(rows);
        var one = await e.RowsAsync(profile.Bounded("SELECT 1 AS a UNION ALL SELECT 2", 1));
        Assert.Single(one);
    }
}
