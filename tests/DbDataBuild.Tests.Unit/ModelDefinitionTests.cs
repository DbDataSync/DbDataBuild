using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class ModelDefinitionTests
{
    [Fact]
    public void Design_document_example_is_valid()
    {
        var (def, diags) = Load(ValidModel);
        Assert.Empty(diags);
        Assert.NotNull(def);
        Assert.Equal("marts.fct_orders", def!.Name);
        Assert.Equal(ModelKinds.IncrementalByUniqueKey, def.Kind);
        Assert.Equal(["order_id"], def.UniqueKey);
        Assert.Equal(["sqlserver", "fabric"], def.Targets);
        Assert.Equal(3, def.Columns.Count);
        Assert.False(def.Columns[0].Nullable);
        Assert.True(def.Columns[1].Nullable);
        Assert.Equal("DECIMAL(14, 2)", def.Columns[2].Type);
    }

    [Fact]
    public void Missing_unique_key_matches_the_documented_diagnostic()
    {
        var yaml = ValidModel.Replace("    unique_key: [order_id]\n", "");
        var (_, diags) = Load(yaml);
        var d = Assert.Single(diags, x => x.Code == "DDB-214");
        Assert.Equal(
            "error DDB-214  models/marts/fct_orders.yml:3\n" +
            "  Kind incremental_by_unique_key requires a unique_key, but none is set.\n" +
            "  Supported: unique_key: [<column>, ...]\n" +
            "  Fix: add under `incremental_by_unique_key:`  unique_key: [order_id]\n" +
            "  Docs: dbdatabuild explain DDB-214\n",
            DiagnosticFormatter.Format(d));
    }

    [Fact]
    public void All_problems_are_reported_in_one_pass()
    {
        var (def, diags) = Load("""
            name: marts.wrong
            surprise: 1
            kind: incremental_by_unique_key
            connections: [oracle]
            columns:
              - name: a
                type: INT
                nullable: maybe
            """);
        Assert.Null(def);
        var codes = diags.Select(d => d.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { "DDB-104", "DDB-106", "DDB-107", "DDB-214", "DDB-216" }, codes);
    }

    [Theory]
    [InlineData("name: x\ncolumns: [{name: a, type: INT}]\n", "DDB-105")]               // missing kind
    [InlineData("kind: full\ncolumns: [{name: a, type: INT}]\n", "DDB-105")]       // missing name
    [InlineData("name: marts.fct_orders\nkind: full\n", "DDB-105")]                // columns required for all models
    [InlineData("name: marts.fct_orders\nkind: nope\ncolumns: [{name: a, type: INT}]\n", "DDB-106")]
    [InlineData("name: marts.fct_orders\nkind: {full: {unique_key: [a]}}\ncolumns: [{name: a, type: INT}]\n", "DDB-104")]
    [InlineData("name: marts.fct_orders\nkind: incremental_by_time_range\ngrain: [a]\ncolumns: [{name: a, type: INT}]\n", "DDB-215")]
    [InlineData("name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [a]}}\ncolumns: [{name: a, type: INT}]\n", "DDB-216")]
    [InlineData("name: marts.fct_orders\nkind: full\ngrain: [zzz]\ncolumns: [{name: a, type: INT}]\n", "DDB-217")]
    [InlineData("name: marts.fct_orders\nkind: full\ncolumns: [{name: a, type: INT}, {name: A, type: INT}]\n", "DDB-102")]
    public void Each_rule_produces_its_code(string yaml, string code)
    {
        var (def, diags) = Load(yaml);
        Assert.Null(def);
        Assert.Contains(diags, d => d.Code == code);
    }

    [Fact]
    public void Empty_file_is_a_diagnostic()
    {
        var (def, diags) = Load("");
        Assert.Null(def);
        Assert.Equal("DDB-105", Assert.Single(diags).Code);
    }

    [Fact]
    public void Non_mapping_root_is_a_diagnostic()
    {
        var (_, diags) = Load("- a\n- b\n");
        Assert.Contains(diags, d => d.Code == "DDB-106");
    }

    [Fact]
    public void Malformed_inputs_never_throw()
    {
        string[] inputs = ["", ":", "[", "{{", "a: &x\n  - *x", "name: [1, [2]]", "kind: 3", "columns: x", "\t\t", "a: |\n  block\n", "a: [1, 2\nb: : :\n", "---\n---\n",
            "name: n\nkind: full\ncolumns:\n  - 1\n  - [a]\n  - {name: [x]}\nrenames: 5\nconnections: {a: b}\n"];
        foreach (var input in inputs)
        {
            var ex = Record.Exception(() => Load(input));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Renames_are_declared_not_inferred_and_must_target_declared_columns()
    {
        var yaml = ValidModel + "\nrenames:\n  - from: cust\n    to: customer_id\n";
        var (def, diags) = Load(yaml);
        Assert.Empty(diags);
        Assert.Equal(new RenameDefinition("cust", "customer_id"), Assert.Single(def!.Renames));

        var (_, bad) = Load(ValidModel + "\nrenames:\n  - from: cust\n    to: nowhere\n");
        Assert.Contains(bad, d => d.Code == "DDB-217");
    }
}
