using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Mcp;
using DbDataBuild.Tui.Model;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The connection a command works on is its first positional argument (`connection deploy <c>`); the option `--connection` is gone from those commands, with no alias.</summary>
public class PositionalConnectionTests
{
    private static string Project(string config)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        Directory.CreateDirectory(Path.Combine(dir, "models", "staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        return dir;
    }

    private const string One = "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\n";
    private const string Two = "defaults: {connections: [sqlserver, postgres]}\ntracking: { connection: postgres }\n";

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: _ => null), o.ToString(), e.ToString());
    }

    public static TheoryData<string[]> ConnectionCommands => new()
    {
        { new[] { "connection", "inspect" } },
        { new[] { "connection", "init" } },
        { new[] { "connection", "status" } },
        { new[] { "connection", "deploy" } },
        { new[] { "connection", "refresh" } },
        { new[] { "connection", "monitor" } },
        { new[] { "connection", "compare", "staging.orders" } },
        { new[] { "connection", "seed" } },
        { new[] { "connection", "publish" } },
        { new[] { "project", "import" } },
        { new[] { "ui", "terminal" } },
    };

    [Theory, MemberData(nameof(ConnectionCommands))]
    public void The_old_option_is_refused_and_nothing_runs(string[] command)
    {
        var dir = Project(One);
        var before = Snapshot(dir);
        var r = Run([.. command, "--connection", "sqlserver", "--project", dir]);
        Assert.NotEqual(0, r.Exit);
        Assert.Contains("--connection", r.Err + r.Out);                                    // the parser names what it does not know
        Assert.DoesNotContain("effect:", r.Out);                                            // the command did not start
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void No_command_of_the_tree_that_works_on_one_connection_has_the_option()
    {
        var root = CliApp.Build(TextWriter.Null, TextWriter.Null, TextReader.Null, interactive: false);
        foreach (var spec in CommandSpecs.All.Where(s => s.Path[0] == "connection" || s.Name is "project import" or "ui terminal"))
        {
            var command = CliApp.Find(root, spec.Name)!;
            Assert.DoesNotContain(command.Options, o => o.Name == "--connection");
            var first = command.Arguments[0];
            Assert.Equal("connection", first.Name);                                         // the connection comes first
            Assert.Equal(0, first.Arity.MinimumNumberOfValues);                              // and may be left out
        }
        // the ones that take a list of connections as a filter keep the option
        foreach (var name in new[] { "project compile", "project model create" }) Assert.Contains(CliApp.Find(root, name)!.Options, o => o.Name == "--connection");
    }

    [Fact]
    public void The_connection_is_the_first_argument_and_may_be_left_out_when_the_project_has_one_default()
    {
        var one = Project(One);
        var left = Run("connection", "init", "--project", one);
        Assert.Equal(0, left.Exit);
        Assert.Contains("connection: sqlserver", left.Out);
        var named = Run("connection", "init", "sqlserver", "--project", one);
        Assert.Equal(0, named.Exit);
        Assert.Contains("connection: sqlserver", named.Out);

        var two = Project(Two);
        var pg = Run("connection", "init", "postgres", "--project", two);
        Assert.Equal(0, pg.Exit);
        Assert.Contains("connection: postgres", pg.Out);
        Assert.Contains("\"run_log\"", pg.Out);
        var neither = Run("connection", "init", "--project", two);
        Assert.Equal(CliApp.ExitUsage, neither.Exit);
        Assert.Contains("A connection is required", neither.Err);
        Assert.Contains("connection status sqlserver", neither.Err);                       // the message shows the new way
        var elsewhere = Run("connection", "init", "sqlserver", "--project", two);
        Assert.Contains("keeps no records of its own: they go to `postgres`. Initialize that one: `dbdatabuild connection init postgres`", elsewhere.Err);
        var unknown = Run("connection", "init", "oracle", "--project", two);
        Assert.Equal(CliApp.ExitUsage, unknown.Exit);
        Assert.Contains("Unknown connection `oracle`", unknown.Err);
    }

    [Fact]
    public void A_command_that_takes_models_reads_the_first_argument_as_the_connection_only_when_it_is_one()
    {
        var dir = Project(Two);
        void Is(string? connection, string[] models, string? first, params string[] rest)
        {
            var (c, r) = CliApp.SplitConnection(dir, first, rest);
            Assert.Equal(connection, c);
            Assert.Equal(models, r);
        }
        Is("postgres", ["marts.a"], "postgres", "marts.a");
        Is(null, ["marts.a", "marts.b"], "marts.a", "marts.b");      // a model, so the default connection
        Is(null, ["marts"], "marts");                                 // a directory of models
        Is(null, [], null);
        Is("sqlserver", [], "sqlserver");
        Is(null, ["postgres_copy"], "postgres_copy");                 // a name is a connection only when it is exactly one
        Is(null, ["Postgres"], "Postgres");                           // and the case counts
    }

    [Fact]
    public void A_selector_that_is_not_a_model_says_what_the_connections_are()
    {
        var dir = Project(One);
        var r = Run("project", "show", "metadata", "sqlservre", "--project", dir);
        Assert.Equal(CliApp.ExitUsage, r.Exit);
        Assert.Contains("does not name a valid model", r.Err);
        Assert.Contains("If it was meant to be a connection", r.Err);
        Assert.Contains("sqlserver", r.Err);
    }

    [Fact]
    public void Compare_takes_the_connection_first_and_the_table_after_it_and_the_table_alone_uses_the_default()
    {
        var dir = Project(Two);
        var withConnection = Run("connection", "compare", "postgres", "staging.orders", "--against-schema", "dev", "--key", "order_id", "--project", dir);
        Assert.Contains("DBDATABUILD_POSTGRES_READ", withConnection.Err + withConnection.Out);          // no login in this environment: it says whose
        var tableOnly = Run("connection", "compare", "staging.orders", "--against-schema", "dev", "--key", "order_id", "--project", dir);
        Assert.Equal(CliApp.ExitUsage, tableOnly.Exit);                                                    // two default connections: it must be named
        Assert.Contains("A connection is required", tableOnly.Err);

        var one = Project(One);
        var viaDefault = Run("connection", "compare", "staging.orders", "--against-schema", "dev", "--key", "order_id", "--project", one);
        Assert.Contains("DBDATABUILD_SQLSERVER_READ", viaDefault.Err + viaDefault.Out);
        var none = Run("connection", "compare", "--project", one);
        Assert.Equal(CliApp.ExitUsage, none.Exit);
        Assert.Contains("The table to compare is required", none.Err);
    }

    [Fact]
    public void Import_takes_the_connection_then_the_patterns_and_the_patterns_alone_use_the_default()
    {
        var dir = Project(Two);
        var named = Run("project", "import", "postgres", "staging.*", "--project", dir);
        Assert.Contains("DBDATABUILD_POSTGRES_READ", named.Err + named.Out);
        var patternOnly = Run("project", "import", "staging.*", "--project", dir);
        Assert.Equal(CliApp.ExitUsage, patternOnly.Exit);
        var one = Project(One);
        var viaDefault = Run("project", "import", "staging.*", "--project", one);
        Assert.Contains("DBDATABUILD_SQLSERVER_READ", viaDefault.Err + viaDefault.Out);
    }

    [Fact]
    public void The_hints_and_headers_name_the_connection_the_new_way()
    {
        var dir = Project(One);
        var r = Run("connection", "init", "--project", dir);
        Assert.Contains("run `dbdatabuild connection init sqlserver --apply`", r.Out);
        Assert.DoesNotContain("--connection", r.Out + r.Err);
        var json = JsonNode.Parse(Run("connection", "init", "--project", dir, "--format", "json").Out)!;
        Assert.Contains(json["next"]!.AsArray().Select(n => (string)n!), n => n.EndsWith("connection init sqlserver --apply", StringComparison.Ordinal));
    }

    [Fact]
    public void The_terminal_form_and_the_mcp_tool_take_the_connection_as_the_first_argument()
    {
        var catalog = TuiCommand.CliHost.Catalog();
        var deploy = new FormModel(catalog.Single(c => c.Name == "connection deploy"), ".");
        Assert.Equal(["connection", "models"], deploy.Fields.Where(f => f.IsArgument).Select(f => f.Label));
        Assert.False(deploy.Fields.Single(f => f.Label == "connection").Required);
        Assert.DoesNotContain(deploy.Fields, f => f.Label == "--connection");
        deploy.Field("connection").Value = "postgres";
        deploy.Field("models").Value = "marts.a";
        Assert.Equal("dbdatabuild connection deploy postgres marts.a --project .", deploy.CommandLine().Replace("--project '.'", "--project ."));

        var surface = new ToolSurface(".", catalog, withholdWriteFlags: false);
        var tool = surface.Describe(catalog.Single(c => c.Name == "connection deploy"));
        var inputs = (JsonObject)tool["inputSchema"]!["properties"]!;
        Assert.True(inputs.ContainsKey("connection"));
        Assert.DoesNotContain("connection", tool["inputSchema"]!["required"]!.AsArray().Select(n => (string)n!));
        Assert.True(surface.TryBuildArguments(catalog.Single(c => c.Name == "connection deploy"), new JsonObject { ["connection"] = "postgres", ["models"] = new JsonArray("marts.a"), ["write_plan"] = true }, out var argv, out var problem), problem);
        Assert.Equal(["connection", "deploy", "postgres", "marts.a", "--write-plan", "--project", Path.GetFullPath(".")], argv.Select(a => a == "." ? Path.GetFullPath(".") : a));
    }
}
