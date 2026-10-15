using System.Data;
using System.Security.Authentication;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using Grpc.Core;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Read-only origins, step 4 (docs/research/read-origins.md): the Flight SQL origin without a server: the login value's grammar, how a login is described, the token sources, the words for a failure, the Arrow values
/// and types, the parameters of a prepared statement, and the catalog by metadata calls and by `information_schema` on a fake source.
/// </summary>
public class FlightSqlOriginTests
{
    // ---- the login value ----

    [Fact]
    public void A_plain_uri_has_no_credentials_and_a_tls_scheme_asks_for_tls()
    {
        var plain = FlightSqlLogin.Parse("grpc://127.0.0.1:31337");
        Assert.Equal(("grpc", "127.0.0.1", 31337, false, "no credentials"), (plain.Scheme, plain.Host, plain.Port, plain.Tls, plain.AuthenticationDescription));
        Assert.Equal("http://127.0.0.1:31337/", plain.Address.ToString());
        var tls = FlightSqlLogin.Parse("grpc+tls://proxy.example.com:443;token=abc.def_ghi==");
        Assert.True(tls.Tls);
        Assert.Equal("https://proxy.example.com/", tls.Address.ToString());
        Assert.Equal("abc.def_ghi==", tls.Token);                                                     // a base64 padding is part of a value
        Assert.Equal("proxy.example.com:443", tls.HostAndPort);
        Assert.False(FlightSqlLogin.Parse("grpc+tcp://h:1").Tls);
        Assert.Equal("[::1]:31337", FlightSqlLogin.Parse("grpc://[::1]:31337").HostAndPort);
    }

    [Fact]
    public void The_options_are_read_with_braces_for_a_value_that_holds_semicolons_and_equals()
    {
        var login = FlightSqlLogin.Parse("grpc+tls://h:31337;user=reader;password={p;w=}}x};tls_skip_verify=true;header.X-Database=sales;header.x-team={a b}");
        Assert.Equal(("reader", "p;w=}x", true), (login.User, login.Password, login.TlsSkipVerify));
        Assert.Equal([("x-database", "sales"), ("x-team", "a b")], login.Headers.ToArray());          // header names are lower case, as gRPC has them
        Assert.Equal("user reader", login.AuthenticationDescription);
        var ca = FlightSqlLogin.Parse("grpc+tls://h:31337;ca_file=/etc/ssl/my-ca.pem;token_env=PROXY_TOKEN");
        Assert.Equal(("/etc/ssl/my-ca.pem", "PROXY_TOKEN", false), (ca.CaFile, ca.TokenEnv, ca.TlsSkipVerify));
        Assert.Equal("bearer token from the environment", ca.AuthenticationDescription);
        Assert.Equal("bearer token from a command", FlightSqlLogin.Parse("grpc://h:1;token_command={az account get-access-token}").AuthenticationDescription);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just-a-word")]
    [InlineData("http://h:31337")]
    [InlineData("grpc://h")]                                                                         // no port
    [InlineData("grpc://h:31337/path")]
    [InlineData("grpc://user:pw@h:31337")]
    [InlineData("grpc://h:1;token=a;user=u;password=p")]                                             // two ways to authenticate
    [InlineData("grpc://h:1;token=a;token_env=X")]
    [InlineData("grpc://h:1;user=u")]                                                                // a user without a password
    [InlineData("grpc://h:1;password=p")]
    [InlineData("grpc://h:1;token=")]
    [InlineData("grpc://h:1;token=a;token=b")]
    [InlineData("grpc://h:1;nonsense=1")]
    [InlineData("grpc://h:1;tls_skip_verify=true")]                                                  // a TLS option without TLS
    [InlineData("grpc+tls://h:1;tls_skip_verify=maybe")]
    [InlineData("grpc+tls://h:1;tls_skip_verify=true;ca_file=x.pem")]
    public void A_value_that_does_not_follow_the_grammar_is_refused_in_a_sentence_that_repeats_none_of_it(string value)
    {
        var ex = Assert.Throws<FlightSqlLoginException>(() => FlightSqlLogin.Parse(value));
        Assert.Contains("Flight SQL login", ex.Message);
        Assert.Contains("grpc+tls://host:port", ex.Message);
        foreach (var part in value.Split(';', '/', ':', '@').Where(p => p.Length > 3 && p is not ("grpc" or "grpc+tls" or "password" or "token" or "path" or "user")))
            Assert.DoesNotContain(part, ex.Message.Replace("tls_skip_verify", "").Replace("ca_file", "").Replace("token_env", ""));
    }

    [Fact]
    public void A_login_is_described_by_its_variable_host_and_way_of_signing_in_and_never_by_its_value()
    {
        var cfg = ProjectConfigLoader.Load("connections:\n  proxy: { engine: readonly flightsql }\n", "dbdatabuild.yml", [])!;
        Assert.Equal("ansi", cfg.Connections["proxy"].Kind.Dialect);
        var (login, _) = LoginSettings.FromEnvironment(cfg.Connections["proxy"], Login.Read, v => v == "DBDATABUILD_PROXY_READ" ? "grpc+tls://proxy.internal:31337;user=reader;password=hunter2" : null);
        Assert.Equal("flightsql", login!.Engine);
        Assert.Equal("reader", login.User);
        Assert.Equal("Flight SQL proxy.internal:31337, user reader (DBDATABUILD_PROXY_READ)", login.Describe());
        Assert.DoesNotContain("hunter2", login.Describe());
        var (token, _) = LoginSettings.FromEnvironment(cfg.Connections["proxy"], Login.Read, _ => "grpc://10.0.0.5:31337;token=eyJhbGciOiJIUzI1NiJ9.secret");
        Assert.Equal("Flight SQL 10.0.0.5:31337, bearer token (DBDATABUILD_PROXY_READ)", token!.Describe());
        Assert.Null(token.User);
        var (broken, _) = LoginSettings.FromEnvironment(cfg.Connections["proxy"], Login.Read, _ => "nonsense;password=hunter2");
        Assert.DoesNotContain("hunter2", broken!.Describe());
        Assert.Contains("not a valid Flight SQL URI", broken.Describe());
        // there is no write login for it, whichever way it is asked for
        Assert.Equal("DDB-455", LoginSettings.FromEnvironment(cfg.Connections["proxy"], Login.Write, _ => "x").Error!.Code);
    }

    [Fact]
    public void A_dialect_of_the_project_reaches_the_flight_sql_login()
    {
        var cfg = ProjectConfigLoader.Load("dialects:\n  backtick_sql: { extends: ansi, quote: '`', ping: SELECT 42 }\nconnections:\n  proxy: { engine: readonly flightsql backtick_sql }\n  duck: { engine: readonly flightsql duckdb }\n", "dbdatabuild.yml", [])!;
        string? Env(string _) => "grpc://h:1";
        Assert.Equal("SELECT 42", LoginSettings.FromEnvironment(cfg, "proxy", Login.Read, Env).Settings!.Dialect.Ping);
        Assert.Equal("duckdb", LoginSettings.FromEnvironment(cfg, "duck", Login.Read, Env).Settings!.Dialect.Name);
    }

    // ---- the token sources ----

    [Fact]
    public async Task A_token_comes_from_the_login_the_environment_or_a_command()
    {
        Assert.Equal("literal", await FlightSqlLogin.Parse("grpc://h:1;token=literal").ResolveTokenAsync(default));
        Assert.Null(await FlightSqlLogin.Parse("grpc://h:1").ResolveTokenAsync(default));
        Assert.Null(await FlightSqlLogin.Parse("grpc://h:1;user=u;password=p").ResolveTokenAsync(default));          // the handshake trades these for one
        Assert.Equal("from-env", await FlightSqlLogin.Parse("grpc://h:1;token_env=MY_TOKEN").ResolveTokenAsync(default, v => v == "MY_TOKEN" ? "  from-env\n" : null));
        var missing = await Assert.ThrowsAsync<FlightSqlLoginException>(() => FlightSqlLogin.Parse("grpc://h:1;token_env=MY_TOKEN").ResolveTokenAsync(default, _ => null));
        Assert.Contains("token_env", missing.Message);
    }

    [Fact]
    public void A_command_is_split_into_a_program_and_arguments_without_a_shell()
    {
        Assert.Equal(["az", "account", "get-access-token", "--query", "accessToken", "-o", "tsv"], FlightSqlLogin.SplitCommand("az account get-access-token --query accessToken -o tsv"));
        Assert.Equal(["/opt/my tool/get", "a b", "c"], FlightSqlLogin.SplitCommand("\"/opt/my tool/get\" \"a b\" c"));
        Assert.Equal(["x", "", "y"], FlightSqlLogin.SplitCommand("x \"\" y"));
        Assert.Equal(["$HOME", "|", "cat"], FlightSqlLogin.SplitCommand("$HOME | cat"));                // nothing is expanded or piped
        Assert.Empty(FlightSqlLogin.SplitCommand("   "));
    }

    [Fact]
    public async Task A_token_command_prints_the_token_and_every_other_outcome_is_a_sentence_of_ours()
    {
        // `dotnet --version` prints one word and exits 0 on any machine that builds this
        var ok = await FlightSqlLogin.Parse("grpc://h:1;token_command={dotnet --version}").ResolveTokenAsync(default);
        Assert.Matches(@"^\d+\.\d+\.\d+\S*$", ok);
        var failed = await Assert.ThrowsAsync<FlightSqlLoginException>(() => FlightSqlLogin.Parse("grpc://h:1;token_command={dotnet ddb-no-such-command-qq}").ResolveTokenAsync(default));
        Assert.Contains("exited with code", failed.Message);
        Assert.DoesNotContain("ddb-no-such-command-qq", failed.Message);                           // what the program said is never repeated
        var missing = await Assert.ThrowsAsync<FlightSqlLoginException>(() => FlightSqlLogin.Parse("grpc://h:1;token_command={ddb-no-such-program-qq --secret}").ResolveTokenAsync(default));
        Assert.Contains("could not be started", missing.Message);
        Assert.DoesNotContain("ddb-no-such-program-qq", missing.Message);
        var many = await Assert.ThrowsAsync<FlightSqlLoginException>(() => FlightSqlLogin.Parse("grpc://h:1;token_command={dotnet --info}").ResolveTokenAsync(default));
        Assert.Contains("the token alone", many.Message);                                          // a JSON document or a banner is not a token
        Assert.DoesNotContain("SDK", many.Message);
    }

    // ---- failures, by kind ----

    [Theory]
    [InlineData(StatusCode.Unavailable, "could not be reached", false)]
    [InlineData(StatusCode.Unavailable, "TLS handshake", true)]
    [InlineData(StatusCode.Unauthenticated, "refused the credentials", false)]
    [InlineData(StatusCode.PermissionDenied, "refused the account", false)]
    [InlineData(StatusCode.DeadlineExceeded, "did not answer in time", false)]
    [InlineData(StatusCode.Unimplemented, "does not offer that call", false)]
    [InlineData(StatusCode.Internal, "reported an error", false)]
    [InlineData(StatusCode.Unknown, "reported an error", false)]
    public void A_grpc_failure_is_named_by_its_kind_and_the_servers_words_are_never_repeated(StatusCode code, string sentence, bool tls)
    {
        var rpc = new RpcException(new Status(code, "user bob at 10.1.2.3: password hunter2 wrong; SELECT * FROM secret_table", tls ? new AuthenticationException("the remote certificate is invalid: CN=secret.internal") : null));
        foreach (var ex in new Exception[] { rpc, new InvalidOperationException("Failed to prepare statement", rpc) })           // the client library wraps the status
        {
            var text = DriverFailure.Describe(ex);
            Assert.Contains(sentence, text);
            Assert.Contains($"gRPC {code}", text);
            Assert.Contains("RpcException", text);
            foreach (var secret in new[] { "bob", "10.1.2.3", "hunter2", "secret_table", "secret.internal" }) Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public void A_login_that_cannot_be_used_is_reported_with_our_sentence_and_other_failures_are_left_alone()
    {
        var text = DriverFailure.Describe(new FlightSqlLoginException("The Flight SQL login names more than one way to authenticate."));
        Assert.Contains("more than one way to authenticate", text);
        Assert.Contains("FlightSqlLoginException", text);
        Assert.Null(DriverFailure.FlightSql(new InvalidOperationException("boom")));
        Assert.Equal("InvalidOperationException", DriverFailure.Describe(new InvalidOperationException("password=hunter2")));
    }

    [Fact]
    public void Nothing_listens_where_the_login_points_so_inspect_says_so_and_quotes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-flight-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(0, Cli(["project", "create", "starter", dir]).Exit);
        var config = Path.Combine(dir, "dbdatabuild.yml");
        File.WriteAllText(config, File.ReadAllText(config).Replace("connections:\n  sqlserver:", "connections:\n  proxy: { engine: readonly flightsql }\n  sqlserver:"));
        string? Env(string v) => v == "DBDATABUILD_PROXY_READ" ? "grpc://127.0.0.1:1;token=secrettoken123" : null;
        var inspect = Cli(["connection", "inspect", "proxy", "--project", dir], Env);
        Assert.NotEqual(0, inspect.Exit);
        Assert.Contains("Flight SQL", inspect.Out);
        Assert.Contains("does not connect (127.0.0.1:1: the Flight SQL server could not be reached", inspect.Out);          // the host, nothing else of the value
        Assert.Contains("(RpcException, gRPC Unavailable)", inspect.Out);
        Assert.DoesNotContain("secrettoken123", inspect.Out + inspect.Err);
        var json = Cli(["connection", "inspect", "proxy", "--project", dir, "--format", "json"], Env);
        Assert.DoesNotContain("secrettoken123", json.Out);
        var bad = Cli(["connection", "inspect", "proxy", "--project", dir], v => v == "DBDATABUILD_PROXY_READ" ? "grpc://127.0.0.1:1;token=secrettoken123;wrong=1" : null);
        Assert.Contains("does not connect (The Flight SQL login", bad.Out);
        Assert.DoesNotContain("secrettoken123", bad.Out + bad.Err);
    }

    // ---- the values of Arrow ----

    private static RecordBatch Batch(params (string Name, IArrowArray Array)[] columns) =>
        new(new Schema(columns.Select(c => new Field(c.Name, c.Array.Data.DataType, true)).ToList(), null), columns.Select(c => c.Array).ToList(), columns[0].Array.Length);

    [Fact]
    public void Every_scalar_type_becomes_the_plain_value_a_driver_would_give_and_a_null_is_null()
    {
        var epoch = new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.Zero);
        var batch = Batch(
            ("i64", new Int64Array.Builder().Append(9_000_000_000L).AppendNull().Build()),
            ("i32", new Int32Array.Builder().Append(-7).AppendNull().Build()),
            ("i16", new Int16Array.Builder().Append(300).AppendNull().Build()),
            ("i8", new Int8Array.Builder().Append(-3).AppendNull().Build()),
            ("u32", new UInt32Array.Builder().Append(4_000_000_000).AppendNull().Build()),
            ("f64", new DoubleArray.Builder().Append(0.25).AppendNull().Build()),
            ("f32", new FloatArray.Builder().Append(1.5f).AppendNull().Build()),
            ("bool", new BooleanArray.Builder().Append(true).AppendNull().Build()),
            ("str", new StringArray.Builder().Append("café ☕").AppendNull().Build()),
            ("bin", new BinaryArray.Builder().Append(new byte[] { 1, 2, 3 }.AsSpan()).AppendNull().Build()),
            ("dec", new Decimal128Array.Builder(new Decimal128Type(18, 3)).Append(12345.678m).AppendNull().Build()),
            ("date", new Date32Array.Builder().Append(new DateTime(2024, 2, 29)).AppendNull().Build()),
            ("ts", new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, (string?)null)).Append(epoch).AppendNull().Build()),
            ("tstz", new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC")).Append(epoch).AppendNull().Build()),
            ("time", new Time64Array.Builder(new Time64Type(TimeUnit.Microsecond)).Append(3_661_000_000L).AppendNull().Build()));
        object? V(string name, int row) => ArrowConversions.Value(batch.Column(name), row);

        Assert.Equal(9_000_000_000L, V("i64", 0));
        Assert.Equal(-7, V("i32", 0));
        Assert.Equal((short)300, V("i16", 0));
        Assert.Equal((sbyte)-3, V("i8", 0));
        Assert.Equal(4_000_000_000u, V("u32", 0));
        Assert.Equal(0.25, V("f64", 0));
        Assert.Equal(1.5f, V("f32", 0));
        Assert.Equal(true, V("bool", 0));
        Assert.Equal("café ☕", V("str", 0));
        Assert.Equal(new byte[] { 1, 2, 3 }, V("bin", 0));
        Assert.Equal(12345.678m, V("dec", 0));
        Assert.Equal(new DateOnly(2024, 2, 29), V("date", 0));
        Assert.Equal(new TimeOnly(1, 1, 1), V("time", 0));
        foreach (var column in batch.Schema.FieldsList) Assert.Null(V(column.Name, 1));
        // a clock reading without a zone stays a DateTime (a conditional expression of the two would have made it a DateTimeOffset); a timestamp with a zone is an instant
        var naive = Assert.IsType<DateTime>(V("ts", 0));
        Assert.Equal((new DateTime(2024, 3, 5, 6, 7, 8), DateTimeKind.Unspecified), (naive, naive.Kind));
        Assert.Equal(epoch, Assert.IsType<DateTimeOffset>(V("tstz", 0)));
    }

    [Fact]
    public void A_dictionary_column_is_read_through_its_dictionary_and_a_type_with_no_scalar_is_refused_by_name()
    {
        var dictionary = new StringArray.Builder().Append("red").Append("green").Build();
        var indices = new Int32Array.Builder().Append(1).Append(0).AppendNull().Build();
        var array = new DictionaryArray(new DictionaryType(Int32Type.Default, StringType.Default, false), indices, dictionary);
        Assert.Equal(["green", "red", null], Enumerable.Range(0, 3).Select(i => ArrowConversions.Value(array, i)).ToArray());

        var list = new ListArray.Builder(Int32Type.Default).Append().Build();
        var ex = Assert.Throws<ArrowTypeNotCarriedException>(() => ArrowConversions.Value(list, 0));
        Assert.Equal("list", ex.ArrowType);
        Assert.Contains("list", ex.Message);
    }

    [Fact]
    public void A_copy_converts_the_arrow_values_by_the_declared_logical_type_like_every_other_origin()
    {
        var batch = Batch(
            ("n", new Int32Array.Builder().Append(41).Build()),
            ("d", new Date32Array.Builder().Append(new DateTime(2024, 2, 29)).Build()),
            ("t", new TimestampArray.Builder(new TimestampType(TimeUnit.Nanosecond, (string?)null)).Append(new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.Zero)).Build()),
            ("z", new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC")).Append(new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.Zero)).Build()),
            ("p", new Decimal128Array.Builder(new Decimal128Type(10, 2)).Append(3.14m).Build()),
            ("b", new BinaryArray.Builder().Append(new byte[] { 9 }.AsSpan()).Build()),
            ("flag", new BooleanArray.Builder().Append(false).Build()),
            ("time", new Time32Array.Builder(new Time32Type(TimeUnit.Second)).Append(45).Build()));
        object? Convert(string column, string type) => TransferValues.Convert(ArrowConversions.Value(batch.Column(column), 0), new TransferColumn(column, type));
        Assert.Equal(41L, Convert("n", "BIGINT"));
        Assert.Equal(new DateTime(2024, 2, 29), Convert("d", "DATE"));
        Assert.Equal(new DateTime(2024, 3, 5, 6, 7, 8), Convert("t", "TIMESTAMP"));
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.Zero), Convert("z", "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal(3.14m, Convert("p", "DECIMAL(10, 2)"));
        Assert.Equal(new byte[] { 9 }, Convert("b", "BLOB"));
        Assert.Equal(false, Convert("flag", "BOOLEAN"));
        Assert.Equal(TimeSpan.FromSeconds(45), Convert("time", "TIME"));
        Assert.Equal("41", Convert("n", "VARCHAR"));
    }

    // ---- the types of Arrow ----

    [Fact]
    public void An_arrow_schema_is_typed_as_information_schema_names_types_and_the_profile_maps_them()
    {
        var schema = new Schema(new List<Field>
        {
            new("a", Int64Type.Default, false), new("b", Int32Type.Default, true), new("c", Int16Type.Default, true), new("d", BooleanType.Default, true),
            new("e", DoubleType.Default, true), new("f", FloatType.Default, true), new("g", new Decimal128Type(18, 3), true), new("h", Date32Type.Default, true),
            new("i", new TimestampType(TimeUnit.Microsecond, (string?)null), true), new("j", new TimestampType(TimeUnit.Microsecond, "UTC"), true),
            new("k", StringType.Default, true), new("l", BinaryType.Default, true), new("m", new Time64Type(TimeUnit.Microsecond), true),
            new("n", new ListType(Int32Type.Default), true),
            new("o", new DictionaryType(Int32Type.Default, StringType.Default, false), true),
        }, null);
        var columns = ArrowConversions.Columns(schema);
        Assert.Equal(["bigint", "integer", "smallint", "boolean", "double precision", "real", "decimal", "date", "timestamp", "timestamp with time zone", "character varying", "blob", "time", "list", "character varying"],
            columns.Select(c => c.Type).ToArray());
        Assert.Equal((18, 3), (columns[6].Precision, columns[6].Scale));
        Assert.False(columns[0].Nullable);
        Assert.True(columns[1].Nullable);

        ImportedType Of(ColumnShape c) => ImportedTypes.From("flightsql", c);
        Assert.Equal("BIGINT", Of(columns[0]).LogicalType);
        Assert.Equal("DECIMAL(18, 3)", Of(columns[6]).LogicalType);
        Assert.Equal("TIMESTAMP WITH TIME ZONE", Of(columns[9]).LogicalType);
        Assert.Equal("VARCHAR", Of(columns[10]).LogicalType);
        Assert.Equal("BLOB", Of(columns[11]).LogicalType);
        var list = Of(columns[13]);
        Assert.Null(list.LogicalType);                                                                // a list has no logical type, and is said so
        Assert.Contains("types:", list.Reason);
    }

    // ---- the parameters of a prepared statement ----

    [Fact]
    public void Parameters_are_one_row_of_arrow_values_in_the_order_the_plan_gives_and_a_name_used_twice_is_two_columns()
    {
        var batch = ArrowConversions.ParameterBatch([
            new GateParameter("p_region", DbType.String, "EU"),
            new GateParameter("watermark", DbType.DateTime2, new DateTime(2026, 3, 1, 4, 5, 6, DateTimeKind.Unspecified)),
            new GateParameter("p_region", DbType.String, "EU"),
            new GateParameter("day", DbType.Date, new DateTime(2026, 3, 1)),
            new GateParameter("n", DbType.Int64, 7L),
            new GateParameter("small", DbType.Int32, 3),
            new GateParameter("none", DbType.String, null),
            new GateParameter("nothing", DbType.Int64, null),
            new GateParameter("price", DbType.Decimal, 12.340m),
            new GateParameter("flag", DbType.Boolean, true),
            new GateParameter("ratio", DbType.Double, 0.5),
        ]);
        Assert.Equal((1, 11), (batch.Length, batch.ColumnCount));
        Assert.Equal(["StringType", "TimestampType", "StringType", "Date32Type", "Int64Type", "Int32Type", "StringType", "Int64Type", "Decimal128Type", "BooleanType", "DoubleType"],
            batch.Schema.FieldsList.Select(f => f.DataType.GetType().Name).ToArray());
        var values = Enumerable.Range(0, 11).Select(i => ArrowConversions.Value(batch.Column(i), 0)).ToArray();
        Assert.Equal(["EU", new DateTime(2026, 3, 1, 4, 5, 6), "EU", new DateOnly(2026, 3, 1), 7L, 3, null, null, 12.340m, true, 0.5], values);
        Assert.Equal(3, ((Decimal128Type)batch.Schema.FieldsList[8].DataType).Scale);
    }

    // ---- the catalog ----

    private sealed class FakeFlightSource : IRowSource, IMetadataCatalog
    {
        public bool MetadataOffered { get; init; } = true;
        public bool SqlOffered { get; init; } = true;
        public List<string> Calls { get; } = [];
        public List<string> Texts { get; } = [];

        public Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct)
        {
            Texts.Add(sql);
            if (!SqlOffered) throw new InvalidOperationException("Failed to get flight info", new RpcException(new Status(StatusCode.InvalidArgument, "Table with name tables does not exist!")));
            IReadOnlyList<IReadOnlyList<object?>> rows = sql.Contains("information_schema.columns")
                ? [new object?[] { "items", "BASE TABLE", 1, "id", "bigint", null, 64, 0, "NO" }, new object?[] { "items", "BASE TABLE", 2, "code", "character varying", 40, null, null, "YES" }]
                : sql.Contains("information_schema.schemata") ? [new object?[] { "src" }] : [];
            return Task.FromResult<IRowReader>(new Reader(rows));
        }

        public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => Task.FromResult<IReadOnlyList<ColumnShape>?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<string>?> SchemasAsync(CancellationToken ct)
        {
            Calls.Add("schemas");
            return Task.FromResult<IReadOnlyList<string>?>(MetadataOffered ? ["information_schema", "sales", "ops"] : null);
        }

        public Task<IReadOnlyList<MetadataTable>?> TablesAsync(string schema, string? table, CancellationToken ct)
        {
            Calls.Add($"tables {schema} {table}");
            return Task.FromResult<IReadOnlyList<MetadataTable>?>(!MetadataOffered ? null : [
                new(schema, "orders", false, [new("id", "bigint", null, null, null, false, null), new("amount", "decimal", null, 12, 2, true, null)]),
                new(schema, "orders_v", true, [new("id", "bigint", null, null, null, true, null)]),
            ]);
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> PrimaryKeysAsync(string schema, CancellationToken ct)
        {
            Calls.Add($"pk {schema}");
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>?>(!MetadataOffered ? null : new Dictionary<string, IReadOnlyList<string>> { ["orders"] = ["id"] });
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?> ForeignKeysAsync(string schema, CancellationToken ct)
        {
            Calls.Add($"fk {schema}");
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?>(!MetadataOffered ? null : new Dictionary<string, IReadOnlyList<MetadataForeignKey>>
            {
                ["orders"] = [new("orders", "fk_orders_customer", ["customer_id"], "sales", "customers", ["id"])],
            });
        }

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

    [Fact]
    public async Task The_metadata_calls_are_asked_first_and_their_arrow_schemas_are_the_columns()
    {
        var source = new FakeFlightSource();
        await using var session = ReadSession.ForTesting(source, "flightsql");
        var shapes = await CatalogReader.ReadObjectsAsync(session, "flightsql", "sales");
        Assert.Equal(["sales.orders", "sales.orders_v"], shapes.Keys.Order().ToArray());
        Assert.Equal(ObjectKind.View, shapes["sales.orders_v"].Kind);
        Assert.Equal(("decimal", 12, 2), (shapes["sales.orders"].Columns[1].Type, shapes["sales.orders"].Columns[1].Precision, shapes["sales.orders"].Columns[1].Scale));
        Assert.Equal(["sales", "ops"], await SourceCatalogReader.SchemasAsync(session, "flightsql"));      // information_schema is the server's own
        Assert.Equal(["id"], (await SourceCatalogReader.PrimaryKeysAsync(session, "flightsql", "sales"))["orders"]);
        var fk = (await SourceCatalogReader.ForeignKeysAsync(session, "flightsql", "sales"))["orders"].Single();
        Assert.Equal(("fk_orders_customer", "customers", "customer_id"), (fk.Name, fk.ReferencedTable, fk.Columns.Single()));
        Assert.Empty(source.Texts);                                                                          // no SQL was sent: the server answered the calls
    }

    [Fact]
    public async Task A_server_without_the_metadata_calls_is_read_through_information_schema_with_values_by_position()
    {
        var source = new FakeFlightSource { MetadataOffered = false };
        await using var session = ReadSession.ForTesting(source, "flightsql");
        var shapes = await CatalogReader.ReadObjectsAsync(session, "flightsql", "src");
        Assert.Equal(["src.items"], shapes.Keys.ToArray());
        Assert.Equal(("character varying", 40), (shapes["src.items"].Columns[1].Type, shapes["src.items"].Columns[1].Length));
        Assert.False(shapes["src.items"].Columns[0].Nullable);                                                // information_schema knows NOT NULL, which an Arrow schema may not
        Assert.Contains("information_schema.columns", source.Texts[0]);
        Assert.Contains("t.table_schema = ?", source.Texts[0]);
        Assert.Equal(["src"], await SourceCatalogReader.SchemasAsync(session, "flightsql"));
        Assert.Contains("tables src ", source.Calls);                                                           // the metadata call was tried first
    }

    [Fact]
    public async Task A_server_with_neither_says_so_plainly_and_a_dialect_that_says_none_is_never_asked()
    {
        var neither = new FakeFlightSource { MetadataOffered = false, SqlOffered = false };
        await using var session = ReadSession.ForTesting(neither, "flightsql");
        var ex = await Assert.ThrowsAsync<CatalogUnavailableException>(() => CatalogReader.ReadObjectsAsync(session, "flightsql", "src"));
        Assert.Contains("neither the metadata calls", ex.Message);
        Assert.Contains("declare them by hand", ex.Message);
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => SourceCatalogReader.SchemasAsync(session, "flightsql"));
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => SourceCatalogReader.PrimaryKeysAsync(session, "flightsql", "src"));

        // a transport failure is not "no catalog": it surfaces as it is
        var unreachable = new UnreachableSource();
        await using var down = ReadSession.ForTesting(unreachable, "flightsql");
        await Assert.ThrowsAnyAsync<Exception>(() => CatalogReader.ReadObjectsAsync(down, "flightsql", "src")).ContinueWith(t => Assert.IsNotType<CatalogUnavailableException>(t.Result));

        var blind = DialectProfiles.Build("blind", DialectProfiles.Ansi, "ansi", null, null, null, "none", null, null, null).Profile!;
        var source = new FakeFlightSource();
        await using var none = ReadSession.ForTesting(source, "flightsql", blind);
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => CatalogReader.ReadObjectsAsync(none, "flightsql", "src"));
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => SourceCatalogReader.SchemasAsync(none, "flightsql"));
        Assert.Empty(source.Calls);
        Assert.Empty(source.Texts);
    }

    private sealed class UnreachableSource : IRowSource, IMetadataCatalog
    {
        private static RpcException Down() => new(new Status(StatusCode.Unavailable, "no route to host"));
        public Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct) => throw Down();
        public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => throw Down();
        public Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => throw Down();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IReadOnlyList<string>?> SchemasAsync(CancellationToken ct) => throw Down();
        public Task<IReadOnlyList<MetadataTable>?> TablesAsync(string schema, string? table, CancellationToken ct) => throw Down();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> PrimaryKeysAsync(string schema, CancellationToken ct) => throw Down();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?> ForeignKeysAsync(string schema, CancellationToken ct) => throw Down();
    }

    // ---- the copy's text ----

    [Fact]
    public void A_copy_reads_a_flight_sql_origin_in_its_dialect_with_positional_parameters_in_the_plans_order()
    {
        var cfg = ProjectConfigLoader.Load("dialects:\n  backtick_sql: { extends: ansi, quote: '`' }\nconnections:\n  proxy: { engine: readonly flightsql backtick_sql }\n  plain: { engine: readonly flightsql }\n", "dbdatabuild.yml", [])!;
        Assert.Equal(ParameterStyle.Positional, cfg.DialectOf("proxy").Parameters);
        Assert.Equal("`", cfg.DialectOf("proxy").QuoteOpen);
        Assert.Equal("\"", cfg.DialectOf("plain").QuoteOpen);
        var (text, order) = ParameterPlaceholders.Rewrite("SELECT * FROM t WHERE a >= @w AND b = @p AND c = @p", ["w", "p"], cfg.DialectOf("plain"));
        Assert.Equal("SELECT * FROM t WHERE a >= ? AND b = ? AND c = ?", text);
        Assert.Equal(["w", "p", "p"], order);                                                                    // a placeholder used twice is passed twice
    }

    private static (int Exit, string Out, string Err) Cli(string[] args, Func<string, string?>? env = null)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, environment: env ?? (_ => null));
        return (exit, o.ToString(), e.ToString());
    }
}
