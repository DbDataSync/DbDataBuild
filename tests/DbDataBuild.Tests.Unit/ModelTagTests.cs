using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`tags:` on a model, inherited and merged like `lint_ignore`, and `tag:` in a model selector.</summary>
public class ModelTagTests
{
    private static string Project()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults:\n  connections: [sqlserver]\n  tags: [everywhere]\n");
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        Directory.CreateDirectory(Path.Combine(dir, "models/finance"));
        Directory.CreateDirectory(Path.Combine(dir, "models/ops"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/finance/_dbdatabuild.yml"), "defaults:\n  tags: [finance]\n");
        void Model(string path, string name, string tags)
        {
            File.WriteAllText(Path.Combine(dir, "models", path + ".yml"), $"name: {name}\nkind: view\n{tags}columns:\n  - {{name: order_id, type: BIGINT, nullable: false}}\n");
            File.WriteAllText(Path.Combine(dir, "models", path + ".sql"), "SELECT order_id FROM staging.orders\n");
        }
        Model("finance/revenue", "finance.revenue", "tags: [critical]\n");
        Model("finance/costs", "finance.costs", "tags=: [only_mine]\n");
        Model("finance/margin", "finance.margin", "tags-: [everywhere]\n");
        Model("ops/queue", "ops.queue", "");
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    private static string[] Chosen(string dir, params string[] selectors)
    {
        var r = Cli(["project", "show", "metadata", .. selectors, "--project", dir, "--format", "json"]);
        Assert.Equal(0, r.Exit);
        return JsonNode.Parse(r.Out)!["data"]!["models"]!.AsArray().Select(m => (string)m!["name"]!).Order().ToArray();
    }

    [Fact]
    public void Tags_merge_down_the_project_files_and_tag_selects_the_models_that_carry_one()
    {
        var dir = Project();
        Assert.Equal(["finance.margin", "finance.revenue"], Chosen(dir, "tag:finance"));
        Assert.Equal(["finance.revenue"], Chosen(dir, "tag:critical"));
        Assert.Equal(["finance.costs"], Chosen(dir, "tag:only_mine"));                              // `tags=` replaces what was inherited
        Assert.Equal(["finance.revenue", "ops.queue", "staging.orders"], Chosen(dir, "tag:everywhere"));              // the project's tag reaches the models that do not remove it, mapped ones too
        Assert.DoesNotContain("finance.margin", Chosen(dir, "tag:everywhere"));                     // `tags-` removes one
        Assert.Equal(["finance.revenue"], Chosen(dir, "tag:finance,tag:critical"));                 // an intersection with another term
    }

    [Fact]
    public void The_metadata_document_lists_the_tags_and_a_model_without_any_has_none()
    {
        var dir = Project();
        var r = Cli("project", "show", "metadata", "finance.revenue", "ops.queue", "--project", dir, "--format", "json");
        var models = JsonNode.Parse(r.Out)!["data"]!["models"]!.AsArray();
        Assert.Equal(["critical", "everywhere", "finance"], models.Single(m => (string?)m!["name"] == "finance.revenue")!["tags"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(["everywhere"], models.Single(m => (string?)m!["name"] == "ops.queue")!["tags"]!.AsArray().Select(t => (string)t!));
    }

    [Fact]
    public void A_tag_nobody_carries_is_a_usage_error_and_a_malformed_tag_is_a_diagnostic()
    {
        var dir = Project();
        var none = Cli("project", "show", "metadata", "tag:nothing", "--project", dir);
        Assert.Equal(CliApp.ExitUsage, none.Exit);
        Assert.Contains("no model has the tag `nothing`", none.Err);
        File.WriteAllText(Path.Combine(dir, "models/ops/queue.yml"), "name: ops.queue\nkind: view\ntags: [\"bad tag\"]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        Assert.Contains("`bad tag` is not a tag", Cli("project", "compile", "--project", dir).Err);
    }
}
