using DbDataBuild.Core;
using DbDataBuild.Sql.Matrix;

namespace DbDataBuild.Tests.Unit;

public class MatrixLinterTests
{
    private static readonly SupportMatrix Matrix = MatrixLoader.LoadEmbedded([]);
    private static readonly MatrixLinter Linter = new(Matrix);
    private static readonly string[] All = ["sqlserver", "fabric", "postgres"];

    private static IReadOnlyList<Diagnostic> Lint(string sql, params string[] targets) =>
        Linter.Lint(sql, "models/m.sql", targets.Length == 0 ? All : targets);

    // One fixture per matrix row: the enumerated-data test (DESIGN.md principle 7).
    public static readonly IReadOnlyDictionary<string, string> RowFixtures = new Dictionary<string, string>
    {
        ["op.div"] = "SELECT a / b AS x FROM t",
        ["fn.avg"] = "SELECT AVG(a) AS x FROM t",
        ["fn.length"] = "SELECT LENGTH(s) AS x FROM t",
        ["fn.try_cast"] = "SELECT TRY_CAST(s AS INTEGER) AS x FROM t",
        ["type.cast_varchar_length"] = "SELECT CAST(s AS VARCHAR(20)) AS x FROM t",
        ["str.unicode_literal"] = "SELECT 'héllo' AS x",
        ["str.eq"] = "SELECT * FROM t WHERE s = 'abc'",
        ["str.like.case"] = "SELECT * FROM t WHERE s LIKE 'a%'",
        ["str.ilike"] = "SELECT * FROM t WHERE s ILIKE 'a%'",
        ["str.concat"] = "SELECT s || s AS x FROM t",
        ["fn.replace"] = "SELECT REPLACE(s, 'a', 'b') AS x FROM t",
        ["fn.current_date"] = "SELECT CURRENT_DATE AS x FROM t",
        ["fn.regexp"] = "SELECT * FROM t WHERE REGEXP_MATCHES(s, 'a')",
        ["syntax.qualify"] = "SELECT a FROM t QUALIFY ROW_NUMBER() OVER (PARTITION BY a ORDER BY b) = 1",
        ["syntax.group_by_ordinal"] = "SELECT a, COUNT(*) FROM t GROUP BY 1",
        ["syntax.group_by_all"] = "SELECT a, COUNT(*) FROM t GROUP BY ALL",
        ["type.list"] = "SELECT [1, 2] AS l",
        ["type.struct"] = "SELECT {'a': 1} AS s",
        ["fn.generate_series"] = "SELECT x FROM generate_series(1, 5) AS g(x)",
        ["fn.unnest"] = "SELECT UNNEST([1, 2]) AS u",
        ["fn.date_trunc"] = "SELECT DATE_TRUNC('month', d) AS x FROM t",
        ["op.interval_add"] = "SELECT d + INTERVAL 3 DAY AS x FROM t",
        ["fn.date_diff"] = "SELECT DATE_DIFF('day', d1, d2) AS x FROM t",
        ["syntax.order_nulls"] = "SELECT a FROM t ORDER BY a",
        ["syntax.join.using"] = "SELECT * FROM t LEFT JOIN u USING (a)",
        ["syntax.join.natural"] = "SELECT * FROM t NATURAL JOIN u",
        ["syntax.distinct_on"] = "SELECT DISTINCT ON (a) a, b FROM t",
        ["syntax.sample"] = "SELECT * FROM t USING SAMPLE 50%",
        ["str.case_mapping"] = "SELECT UPPER(s) AS x FROM t",
        ["fn.pad"] = "SELECT LPAD(s, 5, 'x') AS x FROM t",
        ["syntax.recursive_cte"] = "WITH RECURSIVE c AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM c WHERE n < 3) SELECT n FROM c",
        ["fn.greatest_least"] = "SELECT GREATEST(a, b) AS x FROM t",
        ["type.decimal_product_wide"] = "SELECT CAST(a AS DECIMAL(24, 0)) * b * CAST(b AS DECIMAL(38, 4)) AS x FROM t",
        ["fn.nth_value"] = "SELECT NTH_VALUE(x, 2) OVER (ORDER BY b ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS p FROM t",
        ["fn.json_extract_string"] = "SELECT JSON_EXTRACT_STRING(s, '$.a.b') AS x FROM t",
        ["fn.json_other"] = "SELECT JSON_VALID(s) AS x FROM t",
        ["fn.json_array_length"] = "SELECT JSON_ARRAY_LENGTH(s) AS x FROM t",
        ["fn.regexp_full_match"] = "SELECT REGEXP_FULL_MATCH(s, 'a.c') AS x FROM t",
        ["fn.regexp_extract"] = "SELECT REGEXP_EXTRACT(s, 'a(b)', 1) AS x FROM t",
        ["fn.regexp_replace"] = "SELECT REGEXP_REPLACE(s, 'a', 'x') AS x FROM t",
        ["fn.split_part"] = "SELECT SPLIT_PART(s, ',', 2) AS x FROM t",
        ["fn.contains"] = "SELECT * FROM t WHERE CONTAINS(s, 'a')",
        ["fn.mode"] = "SELECT MODE(a) AS x FROM t",
        ["fn.corr"] = "SELECT CORR(a, b) AS x FROM t",
        ["fn.string_agg"] = "SELECT STRING_AGG(s, ',' ORDER BY a) AS x FROM t",
        ["fn.substring_negative"] = "SELECT SUBSTR(s, -2) AS x FROM t",
        ["fn.date_unit"] = "SELECT DATE_PART('timezone_hour', ts) AS x FROM t",
        ["fn.strftime"] = "SELECT STRFTIME(ts, '%A %B %j') AS x FROM t",
        ["fn.format"] = "SELECT FORMAT('{}|{}', s, a) AS x FROM t",
        ["fn.age"] = "SELECT AGE(ts, d) AS x FROM t",
        ["fn.log"] = "SELECT LOG(a) AS x FROM t",
        ["fn.normalized_interval"] = "SELECT NORMALIZED_INTERVAL(INTERVAL 1 DAY) AS x FROM t",
        ["fn.normalized_interval"] = "SELECT NORMALIZED_INTERVAL(INTERVAL 1 DAY) AS x FROM t",
        ["fn.normalized_interval"] = "SELECT NORMALIZED_INTERVAL(INTERVAL 1 DAY) AS x FROM t",
        ["fn.normalized_interval"] = "SELECT NORMALIZED_INTERVAL(a) AS x FROM t",
        ["op.shift"] = "SELECT a << 2 AS x FROM t",
        ["fn.factorial"] = "SELECT FACTORIAL(a) AS x FROM t",
        ["fn.xor"] = "SELECT XOR(a, b) AS x FROM t",
        ["fn.power"] = "SELECT POWER(a, 2) AS x FROM t",
        ["op.interval_text"] = "SELECT d + CAST('1 d' AS INTERVAL) AS x FROM t",
        ["fn.missing.statistics"] = "SELECT ENTROPY(a) AS x FROM t",
        ["fn.missing.statistics.sqlserver"] = "SELECT COVAR_POP(a, b) AS x FROM t",
        ["fn.missing.statistics.postgres"] = "SELECT APPROX_COUNT_DISTINCT(a) AS x FROM t",
        ["fn.missing.picks"] = "SELECT ARG_MAX(a, b) AS x FROM t",
        ["fn.missing.lists"] = "SELECT LIST_SLICE(s, 1, 2) AS x FROM t",
        ["fn.missing.lists.sqlserver"] = "SELECT STR_SPLIT(s, 'b') AS x FROM t",
        ["fn.missing.encoding"] = "SELECT HASH(s) AS x FROM t",
        ["fn.missing.encoding.sqlserver"] = "SELECT HEX(a) AS x FROM t",
        ["fn.missing.similarity"] = "SELECT LEVENSHTEIN(s, 'abc') AS x FROM t",
        ["fn.missing.text"] = "SELECT STRIP_ACCENTS(s) AS x FROM t",
        ["fn.missing.text.sqlserver"] = "SELECT LIKE_ESCAPE(s, 'a%', '\\') AS x FROM t",
        ["fn.missing.json"] = "SELECT JSON_CONTAINS('{\"a\": 1}', '{\"a\": 1}') AS x FROM t",
        ["fn.missing.json.sqlserver"] = "SELECT JSON_EXISTS('{\"a\": 1}', '$.a') AS x FROM t",
        ["fn.missing.math"] = "SELECT EVEN(CAST(a AS DOUBLE)) AS x FROM t",
        ["fn.missing.math.sqlserver"] = "SELECT GCD(a, b) AS x FROM t",
        ["fn.missing.math.postgres"] = "SELECT ROUND_EVEN(CAST(a AS DOUBLE), 1) AS x FROM t",
        ["fn.missing.random"] = "SELECT UUID_EXTRACT_VERSION(UUIDV7()) AS x FROM t",
        ["fn.missing.random.sqlserver"] = "SELECT UUID_EXTRACT_VERSION(UUIDV4()) AS x FROM t",
        ["fn.missing.internal"] = "SELECT ALIAS(a) AS x FROM t",
        ["fn.missing.datetime"] = "SELECT EPOCH_US(ts) AS x FROM t",
        ["fn.missing.datetime.sqlserver"] = "SELECT MAKE_TIMESTAMP(2024, 1, 2, 3, 4, 5.0) AS x FROM t",
        ["fn.missing.datetime.postgres"] = "SELECT AGO(INTERVAL 1 DAY) AS x FROM t",
        ["syntax.values_list"] = "SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v(id, s)",
        ["syntax.lateral"] = "SELECT * FROM t, LATERAL (SELECT u.b FROM u WHERE u.a = t.a) AS l",
    };

    [Fact]
    public void Every_matrix_row_has_a_fixture_and_every_fixture_a_row()
    {
        Assert.Equal(Matrix.Rows.Select(r => r.Id).Order(), RowFixtures.Keys.Order());
    }

    [Fact]
    public void Each_fixture_triggers_its_row_on_every_target_that_makes_a_claim()
    {
        foreach (var row in Matrix.Rows)
        {
            var diags = Lint(RowFixtures[row.Id]);
            foreach (var (target, entry) in row.Targets)
            {
                var quiet = entry.Status is SupportStatus.Native or SupportStatus.Translated && entry.MinVersion == null;
                var hit = diags.Any(d => d.Found.Contains($"`{row.Id}`") && d.Found.Contains(" on " + target));
                Assert.True(quiet ? !hit : hit, $"{row.Id} on {target} ({entry.Status}): hit={hit}");
            }
        }
    }

    [Fact]
    public void Every_function_a_missing_function_row_lists_is_refused_on_the_engines_that_lack_it()
    {
        var checkedCount = 0;
        foreach (var row in Matrix.Rows.Where(r => r.Id.StartsWith("fn.missing.", StringComparison.Ordinal)))
            foreach (var detect in row.Detect.Where(d => d.Kind == DetectKind.Function))
            {
                var sql = Enumerable.Range(1, 3).Select(n => $"SELECT {detect.Name}({string.Join(", ", "abc".Take(n))}) AS x FROM t")
                    .FirstOrDefault(q => DbDataBuild.Sql.Polyglot.Parse(q, DbDataBuild.Sql.Dialects.Canonical).Ok);
                Assert.True(sql != null, $"{row.Id}: {detect.Name} does not parse with one to three arguments");
                var diags = Lint(sql!);
                foreach (var (target, entry) in row.Targets.Where(t => t.Value.Status == SupportStatus.Unsupported))
                    Assert.True(diags.Any(d => d.Code == "DDB-301" && d.Severity == Severity.Error && d.Found.Contains($"`{row.Id}`") && d.Found.Contains(" on " + target)), $"{sql} on {target}: not refused by {row.Id}");
                checkedCount++;
            }
        Assert.True(checkedCount > 150, $"only {checkedCount} functions checked");
    }

    [Fact]
    public void A_function_listed_by_two_rows_is_a_mistake()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Matrix.Rows)
            foreach (var d in row.Detect.Where(d => d.Kind == DetectKind.Function))
                if (seen.TryGetValue(d.Name, out var other) && other != row.Id && !other.StartsWith("fn.regexp", StringComparison.Ordinal) && !row.Id.StartsWith("fn.regexp", StringComparison.Ordinal))
                    Assert.Fail($"{d.Name} is in both {other} and {row.Id}");
                else seen[d.Name] = row.Id;
    }

    [Fact]
    public void Design_document_model_is_clean()
    {
        Assert.Empty(Lint("SELECT\n  o.order_id,\n  o.customer_id,\n  o.order_date,\n  o.amount,\n  o.discount_code\nFROM staging.orders AS o").Select(DiagnosticFormatter.Format));
    }

    [Fact]
    public void Severity_follows_status_per_target()
    {
        const string qualify = "SELECT a FROM t QUALIFY ROW_NUMBER() OVER (ORDER BY a) = 1";
        Assert.Contains(Lint(qualify, "fabric"), d => d.Code == "DDB-304" && d.Severity == Severity.Warning);
        Assert.Contains(Lint(qualify, "sqlserver"), d => d.Code == "DDB-303" && d.Severity == Severity.Note);
        Assert.DoesNotContain(Lint(qualify, "sqlserver"), d => d.Severity == Severity.Error);
        Assert.Contains(Lint("SELECT a, COUNT(*) FROM t GROUP BY 1", "sqlserver"), d => d.Code == "DDB-301" && d.Severity == Severity.Error);
        Assert.Contains(Lint("SELECT a / b FROM t", "sqlserver"), d => d.Code == "DDB-302" && d.Severity == Severity.Warning);
        Assert.Contains(Lint("SELECT [1] AS l", "postgres"), d => d.Code == "DDB-304");
    }

    [Fact]
    public void Only_declared_targets_are_evaluated()
    {
        const string ordinal = "SELECT a, COUNT(*) FROM t GROUP BY 1";
        Assert.Empty(Lint(ordinal, "postgres"));
        Assert.Contains(Lint(ordinal, "sqlserver"), d => d.Code == "DDB-301" && d.Found.Contains("on sqlserver"));
        Assert.DoesNotContain(Lint(ordinal, "sqlserver"), d => d.Found.Contains("on postgres"));
    }

    [Fact]
    public void Min_version_is_reported_as_a_warning_with_the_version()
    {
        var d = Assert.Single(Lint("SELECT * FROM t WHERE REGEXP_MATCHES(s, 'a')", "sqlserver"), x => x.Code == "DDB-308");
        Assert.Contains("17", d.Found);
        Assert.Equal(Severity.Warning, d.Severity);
    }

    [Theory]
    [InlineData("SELECT COSH(a) AS x FROM t", "function `COSH`")]
    [InlineData("SELECT CAST(a AS UUID) AS x FROM t", "data type `uuid`")]
    [InlineData("SELECT * FROM t SEMI JOIN u ON t.a = u.a", "join kind `Semi`")]
    public void Constructs_the_matrix_does_not_cover_are_reported_not_assumed_safe(string sql, string what)
    {
        Assert.Contains(Lint(sql), d => d.Code == "DDB-305" && d.Found.Contains(what));
    }

    [Fact]
    public void Verified_constructs_are_not_reported_as_uncovered()
    {
        Assert.Empty(Lint("SELECT CAST(a AS INTEGER) AS i, ABS(a) AS b, ROUND(CAST(a AS DECIMAL(10, 2)), 1) AS r FROM t WHERE a IN (1, 2) AND b BETWEEN 1 AND 3").Where(d => d.Code == "DDB-305"));
    }

    [Fact]
    public void Unparseable_sql_and_non_single_selects_are_errors_without_fallback()
    {
        Assert.Equal("DDB-306", Assert.Single(Lint("SELEC FROM FROM (")).Code);
        Assert.Equal("DDB-307", Assert.Single(Lint("SELECT 1; SELECT 2")).Code);
        Assert.Equal("DDB-307", Assert.Single(Lint("CREATE TABLE x (a INT)")).Code);
    }

    [Fact]
    public void Findings_carry_the_line_of_the_construct()
    {
        var d = Assert.Single(Lint("SELECT 1 AS one,\n  a / b AS x\nFROM t", "sqlserver"), x => x.Code == "DDB-302");
        Assert.Equal("models/m.sql", d.Location.File);
        Assert.Equal(2, d.Location.Line);
    }

    [Fact]
    public void Clause_level_findings_point_at_their_clause()
    {
        const string sql = "SELECT a, b\nFROM t\nGROUP BY 1\nQUALIFY ROW_NUMBER() OVER (PARTITION BY a ORDER BY b) = 1\nORDER BY a";
        var diags = Lint(sql, "sqlserver");
        Assert.Equal(4, Assert.Single(diags, d => d.Found.Contains("`syntax.qualify`")).Location.Line);
        // polyglot attaches no span to literals, so a clause made only of literals (GROUP BY 1) falls back to the select's first span.
        Assert.True(Assert.Single(diags, d => d.Found.Contains("`syntax.group_by_ordinal`")).Location.Line >= 1);
        Assert.Equal(5, Assert.Single(diags, d => d.Found.Contains("`syntax.order_nulls`") && d.Location.Line == 5).Location.Line);
    }

    [Fact]
    public void Lint_is_deterministic()
    {
        const string sql = "SELECT a / b, LENGTH(s), s || s FROM t WHERE s = 'x' ORDER BY a";
        Assert.Equal(Lint(sql).Select(DiagnosticFormatter.Format), Lint(sql).Select(DiagnosticFormatter.Format));
    }
}
