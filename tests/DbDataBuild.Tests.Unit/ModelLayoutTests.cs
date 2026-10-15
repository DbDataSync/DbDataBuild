using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>A model's name is its definition's `name:`; `model_layout` says whether the files must spell it (`schema_name/object_name.yml`, `schema_name.object_name.yml`, `object.yml`), or not (`none`).</summary>
public class ModelLayoutTests
{
    private const string Cols = "grain: [n]\ncolumns:\n  - {name: n, type: INTEGER, nullable: false}\n";

    private static string Project(string layout, params (string Path, string Name)[] models)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\ntracking: none\n" + (layout.Length == 0 ? "" : $"model_layout: {layout}\n"));
        foreach (var (path, name) in models)
        {
            var full = Path.Combine(dir, "models", path + ".yml");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, $"name: {name}\nkind: full\n{Cols}");
            File.WriteAllText(Path.Combine(dir, "models", path + ".sql"), "SELECT 1 AS n\n");
        }
        return dir;
    }

    private static string Problems(string dir) => string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Code + " " + d.Found));

    [Fact]
    public void The_default_layout_is_the_path_as_before_and_the_name_may_be_anything_with_none()
    {
        Assert.Equal("", Problems(Project("", ("marts/fct", "marts.fct"))));
        Assert.Contains("name is `marts.other`, but the path implies `marts.fct`", Problems(Project("folder", ("marts/fct", "marts.other"))));
        Assert.Equal("", Problems(Project("none", ("anywhere/at/all", "marts.fct"))));
        var result = ProjectValidator.Validate(Project("none", ("anywhere/at/all", "marts.fct")));
        Assert.Equal("marts.fct", result.BuiltModels.Single().Definition.Name);
        Assert.Equal("models/anywhere/at/all.sql", result.BuiltModels.Single().QueryFile);                 // the query is the file beside the definition
    }

    [Fact]
    public void Dotted_means_a_file_called_schema_dot_object_in_any_folder()
    {
        Assert.Equal("", Problems(Project("dotted", ("finance/marts.fct", "marts.fct"), ("misc/staging.orders", "staging.orders"))));
        var problems = Problems(Project("dotted", ("marts/fct", "marts.fct")));
        Assert.Contains("`model_layout: dotted` expects the file to be called `marts.fct.yml`", problems);
    }

    [Fact]
    public void Object_means_a_file_called_object_in_any_folder_and_the_schema_comes_from_the_name()
    {
        Assert.Equal("", Problems(Project("object", ("finance/fct", "marts.fct"), ("misc/orders", "staging.orders"))));
        Assert.Contains("`model_layout: object` expects the file to be called `fct.yml`", Problems(Project("object", ("finance/fact", "marts.fct"))));
    }

    [Fact]
    public void A_name_belongs_to_one_model_wherever_the_files_are()
    {
        var problems = Problems(Project("none", ("a/one", "marts.fct"), ("b/two", "marts.fct")));
        Assert.Contains("`marts.fct` is the name of `models/a/one.yml` too", problems);
    }

    [Fact]
    public void An_unknown_layout_is_refused_and_the_files_of_a_mapped_model_are_where_it_was_written()
    {
        var bad = Project("scattered", ("marts/fct", "marts.fct"));
        var diags = new List<Diagnostic>();
        ProjectConfigLoader.LoadFromProject(bad, diags);
        Assert.Contains(diags, d => d.Found.Contains("`model_layout` is `scattered`"));

        var dir = Project("none");
        Directory.CreateDirectory(Path.Combine(dir, "models", "elsewhere"));
        File.WriteAllText(Path.Combine(dir, "models", "elsewhere", "orders.yml"), "name: erp.orders\nkind: mapped\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
        var ctx = ProjectContext.Load(dir);
        Assert.Equal("models/elsewhere/orders.yml", ctx.Project.MappedModels.Single().File);
        Assert.Equal("models/elsewhere/orders.yml", MetadataBuilder.ModelFile(ctx.Project.MappedModels.Single()));
    }

    [Fact]
    public void Import_writes_a_new_mapped_model_where_the_layout_puts_it()
    {
        Assert.Equal("models/erp/orders.yml", DeclaredModelWriter.PathFor("erp", "orders"));
        Assert.Equal("models/erp.orders.yml", DeclaredModelWriter.PathFor("erp", "orders", ModelLayout.Dotted));
        Assert.Equal("models/orders.yml", DeclaredModelWriter.PathFor("erp", "orders", ModelLayout.Object));
        Assert.Null(DeclaredModelWriter.PathFor("er.p", "orders", ModelLayout.Dotted));
    }

    [Fact]
    public void Define_follows_the_definitions_name_wherever_the_file_is_and_asks_for_one_when_the_layout_cannot_name_a_new_model()
    {
        var dir = Project("none", ("anywhere/at/all", "marts.fct"));
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(0, CliApp.Run(["project", "model", "update", "--project", dir, "--check"], o, e, environment: _ => null));

        var objectDir = Project("object");
        File.WriteAllText(Path.Combine(objectDir, "models", "fresh.sql"), "SELECT 1 AS n\n");
        var o2 = new StringWriter(); var e2 = new StringWriter();
        CliApp.Run(["project", "model", "update", "--project", objectDir, "--check"], o2, e2, environment: _ => null);
        Assert.Contains("its file name does not say the schema", e2.ToString() + o2.ToString());
    }
}
