using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Project files in folders (`_dbdatabuild.yml`): their `defaults:` layer onto the root file's and under the model's own file, the nearest winning.</summary>
public class ProjectLayersTests
{
    private const string Columns = "columns:\n  - {name: order_id, type: BIGINT, nullable: false}\n";
    private const string Sql = "SELECT order_id FROM staging.orders\n";

    private static string Project(string config)
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        return dir;
    }

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static void Model(string dir, string name, string yaml)
    {
        var stem = "models/" + name.Replace('.', '/');
        Write(dir, stem + ".yml", $"name: {name}\n{yaml}{Columns}");
        Write(dir, stem + ".sql", Sql);
    }

    private static (ProjectValidationResult Result, List<string> Messages) Validate(string dir)
    {
        var result = ProjectValidator.Validate(dir);
        return (result, result.Diagnostics.Select(DiagnosticFormatter.Format).ToList());
    }

    private static ModelDefinition Defined(string dir, string name)
    {
        var (result, messages) = Validate(dir);
        Assert.False(result.HasErrors, string.Join("\n", messages));
        return result.Models.Single(m => m.Name == name);
    }

    private const string TwoConnections = "connections:\n  wh_sql: { engine: sqlserver }\n  wh_pg: { engine: postgres }\n";

    [Fact]
    public void A_model_takes_what_it_does_not_say_from_the_folders_above_it_nearest_first()
    {
        var dir = Project("defaults:\n  connections: [wh_sql]\n  kind: view\n" + TwoConnections);
        Write(dir, "models/crm/_dbdatabuild.yml", "defaults:\n  kind: full\n");
        Write(dir, "models/crm/eu/_dbdatabuild.yml", "defaults:\n  connections=: [wh_pg]\n");
        Model(dir, "crm.orders", "");                      // the root's connections, the folder's kind
        Model(dir, "crm.eu.orders", "");                   // the folder beneath replaces the connections
        Model(dir, "crm.eu.own", "kind: view\n");  // and the model's own file is the nearest of all

        var orders = Defined(dir, "crm.orders");
        Assert.Equal($"{ModelKinds.Full} wh_sql", $"{orders.Kind} {string.Join(",", orders.Targets!)}");
        var eu = Defined(dir, "crm.eu.orders");
        Assert.Equal($"{ModelKinds.Full} wh_pg", $"{eu.Kind} {string.Join(",", eu.Targets!)}");
        Assert.Equal(ModelKinds.View, Defined(dir, "crm.eu.own").Kind);
    }

    [Fact]
    public void Lists_append_and_a_suffix_changes_that_for_the_key()
    {
        var dir = Project("defaults:\n  connections: [wh_sql]\n" + TwoConnections);
        Model(dir, "marts.both", "kind: full\nconnections: [wh_pg]\n");           // appended to the inherited list
        Model(dir, "marts.pg_only", "kind: full\nconnections=: [wh_pg]\n");       // replaces it
        Model(dir, "marts.removed", "kind: full\nconnections-: [wh_sql]\nconnections+: [wh_pg]\n");   // removes one, adds one
        Assert.Equal(["wh_sql", "wh_pg"], Defined(dir, "marts.both").Targets);
        Assert.Equal(["wh_pg"], Defined(dir, "marts.pg_only").Targets);
        Assert.Equal(["wh_pg"], Defined(dir, "marts.removed").Targets);
    }

    [Fact]
    public void A_folder_project_file_is_not_a_model_and_a_problem_in_it_is_reported_once_where_it_is()
    {
        var dir = Project("");
        Write(dir, "models/crm/_dbdatabuild.yml", "defaults:\n  kind: full\nsurprise: {a: 1}\n");
        Model(dir, "crm.a", "");
        Model(dir, "crm.b", "");
        var (result, messages) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Single(messages, m => m.Contains("Unknown key `surprise` in _dbdatabuild.yml"));
        Assert.All(result.Diagnostics, d => Assert.DoesNotContain("orphan", d.Found, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, d => d.Location.File == "models/crm/_dbdatabuild.yml" && d.Location.Line == 3);
    }

    [Fact]
    public void A_problem_in_an_inherited_setting_is_reported_in_the_file_that_has_it()
    {
        var dir = Project("");
        Write(dir, "models/crm/_dbdatabuild.yml", "defaults:\n  kind: nope\n");
        Model(dir, "crm.a", "");
        var (result, _) = Validate(dir);
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(("models/crm/_dbdatabuild.yml", 2), (d.Location.File, d.Location.Line));
    }

    [Theory]
    [InlineData("kind: full\ncolumns=: []\n", "DDB-104")]                     // `columns` does not inherit, so a suffix on it is just an unknown key
    [InlineData("kind: [full]\n", "DDB-248")]                                          // a list is not a kind; the shape says so
    [InlineData("kind: full\nconnections: [nowhere]\n", "DDB-106")]
    public void A_model_that_cannot_merge_or_validate_says_why(string yaml, string code)
    {
        var dir = Project("defaults:\n  kind: view\n");
        Write(dir, "models/m.yml", "name: m\n" + yaml + (yaml.Contains("columns") ? "" : Columns));
        Write(dir, "models/m.sql", Sql);
        var (result, _) = Validate(dir);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public void Validate_shows_what_each_model_inherited_and_where_it_came_from_and_the_metadata_carries_it()
    {
        var dir = Project("defaults:\n  connections: [wh_sql]\n" + TwoConnections);
        Write(dir, "models/crm/_dbdatabuild.yml", "defaults:\n  kind: full\n");
        Model(dir, "crm.a", "");
        Model(dir, "marts.own", "kind: view\nconnections=: [wh_sql]\n");
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(0, CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null));
        var text = o.ToString();
        Assert.Contains("Inherited by crm.a: connections[0] = wh_sql (dbdatabuild.yml:2), kind = full (models/crm/_dbdatabuild.yml:2)", text);
        Assert.DoesNotContain("Inherited by marts.own", text);                           // it says everything itself

        var j = new StringWriter();
        Assert.Equal(0, CliApp.Run(["project", "show", "metadata", "--project", dir, "--format", "json"], j, new StringWriter(), environment: _ => null));
        var model = JsonNode.Parse(j.ToString())!["data"]!["models"]!.AsArray().Single(m => (string?)m!["name"] == "crm.a")!;
        Assert.Equal(["connections[0]|dbdatabuild.yml|2", "kind|models/crm/_dbdatabuild.yml|2"],
            model["inherited"]!.AsArray().Select(i => $"{i!["path"]}|{i["file"]}|{i["line"]}"));
    }

    [Fact]
    public void Renders_follow_the_effective_connections()
    {
        var dir = Project("defaults:\n  connections: [wh_sql]\n" + TwoConnections);
        Write(dir, "models/lake/_dbdatabuild.yml", "defaults:\n  connections=: [wh_pg]\n  kind: full\n");
        Model(dir, "lake.events", "");
        var o = new StringWriter(); var e = new StringWriter();
        Assert.True(RenderFiles("--write", "--project", dir).Exit == 0);
        Assert.True(File.Exists(Path.Combine(dir, "rendered/wh_pg/lake.events/load.default.sql")));
        Assert.False(Directory.Exists(Path.Combine(dir, "rendered/wh_sql")));
    }

    [Theory]
    [InlineData("defaults: [a]\n", "DDB-106")]
    [InlineData("defaults:\n  surprise: 1\n", "DDB-104")]
    [InlineData("defaults:\n  connections: [nowhere]\n", "DDB-106")]
    [InlineData("defaults:\n  connections: []\n", "DDB-106")]
    public void The_root_defaults_are_checked(string yaml, string code)
    {
        var diags = new List<Diagnostic>();
        Assert.Null(ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags));
        Assert.Contains(diags, d => d.Code == code);
    }
}
