using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Lowering;
using DbDataBuild.Models.Yaml;
using DbDataBuild.Targets.DuckDb;
using DuckDB.NET.Data;
using static DbDataBuild.Tests.Unit.PolyglotBindingTests;

namespace DbDataBuild.Tests.Unit;

/// <summary>The plan lowerer (docs/research/duckdb-plan-lowering), differentially tested against DuckDB itself on the spike corpus and the seeded edge-case data.</summary>
public class PlanLowererTests
{
    private static DuckDBConnection Open()
    {
        var c = PlanningConnection.Open(lockDown: false);          // the connection that plans is the one that answers too: on DuckDB 2.0 it keeps the clock calls in the plan, and its replacements return the real time
        Exec(c, File.ReadAllText(Path.Combine(RepoRoot(), "spike", "seed.duckdb.sql")));
        // DuckDB 2.0 raises for `%` and `//` by zero where 1.x gave NULL, and the lowering writes the 1.x answer (NULLIF); the oracle answers like 1.x so the rows can be compared (owner decision pending)
        using (var probe = c.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM duckdb_settings() WHERE name = 'error_on_division_by_zero'";
            if (Convert.ToInt64(probe.ExecuteScalar()) > 0) Exec(c, "SET error_on_division_by_zero = false");
        }
        return c;
    }

    private static void Exec(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private static string PlanOf(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = QueryDescriber.PlanStatement(sql);
        return (string)cmd.ExecuteScalar()!;
    }

    private static List<string> Rows(DuckDBConnection c, string sql, bool ordered)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = ordered ? sql : $"SELECT * FROM ({sql}) ORDER BY ALL";
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }

    private static List<string> NamesOf(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DESCRIBE " + sql;
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private static List<string> ResultNames(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        return Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
    }

    private static string Lower(DuckDBConnection c, string sql) => PlanLowerer.Lower(PlanOf(c, sql), NamesOf(c, sql)).Sql;

    /// <summary>
    /// x op ANY/ALL (subquery) is planned by DuckDB 2.0 as a CASE over the min or max and the counts of the subquery (uncorrelated) or with a count-bug guard (correlated). That form is refused, never
    /// written (docs/research/duckdb-2.0/README.md); null here means exactly that refusal on a 2.0 plan, and 1.x never returns null.
    /// </summary>
    private static string? LowerExceptTheKnown2AnyAllGap(DuckDBConnection c, string sql)
    {
        try { return Lower(c, sql); }
        catch (LoweringException ex) when (PlanOf(c, sql).Contains("\"qname\"", StringComparison.Ordinal) && (ex.Message.Contains("DuckDB 2.0 plans it", StringComparison.Ordinal) || ex.Message.Contains("EMPTY_RESULT", StringComparison.Ordinal))) { return null; }
    }

    // the declared grains of the seeded tables: t is identified by id, u by (a, b)
    private static IReadOnlyList<string> Grain(string table) => table switch { "t" => ["id"], "u" => ["a", "b"], _ => [] };
    private static LoweredQuery LowerWith(DuckDBConnection c, string sql, params string[] off) => PlanLowerer.Lower(PlanOf(c, sql), NamesOf(c, sql), null, new RewritePolicy(off));

    [Theory]
    [InlineData("SELECT CAST(CAST(a AS DECIMAL(10, 2)) AS INTEGER) AS x FROM t", "decimal-to-int", "round(")]
    [InlineData("SELECT CAST(a / 2 AS INTEGER) AS x FROM t", "double-to-int", "AS DOUBLE) AS INTEGER")]
    [InlineData("SELECT AVG(a) AS x FROM t", "avg-double", "CAST(a AS DOUBLE)")]
    [InlineData("SELECT SUM(a) AS x FROM t", "sum-widen", "CAST(a AS BIGINT)")]
    [InlineData("SELECT d1 + 3 AS x FROM t", "date-plus-days", "CAST(d1 AS DATE)")]
    public void A_rewrite_that_is_off_is_not_written_into_the_lowered_query(string source, string rewrite, string marker)
    {
        using var c = Open();
        var on = LowerWith(c, source);
        var off = LowerWith(c, source, rewrite);
        Assert.Contains(rewrite, on.Rules);
        Assert.Contains(marker, on.Sql);
        Assert.DoesNotContain(rewrite, off.Rules);
        Assert.DoesNotContain(marker, off.Sql);
        Assert.Equal(Rows(c, source, false), Rows(c, off.Sql, false));         // without the rewrite DuckDB still gives the same rows: it is the engines the rewrite is for
    }

    private static string LowerWithGrain(DuckDBConnection c, string sql) => PlanLowerer.Lower(PlanOf(c, sql), NamesOf(c, sql), Grain).Sql;

    private static List<(string Id, string Sql)> Corpus()
    {
        var diags = new List<Diagnostic>();
        var seq = (YamlSequence)StrictYamlReader.Read(File.ReadAllText(Path.Combine(RepoRoot(), "spike", "constructs.yml")), "constructs.yml", diags)!;
        return seq.Items.Cast<YamlMapping>().Select(m => (((YamlScalar)m.Get("id")!).Value, ((YamlScalar)m.Get("sql")!).Value)).ToList();
    }

    // what has no lowering yet: UNNEST, USING SAMPLE, nested constructors
    private static readonly string[] NotLowered = ["list_literal", "struct_literal", "unnest", "select_distinct_on", "sample_clause"];

    [Fact]
    public void Every_corpus_construct_lowers_to_an_equal_query_or_is_refused_with_a_reason()
    {
        using var c = Open();
        var refused = new List<string>();
        var different = new List<string>();
        var equal = 0;
        foreach (var (id, sql) in Corpus().Where(x => x.Id != "current_ts"))
        {
            string lowered;
            try { lowered = Lower(c, sql); }
            catch (LoweringException ex) { Assert.False(string.IsNullOrWhiteSpace(ex.Message)); refused.Add(id); continue; }
            List<string>? a = null, b = null; string? ea = null, eb = null;
            try { a = Rows(c, sql, false); } catch (DuckDBException ex) { ea = ex.Message; }
            try { b = Rows(c, lowered, false); } catch (DuckDBException ex) { eb = ex.Message; }
            if (ea != null || eb != null) { if (ea == null || eb == null) different.Add($"{id}: one side failed ({ea ?? eb})"); else equal++; continue; }
            // output names are kept; a name DuckDB repeats (a join of two tables with a column b) gets a suffix, because a table cannot have two columns of one name
            var expectedNames = new List<string>();
            foreach (var n in ResultNames(c, sql)) { var name = n; var k = 1; while (expectedNames.Contains(name)) name = $"{n}_{++k}"; expectedNames.Add(name); }
            if (!expectedNames.SequenceEqual(ResultNames(c, lowered))) { different.Add($"{id}: column names {string.Join(",", expectedNames)} vs {string.Join(",", ResultNames(c, lowered))}"); continue; }
            if (a!.SequenceEqual(b!)) equal++; else different.Add($"{id}: {string.Join(",", a!.Take(3))} vs {string.Join(",", b!.Take(3))}\n  {lowered}");
        }
        Assert.Empty(different);
        Assert.Equal(NotLowered.Order(), refused.Order());
        Assert.True(equal >= 83, $"only {equal} constructs lowered equal");
    }

    [Theory]
    [InlineData("SELECT a / b AS q FROM t", "SELECT (CAST(a AS DOUBLE) / CAST(b AS DOUBLE)) AS q\nFROM t")]
    [InlineData("SELECT AVG(a) AS m FROM t", "SELECT avg(CAST(a AS DOUBLE)) AS m\nFROM t")]
    [InlineData("SELECT DATE_TRUNC('month', d) AS m FROM t", "SELECT CAST(date_trunc('month', d) AS TIMESTAMP) AS m\nFROM t")]
    [InlineData("SELECT d + INTERVAL 3 DAY AS x FROM t", "SELECT CAST((d + INTERVAL 3 DAY) AS TIMESTAMP) AS x\nFROM t")]
    [InlineData("SELECT a, COUNT(*) AS n FROM t GROUP BY ALL", "SELECT a, count(*) AS n\nFROM t\nGROUP BY a")]
    [InlineData("SELECT a FROM t WHERE s LIKE 'a%' AND a IN (1, 2)", "SELECT a\nFROM t\nWHERE (s LIKE 'a%') AND (a IN (1, 2))")]
    [InlineData("SELECT id, row_number() OVER (PARTITION BY a ORDER BY id) AS rn FROM t", "SELECT id, row_number() OVER (PARTITION BY a ORDER BY id NULLS LAST) AS rn\nFROM t")]
    [InlineData("SELECT a FROM t UNION ALL SELECT a FROM u", "SELECT a\nFROM t\nUNION ALL\nSELECT a\nFROM u")]
    [InlineData("SELECT * FROM u", "SELECT a, b\nFROM u")]
    public void Typical_queries_lower_to_the_expected_text(string source, string expected)
    {
        using var c = Open();
        Assert.Equal(expected, Lower(c, source));
    }

    [Theory]
    [InlineData("SELECT x FROM generate_series(1, 10) AS g(x)")]
    [InlineData("SELECT x FROM generate_series(0, 9, 3) AS g(x)")]
    [InlineData("SELECT x FROM generate_series(10, 1, -4) AS g(x)")]
    [InlineData("SELECT x FROM generate_series(5, 1) AS g(x)")]
    [InlineData("SELECT x FROM range(5) AS g(x)")]
    [InlineData("SELECT x FROM range(2, 12, 5) AS g(x)")]
    [InlineData("SELECT x FROM range(10, 0, -5) AS g(x)")]
    [InlineData("SELECT x FROM range(0) AS g(x)")]
    [InlineData("SELECT x * 2 AS y FROM generate_series(1, 4) AS g(x) WHERE x > 1")]
    [InlineData("SELECT g.x, t.id FROM generate_series(1, 3) AS g(x) JOIN t ON t.a = g.x")]
    [InlineData("SELECT * FROM generate_series(1, 3)")]
    public void Integer_series_lower_to_generate_series_and_return_the_same_rows(string source)
    {
        using var c = Open();
        var lowered = Lower(c, source);
        Assert.Contains("generate_series(", lowered);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
        Assert.Equal(ResultNames(c, source), ResultNames(c, lowered));
    }

    [Fact]
    public void Range_ends_are_made_inclusive_and_the_column_keeps_DuckDBs_bigint_type()
    {
        using var c = Open();
        Assert.Equal("SELECT CAST(value AS BIGINT) AS x\nFROM generate_series(0, 4) AS series(value)", Lower(c, "SELECT x FROM range(5) AS g(x)"));
        Assert.Contains("generate_series(2, 11, 5)", Lower(c, "SELECT x FROM range(2, 12, 5) AS g(x)"));
    }

    [Theory]
    [InlineData("SELECT d FROM generate_series(DATE '2024-01-01', DATE '2024-01-05', INTERVAL 1 DAY) AS g(d)")]
    [InlineData("SELECT x FROM generate_series(1, 3) AS g(x) WHERE x > (SELECT min(a) FROM t)", false)]
    public void Series_that_have_no_equal_on_the_engines_are_refused(string source, bool refused = true)
    {
        using var c = Open();
        if (refused) Assert.Throws<LoweringException>(() => Lower(c, source));
        else Assert.Contains("generate_series", Lower(c, source));
    }

    [Fact]
    public void The_authors_output_names_survive_even_when_the_plan_has_no_projection_to_carry_them()
    {
        using var c = Open();
        const string source = "SELECT CASE WHEN a > 2 THEN 'big' ELSE 'small' END AS size, COUNT(*) AS n, AVG(a) AS mean FROM t GROUP BY ALL";
        var lowered = Lower(c, source);
        Assert.Equal(["size", "n", "mean"], ResultNames(c, lowered));
        Assert.Equal(ResultNames(c, source), ResultNames(c, lowered));
        // an unaliased expression keeps the name DuckDB gives it, which is explicit in the lowered text
        Assert.Contains("AS \"count_star()\"", Lower(c, "SELECT a, COUNT(*) FROM t GROUP BY ALL"));
    }

    [Fact]
    public void A_join_qualifies_columns_only_where_a_block_has_several_sources_and_keeps_the_author_of_each_name()
    {
        using var c = Open();
        var sql = Lower(c, "SELECT t.id, u.b AS ub, sum(t.x) AS sx FROM t JOIN u ON t.a = u.a WHERE t.b > 1 GROUP BY t.id, u.b HAVING sum(t.x) > 1 ORDER BY sx DESC LIMIT 3");
        Assert.Equal("SELECT t.id, u.b AS ub, sum(CAST(t.x AS BIGINT)) AS sx\nFROM t\nJOIN u\n  ON (t.a = u.a)\nWHERE (t.b > 1)\nGROUP BY t.id, u.b\nHAVING (sum(CAST(t.x AS BIGINT)) > 1)\nORDER BY sum(CAST(t.x AS BIGINT)) DESC NULLS LAST\nLIMIT 3", sql);
        Assert.Equal(Rows(c, "SELECT t.id, u.b AS ub, sum(t.x) AS sx FROM t JOIN u ON t.a = u.a WHERE t.b > 1 GROUP BY t.id, u.b HAVING sum(t.x) > 1 ORDER BY sx DESC, t.id LIMIT 3", true).Count, Rows(c, sql, true).Count);
    }

    [Fact]
    public void Null_ordering_is_always_written_so_engines_cannot_disagree_about_row_order()
    {
        using var c = Open();
        var asc = Lower(c, "SELECT a FROM t ORDER BY a");
        var desc = Lower(c, "SELECT a FROM t ORDER BY a DESC");
        var first = Lower(c, "SELECT a FROM t ORDER BY a NULLS FIRST");
        Assert.EndsWith("ORDER BY a NULLS LAST", asc);
        Assert.EndsWith("ORDER BY a DESC NULLS LAST", desc);
        Assert.EndsWith("ORDER BY a NULLS FIRST", first);
        // and the order a query returns rows in is the same after lowering, row by row
        foreach (var (src, low) in new[] { ("SELECT id, a FROM t ORDER BY a, id", Lower(c, "SELECT id, a FROM t ORDER BY a, id")), ("SELECT id, a FROM t ORDER BY a DESC, id LIMIT 4", Lower(c, "SELECT id, a FROM t ORDER BY a DESC, id LIMIT 4")) })
            Assert.Equal(Rows(c, src, true), Rows(c, low, true));
    }

    [Theory]
    [InlineData("SELECT id, sum(x) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, sum(x) OVER (ORDER BY a RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, sum(x) OVER (ORDER BY a) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, sum(x) OVER (ORDER BY a ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, sum(x) OVER (PARTITION BY a) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, lag(x, 2, 0) OVER (ORDER BY id) AS p FROM t ORDER BY id")]
    public void Window_frames_keep_their_meaning_row_by_row_even_when_rows_tie(string source)
    {
        using var c = Open();
        Assert.Equal(Rows(c, source, true), Rows(c, Lower(c, source), true));
    }

    [Fact]
    public void ROWS_and_RANGE_frames_are_not_confused()
    {
        using var c = Open();
        Assert.Contains("ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW", Lower(c, "SELECT sum(x) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM t"));
        Assert.DoesNotContain("ROWS", Lower(c, "SELECT sum(x) OVER (ORDER BY a) AS s FROM t"));      // the default frame is RANGE and is not written
    }

    [Fact]
    public void Literals_the_binder_turned_into_casts_come_back_as_literals()
    {
        using var c = Open();
        Assert.Equal("SELECT TRUE AS x", Lower(c, "SELECT TRUE AS x"));
        Assert.Equal("SELECT DATE '2024-01-15' AS x", Lower(c, "SELECT DATE '2024-01-15' AS x"));
        Assert.Contains("INTERVAL 3 DAY", Lower(c, "SELECT d + INTERVAL 3 DAY FROM t"));
        Assert.Equal("SELECT a\nFROM t\nWHERE (s = 'it''s')", Lower(c, "SELECT a FROM t WHERE s = 'it''s'"));
    }

    [Theory]
    [InlineData("SELECT DISTINCT ON (a) a, b, x FROM t ORDER BY a, b DESC, id")]
    [InlineData("SELECT DISTINCT ON (a) id, b FROM t ORDER BY a, id")]
    [InlineData("SELECT DISTINCT ON (a) id, b FROM t ORDER BY a DESC, id DESC")]
    [InlineData("SELECT DISTINCT ON (a, b) id, s FROM t ORDER BY a, b, id")]
    [InlineData("SELECT DISTINCT ON (t.a) t.id, u.b FROM t JOIN u ON t.a = u.a ORDER BY t.a, t.id, u.a, u.b")]
    [InlineData("SELECT DISTINCT ON (a) id, b FROM t WHERE x > 1 ORDER BY a, b NULLS FIRST, id")]
    public void DISTINCT_ON_with_an_ordering_that_decides_the_row_lowers_to_row_number_and_keeps_the_same_rows(string source)
    {
        using var c = Open();
        var lowered = LowerWithGrain(c, source);
        Assert.Contains("row_number() OVER (PARTITION BY", lowered);
        Assert.Contains("= 1", lowered);
        Assert.DoesNotContain("DISTINCT ON", lowered);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
    }

    [Fact]
    public void Hidden_trailing_columns_DuckDB_adds_for_its_own_use_are_not_part_of_the_lowered_output()
    {
        using var c = Open();
        const string source = "SELECT DISTINCT ON (a) a, id FROM t ORDER BY a, id";
        var lowered = LowerWithGrain(c, source);
        Assert.Equal(ResultNames(c, source), ResultNames(c, lowered));
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
    }

    [Fact]
    public void DISTINCT_ON_reads_as_a_partitioned_row_number_filtered_to_one()
    {
        using var c = Open();
        Assert.Equal("SELECT a, b\nFROM (\n  SELECT a, b, id, row_number() OVER (PARTITION BY a ORDER BY b DESC NULLS LAST, id NULLS LAST) AS rn\n  FROM t\n) AS s1\nWHERE (rn = 1)\nORDER BY a NULLS LAST, b DESC NULLS LAST, id NULLS LAST",
            LowerWithGrain(c, "SELECT DISTINCT ON (a) a, b FROM t ORDER BY a, b DESC, id"));
    }

    [Theory]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t", "ORDER BY")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t ORDER BY a", "only names the ON columns")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t ORDER BY a, b", "`id`")]
    [InlineData("SELECT DISTINCT ON (t.a) t.id, u.b FROM t JOIN u ON t.a = u.a ORDER BY t.a, t.id", "`u`")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM (SELECT a, b, id FROM t WHERE x > 0 LIMIT 5) q ORDER BY a, id", "derived table")]
    public void DISTINCT_ON_that_could_pick_among_ties_is_refused_and_says_what_to_add(string source, string mention)
    {
        using var c = Open();
        var ex = Assert.Throws<LoweringException>(() => LowerWithGrain(c, source));
        Assert.Contains(mention, ex.Message);
        Assert.Contains("DISTINCT ON", ex.Message);
    }

    [Theory]
    [InlineData("SELECT a FROM t UNION SELECT a FROM u UNION SELECT a + 100 FROM t")]
    [InlineData("SELECT a FROM t UNION ALL SELECT a FROM u UNION ALL SELECT a + 100 FROM t UNION ALL SELECT a FROM t")]
    [InlineData("SELECT a FROM t EXCEPT SELECT a FROM u EXCEPT SELECT a + 100 FROM t")]
    public void A_set_operation_of_more_than_two_queries_keeps_every_query(string source)
    {
        // DuckDB flattens A UNION B UNION C into one operator with three children; the lowered text must read all of them
        using var c = Open();
        var sql = Lower(c, source);
        Assert.Contains("+ 100", sql);
        Assert.Equal(Rows(c, source, false), Rows(c, sql, false));
    }

    [Theory]
    [InlineData("SELECT t.a FROM t JOIN u ON t.a = u.a AND u.b IS NOT NULL")]
    [InlineData("SELECT t.a FROM t JOIN u ON t.a = u.a AND u.b > 1")]
    [InlineData("SELECT t.a FROM t JOIN u ON t.a = u.a AND t.b IS NOT NULL")]
    [InlineData("SELECT t.a, u.b FROM t LEFT JOIN u ON t.a = u.a AND u.b IS NOT NULL")]
    [InlineData("SELECT t.a FROM t JOIN (SELECT a, b, count(*) AS n FROM u GROUP BY a, b) g ON t.a = g.a AND g.n > 1")]
    public void A_condition_on_one_side_of_a_join_condition_is_not_lost(string source)
    {
        // the binder moves a one-sided predicate of the ON clause into a filter on that side's input
        using var c = Open();
        var sql = Lower(c, source);
        Assert.Equal(Rows(c, source, false), Rows(c, sql, false));
        Assert.True(sql.Contains("IS NOT NULL", StringComparison.Ordinal) == source.Contains("IS NOT NULL", StringComparison.Ordinal), sql);
    }

    [Theory]
    [InlineData("WITH RECURSIVE c AS (SELECT 1 AS n, 'a' AS s UNION ALL SELECT n + 1, s || 'b' FROM c WHERE n < 4) SELECT n, s FROM c")]
    [InlineData("WITH RECURSIVE c AS (SELECT a, 1 AS depth, CAST(a AS VARCHAR) AS path FROM t WHERE a = 1 UNION ALL SELECT t.a, c.depth + 1, c.path || '>' || CAST(t.a AS VARCHAR) FROM t JOIN c ON t.a = c.a + 1 WHERE c.depth < 3) SELECT a, depth, path FROM c")]
    [InlineData("WITH RECURSIVE c AS (SELECT 1 AS n UNION SELECT n % 3 + 1 FROM c) SELECT n FROM c")]
    public void A_recursive_query_is_lowered_with_the_same_rows(string source)
    {
        using var c = Open();
        var sql = Lower(c, source);
        Assert.StartsWith("WITH RECURSIVE ", sql);
        Assert.Equal(Rows(c, source, false), Rows(c, sql, false));
    }

    [Fact]
    public void The_parts_of_a_recursive_query_get_the_same_text_and_decimal_types_so_SQL_Server_accepts_them()
    {
        using var c = Open();
        var sql = Lower(c, "WITH RECURSIVE c AS (SELECT 1 AS n, 'a' AS s, 1.5 AS d UNION ALL SELECT n + 1, s || 'b', d * 2 FROM c WHERE n < 4) SELECT n, s, d FROM c");
        Assert.Contains("CAST('a' AS VARCHAR)", sql);
        Assert.Contains("AS VARCHAR) AS s", sql);
        Assert.Contains("AS DECIMAL(", sql);
    }

    [Theory]
    [InlineData("SELECT id, count(*) OVER () AS n FROM t")]
    [InlineData("SELECT id, count(*) OVER (PARTITION BY a ORDER BY id) AS n FROM t")]
    [InlineData("SELECT id, ntile(3) OVER (ORDER BY id) AS q, percent_rank() OVER (ORDER BY id) AS p, cume_dist() OVER (ORDER BY id) AS c FROM t")]
    [InlineData("SELECT id, lead(a, 2, -1) OVER (ORDER BY id) AS l, first_value(a) OVER (ORDER BY id) AS f FROM t")]
    public void Window_functions_are_lowered_with_the_same_rows(string source)
    {
        using var c = Open();
        var sql = Lower(c, source);
        Assert.DoesNotContain("count()", sql);
        Assert.Equal(Rows(c, source, false), Rows(c, sql, false));
    }

    [Theory]
    [InlineData("SELECT median(a) AS m FROM t")]
    [InlineData("SELECT quantile_cont(a, 0.9) AS q, quantile_cont(a, 0.1) AS p FROM t")]
    [InlineData("SELECT quantile_disc(a, 0.5) AS q, quantile_disc(a, 0.0) AS z, quantile_disc(a, 1.0) AS o FROM t")]
    [InlineData("SELECT id % 2 AS g, median(a) AS m, quantile_cont(CAST(a AS DECIMAL(10, 2)), 0.75) AS q, count(*) AS n FROM t GROUP BY id % 2")]
    [InlineData("SELECT median(a) FILTER (WHERE id > 1000) AS m FROM t")]
    public void Median_and_quantiles_are_ranked_and_read_off_with_the_same_values_as_DuckDB(string source)
    {
        using var c = Open();
        var lowered = source.Contains("FILTER") ? null : Lower(c, source);
        if (lowered == null) { Assert.Throws<LoweringException>(() => Lower(c, source)); return; }
        Assert.Contains("row_number() OVER", lowered);
        Assert.DoesNotContain("median(", lowered);
        Assert.DoesNotContain("quantile_", lowered);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
    }

    [Theory]
    [InlineData("SELECT g, median(d) AS m, quantile_cont(d, 0.9) AS p, quantile_cont(d, 0.1) AS q, quantile_disc(d, 0.75) AS r FROM dq GROUP BY g")]
    [InlineData("SELECT median(d) AS m, quantile_cont(d, 0.37) AS p, quantile_cont(CAST(d AS DECIMAL(18, 2)), 0.9) AS q FROM dq")]
    public void Quantiles_of_decimals_cut_to_a_whole_number_of_the_scale_like_DuckDB_and_agree_on_many_rows(string source)
    {
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE dq AS SELECT i % 9 AS g, CASE WHEN i % 40 = 0 THEN NULL ELSE CAST((hash(i) % 100000000) / 10000.0 AS DECIMAL(19, 4)) END AS d FROM range(1, 1500) t(i)";
            cmd.ExecuteNonQuery();
        }
        var lowered = Lower(c, source);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
    }

    [Theory]
    [InlineData("SELECT * FROM (UNPIVOT t ON a, b INTO NAME measure VALUE amount)")]
    [InlineData("SELECT id, measure, amount FROM (UNPIVOT t ON a, b INTO NAME measure VALUE amount) WHERE amount > 1")]
    [InlineData("SELECT * FROM (UNPIVOT t ON a, b INTO NAME measure VALUE amount) ORDER BY id, measure")]
    public void Unpivot_becomes_a_lateral_values_list_over_one_scan_with_the_same_rows(string source)
    {
        using var c = Open();
        var lowered = Lower(c, source);
        Assert.Contains("LATERAL (VALUES", lowered);
        Assert.DoesNotContain("UNION ALL", lowered);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
    }

    [Theory]
    [InlineData("SELECT * FROM (PIVOT t ON id IN (1, 2, 3) USING sum(a) GROUP BY b)")]
    [InlineData("SELECT * FROM (PIVOT t ON b IN (1, 2) USING sum(a) AS total, count(*) AS n)")]
    public void Pivot_is_lowered_to_aggregates_with_the_same_rows(string source)
    {
        using var c = Open();
        Assert.Equal(Rows(c, source, false), Rows(c, Lower(c, source), false));
    }

    [Fact]
    public void QUALIFY_and_LATERAL_are_lowered_with_the_same_rows()
    {
        using var c = Open();
        foreach (var source in new[]
        {
            "SELECT id, a FROM t QUALIFY row_number() OVER (PARTITION BY b ORDER BY id DESC) = 1",
            "SELECT t.id, top.a FROM t, LATERAL (SELECT u.a FROM u WHERE u.a = t.a ORDER BY u.a LIMIT 1) top",
        })
            Assert.Equal(Rows(c, source, false), Rows(c, Lower(c, source), false));
    }

    [Fact]
    public void The_json_arrows_are_the_functions_the_transpile_knows()
    {
        using var c = Open();
        var sql = Lower(c, "SELECT CAST('{\"k\": \"v\"}' AS JSON) ->> '$.k' AS x");
        Assert.Contains("json_extract_string(", sql);
        Assert.DoesNotContain("->>", sql);
    }

    [Fact]
    public void A_double_cast_to_a_decimal_rounds_the_scaled_value_half_away_from_zero_like_DuckDB()
    {
        using var c = Open();
        var sql = Lower(c, "SELECT CAST(a / 8.0 AS DECIMAL(18, 2)) AS x FROM t");
        Assert.Contains("round(CAST(", sql);
        Assert.Contains(", 2) AS DECIMAL(18, 2))", sql);
        Assert.Equal(Rows(c, "SELECT CAST(a / 8.0 AS DECIMAL(18, 2)) AS x FROM t", false), Rows(c, sql, false));
    }

    [Fact]
    public void A_cte_is_kept_as_a_cte_in_dependency_order()
    {
        using var c = Open();
        var sql = Lower(c, "WITH c1 AS MATERIALIZED (SELECT a, b FROM t WHERE a > 1), c2 AS MATERIALIZED (SELECT a FROM c1 WHERE b > 1) SELECT a FROM c2 UNION ALL SELECT a FROM c2");
        Assert.True(sql.IndexOf("c1 AS", StringComparison.Ordinal) < sql.IndexOf("c2 AS", StringComparison.Ordinal), sql);
        Assert.Equal(Rows(c, "WITH c1 AS MATERIALIZED (SELECT a, b FROM t WHERE a > 1), c2 AS MATERIALIZED (SELECT a FROM c1 WHERE b > 1) SELECT a FROM c2 UNION ALL SELECT a FROM c2", false), Rows(c, sql, false));
    }

    [Fact]
    public void Output_columns_and_their_resolved_types_come_from_the_plan()
    {
        using var c = Open();
        var q = PlanLowerer.Lower(PlanOf(c, "SELECT a / b AS ratio, avg(a) OVER () AS m, d, CAST(s AS VARCHAR(20)) AS name FROM t"));
        Assert.Equal([("ratio", "DOUBLE"), ("m", "DOUBLE"), ("d", "DATE"), ("name", "VARCHAR")], q.Columns.Select(x => (x.Name, x.DuckDbType)));
        Assert.Contains("avg-double", q.Rules);
        var sums = PlanLowerer.Lower(PlanOf(c, "SELECT sum(a) AS s FROM t"));
        Assert.Equal("HUGEINT", sums.Columns[0].DuckDbType);         // the type an engine's SUM over integers does not have
    }

    // subqueries: every one lowers to a query with the same rows (DuckDB's binder had decorrelated them; the lowerer writes them back as subqueries)
    public static TheoryData<string> Subqueries => new()
    {
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a)",
        "SELECT id FROM t WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.a = t.a)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND u.b > t.b)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a OR u.b = t.b)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.b > t.a * 10)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u)",
        "SELECT id FROM t WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.a > 100)",
        "SELECT id FROM t WHERE a IN (SELECT a FROM u)",
        "SELECT id FROM t WHERE a NOT IN (SELECT a FROM u)",
        "SELECT id FROM t WHERE a NOT IN (SELECT a FROM u WHERE a IS NOT NULL)",
        "SELECT id FROM t WHERE a IN (SELECT u.a FROM u WHERE u.b > t.b)",
        "SELECT id FROM t WHERE a NOT IN (SELECT u.a FROM u WHERE u.b > t.b)",
        "SELECT id FROM t WHERE a IN (SELECT a FROM u WHERE b > 150)",
        "SELECT id, EXISTS (SELECT 1 FROM u WHERE u.a = t.a) AS has_match FROM t",
        "SELECT id, a IN (SELECT a FROM u) AS in_u FROM t",
        "SELECT id, CASE WHEN EXISTS (SELECT 1 FROM u WHERE u.a = t.a) THEN 'y' ELSE 'n' END AS m FROM t",
        "SELECT id, (SELECT max(u.b) FROM u WHERE u.a = t.a) AS m FROM t",
        "SELECT id, (SELECT count(*) FROM u WHERE u.a = t.a) AS c FROM t",
        "SELECT id, (SELECT sum(u.b) FROM u WHERE u.a = t.a) AS s FROM t",
        "SELECT id, (SELECT u.b FROM u WHERE u.a = t.a AND u.b > 250) AS only_one FROM t",
        "SELECT id, (SELECT max(b) FROM u) AS m FROM t",
        "SELECT id FROM t WHERE b > (SELECT avg(u.b) / 100 FROM u WHERE u.a = t.a)",
        "SELECT id FROM t WHERE a > (SELECT min(a) FROM u)",
        "SELECT id FROM t WHERE (SELECT count(*) FROM u WHERE u.a = t.a) = 0",
        "SELECT id FROM t WHERE (SELECT u.b FROM u WHERE u.a = t.a ORDER BY u.b DESC LIMIT 1) > 100",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND EXISTS (SELECT 1 FROM u u2 WHERE u2.b = u.b AND u2.a = t.a))",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND u.b IN (SELECT b FROM u u3 WHERE u3.a = t.a))",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM (SELECT a, b FROM u WHERE b > 100) v WHERE v.a = t.a)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a GROUP BY u.a HAVING count(*) > 0)",
        "SELECT a, count(*) AS n FROM t GROUP BY a HAVING EXISTS (SELECT 1 FROM u WHERE u.a = t.a)",
        "SELECT t.id, x.b FROM t, LATERAL (SELECT u.b FROM u WHERE u.a = t.a) x",
        "SELECT t.id, x.b FROM t LEFT JOIN LATERAL (SELECT u.b FROM u WHERE u.a = t.a) x ON TRUE",
        "SELECT t.id, x.m FROM t, LATERAL (SELECT max(u.b) AS m FROM u WHERE u.a = t.a) x",
        "SELECT q.id, x.b FROM (SELECT * FROM t WHERE id > 1) q, LATERAL (SELECT u.b FROM u WHERE u.a = q.a) x",
        "SELECT q.id, x.b FROM (SELECT * FROM t WHERE id > 1) q LEFT JOIN LATERAL (SELECT u.b + q.b AS b FROM u) x ON TRUE",
        "SELECT t.id FROM t JOIN u ON t.a = u.a WHERE EXISTS (SELECT 1 FROM u u2 WHERE u2.b > u.b AND u2.a = t.a)",
        "SELECT id FROM t WHERE id IN (SELECT id FROM t t2 WHERE t2.a = t.a AND t2.id <> t.id)",
        "WITH big AS (SELECT a FROM u WHERE b > 100) SELECT id FROM t WHERE EXISTS (SELECT 1 FROM big WHERE big.a = t.a)",
        "SELECT id FROM t WHERE s IN (SELECT s FROM t t2 WHERE t2.d > t.d)",
        "SELECT t.id FROM t JOIN u ON t.a = u.a ORDER BY (SELECT count(*) FROM u u2 WHERE u2.a = t.a), t.id",
        "SELECT t.id FROM t JOIN u ON t.a = u.a AND EXISTS (SELECT 1 FROM u u2 WHERE u2.b > u.b)",
        "SELECT t.id, u.b FROM t JOIN u ON t.a = u.a WHERE u.b > (SELECT avg(u2.b) FROM u u2 WHERE u2.a = t.a)",
        "SELECT a, (SELECT count(*) FROM u WHERE u.a = x.a) AS c FROM (SELECT DISTINCT a FROM t) x",
        "SELECT id FROM t WHERE a + 1 IN (SELECT a FROM u WHERE u.b > t.b)",
        "SELECT sum((SELECT count(*) FROM u WHERE u.a = t.a)) AS s FROM t",
        "SELECT id FROM t WHERE EXISTS (SELECT DISTINCT u.b FROM u WHERE u.a = t.a)",
        "SELECT id, (SELECT u.b FROM u WHERE u.a = t.a ORDER BY u.b LIMIT 1) AS first_b FROM t",
        "SELECT id FROM t WHERE (SELECT count(*) FROM u WHERE u.a = t.a) > (SELECT count(*) FROM u WHERE u.b = t.b)",
        "SELECT id, (SELECT max(b) FROM u WHERE u.a = t.a) + (SELECT min(b) FROM u WHERE u.a = t.a) AS span FROM t",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND u.b > (SELECT avg(b) FROM u u2 WHERE u2.a = t.a))",
        "SELECT id FROM t WHERE NOT EXISTS (SELECT 1 FROM u)",
        "SELECT id FROM t WHERE a = (SELECT max(a) FROM u)",
        "SELECT id, row_number() OVER (ORDER BY (SELECT count(*) FROM u WHERE u.a = t.a), id) AS r FROM t",
        "SELECT id FROM t WHERE (SELECT count(*) FROM u WHERE u.a = t.a) BETWEEN 1 AND 2",
        "SELECT id FROM (SELECT id, a FROM t ORDER BY id LIMIT 5) q WHERE EXISTS (SELECT 1 FROM u WHERE u.a = q.a)",
    };

    [Theory, MemberData(nameof(Subqueries))]
    public void Subqueries_lower_to_subqueries_with_the_same_rows_as_the_original(string source)
    {
        using var c = Open();
        var lowered = Lower(c, source);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
        Assert.Contains("SELECT", lowered);
    }

    [Theory]
    [InlineData("SELECT sum((SELECT count(*) FROM u WHERE u.a = t.a)) AS s FROM t")]
    [InlineData("SELECT max(coalesce((SELECT max(u.b) FROM u WHERE u.a = t.a), 0)) AS m, count(*) AS n FROM t")]
    [InlineData("SELECT t.a, sum((SELECT count(*) FROM u WHERE u.a = t.a) + 1) AS s FROM t GROUP BY t.a")]
    public void An_aggregate_over_a_subquery_reads_the_subquerys_column_of_a_derived_table(string source)
    {
        // SQL Server rejects `sum((SELECT ...))`: "Cannot perform an aggregate function on an expression containing an aggregate or a subquery"
        using var c = Open();
        var lowered = Lower(c, source);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
        Assert.DoesNotMatch(@"(sum|max|min|count|avg)\(\s*\(?\s*\(\s*SELECT", lowered);
    }

    // x op ANY / ALL (subquery) and a row-value IN, as filter conditions: lowered with predicates only, and equal to DuckDB's rows on data with NULLs on both sides
    // (t.a is NULL for id 3, u.a is NULL in one row; none of these may differ from DuckDB, which is why they were refused or wrong before). A correlated row-value IN is not in the list: DuckDB itself
    // cannot run it ("Correlated IN/ANY/ALL with multiple columns not yet supported").
    public static TheoryData<string> ComparisonSubqueries => new()
    {
        "SELECT id FROM t WHERE a > ANY (SELECT a FROM u)",
        "SELECT id FROM t WHERE a <= ANY (SELECT a FROM u)",
        "SELECT id FROM t WHERE a = ANY (SELECT a FROM u)",
        "SELECT id FROM t WHERE a < ANY (SELECT a FROM u WHERE a IS NOT NULL)",
        "SELECT id FROM t WHERE a >= ALL (SELECT a FROM u)",
        "SELECT id FROM t WHERE a < ALL (SELECT a FROM u WHERE a IS NOT NULL)",
        "SELECT id FROM t WHERE a <> ALL (SELECT a FROM u)",
        "SELECT id FROM t WHERE a > ALL (SELECT a FROM u WHERE b > 1000)",
        "SELECT id FROM t WHERE a > ANY (SELECT a FROM u WHERE b > 1000)",
        "SELECT id FROM t WHERE NOT (a > ANY (SELECT a FROM u))",
        "SELECT id FROM t WHERE NOT (a <= ALL (SELECT a FROM u WHERE a IS NOT NULL))",
        "SELECT id FROM t WHERE a > ANY (SELECT u.b / 100 FROM u WHERE u.a = t.a)",
        "SELECT id FROM t WHERE a >= ALL (SELECT u.a FROM u WHERE u.b > t.b)",
        "SELECT id FROM t WHERE a >= ALL (SELECT avg(a) FROM u)",
        "SELECT id FROM t WHERE a > ANY (SELECT a FROM u ORDER BY a LIMIT 2)",
        "SELECT id FROM t WHERE a > ANY (SELECT DISTINCT a FROM u)",
        "SELECT id FROM t WHERE b = 3 OR a > ANY (SELECT a FROM u)",
        "SELECT id FROM t WHERE b = 3 AND a <= ALL (SELECT a FROM u WHERE a IS NOT NULL)",
        "SELECT id FROM t WHERE NOT (b = 3 OR a > ANY (SELECT a FROM u))",
        "SELECT id FROM t WHERE a > ANY (SELECT a FROM u) AND a < ALL (SELECT b FROM u)",
        "SELECT a, count(*) AS n FROM t GROUP BY a HAVING a > ANY (SELECT a FROM u)",
        "SELECT id FROM t WHERE (a, b) IN (SELECT a, b / 100 FROM u)",
        "SELECT id FROM t WHERE (a, b) NOT IN (SELECT a, b / 100 FROM u)",
        "SELECT id FROM t WHERE (a, b) NOT IN (SELECT a, b FROM u WHERE b < 0)",
        "SELECT id FROM t WHERE (a, b, id) IN (SELECT a, b, id FROM t t2 WHERE t2.x > 3)",
    };

    [Theory, MemberData(nameof(ComparisonSubqueries))]
    public void ANY_ALL_and_row_value_IN_lower_to_predicates_with_the_same_rows_as_DuckDB_on_data_with_NULLs(string source)
    {
        using var c = Open();
        var lowered = LowerExceptTheKnown2AnyAllGap(c, source);
        if (lowered == null) return;
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
        Assert.DoesNotContain('\u0003', lowered);
    }

    // a constant DuckDB stores in 128 bits (a DECIMAL wider than 18 digits, a HUGEINT) arrives in the plan as {upper, lower}, not as a number
    [Theory]
    [InlineData("SELECT CAST(123456789012345678901234567890.123456 AS DECIMAL(36, 6)) AS x", "123456789012345678901234567890.123456")]
    [InlineData("SELECT CAST(-123456789012345678901234567890.5 AS DECIMAL(38, 1)) AS x", "-123456789012345678901234567890.5")]
    [InlineData("SELECT CAST(0.000000000000000000000001 AS DECIMAL(30, 24)) AS x", "0.000000000000000000000001")]
    [InlineData("SELECT CAST(99999999999999999999999999999999999999 AS DECIMAL(38, 0)) AS x", "99999999999999999999999999999999999999")]
    public void A_wide_decimal_constant_is_lowered_with_every_digit(string source, string digits)
    {
        using var c = Open();
        var lowered = Lower(c, source);
        Assert.Contains(digits, lowered);
        string AsText(string sql) => $"SELECT CAST(x AS VARCHAR) AS x FROM ({sql}) q";                    // a .NET decimal cannot hold 38 digits: compare the text
        Assert.Equal(Rows(c, AsText(source), false), Rows(c, AsText(lowered), false));
    }

    // used as a value, the mark is written as `CASE WHEN <a row matches> THEN TRUE WHEN <none can> THEN FALSE ELSE NULL END`: DuckDB's three-valued answer, on data with NULLs on both sides
    public static TheoryData<string> ComparisonSubqueriesAsValues => new()
    {
        "SELECT id, a > ANY (SELECT a FROM u) AS g FROM t",
        "SELECT id, a >= ALL (SELECT a FROM u) AS g FROM t",
        "SELECT id, a > ALL (SELECT a FROM u WHERE b > 1000) AS g FROM t",
        "SELECT id FROM t WHERE (a > ANY (SELECT a FROM u)) IS NULL",
        "SELECT id FROM t WHERE CASE WHEN a > ALL (SELECT a FROM u) THEN 1 ELSE 0 END = 1",
        "SELECT id, (a, b) NOT IN (SELECT a, b / 100 FROM u) AS g FROM t",
        "SELECT id, (a, b) IN (SELECT a, b / 100 FROM u) AS g FROM t",
        "SELECT id FROM t ORDER BY a > ANY (SELECT a FROM u), id",
        "SELECT id, NOT (a > ANY (SELECT a FROM u)) AS g, (a = ANY (SELECT a FROM u)) AND b > 0 AS h FROM t",
        "SELECT id, a > ANY (SELECT a FROM u WHERE u.b = t.b) AS g FROM t",
        "SELECT count(*) AS n, count(a > ANY (SELECT a FROM u)) AS known FROM t",
        "SELECT g, count(*) AS n FROM (SELECT a > ANY (SELECT a FROM u) AS g FROM t) q GROUP BY g",
    };

    [Theory, MemberData(nameof(ComparisonSubqueriesAsValues))]
    public void A_comparison_subquery_used_as_a_value_is_written_as_a_CASE_with_DuckDBs_three_valued_answer(string source)
    {
        using var c = Open();
        var lowered = LowerExceptTheKnown2AnyAllGap(c, source);
        if (lowered == null) return;
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
        Assert.DoesNotContain('\u0003', lowered);
        Assert.Contains("CASE WHEN", lowered);
    }

    // what the engine probes found (tests/DbDataBuild.Tests.Conformance/EngineDifferenceProbes.cs): each lowered query returns DuckDB's rows on DuckDB itself, which is the first half of the claim that
    // the engines agree; the second half is the probes. t has NULLs, zeros, blanks and an emoji.
    public static TheoryData<string, string> Probed => new()
    {
        { "SELECT id FROM t WHERE s <> ''", "" },
        { "SELECT id, coalesce(s, '') AS v FROM t", "" },
        { "SELECT id, nullif(s, '') AS v FROM t", "" },
        { "SELECT id, replace(s, '', 'x') AS v FROM t", "" },
        { "SELECT id FROM t WHERE s IN ('', 'abc')", "" },
        { "SELECT id, position('b' IN s) AS v FROM t", "strpos(s, 'b')" },
        { "SELECT id, round(CAST(a AS DECIMAL(10, 2)) / 4) AS v FROM t", "round(" },
        { "SELECT id, round(a * 0.5) AS v FROM t", "round(" },
        { "SELECT id, year(d) AS y, month(d) AS m, day(d) AS dd, quarter(d) AS q, hour(ts) AS h, minute(ts) AS mi, second(ts) AS s2 FROM t", "date_part('year', d)" },
        { "SELECT id, dayofweek(d) AS w, dayofyear(d) AS y, isodow(d) AS i FROM t", "date_part('dow', d)" },
        { "SELECT id, a % b AS v FROM t", "NULLIF(b, 0)" },
        { "SELECT id, a % 3 AS v FROM t", "(a % 3)" },
        { "SELECT id, a // b AS v FROM t", "trunc(" },
        { "SELECT id, CAST(a * 0.5 AS INTEGER) AS v FROM t", "decimal-to-int" },
        { "SELECT id, CAST(CAST(a AS DECIMAL(10, 2)) AS SMALLINT) AS v FROM t", "round(" },
        { "SELECT id, CAST(a / 2 AS INTEGER) AS v FROM t", "double-to-int" },
        { "SELECT id, CAST(CAST(a AS DOUBLE) / 4 AS BIGINT) AS v FROM t", "double-to-int" },
        { "SELECT string_agg(s, ' | ' ORDER BY id DESC) AS v FROM t", "string_agg(s, ' | ' ORDER BY id DESC NULLS LAST)" },
        { "SELECT string_agg(s, '-') FILTER (WHERE id > 2) AS v FROM t", "FILTER (WHERE" },
        { "SELECT a, string_agg(s, ',') AS v FROM t GROUP BY a", "string_agg(s, ',')" },
        { "SELECT id, d1 - d2 AS v FROM t", "date_diff('day', d2, d1)" },
        { "SELECT id, substr(s, 2) AS v FROM t", "substr(s, 2, 2147483647)" },
        { "SELECT id, d1 + 3 AS v FROM t", "date-plus-days" },
        { "SELECT id, d1 - id AS v FROM t", "date-plus-days" },
        { "SELECT id, id + d1 AS v FROM t", "date-plus-days" },
    };

    [Theory, MemberData(nameof(Probed))]
    public void Constructs_the_engine_probes_found_wrong_lower_to_queries_with_DuckDBs_own_rows(string source, string expectedInLowered)
    {
        using var c = Open();
        var q = PlanLowerer.Lower(PlanOf(c, source), NamesOf(c, source));
        Assert.Equal(Rows(c, source, false), Rows(c, q.Sql, false));
        Assert.True(expectedInLowered == "" || q.Sql.Contains(expectedInLowered) || q.Rules.Contains(expectedInLowered), $"{expectedInLowered} is not in\n{q.Sql}\nrules: {string.Join(", ", q.Rules)}");
    }

    [Theory]
    [InlineData("SELECT quantile_cont(a, [0.25, 0.75]) AS v FROM t", "list of fractions")]
    [InlineData("SELECT median(DISTINCT a) AS v FROM t", "DISTINCT")]
    [InlineData("SELECT first(a ORDER BY id DESC) AS v FROM t", "ORDER BY inside")]
    [InlineData("SELECT list(a ORDER BY id) AS v FROM t", "")]
    [InlineData("SELECT x // 2 AS v FROM (SELECT CAST(a AS BIGINT) AS x FROM t) q", "BIGINT")]
    public void An_aggregate_with_data_the_lowerer_does_not_read_and_an_unsupported_integer_division_are_refused(string source, string fragment)
    {
        using var c = Open();
        var ex = Assert.Throws<LoweringException>(() => Lower(c, source));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void A_generated_alias_never_takes_the_name_of_a_table_of_the_query()
    {
        using var c = Open();
        Exec(c, "CREATE TABLE s1 (id INTEGER, x INTEGER); INSERT INTO s1 VALUES (1, 5), (2, NULL), (3, 1), (4, 9); CREATE TABLE s2 (id INTEGER, z INTEGER); INSERT INTO s2 VALUES (1, 2), (2, 4), (3, NULL);");
        Exec(c, "CREATE TABLE series_2 (id INTEGER); INSERT INTO series_2 VALUES (1), (2)");
        foreach (var source in new[]
        {
            "SELECT id FROM s1 WHERE x >= ALL (SELECT avg(z) FROM s2)",                                    // the derived table around the aggregate would have been called s1
            "SELECT id FROM s1 WHERE x > ANY (SELECT max(z) FROM s2)",
            "SELECT s1.id, q.m FROM s1 JOIN (SELECT id, max(z) AS m FROM s2 GROUP BY id) q ON q.id = s1.id",
            "SELECT g.x, series_2.id FROM generate_series(1, 3) AS g(x) JOIN series_2 ON series_2.id = g.x JOIN generate_series(1, 3) AS h(y) ON h.y = g.x",
        })
        {
            var lowered = LowerExceptTheKnown2AnyAllGap(c, source);
            if (lowered == null) continue;
            Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
            if (source.Contains("FROM s1 ")) Assert.DoesNotMatch(@"\) AS s1\b", lowered);                    // no derived table is named like the table s1
        }
    }

    [Fact]
    public void A_comparison_subquery_is_written_with_EXISTS_and_the_false_case_with_a_null_aware_NOT_EXISTS()
    {
        using var c = Open();
        var any = LowerExceptTheKnown2AnyAllGap(c, "SELECT id FROM t WHERE a > ANY (SELECT a FROM u)");
        if (any != null) Assert.Equal("SELECT id\nFROM t\nWHERE EXISTS (\n  SELECT 1\n  FROM u\n  WHERE (u.a < t.a)\n)", any);
        var all = LowerExceptTheKnown2AnyAllGap(c, "SELECT id FROM t WHERE a > ALL (SELECT a FROM u)");
        if (all != null) Assert.Equal("SELECT id\nFROM t\nWHERE (NOT EXISTS (\n  SELECT 1\n  FROM u\n  WHERE ((u.a >= t.a) OR u.a IS NULL OR t.a IS NULL)\n))", all);
        Assert.Equal("SELECT id\nFROM t\nWHERE EXISTS (\n  SELECT 1\n  FROM u\n  WHERE ((u.a = t.a) AND (u.b = t.b))\n)", Lower(c, "SELECT id FROM t WHERE (a, b) IN (SELECT a, b FROM u)"));
    }

    [Fact]
    public void Correlated_subqueries_read_like_the_original_with_the_outer_column_qualified()
    {
        using var c = Open();
        Assert.Equal("SELECT id\nFROM t\nWHERE EXISTS (\n  SELECT 1\n  FROM u\n  WHERE (u.a = t.a)\n)", Lower(c, "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a)"));
        Assert.Equal("SELECT id\nFROM t\nWHERE (NOT EXISTS (\n  SELECT 1\n  FROM u\n  WHERE (u.a = t.a)\n))", Lower(c, "SELECT id FROM t WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.a = t.a)"));
        Assert.Equal("SELECT id\nFROM t\nWHERE (a IN (\n  SELECT a\n  FROM u\n))", Lower(c, "SELECT id FROM t WHERE a IN (SELECT a FROM u)"));
        var scalar = Lower(c, "SELECT id, (SELECT max(u.b) FROM u WHERE u.a = t.a) AS m FROM t");
        Assert.Contains("max(u.b)", scalar);
        Assert.Contains("(u.a = t.a)", scalar);
        Assert.DoesNotContain("GROUP BY", scalar);          // the group on the correlated value is gone
    }

    [Fact]
    public void An_unqualified_name_inside_a_subquery_never_stands_for_an_outer_column()
    {
        using var c = Open();
        // t and u both have columns a and b: inside the subquery every column is written with its source
        var sql = Lower(c, "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND u.b = t.b)");
        Assert.DoesNotContain("WHERE (a =", sql);
        Assert.Contains("u.a = t.a", sql);
        Assert.Contains("u.b = t.b", sql);
    }

    [Theory]
    [InlineData("SELECT * FROM unnest([1, 2])", "table function")]
    [InlineData("SELECT a FROM t USING SAMPLE 3 ROWS", "SAMPLE")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t ORDER BY a, b", "DISTINCT ON")]
    [InlineData("SELECT [1, 2] AS l", "list_value")]
    [InlineData("SELECT a FROM t LIMIT 10 PERCENT", "percentage")]
    public void What_has_no_lowering_is_refused_by_name_never_guessed(string source, string mention)
    {
        using var c = Open();
        var ex = Assert.Throws<LoweringException>(() => Lower(c, source));
        Assert.Contains(mention, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_query_DuckDB_cannot_bind_is_reported_as_such()
    {
        using var c = Open();
        var json = "{\"error\":true,\"error_type\":\"binder\",\"error_message\":\"Table with name nope does not exist!\"}";
        var ex = Assert.Throws<LoweringException>(() => PlanLowerer.Lower(json));
        Assert.Contains("Table with name nope does not exist", ex.Message);
        Assert.Equal("binder", ex.Kind);
    }

    [Fact]
    public void The_production_path_builds_the_plan_against_an_empty_upstream_schema()
    {
        var upstream = new[] { new DuckTable("staging", "orders", [new DuckColumn("order_id", "BIGINT", false), new DuckColumn("amount", "DECIMAL(14, 2)", true)]) };
        var (json, error) = QueryDescriber.SerializePlan(upstream, "SELECT o.order_id, o.amount / 3 AS third FROM staging.orders o WHERE o.amount > 0");
        Assert.Null(error);
        var q = PlanLowerer.Lower(json!);
        Assert.Equal("SELECT order_id, (CAST(amount AS DOUBLE) / 3) AS third\nFROM staging.orders\nWHERE (amount > 0)", q.Sql);
        Assert.Equal([("order_id", "BIGINT"), ("third", "DOUBLE")], q.Columns.Select(x => (x.Name, x.DuckDbType)));
        Assert.StartsWith("v", QueryDescriber.DuckDbVersion());
        // DuckDB reports a query it cannot bind inside the JSON, and the lowerer turns that into a refusal that carries its message
        var (badJson, _) = QueryDescriber.SerializePlan(upstream, "SELECT nope FROM staging.orders");
        Assert.Contains("nope", Assert.Throws<LoweringException>(() => PlanLowerer.Lower(badJson!)).Message);
        // the text is one string literal: a second statement in it is not run, and it cannot end the literal
        var (hostileJson, _) = QueryDescriber.SerializePlan(upstream, "SELECT 1; DROP TABLE staging.orders");
        Assert.Throws<LoweringException>(() => PlanLowerer.Lower(hostileJson!));
        var (quoted, _) = QueryDescriber.SerializePlan(upstream, "SELECT 'x'' || (SELECT 1)' AS s");
        Assert.Contains("x'' || (SELECT 1)", PlanLowerer.Lower(quoted!).Sql);
    }
}
