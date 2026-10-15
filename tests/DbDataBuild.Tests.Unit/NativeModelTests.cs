using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Native models: a table computed by an engine-native query, bound by queries from its declared columns and inlined as a derived table.</summary>
public class NativeModelTests
{
    private const string Cols = "grain: [n]\ncolumns:\n  - {name: n, type: INTEGER, nullable: false}\n";

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Project(string config = "defaults: {connections: [sqlserver]}\nparameters:\n  top: { type: INTEGER, value: 5 }\n")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config);
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: _ => null), o.ToString(), e.ToString());
    }

    private static string Diags(string dir) => string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Select(DiagnosticFormatter.Format));

    [Fact]
    public void The_text_is_inline_or_in_a_file_beside_the_definition_and_declares_columns_like_a_mapped_model()
    {
        var dir = Project();
        Write(dir, "models/src/a.yml", "name: src.a\nkind:\n  native:\n    query: SELECT 1 AS n\n" + Cols);
        Write(dir, "models/src/b.yml", "name: src.b\nkind: native\n" + Cols);
        Write(dir, "models/src/b.native.sql", "SELECT 2 AS n\n");
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, Diags(dir));
        Assert.Equal(["src.a", "src.b"], result.NativeModels.Select(n => n.Name).Order());
        Assert.Equal("SELECT 2 AS n", result.NativeModels.Single(n => n.Name == "src.b").Native!.Text);
        Assert.Empty(result.MappedModels);                                                           // not a mapped model, and not a model
        Assert.Equal(2, result.DeclaredModelsAndStaging.Count(d => d.IsNative));                              // but a queryable table
        Assert.Equal(["n"], result.NativeModels[0].Columns.Select(c => c.Name));
    }

    [Theory]
    [InlineData("kind:\n  native:\n    query: SELECT 1 AS n\n    access: command\n", "the connection must allow it")]
    [InlineData("kind:\n  native:\n    query: SELECT 1 AS n\n    access: maybe\n", "not `maybe`")]
    [InlineData("kind: native\n", "needs its text")]
    [InlineData("kind:\n  native:\n    query: EXEC dbo.p\n", "not a single SELECT")]
    [InlineData("kind:\n  native:\n    query: SELECT 1 AS n; DROP TABLE t\n", "not a single SELECT")]
    [InlineData("kind:\n  native:\n    query: WITH x AS (SELECT 1 AS n) SELECT n FROM x\n", "WITH cannot sit inside the derived table")]
    [InlineData("kind:\n  native:\n    query: SELECT ${project.nothing} AS n\n", "has no value")]
    [InlineData("kind:\n  native:\n    query: SELECT ${origin.x} AS n\n", "is not a parameter a native text can use")]
    [InlineData("kind:\n  native:\n    query: SELECT 1 AS n\nconnections=: [sqlserver, postgres]\n", "exactly one connection")]
    public void A_native_model_that_cannot_work_says_why(string kind, string expected)
    {
        var dir = Project();
        Write(dir, "models/src/a.yml", "name: src.a\n" + kind + Cols);
        var text = Diags(dir);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void Text_in_both_places_or_text_with_no_native_model_is_refused()
    {
        var dir = Project();
        Write(dir, "models/src/a.yml", "name: src.a\nkind:\n  native:\n    query: SELECT 1 AS n\n" + Cols);
        Write(dir, "models/src/a.native.sql", "SELECT 2 AS n\n");
        Assert.Contains("not both", Diags(dir));
        Write(dir, "models/src/c.native.sql", "SELECT 3 AS n\n");
        Assert.Contains("is native text, but `models/src/c.yml` is not a native model", Diags(dir));
    }

    [Fact]
    public void A_model_that_reads_a_native_select_gets_its_text_as_a_derived_table_with_the_native_models_own_placeholders()
    {
        var dir = Project();
        Write(dir, "models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    query: SELECT CAST(value AS int) AS n FROM STRING_SPLIT('1,2', ',') WHERE 1 < ${project.top}\n" + Cols);
        Write(dir, "models/marts/m.yml", "name: marts.m\nkind: full\n" + Cols);
        Write(dir, "models/marts/m.sql", "SELECT x.n FROM src.nums AS x WHERE x.n > 0\n");
        var (exit, _, err) = Cli("project", "compile", "--project", dir);
        Assert.True(exit == 0, err);
        var script = File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/marts.m/load.default.sql"));
        Assert.Contains("FROM (SELECT CAST(value AS int) AS n FROM STRING_SPLIT('1,2', ',') WHERE 1 < @p_native_src_nums__project_top) AS x", script);   // the author's alias, the text is the engine's own
        Assert.Contains("@p_native_src_nums__project_top (INTEGER, parameter)", script);
        Assert.Equal(0, Cli("project", "compile", "--check", "--project", dir).Exit);
    }

    [Fact]
    public void A_native_model_is_a_table_on_its_connection_only()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\nconnections:\n  pg: { engine: postgres }\n");
        Write(dir, "models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    query: SELECT 1 AS n\nconnections=: [pg]\n" + Cols);
        Write(dir, "models/marts/m.yml", "name: marts.m\nkind: full\n" + Cols);
        Write(dir, "models/marts/m.sql", "SELECT n FROM src.nums\n");
        var (exit, _, err) = Cli("project", "compile", "--project", dir);
        Assert.NotEqual(0, exit);
        Assert.Contains("DDB-231", err);
    }

    [Fact]
    public void A_native_model_can_be_copied_locally_or_to_another_connection_and_never_alone()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\nconnections:\n  pg: { engine: postgres }\n");
        Write(dir, "models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    query: SELECT 1 AS n\nconnections=: [pg]\n" + Cols);
        Write(dir, "models/pgcopy/snap.yml", "name: pgcopy.snap\nkind:\n  copy:\n    from: src.nums\nconnections=: [pg]\n");   // local
        Write(dir, "models/dst/snap.yml", "name: dst.snap\nkind:\n  copy:\n    from: src.nums\n");                              // remote (the default connection)
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, Diags(dir));
        Assert.True(result.BuiltModels.Single(s => s.Definition.Name == "pgcopy.snap").Definition.LocalCopy);
        Assert.False(result.BuiltModels.Single(s => s.Definition.Name == "dst.snap").Definition.LocalCopy);
    }

    [Fact]
    public void A_view_over_a_native_select_renders_and_the_plan_is_where_parameters_are_refused()
    {
        var dir = Project();
        Write(dir, "models/src/plain.yml", "name: src.plain\nkind:\n  native:\n    query: SELECT 1 AS n\n" + Cols);
        Write(dir, "models/src/bound.yml", "name: src.bound\nkind:\n  native:\n    query: SELECT 1 AS n WHERE 1 < ${project.top}\n" + Cols);
        Write(dir, "models/marts/v1.yml", "name: marts.v1\nkind: view\n" + Cols);
        Write(dir, "models/marts/v1.sql", "SELECT n FROM src.plain\n");
        Write(dir, "models/marts/v2.yml", "name: marts.v2\nkind: view\n" + Cols);
        Write(dir, "models/marts/v2.sql", "SELECT n FROM src.bound\n");
        Assert.Equal(0, Cli("project", "compile", "--project", dir).Exit);                   // rendering views has no load script; the plan is where the refusal is (conformance tests plan and apply)
    }

    [Fact]
    public void Reads_place_a_native_model_in_the_graph_and_the_build_order_and_a_missing_list_is_a_note()
    {
        var dir = Project();
        Write(dir, "models/marts/stock.yml", "name: marts.stock\nkind: full\n" + Cols);
        Write(dir, "models/marts/stock.sql", "SELECT 1 AS n\n");
        Write(dir, "models/src/a.yml", "name: src.a\nreads: [marts.stock]\nkind:\n  native:\n    query: SELECT n FROM marts.stock\n" + Cols);
        Write(dir, "models/src/b.yml", "name: src.b\nkind:\n  native:\n    query: SELECT 2 AS n\n" + Cols);
        Write(dir, "models/marts/top.yml", "name: marts.top\nkind: full\n" + Cols);
        Write(dir, "models/marts/top.sql", "SELECT n FROM src.a UNION ALL SELECT n FROM src.b\n");
        var ctx = ProjectContext.Load(dir);
        Assert.Equal(["marts.stock"], ctx.Graph.Reads("src.a"));
        Assert.Contains("marts.stock", ctx.Graph.Ancestors("marts.top").Keys);                       // through the native model
        Assert.Contains("marts.top", ctx.Graph.Descendants("marts.stock").Keys);
        Assert.Equal(["src.a", "marts.stock"], ctx.WithNativeReads(["src.a"]));
        var notes = ProjectChecks.Reachability(ctx, null).Where(d => d.Code == "DDB-233").ToList();
        Assert.Equal("src.b is a native select and does not declare what it reads, so it has no ancestors in the graph.", Assert.Single(notes).Found);
    }

    [Fact]
    public void Metadata_and_graph_show_a_native_model_with_its_reads_and_the_hash_of_its_text()
    {
        var dir = Project();
        Write(dir, "models/marts/stock.yml", "name: marts.stock\nkind: full\n" + Cols);
        Write(dir, "models/marts/stock.sql", "SELECT 1 AS n\n");
        Write(dir, "models/src/a.yml", "name: src.a\nreads: [marts.stock]\nkind:\n  native:\n    query: SELECT n FROM marts.stock\n" + Cols);
        Write(dir, "models/marts/top.yml", "name: marts.top\nkind: full\n" + Cols);
        Write(dir, "models/marts/top.sql", "SELECT n FROM src.a\n");
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(Cli("project", "show", "metadata", "--project", dir, "--format", "json").Out)!["data"]!;
        var source = metadata["models"]!.AsArray().Single(m => (string?)m!["name"] == "src.a")!;
        Assert.Equal("models/src/a.yml", (string?)source["files"]!["definition"]);
        Assert.Equal("native", (string?)source["kind"]);
        Assert.Equal("dbdatabuild.model/3", (string?)source["schema"]);
        Assert.Null(source["native"]);
        Assert.Equal(["marts.stock:model/full"], source["upstream"]!.AsArray().Select(u => $"{(string?)u!["name"]}:{(string?)u["category"]}/{(string?)u["kind"]}"));
        Assert.Equal("select", (string?)source["kind_settings"]!["access"]);
        Assert.Equal(["marts.stock"], source["kind_settings"]!["reads"]!.AsArray().Select(r => (string?)r));
        Assert.Matches("^[0-9a-f]{64}$", (string?)source["kind_settings"]!["text_hash"]);
        Assert.Contains(metadata["models"]!.AsArray(), m => (string?)m!["name"] == "marts.top");

        var hash = (string?)source["definition_hash"];
        Write(dir, "models/src/a.yml", "name: src.a\nreads: [marts.stock]\nkind:\n  native:\n    query: SELECT n FROM marts.stock WHERE n > 0\n" + Cols);
        var changed = System.Text.Json.Nodes.JsonNode.Parse(Cli("project", "show", "metadata", "--project", dir, "--format", "json").Out)!["data"]!["models"]!.AsArray().Single(m => (string?)m!["name"] == "src.a")!;
        Assert.NotEqual(hash, (string?)changed["definition_hash"]);                                    // the text is part of what the model is

        var graph = System.Text.Json.Nodes.JsonNode.Parse(Cli("project", "show", "graph", "--project", dir, "--format", "json", "marts.top").Out)!["data"]!;
        Assert.Equal(["marts.stock:full", "marts.top:full", "src.a:native"], graph["nodes"]!.AsArray().Select(n => $"{(string?)n!["name"]}:{(string?)n["kind"]}").Order(StringComparer.Ordinal));
        Assert.Contains(graph["edges"]!.AsArray(), e => (string?)e!["from"] == "marts.stock" && (string?)e["to"] == "src.a");
    }

    [Fact]
    public void Track_definition_lists_routine_names_and_belongs_to_a_native_model()
    {
        var dir = Project();
        Write(dir, "models/src/a.yml", "name: src.a\ntrack_definition: [dbo.fn_open, \"public.fn_open(date, int)\"]\nkind:\n  native:\n    query: SELECT 1 AS n\n" + Cols);
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, Diags(dir));
        Assert.Equal(["dbo.fn_open", "public.fn_open(date, int)"], result.NativeModels.Single().Native!.TrackDefinition);

        Write(dir, "models/src/a.yml", "name: src.a\ntrack_definition: [\"dbo.fn; DROP TABLE x\"]\nkind:\n  native:\n    query: SELECT 1 AS n\n" + Cols);
        Assert.Contains("is not a routine name", Diags(dir));

        Write(dir, "models/src/m.yml", "name: src.m\ntrack_definition: [dbo.fn]\nkind: mapped\n" + Cols);
        Assert.Contains("belongs to a native model", Diags(dir));
    }

    [Fact]
    public void A_changed_definition_is_a_warning_unless_the_policy_says_error()
    {
        Assert.Equal(Severity.Warning, ProjectConfig.Default.Policy[PolicyKeys.NativeDefinitionChanged]);
        Assert.Contains(PolicyKeys.NativeDefinitionChanged, PolicyKeys.All);
    }
}
