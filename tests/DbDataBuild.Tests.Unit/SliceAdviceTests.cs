using DbDataBuild.Cli;
using DbDataBuild.Sql.Analysis;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Slice lint (DDB-225): can the filter of a load's slice be applied below the query's aggregates and windows? Advice only; the query is never rewritten.</summary>
public class SliceAdviceTests
{
    private static string[] Kinds(string sql, string column) => SliceAnalyzer.Analyze(sql, column).Select(b => b.Kind).ToArray();

    [Theory]
    [InlineData("SELECT d, SUM(x) AS total FROM t GROUP BY d", "d")]                                              // a grouping key
    [InlineData("SELECT a, ts FROM t", "ts")]                                                                      // a plain select
    [InlineData("SELECT o.id, o.ts FROM orders o JOIN cust c ON c.id = o.cid", "ts")]                              // joins do not stop the optimizer
    [InlineData("SELECT CAST(ts AS DATE) AS d, COUNT(*) AS n FROM t GROUP BY CAST(ts AS DATE)", "d")]             // an expression of a grouping key
    [InlineData("SELECT * FROM t", "ts")]                                                                          // nothing can be proved about a star
    [InlineData("SELECT d, s FROM (SELECT d AS d, MAX(m) AS s FROM t GROUP BY d) AS q", "d")]                      // through a derived table to a grouping key
    [InlineData("WITH c AS (SELECT d, SUM(x) AS s FROM t GROUP BY d) SELECT d, s FROM c", "d")]                    // through a CTE
    [InlineData("SELECT a, ts, row_number() OVER (PARTITION BY a, ts ORDER BY id) AS rn FROM t", "ts")]            // partitioned by the column
    [InlineData("SELECT a FROM t UNION ALL SELECT a FROM u", "a")]
    [InlineData("SELECT ts FROM t", "not_a_column")]                                                               // unknown: no finding
    public void A_slice_that_the_engine_can_apply_early_has_no_finding(string sql, string column) => Assert.Empty(SliceAnalyzer.Analyze(sql, column));

    [Theory]
    [InlineData("SELECT d, SUM(x) AS total FROM t GROUP BY d", "total", "aggregate_output", "SUM")]
    [InlineData("SELECT customer, MAX(modified_at) AS modified_at FROM t GROUP BY customer", "modified_at", "aggregate_output", "MAX")]
    [InlineData("SELECT d, s FROM (SELECT d AS d, MAX(m) AS s FROM t GROUP BY d) AS q", "s", "aggregate_output", "MAX")]
    [InlineData("WITH c AS (SELECT d, SUM(x) AS s FROM t GROUP BY d) SELECT d, s FROM c", "s", "aggregate_output", "SUM")]
    [InlineData("SELECT d FROM t UNION ALL SELECT MAX(x) AS d FROM u", "d", "aggregate_output", "MAX")]
    [InlineData("SELECT a, ts, row_number() OVER (PARTITION BY a ORDER BY ts) AS rn FROM t", "rn", "window_output", "window function")]
    [InlineData("SELECT a, ts, row_number() OVER (PARTITION BY a ORDER BY ts) AS rn FROM t", "ts", "window_partition", "not partitioned by `ts`")]
    [InlineData("SELECT a, ts FROM t ORDER BY ts LIMIT 5", "ts", "limit", "LIMIT")]
    [InlineData("SELECT DISTINCT ON (a) a, ts FROM t ORDER BY a, ts DESC", "ts", "distinct_on", "DISTINCT ON")]
    public void A_slice_the_engine_cannot_apply_early_says_why(string sql, string column, string kind, string detail)
    {
        var blockers = SliceAnalyzer.Analyze(sql, column);
        var b = Assert.Single(blockers, x => x.Kind == kind);
        Assert.Contains(detail, b.Detail);
    }

    [Fact]
    public void Several_reasons_are_all_reported_once_each()
    {
        var kinds = Kinds("SELECT a, MAX(ts) AS ts FROM t GROUP BY a ORDER BY a LIMIT 10", "ts");
        Assert.Equal(["aggregate_output", "limit"], kinds.Order());
    }

    // ---- through the CLI ----

    private const string Cols = "columns:\n  - {name: order_date, type: DATE, nullable: false}\n  - {name: modified_at, type: TIMESTAMP}\n  - {name: total, type: \"DECIMAL(38, 2)\"}\n";

    private static string Project(string sql, string loads = "", string extra = "", string config = "defaults: {connections: [sqlserver]}\nlint:\n  indexes: false\n")
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: order_date, type: DATE, nullable: false}\n  - {name: modified_at, type: TIMESTAMP}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_daily.yml"), "name: marts.fct_daily\nkind: {incremental_by_time_range: {time_column: order_date}}\ngrain: [order_date]\n" + Cols + loads + extra);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_daily.sql"), sql);
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    private const string Daily = "SELECT order_date, MAX(modified_at) AS modified_at, SUM(amount) AS total FROM staging.orders GROUP BY order_date";
    private const string WatermarkOnMax = "loads:\n  daily:\n    default: true\n    strategy: watermark_append\n    watermark: {column: modified_at, resolver: target_max, on_null: require_param}\n";

    [Fact]
    public void A_watermark_on_an_aggregate_is_a_warning_that_names_the_column_the_aggregate_and_the_way_out()
    {
        var (exit, output, err) = Cli("project", "compile", "--project", Project(Daily, WatermarkOnMax));
        Assert.Equal(CliApp.ExitOk, exit);                                                   // advice never fails validation
        Assert.Contains("warning DDB-225  models/marts/fct_daily.yml", err);
        Assert.Contains("marts.fct_daily / daily loads rows at or after its watermark by `modified_at`", err);
        Assert.Contains("computed by an aggregate (MAX)", err);
        Assert.Contains("lint_ignore: [DDB-225]", err);
        Assert.Contains("1 warning(s)", output);
    }

    [Fact]
    public void The_default_time_range_load_slices_by_a_group_key_and_is_quiet()
    {
        Assert.DoesNotContain("DDB-225", Cli("project", "compile", "--project", Project(Daily)).Err);                      // the implicit load watermarks order_date, which is the GROUP BY key
        const string byTotal = "SELECT order_date, MAX(modified_at) AS modified_at, SUM(amount) AS total FROM staging.orders GROUP BY order_date";
        Assert.DoesNotContain("DDB-225", Cli("project", "compile", "--project", Project(byTotal)).Err);
    }

    [Fact]
    public void A_reload_range_over_a_windowed_column_and_a_limited_query_are_found_too()
    {
        const string reload = "loads:\n  reload:\n    strategy: delete_insert_by_range\n    params: {start: DATE, end: DATE}\n    max_span: 400 days\n";
        var windowed = "SELECT order_date, modified_at, row_number() OVER (PARTITION BY modified_at ORDER BY order_id) AS total FROM staging.orders";
        Assert.Contains("not partitioned by `order_date`", Cli("project", "compile", "--project", Project(windowed, reload)).Err);
        var limited = "SELECT order_date, modified_at, amount AS total FROM staging.orders ORDER BY order_date LIMIT 100";
        Assert.Contains("the query has a LIMIT", Cli("project", "compile", "--project", Project(limited, reload)).Err);
    }

    [Fact]
    public void An_operator_can_silence_the_advice_for_a_model_or_the_project_and_an_unknown_code_is_an_error()
    {
        Assert.DoesNotContain("DDB-225", Cli("project", "compile", "--project", Project(Daily, WatermarkOnMax, "lint_ignore: [DDB-225]\n")).Err);
        Assert.DoesNotContain("DDB-225", Cli("project", "compile", "--project", Project(Daily, WatermarkOnMax, config: "defaults: {connections: [sqlserver]}\nlint:\n  indexes: false\n  slices: false\n")).Err);
        Assert.Contains("DDB-225", Cli("project", "compile", "--project", Project(Daily, WatermarkOnMax, "lint_ignore: [DDB-223]\n")).Err);
        Assert.Contains("must be true or false", Cli("project", "compile", "--project", Project(Daily, WatermarkOnMax, config: "defaults: {connections: [sqlserver]}\nlint:\n  slices: sometimes\n")).Err);
    }

    [Fact]
    public void The_query_is_never_rewritten_the_rendered_load_is_the_same_with_or_without_the_advice()
    {
        var with = Project(Daily, WatermarkOnMax);
        var without = Project(Daily, WatermarkOnMax, "lint_ignore: [DDB-225]\n");
        Assert.Equal(0, Cli("project", "compile", "--project", with).Exit);
        Assert.Equal(0, Cli("project", "compile", "--project", without).Exit);
        var file = "rendered/sqlserver/marts.fct_daily/load.daily.sql";
        Assert.Equal(File.ReadAllText(Path.Combine(without, file)), File.ReadAllText(Path.Combine(with, file)));
    }

    // ---- the source column of the slice (DDB-239) ----

    private const string Simple = "SELECT order_id, order_date, modified_at, amount AS total FROM staging.orders";

    [Fact]
    public void A_slice_read_from_a_source_column_that_no_declared_index_leads_is_a_warning_naming_the_source_column()
    {
        var (exit, _, err) = Cli("project", "compile", "--project", Project(Simple));
        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("warning DDB-239  models/marts/fct_daily.yml", err);
        Assert.Contains("which the query reads from `staging.orders.order_date`", err);
        Assert.DoesNotContain("DDB-225", err);
    }

    [Fact]
    public void A_declared_index_that_leads_with_the_column_or_a_grain_that_starts_with_it_silences_it_and_a_source_with_nothing_declared_is_not_judged()
    {
        var withIndex = Project(Simple);
        File.AppendAllText(Path.Combine(withIndex, "models/staging/orders.yml"), "indexes:\n  - {name: ix_orders_date, columns: [order_date, order_id]}\n");
        Assert.DoesNotContain("DDB-239", Cli("project", "compile", "--project", withIndex).Err);

        var byGrain = Project(Simple);
        File.WriteAllText(Path.Combine(byGrain, "models/staging/orders.yml"), File.ReadAllText(Path.Combine(byGrain, "models/staging/orders.yml")).Replace("grain: [order_id]", "grain: [order_date, order_id]"));
        Assert.DoesNotContain("DDB-239", Cli("project", "compile", "--project", byGrain).Err);

        var nothing = Project(Simple);
        File.WriteAllText(Path.Combine(nothing, "models/staging/orders.yml"), File.ReadAllText(Path.Combine(nothing, "models/staging/orders.yml")).Replace("grain: [order_id]\n", ""));
        Assert.DoesNotContain("DDB-239", Cli("project", "compile", "--project", nothing).Err);
    }

    [Fact]
    public void A_computed_slice_column_is_not_traced_and_the_advice_can_be_silenced()
    {
        const string computed = "SELECT order_id, CAST(order_date AS DATE) + 1 AS order_date, modified_at, amount AS total FROM staging.orders";
        Assert.DoesNotContain("DDB-239", Cli("project", "compile", "--project", Project(computed)).Err);
        Assert.DoesNotContain("DDB-239", Cli("project", "compile", "--project", Project(Simple, extra: "lint_ignore: [DDB-239]\n")).Err);
        Assert.DoesNotContain("DDB-239", Cli("project", "compile", "--project", Project(Simple, config: "defaults: {connections: [sqlserver]}\nlint:\n  indexes: false\n  slices: false\n")).Err);
    }
}
