using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Connections: named endpoints with an engine, so two servers of one engine can be told apart. A connection named after an engine needs no declaration.</summary>
public class ConnectionsTests
{
    private static ProjectConfig Load(string yaml, List<Diagnostic>? diags = null) => ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags ?? [])!;

    [Fact]
    public void A_connection_named_after_an_engine_exists_without_a_declaration_and_declaring_it_sets_its_version()
    {
        var plain = Load("defaults: {connections: [sqlserver]}\n");
        Assert.Equal(["fabric", "postgres", "sqlserver"], plain.Connections.Keys.Order());
        Assert.All(plain.Connections, c => Assert.Equal(c.Key, c.Value.Engine));
        Assert.Empty(plain.TargetVersions);
        Assert.Equal(16, Load("connections:\n  sqlserver: { version: 16 }\n").Connections["sqlserver"].Version);
    }

    [Fact]
    public void Any_other_name_says_its_engine_and_two_connections_of_one_engine_have_their_own_versions()
    {
        var cfg = Load("defaults: {connections: [wh_old]}\nconnections:\n  wh_old: { engine: sqlserver, version: 16 }\n  wh_new: { engine: sqlserver, version: 17 }\n  lake: { engine: postgres }\n");
        Assert.Equal("sqlserver", cfg.EngineOf("wh_old"));
        Assert.Equal("sqlserver", cfg.EngineOf("wh_new"));
        Assert.Equal("postgres", cfg.EngineOf("lake"));
        Assert.Equal(16, cfg.TargetVersions["wh_old"]);
        Assert.Equal(17, cfg.TargetVersions["wh_new"]);
        Assert.False(cfg.TargetVersions.ContainsKey("lake"));
        Assert.Null(cfg.EngineOf("nowhere"));
        Assert.Contains("connections: lake (postgres), wh_new (sqlserver), wh_old (sqlserver)", cfg.Describe());
    }

    [Theory]
    [InlineData("connections:\n  warehouse: { version: 16 }\n", "DDB-105")]                                         // no engine
    [InlineData("connections:\n  warehouse: { engine: oracle }\n", "DDB-106")]                                      // an engine the tool does not know
    [InlineData("connections:\n  postgres: { engine: sqlserver }\n", "DDB-106")]                                    // a name that is an engine, with another engine
    [InlineData("connections:\n  a: { engine: postgres }\n  A: { engine: postgres }\n", "DDB-102")]                 // differ only in case: one login variable
    [InlineData("connections:\n  \"9bad\": { engine: postgres }\n", "DDB-106")]
    [InlineData("connections:\n  has-dash: { engine: postgres }\n", "DDB-106")]
    [InlineData("connections: [a]\n", "DDB-106")]
    [InlineData("defaults: {connections: [warehouse]}\n", "DDB-106")]                                                         // not declared
    public void A_connection_that_cannot_work_is_refused_with_the_reason(string yaml, string code)
    {
        var diags = new List<Diagnostic>();
        Assert.Null(ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags));
        Assert.Contains(diags, d => d.Code == code);
    }

    [Fact]
    public void The_login_of_a_connection_is_its_name_in_capitals_and_never_falls_back_to_another()
    {
        Assert.Equal("DBDATABUILD_WAREHOUSE_READ", LoginSettings.VariableName("warehouse", Login.Read));
        Assert.Equal("DBDATABUILD_WH_NEW_WRITE", LoginSettings.VariableName("wh_new", Login.Write));
        var env = new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_READ"] = "Server=a;User Id=u;Password=p", ["DBDATABUILD_WH_NEW_READ"] = "Server=b;User Id=v;Password=q" };
        var (own, _) = LoginSettings.FromEnvironment("wh_new", "sqlserver", Login.Read, env.GetValueOrDefault);
        Assert.Equal(("wh_new", "sqlserver", "v"), (own!.Connection, own.Engine, own.User));
        var (missing, error) = LoginSettings.FromEnvironment("wh_old", "sqlserver", Login.Read, env.GetValueOrDefault);
        Assert.Null(missing);
        Assert.Contains("connection `wh_old`", error!.Found);              // the other connection's login is not used
    }

    [Fact]
    public void A_model_may_name_a_declared_connection_and_only_a_declared_one()
    {
        const string Yaml = "name: marts.m\nkind: full\nconnections: [wh]\ncolumns:\n  - {name: a, type: BIGINT}\n";
        var known = new HashSet<string> { "wh", "sqlserver" };
        var ok = new List<Diagnostic>();
        Assert.Equal(["wh"], ModelDefinitionLoader.Load(Yaml, "m.yml", "marts.m", ok, known)!.Targets);
        var bad = new List<Diagnostic>();
        Assert.Null(ModelDefinitionLoader.Load(Yaml.Replace("[wh]", "[elsewhere]"), "m.yml", "marts.m", bad, known));
        Assert.Contains(bad, d => d.Found.Contains("Unknown connection `elsewhere`"));
    }

    // ---- through the commands ----

    private const string Source = "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: note, type: \"VARCHAR(40)\"}\n";

    private static string Project(string config, params (string Name, string Targets, string Sql)[] models)
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), Source);
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        foreach (var (name, targets, sql) in models)
        {
            var stem = Path.Combine(dir, "models", name.Replace('.', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
            File.WriteAllText(stem + ".yml", $"name: {name}\nkind: full\nconnections: [{targets}]\ncolumns:\n  - {{name: order_id, type: BIGINT, nullable: false}}\n  - {{name: label, type: \"VARCHAR(40)\"}}\n");
            File.WriteAllText(stem + ".sql", sql);
        }
        return dir;
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: _ => null), o.ToString(), e.ToString());
    }

    [Fact]
    public void Each_connection_renders_into_its_own_directory_in_its_engines_dialect()
    {
        var dir = Project("defaults: {connections: [wh_sql]}\nconnections:\n  wh_sql: { engine: sqlserver }\n  wh_pg: { engine: postgres }\n  wh_sql_two: { engine: sqlserver }\n",
            ("marts.orders", "wh_sql, wh_pg, wh_sql_two", "SELECT order_id, upper(note) AS label FROM staging.orders\n"));
        var (exit, _, err) = RenderFiles("--write", "--project", dir);
        Assert.True(exit == 0, err);
        string Script(string connection) => File.ReadAllText(Path.Combine(dir, "rendered", connection, "marts.orders", "load.default.sql"));
        Assert.Contains("-- connection:      wh_sql\n", Script("wh_sql"));                                      // the header names the connection
        Assert.Contains("SET XACT_ABORT ON", Script("wh_sql"));
        Assert.Contains("SET XACT_ABORT ON", Script("wh_sql_two"));                                           // two connections of one engine: the same dialect, separate files
        Assert.DoesNotContain("XACT_ABORT", Script("wh_pg"));
        Assert.Contains("BEGIN", Script("wh_pg"));
        Assert.False(Directory.Exists(Path.Combine(dir, "rendered", "sqlserver")));                           // nothing for a connection the project did not use
        Assert.Equal(0, RenderFiles("--check", "--project", dir).Exit);
        Assert.Equal(CliApp.ExitUsage, RenderFiles("--project", dir, "--connection", "nowhere", "--content").Exit);
    }

    [Fact]
    public void The_version_belongs_to_the_connection_so_one_engine_can_have_a_new_and_an_old_server()
    {
        const string Sql = "SELECT order_id, regexp_extract(note, 'a(b)', 1) AS label FROM staging.orders\n";
        var dir = Project("defaults: {connections: [wh_new]}\nconnections:\n  wh_old: { engine: sqlserver, version: 16 }\n  wh_new: { engine: sqlserver, version: 17 }\n", ("marts.orders", "wh_old, wh_new", Sql));
        var (exit, output, err) = Run("project", "compile", "--project", dir);
        var all = output + err;
        Assert.Contains("needs wh_old version 17 or later, but the project configures version 16", all);       // the old server is refused with the reason
        Assert.DoesNotContain("needs wh_new version", all);                                                    // the new one is not
        Assert.NotEqual(0, exit);
        var rendered = RenderFiles("--project", dir, "--connection", "wh_new", "--content");
        Assert.Contains("REGEXP_SUBSTR(", rendered.Out);                                                       // and version 17 gets the regular expression written for it
    }

    [Fact]
    public void A_command_that_needs_one_connection_names_it_in_its_header_and_asks_for_that_connections_login()
    {
        var dir = Project("defaults: {connections: [wh_sql]}\ntracking: { connection: wh_pg }\nconnections:\n  wh_sql: { engine: sqlserver }\n  wh_pg: { engine: postgres }\n");
        var (exit, output, err) = Run("connection", "init", "wh_pg", "--project", dir, "--apply");
        Assert.NotEqual(0, exit);
        Assert.Contains("connection: wh_pg", output);
        Assert.Contains("DBDATABUILD_WH_PG_WRITE", err + output);                                             // not DBDATABUILD_POSTGRES_WRITE
        var script = Run("connection", "init", "wh_pg", "--project", dir);
        Assert.Contains("CREATE SCHEMA", script.Out);
        Assert.DoesNotContain("[", script.Out.Split('\n').Where(l => l.StartsWith("CREATE", StringComparison.Ordinal)).FirstOrDefault() ?? "");      // PostgreSQL quoting, not SQL Server's
    }
}
