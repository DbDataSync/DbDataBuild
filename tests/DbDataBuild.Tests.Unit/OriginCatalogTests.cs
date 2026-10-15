using System.Data;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;

namespace DbDataBuild.Tests.Unit;

/// <summary>Read-only origins, step 5 (docs/research/read-origins.md section 8c): the catalog on the new origins. ODBC's own catalog functions (`catalog: odbc`), indexes, DuckDB's foreign key schema, `limit`, `identifier_case`.</summary>
public class OriginCatalogTests
{
    private static ProjectConfig? Load(string yaml, out List<Diagnostic> diags)
    {
        diags = [];
        return ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags);
    }

    // ---- a built-in engine dialect on a driver of its own ----

    [Fact]
    public void A_built_in_engine_dialect_on_flightsql_or_odbc_keeps_its_quoting_and_binds_by_position()
    {
        var cfg = Load("connections:\n  pg: { engine: readonly flightsql postgres }\n  ms: { engine: readonly odbc sqlserver }\n  plain: { engine: readonly flightsql }\n", out var diags)!;
        Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
        var pg = cfg.DialectOf("pg");
        Assert.Equal(("postgres", "\"", ParameterStyle.Positional, LimitStyle.Limit), (pg.Name, pg.QuoteOpen, pg.Parameters, pg.Limit));
        var ms = cfg.DialectOf("ms");
        Assert.Equal(("sqlserver", "[", ParameterStyle.Positional, LimitStyle.Top), (ms.Name, ms.QuoteOpen, ms.Parameters, ms.Limit));
        Assert.Equal(DialectProfiles.Ansi, cfg.DialectOf("plain"));

        // the read text a copy sends is in the backend's quoting with `?` for the watermark
        var def = new ModelDefinition("dst.items", ModelKinds.Copy, [], null, null, ["id"], null, [new ColumnDefinition("id", "BIGINT", false), new ColumnDefinition("changed", "TIMESTAMP")], [], From: "src.items", Watermark: new CopyWatermark("changed", null));
        var ddl = TargetRegistry.Get("sqlserver").CreateDdl(cfg);
        var (_, viaMs) = Planner.CopyTransfer(cfg, def, ddl, new CopyOrigin("ms", "odbc", "src.items", null, "2024-01-01"));
        Assert.Equal("SELECT [id], [changed] FROM [src].[items] WHERE [changed] >= ?", viaMs.ReadText);
        Assert.Equal(["watermark"], viaMs.ParameterOrder);
        var (_, viaPg) = Planner.CopyTransfer(cfg, def, ddl, new CopyOrigin("pg", "flightsql", "src.items", null, "2024-01-01"));
        Assert.Equal("SELECT \"id\", \"changed\" FROM \"src\".\"items\" WHERE \"changed\" >= ?", viaPg.ReadText);
    }

    [Fact]
    public void An_engine_dialect_on_flightsql_or_odbc_reads_the_engines_own_type_names()
    {
        var cfg = Load("connections:\n  pg: { engine: readonly flightsql postgres }\n  ms: { engine: readonly odbc sqlserver }\n", out _)!;
        ColumnShape Col(string type, int? length = null, int? precision = null, int? scale = null) => new("c", type, length, precision, scale, true, null, null);
        Assert.Equal("BIGINT", ImportedTypes.From("flightsql", Col("int8"), cfg.DialectOf("pg")).LogicalType);
        Assert.Equal("BOOLEAN", ImportedTypes.From("flightsql", Col("bool"), cfg.DialectOf("pg")).LogicalType);
        Assert.Equal("BIGINT", ImportedTypes.From("flightsql", Col("bigint"), cfg.DialectOf("pg")).LogicalType);        // the standard names still work
        Assert.Equal("VARCHAR(20)", ImportedTypes.From("odbc", Col("nvarchar", 20), cfg.DialectOf("ms")).LogicalType);
        Assert.Equal("TIMESTAMP", ImportedTypes.From("odbc", Col("datetime2"), cfg.DialectOf("ms")).LogicalType);
        Assert.Equal("UUID", ImportedTypes.From("odbc", Col("uniqueidentifier"), cfg.DialectOf("ms")).LogicalType);
    }

    [Fact]
    public void A_project_dialect_that_says_named_at_is_bound_by_position_on_odbc_and_flightsql()
    {
        var cfg = Load("dialects:\n  legacy_sql: { extends: postgres, quote: '`' }\nconnections:\n  a: { engine: readonly odbc legacy_sql }\n", out var diags)!;
        Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
        Assert.Equal(ParameterStyle.Positional, cfg.DialectOf("a").Parameters);
        Assert.Equal("`", cfg.DialectOf("a").QuoteOpen);
    }

    // ---- the catalog choice ----

    [Fact]
    public void The_odbc_catalog_choice_is_a_choice_of_the_profile_and_only_for_an_odbc_connection()
    {
        var cfg = Load("dialects:\n  text_files: { extends: ansi, catalog: odbc }\nconnections:\n  files: { engine: readonly odbc text_files }\n", out var diags)!;
        Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
        Assert.Equal(CatalogStyle.Odbc, cfg.DialectOf("files").Catalog);
        Assert.Equal(CatalogStyle.InformationSchema, DialectProfiles.Ansi.Catalog);

        Load("dialects:\n  text_files: { extends: ansi, catalog: odbc }\nconnections:\n  files: { engine: readonly flightsql text_files }\n", out diags);
        var refused = Assert.Single(diags, d => d.Severity == Severity.Error);
        Assert.Equal("DDB-456", refused.Descriptor.Code);
        Assert.Contains("`catalog: odbc`", refused.Found);

        Load("dialects:\n  bad: { extends: ansi, catalog: sqlcolumns }\nconnections:\n  a: { engine: readonly odbc bad }\n", out diags);
        Assert.Contains(diags, d => d.Severity == Severity.Error && d.Found.Contains("information_schema, odbc, none"));
    }

    // ---- ODBC's catalog functions over a fake driver ----

    private sealed class FakeDriver : IOdbcCatalogFunctions
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> Unsupported { get; } = [];

        private IReadOnlyList<string?[]> Rows(string function, params string?[][] rows)
        {
            Calls.Add(function);
            if (Unsupported.Contains(function)) throw new OdbcCatalogException(function, "IM001");
            return rows;
        }

        public Task<IReadOnlyList<string?[]>> SchemasAsync(CancellationToken ct) =>
            Task.FromResult(Rows("SQLTables", [null, "sales", null, null, null], [null, "information_schema", null, null, null], [null, "ops", null, null, null], [null, "sys", null, null, null]));

        public Task<IReadOnlyList<string?[]>> TablesAsync(string schema, CancellationToken ct) =>
            Task.FromResult(Rows("SQLTables",
                [null, "sales", "orders", "TABLE", null],
                [null, "sales", "orders_v", "VIEW", null],
                [null, "sales", "pg_stat", "SYSTEM TABLE", null],
                [null, "sales_x", "other", "TABLE", null]));                                    // a pattern match that is not the schema asked for

        public Task<IReadOnlyList<string?[]>> ColumnsAsync(string schema, string? table, CancellationToken ct) =>
            Task.FromResult(Rows("SQLColumns",
                [null, "sales", "orders", "amount", "3", "numeric", "12", "14", "2", "10", "1", null, null, "3", null, null, "2", "YES"],
                [null, "sales", "orders", "id", "-5", "int8", "19", "8", "0", "10", "0", null, null, "-5", null, null, "1", "NO"],
                [null, "sales", "orders", "code", "12", "varchar", "40", "40", null, null, "1", null, null, "12", null, "40", "3", "YES"],
                [null, "sales", "orders", "flag", "-7", "bool", "1", "1", null, null, "1", null, null, "-7", null, null, "4", "YES"],
                [null, "sales", "orders", "odd", "-150", "Sql_Variant", "0", "0", null, null, "1", null, null, "-150", null, null, "5", "YES"],
                [null, "sales", "orders_v", "id", "4", "int4", "10", "4", "0", "10", "2", null, null, "4", null, null, "1", ""],
                [null, "sales_x", "other", "z", "4", "int4", "10", "4", "0", "10", "1", null, null, "4", null, null, "1", "YES"]));

        public Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string schema, string table, CancellationToken ct) =>
            Task.FromResult(table != "orders" ? Rows("SQLPrimaryKeys") : Rows("SQLPrimaryKeys",
                [null, "sales", "orders", "code", "2", "orders_pk"], [null, "sales", "orders", "id", "1", "orders_pk"]));

        public Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string schema, string table, CancellationToken ct) =>
            Task.FromResult(table != "orders" ? Rows("SQLForeignKeys") : Rows("SQLForeignKeys",
                [null, "ref", "customers", "region", null, "sales", "orders", "cust_region", "2", "1", "1", "orders_cust_fk", "customers_pk"],
                [null, "ref", "customers", "id", null, "sales", "orders", "cust_id", "1", "1", "1", "orders_cust_fk", "customers_pk"],
                [null, "ref", "products", "id", null, "sales", "orders", "product_id", "1", "1", "1", null, "products_pk"]));

        public Task<IReadOnlyList<string?[]>> StatisticsAsync(string schema, string table, CancellationToken ct) =>
            Task.FromResult(table != "orders" ? Rows("SQLStatistics") : Rows("SQLStatistics",
                [null, "sales", "orders", null, null, null, "0", null, null, null, "1000", "10", null],                         // the table's own row
                [null, "sales", "orders", "0", null, "orders_pk", "3", "1", "id", "A", null, null, null],
                [null, "sales", "orders", "0", null, "orders_pk", "3", "2", "code", "A", null, null, null],
                [null, "sales", "orders", "1", null, "ix_code", "3", "2", "amount", "D", null, null, null],
                [null, "sales", "orders", "1", null, "ix_code", "3", "1", "code", "A", null, null, null],
                [null, "sales", "orders", "1", null, "ix_expr", "3", "1", null, "A", null, null, null]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static OdbcCatalog Catalog(FakeDriver driver, DialectProfile? dialect = null) => new(driver, dialect ?? DialectProfiles.Ansi);

    [Fact]
    public async Task The_schema_names_are_the_drivers_less_the_systems_own()
    {
        var schemas = await Catalog(new FakeDriver()).SchemasAsync(default);
        Assert.Equal(["ops", "sales"], schemas);
    }

    [Fact]
    public async Task Tables_and_views_come_with_their_columns_named_by_the_standard_where_the_driver_has_a_code_for_them()
    {
        var tables = (await Catalog(new FakeDriver()).TablesAsync("sales", null, default))!;
        Assert.Equal(["orders", "orders_v"], tables.Select(t => t.Name));                  // not the system table, not the table of a schema that only matched the pattern
        Assert.Equal([false, true], tables.Select(t => t.IsView));
        var orders = tables[0].Columns;
        Assert.Equal(["id", "amount", "code", "flag", "odd"], orders.Select(c => c.Name));  // in ORDINAL_POSITION order
        Assert.Equal(("bigint", false), (orders[0].Type, orders[0].Nullable));
        Assert.Equal(("numeric", 12, 2, true), (orders[1].Type, orders[1].Precision, orders[1].Scale, orders[1].Nullable));
        Assert.Equal(("varchar", 40), (orders[2].Type, orders[2].Length));                 // the driver's name when the dialect knows it (ansi does), the standard name of the code when it does not
        Assert.Equal("boolean", orders[3].Type);
        Assert.Equal("sql_variant", orders[4].Type);                                       // a vendor code: the driver's own name, for the profile to map or the import to report
        Assert.True(tables[1].Columns[0].Nullable);                                        // NULLABLE = 2 is "unknown": nullable
    }

    [Fact]
    public async Task A_type_the_profile_maps_by_the_drivers_own_name_is_named_that_way()
    {
        var dialect = DialectProfiles.Build("mine", DialectProfiles.Ansi, "ansi", null, null, null, "odbc", null, null, new Dictionary<string, string> { ["sql_variant"] = "VARCHAR" }).Profile!;
        var odd = (await Catalog(new FakeDriver(), dialect).TablesAsync("sales", "orders", default))![0].Columns.Single(c => c.Name == "odd");
        Assert.Equal("sql_variant", odd.Type);
        Assert.Equal("VARCHAR", ImportedTypes.From("odbc", odd, dialect).LogicalType);
        Assert.Null(ImportedTypes.From("odbc", odd, DialectProfiles.Ansi).LogicalType);
    }

    [Fact]
    public async Task Primary_keys_are_in_key_order_and_foreign_keys_group_by_name_with_the_referenced_schema()
    {
        var catalog = Catalog(new FakeDriver());
        var keys = (await catalog.PrimaryKeysAsync("sales", default))!;
        Assert.Equal(["id", "code"], keys["orders"]);
        Assert.False(keys.ContainsKey("orders_v"));                                         // a view has none, and is not asked

        var foreign = (await catalog.ForeignKeysAsync("sales", default))!["orders"];
        Assert.Equal(2, foreign.Count);
        var customers = foreign.Single(f => f.Name == "orders_cust_fk");
        Assert.Equal(["cust_id", "cust_region"], customers.Columns);
        Assert.Equal(["id", "region"], customers.ReferencedColumns);
        Assert.Equal(("ref", "customers"), (customers.ReferencedSchema, customers.ReferencedTable));
        var unnamed = foreign.Single(f => f.Name != "orders_cust_fk");
        Assert.Equal("products", unnamed.ReferencedTable);
        Assert.Equal(["product_id"], unnamed.Columns);
        Assert.StartsWith("fk_orders_products", unnamed.Name);                              // a driver that names no key gets a stable name from the tables
    }

    [Fact]
    public async Task Indexes_come_from_the_statistics_without_the_tables_own_row()
    {
        var indexes = (await Catalog(new FakeDriver()).IndexesAsync("sales", default))!["orders"];
        Assert.Equal(["ix_code", "ix_expr", "orders_pk"], indexes.Select(i => i.Name));
        var code = indexes.Single(i => i.Name == "ix_code");
        Assert.False(code.Unique);
        Assert.Equal(["code", "amount desc"], code.Columns);
        Assert.True(indexes.Single(i => i.Name == "orders_pk").Unique);
        Assert.Equal(["<expression>"], indexes.Single(i => i.Name == "ix_expr").Columns);
    }

    [Fact]
    public async Task A_driver_that_does_not_implement_a_function_answers_null_for_that_part()
    {
        var driver = new FakeDriver { Unsupported = { "SQLPrimaryKeys", "SQLForeignKeys", "SQLStatistics", "SQLColumns" } };
        var catalog = Catalog(driver);
        Assert.Null(await catalog.PrimaryKeysAsync("sales", default));
        Assert.Null(await catalog.ForeignKeysAsync("sales", default));
        Assert.Null(await catalog.IndexesAsync("sales", default));
        Assert.Null(await catalog.TablesAsync("sales", null, default));
    }

    [Fact]
    public void An_odbc_type_code_has_the_standard_name_or_none()
    {
        Assert.Equal(["bigint", "integer", "smallint", "boolean", "date", "time", "timestamp", "uuid", ""], new[] { -5, 4, 5, -7, 91, 92, 93, -11, -150 }.Select(OdbcCatalog.StandardName));
    }

    // ---- the readers over a session with catalog: odbc ----

    private sealed class MetadataOnlySource(OdbcCatalog catalog) : IRowSource, IMetadataCatalog
    {
        public List<string> Texts { get; } = [];
        public Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct) { Texts.Add(sql); throw new InvalidOperationException("no SQL is sent for a catalog read through the driver's functions"); }
        public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => Task.FromResult<IReadOnlyList<ColumnShape>?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IReadOnlyList<string>?> SchemasAsync(CancellationToken ct) => catalog.SchemasAsync(ct);
        public Task<IReadOnlyList<MetadataTable>?> TablesAsync(string schema, string? table, CancellationToken ct) => catalog.TablesAsync(schema, table, ct);
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> PrimaryKeysAsync(string schema, CancellationToken ct) => catalog.PrimaryKeysAsync(schema, ct);
        public Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?> ForeignKeysAsync(string schema, CancellationToken ct) => catalog.ForeignKeysAsync(schema, ct);
        public Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataIndex>>?> IndexesAsync(string schema, CancellationToken ct) => catalog.IndexesAsync(schema, ct);
    }

    private static readonly DialectProfile OdbcCatalogDialect = DialectProfiles.Build("text_files", DialectProfiles.Ansi, "ansi", null, null, null, "odbc", null, null, null).Profile!;

    [Fact]
    public async Task With_catalog_odbc_the_importer_reads_schemas_tables_keys_and_indexes_and_sends_no_SQL()
    {
        var source = new MetadataOnlySource(Catalog(new FakeDriver(), OdbcCatalogDialect));
        await using var session = ReadSession.ForTesting(source, "odbc", OdbcCatalogDialect);
        Assert.Equal(["ops", "sales"], await SourceCatalogReader.SchemasAsync(session, "odbc"));
        var shapes = await CatalogReader.ReadObjectsAsync(session, "odbc", "sales");
        Assert.Equal(["sales.orders", "sales.orders_v"], shapes.Keys.Order());
        Assert.Equal(ObjectKind.View, shapes["sales.orders_v"].Kind);
        Assert.Equal(["id", "code"], (await SourceCatalogReader.PrimaryKeysAsync(session, "odbc", "sales"))["orders"]);
        var foreign = (await SourceCatalogReader.ForeignKeysAsync(session, "odbc", "sales"))["orders"];
        Assert.Equal("ref.customers", foreign.Single(f => f.Name == "orders_cust_fk").ReferencedSchema + "." + foreign.Single(f => f.Name == "orders_cust_fk").ReferencedTable);
        var indexes = (await SourceCatalogReader.IndexesAsync(session, "odbc", "sales"))!;
        Assert.Equal(["ix_code", "ix_expr", "orders_pk"], indexes["orders"].Select(i => i.Name));
        Assert.Empty(source.Texts);
        Assert.Empty(session.CatalogNotes);

        // and the importer's descriptor: the key seeds the grain, the index is exported, the expression index is said to be left out
        var live = MappedModelImport.Describe("odbc", shapes["sales.orders"] with { Physical = indexes["orders"] }, (await SourceCatalogReader.PrimaryKeysAsync(session, "odbc", "sales"))["orders"], dialect: OdbcCatalogDialect);
        Assert.Equal(["id", "code"], live.PrimaryKey);
        Assert.Equal(["ix_code"], live.Indexes!.Select(i => i.Name));                       // the primary key's own index is the grain
        Assert.Equal(["code", "amount"], live.Indexes![0].Columns);
        Assert.Contains(live.Notes!, n => n.Contains("ix_expr") && n.Contains("expression"));
    }

    [Fact]
    public async Task A_driver_without_the_key_functions_is_said_to_have_none_and_one_without_the_table_functions_is_refused_plainly()
    {
        var driver = new FakeDriver { Unsupported = { "SQLPrimaryKeys", "SQLForeignKeys", "SQLStatistics" } };
        await using var session = ReadSession.ForTesting(new MetadataOnlySource(Catalog(driver, OdbcCatalogDialect)), "odbc", OdbcCatalogDialect);
        Assert.Empty(await SourceCatalogReader.PrimaryKeysAsync(session, "odbc", "sales"));
        Assert.Empty(await SourceCatalogReader.ForeignKeysAsync(session, "odbc", "sales"));
        Assert.Null(await SourceCatalogReader.IndexesAsync(session, "odbc", "sales"));
        Assert.Contains(session.CatalogNotes, n => n.Contains("SQLPrimaryKeys"));
        Assert.Contains(session.CatalogNotes, n => n.Contains("SQLForeignKeys"));
        Assert.Contains(session.CatalogNotes, n => n.Contains("SQLStatistics"));

        var blind = new FakeDriver { Unsupported = { "SQLColumns" } };
        await using var noColumns = ReadSession.ForTesting(new MetadataOnlySource(Catalog(blind, OdbcCatalogDialect)), "odbc", OdbcCatalogDialect);
        var refused = await Assert.ThrowsAsync<CatalogUnavailableException>(() => CatalogReader.ReadObjectsAsync(noColumns, "odbc", "sales"));
        Assert.Contains("SQLColumns", refused.Message);
        Assert.Contains("declare them by hand", refused.Message);
        Assert.Contains("SQLColumns", refused.Brief);
    }

    [Fact]
    public async Task Indexes_of_a_source_that_has_no_way_to_say_them_are_said_to_be_missing()
    {
        var flight = new MetadataOnlySource(Catalog(new FakeDriver()));
        await using var session = ReadSession.ForTesting(flight, "flightsql");
        Assert.Null(await SourceCatalogReader.IndexesAsync(session, "flightsql", "sales"));
        Assert.Contains(session.CatalogNotes, n => n.Contains("Flight SQL has no metadata call for indexes"));

        // an ODBC source read through information_schema still asks the driver for its statistics; a source that is not able to answer is a note and not a failure
        var plain = new MetadataOnlySource(Catalog(new FakeDriver { Unsupported = { "SQLStatistics" } }));
        await using var odbc = ReadSession.ForTesting(plain, "odbc");
        Assert.Null(await SourceCatalogReader.IndexesAsync(odbc, "odbc", "sales"));
        Assert.Contains(odbc.CatalogNotes, n => n.Contains("SQLStatistics"));

        // the engines carry their indexes in the shape already
        await using var postgres = ReadSession.ForTesting(plain, "postgres");
        Assert.Null(await SourceCatalogReader.IndexesAsync(postgres, "postgres", "sales"));
        Assert.Empty(postgres.CatalogNotes);
    }

    // ---- DuckDB: the schema of a foreign key and the columns of an index ----

    [Theory]
    [InlineData("FOREIGN KEY (pid) REFERENCES p(id)", "p", "main", "main")]
    [InlineData("FOREIGN KEY (pid) REFERENCES s.p(id)", "p", "s", "s")]
    [InlineData("FOREIGN KEY (pid) REFERENCES other.p(id)", "p", "main", "other")]
    [InlineData("FOREIGN KEY (pid) REFERENCES memory.other.p(id)", "p", "main", "other")]
    [InlineData("FOREIGN KEY (pid, x) REFERENCES my s.P q(id, \"i d\")", "P q", "my s", "my s")]
    [InlineData("FOREIGN KEY (pid) REFERENCES \"other\".p(id)", "p", "main", "other")]
    [InlineData("something else", "p", "main", "main")]
    public void The_schema_a_duckdb_foreign_key_points_to_is_read_from_its_constraint_text(string text, string table, string own, string expected) =>
        Assert.Equal(expected, SourceCatalogReader.DuckDbReferencedSchema(text, table, own));

    [Theory]
    [InlineData("CREATE INDEX ix ON s.t(a, b);", "a|b")]
    [InlineData("CREATE UNIQUE INDEX iu ON s.t(c);", "c")]
    [InlineData("CREATE INDEX ix ON s.t(a, \"b c\");", "a|b c")]
    [InlineData("CREATE INDEX ie ON s.t(((a + 1)));", "<expression>")]
    [InlineData("CREATE INDEX im ON s.t(a, (b * 2), c);", "a|<expression>|c")]
    [InlineData("CREATE INDEX iq ON s.t(\"we\"\"ird\");", "we\"ird")]
    public void The_columns_of_a_duckdb_index_are_read_from_its_create_text(string sql, string expected) =>
        Assert.Equal(expected, string.Join("|", SourceCatalogReader.DuckDbIndexColumns(sql)!));

    [Fact]
    public void A_create_text_that_is_not_understood_gives_no_columns() => Assert.Null(SourceCatalogReader.DuckDbIndexColumns("something"));

    // ---- limit and identifier_case ----

    [Theory]
    [InlineData("limit", "SELECT * FROM (SELECT a FROM t) AS ddb_described LIMIT 0")]
    [InlineData("top", "SELECT TOP 0 * FROM (SELECT a FROM t) AS ddb_described")]
    [InlineData("fetch", "SELECT * FROM (SELECT a FROM t) AS ddb_described FETCH FIRST 0 ROWS ONLY")]
    public void A_bounded_read_is_written_in_the_limit_style_of_the_dialect(string style, string expected)
    {
        var profile = DialectProfiles.Build("x", DialectProfiles.Ansi, "ansi", null, null, null, null, null, style, null).Profile!;
        Assert.Equal(expected, profile.Bounded("SELECT a FROM t;\n", 0));
    }

    [Fact]
    public void The_duckdb_describe_text_is_the_one_it_always_was() =>
        Assert.Equal("SELECT * FROM (SELECT 1) AS ddb_described LIMIT 0", DialectProfiles.DuckDb.Bounded(" SELECT 1;", 0));

    [Fact]
    public void An_unquoted_name_is_folded_as_the_dialect_says()
    {
        Assert.Equal("ORDERS", DialectProfiles.Build("o", DialectProfiles.Ansi, "ansi", null, null, null, null, "upper", null, null).Profile!.Fold("orders"));
        Assert.Equal("orders", DialectProfiles.Postgres.Fold("Orders"));
        Assert.Equal("Orders", DialectProfiles.Ansi.Fold("Orders"));
    }

    /// <summary>A catalog that matches a schema name the way Oracle does: exactly, and it stores unquoted names in upper case.</summary>
    private sealed class UpperCaseCatalog : IRowSource
    {
        public List<string> Asked { get; } = [];
        public Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct)
        {
            Asked.Add((string)parameters[0].Value!);
            IReadOnlyList<IReadOnlyList<object?>> rows = Equals(parameters[0].Value, "SRC") ? [new object?[] { "ITEMS", "BASE TABLE", 1, "ID", "bigint", null, 64, 0, "NO" }] : [];
            return Task.FromResult<IRowReader>(new Reader(rows));
        }
        public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => Task.FromResult<IReadOnlyList<ColumnShape>?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Reader(IReadOnlyList<IReadOnlyList<object?>> rows) : IRowReader
        {
            private int at = -1;
            public IReadOnlyList<string> Names => Enumerable.Range(0, 9).Select(i => "c" + i).ToList();
            public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++at < rows.Count);
            public ValueTask<object?> ValueAsync(int column, CancellationToken ct) => ValueTask.FromResult(rows[at][column]);
            public void Cancel() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_declared_name_is_looked_up_as_written_and_then_as_the_dialect_folds_an_unquoted_one()
    {
        var upper = DialectProfiles.Build("shouting", DialectProfiles.Ansi, "ansi", null, null, null, null, "upper", null, null).Profile!;
        var source = new UpperCaseCatalog();
        await using var session = ReadSession.ForTesting(source, "odbc", upper);
        var (shape, storedAs) = await CatalogReader.FindAsync(session, "odbc", "src", "items");
        Assert.Equal("SRC.ITEMS", storedAs);
        Assert.Equal("ITEMS", shape!.Name);
        Assert.Equal(["src", "SRC"], source.Asked);                                         // as written first: a quoted mixed-case name keeps working

        var (same, none) = await CatalogReader.FindAsync(session, "odbc", "SRC", "ITEMS");
        Assert.Null(none);                                                                  // spelled as stored: no remark
        Assert.NotNull(same);

        // a dialect that preserves case does not look twice
        await using var plain = ReadSession.ForTesting(new UpperCaseCatalog(), "odbc");
        Assert.Null((await CatalogReader.FindAsync(plain, "odbc", "src", "items")).Shape);
    }

    // ---- help dialects ----

    [Fact]
    public void Help_dialects_lists_the_fields_and_the_built_ins_from_the_data_and_its_example_is_a_project_that_loads()
    {
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(0, Cli.CliApp.Run(["help", "dialects"], o, e, environment: _ => null));
        var text = o.ToString();
        foreach (var field in new[] { "extends", "quote", "parameters", "ping", "catalog", "identifier_case", "limit", "types" }) Assert.Contains($"  {field} ", text);
        foreach (var name in DialectProfiles.BuiltIn.Keys) Assert.Contains($"  {name} ", text);
        Assert.Contains("information_schema | odbc | none", text);
        Assert.Contains("readonly flightsql postgres", text);
        Assert.Contains("header.x-datasource=", text);
        Assert.Contains("not to the driver or the server in front of it", text);

        // the example in the page is a project the tool accepts
        var example = string.Join("\n", text.Split('\n').SkipWhile(l => !l.StartsWith("Example:", StringComparison.Ordinal)).Skip(1).TakeWhile(l => !l.StartsWith("See ", StringComparison.Ordinal)).Select(l => l.TrimEnd('\r')));
        var cfg = Load(example, out var diags)!;
        Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
        Assert.Equal(("postgres", "sqlserver", "clickhouse_sql"), (cfg.DialectOf("erp").Name, cfg.DialectOf("sales").Name, cfg.DialectOf("clicks").Name));

        var json = new StringWriter();
        Assert.Equal(0, Cli.CliApp.Run(["help", "dialects", "--format", "json"], json, new StringWriter(), environment: _ => null));
        using var doc = System.Text.Json.JsonDocument.Parse(json.ToString());
        Assert.Equal(8, doc.RootElement.GetProperty("data").GetProperty("fields").GetArrayLength());
    }

    [Fact]
    public void The_gate_names_the_native_odbc_catalog_file_and_it_executes_no_statement()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DbDataBuild.sln"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "src/DbDataBuild.Execution/OdbcCatalogClient.cs"));
        // the catalog functions only: nothing that prepares or executes a statement is imported from the driver manager
        Assert.DoesNotContain("SQLExecDirect", text);
        Assert.DoesNotContain("SQLExecute", text);
        Assert.DoesNotContain("SQLPrepare", text);
        Assert.DoesNotContain("SQLBulkOperations", text);
        Assert.DoesNotContain("SQLSetPos", text);
        var users = Directory.EnumerateFiles(Path.Combine(dir.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/") && !f.Contains("\\obj\\") && File.ReadAllText(f).Contains("DllImport(\"ddb-odbc\")")).Select(f => Path.GetFileName(f)).ToList();
        Assert.Equal(["OdbcCatalogClient.cs"], users);
    }
}
