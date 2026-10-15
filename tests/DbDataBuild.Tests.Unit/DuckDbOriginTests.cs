using System.Data;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Unit;

/// <summary>Read-only origins, step 2: a DuckDB file (or `:memory:`) as an origin. No docker: DuckDB runs in process.</summary>
public class DuckDbOriginTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-duckdb-origin-" + Guid.NewGuid().ToString("N")[..10]);
    private string File_ => Path.Combine(dir, "analytics.duckdb");

    public DuckDbOriginTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private static void Exec(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    /// <summary>A database file with a table of every logical type, a view, a table with a foreign key, and a few rows (one of them all NULL).</summary>
    private string MakeFile()
    {
        using var c = new DuckDBConnection($"Data Source={File_}");
        c.Open();
        Exec(c, "CREATE SCHEMA src");
        Exec(c, """
            CREATE TABLE src.items (id BIGINT NOT NULL PRIMARY KEY, small SMALLINT, n INTEGER, ratio DOUBLE, flag BOOLEAN, born DATE, seen TIMESTAMP, price DECIMAL(18,3), big DECIMAL(28,6),
                                    code VARCHAR, notes VARCHAR, tiny TINYINT, stamp TIMESTAMPTZ, clock TIME, token UUID, raw BLOB, real_ FLOAT, wide HUGEINT)
            """);
        Exec(c, """
            INSERT INTO src.items VALUES
              (1, 7, 100000, 3.5, true, DATE '2024-02-29', TIMESTAMP '2024-02-29 13:14:15.123456', 12345.678, 12345678901234567890.123456, 'café ☕', 'long text', 5, TIMESTAMPTZ '2024-02-29 13:14:15+00', TIME '13:14:15', '6f9619ff-8b86-d011-b42d-00c04fc964ff', '\xDE\xAD\xBE\xEF'::BLOB, 1.5, 170141183460469231731687303715884105727),
              (2, -32768, -2147483648, -0.25, false, DATE '0001-01-01', TIMESTAMP '1999-12-31 23:59:59.999999', -0.001, -0.000001, '', '', -128, NULL, NULL, NULL, NULL, NULL, NULL),
              (3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
            """);
        Exec(c, "CREATE VIEW src.item_codes AS SELECT id, code FROM src.items");
        Exec(c, "CREATE TABLE src.parts (id BIGINT NOT NULL, item_id BIGINT NOT NULL, PRIMARY KEY (id), FOREIGN KEY (item_id) REFERENCES src.items (id))");
        return File_;
    }

    private static LoginSettings Login_(string name, string value, string engine = "duckdb") =>
        LoginSettings.FromEnvironment(name, engine, Login.Read, v => v == LoginSettings.VariableName(name, Login.Read) ? value : null).Settings!;

    private static ProjectConfig Config(string yaml)
    {
        var diags = new List<Diagnostic>();
        var cfg = ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return cfg!;
    }

    // ---- the connection ----

    [Fact]
    public void A_readonly_duckdb_connection_is_accepted_and_has_the_duckdb_dialect()
    {
        var cfg = Config("connections:\n  analytics: { engine: readonly duckdb }\n");
        var c = cfg.Connections["analytics"];
        Assert.True(c.ReadOnly);
        Assert.Equal("duckdb", c.Engine);
        Assert.Equal(ParameterStyle.Positional, cfg.DialectOf("analytics").Parameters);
        Assert.False(c.Kind.IsBuildTarget);
        var (settings, _) = LoginSettings.FromEnvironment(c, Login.Read, _ => "/data/x.duckdb");
        Assert.Equal("DuckDB file, read-only (DBDATABUILD_ANALYTICS_READ)", settings!.Describe());       // the path is not printed
        Assert.Null(settings.User);
        Assert.Equal("DDB-455", LoginSettings.FromEnvironment(c, Login.Write, _ => "/data/x.duckdb").Error!.Code);
    }

    [Fact]
    public async Task The_file_is_opened_read_only_and_a_missing_file_is_not_created()
    {
        var path = MakeFile();
        var login = Login_("analytics", path);
        await using (var read = await ReadSession.OpenAsync(login))
        {
            var rows = await read.QueryAsync("SELECT id, code FROM src.items ORDER BY id");
            Assert.Equal([1L, 2L, 3L], rows.Select(r => r[0]));
            Assert.Equal(["café ☕", "", null], rows.Select(r => r[1]));
            await Assert.ThrowsAsync<GateRefusedException>(() => read.QueryAsync("DELETE FROM src.items"));
        }
        // the layer under the guard: the database itself refuses a write on this connection
        await using (var raw = await login.OpenAsync(CancellationToken.None))
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "INSERT INTO src.items (id) VALUES (99)";
            Assert.ThrowsAny<Exception>(() => cmd.ExecuteNonQuery());
        }
        var missing = Path.Combine(dir, "missing.duckdb");
        await Assert.ThrowsAnyAsync<Exception>(async () => { await using var r = await ReadSession.OpenAsync(Login_("gone", missing)); });
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task A_duckdb_file_has_no_routine_definitions_to_watch()
    {
        await using var read = await ReadSession.OpenAsync(Login_("analytics", MakeFile()));
        Assert.Null(await read.RoutineDefinitionAsync("duckdb", "src.some_function"));
    }

    [Fact]
    public async Task Several_sessions_read_one_file_at_once()
    {
        var path = MakeFile();
        await using var a = await ReadSession.OpenAsync(Login_("analytics", path));
        await using var b = await ReadSession.OpenAsync(Login_("analytics", path));
        await using var sa = await a.OpenStreamAsync("SELECT id FROM src.items ORDER BY id");
        await using var sb = await b.OpenStreamAsync("SELECT id FROM src.items ORDER BY id DESC");
        var first = new List<object?>(); var second = new List<object?>();
        await foreach (var row in sa.ReadAsync()) first.Add(row[0]);
        await foreach (var row in sb.ReadAsync()) second.Add(row[0]);
        Assert.Equal([1L, 2L, 3L], first);
        Assert.Equal([3L, 2L, 1L], second);
    }

    [Fact]
    public async Task A_query_reads_a_parquet_file_in_a_memory_origin()
    {
        var parquet = Path.Combine(dir, "orders.parquet").Replace('\\', '/');
        await using (var writer = new DuckDBConnection("Data Source=:memory:")) { writer.Open(); Exec(writer, $"COPY (SELECT i AS id, 'o' || i AS label FROM range(1, 6) t(i)) TO '{parquet}' (FORMAT PARQUET)"); }
        await using var read = await ReadSession.OpenAsync(Login_("lake", ":memory:"));
        var rows = await read.QueryAsync($"SELECT id, label FROM read_parquet('{parquet}') WHERE id >= ? ORDER BY id", [new GateParameter("from", DbType.Int64, 3L)]);
        Assert.Equal([3L, 4L, 5L], rows.Select(r => r[0]));
        // and CSV, which is the same kind of query
        var csv = Path.Combine(dir, "orders.csv").Replace('\\', '/');
        File.WriteAllText(csv, "id,label\n1,a\n2,b\n");
        Assert.Equal(2, (await read.QueryAsync($"SELECT * FROM read_csv('{csv}')")).Count);
    }

    [Fact]
    public async Task Positional_parameters_are_bound_in_the_order_given_and_a_value_used_twice_is_passed_twice()
    {
        var path = MakeFile();
        await using var read = await ReadSession.OpenAsync(Login_("analytics", path));
        await using var stream = await read.OpenStreamAsync("SELECT id FROM src.items WHERE id >= ? AND (id <= ? OR id = ?) ORDER BY id",
            [new GateParameter("lo", DbType.Int64, 2L), new GateParameter("hi", DbType.Int64, 2L), new GateParameter("lo", DbType.Int64, 2L)]);
        var ids = new List<object?>();
        await foreach (var row in stream.ReadAsync()) ids.Add(row[0]);
        Assert.Equal(["id"], stream.Names);
        Assert.Equal([2L], ids);
    }

    // ---- values: converted by the declared type ----

    [Fact]
    public async Task Every_logical_type_comes_out_of_a_duckdb_file_in_the_value_the_destination_takes()
    {
        var path = MakeFile();
        await using var read = await ReadSession.OpenAsync(Login_("analytics", path));
        await using var stream = await read.OpenStreamAsync("SELECT id, small, n, ratio, flag, born, seen, price, big, code, stamp, clock, token, raw, real_, tiny FROM src.items ORDER BY id");
        var rows = new List<object?[]>();
        await foreach (var row in stream.ReadAsync()) rows.Add(row);
        var types = new[] { "BIGINT", "SMALLINT", "INTEGER", "DOUBLE", "BOOLEAN", "DATE", "TIMESTAMP", "DECIMAL(18, 3)", "DECIMAL(28, 6)", "VARCHAR(40)", "TIMESTAMP WITH TIME ZONE", "TIME", "UUID", "BLOB", "FLOAT", "SMALLINT" };
        var names = stream.Names;
        object?[] Converted(object?[] row) => row.Select((v, i) => TransferValues.Convert(v, new TransferColumn(names[i], types[i]))).ToArray();
        var first = Converted(rows[0]);
        Assert.Equal([1L, (short)7, 100000, 3.5, true], first[..5]);
        Assert.Equal(new DateTime(2024, 2, 29), first[5]);
        Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1234560), first[6]);
        Assert.Equal(12345.678m, first[7]);
        Assert.Equal(12345678901234567890.123456m, first[8]);
        Assert.Equal("café ☕", first[9]);
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 13, 14, 15, TimeSpan.Zero), first[10]);
        Assert.Equal(new TimeSpan(13, 14, 15), first[11]);
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), first[12]);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, first[13]);
        Assert.Equal(1.5f, first[14]);
        Assert.Equal((short)5, first[15]);
        var second = Converted(rows[1]);
        Assert.Equal(new DateTime(1, 1, 1), second[5]);
        Assert.Equal(-0.000001m, second[8]);
        Assert.Equal("", second[9]);
        Assert.All(Converted(rows[2])[1..], Assert.Null);
    }

    // ---- the catalog ----

    [Fact]
    public async Task The_catalog_of_a_duckdb_file_is_read_through_information_schema()
    {
        var path = MakeFile();
        await using var read = await ReadSession.OpenAsync(Login_("analytics", path));
        Assert.Contains("src", await SourceCatalogReader.SchemasAsync(read, "duckdb"));
        Assert.DoesNotContain("information_schema", await SourceCatalogReader.SchemasAsync(read, "duckdb"));
        var shapes = await CatalogReader.ReadObjectsAsync(read, "duckdb", "src");
        Assert.Equal(["src.item_codes", "src.items", "src.parts"], shapes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(ObjectKind.View, shapes["src.item_codes"].Kind);
        var keys = await SourceCatalogReader.PrimaryKeysAsync(read, "duckdb", "src");
        Assert.Equal(["id"], keys["items"]);
        var fks = await SourceCatalogReader.ForeignKeysAsync(read, "duckdb", "src");
        var fk = Assert.Single(fks["parts"]);
        Assert.Equal(["item_id"], fk.Columns);
        Assert.Equal(("src", "items"), (fk.ReferencedSchema, fk.ReferencedTable));
        Assert.Equal(["id"], fk.ReferencedColumns);

        var items = MappedModelImport.Describe("duckdb", shapes["src.items"], keys["items"]);
        string Logical(string column) => items.Columns.Single(c => c.Name == column).LogicalType ?? "(none)";
        Assert.Equal(("BIGINT", "SMALLINT", "INTEGER", "DOUBLE", "BOOLEAN", "DATE", "TIMESTAMP"), (Logical("id"), Logical("small"), Logical("n"), Logical("ratio"), Logical("flag"), Logical("born"), Logical("seen")));
        Assert.Equal(("DECIMAL(18, 3)", "DECIMAL(28, 6)", "VARCHAR", "TIMESTAMP WITH TIME ZONE", "TIME", "UUID", "BLOB", "FLOAT"), (Logical("price"), Logical("big"), Logical("code"), Logical("stamp"), Logical("clock"), Logical("token"), Logical("raw"), Logical("real_")));
        Assert.Equal("SMALLINT", Logical("tiny"));
        Assert.Equal("(none)", Logical("wide"));                                                   // HUGEINT has no logical type: said, never guessed
        Assert.False(items.Columns.Single(c => c.Name == "id").Nullable);
        Assert.True(items.Columns.Single(c => c.Name == "small").Nullable);
        Assert.Equal(["id"], items.PrimaryKey);
    }
}
