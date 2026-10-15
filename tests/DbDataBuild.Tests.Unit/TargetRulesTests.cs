using DbDataBuild.Lowering;
using DbDataBuild.Sql;
using DbDataBuild.Targets.Rules;

namespace DbDataBuild.Tests.Unit;

/// <summary>The step between the lowered query and the transpile (DESIGN.md 7.6.1). Behavior on real engines is in the conformance suite.</summary>
public class TargetRulesTests
{
    private static string Transpiled(string sql, string target) => Polyglot.TranspileOne(TargetRules.Apply(sql, target).Sql, Dialects.Canonical, target == "sqlserver" ? "tsql" : "postgres").Sql!;

    private static string ForSpark(string sql) => Polyglot.TranspileOne(TargetRules.Apply(sql, "spark").Sql, Dialects.Canonical, Dialects.ForTarget("spark")).Sql!;

    [Theory]
    [InlineData("SELECT a / b AS v FROM t", TargetRules.DivisionByZeroIsInfinity, "CASE WHEN b = 0")]
    [InlineData("SELECT concat(s, 'x') AS v FROM t", TargetRules.ConcatSkipsNull, "CONCAT_WS('', s, 'x')")]
    [InlineData("SELECT substr(s, 0, 2) AS v FROM t", TargetRules.SubstringBounds, "SUBSTRING(s, 1, 1)")]
    [InlineData("SELECT substr(s, 2, -1) AS v FROM t", TargetRules.SubstringBounds, "SUBSTRING(s, 1, 1)")]
    [InlineData("SELECT left(s, -1) AS v FROM t", TargetRules.SubstringBounds, "GREATEST(LENGTH(s) - 1, 0)")]
    [InlineData("SELECT week(d) AS v FROM t", TargetRules.WeekOfYear, "EXTRACT(WEEK FROM d)")]
    [InlineData("SELECT regexp_replace(s, 'a', 'x', 'g') AS v FROM t", TargetRules.RegexpReplaceFlags, "REGEXP_REPLACE(s, 'a', 'x')")]
    [InlineData("SELECT regexp_replace(s, '(a)(b)', '\\2\\1') AS v FROM t", TargetRules.RegexpReplaceFlags, "$2$1")]
    [InlineData("SELECT regexp_full_match(s, 'a.c') AS v FROM t", TargetRules.RegexpFullMatch, "RLIKE")]
    public void Spark_rules_rewrite_what_the_engine_does_differently(string sql, string rule, string expected)
    {
        Assert.Contains(rule, TargetRules.Apply(sql, "spark").Rules);
        Assert.Contains(expected.Replace(" ", ""), ForSpark(sql).Replace(" ", "").Replace("\\\\", "\\"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT now() AS v FROM t", "SYSDATETIMEOFFSET()")]
    [InlineData("SELECT CAST(now() AS TIMESTAMP) AS v FROM t", "CAST(SYSDATETIMEOFFSET() AS DATETIME2)")]
    [InlineData("SELECT date_trunc('day', now()) AS v FROM t", "SYSDATETIMEOFFSET()")]
    public void SQL_Server_reads_the_clock_with_its_zone(string sql, string expected)
    {
        Assert.Contains(TargetRules.NowKeepsTheZone, TargetRules.Apply(sql, "sqlserver").Rules);
        Assert.Contains(expected, For("sqlserver", sql));
        Assert.DoesNotContain("GETDATE", For("sqlserver", sql));
        Assert.DoesNotContain(TargetRules.NowKeepsTheZone, TargetRules.Apply(sql, "postgres").Rules);       // PostgreSQL's CURRENT_TIMESTAMP has a zone already
    }

    [Fact]
    public void The_date_and_local_time_functions_keep_the_engines_local_clock()
    {
        Assert.Contains("GETDATE", For("sqlserver", "SELECT current_date AS v FROM t"));
        Assert.Contains("GETDATE", For("sqlserver", "SELECT localtimestamp AS v FROM t"));
    }

    private static string For(string target, string sql) => TargetRules.Finish(Polyglot.TranspileOne(TargetRules.Apply(sql, target).Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!, target);

    [Theory]
    [InlineData("oracle", "SELECT a % b AS v FROM t", TargetRules.ModAsFunction, "MOD(a, b)")]
    [InlineData("bigquery", "SELECT a % b AS v FROM t", TargetRules.ModAsFunction, "MOD(a, b)")]
    [InlineData("oracle", "SELECT CAST(a AS VARCHAR) AS v FROM t", TargetRules.VarcharLength, "VARCHAR2(4000)")]
    [InlineData("oracle", "SELECT left(s, 2) AS v FROM t", TargetRules.LeftRightAsSubstr, "SUBSTR(s, 1, 2)")]
    [InlineData("oracle", "SELECT right(s, 2) AS v FROM t", TargetRules.LeftRightAsSubstr, "SUBSTR(s, -2)")]
    [InlineData("bigquery", "SELECT date_diff('month', a, b) AS v FROM t", TargetRules.DateDiffArgumentOrder, "DATE_DIFF(b, a, MONTH)")]
    public void Oracle_and_BigQuery_rules_write_the_engines_spelling(string target, string sql, string rule, string expected)
    {
        Assert.Contains(rule, TargetRules.Apply(sql, target).Rules);
        var text = For(target, sql);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ddb_", text, StringComparison.OrdinalIgnoreCase);     // every marker is replaced
    }

    private static string ForSqlServer(string sql, int? version) =>
        TargetRules.Finish(Polyglot.TranspileOne(TargetRules.Apply(sql, "sqlserver", null, version).Sql, Dialects.Canonical, "tsql").Sql!, "sqlserver");

    [Theory]
    [InlineData("SELECT regexp_replace(s, 'a', 'x') AS v FROM t", "REGEXP_REPLACE(s, 'a', 'x', 1, 1)")]
    [InlineData("SELECT regexp_replace(s, 'a', 'x', 'g') AS v FROM t", "REGEXP_REPLACE(s, 'a', 'x', 1, 0)")]
    [InlineData("SELECT regexp_extract(s, 'a(b)', 1) AS v FROM t", "REGEXP_SUBSTR(s, 'a(b)', 1, 1, 'c', 1)")]
    [InlineData("SELECT regexp_extract(s, 'ab') AS v FROM t", "REGEXP_SUBSTR(s, 'ab', 1, 1, 'c', 0)")]
    [InlineData("SELECT regexp_full_match(s, 'a.c') AS v FROM t", "REGEXP_LIKE(s, ('^(?:' + 'a.c' + ')$'))")]
    public void SQL_Server_2025_gets_regular_expressions_written_for_it_and_only_from_version_17(string sql, string expected)
    {
        Assert.Contains(expected.Replace(" ", ""), ForSqlServer(sql, 17).Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ddb_", ForSqlServer(sql, 17), StringComparison.OrdinalIgnoreCase);          // the marker is spelled out
        foreach (var version in new int?[] { null, 16 }) Assert.Empty(TargetRules.Apply(sql, "sqlserver", null, version).Rules);      // a 2022 project (or one that does not say) is left alone
        Assert.Empty(TargetRules.Apply(sql, "fabric", null, 17).Rules);                                    // Fabric has never been run: nothing is written for it on the strength of SQL Server's version
    }

    [Theory]
    [InlineData("SELECT regexp_replace(s, 'a', 'x', 'i') AS v FROM t")]
    [InlineData("SELECT regexp_extract(s, 'a(b)', n) AS v FROM t")]
    public void SQL_Server_2025_leaves_the_forms_whose_meaning_is_not_settled_to_the_matrix(string sql) => Assert.Empty(TargetRules.Apply(sql, "sqlserver", null, 17).Rules);

    [Theory]
    [InlineData("SELECT substr(s, 2, 2) AS v FROM t")]
    [InlineData("SELECT substr(s, -2, 2147483647) AS v FROM t")]
    [InlineData("SELECT left(s, 2) AS v FROM t")]
    public void Spark_substring_rule_leaves_ordinary_bounds_alone(string sql) => Assert.DoesNotContain(TargetRules.SubstringBounds, TargetRules.Apply(sql, "spark").Rules);

    [Fact]
    public void A_query_no_rule_applies_to_is_returned_byte_for_byte()
    {
        const string sql = "SELECT  a,\n  b\nFROM t   -- kept\nWHERE a > 1";
        foreach (var target in new[] { "sqlserver", "postgres", "fabric", "duckdb" })
        {
            var r = TargetRules.Apply(sql, target);
            Assert.Equal(sql, r.Sql);
            Assert.Empty(r.Rules);
        }
    }

    [Fact]
    public void A_cast_of_a_double_to_an_integer_rounds_half_to_even_on_SQL_Server_only()
    {
        const string sql = "SELECT CAST(CAST(x AS DOUBLE) AS BIGINT) AS n FROM t";
        var r = TargetRules.Apply(sql, "sqlserver");
        Assert.Equal([TargetRules.DoubleToInt], r.Rules);
        var text = Transpiled(sql, "sqlserver");
        Assert.Contains("CASE WHEN ABS(x - FLOOR(x)) = 0.5 THEN 2 * ROUND(x * 0.5, 0) ELSE ROUND(x, 0) END", text);
        Assert.StartsWith("SELECT CAST(CASE", text);
        Assert.EndsWith("AS BIGINT) AS n FROM t", text);
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);                 // PostgreSQL's own cast already rounds half to even
        Assert.Equal(sql, TargetRules.Apply(sql, "postgres").Sql);
        Assert.Empty(TargetRules.Apply("SELECT CAST(CAST(x AS INTEGER) AS BIGINT) AS n FROM t", "sqlserver").Rules);   // only a cast from a DOUBLE
    }

    [Fact]
    public void Weekday_numbers_are_counted_from_a_known_day_on_SQL_Server_so_DATEFIRST_does_not_matter()
    {
        var dow = TargetRules.Apply("SELECT date_part('dow', d) AS w FROM t", "sqlserver");
        Assert.Equal([TargetRules.WeekdayIndependentOfDateFirst], dow.Rules);
        Assert.Contains("DATEDIFF", Transpiled("SELECT date_part('dow', d) AS w FROM t", "sqlserver"));
        Assert.Contains("1900-01-07", dow.Sql);                      // a Sunday
        Assert.Contains("1900-01-01", TargetRules.Apply("SELECT date_part('isodow', d) AS w FROM t", "sqlserver").Sql);     // a Monday
        Assert.Empty(TargetRules.Apply("SELECT date_part('dow', d) AS w FROM t", "postgres").Rules);
        Assert.Empty(TargetRules.Apply("SELECT date_part('year', d) AS w FROM t", "sqlserver").Rules);
    }

    [Theory]
    [InlineData("SELECT lpad(s, 5, '*') AS x FROM t", "left(replicate('*', 5), 5 - (len(s + 'x') - 1)) + s")]
    [InlineData("SELECT rpad(s, 5, 'ab') AS x FROM t", "s + left(replicate('ab', 5), 5 - (len(s + 'x') - 1))")]
    public void Padding_to_a_literal_length_is_written_out_for_SQL_Server(string sql, string expectedPart)
    {
        var r = TargetRules.Apply(sql, "sqlserver");
        Assert.Equal([TargetRules.PadToLength], r.Rules);
        Assert.Contains(expectedPart, Transpiled(sql, "sqlserver").Replace("LEN(", "len(").Replace("LEFT(", "left(").Replace("REPLICATE(", "replicate("));
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);                                  // PostgreSQL has lpad and rpad
    }

    [Theory]
    [InlineData("SELECT lpad(s, n, '*') AS x FROM t")]          // the length is not a literal
    [InlineData("SELECT lpad(s, 5, p) AS x FROM t")]            // the pad is not a literal
    [InlineData("SELECT lpad(s, 5, '') AS x FROM t")]           // an empty pad
    [InlineData("SELECT lpad(s, 5000, '*') AS x FROM t")]       // longer than REPLICATE keeps of a non-MAX string
    public void Padding_that_cannot_be_written_out_is_left_for_the_matrix_to_refuse(string sql) => Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);

    [Theory]
    [InlineData("SELECT CAST(d AS DATE) + CAST(n AS INTEGER) AS x FROM t", "DATEADD(DAY, (n), d)")]
    [InlineData("SELECT CAST(d AS DATE) - CAST(3 AS INTEGER) AS x FROM t", "DATEADD(DAY, -(3), d)")]
    [InlineData("SELECT CAST(n AS BIGINT) + CAST(d AS DATE) AS x FROM t", "DATEADD(DAY, (n), d)")]
    public void A_date_and_a_number_of_days_become_an_interval_on_SQL_Server_only(string sql, string expectedPart)
    {
        var r = TargetRules.Apply(sql, "sqlserver");
        Assert.Equal([TargetRules.DatePlusDays], r.Rules);
        Assert.Contains(expectedPart, Transpiled(sql, "sqlserver").Replace("\n", " "));
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);                                  // PostgreSQL adds a number of days to a date itself
        Assert.Empty(TargetRules.Apply("SELECT CAST(a AS INTEGER) + CAST(b AS INTEGER) AS x FROM t", "sqlserver").Rules);
    }

    [Fact]
    public void String_agg_is_written_as_array_to_string_of_array_agg_for_PostgreSQL()
    {
        const string sql = "SELECT string_agg(DISTINCT g, ', ' ORDER BY g DESC) FILTER (WHERE w > 1) AS x FROM t GROUP BY k";
        var r = TargetRules.Apply(sql, "postgres");
        Assert.Equal([TargetRules.StringAggAsArrayToString], r.Rules);
        var text = Transpiled(sql, "postgres");
        Assert.Contains("ARRAY_TO_STRING(ARRAY_AGG(DISTINCT g ORDER BY g DESC) FILTER(WHERE w > 1), ', ')", text);
        Assert.DoesNotContain("LISTAGG", text);
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);                                 // STRING_AGG ... WITHIN GROUP is written by the transpile
    }

    [Fact]
    public void The_concatenation_operator_is_written_as_a_plus_on_SQL_Server_so_it_is_also_one_inside_strpos()
    {
        const string sql = "SELECT strpos(a || b, 'x') AS p, a || b || c AS q FROM t";
        var r = TargetRules.Apply(sql, "sqlserver");
        Assert.Equal([TargetRules.ConcatAsPlus], r.Rules);
        var text = Transpiled(sql, "sqlserver");
        Assert.DoesNotContain("||", text);
        Assert.Contains("CHARINDEX('x', a + b)", text);
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);
    }

    [Theory]
    [InlineData("year", "date_part('year', b) - date_part('year', a)")]
    [InlineData("month", "(date_part('year', b) - date_part('year', a)) * 12 + (date_part('month', b) - date_part('month', a))")]
    [InlineData("quarter", "(date_part('year', b) - date_part('year', a)) * 4 + (date_part('quarter', b) - date_part('quarter', a))")]
    public void A_difference_in_years_months_or_quarters_counts_boundaries_on_PostgreSQL_as_DuckDB_does(string unit, string expectedPart)
    {
        var sql = $"SELECT date_diff('{unit}', a, b) AS x FROM t";
        var r = TargetRules.Apply(sql, "postgres");
        Assert.Equal([TargetRules.DateDiffBoundaries], r.Rules);
        Assert.Contains(expectedPart, System.Text.RegularExpressions.Regex.Replace(r.Sql.ToLowerInvariant(), @"\s+", " ").Replace("( ", "(").Replace(" )", ")"));
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);                                 // DATEDIFF counts boundaries
        Assert.Empty(TargetRules.Apply("SELECT date_diff('day', a, b) AS x FROM t", "postgres").Rules);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public void A_difference_in_weeks_is_the_days_between_over_seven_on_both_engines(string target)
    {
        const string sql = "SELECT date_diff('week', a, b) AS x FROM t";
        var r = TargetRules.Apply(sql, target);
        Assert.Equal([TargetRules.DateDiffWeeks], r.Rules);
        Assert.Contains("date_diff('day'", r.Sql.ToLowerInvariant());
        Assert.DoesNotContain("'week'", r.Sql);
    }

    [Theory]
    [InlineData("SELECT json_extract_string(s, '$.a.b') AS x FROM t", "json_extract_path_text(json_extract_path(cast(s as json), 'a'), 'b')")]
    [InlineData("SELECT json_extract_string(s, '$.arr[1].k') AS x FROM t", "json_extract_path_text(json_extract_path(json_extract_path(cast(s as json), 'arr'), 1), 'k')")]
    [InlineData("SELECT json_extract_string(s, '$.k') AS x FROM t", "json_extract_path_text(cast(s as json), 'k')")]
    public void A_simple_json_path_becomes_the_keys_of_json_extract_path_text_on_PostgreSQL(string sql, string expected)
    {
        var r = TargetRules.Apply(sql, "postgres");
        Assert.Equal([TargetRules.JsonExtractString], r.Rules);
        Assert.Contains(expected, System.Text.RegularExpressions.Regex.Replace(r.Sql.ToLowerInvariant(), @"\s+", " ").Replace("( ", "(").Replace(" )", ")"));
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);                                 // JSON_VALUE is what the transpile writes
    }

    [Theory]
    [InlineData("SELECT json_extract_string(s, '$..k') AS x FROM t")]       // a wildcard
    [InlineData("SELECT json_extract_string(s, p) AS x FROM t")]            // a computed path
    [InlineData("SELECT json_extract_string(s, '$') AS x FROM t")]          // the whole document
    public void A_json_path_that_is_not_a_list_of_keys_is_left_for_the_matrix_to_refuse(string sql) => Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);

    [Fact]
    public void A_json_value_under_a_cast_keeps_its_last_key_on_PostgreSQL()
    {
        const string sql = "SELECT CAST(json_extract_string(s, '$.frame.gears') AS INTEGER) AS x FROM t";
        var text = Transpiled(sql, "postgres");
        Assert.Contains("-> 'frame' ->> 'gears'", text);
        Assert.DoesNotContain("'$.", text);
    }

    [Fact]
    public void Regular_expression_functions_PostgreSQL_does_not_have_are_written_out_for_it()
    {
        var full = TargetRules.Apply("SELECT regexp_full_match(s, 'a.c') AS x FROM t", "postgres");
        Assert.Equal([TargetRules.RegexpFullMatch], full.Rules);
        Assert.Contains("'^(?:'", full.Sql);
        var extract = TargetRules.Apply("SELECT regexp_extract(s, 'a(b)', 1) AS x FROM t", "postgres");
        Assert.Equal([TargetRules.RegexpExtract], extract.Rules);
        Assert.Contains("regexp_match(", extract.Sql.ToLowerInvariant());
        Assert.Contains("[2]", extract.Sql);                                                    // group 1 is the second element: the whole match comes first
        Assert.Contains("[1]", TargetRules.Apply("SELECT regexp_extract(s, 'ab') AS x FROM t", "postgres").Sql);
        Assert.Empty(TargetRules.Apply("SELECT regexp_extract(s, 'a(b)', n) AS x FROM t", "postgres").Rules);       // the group is not a literal
        Assert.Empty(TargetRules.Apply("SELECT regexp_extract(s, 'a(b)', 1) AS x FROM t", "sqlserver").Rules);
    }

    [Fact]
    public void Json_array_length_casts_text_to_json_on_PostgreSQL()
    {
        var r = TargetRules.Apply("SELECT json_array_length(s) AS x FROM t", "postgres");
        Assert.Equal([TargetRules.JsonArrayLength], r.Rules);
        Assert.Contains("cast(s as json)", r.Sql.ToLowerInvariant());
    }

    [Fact]
    public void Split_part_reaches_PostgreSQL_as_its_own_split_part()
    {
        const string sql = "SELECT coalesce(array_extract(string_split(s, ', '), 2), '') AS x FROM t";
        var r = TargetRules.Apply(sql, "postgres");
        Assert.Equal([TargetRules.SplitPart], r.Rules);
        Assert.Contains("split_part(s, ', ', cast(2 as int))", r.Sql.ToLowerInvariant());
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);                                 // there is nothing to write it as: the matrix refuses it
    }

    [Fact]
    public void Length_counts_trailing_spaces_on_SQL_Server_only()
    {
        var r = TargetRules.Apply("SELECT length(s) AS n FROM t", "sqlserver");
        Assert.Equal([TargetRules.LengthKeepsTrailingSpaces], r.Rules);
        Assert.Equal("SELECT LEN(s + 'x') - 1 AS n FROM t", Transpiled("SELECT length(s) AS n FROM t", "sqlserver"));
        Assert.Empty(TargetRules.Apply("SELECT length(s) AS n FROM t", "postgres").Rules);
        Assert.Equal([TargetRules.LengthKeepsTrailingSpaces], TargetRules.Apply("SELECT length(s) AS n FROM t", "fabric").Rules);
    }

    [Theory]
    [InlineData("SELECT round(CAST(x AS DOUBLE), 2) AS r FROM t", "SIGN(x * 100.0)")]
    [InlineData("SELECT round(CAST(x AS DOUBLE)) AS r FROM t", "SIGN(x) * FLOOR(ABS(x) + 0.5)")]
    [InlineData("SELECT round(CAST(x AS DOUBLE), -2) AS r FROM t", "SIGN(x / 100.0)")]
    public void Round_of_a_double_scales_rounds_half_away_from_zero_and_unscales(string sql, string expected)
    {
        foreach (var target in new[] { "sqlserver", "postgres" })
        {
            Assert.Equal([TargetRules.RoundDouble], TargetRules.Apply(sql, target).Rules);
            Assert.Contains(expected, TargetRules.Apply(sql, target).Sql.ToUpperInvariant().Replace("  ", " ").Replace("\n", "").Replace("( ", "(").Replace(" )", ")"), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("SELECT round(x, 2) AS r FROM t")]                                       // not marked as a double: a decimal rounds correctly on every engine
    [InlineData("SELECT round(CAST(x AS DOUBLE), d) AS r FROM t")]                        // digits not a constant
    [InlineData("SELECT round(CAST(x AS DOUBLE), 40) AS r FROM t")]                       // beyond what a double holds
    public void Round_is_left_alone_unless_it_is_marked_and_has_constant_digits(string sql)
    {
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);
    }

    [Fact]
    public void Try_cast_of_a_string_treats_blank_as_NULL_on_SQL_Server_and_tests_the_text_on_PostgreSQL()
    {
        const string sql = "SELECT TRY_CAST(CAST(s AS VARCHAR) AS INTEGER) AS i FROM t";
        Assert.Equal("SELECT TRY_CAST(NULLIF(TRIM(s), '') AS INTEGER) AS i FROM t", Transpiled(sql, "sqlserver"));
        var pg = Transpiled(sql, "postgres");
        Assert.Contains("CASE WHEN s ~ '^\\s*[+-]?[0-9]+\\s*$' THEN CASE WHEN CAST(s AS DECIMAL(30, 0)) BETWEEN -2147483648 AND 2147483647 THEN CAST(s AS INT) END END", pg);
        Assert.DoesNotContain("TRY_CAST", pg);
        Assert.Equal([TargetRules.TryCastParse], TargetRules.Apply(sql, "postgres").Rules);
    }

    [Fact]
    public void Try_cast_to_decimal_checks_the_range_the_target_column_holds()
    {
        var pg = Transpiled("SELECT TRY_CAST(CAST(s AS VARCHAR) AS DECIMAL(10, 2)) AS d FROM t", "postgres");
        Assert.Contains("ABS(CAST(s AS DECIMAL(38, 2))) < 100000000", pg);
        Assert.Contains("THEN CAST(s AS DECIMAL(10, 2))", pg);
    }

    [Fact]
    public void Try_cast_to_a_date_is_not_emulated_on_PostgreSQL_because_a_date_cannot_be_tested_without_raising()
    {
        const string sql = "SELECT TRY_CAST(CAST(s AS VARCHAR) AS DATE) AS d FROM t";
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);
        Assert.Equal([TargetRules.TryCastParse], TargetRules.Apply(sql, "sqlserver").Rules);
    }

    [Fact]
    public void Try_cast_that_is_not_marked_as_a_string_parse_is_left_alone()
    {
        Assert.Empty(TargetRules.Apply("SELECT TRY_CAST(x AS INTEGER) AS i FROM t", "sqlserver").Rules);
        Assert.Empty(TargetRules.Apply("SELECT TRY_CAST(CAST(s AS VARCHAR) AS VARCHAR) AS i FROM t", "sqlserver").Rules);
    }

    [Fact]
    public void Rules_apply_inside_subqueries_and_other_clauses()
    {
        var r = TargetRules.Apply("SELECT a FROM (SELECT a, length(s) AS n FROM t) q WHERE length(a) > 2 ORDER BY length(a)", "sqlserver");
        var t = Transpiled("SELECT a FROM (SELECT a, length(s) AS n FROM t) q WHERE length(a) > 2 ORDER BY length(a)", "sqlserver"); Assert.DoesNotContain("LENGTH(", t); Assert.Contains("LEN(s + \'x\') - 1", t); Assert.Contains("WHERE LEN(a + \'x\') - 1 > 2", t);
        Assert.Equal([TargetRules.LengthKeepsTrailingSpaces], r.Rules);
    }

    [Fact]
    public void The_lowerer_marks_exactly_the_expressions_the_rules_need()
    {
        using var c = DbDataBuild.Targets.DuckDb.PlanningConnection.Open(lockDown: false);
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "CREATE TABLE t (s VARCHAR, n INTEGER, x DOUBLE, d DECIMAL(10,2))"; cmd.ExecuteNonQuery(); }
        string Lower(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = DbDataBuild.Targets.DuckDb.QueryDescriber.PlanStatement(sql);
            return PlanLowerer.Lower((string)cmd.ExecuteScalar()!).Sql;
        }
        Assert.Contains("round(CAST(x AS DOUBLE), 2)", Lower("SELECT round(x, 2) AS r FROM t"));
        Assert.Contains("round(d, 2)", Lower("SELECT round(d, 2) AS r FROM t"));
        Assert.Contains("TRY_CAST(CAST(s AS VARCHAR) AS INTEGER)", Lower("SELECT TRY_CAST(s AS INTEGER) AS i FROM t"));
        Assert.Contains("TRY_CAST(n AS SMALLINT)", Lower("SELECT TRY_CAST(n AS SMALLINT) AS i FROM t"));
    }
}
