using System.Data;
using System.Data.Odbc;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Read-only origins, step 3 (docs/research/read-origins.md): the ODBC origin without a database: the login's value, the positional binding, the catalog queries, the types of a profile, and the words for a missing prerequisite.</summary>
public class OdbcOriginTests
{
    // ---- the login's value ----

    public static TheoryData<string, string?> Users => new()
    {
        { "Driver={PostgreSQL Unicode};Server=h;Database=d;UID=reader;PWD=secret", "reader" },
        { "Driver={PostgreSQL Unicode};Server=h;Uid=reader;Pwd=secret", "reader" },
        { "DSN=warehouse;User=sam;Password=x", "sam" },
        { "DSN=warehouse;Username=sam", "sam" },
        { "DSN=warehouse;User Id=sam", "sam" },
        { "DSN=warehouse", null },                                           // the DSN holds the credentials
        { "warehouse", null },                                               // a bare DSN name
        { "Driver={X};Trusted_Connection=yes", null },
        { "Driver={X};PWD={a;b=c}}};UID=odd", "odd" },                        // a value in braces may hold ; and = and a doubled }
        { "Driver={X};UID={semi;colon};PWD=p", "semi;colon" },
        { "Driver={X};;UID=after-empty-part", "after-empty-part" },
        { "", null },
    };

    [Theory, MemberData(nameof(Users))]
    public void The_user_is_read_from_the_login_value_and_nothing_else_is(string value, string? user) => Assert.Equal(user, OdbcLogin.UserOf(value));

    [Fact]
    public void A_bare_name_is_a_dsn_and_a_connection_string_is_passed_as_it_is()
    {
        Assert.Equal("DSN=warehouse", OdbcLogin.Normalize("  warehouse "));
        Assert.Equal("DSN=w;UID=u;PWD=p", OdbcLogin.Normalize("DSN=w;UID=u;PWD=p"));
        var pairs = OdbcLogin.Parse("Driver={A;B};Server=h; PWD = {x}}y} ;Port=5432");
        Assert.Equal([("driver", "A;B"), ("server", "h"), ("pwd", "x}y"), ("port", "5432")], pairs);
    }

    [Fact]
    public void The_login_is_described_by_its_variable_and_user_and_never_by_its_value()
    {
        var cfg = ProjectConfigLoader.Load("connections:\n  legacy: { engine: readonly odbc }\n", "dbdatabuild.yml", [])!;
        var (login, _) = LoginSettings.FromEnvironment(cfg.Connections["legacy"], Login.Read, v => v == "DBDATABUILD_LEGACY_READ" ? "Driver={PostgreSQL Unicode};Server=db.internal;UID=reader;PWD=hunter2" : null);
        Assert.Equal("odbc", login!.Engine);
        Assert.Equal("reader", login.User);
        Assert.Equal("ODBC, reader (DBDATABUILD_LEGACY_READ)", login.Describe());
        Assert.DoesNotContain("hunter2", login.Describe());
        Assert.DoesNotContain("db.internal", login.Describe());
        var (dsn, _) = LoginSettings.FromEnvironment(cfg.Connections["legacy"], Login.Read, _ => "DSN=erp");
        Assert.Equal("ODBC, the driver's own credentials (DBDATABUILD_LEGACY_READ)", dsn!.Describe());
        // no write login exists for it, whichever way it is asked for
        Assert.Equal("DDB-455", LoginSettings.FromEnvironment(cfg.Connections["legacy"], Login.Write, _ => "x").Error!.Code);
    }

    [Fact]
    public void A_dialect_of_the_project_reaches_the_login_through_every_way_of_asking_for_it()
    {
        var cfg = ProjectConfigLoader.Load("dialects:\n  backtick_sql: { extends: ansi, quote: '`', ping: SELECT 42 }\nconnections:\n  legacy: { engine: readonly odbc backtick_sql }\n  plain: { engine: readonly odbc }\n", "dbdatabuild.yml", [])!;
        Assert.Equal("backtick_sql", cfg.Connections["legacy"].Profile!.Name);
        Assert.Null(cfg.Connections["plain"].Profile);
        string? Env(string _) => "DSN=x";
        Assert.Equal("SELECT 42", LoginSettings.FromEnvironment(cfg.Connections["legacy"], Login.Read, Env).Settings!.Dialect.Ping);
        Assert.Equal("SELECT 42", LoginSettings.FromEnvironment(cfg, "legacy", Login.Read, Env).Settings!.Dialect.Ping);
        Assert.Equal("SELECT 1", LoginSettings.FromEnvironment(cfg, "plain", Login.Read, Env).Settings!.Dialect.Ping);
        Assert.Equal("postgres", LoginSettings.FromEnvironment(cfg, "postgres", Login.Read, _ => "Host=x").Settings!.Engine);          // a connection named after an engine needs no declaration
    }

    // ---- binding ----

    [Fact]
    public void Values_are_bound_in_the_order_given_with_types_odbc_has_and_a_name_used_twice_is_passed_twice()
    {
        var command = new OdbcCommand();
        OdbcRowSource.BindPositional(command, [
            new GateParameter("p_region", DbType.String, "EU"),
            new GateParameter("watermark", DbType.DateTime2, new DateTime(2026, 3, 1, 4, 5, 6, DateTimeKind.Unspecified)),
            new GateParameter("p_region", DbType.String, "EU"),
            new GateParameter("day", DbType.Date, new DateTime(2026, 3, 1)),
            new GateParameter("n", DbType.Int64, 7L),
            new GateParameter("none", DbType.String, null),
            new GateParameter("price", DbType.Decimal, 12.340m),
        ]);
        var bound = command.Parameters.Cast<OdbcParameter>().ToList();
        Assert.Equal(["EU", new DateTime(2026, 3, 1, 4, 5, 6), "EU", new DateTime(2026, 3, 1), 7L, DBNull.Value, 12.340m], bound.Select(p => p.Value).ToArray());
        Assert.Equal([OdbcType.NVarChar, OdbcType.DateTime, OdbcType.NVarChar, OdbcType.Date, OdbcType.BigInt, OdbcType.NVarChar, OdbcType.Numeric], bound.Select(p => p.OdbcType).ToArray());
        Assert.Equal(3, bound[6].Scale);
        Assert.True(bound[5].Size > 0);                                                       // a NULL still has a size, or the driver refuses the parameter
    }

    [Fact]
    public void A_positional_session_hands_the_values_to_the_source_untouched_and_in_order()
    {
        var source = new RecordingSource();
        var session = ReadSession.ForTesting(source, "odbc");
        session.QueryAsync("SELECT id FROM t WHERE a = ? AND (b = ? OR c = ?)", [new("a", DbType.String, "x"), new("b", DbType.String, "y"), new("a", DbType.String, "x")]).GetAwaiter().GetResult();
        Assert.Equal(["x", "y", "x"], source.Last!.Select(p => p.Value).ToArray());
        Assert.Equal("ansi", session.Dialect.Name);
        Assert.Equal(DialectProfiles.Ansi.Catalog, session.Dialect.Catalog);
    }

    private sealed class RecordingSource : IRowSource
    {
        public List<string> Texts { get; } = [];
        public IReadOnlyList<GateParameter>? Last { get; private set; }
        public Func<string, IReadOnlyList<IReadOnlyList<object?>>>? Rows { get; init; }

        public Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct)
        {
            Texts.Add(sql); Last = parameters;
            return Task.FromResult<IRowReader>(new Reader(Rows?.Invoke(sql) ?? []));
        }

        public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => Task.FromResult<IReadOnlyList<ColumnShape>?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Reader(IReadOnlyList<IReadOnlyList<object?>> rows) : IRowReader
        {
            private int at = -1;
            public IReadOnlyList<string> Names => Enumerable.Range(0, rows.Count > 0 ? rows[0].Count : 1).Select(i => "c" + i).ToList();
            public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++at < rows.Count);
            public ValueTask<object?> ValueAsync(int column, CancellationToken ct) => ValueTask.FromResult(rows[at][column]);
            public void Cancel() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // ---- the catalog through information_schema ----

    [Fact]
    public async Task The_catalog_of_an_odbc_source_is_read_through_information_schema_with_values_by_position()
    {
        var source = new RecordingSource
        {
            Rows = sql => sql.Contains("information_schema.columns") ? new List<IReadOnlyList<object?>>
            {
                new object?[] { "items", "BASE TABLE", 1, "id", "bigint", null, 64, 0, "NO" },
                new object?[] { "items", "BASE TABLE", 2, "price", "numeric", null, 18, 3, "YES" },
                new object?[] { "items", "BASE TABLE", 3, "code", "character varying", 40, null, null, "YES" },
                new object?[] { "items_v", "VIEW", 1, "id", "bigint", null, 64, 0, "NO" },
            } : [],
        };
        await using var session = ReadSession.ForTesting(source, "odbc");
        var shapes = await CatalogReader.ReadObjectsAsync(session, "odbc", "src");
        Assert.Equal(["src.items", "src.items_v"], shapes.Keys.Order().ToArray());
        Assert.Equal(ObjectKind.View, shapes["src.items_v"].Kind);
        Assert.Equal(["src"], source.Last!.Select(p => p.Value).ToArray());                   // one parameter: the schema
        Assert.DoesNotContain("@", source.Texts[0]);
        Assert.Contains("t.table_schema = ?", source.Texts[0]);
        var price = shapes["src.items"].Columns[1];
        Assert.Equal(("numeric", 18, 3, true), (price.Type, price.Precision, price.Scale, price.Nullable));

        await CatalogReader.ReadObjectsAsync(session, "odbc", "src", objectName: "items");
        Assert.Equal(["src", "items"], source.Last!.Select(p => p.Value).ToArray());          // schema, then the object, in the order the ?s appear
        Assert.EndsWith("AND t.table_name = ? ORDER BY t.table_name, c.ordinal_position", source.Texts[^1].Replace("\n", " ").Replace("  ", " "));

        // the keys and the schema names ask the standard views, one value by position
        await SourceCatalogReader.PrimaryKeysAsync(session, "odbc", "src");
        Assert.Contains("information_schema.table_constraints", source.Texts[^1]);
        Assert.Equal(["src"], source.Last!.Select(p => p.Value).ToArray());
        await SourceCatalogReader.ForeignKeysAsync(session, "odbc", "src");
        Assert.Contains("information_schema.referential_constraints", source.Texts[^1]);
        await SourceCatalogReader.SchemasAsync(session, "odbc");
        Assert.Contains("information_schema.schemata", source.Texts[^1]);
    }

    [Fact]
    public async Task A_dialect_that_says_catalog_none_is_never_asked_for_its_tables()
    {
        var source = new RecordingSource();
        var none = DialectProfiles.Build("blind", DialectProfiles.Ansi, "ansi", null, null, null, "none", null, null, null).Profile!;
        await using var session = ReadSession.ForTesting(source, "odbc");
        Assert.Equal(CatalogStyle.InformationSchema, session.Dialect.Catalog);
        await using var blind = ReadSession.ForTesting(source, "odbc", none);
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => CatalogReader.ReadObjectsAsync(blind, "odbc", "src"));
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => SourceCatalogReader.SchemasAsync(blind, "odbc"));
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => SourceCatalogReader.PrimaryKeysAsync(blind, "odbc", "src"));
        Assert.Empty(source.Texts);                                                           // nothing was sent: the tool does not guess
    }

    [Fact]
    public void Project_import_says_plainly_that_a_dialect_without_a_catalog_cannot_be_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-odbc-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(0, Cli(["project", "create", "starter", dir]).Exit);
        var config = Path.Combine(dir, "dbdatabuild.yml");
        File.WriteAllText(config, "dialects:\n  blind: { extends: ansi, catalog: none }\n" + File.ReadAllText(config).Replace("connections:\n  sqlserver:", "connections:\n  legacy: { engine: readonly odbc blind }\n  sqlserver:"));
        var r = Cli(["project", "import", "legacy", "src.*", "--project", dir], v => v == "DBDATABUILD_LEGACY_READ" ? "Driver={No Such ODBC Driver};PWD=secretpw" : null);
        Assert.Equal(1, r.Exit);
        Assert.Contains("`catalog: none`", r.Err);
        Assert.Contains("not guessed", r.Err);
        Assert.DoesNotContain("secretpw", r.Out + r.Err);
    }

    // ---- the types of a profile ----

    [Fact]
    public void The_standard_names_of_information_schema_map_to_logical_types_and_a_profile_adds_its_own()
    {
        ImportedType Of(string type, int? length = null, int? precision = null, int? scale = null, DialectProfile? profile = null) =>
            ImportedTypes.From("odbc", new ColumnShape("c", type, length, precision, scale, true, null, null), profile);
        Assert.Equal("BIGINT", Of("bigint").LogicalType);
        Assert.Equal("DOUBLE", Of("double precision").LogicalType);
        Assert.Equal("DECIMAL(18, 3)", Of("numeric", precision: 18, scale: 3).LogicalType);
        Assert.Equal(TypeFit.None, Of("numeric").Fit);                                              // a decimal with no precision has no logical type
        Assert.Equal("VARCHAR(40)", Of("character varying", length: 40).LogicalType);
        Assert.Equal("VARCHAR", Of("text").LogicalType);
        Assert.Equal(TypeFit.Widened, Of("character", length: 3).Fit);                              // fixed length: padding is not part of the logical type
        Assert.Equal("TIMESTAMP", Of("timestamp without time zone").LogicalType);
        Assert.Equal("TIMESTAMP WITH TIME ZONE", Of("timestamp with time zone").LogicalType);
        var bytea = Of("bytea");
        Assert.Null(bytea.LogicalType);                                                                  // said, not guessed
        Assert.Contains("types:", bytea.Reason);

        var mine = DialectProfiles.Build("mine", DialectProfiles.Ansi, "ansi", null, null, null, null, null, null, new Dictionary<string, string> { ["bytea"] = "BLOB", ["int8"] = "BIGINT" }).Profile!;
        Assert.Equal("BLOB", Of("bytea", profile: mine).LogicalType);
        Assert.Equal("BIGINT", Of("int8", profile: mine).LogicalType);
        Assert.Equal("BIGINT", Of("bigint", profile: mine).LogicalType);                                 // what ansi had stays
    }

    [Fact]
    public void Types_the_driver_reports_have_the_standard_names_the_profile_maps()
    {
        Assert.Equal("bigint", OdbcRowSource.OdbcTypeName(OdbcType.BigInt));
        Assert.Equal("smallint", OdbcRowSource.OdbcTypeName(OdbcType.TinyInt));
        Assert.Equal("decimal", OdbcRowSource.OdbcTypeName(OdbcType.Numeric));
        Assert.Equal("character varying", OdbcRowSource.OdbcTypeName(OdbcType.NVarChar));
        Assert.Equal("timestamp", OdbcRowSource.OdbcTypeName(OdbcType.DateTime));
        Assert.Equal("blob", OdbcRowSource.OdbcTypeName(OdbcType.Binary));
        Assert.Equal("", OdbcRowSource.OdbcTypeName((OdbcType)999));
        foreach (var name in new[] { "bigint", "integer", "smallint", "boolean", "decimal", "double precision", "real", "date", "time", "timestamp", "character", "character varying", "text", "blob", "uuid" })
            Assert.True(DialectProfiles.Ansi.Types.ContainsKey(name), $"the ansi profile maps `{name}`");
    }

    [Theory]
    [InlineData("t", true)]
    [InlineData("f", false)]
    [InlineData("TRUE", true)]
    [InlineData(" 0 ", false)]
    [InlineData("Y", true)]
    [InlineData("no", false)]
    public void A_boolean_a_driver_hands_over_as_text_is_read(string text, bool expected) =>
        Assert.Equal(expected, TransferValues.Convert(text, new TransferColumn("flag", "BOOLEAN")));

    [Fact]
    public void Text_that_is_not_a_boolean_is_an_error_that_names_the_column_and_not_the_value() =>
        Assert.DoesNotContain("maybe", Assert.Throws<TransferException>(() => TransferValues.Convert("maybe", new TransferColumn("flag", "BOOLEAN"))).Message);

    // ---- a missing prerequisite ----

    [Fact]
    public void A_missing_driver_manager_is_named_as_such()
    {
        var manager = new DllNotFoundException("Unable to load shared library 'libodbc.so.2' at /secret/path");
        var text = DriverFailure.Describe(manager);
        Assert.Contains("ODBC driver manager is not installed", text);
        Assert.Contains("unixODBC", text);
        Assert.DoesNotContain("/secret/path", text);
        // it may arrive wrapped, as the type initializer of the provider
        Assert.Equal(text, DriverFailure.Describe(new TypeInitializationException("System.Data.Odbc.OdbcEnvironment", manager)));
        Assert.Equal(text, DriverFailure.Describe(new InvalidOperationException("outer", new TypeInitializationException("x", manager))));
        Assert.Null(DriverFailure.Prerequisite(new InvalidOperationException("boom")));
        Assert.Equal("InvalidOperationException", DriverFailure.Describe(new InvalidOperationException("password=hunter2")));
    }

    [Fact]
    public void A_driver_or_data_source_the_login_names_that_is_not_there_is_named_without_the_login()
    {
        // the real driver manager, when the machine has one: a name that is neither a driver nor a DSN. Without one, the manager is what is reported.
        const string secret = "ddb_no_such_data_source_qq";
        string text;
        try { using var c = new OdbcConnection($"DSN={secret};UID=reader;PWD=hunter2"); c.Open(); return; }
        catch (Exception ex) { text = DriverFailure.Describe(ex); }
        Assert.True(text.Contains("ODBC driver manager is not installed") || text.Contains("ODBC driver or data source named in the login was not found"), text);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("reader", text);
    }

    [Fact]
    public void Inspect_and_the_origin_check_of_a_copy_say_which_prerequisite_is_missing_and_quote_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-odbc-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(0, Cli(["project", "create", "starter", dir]).Exit);
        var config = Path.Combine(dir, "dbdatabuild.yml");
        File.WriteAllText(config, File.ReadAllText(config).Replace("connections:\n  sqlserver:", "connections:\n  legacy: { engine: readonly odbc }\n  sqlserver:"));
        string? Env(string v) => v == "DBDATABUILD_LEGACY_READ" ? "Driver={Vendor Driver 9000};Server=db.internal;UID=reader;PWD=hunter2" : null;
        var inspect = Cli(["connection", "inspect", "legacy", "--project", dir], Env);
        Assert.NotEqual(0, inspect.Exit);
        Assert.Contains("ODBC", inspect.Out);
        Assert.Matches("does not connect \\(the ODBC driver", inspect.Out);                                // the driver manager's or the driver's absence, in words
        Assert.DoesNotContain("hunter2", inspect.Out + inspect.Err);
        Assert.DoesNotContain("db.internal", inspect.Out + inspect.Err);
        Assert.DoesNotContain("Vendor Driver 9000", inspect.Out + inspect.Err);
        var json = Cli(["connection", "inspect", "legacy", "--project", dir, "--format", "json"], Env);
        Assert.DoesNotContain("hunter2", json.Out);
    }

    private static (int Exit, string Out, string Err) Cli(string[] args, Func<string, string?>? env = null)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, environment: env ?? (_ => null));
        return (exit, o.ToString(), e.ToString());
    }
}
