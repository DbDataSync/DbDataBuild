using System.Text;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Flight.Sql.Client;
using DbDataBuild.Execution;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>A connection of the test's own to the sqlflite server, to put data there (the tool never writes to an origin) and to ask it for a bearer token.</summary>
internal sealed class SqlFliteSession : IAsyncDisposable
{
    private readonly GrpcChannel channel;
    private readonly FlightSqlClient client;
    private readonly Metadata headers;

    private SqlFliteSession(GrpcChannel channel, FlightSqlClient client, Metadata headers, string bearer, FlightSqlLogin login) { this.channel = channel; this.client = client; this.headers = headers; Bearer = bearer; Login = login; }

    public string Bearer { get; }
    public FlightSqlLogin Login { get; }
    public string Schema { get; } = "s" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The server's login with the token instead of the user and password.</summary>
    public string TokenLogin => $"grpc://127.0.0.1:{Login.Port};token={Bearer}";

    public static async Task<SqlFliteSession> OpenAsync()
    {
        var value = EngineEnv.Get("DBDATABUILD_TEST_FLIGHT");
        Skip.If(value == null, "Set DBDATABUILD_TEST_FLIGHT to the login of a local Flight SQL test server (run scripts/test-engines.sh up flight, then eval \"$(scripts/test-engines.sh env)\").");
        var login = FlightSqlLogin.Parse(value!);
        if (login.Host is not ("127.0.0.1" or "localhost" or "::1")) throw new InvalidOperationException($"Refusing to run conformance tests against `{login.Host}`: only local throwaway servers are allowed.");
        var channel = GrpcChannel.ForAddress(login.Address);
        var flight = new FlightClient(channel);
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login.User}:{login.Password}"));
        using var call = flight.Handshake(new Metadata { { "authorization", basic } });
        await call.RequestStream.CompleteAsync();
        var responseHeaders = await call.ResponseHeadersAsync;
        while (await call.ResponseStream.MoveNext(default)) { }
        var bearer = responseHeaders.GetValue("authorization")!;
        var session = new SqlFliteSession(channel, new FlightSqlClient(flight), new Metadata { { "authorization", bearer } }, bearer["Bearer ".Length..], login);
        await session.ExecAsync($"CREATE SCHEMA {session.Schema}");
        return session;
    }

    public async Task ExecAsync(params string[] statements)
    {
        foreach (var sql in statements)
            await client.ExecuteUpdateAsync(sql, Transaction.NoTransaction, new Apache.Arrow.Flight.Sql.FlightCallOptions { Headers = headers }, default);
    }

    public async ValueTask DisposeAsync()
    {
        try { await ExecAsync($"DROP SCHEMA {Schema} CASCADE"); } catch (Exception) { /* a leftover schema of a throwaway server is harmless */ }
        channel.Dispose();
    }
}

/// <summary>
/// Read-only origins, step 4, against a real server: voltrondata/sqlflite (Arrow's C++ Flight SQL server, DuckDB behind it), started by `scripts/test-engines.sh up flight` as two containers, one plain and one with TLS.
/// It answers the user and password handshake with a JWT, the metadata calls, prepared statements and TLS. It binds every prepared-statement parameter as text, so a typed comparison against a watermark cannot
/// be tried here (the tests with the in-process server do that); a native query that casts its parameters can. Without the containers every test here is skipped, and says why.
/// </summary>
[Trait("Group", "apply")]
public class FlightSqlServerConformanceTests
{
    public static TheoryData<string> Destinations => new() { "sqlserver", "postgres" };

    private static void Ok((int Exit, string Out, string Err) r, string what) => FlightRun.Ok(r, what);

    private static string Model(string schema, string table) => $"name: {schema}.{table}\nkind: mapped\nconnections=: [proxy]\ngrain: [id]\n{FlightRun.Columns}";

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task The_rows_of_a_real_flight_sql_server_arrive_intact_with_a_user_and_password_the_handshake_giving_the_token(string destination)
    {
        await using var origin = await SqlFliteSession.OpenAsync();
        using var run = await FlightRun.SetUpAsync(destination, EngineEnv.Get("DBDATABUILD_TEST_FLIGHT")!);
        await using var to = run.Destination;
        var s = origin.Schema;
        run.Write($"models/{s}/items.yml", Model(s, "items"));
        run.Write("models/dst/items.yml", $"name: dst.items\nkind: {{copy: {{from: {s}.items}}}}\nindexes:\n  - {{name: ix_items_code, columns: [code]}}\n");
        await origin.ExecAsync(FlightRun.ItemsDdl.Replace("{s}", s), FlightRun.ItemsRows.Replace("{s}", s));

        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var inspect = run.Cli("connection", "inspect", "proxy");
        Ok(inspect, "inspect the origin");
        Assert.Contains($"Flight SQL server 127.0.0.1:{origin.Login.Port} (no TLS)", inspect.Out);
        Assert.DoesNotContain(origin.Login.Password!, inspect.Out + inspect.Err);
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        var planFile = FlightRun.PlanOf(run, plan.Out);
        Assert.DoesNotContain(origin.Login.Password!, File.ReadAllText(planFile));
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        var copied = await FlightRun.ComparableAsync(to, "dst.items");
        Assert.Equal(string.Join("\n", FlightRun.ItemsComparable), string.Join("\n", copied));

        // a second deploy loads again (a copy is a full replace)
        await origin.ExecAsync($"INSERT INTO {s}.items (id) VALUES (5)");
        var again = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(again, "second plan");
        Ok(run.Cli("connection", "deploy", "--apply-plan", FlightRun.PlanOf(run, again.Out)), "second apply");
        Assert.Equal(5, (await FlightRun.ComparableAsync(to, "dst.items")).Count);
    }

    [SkippableFact]
    public async Task A_bearer_token_comes_from_the_login_the_environment_or_a_command_and_a_wrong_one_is_refused_in_words_that_quote_nothing()
    {
        await using var origin = await SqlFliteSession.OpenAsync();
        using var run = await FlightRun.SetUpAsync("postgres", origin.TokenLogin);
        await using var to = run.Destination;
        var s = origin.Schema;
        run.Write($"models/{s}/items.yml", Model(s, "items"));
        run.Write("models/dst/items.yml", $"name: dst.items\nkind: {{copy: {{from: {s}.items}}}}\n");
        await origin.ExecAsync(FlightRun.ItemsDdl.Replace("{s}", s), FlightRun.ItemsRows.Replace("{s}", s));
        Ok(run.Cli("connection", "init", "postgres", "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");

        // the token in the login value
        var inspect = run.Cli("connection", "inspect", "proxy");
        Ok(inspect, "inspect with the token");
        Assert.DoesNotContain(origin.Bearer, inspect.Out + inspect.Err);
        var plan = run.Cli("connection", "deploy", "postgres", "--write-plan");
        Ok(plan, "plan");
        Ok(run.Cli("connection", "deploy", "--apply-plan", FlightRun.PlanOf(run, plan.Out)), "apply with the token");
        Assert.Equal(4, (await FlightRun.ComparableAsync(to, "dst.items")).Count);

        // the token in the environment, and fetched by a command when the tool connects
        const string variable = "DDB_TEST_FLIGHT_TOKEN";
        Environment.SetEnvironmentVariable(variable, origin.Bearer);
        try
        {
            run.OriginLogin = $"grpc://127.0.0.1:{origin.Login.Port};token_env={variable}";
            Ok(run.Cli("connection", "inspect", "proxy"), "inspect with token_env");
            if (!OperatingSystem.IsWindows())
            {
                run.OriginLogin = $"grpc://127.0.0.1:{origin.Login.Port};token_command={{printf %s {origin.Bearer}}}";
                var viaCommand = run.Cli("connection", "inspect", "proxy");
                Ok(viaCommand, "inspect with token_command");
                Assert.DoesNotContain(origin.Bearer, viaCommand.Out + viaCommand.Err);
                run.OriginLogin = $"grpc://127.0.0.1:{origin.Login.Port};token_command={{false}}";
                var failing = run.Cli("connection", "inspect", "proxy");
                Assert.NotEqual(0, failing.Exit);
                Assert.Contains("token_command", failing.Out);
                Assert.Contains("exited with code", failing.Out);
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
        run.OriginLogin = $"grpc://127.0.0.1:{origin.Login.Port};token_env={variable}";
        var unset = run.Cli("connection", "inspect", "proxy");
        Assert.NotEqual(0, unset.Exit);
        Assert.Contains("token_env", unset.Out);

        // a token the server did not issue
        run.OriginLogin = $"grpc://127.0.0.1:{origin.Login.Port};token=not-a-token-the-server-issued";
        var refused = run.Cli("connection", "inspect", "proxy");
        Assert.NotEqual(0, refused.Exit);
        Assert.Contains("the Flight SQL server refused the credentials", refused.Out);
        Assert.Contains("gRPC Unauthenticated", refused.Out);
        Assert.DoesNotContain("not-a-token-the-server-issued", refused.Out + refused.Err);
        // a wrong password: the handshake is refused, and the password stays out of every line
        run.OriginLogin = $"grpc://127.0.0.1:{origin.Login.Port};user={origin.Login.User};password=wrong-pw-never-printed";
        var wrong = run.Cli("connection", "inspect", "proxy");
        Assert.NotEqual(0, wrong.Exit);
        Assert.Contains("refused the credentials", wrong.Out);
        Assert.DoesNotContain("wrong-pw-never-printed", wrong.Out + wrong.Err);
        // the same through a copy: failed as the origin, nothing of the login in the output
        var refresh = run.Cli("connection", "deploy", "postgres", "--write-plan");
        Assert.DoesNotContain("wrong-pw-never-printed", refresh.Out + refresh.Err);
    }

    [SkippableFact]
    public async Task TLS_is_checked_against_the_machine_or_a_certificate_authority_file_and_a_failure_is_named_as_a_handshake_failure()
    {
        var tls = EngineEnv.Get("DBDATABUILD_TEST_FLIGHT_TLS");
        var ca = EngineEnv.Get("DBDATABUILD_TEST_FLIGHT_CA");
        Skip.If(tls == null || ca == null || !File.Exists(ca), "Set DBDATABUILD_TEST_FLIGHT_TLS and DBDATABUILD_TEST_FLIGHT_CA (run scripts/test-engines.sh up flight, then eval \"$(scripts/test-engines.sh env)\").");
        using var run = await FlightRun.SetUpAsync("postgres", tls!);
        await using var to = run.Destination;
        Ok(run.Cli("connection", "init", "postgres", "--apply"), "init");                                          // so that a clean inspect exits 0
        var login = FlightSqlLogin.Parse(tls!);
        string Value(string options) => $"grpc+tls://localhost:{login.Port};user={login.User};password={login.Password}{options}";

        // the certificate is not one the machine trusts
        run.OriginLogin = Value("");
        var untrusted = run.Cli("connection", "inspect", "proxy");
        Assert.NotEqual(0, untrusted.Exit);
        Assert.True(untrusted.Out.Contains("the TLS handshake with the Flight SQL server failed"), untrusted.Out);
        Assert.DoesNotContain(login.Password!, untrusted.Out + untrusted.Err);

        // the authority that signed it, from a file: the name `localhost` is the certificate's
        run.OriginLogin = Value($";ca_file={{{ca}}}");
        var trusted = run.Cli("connection", "inspect", "proxy");
        Ok(trusted, "inspect with ca_file");
        Assert.Contains("(TLS)", trusted.Out);
        Assert.DoesNotContain(ca!, trusted.Out + trusted.Err);                                                     // the value is never printed, the file name with it

        // the same file, and an address whose name the certificate does not carry
        run.OriginLogin = $"grpc+tls://127.0.0.1:{login.Port};user={login.User};password={login.Password};ca_file={{{ca}}}";
        var wrongName = run.Cli("connection", "inspect", "proxy");
        Assert.NotEqual(0, wrongName.Exit);
        Assert.Contains("TLS handshake", wrongName.Out);

        // no check at all, when it is asked for
        run.OriginLogin = $"grpc+tls://127.0.0.1:{login.Port};user={login.User};password={login.Password};tls_skip_verify=true";
        Ok(run.Cli("connection", "inspect", "proxy"), "inspect with tls_skip_verify");

        // a file that is not a certificate
        var junk = Path.Combine(run.Dir, "not-a-ca.pem");
        File.WriteAllText(junk, "this is not a certificate");
        run.OriginLogin = Value($";ca_file={{{junk}}}");
        var unreadable = run.Cli("connection", "inspect", "proxy");
        Assert.NotEqual(0, unreadable.Exit);
        Assert.Contains("ca_file", unreadable.Out);

        // TLS asked for on a plain port, and the other way round: said as unreachable, not as a parse of what came back
        var plain = EngineEnv.Get("DBDATABUILD_TEST_FLIGHT");
        Skip.If(plain == null, "The plain server is not running.");
        var plainLogin = FlightSqlLogin.Parse(plain!);
        run.OriginLogin = $"grpc+tls://localhost:{plainLogin.Port};user={plainLogin.User};password={plainLogin.Password};tls_skip_verify=true";
        Assert.NotEqual(0, run.Cli("connection", "inspect", "proxy").Exit);
        run.OriginLogin = $"grpc://127.0.0.1:{login.Port};user={login.User};password={login.Password}";
        var downgraded = run.Cli("connection", "inspect", "proxy");
        Assert.NotEqual(0, downgraded.Exit);
        Assert.DoesNotContain(login.Password!, downgraded.Out + downgraded.Err);
        await Task.CompletedTask;
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task The_catalog_comes_from_the_metadata_calls_and_falls_back_to_information_schema_when_the_server_will_not_describe_the_table(string destination)
    {
        await using var origin = await SqlFliteSession.OpenAsync();
        using var run = await FlightRun.SetUpAsync(destination, EngineEnv.Get("DBDATABUILD_TEST_FLIGHT")!);
        await using var to = run.Destination;
        // `main` is the schema name the server describes in its metadata calls (its table-schema call fails for any other, which is the fallback); the tables of other tests are in it too, so a prefix picks ours
        var prefix = "ddb" + Guid.NewGuid().ToString("N")[..8];
        await origin.ExecAsync(
            $"CREATE TABLE main.{prefix}_parents (id BIGINT NOT NULL PRIMARY KEY, name VARCHAR NOT NULL)",
            $"CREATE TABLE main.{prefix}_kids (id BIGINT NOT NULL PRIMARY KEY, parent_id BIGINT NOT NULL REFERENCES main.{prefix}_parents (id), born DATE, price DECIMAL(10,2), payload BLOB, ratio DOUBLE, seen TIMESTAMP)",
            $"CREATE TABLE {origin.Schema}.kids (id BIGINT NOT NULL PRIMARY KEY, born DATE, price DECIMAL(10,2))");
        try
        {
            // through the metadata calls: names, types from the Arrow schema, the primary key, the foreign key; nullability is the server's Arrow schema's (this server marks every column nullable)
            var viaMetadata = run.Cli("project", "import", "proxy", $"main.{prefix}_*", "--write");
            Ok(viaMetadata, "import through metadata");
            var kids = File.ReadAllText(Directory.GetFiles(Path.Combine(run.Dir, "models"), $"{prefix}_kids.yml", SearchOption.AllDirectories).Single());
            Assert.Contains("parent_id", kids);
            Assert.Contains("DECIMAL(10, 2)", kids);
            Assert.Contains("TIMESTAMP", kids);
            Assert.Contains("DOUBLE", kids);
            Assert.Contains("grain", kids);                                                                      // the primary key, from GetPrimaryKeys
            Assert.DoesNotContain("nullable: false", kids);                                                      // the Arrow schema of this server says nullable for every column: that is what was read
            Assert.Contains($"{prefix}_parents", kids);                                                          // the foreign key, from GetImportedKeys

            // through information_schema, for a schema name the server cannot describe by its metadata call: the same tables, and NOT NULL is known
            var viaSql = run.Cli("project", "import", "proxy", $"{origin.Schema}.kids", "--write");
            Ok(viaSql, "import through information_schema");
            var sqlKids = File.ReadAllText(Directory.GetFiles(Path.Combine(run.Dir, "models"), "kids.yml", SearchOption.AllDirectories).Single());
            Assert.Contains("nullable: false", sqlKids);
            Assert.Contains("DECIMAL(10, 2)", sqlKids);

            // the structure check of a copy from a table the metadata calls described: a declared NOT NULL is not held against an Arrow schema that says nullable
            var s = "main";
            run.Write($"models/{s}/{prefix}_items.yml", $"name: {s}.{prefix}_items\nkind: mapped\nconnections=: [proxy]\ngrain: [id]\n{FlightRun.Columns}");
            run.Write("models/dst/items.yml", $"name: dst.items\nkind: {{copy: {{from: {s}.{prefix}_items}}}}\n");
            await origin.ExecAsync(FlightRun.ItemsDdl.Replace("{s}.items", $"main.{prefix}_items"), FlightRun.ItemsRows.Replace("{s}.items", $"main.{prefix}_items"));
            Ok(run.Cli("connection", "init", destination, "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", destination, "--write-plan");
            Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", FlightRun.PlanOf(run, plan.Out)), "apply");
            Assert.Equal(string.Join("\n", FlightRun.ItemsComparable), string.Join("\n", await FlightRun.ComparableAsync(to, "dst.items")));

            // a type that changed at the origin is still named
            await origin.ExecAsync($"ALTER TABLE main.{prefix}_items ALTER COLUMN small TYPE VARCHAR");
            var changedPlan = run.Cli("connection", "deploy", destination, "--write-plan");
            var changed = run.Cli("connection", "deploy", "--apply-plan", FlightRun.PlanOf(run, changedPlan.Out));
            Assert.NotEqual(0, changed.Exit);
            Assert.Contains("DDB-230", changed.Out + changed.Err);
            Assert.Contains("small", changed.Out + changed.Err);
        }
        finally
        {
            try { await origin.ExecAsync($"DROP TABLE IF EXISTS main.{prefix}_items", $"DROP TABLE IF EXISTS main.{prefix}_kids", $"DROP TABLE IF EXISTS main.{prefix}_parents"); } catch (Exception) { /* throwaway */ }
        }
    }

    [SkippableTheory, MemberData(nameof(Destinations))]
    public async Task A_native_query_runs_as_a_prepared_statement_on_a_real_server_with_its_values_bound_in_the_plans_order(string destination)
    {
        await using var origin = await SqlFliteSession.OpenAsync();
        using var run = await FlightRun.SetUpAsync(destination, EngineEnv.Get("DBDATABUILD_TEST_FLIGHT")!, "parameters:\n  min_id: { type: BIGINT, value: \"3\" }\n  since: { type: DATE, value: \"2024-02-01\" }\n");
        await using var to = run.Destination;
        var s = origin.Schema;
        // the server binds every parameter as text, so the query casts them: `CAST(? AS BIGINT)` is the dialect of a server like this one
        run.Write($"models/{s}/picked.yml", $"name: {s}.picked\nkind:\n  native:\n    query: SELECT id, upper(code) AS code FROM {s}.items WHERE id >= CAST(${{project.min_id}} AS BIGINT) AND (id = CAST(${{project.min_id}} AS BIGINT) OR born >= CAST(${{project.since}} AS DATE))\nconnections=: [proxy]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT, nullable: false}}\n  - {{name: code, type: \"VARCHAR(40)\"}}\n");
        run.Write("models/dst/picked.yml", $"name: dst.picked\nkind: {{copy: {{from: {s}.picked}}}}\n");
        await origin.ExecAsync($"CREATE TABLE {s}.items (id BIGINT NOT NULL, code VARCHAR, born DATE)",
            $"INSERT INTO {s}.items VALUES (1, 'a', DATE '2024-03-01'), (2, 'b', DATE '2024-03-01'), (3, 'c', DATE '2024-01-01'), (4, 'd', DATE '2024-03-01'), (5, 'e', DATE '2023-01-01')");
        Ok(run.Cli("connection", "init", destination, "--apply"), "init");
        Ok(run.Cli("project", "compile"), "compile");
        var plan = run.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        var planFile = FlightRun.PlanOf(run, plan.Out);
        var text = File.ReadAllText(planFile);
        Assert.Contains("CAST(? AS BIGINT) AND (id = CAST(? AS BIGINT) OR born >= CAST(? AS DATE))", text);
        Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
        Assert.Equal(["3:C", "4:D"], await to.RowsAsync(destination == "postgres" ? "SELECT CAST(id AS VARCHAR(10)) || ':' || code FROM dst.picked ORDER BY id" : "SELECT CAST(id AS VARCHAR(10)) + ':' + code FROM dst.picked ORDER BY id"));
    }
}
