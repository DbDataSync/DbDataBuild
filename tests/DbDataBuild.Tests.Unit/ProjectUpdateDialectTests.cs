using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using Json.Schema;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// `project update`, the dialect part (docs/research/read-origins.md section 10): a Flight SQL server is asked what it is (a fake that answers `GetSqlInfo`), the answer is committed in dialects.lock.yml, compile and the
/// plans carry it, and the origin check of a refresh fails when the server stops agreeing. The metadata part is in ProjectUpdateMappedModelsTests.
/// </summary>
public class ProjectUpdateDialectTests : IDisposable
{
    private static readonly JsonSchema Schema = SchemaConformanceTests.LoadSchema("output");
    private readonly string dir = NewProjectDir();

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    /// <summary>A Flight SQL server that answers `GetSqlInfo` with the facts it is given and counts how often it was asked.</summary>
    internal sealed class FakeServer(ServerFacts? facts) : IRowSource, IServerFacts
    {
        public int Asked;
        public ServerFacts? Facts { get; set; } = facts;
        public Task<ServerFacts?> ServerFactsAsync(CancellationToken ct) { Interlocked.Increment(ref Asked); return Task.FromResult(Facts); }
        public Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct) => Task.FromResult<IRowReader>(new One());
        public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct) => Task.FromResult<IReadOnlyList<ColumnShape>?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class One : IRowReader
        {
            private int at = -1;
            public IReadOnlyList<string> Names => ["c0"];
            public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++at < 1);
            public ValueTask<object?> ValueAsync(int column, CancellationToken ct) => ValueTask.FromResult<object?>(1);
            public void Cancel() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static readonly ServerFacts Postgres = new("PostgreSQL", "16.4", "\"", "case_insensitive", "case_insensitive", true);
    private static readonly ServerFacts SqlServer = new("Microsoft SQL Server", "16.0.4135", "\"", "case_insensitive", "case_insensitive", false);

    private void Write(string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private string LockText => File.Exists(Path.Combine(dir, ProductInfo.DialectLockFile)) ? File.ReadAllText(Path.Combine(dir, ProductInfo.DialectLockFile)) : "";

    private void Project(string proxyEngine = "readonly flightsql")
    {
        Write("dbdatabuild.yml", $"defaults: {{connections: [sqlserver]}}\ntracking: {{ connection: sqlserver }}\nconnections:\n  proxy_erp: {{ engine: {proxyEngine} }}\n  proxy_fixed: {{ engine: readonly flightsql postgres }}\n  files: {{ engine: readonly duckdb }}\n");
        Write("models/src/events.yml", "name: src.events\nkind: mapped\nconnections=: [proxy_erp]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: label, type: VARCHAR}\n");
        Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events}}\n");
    }

    private static string Env(string v) => v is "DBDATABUILD_PROXY_ERP_READ" or "DBDATABUILD_PROXY_FIXED_READ" ? "grpc://127.0.0.1:9;token=x" : v == "DBDATABUILD_FILES_READ" ? ":memory:" : "";

    private (int Exit, JsonNode Doc, string Text, string Err) Update(FakeServer? server, Func<string, string?>? env = null, params string[] args)
    {
        ProjectUpdateCommand.TestOpener.Value = (login, ct) => login.Connection == "proxy_erp" ? Task.FromResult(ReadSession.ForTesting(server!, "flightsql", login.Dialect)) : throw new InvalidOperationException("the explicit dialect must not be asked");
        try
        {
            var json = new StringWriter(); var jsonErr = new StringWriter();
            var exit = CliApp.Run(["project", "update", "--project", dir, "--format", "json", .. args], json, jsonErr, environment: env ?? Env);
            var doc = JsonNode.Parse(json.ToString())!;
            var result = Schema.Evaluate(JsonSerializer.SerializeToNode(doc), new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, $"output does not satisfy output.schema.json:\n{json}");
            var text = string.Join('\n', doc["messages"]!.AsArray().Select(m => (string)m!));
            var err = string.Join('\n', doc["errors"]!.AsArray().Select(m => (string)m!)) + string.Join('\n', doc["diagnostics"]!.AsArray().Select(d => (string)d!["code"]! + " " + (string)d["found"]!));
            return (exit, doc, text, err);
        }
        finally { ProjectUpdateCommand.TestOpener.Value = null; }
    }

    private static JsonNode Row(JsonNode doc, string connection) => doc["data"]!["dialects"]!.AsArray().Single(r => (string)r!["connection"]! == connection)!;

    [Fact]
    public void A_flightsql_connection_without_a_keyword_is_asked_and_the_answer_is_committed()
    {
        Project();
        var server = new FakeServer(Postgres);
        var (exit, doc, text, _) = Update(server);
        Assert.Equal(0, exit);
        Assert.Equal(1, server.Asked);                                                              // proxy_erp only: proxy_fixed names its dialect and files has nothing to ask
        Assert.Equal("new", (string)Row(doc, "proxy_erp")["status"]!);
        Assert.Equal("postgres", (string)Row(doc, "proxy_erp")["dialect"]!);
        Assert.Equal("PostgreSQL", (string)Row(doc, "proxy_erp")["server"]!);
        Assert.Equal("explicit", (string)Row(doc, "proxy_fixed")["status"]!);
        Assert.Equal("nothing_to_discover", (string)Row(doc, "files")["status"]!);
        Assert.Contains("nothing to discover", text);
        Assert.Equal("new", (string)doc["data"]!["dialects_file"]!["status"]!);
        var committed = LockText;
        Assert.StartsWith("# Written by `dbdatabuild project update`", committed);
        Assert.Contains("dialect: postgres", committed);
        Assert.Contains("server: PostgreSQL", committed);
        Assert.DoesNotContain("proxy_fixed", committed);                                            // an explicit keyword wins and has no entry
        Assert.DoesNotContain("\r", committed);
        Assert.Equal(["dialects.lock.yml"], doc["data"]!["written"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Contains("project compile", (string)doc["next"]![0]!);
    }

    [Fact]
    public void A_second_run_changes_nothing_and_check_passes()
    {
        Project();
        Update(new FakeServer(Postgres));
        var before = Snapshot(dir);
        var (exit, doc, text, _) = Update(new FakeServer(Postgres));
        Assert.Equal(0, exit);
        Assert.Equal("unchanged", (string)Row(doc, "proxy_erp")["status"]!);
        Assert.Equal("unchanged", (string)doc["data"]!["dialects_file"]!["status"]!);
        Assert.Equal(before, Snapshot(dir));
        Assert.Contains("is up to date", text);
        var (checkExit, _, _, _) = Update(new FakeServer(Postgres), null, "--check");
        Assert.Equal(0, checkExit);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void A_server_that_now_reports_another_dialect_is_a_difference_for_check_and_a_rewrite_otherwise()
    {
        Project();
        Update(new FakeServer(Postgres));
        var before = Snapshot(dir);
        var (checkExit, doc, text, err) = Update(new FakeServer(SqlServer), null, "--check");
        Assert.Equal(1, checkExit);
        Assert.Equal("changed", (string)Row(doc, "proxy_erp")["status"]!);
        Assert.Contains("the dialect was `postgres` and is now `sqlserver`", (string)Row(doc, "proxy_erp")["changes"]![0]! + string.Join(' ', Row(doc, "proxy_erp")["changes"]!.AsArray().Select(x => (string)x!)));
        Assert.Contains("DDB-461", err);
        Assert.Contains("+++ b/dialects.lock.yml", text);                                          // the diff is shown
        Assert.Equal(before, Snapshot(dir));                                                       // --check writes nothing
        var (exit, _, _, _) = Update(new FakeServer(SqlServer));
        Assert.Equal(0, exit);
        Assert.Contains("dialect: sqlserver", LockText);
    }

    [Fact]
    public void A_new_server_version_alone_is_no_difference_for_check_but_is_written_by_update()
    {
        Project();
        Update(new FakeServer(Postgres));
        var upgraded = Postgres with { ServerVersion = "16.5" };
        var (checkExit, doc, _, _) = Update(new FakeServer(upgraded), null, "--check");
        Assert.Equal(0, checkExit);
        Assert.Equal("version_changed", (string)Row(doc, "proxy_erp")["status"]!);
        Update(new FakeServer(upgraded));
        Assert.Contains("server_version: \"16.5\"", LockText);
    }

    [Fact]
    public void A_connection_with_no_read_login_or_that_cannot_be_reached_is_said_and_skipped()
    {
        Project();
        Update(new FakeServer(Postgres));
        var before = LockText;
        var (exit, doc, text, _) = Update(null, v => v == "DBDATABUILD_FILES_READ" ? ":memory:" : null);
        Assert.Equal(0, exit);
        Assert.Equal("skipped", (string)Row(doc, "proxy_erp")["status"]!);
        Assert.Contains("DBDATABUILD_PROXY_ERP_READ", text);
        Assert.Equal(before, LockText);                                                            // what was committed stays
        ProjectUpdateCommand.TestOpener.Value = (_, _) => throw new TimeoutException("secret-host");
        try
        {
            var o = new StringWriter(); var e = new StringWriter();
            var down = CliApp.Run(["project", "update", "--project", dir], o, e, environment: Env);
            Assert.Equal(0, down);
            Assert.Contains("could not be reached (TimeoutException)", o.ToString());
            Assert.DoesNotContain("secret-host", o + e.ToString());
        }
        finally { ProjectUpdateCommand.TestOpener.Value = null; }
        Assert.Equal(before, LockText);
    }

    [Fact]
    public void Offline_asks_nobody_and_writes_no_lock()
    {
        Project();
        var server = new FakeServer(Postgres);
        var (exit, doc, text, _) = Update(server, null, "--offline");
        Assert.Equal(0, exit);
        Assert.Equal(0, server.Asked);
        Assert.False((bool)doc["data"]!["online"]!);
        Assert.Null(doc["data"]!["dialects"]);
        Assert.False(File.Exists(Path.Combine(dir, ProductInfo.DialectLockFile)));
        Assert.Contains("Offline: no connection was read", text);
    }

    [Fact]
    public void A_server_that_says_nothing_is_ansi_and_says_so()
    {
        Project();
        var (exit, doc, _, _) = Update(new FakeServer(null));
        Assert.Equal(0, exit);
        Assert.Equal("ansi", (string)Row(doc, "proxy_erp")["dialect"]!);
        Assert.Contains("does not implement GetSqlInfo", (string)Row(doc, "proxy_erp")["note"]!);
        Assert.Contains("dialect: ansi", LockText);
    }

    [Fact]
    public void An_explicit_keyword_wins_over_an_entry_and_the_stale_entry_is_dropped()
    {
        Project();
        Update(new FakeServer(Postgres));
        Project("readonly flightsql sqlserver");                                                    // the person now names the dialect
        var load = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(dir, load);
        Assert.Contains(load, d => d.Code == "DDB-459");
        Assert.Equal("sqlserver", config.DialectOf("proxy_erp").Name);                              // the keyword, not the entry
        var (exit, doc, _, _) = Update(null);
        Assert.Equal(0, exit);
        Assert.Equal("explicit", (string)Row(doc, "proxy_erp")["status"]!);
        Assert.False(File.Exists(Path.Combine(dir, ProductInfo.DialectLockFile)));                  // nothing left to commit
    }

    // ---- compile and the plans use what is committed ----

    private (int Exit, string Out, string Err) Compile()
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void Compile_without_a_committed_dialect_says_to_run_project_update_and_writes_for_ansi()
    {
        Project();
        var (exit, output, err) = Compile();
        Assert.Equal(0, exit);
        Assert.Contains("DDB-458", err + output);
        Assert.Contains("project update", err + output);
        var loads = File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "loads.yml"));
        Assert.Contains("\"id\", \"label\" FROM \"src\".\"events\"", loads.Replace("\\\"", "\""));        // the text is quoted in the YAML
        Assert.Contains("dialect: \"ansi\"", loads);                                                // the dialect and the hash of its profile are in the plan
        Assert.Contains("dialect_hash: ", loads);
    }

    [Fact]
    public void Compile_writes_the_read_in_the_committed_dialect_and_carries_its_name_and_hash()
    {
        Project();
        Update(new FakeServer(new ServerFacts("ClickHouse", "24.1", "`", "case_insensitive")));
        Assert.Contains("dialect: ansi", LockText);                                                 // a product the tool does not know: ansi, with the quote the server reported
        Assert.Contains("quote: \"`\"", LockText);
        var (exit, output, err) = Compile();
        Assert.Equal(0, exit);
        Assert.DoesNotContain("DDB-458", err + output);
        var loads = File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "loads.yml"));
        Assert.Contains("`id`, `label` FROM `src`.`events`", loads);
        var config = ProjectConfigLoader.LoadFromProject(dir, []);
        Assert.Contains($"dialect_hash: \"{DialectDiscovery.Hash(config.DialectOf("proxy_erp"))}\"", loads);
        // the check of the committed files holds: the same compile again changes nothing
        var check = new StringWriter();
        Assert.Equal(0, CliApp.Run(["project", "compile", "--check", "--project", dir], check, new StringWriter(), environment: _ => null));
    }

    [Fact]
    public void A_deploy_plan_carries_the_dialect_of_a_flightsql_origin()
    {
        Project();
        Update(new FakeServer(Postgres));
        var ctx = ProjectContext.Load(dir);
        var copy = ctx.Project.Models.Single(m => m.Name == "dst.events");
        var origin = ctx.OriginsOf(copy, "sqlserver").Single();
        var (_, spec) = DbDataBuild.Planning.Planner.CopyTransfer(ctx.Config, copy, DbDataBuild.Targets.TargetRegistry.Get("sqlserver").CreateDdl(ctx.Config.ForConnection("sqlserver")), origin);
        Assert.Equal("postgres", spec.Dialect);
        Assert.Equal(DialectDiscovery.Hash(ctx.Config.DialectOf("proxy_erp")), spec.DialectHash);
        var step = new DbDataBuild.Planning.PlanStep("", DbDataBuild.Planning.StepType.Transfer, copy.Name, "copy", "SELECT 1", DbDataBuild.Planning.RiskClass.Safe, ["copy.transfer"], null, [], Transfer: spec);
        // a connection that is not a Flight SQL one carries neither
        var plain = DbDataBuild.Planning.Planner.CopyTransfer(ctx.Config, copy, DbDataBuild.Targets.TargetRegistry.Get("sqlserver").CreateDdl(ctx.Config.ForConnection("sqlserver")), origin with { Connection = "files", Engine = "duckdb" }).Spec;
        Assert.Null(plain.Dialect);
        Assert.NotNull(step.Transfer);
    }

    // ---- the refresh's origin check ----

    private async Task<string?> Check(FakeServer server)
    {
        var config = ProjectConfigLoader.LoadFromProject(dir, []);
        var access = new OriginAccess(config, Env, Open: (login, ct) => Task.FromResult(ReadSession.ForTesting(server, "flightsql", login.Dialect)));
        return await access.CheckAsync("proxy_erp", default);
    }

    [Fact]
    public async Task The_origin_check_passes_while_the_server_agrees_and_fails_the_origin_when_it_does_not()
    {
        Project();
        Update(new FakeServer(Postgres));
        Assert.Null(await Check(new FakeServer(Postgres)));
        Assert.Null(await Check(new FakeServer(Postgres with { ServerVersion = "17.0" })));          // a new version changes no text
        var changed = await Check(new FakeServer(SqlServer));
        Assert.NotNull(changed);
        Assert.Contains("a different dialect than the one committed in dialects.lock.yml", changed);
        Assert.Contains("the dialect was `postgres` and is now `sqlserver`", changed);
        Assert.Contains("DDB-460", changed);
        Assert.Contains("project update", changed);
        var quiet = await Check(new FakeServer(null));                                              // a server that now says nothing is another dialect than the committed one
        Assert.Contains("the dialect was `postgres` and is now `ansi`", quiet);
    }

    [Fact]
    public async Task The_origin_check_asks_nothing_of_a_connection_with_no_committed_dialect()
    {
        Project();
        var server = new FakeServer(Postgres);
        Assert.Null(await Check(server));
        Assert.Equal(0, server.Asked);
    }
}
