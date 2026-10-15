using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Parameters: one concept, four scopes in files (project, connection, origin, model). Layered like any project setting, except a model's own, which are only in its file.</summary>
public class ParametersTests
{
    private const string Config = "parameters:\n  region: eu\n  cutoff: { type: DATE, value: \"2024-01-01\" }\n  limit: { type: BIGINT, value: 5 }\nconnections:\n  wh: { engine: sqlserver, parameters: { site: main } }\ndefaults: {connections: [wh]}\n";

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Project(string config = Config)
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config);
        return dir;
    }

    private static void Model(string dir, string name, string extra = "")
    {
        var stem = "models/" + name.Replace('.', '/');
        Write(dir, stem + ".yml", $"name: {name}\nkind: view\n{extra}columns:\n  - {{name: a, type: BIGINT}}\n");
        Write(dir, stem + ".sql", "SELECT 1 AS a\n");
    }

    [Fact]
    public void A_parameter_is_a_value_or_a_typed_value()
    {
        var cfg = ProjectConfigLoader.Load(Config, "dbdatabuild.yml", [])!;
        Assert.Equal(new ParameterValue("eu"), cfg.Parameters["region"]);
        Assert.Equal(new ParameterValue("2024-01-01", "DATE"), cfg.Parameters["cutoff"]);
        Assert.Equal(new ParameterValue("5", "BIGINT"), cfg.Parameters["limit"]);
        Assert.Equal("main", cfg.Connections["wh"].Parameters["site"].Value);
    }

    [Theory]
    [InlineData("parameters: [a]\n")]
    [InlineData("parameters: { Bad: x }\n")]
    [InlineData("parameters: { a: [1] }\n")]
    [InlineData("parameters: { a: { type: DATE, value: yesterday } }\n")]
    [InlineData("parameters: { a: { type: GEOGRAPHY, value: x } }\n")]
    [InlineData("parameters: { a: { type: DATE } }\n")]
    [InlineData("parameters: { a: { type: DATE, value: \"2024-01-01\", color: red } }\n")]
    public void A_parameter_that_cannot_be_read_is_refused(string yaml)
    {
        var diags = new List<Diagnostic>();
        Assert.Null(ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags));
        Assert.NotEmpty(diags);
    }

    [Fact]
    public void A_model_sees_the_project_parameters_the_folders_above_it_override_and_its_own_and_never_inherits_the_second()
    {
        var dir = Project();
        Write(dir, "models/eu/_dbdatabuild.yml", "parameters:\n  region: eu-west\nconnections:\n  wh:\n    parameters: { site: branch }\n");
        Write(dir, "models/eu/uk/_dbdatabuild.yml", "parameters:\n  region: uk\n");
        Model(dir, "eu.a");
        Model(dir, "eu.uk.b", "parameters: { tone: loud }\n");
        Model(dir, "root.c");
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(DiagnosticFormatter.Format)));
        var cfg = ProjectConfigLoader.LoadFromProject(dir, []);
        string Seen(string model, string key) => result.BuiltModels.Single(s => s.Definition.Name == model).ParametersFor(cfg, "wh")[key].Value;
        Assert.Equal("eu", Seen("root.c", "project.region"));                 // the root's
        Assert.Equal("eu-west", Seen("eu.a", "project.region"));               // a folder overrides it for what is beneath
        Assert.Equal("uk", Seen("eu.uk.b", "project.region"));                 // the nearest wins
        Assert.Equal("main", Seen("root.c", "connection.site"));
        Assert.Equal("branch", Seen("eu.a", "connection.site"));               // a folder may override a connection's parameter
        Assert.Equal("loud", Seen("eu.uk.b", "model.tone"));
        Assert.DoesNotContain("model.tone", result.BuiltModels.Single(s => s.Definition.Name == "eu.a").ParametersFor(cfg, "wh").Keys);   // a model's parameters are its own
        Assert.Equal("2024-01-01", Seen("eu.a", "project.cutoff"));            // untouched ones come through
    }

    [Theory]
    [InlineData("parameters: [a]\n")]
    [InlineData("connections: { wh: { engine: sqlserver } }\n")]
    [InlineData("connections: { wh: { parameters: [x] } }\n")]
    [InlineData("parameters: { a: { type: DATE, value: nope } }\n")]
    public void A_folder_file_cannot_declare_a_connection_and_its_parameters_are_checked(string yaml)
    {
        var dir = Project();
        Write(dir, "models/x/_dbdatabuild.yml", yaml);
        Model(dir, "x.a");
        Assert.True(ProjectValidator.Validate(dir).HasErrors);
    }

    [Fact]
    public void A_model_parameter_is_declared_in_the_model_file_only()
    {
        var dir = Project();
        Write(dir, "models/x/_dbdatabuild.yml", "defaults:\n  parameters: { tone: loud }\n");
        Model(dir, "x.a");
        var d = Assert.Single(ProjectValidator.Validate(dir).Diagnostics, x => x.Found.Contains("Unknown key `parameters` in `defaults`"));
        Assert.Equal("models/x/_dbdatabuild.yml", d.Location.File);
    }

    [Fact]
    public void A_slice_may_use_project_and_model_parameters_and_a_missing_one_is_named()
    {
        var dir = Project("parameters:\n  region: eu\nconnections:\n  crm: { engine: postgres, parameters: { id: \"1\" } }\n  wh: { engine: sqlserver }\ndefaults: {connections: [wh]}\n");
        Write(dir, "models/crm/c.yml", "name: crm.c\nkind: mapped\nconnections=: [crm]\ngrain: [a]\ncolumns:\n  - {name: a, type: BIGINT, nullable: false}\n");
        Write(dir, "models/dst/c.yml", "name: dst.c\nkind:\n  copy:\n    from: crm.c\n    slice: {column: region, value: \"${project.region}-${origin.id}\", type: \"VARCHAR(20)\"}\n");
        var ok = ProjectValidator.Validate(dir);
        Assert.False(ok.HasErrors, string.Join("\n", ok.Diagnostics.Select(DiagnosticFormatter.Format)));
        Write(dir, "models/dst/c.yml", "name: dst.c\nkind:\n  copy:\n    from: crm.c\n    slice: {column: region, value: \"${project.nothing}-${origin.id}\", type: \"VARCHAR(20)\"}\n");
        Assert.Contains(ProjectValidator.Validate(dir).Diagnostics, d => d.Found.Contains("there is no such project parameter"));
    }

    // ---- parameters as values in a model's query ----

    private const string Staging = "name: staging.t\nkind: mapped\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: region, type: \"VARCHAR(20)\"}\n  - {name: d, type: DATE}\n  - {name: ts, type: TIMESTAMP}\n  - {name: n, type: INTEGER}\n";
    private const string Marts = "name: marts.m\nkind: full\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: region, type: \"VARCHAR(20)\"}\n";
    private const string Typed = "parameters:\n  region: eu\n  cutoff: { type: DATE, value: \"2024-01-03\" }\n  since: { type: TIMESTAMP, value: \"2024-01-04 00:00:00\" }\n  limit: { type: BIGINT, value: 5 }\n  small: { type: INTEGER, value: 7 }\n  tiny: { type: SMALLINT, value: 3 }\ndefaults: {connections: [sqlserver, postgres]}\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C, sqlserver: Latin1_General_100_BIN2 }\n";
    private const string Query = "SELECT id, region FROM staging.t WHERE region = ${project.region} AND d >= ${project.cutoff} AND ts >= ${project.since} AND n < ${project.small} AND id < ${project.limit} AND n > ${project.tiny}\n";

    private static readonly string OnSqlServer = Typed[..Typed.IndexOf("defaults:", StringComparison.Ordinal)] + "defaults: {connections: [sqlserver]}\n";

    private static string QueryProject(string sql = Query, string config = Typed, string martsExtra = "")
    {
        var dir = Project(config);
        Write(dir, "models/staging/t.yml", Staging);
        Write(dir, "models/marts/m.yml", martsExtra + Marts);
        Write(dir, "models/marts/m.sql", sql);
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (DbDataBuild.Cli.CliApp.Run(args, o, e, environment: _ => null), o.ToString(), e.ToString());
    }

    [Fact]
    public void A_parameter_in_a_query_renders_as_a_placeholder_on_every_engine_and_the_value_is_never_in_the_text()
    {
        var dir = QueryProject();
        var (exit, _, err) = RenderFiles("--write", "--project", dir);
        Assert.True(exit == 0, err);
        foreach (var connection in new[] { "sqlserver", "postgres" })
        {
            var script = File.ReadAllText(Path.Combine(dir, "rendered", connection, "marts.m", "load.default.sql"));
            foreach (var p in new[] { "region", "cutoff", "since", "small", "limit", "tiny" }) Assert.Contains($"@p_project_{p}", script);
            Assert.Contains("@p_project_cutoff (DATE, parameter)", script);                                   // declared, with its type
            Assert.Contains("@p_project_limit (BIGINT, parameter)", script);
            Assert.DoesNotContain("'eu'", script);
            Assert.DoesNotContain("__ddb_param", script);                                                   // no marker is left behind
            Assert.DoesNotContain("7400000000", script);
        }
        // the committed lowered query shows the references, not the markers
        var lowered = File.ReadAllText(Path.Combine(dir, "rendered", "lowered", "marts.m", "lowered.sql"));
        Assert.Contains("${project.region}", lowered);
        Assert.DoesNotContain("__ddb_param", lowered);
    }

    [Fact]
    public void A_value_change_changes_no_load_script_and_a_reference_change_does()
    {
        var dir = QueryProject();
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), Typed.Replace("region: eu", "region: us").Replace("value: 5", "value: 50"));
        var changed = RenderFiles("--check", "--project", dir);
        Assert.Equal(1, changed.Exit);                                                                      // the values are bound at run time, not rendered: only the load plans (`loads.yml`), which record them, and the plan that names them by hash differ
        Assert.Contains("loads.yml", changed.Err);
        Assert.DoesNotContain("load.", changed.Err);
        Assert.DoesNotContain("lowered.sql", changed.Err);
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        Assert.Equal(0, RenderFiles("--check", "--project", dir).Exit);
        File.WriteAllText(Path.Combine(dir, "models/marts/m.sql"), Query.Replace("${project.region}", "${project.limit}").Replace("region = ", "id = "));
        Assert.NotEqual(0, RenderFiles("--check", "--project", dir).Exit);
    }

    [Theory]
    [InlineData("SELECT id, region FROM staging.t WHERE region = ${project.nothing}\n", "has no value")]
    [InlineData("SELECT id, region FROM staging.t WHERE region = ${connection.site}\n", "on the connection `sqlserver`")]
    [InlineData("SELECT id, region FROM staging.t WHERE region = ${model.tone}\n", "the model's file")]
    [InlineData("SELECT id, region FROM staging.t WHERE region = ${origin.x}\n", "only for a copy's slice")]
    [InlineData("SELECT id, region FROM staging.t WHERE region = ${weird.x}\n", "is not a parameter scope")]
    [InlineData("SELECT id, region FROM staging.t WHERE n = 2100000001 AND id < ${project.small}\n", "already contains")]
    public void A_reference_that_cannot_be_bound_says_why(string sql, string expected)
    {
        var dir = QueryProject(sql, Typed);
        var (exit, _, err) = Cli("project", "compile", "--project", dir);
        Assert.NotEqual(0, exit);
        Assert.Contains(expected, err);
    }

    [Fact]
    public void A_view_cannot_use_a_parameter_because_ddl_binds_no_values_and_a_table_can()
    {
        var dir = QueryProject("SELECT id, region FROM staging.t WHERE region = ${project.region}\n", OnSqlServer);
        File.WriteAllText(Path.Combine(dir, "models/marts/m.yml"), Marts.Replace("kind: full", "kind: view"));
        var (exit, _, err) = Cli("project", "compile", "--project", dir);
        Assert.NotEqual(0, exit);
        Assert.Contains("a view's query cannot use parameters", err);
        File.WriteAllText(Path.Combine(dir, "models/marts/m.yml"), Marts);
        Assert.Equal(0, Cli("project", "compile", "--project", dir).Exit);
    }

    [Fact]
    public void Connection_parameters_may_differ_per_connection_and_model_parameters_are_the_models_own()
    {
        var cfg = Typed + "connections:\n  sqlserver: { parameters: { site: a } }\n  postgres: { parameters: { site: b } }\n";
        var dir = QueryProject("SELECT id, region FROM staging.t WHERE region = ${connection.site} AND n = ${model.n}\n", cfg, "parameters:\n  n: { type: INTEGER, value: 4 }\n");
        var (exit, _, err) = RenderFiles("--write", "--project", dir);
        Assert.True(exit == 0, err);
        Assert.Contains("@p_connection_site", File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/marts.m/load.default.sql")));
        Assert.Contains("@p_model_n (INTEGER, parameter)", File.ReadAllText(Path.Combine(dir, "rendered/postgres/marts.m/load.default.sql")));
    }

    [Fact]
    public void Sample_runs_the_query_with_the_values_filled_in()
    {
        var dir = QueryProject("SELECT id, region FROM staging.t WHERE region = ${project.region}\n");
        var (exit, output, err) = Cli("project", "sample", "marts.m", "--project", dir, "--rows", "20", "--format", "json");
        Assert.True(exit == 0, output + err);                                                              // DuckDB ran the real query: the literal is a string, so it binds
    }
}
