using DbDataBuild.Cli;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The model selector grammar is one piece of code (<see cref="ModelSelection"/>) that every command uses, and it works the same on a mapped or native model as on a built one: `kind:mapped`, `+model`, tags, properties,
/// connections, paths, patterns and exclusions, for the project's selectors and for `project update` alike.
/// </summary>
public class ModelSelectionTests
{
    private static string Project()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults:\n  connections: [sqlserver]\n");
        foreach (var d in new[] { "staging", "src", "stg", "marts" }) Directory.CreateDirectory(Path.Combine(dir, "models", d));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ntags: [raw]\nproperties: {tier: 1}\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/src/ext.yml"), "name: src.ext\nkind:\n  native:\n    query: SELECT order_id FROM staging.orders\nreads: [staging.orders]\nconnections: [sqlserver]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/stg/orders.yml"), "name: stg.orders\nkind: view\ntags: [clean]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/stg/orders.sql"), "SELECT order_id FROM staging.orders\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/big.yml"), "name: marts.big\nkind: full\ntags: [clean, big]\nproperties: {tier: 2}\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/big.sql"), "SELECT o.order_id FROM stg.orders o JOIN src.ext e ON e.order_id = o.order_id\n");
        return dir;
    }

    private static readonly string[] All = ["marts.big", "src.ext", "staging.orders", "stg.orders"];

    public static TheoryData<string, string> Terms => new()
    {
        { "kind:mapped", "staging.orders" },
        { "kind:native", "src.ext" },
        { "kind:view", "stg.orders" },
        { "kind:full", "marts.big" },
        { "tag:raw", "staging.orders" },
        { "tag:clean", "marts.big,stg.orders" },
        { "property:tier=1", "staging.orders" },
        { "property:tier", "marts.big,staging.orders" },
        { "connection:sqlserver", "marts.big,src.ext,staging.orders,stg.orders" },
        { "path:models/staging", "staging.orders" },
        { "path:models/src", "src.ext" },
        { "staging.*", "staging.orders" },
        { "s*.ext", "src.ext" },
        { "models/src/ext.yml", "src.ext" },
        { "models/staging", "staging.orders" },
        { "+src.ext", "src.ext,staging.orders" },            // the tables a native model says it reads are its ancestors
        { "src.ext+", "marts.big,src.ext" },
        { "+marts.big", "marts.big,src.ext,staging.orders,stg.orders" },
        { "1+marts.big", "marts.big,src.ext,stg.orders" },
        { "@stg.orders", "marts.big,src.ext,staging.orders,stg.orders" },
        { "kind:mapped,tag:raw", "staging.orders" },
    };

    private static string[] Names(IEnumerable<ProjectModel>? models) => models!.Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();

    [Theory, MemberData(nameof(Terms))]
    public void A_term_selects_models_of_every_kind_the_same_way(string selector, string expected)
    {
        var ctx = ProjectContext.Load(Project());
        var error = new StringWriter();
        Assert.Equal(expected.Split(','), Names(ctx.SelectModels([selector], error)));
        Assert.Equal("", error.ToString());
    }

    [Theory, MemberData(nameof(Terms))]
    public void Project_update_selects_with_the_same_grammar_and_gets_the_same_models(string selector, string expected)
    {
        var ctx = ProjectContext.Load(Project());
        var update = UpdateSelection.Resolve(ctx, [selector], new StringWriter())!;
        Assert.False(update.Everything);
        Assert.Equal(expected.Split(','), update.Names.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void No_argument_selects_every_model_and_an_exclusion_takes_models_out_of_it()
    {
        var ctx = ProjectContext.Load(Project());
        Assert.Equal(All, Names(ctx.SelectModels([], TextWriter.Null)));
        Assert.Equal(["marts.big", "src.ext", "stg.orders"], Names(ctx.SelectModels(["exclude:kind:mapped"], TextWriter.Null)));
        Assert.Equal(["marts.big", "src.ext"], Names(ctx.SelectModels(["+marts.big", "exclude:tag:raw", "exclude:stg.orders"], TextWriter.Null)));
        Assert.Equal(["src.ext", "staging.orders"], Names(ctx.SelectModels(["kind:mapped", "kind:native"], TextWriter.Null)));          // several arguments are a union
    }

    [Fact]
    public void The_commands_that_build_get_the_built_models_of_the_selection_and_are_told_when_there_are_none()
    {
        var ctx = ProjectContext.Load(Project());
        Assert.Equal(["marts.big", "stg.orders"], ctx.Select(["+marts.big"], TextWriter.Null)!.Select(m => m.Model.Definition.Name));      // the mapped and native models under it are in the selection, and not built
        var error = new StringWriter();
        Assert.Null(ctx.Select(["kind:mapped"], error));
        Assert.Contains("the tool builds none of them", error.ToString());
        Assert.Contains("mapped", error.ToString());
        Assert.Equal(2, ctx.Select([], TextWriter.Null)!.Count);                                                                          // no argument: every built model
        Assert.Empty(ctx.SelectModels(["kind:view,tag:raw"], TextWriter.Null)!);                                                          // an intersection that is empty is an answer
        var none = new StringWriter();
        Assert.Null(ctx.SelectModels(["kind:snapshot"], none));                                                                             // a term that selects nothing says so
        Assert.Contains("selects no model", none.ToString());
    }

    [Fact]
    public void The_projection_has_the_seven_kinds_and_the_facts_a_selector_reads()
    {
        var ctx = ProjectContext.Load(Project());
        var byName = ctx.Models.ToDictionary(m => m.Name);
        Assert.Equal(("mapped", false, "models/staging/orders.yml"), (byName["staging.orders"].Kind, byName["staging.orders"].IsBuilt, byName["staging.orders"].Path));
        Assert.Equal(("native", false), (byName["src.ext"].Kind, byName["src.ext"].IsBuilt));
        Assert.Equal(("full", true, "models/marts/big.sql"), (byName["marts.big"].Kind, byName["marts.big"].IsBuilt, byName["marts.big"].Path));
        Assert.Equal(["big", "clean"], byName["marts.big"].Tags.Order(StringComparer.Ordinal));
        Assert.Equal(["sqlserver"], byName["staging.orders"].Connections);               // a declared model that names none is on the project's default connections
        Assert.All(ModelKinds.Declared, k => Assert.False(ModelKinds.IsBuilt(k)));
        Assert.Equal(7, ModelKinds.Every.Count);
    }

    [Fact]
    public void The_grammar_is_written_once()
    {
        // a second copy of the filters in another file is how two commands come to disagree about what `kind:` or `tag:` means
        var root = PolyglotBindingTests.RepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.EndsWith("ProjectModel.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("StartsWith(\"tag:\"", StringComparison.Ordinal) || File.ReadAllText(f).Contains("StartsWith(\"property:\"", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.Empty(offenders);
    }
}
