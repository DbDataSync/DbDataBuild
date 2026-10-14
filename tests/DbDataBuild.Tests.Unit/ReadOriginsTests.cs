using System.Data;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Targets;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Read-only origins, step 1 (docs/research/read-origins.md): the keywords of `engine`, `dialects:`, `readonly`, the row source under the read session, and the origin's quoting and parameter style in a copy.</summary>
public class ReadOriginsTests
{
    private static ProjectConfig? Load(string yaml, out List<Diagnostic> diags)
    {
        diags = [];
        return ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags);
    }

    // ---- the keywords ----

    public static TheoryData<string, string, string, string> Accepted => new()
    {
        { "postgres", "ReadWrite", "postgres", "postgres" },
        { "readonly postgres", "ReadOnly", "postgres", "postgres" },
        { "postgres readonly", "ReadOnly", "postgres", "postgres" },
        { "readonly, sqlserver", "ReadOnly", "sqlserver", "sqlserver" },
        { "readonly fabric", "ReadOnly", "fabric", "fabric" },
        { "readonly duckdb", "ReadOnly", "duckdb", "duckdb" },
        { "readonly odbc", "ReadOnly", "odbc", "ansi" },
        { "readonly odbc ansi", "ReadOnly", "odbc", "ansi" },
        { "ansi odbc readonly", "ReadOnly", "odbc", "ansi" },
        { "readonly odbc postgres", "ReadOnly", "odbc", "postgres" },          // postgres is the dialect when the connection has a driver of its own
        { "readonly,odbc,duckdb", "ReadOnly", "odbc", "duckdb" },
        { "readonly flightsql backtick_sql", "ReadOnly", "flightsql", "backtick_sql" },
    };

    [Theory, MemberData(nameof(Accepted))]
    public void Keywords_are_sorted_into_access_driver_and_dialect_in_any_order(string engine, string access, string driver, string dialect)
    {
        var (kind, error, _) = ConnectionKeywords.Parse(ConnectionKeywords.Words(engine), new HashSet<string> { "backtick_sql" });
        Assert.Null(error);
        Assert.Equal(new ConnectionKind(Enum.Parse<ConnectionAccess>(access), driver, dialect) with { }, kind! with { DialectDeclared = true });
        // odbc and flightsql with no dialect keyword leave the dialect undeclared: ansi, or (Flight SQL) what `project update` committed
        var named = ConnectionKeywords.Words(engine).Any(w => w != "readonly" && w != driver);
        Assert.Equal(named || driver is not ("odbc" or "flightsql"), kind!.DialectDeclared);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "", "empty" },
        { "readonly", "names no driver" },
        { "oracle", "not a keyword" },
        { "postgres postgres", "repeated" },
        { "readonly readonly postgres", "repeated" },
        { "readonly odbc flightsql", "two drivers" },
        { "readonly postgres ansi", "already has a dialect" },
        { "readonly duckdb ansi", "already has a dialect" },
        { "readonly sqlserver postgres", "already has a dialect" },
        { "odbc", "needs `readonly`" },
        { "duckdb", "needs `readonly`" },
        { "flightsql ansi", "needs `readonly`" },
        { "readonly odbc ansi duckdb", "two dialects" },
    };

    [Theory, MemberData(nameof(Refused))]
    public void A_keyword_that_breaks_a_rule_is_refused_with_the_choices(string engine, string why)
    {
        var (kind, error, fix) = ConnectionKeywords.Parse(ConnectionKeywords.Words(engine), new HashSet<string>());
        Assert.Null(kind);
        Assert.Contains(why, error!);
        Assert.False(string.IsNullOrWhiteSpace(fix));
    }

    [Fact]
    public void The_loader_parses_engine_once_and_leaves_plain_engines_as_they_were()
    {
        var cfg = Load("""
            connections:
              shop_east: { engine: readonly postgres }
              shop_west: { engine: [readonly, postgres] }
              warehouse: { engine: sqlserver }
              plain: { engine: "postgres" }
            """, out var diags)!;
        Assert.Empty(diags);
        Assert.True(cfg.Connections["shop_east"].ReadOnly);
        Assert.Equal("postgres", cfg.Connections["shop_east"].Engine);       // the driver, which the rest of the code reads
        Assert.True(cfg.Connections["shop_west"].ReadOnly);
        Assert.False(cfg.Connections["warehouse"].ReadOnly);
        Assert.Equal(ConnectionKind.OfEngine("postgres"), cfg.Connections["plain"].Kind);
        Assert.Equal(ConnectionKind.OfEngine("sqlserver"), cfg.Connections["sqlserver"].Kind);              // undeclared, named after the engine
        Assert.Contains("shop_east (readonly postgres)", cfg.Describe());
        Assert.Contains("warehouse (sqlserver)", cfg.Describe());               // as before
    }

    [Theory]
    [InlineData("connections:\n  x: { engine: oracle }\n", "not a keyword")]
    [InlineData("connections:\n  x: { engine: readonly postgres ansi }\n", "already has a dialect")]
    [InlineData("connections:\n  x: { engine: duckdb }\n", "needs `readonly`")]
    [InlineData("connections:\n  x: { engine: [] }\n", "list of keywords")]
    [InlineData("connections:\n  x: { engine: readonly postgres, allow_native_commands: true }\n", null)]
    [InlineData("connections:\n  postgres: { engine: sqlserver }\n", "named after an engine")]
    [InlineData("connections:\n  x: { engine: readonly duckdb, allow_native_commands: true }\n", "SQL Server and PostgreSQL connections")]
    [InlineData("connections:\n  x: { engine: readonly flightsql }\n", null)]
    public void A_bad_engine_is_a_diagnostic_that_names_the_choices(string yaml, string? expected)
    {
        Load(yaml, out var diags);
        if (expected == null) { Assert.Empty(diags); return; }
        var d = Assert.Single(diags);
        Assert.Equal("DDB-106", d.Code);
        Assert.Contains(expected, d.Found);
    }

    [Fact]
    public void Only_the_engines_the_tool_builds_on_are_build_targets()
    {
        Assert.True(ConnectionKind.OfEngine("postgres").IsBuildTarget);
        Assert.False(new ConnectionKind(ConnectionAccess.ReadOnly, "duckdb", "duckdb").IsBuildTarget);
    }

    // ---- dialects ----

    [Fact]
    public void Dialects_start_from_a_base_and_change_what_they_name()
    {
        var cfg = Load("""
            dialects:
              backtick_sql:
                extends: ansi
                quote: '`'
                ping: SELECT 1 AS ok
                types: { int8: BIGINT }
              proxy_two:
                extends: backtick_sql
                parameters: numbered
                limit: top
                identifier_case: upper
                catalog: none
              bare: {}
            connections:
              legacy: { engine: sqlserver }
            """, out var diags)!;
        Assert.Empty(diags);
        var proxy = cfg.Dialects["backtick_sql"];
        Assert.Equal(("`", "`", ParameterStyle.Positional, "SELECT 1 AS ok"), (proxy.QuoteOpen, proxy.QuoteClose, proxy.Parameters, proxy.Ping));
        Assert.Equal("BIGINT", proxy.Types["int8"]);
        Assert.Equal("INTEGER", proxy.Types["integer"]);                                   // what it extends is kept
        var two = cfg.Dialects["proxy_two"];
        Assert.Equal(("`", ParameterStyle.Numbered, LimitStyle.Top, IdentifierCase.Upper, CatalogStyle.None, "backtick_sql"), (two.QuoteOpen, two.Parameters, two.Limit, two.Case, two.Catalog, two.Extends));
        Assert.Equal("SELECT 1 AS ok", two.Ping);
        var bare = cfg.Dialects["bare"];
        Assert.Equal(("bare", "ansi", DialectProfiles.Ansi.Ping, DialectProfiles.Ansi.Parameters), (bare.Name, bare.Extends, bare.Ping, bare.Parameters));
        Assert.Equal(DialectProfiles.Ansi.Types, bare.Types);
        Assert.Equal("`x``y`", proxy.Quote("x`y"));
    }

    [Theory]
    [InlineData("dialects:\n  postgres: {}\n", "keyword of `engine`")]
    [InlineData("dialects:\n  readonly: {}\n", "keyword of `engine`")]
    [InlineData("dialects:\n  ansi: {}\n", "keyword of `engine`")]
    [InlineData("dialects:\n  Proxy: {}\n", "not a valid dialect name")]
    [InlineData("dialects:\n  p: { extends: nowhere }\n", "not a dialect")]
    [InlineData("dialects:\n  p: { extends: q }\n  q: { extends: p }\n", "extends itself")]
    [InlineData("dialects:\n  p: { quote: '#' }\n", "`quote`")]
    [InlineData("dialects:\n  p: { parameters: colon }\n", "`parameters`")]
    [InlineData("dialects:\n  p: { catalog: sys }\n", "`catalog`")]
    [InlineData("dialects:\n  p: { identifier_case: mixed }\n", "`identifier_case`")]
    [InlineData("dialects:\n  p: { limit: rownum }\n", "`limit`")]
    [InlineData("dialects:\n  p: { ping: 'DELETE FROM t' }\n", "`ping`")]
    [InlineData("dialects:\n  p: { types: { x: STRUCT } }\n", "`types.x`")]
    [InlineData("dialects:\n  p: { types: nope }\n", "must map")]
    [InlineData("dialects:\n  p: { colour: red }\n", null)]
    [InlineData("dialects: nope\n", "must map dialect names")]
    public void An_invalid_profile_is_a_diagnostic_at_load(string yaml, string? expected)
    {
        var cfg = Load(yaml, out var diags);
        Assert.Null(cfg);
        if (expected == null) { Assert.Contains(diags, d => d.Code == "DDB-104"); return; }
        Assert.Contains(diags, d => d.Code == "DDB-456" && d.Found.Contains(expected));
    }

    [Fact]
    public void A_connection_may_name_a_dialect_of_the_project_but_the_engines_that_bring_their_own_may_not()
    {
        var cfg = Load("dialects:\n  backtick_sql: { quote: '`' }\nconnections:\n  a: { engine: readonly postgres }\n", out var diags)!;
        Assert.Empty(diags);
        Assert.Equal("postgres", cfg.DialectOf("a").Name);
        Load("dialects:\n  backtick_sql: { quote: '`' }\nconnections:\n  a: { engine: readonly postgres backtick_sql }\n", out diags);
        Assert.Contains(diags, d => d.Found.Contains("already has a dialect"));
    }

    [Fact]
    public void The_built_in_profiles_quote_exactly_as_the_targets_do()
    {
        var config = ProjectConfig.Default;
        foreach (var engine in TargetNames.All)
        {
            var ddl = TargetRegistry.Get(engine).CreateDdl(config);
            var profile = DialectProfiles.OfEngine(engine)!;
            foreach (var name in new[] { "plain", "with space", "has]bracket", "has\"quote", "Mixed_Case1" })
            {
                Assert.Equal(ddl.QuoteIdentifier(name), profile.Quote(name));
                Assert.Equal(ddl.Qualified("sch", name), profile.Qualified("sch", name));
            }
        }
        Assert.Equal(ParameterStyle.NamedAt, DialectProfiles.SqlServer.Parameters);
        Assert.Equal(ParameterStyle.NamedAt, DialectProfiles.Postgres.Parameters);
        Assert.Equal(("\"", ParameterStyle.Positional, "SELECT 1", CatalogStyle.InformationSchema, LimitStyle.Fetch), (DialectProfiles.Ansi.QuoteOpen, DialectProfiles.Ansi.Parameters, DialectProfiles.Ansi.Ping, DialectProfiles.Ansi.Catalog, DialectProfiles.Ansi.Limit));
    }

    // ---- readonly ----

    [Fact]
    public void A_readonly_connection_cannot_be_the_tracking_connection()
    {
        Load("connections:\n  shop: { engine: readonly postgres }\ntracking: { connection: shop }\n", out var diags);
        Assert.Contains(diags, d => d.Code == "DDB-455" && d.Found.Contains("`shop`"));
        // declared after the setting that names it, and as a connection's own tracking
        Load("connections:\n  a: { engine: sqlserver, tracking: { connection: shop } }\n  shop: { engine: readonly postgres }\n", out diags);
        Assert.Contains(diags, d => d.Code == "DDB-455");
        Load("connections:\n  shop: { engine: readonly postgres }\ntracking: none\n", out diags);
        Assert.Empty(diags);
    }

    [Fact]
    public void A_readonly_connection_cannot_be_the_destination_of_a_model_or_a_copy_but_may_be_the_origin_of_one()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "connections:\n  shop: { engine: readonly postgres }\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel.Replace("connections: [sqlserver, fabric]", "connections: [sqlserver, shop]"));
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1 AS order_id");
        var result = ProjectValidator.Validate(dir);
        Assert.Contains(result.Diagnostics, d => d.Code == "DDB-455" && d.Found.Contains("built on the connection `shop`") && d.Location.File == "models/marts/fct_orders.yml");

        // a mapped model that reads it, and a copy of that, is fine
        var ok = NewProjectDir();
        File.WriteAllText(Path.Combine(ok, "dbdatabuild.yml"), "connections:\n  shop: { engine: readonly postgres }\n");
        File.WriteAllText(Path.Combine(ok, "models/marts/fct_orders.yml"), ValidModel.Replace("connections: [sqlserver, fabric]", "connections: [sqlserver]"));
        File.WriteAllText(Path.Combine(ok, "models/marts/fct_orders.sql"), "SELECT 1 AS order_id");
        Assert.DoesNotContain(ProjectValidator.Validate(ok).Diagnostics, d => d.Code == "DDB-455");
    }

    [Fact]
    public void A_readonly_connection_has_no_write_login_and_the_environment_is_not_asked()
    {
        var cfg = Load("connections:\n  shop: { engine: readonly postgres }\n  warehouse: { engine: sqlserver }\n", out _)!;
        var asked = new List<string>();
        string? Env(string name) { asked.Add(name); return "Host=x"; }
        var (write, refused) = LoginSettings.FromEnvironment(cfg.Connections["shop"], Login.Write, Env);
        Assert.Null(write);
        Assert.Equal("DDB-455", refused!.Code);
        Assert.Empty(asked);                                                                    // no _WRITE variable is read
        var (read, _) = LoginSettings.FromEnvironment(cfg.Connections["shop"], Login.Read, Env);
        Assert.True(read!.ReadOnly);
        Assert.Equal("DBDATABUILD_SHOP_READ", read.Variable);
        var (warehouseWrite, none) = LoginSettings.FromEnvironment(cfg.Connections["warehouse"], Login.Write, Env);
        Assert.Null(none);
        Assert.False(warehouseWrite!.ReadOnly);
    }

    [Fact]
    public void Commands_that_write_or_build_refuse_a_readonly_connection_and_the_ones_that_read_take_it()
    {
        var cfg = Load("connections:\n  shop: { engine: readonly postgres }\n", out _)!;
        var error = new StringWriter();
        Assert.Null(DbDataBuild.Cli.CommandTargets.Resolve(cfg, "shop", error));
        Assert.Contains("DDB-455", error.ToString());
        var okError = new StringWriter();
        Assert.Equal("shop", DbDataBuild.Cli.CommandTargets.Resolve(cfg, "shop", okError, readOnlyOk: true)!.Name);
        Assert.Equal("", okError.ToString());
    }

    private static (int Exit, string Out, string Err) Cli(string[] args, Func<string, string?>? env = null)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, environment: env ?? (_ => null));
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void The_commands_that_write_refuse_a_readonly_connection_and_inspect_says_it_has_no_write_login()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-readonly-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(0, Cli(["project", "create", "starter", dir]).Exit);
        var config = Path.Combine(dir, "dbdatabuild.yml");
        File.WriteAllText(config, File.ReadAllText(config).Replace("connections:\n  sqlserver:", "connections:\n  shop: { engine: readonly postgres }\n  sqlserver:"));
        string? Env(string v) => v == "DBDATABUILD_SHOP_READ" ? "Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=1" : null;
        foreach (var command in new[] { new[] { "connection", "deploy", "shop", "--write-plan" }, ["connection", "refresh", "shop"], ["connection", "seed", "shop"], ["connection", "init", "shop"], ["connection", "publish", "shop"] })
        {
            var r = Cli([.. command, "--project", dir], Env);
            Assert.NotEqual(0, r.Exit);
            Assert.True(r.Err.Contains("DDB-455"), $"{string.Join(' ', command)}: {r.Err}");
        }
        var inspect = Cli(["connection", "inspect", "shop", "--project", dir], Env);
        Assert.Contains("engine readonly postgres", inspect.Out);
        Assert.Contains("write login   none: the connection is readonly, so no write login is read", inspect.Out);
        Assert.DoesNotContain("DBDATABUILD_SHOP_WRITE", inspect.Out);
    }

    // ---- parameters ----

    [Fact]
    public void Placeholders_are_rewritten_for_the_dialect_and_their_order_recorded()
    {
        var names = new[] { "a", "b" };
        var text = "SELECT x FROM t WHERE p = @a AND q = @b AND r = @a AND note = '@a' -- @b\n AND \"@b\" = 1 AND z = @other";
        var (positional, order) = ParameterPlaceholders.Rewrite(text, names, DialectProfiles.Ansi);
        Assert.Equal("SELECT x FROM t WHERE p = ? AND q = ? AND r = ? AND note = '@a' -- @b\n AND \"@b\" = 1 AND z = @other", positional);
        Assert.Equal(["a", "b", "a"], order);                                                   // a placeholder used twice is passed twice
        var numbered = DialectProfiles.Ansi with { Parameters = ParameterStyle.Numbered };
        var (n, nOrder) = ParameterPlaceholders.Rewrite("WHERE p = @a AND q = @b AND r = @a", names, numbered);
        Assert.Equal("WHERE p = $1 AND q = $2 AND r = $1", n);
        Assert.Equal(["a", "b"], nOrder);
        var (same, none) = ParameterPlaceholders.Rewrite(text, names, DialectProfiles.Postgres);
        Assert.Equal(text, same);
        Assert.Empty(none);
    }

    /// <summary>The configuration with one more readonly connection of a driver that is not built yet (step 3 and 4 of the design): the planning of its read is data and needs no driver.</summary>
    private static ProjectConfig With(ProjectConfig cfg, string name, string driver, string dialect) =>
        cfg with { Connections = new Dictionary<string, ConnectionConfig>(cfg.Connections) { [name] = new ConnectionConfig(name, driver) { DeclaredKind = new ConnectionKind(ConnectionAccess.ReadOnly, driver, dialect) } } };

    private static ModelDefinition Copy(CopyWatermark? watermark = null) =>
        new("dst.items", ModelKinds.Copy, [], null, null, ["id"], null, [new ColumnDefinition("id", "BIGINT", false), new ColumnDefinition("changed", "TIMESTAMP"), new ColumnDefinition("label", "VARCHAR(20)")], [], From: "src.items", Watermark: watermark);

    [Fact]
    public void A_copy_reads_in_the_origins_dialect_and_a_positional_origin_records_the_order_of_its_values()
    {
        var cfg = Load("dialects:\n  backtick_sql: { extends: ansi, quote: '`' }\nconnections:\n  shop: { engine: readonly postgres }\n", out _)!;
        var def = Copy(new CopyWatermark("changed", null));
        var ddl = TargetRegistry.Get("sqlserver").CreateDdl(cfg);

        // the engines the tool builds on keep their text and bind by name
        var (_, postgres) = Planner.CopyTransfer(cfg, def, ddl, new CopyOrigin("shop", "postgres", "src.items", null, "2024-01-01"));
        Assert.Equal("SELECT \"id\", \"changed\", \"label\" FROM \"src\".\"items\" WHERE \"changed\" >= @watermark", postgres.ReadText);
        Assert.Null(postgres.ParameterOrder);

        // an origin of the ansi dialect: positional
        var origin = new CopyOrigin("legacy", "odbc", "src.items", null, "2024-01-01");
        var ansi = With(cfg, "legacy", "odbc", "ansi");
        var (_, spec) = Planner.CopyTransfer(ansi, def, ddl, origin);
        Assert.Equal("SELECT \"id\", \"changed\", \"label\" FROM \"src\".\"items\" WHERE \"changed\" >= ?", spec.ReadText);
        Assert.Equal(["watermark"], spec.ParameterOrder);
        Assert.Equal("2024-01-01", spec.Watermark!.Value);

        // and one with quoting of its own
        var proxy = With(cfg, "proxy", "flightsql", "backtick_sql");
        var (_, viaProxy) = Planner.CopyTransfer(proxy, def, ddl, new CopyOrigin("proxy", "flightsql", "src.items", null, "x"));
        Assert.Equal("SELECT `id`, `changed`, `label` FROM `src`.`items` WHERE `changed` >= ?", viaProxy.ReadText);
    }

    [Fact]
    public void A_native_origin_with_values_used_twice_is_passed_twice_in_text_order()
    {
        var cfg = Load("connections:\n  shop: { engine: readonly postgres }\n", out _)!;
        var ansi = With(cfg, "legacy", "odbc", "ansi");
        var use = new NativeUse("src.open_items", "SELECT id, changed, label FROM items WHERE region = @p_native_region AND (owner = @p_native_region OR kind = @p_native_kind)",
            [new QueryParameter("native", "region", "VARCHAR(10)", ""), new QueryParameter("native", "kind", "VARCHAR(10)", "")]);
        var values = new Dictionary<string, ParameterValue>
        {
            ["native.region"] = new ParameterValue("EU"),
            ["native.kind"] = new ParameterValue("x"),
        };
        var ddl = TargetRegistry.Get("sqlserver").CreateDdl(ansi);
        var (_, spec) = Planner.CopyTransfer(ansi, Copy(), ddl, new CopyOrigin("legacy", "odbc", "src.open_items", null, null, use, values));
        Assert.Contains("region = ? AND (owner = ? OR kind = ?)", spec.ReadText);
        Assert.Equal(["p_native_region", "p_native_region", "p_native_kind"], spec.ParameterOrder);
    }

    // ---- the row source ----

    private sealed class FakeSource : IRowSource
    {
        public List<(string Sql, IReadOnlyList<GateParameter> Parameters, int? Timeout, bool Streaming)> Executed { get; } = [];
        public bool Disposed { get; private set; }

        public Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct)
        {
            Executed.Add((sql, parameters, timeoutSeconds, streaming));
            return Task.FromResult<IRowReader>(new FakeReader());
        }

        public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DbDataBuild.State.ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => Task.FromResult<IReadOnlyList<DbDataBuild.State.ColumnShape>?>(null);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeReader : IRowReader
    {
        private int at = -1;
        private readonly object?[][] rows = [[1L, "a"], [2L, null], [3L, "c"]];
        public bool Cancelled { get; private set; }
        public IReadOnlyList<string> Names => ["id", "label"];
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++at < rows.Length);
        public ValueTask<object?> ValueAsync(int column, CancellationToken ct) => ValueTask.FromResult(rows[at][column]);
        public void Cancel() => Cancelled = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_read_session_reads_through_any_row_source_and_is_still_guarded()
    {
        var source = new FakeSource();
        await using var session = ReadSession.ForTesting(source, "duckdb");
        var rows = await session.QueryAsync("SELECT id, label FROM t", [new GateParameter("x", DbType.Int32, 1)]);
        Assert.Equal(3, rows.Count);
        Assert.Equal([2L, null], rows[1]);
        Assert.Equal(("SELECT id, label FROM t", 1, null, false), (source.Executed[0].Sql, source.Executed[0].Parameters.Count, source.Executed[0].Timeout, source.Executed[0].Streaming));

        var (names, first) = await session.QueryFirstRowsAsync("SELECT id, label FROM t", 2, 5);
        Assert.Equal(["id", "label"], names);
        Assert.Equal(2, first.Count);
        Assert.Equal(5, source.Executed[1].Timeout);

        await using (var stream = await session.OpenStreamAsync("SELECT id, label FROM t"))
        {
            var streamed = new List<object?[]>();
            await foreach (var row in stream.ReadAsync()) streamed.Add(row);
            Assert.Equal(3, streamed.Count);
            Assert.Equal(["id", "label"], stream.Names);
        }
        Assert.True(source.Executed[2].Streaming);
        Assert.Equal(0, source.Executed[2].Timeout);

        // nothing but a SELECT reaches a source
        await Assert.ThrowsAsync<GateRefusedException>(() => session.QueryAsync("DELETE FROM t"));
        await Assert.ThrowsAsync<GateRefusedException>(() => session.OpenStreamAsync("UPDATE t SET x = 1"));
        Assert.Equal(3, source.Executed.Count);
    }
}
