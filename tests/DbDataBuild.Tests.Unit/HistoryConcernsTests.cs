using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Planning;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>DDB-240: an unacknowledged history inconsistency (DDB-443) reaches the models a plan is about to load, through column lineage.</summary>
public class HistoryConcernsTests
{
    private static string Project(string config = "")
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n" + config);
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        Directory.CreateDirectory(Path.Combine(dir, "models/stg"));
        Directory.CreateDirectory(Path.Combine(dir, "models/marts"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
        void Model(string path, string name, string kind, string sql, string columns)
        {
            File.WriteAllText(Path.Combine(dir, "models", path + ".yml"), $"name: {name}\nkind: {kind}\ncolumns:\n{columns}");
            File.WriteAllText(Path.Combine(dir, "models", path + ".sql"), sql + "\n");
        }
        Model("stg/orders", "stg.orders", "view", "SELECT order_id, amount FROM staging.orders", "  - {name: order_id, type: BIGINT}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
        Model("marts/fct", "marts.fct", "view", "SELECT order_id, amount * 2 AS double_amount FROM stg.orders", "  - {name: order_id, type: BIGINT}\n  - {name: double_amount, type: \"DECIMAL(18, 3)\"}\n");
        Model("marts/ids", "marts.ids", "view", "SELECT order_id FROM stg.orders", "  - {name: order_id, type: BIGINT}\n");
        Model("marts/chain", "marts.chain", "view", "SELECT double_amount + 1 AS x FROM marts.fct", "  - {name: x, type: \"DECIMAL(19, 3)\"}\n");
        return dir;
    }

    private static ColumnHistoryEntry Open(string model, string column) =>
        new(model, column, "backfill_later", "text", NeedsAttention: true, AckKey: $"DDB-443|{model}.{column}|p1");

    [Fact]
    public void A_planned_model_built_from_the_column_is_reported_through_every_model_in_between()
    {
        var ctx = ProjectContext.Load(Project());
        var findings = HistoryConcerns.Check(ctx, ["marts.fct", "marts.ids", "stg.orders"], [Open("stg.orders", "amount")]);
        var f = Assert.Single(findings);
        Assert.Equal("DDB-240", f.Code);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("marts.fct is built from a column whose history is inconsistent (`double_amount` comes from stg.orders.amount)", f.Found);
        var chained = Assert.Single(HistoryConcerns.Check(ctx, ["marts.chain"], [Open("stg.orders", "amount")]));
        Assert.Contains("(`x` comes from stg.orders.amount > marts.fct.double_amount)", chained.Found);
        Assert.Contains("--ack history:stg.orders.amount", f.Fix);
    }

    [Fact]
    public void The_owner_of_the_column_a_model_that_does_not_read_it_and_an_acknowledged_entry_are_not_reported()
    {
        var ctx = ProjectContext.Load(Project());
        Assert.Empty(HistoryConcerns.Check(ctx, ["stg.orders"], [Open("stg.orders", "amount")]));
        Assert.Empty(HistoryConcerns.Check(ctx, ["marts.ids"], [Open("stg.orders", "amount")]));
        Assert.Empty(HistoryConcerns.Check(ctx, ["marts.fct"], [Open("stg.orders", "amount") with { NeedsAttention = false }]));
        Assert.Empty(HistoryConcerns.Check(ctx, ["marts.fct"], []));
    }

    [Theory]
    [InlineData("policy:\n  severity:\n    history_inconsistency: error\n", Severity.Error)]
    [InlineData("policy:\n  severity:\n    history_inconsistency: note\n", Severity.Note)]
    public void The_policy_sets_the_severity_and_an_error_is_what_refuses_the_plan(string config, Severity expected)
    {
        var ctx = ProjectContext.Load(Project(config));
        Assert.Equal(expected, Assert.Single(HistoryConcerns.Check(ctx, ["marts.fct"], [Open("stg.orders", "amount")])).Severity);
    }
}
