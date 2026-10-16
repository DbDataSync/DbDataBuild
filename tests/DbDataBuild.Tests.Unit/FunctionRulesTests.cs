using System.Globalization;
using DbDataBuild.Sql;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rules;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The function rules of entry 177 onward (docs/research/function-matrix.md), each tested the way CLAUDE.md asks: the text a target gets (here), the rewritten query run on DuckDB against the original (the
/// differential, here: a rule writes DuckDB-dialect text, so DuckDB can run both), and the engines' rows against DuckDB's (`FunctionRuleProbes` in the conformance suite).
/// </summary>
public class FunctionRulesTests
{
    /// <summary>Edge rows: negatives, zero, NULL, a half, large values.</summary>
    private static DuckDBConnection Edge()
    {
        var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE t (id INTEGER, x DOUBLE, i INTEGER, b BIGINT)");
        Run(c, "INSERT INTO t VALUES (1, 0.5, 0, 0), (2, 2.5, 7, 1), (3, 100.0, -7, -1), (4, 1e-3, 2147483647, 9223372036854775807), (5, NULL, NULL, NULL), (6, 12345.678, -2147483648, -9223372036854775808)");
        return c;
    }

    private static void Run(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    internal static string Rows(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i) switch
            {
                double d => Math.Round(d, 9).ToString("0.#########", CultureInfo.InvariantCulture),
                var v => Convert.ToString(v, CultureInfo.InvariantCulture)!,
            })));
        rows.Sort(StringComparer.Ordinal);
        return string.Join("; ", rows);
    }

    /// <summary>The rule fires on <paramref name="target"/> and what it writes gives DuckDB's rows.</summary>
    private static void Differential(string rule, string target, string sql)
    {
        using var c = Edge();
        var rewritten = TargetRules.Apply(sql, target);
        Assert.Contains(rule, rewritten.Rules);
        Assert.Equal(Rows(c, sql), Rows(c, rewritten.Sql));
    }

    [Theory]
    [InlineData("SELECT id, log(x) AS v FROM t WHERE x > 0", "LOG10(x)")]
    [InlineData("SELECT id, log(x + 1) AS v FROM t WHERE x > 0", "LOG10(x + 1)")]
    public void Log_with_one_argument_is_the_base_ten_logarithm_on_SQL_Server_and_Fabric(string sql, string expected)
    {
        foreach (var target in new[] { "sqlserver", "fabric" })
        {
            Assert.Equal([TargetRules.LogIsBaseTen], TargetRules.Apply(sql, target).Rules);
            Assert.Contains(expected, Polyglot.TranspileOne(TargetRules.Apply(sql, target).Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!);
        }
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);                       // PostgreSQL's log(x) is base 10 already
    }

    [Fact]
    public void Log_with_a_base_is_left_alone_because_the_transpile_already_swaps_the_arguments()
    {
        const string sql = "SELECT log(2, x) AS v FROM t";
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);
        Assert.Contains("LOG(x, 2)", Polyglot.TranspileOne(sql, Dialects.Canonical, Dialects.ForTarget("sqlserver")).Sql!);
    }

    [Fact]
    public void Log_rewritten_for_SQL_Server_gives_DuckDBs_values()
    {
        Differential(TargetRules.LogIsBaseTen, "sqlserver", "SELECT id, log(x) AS v FROM t WHERE x > 0");
        Differential(TargetRules.LogIsBaseTen, "sqlserver", "SELECT id, log(CAST(x AS DOUBLE) + 1) AS v FROM t");
    }

    [Fact]
    public void Log_with_a_base_is_the_ratio_of_natural_logarithms_on_PostgreSQL_as_DuckDB_computes_it()
    {
        const string sql = "SELECT id, log(2, x) AS v FROM t WHERE x > 0";
        var r = TargetRules.Apply(sql, "postgres");
        Assert.Equal([TargetRules.LogWithBase], r.Rules);
        Assert.Contains("LN(CAST(x AS DOUBLE PRECISION)) AS DOUBLE PRECISION) / NULLIF(LN(CAST(2 AS DOUBLE PRECISION)), 0)", Polyglot.TranspileOne(r.Sql, Dialects.Canonical, Dialects.ForTarget("postgres")).Sql!);
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);
        Differential(TargetRules.LogWithBase, "postgres", sql);
        using var c = Edge();                                                      // the same to the last digit but at most one bit (the engines compute the quotient themselves)
        Assert.Equal("5", Rows(c, "SELECT count(*) FROM t WHERE x > 0 AND abs(log(2, x) - ln(CAST(x AS DOUBLE)) / ln(CAST(2 AS DOUBLE))) <= abs(log(2, x)) * 4e-16"));
    }
}
