using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Define;
using DbDataBuild.Models;

namespace DbDataBuild.Tests.Unit;

public class InferenceTests
{
    internal static ModelGraph Graph() => new([], [
        new DeclaredModel("staging.orders",
        [
            new("order_id", "BIGINT", false), new("customer_id", "BIGINT", true), new("amount", "DECIMAL(14, 2)", true),
            new("code", "VARCHAR(20)", false), new("order_date", "DATE", false), new("created_at", "TIMESTAMP", true),
        ], ["order_id"]),
        new DeclaredModel("staging.customers", [new("customer_id", "BIGINT", false), new("name", "VARCHAR(50)", true)], ["customer_id"]),
    ]);

    private static Inference Infer(string sql)
    {
        var (value, diags) = ModelInference.Infer("models/marts/m.sql", sql, Graph());
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return value!;
    }

    private static InferredColumn Col(Inference i, string name) => i.Columns.Single(c => c.Name == name);

    [Fact]
    public void Pass_through_columns_keep_the_upstream_declared_type_and_nullability()
    {
        var i = Infer("SELECT o.order_id, o.customer_id, o.amount, o.code AS discount_code, o.order_date FROM staging.orders AS o");
        Assert.Equal(["order_id", "customer_id", "amount", "discount_code", "order_date"], i.Columns.Select(c => c.Name));

        var code = Col(i, "discount_code");
        Assert.Equal(("VARCHAR(20)", ProposalCertainty.High), (code.Type.LogicalType, code.Type.Certainty));   // the length survives: DuckDB alone would say VARCHAR
        Assert.Contains("passes through staging.orders.code", code.Type.Reason);
        Assert.Equal(false, code.Nullability.Nullable);
        Assert.Equal("DECIMAL(14, 2)", Col(i, "amount").Type.LogicalType);
        Assert.Equal(true, Col(i, "customer_id").Nullability.Nullable);
        Assert.Equal(["staging.orders"], i.UpstreamNames);
    }

    [Fact]
    public void Computed_columns_get_duckdbs_type_and_unlimited_text_stays_unlimited()
    {
        var i = Infer("SELECT o.order_id + 1 AS next_id, o.amount * 2 AS double_amt, COUNT(*) AS n, o.code || '-x' AS tagged, o.order_date + INTERVAL 1 DAY AS shifted FROM staging.orders o GROUP BY ALL");
        Assert.Equal(("BIGINT", ProposalCertainty.High), (Col(i, "next_id").Type.LogicalType, Col(i, "next_id").Type.Certainty));
        Assert.Equal("DECIMAL(18, 2)", Col(i, "double_amt").Type.LogicalType);
        Assert.Equal(("BIGINT", false), (Col(i, "n").Type.LogicalType, Col(i, "n").Nullability.Nullable));
        Assert.Equal(("VARCHAR", ProposalCertainty.High), (Col(i, "tagged").Type.LogicalType, Col(i, "tagged").Type.Certainty));   // no length declared or written: unlimited stays unlimited
        Assert.Contains("unlimited", Col(i, "tagged").Type.Reason);
        Assert.Equal("TIMESTAMP", Col(i, "shifted").Type.LogicalType);           // DuckDB widens DATE + INTERVAL to TIMESTAMP
        Assert.Null(Col(i, "double_amt").Nullability.Nullable);                  // conservative: lineage cannot prove it either way
    }

    [Fact]
    public void A_written_varchar_cast_states_the_length()
    {
        var c = Col(Infer("SELECT CAST(o.code AS VARCHAR(5)) AS c5, CAST(o.code AS VARCHAR) AS c FROM staging.orders o"), "c5");
        Assert.Equal(("VARCHAR(5)", ProposalCertainty.High), (c.Type.LogicalType, c.Type.Certainty));
    }

    [Fact]
    public void An_outer_join_makes_the_right_side_nullable_even_when_declared_not_null()
    {
        var i = Infer("SELECT o.order_id, c.customer_id AS cid, c.name FROM staging.orders o LEFT JOIN staging.customers c ON o.customer_id = c.customer_id");
        Assert.Equal(false, Col(i, "order_id").Nullability.Nullable);
        Assert.Equal(true, Col(i, "cid").Nullability.Nullable);
        Assert.Equal(["staging.customers", "staging.orders"], i.UpstreamNames.Order());
    }

    [Fact]
    public void Star_expands_to_every_upstream_column()
    {
        var i = Infer("SELECT * FROM staging.customers");
        Assert.Equal(["customer_id", "name"], i.Columns.Select(c => c.Name));
        Assert.Equal("VARCHAR(50)", Col(i, "name").Type.LogicalType);
    }

    [Fact]
    public void Lossy_and_unresolvable_types_are_normal_certainty_or_no_proposal()
    {
        var i = Infer("SELECT CAST(o.order_id AS TINYINT) AS t, CAST(o.order_id AS HUGEINT) AS h, [1, 2] AS l FROM staging.orders o");
        Assert.Equal(("TINYINT", ProposalCertainty.Normal), (Col(i, "t").Type.LogicalType, Col(i, "t").Type.Certainty));
        Assert.Null(Col(i, "h").Type.LogicalType);
        Assert.Null(Col(i, "l").Type.LogicalType);
    }

    [Fact]
    public void Grain_candidates_come_from_group_by_distinct_and_passed_through_upstream_keys()
    {
        var grouped = Infer("SELECT o.customer_id, COUNT(*) AS n FROM staging.orders o GROUP BY o.customer_id");
        Assert.Equal(["customer_id"], Assert.Single(grouped.GrainCandidates).Columns);
        Assert.Contains("groups by", grouped.GrainCandidates[0].Evidence);

        var distinct = Infer("SELECT DISTINCT o.customer_id, o.order_date FROM staging.orders o");
        Assert.Equal(["customer_id", "order_date"], Assert.Single(distinct.GrainCandidates).Columns);

        var passthrough = Infer("SELECT o.order_id AS id, o.amount FROM staging.orders o");
        var c = Assert.Single(passthrough.GrainCandidates);
        Assert.Equal(["id"], c.Columns);                                         // the source grain [order_id], under its output name
        Assert.Contains("grain of staging.orders", c.Evidence);
    }

    [Fact]
    public void Joins_and_set_operations_do_not_guess_an_upstream_grain()
    {
        Assert.Empty(Infer("SELECT o.order_id, c.name FROM staging.orders o JOIN staging.customers c ON o.customer_id = c.customer_id").GrainCandidates);
        Assert.Empty(Infer("SELECT order_id AS k FROM staging.orders UNION ALL SELECT customer_id AS k FROM staging.customers").GrainCandidates);
        Assert.Empty(Infer("SELECT o.amount FROM staging.orders o").GrainCandidates);       // the key is not in the output
    }

    [Fact]
    public void Time_column_candidates_are_the_date_and_timestamp_outputs()
    {
        var i = Infer("SELECT o.order_id, o.order_date, o.created_at, o.amount FROM staging.orders o");
        Assert.Equal(["order_date", "created_at"], i.TimeColumnCandidates);
    }

    [Fact]
    public void An_upstream_defined_in_the_same_run_is_visible_to_dependents()
    {
        var graph = Graph();
        graph.Provide("marts.fct_orders", [new ColumnDefinition("order_id", "BIGINT", false), new ColumnDefinition("net", "DECIMAL(14, 2)", true)], ["order_id"]);
        var (i, diags) = ModelInference.Infer("models/marts/report.sql", "SELECT f.order_id, f.net FROM marts.fct_orders f", graph);
        Assert.Empty(diags);
        Assert.Equal("DECIMAL(14, 2)", i!.Columns[1].Type.LogicalType);
        Assert.Equal(false, i.Columns[0].Nullability.Nullable);
    }

    [Fact]
    public void Problems_are_diagnostics_not_exceptions()
    {
        (Inference?, IReadOnlyList<Diagnostic>) Run(string sql) => ModelInference.Infer("models/marts/m.sql", sql, Graph());

        var (v1, d1) = Run("SELEC FROM FROM (");
        Assert.Null(v1);
        Assert.Equal("DDB-306", Assert.Single(d1).Code);

        var (v2, d2) = Run("SELECT * FROM staging.nope");
        Assert.Null(v2);
        var missing = Assert.Single(d2);
        Assert.Equal("DDB-218", missing.Code);
        Assert.Contains("models/staging/nope.yml", missing.Fix);

        var (_, d3) = Run("SELECT o.nope FROM staging.orders o");
        Assert.Equal("DDB-219", Assert.Single(d3).Code);

        var (_, d4) = Run("SELECT o.order_id, o.order_id FROM staging.orders o");
        Assert.Equal("DDB-220", Assert.Single(d4).Code);

        var (_, d5) = Run("SELECT o.amount * 2 FROM staging.orders o");
        Assert.Equal("DDB-220", Assert.Single(d5).Code);
        Assert.Contains("without an alias", d5[0].Found);

        var (ok, d6) = Run("SELECT o.amount * 2 AS _col_0 FROM staging.orders o");           // a real alias that looks like the placeholder
        Assert.Empty(d6);
        Assert.Equal("_col_0", ok!.Columns[0].Name);
    }

    [Fact]
    public void Inference_is_deterministic()
    {
        const string sql = "SELECT o.customer_id, COUNT(*) AS n FROM staging.orders o GROUP BY o.customer_id";
        Assert.Equal(Infer(sql).Columns.Select(c => (c.Name, c.Type.LogicalType, c.Nullability.Nullable)), Infer(sql).Columns.Select(c => (c.Name, c.Type.LogicalType, c.Nullability.Nullable)));
    }
}
