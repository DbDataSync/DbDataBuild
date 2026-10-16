using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Lowering;
using DbDataBuild.Sql;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rules;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// VALUES lists (DuckDB's constant table, the plan node EXPRESSION_GET; OPEN-ITEMS W-3, entry 178), tested the way CLAUDE.md asks: the text the lowerer and the target step write (here), the lowered query run on
/// DuckDB against the original (the differential, here), and the engines' rows against DuckDB's (`ValuesListConformance`).
/// </summary>
public class ValuesListTests
{
    private static DuckDBConnection Open()
    {
        var c = PlanningConnection.Open(lockDown: false);
        Exec(c, "CREATE TABLE t (id INTEGER, a INTEGER, s VARCHAR)");
        Exec(c, "INSERT INTO t VALUES (1, 1, 'x'), (2, 2, 'y'), (3, 5, NULL), (4, NULL, 'z')");
        return c;
    }

    private static void Exec(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private static (string Sql, List<string> Names) LowerWithNames(DuckDBConnection c, string sql)
    {
        string plan;
        using (var cmd = c.CreateCommand()) { cmd.CommandText = QueryDescriber.PlanStatement(sql); plan = (string)cmd.ExecuteScalar()!; }
        var names = new List<string>();
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "DESCRIBE " + sql; using var r = cmd.ExecuteReader(); while (r.Read()) names.Add(r.GetString(0)); }
        return (PlanLowerer.Lower(plan, names).Sql, names);
    }

    private static string Lower(DuckDBConnection c, string sql) => LowerWithNames(c, sql).Sql;

    private static (List<string> Names, List<string> Types, List<string> Rows) Run(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var types = Enumerable.Range(0, r.FieldCount).Select(i => r.GetDataTypeName(i)).ToList();
        var rows = new List<string>();
        while (r.Read()) rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
        rows.Sort(StringComparer.Ordinal);
        return (names, types, rows);
    }

    private static string Render(string lowered, string target) =>
        TargetRules.Finish(Polyglot.TranspileOne(TargetRules.Apply(lowered, target).Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!, target);

    /// <summary>A list of <paramref name="count"/> rows of an integer, a text and a decimal that is NULL on every seventh row.</summary>
    private static string ManyRows(int count) =>
        "SELECT * FROM (VALUES " + string.Join(", ", Enumerable.Range(1, count).Select(i => $"({i}, 's{i}', {(i % 7 == 0 ? "NULL" : $"{i}.25")})")) + ") AS v(id, s, d)";

    [Theory]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, NULL)) AS v(id, s)", "SELECT col0 AS id, col1 AS s\nFROM (VALUES\n  (1, 'a'),\n  (2, CAST(NULL AS VARCHAR))\n) AS vals(col0, col1)")]
    [InlineData("VALUES (1), (2)", "SELECT col0\nFROM (VALUES\n  (1),\n  (2)\n) AS vals(col0)")]
    [InlineData("SELECT * FROM (VALUES (1)) AS v(id)", "SELECT col0 AS id\nFROM (VALUES\n  (1)\n) AS vals(col0)")]
    [InlineData("SELECT * FROM (VALUES (1, 2.5), (2, 3), (NULL, 1 + 1)) AS v(a, b)",
        "SELECT col0 AS a, col1 AS b\nFROM (VALUES\n  (1, CAST(2.5 AS DECIMAL(11, 1))),\n  (2, CAST(3 AS DECIMAL(11, 1))),\n  (CAST(NULL AS INTEGER), CAST((1 + 1) AS DECIMAL(11, 1)))\n) AS vals(col0, col1)")]
    [InlineData("SELECT * FROM (VALUES (DATE '2024-01-01', TIMESTAMP '2024-01-01 10:00:00.5', true), (NULL, NULL, NULL)) AS v(d, ts, b)",
        "SELECT col0 AS d, col1 AS ts, col2 AS b\nFROM (VALUES\n  (DATE '2024-01-01', TIMESTAMP '2024-01-01 10:00:00.5', TRUE),\n  (CAST(NULL AS DATE), CAST(NULL AS TIMESTAMP), CAST(NULL AS BOOLEAN))\n) AS vals(col0, col1, col2)")]
    [InlineData("SELECT * FROM (VALUES (1, 2147483648)) AS v(a, b)", "SELECT col0 AS a, col1 AS b\nFROM (VALUES\n  (1, 2147483648)\n) AS vals(col0, col1)")]
    [InlineData("SELECT * FROM (VALUES (1), (2147483648)) AS v(a)", "SELECT col0 AS a\nFROM (VALUES\n  (CAST(1 AS BIGINT)),\n  (2147483648)\n) AS vals(col0)")]
    [InlineData("SELECT * FROM (VALUES (1 + 2, upper('a') || 'b')) AS v(a, b)", "SELECT col0 AS a, col1 AS b\nFROM (VALUES\n  ((1 + 2), (upper('a') || 'b'))\n) AS vals(col0, col1)")]
    public void A_VALUES_list_lowers_to_a_table_value_constructor_with_DuckDBs_column_types_and_names(string source, string expected)
    {
        using var c = Open();
        Assert.Equal(expected, Lower(c, source));
    }

    public static TheoryData<string> Queries => new()
    {
        "SELECT * FROM (VALUES (1, 'a'), (2, NULL), (NULL, 'c')) AS v(id, s)",
        "VALUES (1, 'a'), (2, 'b')",
        "SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v",
        "SELECT * FROM (VALUES (1)) AS v(id)",
        "SELECT * FROM (VALUES (1, 2.5), (2, 3), (NULL, 1 + 1)) AS v(a, b)",
        "SELECT * FROM (VALUES (1.5), (2.25), (NULL)) AS v(a)",
        "SELECT * FROM (VALUES (1), (2147483648)) AS v(a)",
        "SELECT * FROM (VALUES (1, 2.5e0), (2, 3)) AS v(a, b)",
        "SELECT * FROM (VALUES ('héllo 😀', ''), ('a''b', ' padded ')) AS v(a, b)",
        "SELECT * FROM (VALUES (DATE '2024-02-29', TIMESTAMP '2024-02-29 23:59:59.123456', true), (NULL, NULL, NULL)) AS v(d, ts, b)",
        "SELECT * FROM (VALUES (1 + 2, abs(-3), upper('a') || 'b', CASE WHEN true THEN 1 ELSE 2 END)) AS v(a, b, c, d)",
        "SELECT * FROM (VALUES (CAST(7 AS SMALLINT), CAST(1.5 AS FLOAT)), (CAST(-3 AS SMALLINT), CAST(2 AS FLOAT))) AS v(a, b)",
        "SELECT v.id, w.n FROM (VALUES (1, 'x'), (2, 'y')) AS v(id, s) JOIN (VALUES (1, 10), (2, 20)) AS w(id, n) ON v.id = w.id",
        "SELECT t.id, v.label FROM t JOIN (VALUES (1, 'one'), (2, 'two')) AS v(a, label) ON t.a = v.a ORDER BY t.id",
        "SELECT t.id, v.label FROM t LEFT JOIN (VALUES (1, 'one'), (2, 'two')) AS v(a, label) ON t.a = v.a ORDER BY t.id",
        "WITH c AS (SELECT * FROM (VALUES (1, 'x'), (2, 'y')) AS v(id, s)) SELECT * FROM c WHERE id > 1",
        "SELECT * FROM t WHERE a IN (SELECT x FROM (VALUES (1), (2)) AS v(x))",
        "SELECT * FROM t WHERE a NOT IN (SELECT x FROM (VALUES (1), (2)) AS v(x))",
        "SELECT * FROM t WHERE EXISTS (SELECT 1 FROM (VALUES (1), (2)) AS v(x) WHERE v.x = t.a)",
        "SELECT (SELECT max(x) FROM (VALUES (1), (2)) AS v(x)) AS m",
        "SELECT x, count(*) AS n FROM (VALUES (1), (1), (2)) AS v(x) GROUP BY x",
        "SELECT x FROM (VALUES (3), (1), (2)) AS v(x) ORDER BY x DESC LIMIT 2",
        "SELECT x FROM (VALUES (1), (2)) AS v(x) UNION ALL SELECT a FROM t",
        "SELECT DISTINCT x FROM (VALUES (1), (1), (NULL), (NULL)) AS v(x)",
        "SELECT x FROM (VALUES (1), (2)) AS v(x) WHERE x > 1",
        "SELECT * FROM (SELECT * FROM (VALUES (1), (2)) AS v(x)) AS inner_q",
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void The_lowered_VALUES_query_gives_DuckDBs_rows_names_and_types(string source)
    {
        using var c = Open();
        var (lowered, _) = LowerWithNames(c, source);
        var expected = Run(c, source);
        var actual = Run(c, lowered);
        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.Rows, actual.Rows);
    }

    [Theory]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, NULL)) AS v(id, s)", "FROM (VALUES (1, 'a'), (2, CAST(NULL AS VARCHAR))) AS vals(col0, col1)")]
    [InlineData("SELECT * FROM (VALUES (1, 2.5), (2, 3)) AS v(a, b)", "(VALUES (1, CAST(2.5 AS DECIMAL(11, 1))), (2, CAST(3 AS DECIMAL(11, 1))))")]
    [InlineData("SELECT * FROM (VALUES (DATE '2024-01-01', true), (NULL, NULL)) AS v(d, b)", "(VALUES (CAST('2024-01-01' AS DATE), TRUE), (CAST(NULL AS DATE), CAST(NULL AS BOOLEAN)))")]
    public void Each_target_gets_the_constructor_with_the_cells_typed(string source, string expected)
    {
        using var c = Open();
        var lowered = Lower(c, source);
        var postgres = Render(lowered, "postgres");
        Assert.Contains(expected, postgres);
        var sqlServer = Render(lowered, "sqlserver");
        Assert.Contains("VALUES (", sqlServer);
        Assert.Contains("VALUES (", Render(lowered, "fabric"));
    }

    [Fact]
    public void SQL_Server_gets_the_bit_and_datetime_types_for_booleans_and_timestamps_and_no_UNION_for_a_short_list()
    {
        using var c = Open();
        var lowered = Lower(c, "SELECT * FROM (VALUES (TIMESTAMP '2024-01-01 10:00:00.5', true), (NULL, NULL)) AS v(ts, b)");
        var text = Render(lowered, "sqlserver");
        Assert.Contains("CAST('2024-01-01 10:00:00.5' AS DATETIME2)", text);
        Assert.Contains("CAST(NULL AS BIT)", text);
        Assert.DoesNotContain("UNION", text);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(2500, 3)]
    [InlineData(5000, 5)]
    public void SQL_Server_and_Fabric_get_a_list_of_at_most_1000_rows_per_constructor_and_PostgreSQL_one_list(int rows, int constructors)
    {
        using var c = Open();
        var lowered = Lower(c, ManyRows(rows));
        foreach (var target in new[] { "sqlserver", "fabric" })
        {
            var text = Render(lowered, target);
            var perConstructor = Regex.Split(text, @"\(VALUES ").Skip(1).Select(part => Regex.Matches(part.Split(") AS ")[0], @"\(\d+, N?'s\d+',").Count).ToList();
            Assert.Equal(constructors, perConstructor.Count);
            Assert.All(perConstructor, n => Assert.InRange(n, 1, 1000));
            Assert.Equal(rows, perConstructor.Sum());
            Assert.Equal(constructors - 1, Regex.Matches(text, "UNION ALL").Count);
            Assert.Equal(rows > 1000 ? [TargetRules.ValuesInChunks] : [], TargetRules.Apply(lowered, target).Rules);
        }
        var postgres = Render(lowered, "postgres");
        Assert.Single(Regex.Matches(postgres, @"\(VALUES "));
        Assert.DoesNotContain("UNION", postgres);
        Assert.Empty(TargetRules.Apply(lowered, "postgres").Rules);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(3500)]
    public void The_chunked_list_gives_DuckDBs_rows_in_the_same_order_of_values(int rows)
    {
        using var c = Open();
        var source = ManyRows(rows);
        var lowered = Lower(c, source);
        var chunked = TargetRules.Apply(lowered, "sqlserver").Sql;                     // the rule writes DuckDB's dialect, so DuckDB can run it
        Assert.Contains("UNION ALL", chunked);
        var expected = Run(c, source);
        var actual = Run(c, chunked);
        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.Rows, actual.Rows);
        Assert.Equal(rows, actual.Rows.Count);
    }

    [Fact]
    public void A_chunked_list_works_as_a_join_side_a_CTE_and_a_subquery()
    {
        using var c = Open();
        var list = string.Join(", ", Enumerable.Range(1, 2200).Select(i => $"({i}, {i % 5})"));
        foreach (var source in new[]
        {
            $"SELECT count(*) AS n, sum(v.k) AS s FROM t JOIN (VALUES {list}) AS v(id, k) ON t.id = v.id",
            $"WITH c AS (SELECT * FROM (VALUES {list}) AS v(id, k)) SELECT k, count(*) AS n FROM c GROUP BY k",
            $"SELECT count(*) AS n FROM t WHERE id IN (SELECT id FROM (VALUES {list}) AS v(id, k) WHERE k = 1)",
        })
        {
            var lowered = Lower(c, source);
            var chunked = TargetRules.Apply(lowered, "sqlserver");
            Assert.Contains(TargetRules.ValuesInChunks, chunked.Rules);
            Assert.Equal(Run(c, source).Rows, Run(c, chunked.Sql).Rows);
            Assert.Contains("UNION ALL", Render(lowered, "sqlserver"));
        }
    }

    [Theory]
    [InlineData("SELECT * FROM (VALUES (CAST(1 AS TINYINT)), (2)) AS v(a)", "TINYINT")]
    [InlineData("SELECT * FROM (VALUES (CAST(1 AS UTINYINT))) AS v(a)", "UTINYINT")]
    [InlineData("SELECT * FROM (VALUES (CAST(1 AS UBIGINT))) AS v(a)", "UBIGINT")]
    [InlineData("SELECT * FROM (VALUES (CAST(1 AS HUGEINT))) AS v(a)", "HUGEINT")]
    [InlineData("SELECT * FROM (VALUES (INTERVAL 3 DAY)) AS v(a)", "INTERVAL")]
    [InlineData("SELECT * FROM (VALUES ([1, 2])) AS v(a)", "type LIST")]
    [InlineData("SELECT * FROM (VALUES (TIME '10:00:00')) AS v(a)", "TIME")]
    [InlineData("SELECT * FROM (VALUES (TIMESTAMPTZ '2024-01-01 10:00:00+00')) AS v(a)", "TIME ZONE")]
    [InlineData("SELECT * FROM t, LATERAL (VALUES (t.a), (t.a + 1)) AS v(x)", "another table")]
    public void A_list_whose_column_type_the_engines_cannot_hold_exactly_is_refused_naming_it(string source, string named)
    {
        using var c = Open();
        var ex = Assert.Throws<LoweringException>(() => Lower(c, source));
        Assert.Contains(named, ex.Message);
    }
}
