using DbDataBuild.Core;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DbDataBuild.State;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`diff`: pairing the columns, the queries it sends, and what it refuses before it connects (the engines themselves are in the conformance suite).</summary>
public class TableDiffTests
{
    private static ColumnShape C(string name, string type = "bigint", int? length = null, int? precision = null, int? scale = null, string? collation = null) =>
        new(name, type, length, precision, scale, true, collation);

    private static DiffTable T(string schema, string name, params ColumnShape[] columns) => new(schema, name, columns);

    private static DiffPlan Plan(string target = "sqlserver")
    {
        var left = T("marts", "fct", C("id"), C("amount", "decimal", precision: 14, scale: 2), C("name", "nvarchar", 20, collation: "Latin1_General_100_BIN2"), C("only_left"));
        var right = T("dev", "fct", C("id", "int"), C("amount", "decimal", precision: 18, scale: 3), C("name", "nvarchar", 20, collation: "Latin1_General_100_CI_AS"), C("only_right"));
        var (plan, problem) = TableDiffer.Plan(target, left, right, ["id"]);
        Assert.Null(problem);
        return plan!;
    }

    [Fact]
    public void Columns_are_paired_by_name_and_the_ones_on_one_side_only_are_listed()
    {
        var p = Plan();
        Assert.Equal(["id", "amount", "name"], p.Compared.Select(c => c.Name));
        Assert.Equal(["only_left"], p.OnlyLeft);
        Assert.Equal(["only_right"], p.OnlyRight);
        Assert.Equal(["id"], p.Key);
        Assert.Empty(p.Skipped);
    }

    [Fact]
    public void A_column_with_no_equality_or_a_different_kind_of_type_is_not_compared_and_says_why()
    {
        var left = T("a", "t", C("id"), C("doc", "xml"), C("v", "varchar", 10), C("n", "int"));
        var right = T("b", "t", C("id"), C("doc", "xml"), C("v", "int"), C("n", "bigint"));
        var (plan, _) = TableDiffer.Plan("sqlserver", left, right, ["id"]);
        Assert.Equal(["id", "n"], plan!.Compared.Select(c => c.Name));                              // int and bigint are the same kind
        Assert.Contains(plan.Skipped, s => s.Column == "doc" && s.Reason.Contains("no equality"));
        Assert.Contains(plan.Skipped, s => s.Column == "v" && s.Reason.Contains("different kinds"));
    }

    [Theory]
    [InlineData("ghost", "is not a column of a.t")]
    [InlineData("doc", "cannot be compared")]
    public void A_key_column_that_is_missing_or_cannot_be_compared_is_refused(string key, string reason)
    {
        var left = T("a", "t", C("id"), C("doc", "xml"));
        var right = T("b", "t", C("id"), C("doc", "xml"));
        var (plan, problem) = TableDiffer.Plan("sqlserver", left, right, [key]);
        Assert.Null(plan);
        Assert.Contains(reason, problem);
    }

    [Fact]
    public void The_columns_and_exclude_columns_options_narrow_the_comparison_but_never_drop_the_key()
    {
        var left = T("a", "t", C("id"), C("x"), C("y"));
        var right = T("b", "t", C("id"), C("x"), C("y"));
        Assert.Equal(["id", "x"], TableDiffer.Plan("postgres", left, right, ["id"], only: ["x"]).Plan!.Compared.Select(c => c.Name));
        Assert.Equal(["id", "x"], TableDiffer.Plan("postgres", left, right, ["id"], except: ["y", "id"]).Plan!.Compared.Select(c => c.Name));
    }

    [Fact]
    public void The_summary_is_one_full_outer_join_that_counts_and_compares_with_NULL_equal_to_NULL()
    {
        var sql = TableDiffer.SummarySql(Plan("sqlserver"));
        Assert.StartsWith("SELECT SUM(CASE WHEN a.ddb_present IS NOT NULL AND b.ddb_present IS NULL THEN 1 ELSE 0 END) AS only_left", sql);
        Assert.Contains("FROM (SELECT [id] AS [id], [amount] AS [amount], [name] AS [name], 1 AS ddb_present FROM [marts].[fct]) a FULL OUTER JOIN (SELECT [id] AS [id], [amount] AS [amount], [name] AS [name], 1 AS ddb_present FROM [dev].[fct]) b ON a.[id] = b.[id]", sql);
        Assert.Contains("(a.[amount] <> b.[amount] OR (a.[amount] IS NULL AND b.[amount] IS NOT NULL) OR (a.[amount] IS NOT NULL AND b.[amount] IS NULL))", sql);
        Assert.Contains("AS d0", sql);
        Assert.Contains("AS d1", sql);
        Assert.DoesNotContain("AS d2", sql);                                                          // the key is matched on, not compared
        var pg = TableDiffer.SummarySql(Plan("postgres"));
        Assert.Contains("FROM (SELECT \"id\" AS \"id\"", pg);
        Assert.Contains("a.\"id\" = b.\"id\"", pg);
    }

    [Fact]
    public void Text_columns_of_different_collations_are_compared_in_the_left_ones()
    {
        Assert.Contains("b.[name] COLLATE Latin1_General_100_BIN2", TableDiffer.SummarySql(Plan("sqlserver")));
    }

    [Fact]
    public void Samples_are_limited_and_ordered_by_the_key_in_each_dialect()
    {
        var sql = TableDiffer.OnlySql(Plan("sqlserver"), true, 5);
        Assert.StartsWith("SELECT TOP (5) a.[id], a.[amount], a.[name] FROM ", sql);
        Assert.EndsWith("WHERE b.ddb_present IS NULL ORDER BY a.[id]", sql);
        Assert.Contains("LEFT JOIN", sql);
        var pg = TableDiffer.OnlySql(Plan("postgres"), false, 7);
        Assert.StartsWith("SELECT b.\"id\", b.\"amount\", b.\"name\" FROM ", pg);
        Assert.EndsWith("WHERE a.ddb_present IS NULL ORDER BY b.\"id\" LIMIT 7", pg);
        Assert.Contains("RIGHT JOIN", pg);
        var diff = TableDiffer.DifferingSql(Plan("sqlserver"), 3);
        Assert.StartsWith("SELECT TOP (3) a.[id], a.[amount], b.[amount], a.[name], b.[name] FROM ", diff);
    }

    [Fact]
    public void Values_are_only_asked_for_when_requested()
    {
        var p = Plan("postgres");
        Assert.DoesNotContain("MIN(", TableDiffer.CountSql(p, p.Left, true, false));
        Assert.Contains("MIN(\"amount\")", TableDiffer.CountSql(p, p.Left, true, true));
        Assert.Contains("COUNT(\"amount\") AS n1", TableDiffer.CountSql(p, p.Left, true, false));       // the non-NULL counts are counts, always
    }

    [Fact]
    public void Every_query_passes_the_read_guard()
    {
        foreach (var target in new[] { "sqlserver", "postgres" })
        {
            var p = Plan(target);
            foreach (var sql in new[] { TableDiffer.CountSql(p, p.Left, true, true), TableDiffer.DuplicateKeySql(p, p.Left, true), TableDiffer.SummarySql(p), TableDiffer.OnlySql(p, true, 5), TableDiffer.OnlySql(p, false, 5), TableDiffer.DifferingSql(p, 5) })
                Assert.Null(ReadGuard.Check(sql));
        }
    }

    // ---- what it refuses before it connects ----

    private static (int Exit, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/staging/loose.yml"), "name: staging.loose\nkind: mapped\ncolumns:\n  - {name: a, type: BIGINT}\n");
        var exit = CliApp.Run(["connection", "compare", .. args, "--project", dir], o, e, environment: _ => null);
        return (exit, e.ToString());
    }

    [Fact]
    public void Diff_needs_exactly_one_other_table_and_schema_qualified_names()
    {
        Assert.Contains("--against-schema", Run("staging.orders").Err);
        Assert.Contains("--against-schema", Run("staging.orders", "--against", "dev.orders", "--against-schema", "dev").Err);
        Assert.Contains("is not `schema_name.table_name`", Run("orders", "--against-schema", "dev").Err);
        Assert.Contains("is not `schema_name.table_name`", Run("staging.orders", "--against", "orders").Err);
        Assert.Contains("same table", Run("staging.orders", "--against", "staging.orders").Err);
        Assert.Contains("--limit must not be negative", Run("staging.orders", "--against-schema", "dev", "--limit", "-1").Err);
    }

    [Fact]
    public void Diff_needs_a_key_when_the_table_has_none_in_the_project_and_the_read_login_after_that()
    {
        Assert.Contains("Give the columns with --key", Run("staging.loose", "--against-schema", "dev").Err);
        Assert.Contains("Give the columns with --key", Run("other.table", "--against-schema", "dev").Err);
        var (exit, err) = Run("staging.orders", "--against-schema", "dev");                       // the grain is the key, so it gets as far as the login
        Assert.Equal(1, exit);
        Assert.Contains("DDB-501", err);
        Assert.Equal(1, Run("staging.loose", "--against-schema", "dev", "--key", "a").Exit);
    }
}
