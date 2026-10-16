using System.Text;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Where the engines disagree with DuckDB over scalar expressions and aggregates, run the way the tool would send them. A case whose engine answer equals DuckDB's is a regression test; a case in
/// <see cref="Known"/> is a difference that is understood and written down (docs/research/engine-differences); anything else fails the run. Set DDB_PROBE_OUT to a file to get the full report.
/// </summary>
public class EngineDifferenceProbes
{
    private static EngineProbe.Case C(string id, string expr, string? note = null) => new(id, $"SELECT id, {expr} AS v FROM probe", note);
    private static EngineProbe.Case A(string id, string agg, string? note = null) => new(id, $"SELECT {agg} AS v FROM probe", note);

    public static readonly EngineProbe.Case[] Cases =
    [
        // integer and decimal arithmetic
        C("int_div", "i / j"), C("int_floor_div", "i // j"), C("mod", "i % j"), C("mod_neg", "(-7) % 3"), C("mod_neg2", "7 % (-3)"), C("mod_by_zero", "i % j"),
        C("mul", "i * j"), C("add_overflow", "i + j"), C("neg", "-i"), C("abs", "abs(i)"), C("sign", "sign(i)"), C("power", "power(i, 2)"), C("sqrt", "sqrt(abs(d))"),
        C("dec_div", "n / j"), C("dec_product_wide", "CAST(CAST(18 AS DECIMAL(24, 0)) * CAST(755.8018 AS DECIMAL(19, 4)) * CAST(0.6787 AS DECIMAL(38, 4)) * CAST(1.162 AS DECIMAL(38, 4)) AS DECIMAL(19, 4))"), C("dec_product_two", "CAST(CAST(18 AS DECIMAL(24, 0)) * CAST(755.8018 AS DECIMAL(19, 4)) AS DECIMAL(19, 4))"), C("dec_mul", "n * n"), C("dec_add", "n + d"), C("div_double", "d / j"), C("div_zero_double", "d / 0"), C("int_div_zero", "i / 0"),
        C("ceil", "ceil(d)"), C("floor", "floor(d)"), C("ceil_dec", "ceil(n)"), C("floor_dec", "floor(n)"), C("round1", "round(n, 1)"), C("round0", "round(n)"), C("round_d", "round(d)"), C("round_d1", "round(d, 1)"),
        C("round_half_dec", "round(CAST(2.5 AS DECIMAL(5, 1)))"), C("round_neg", "round(n, -1)"), C("trunc", "trunc(d)"), C("ln", "ln(abs(d) + 1)"), C("exp", "exp(i / 10.0)"), C("log10", "log10(abs(d) + 1)"),
        C("greatest", "greatest(i, j)"), C("least", "least(i, j)"), C("greatest3", "greatest(i, j, 3)"),
        // casts
        C("cast_double_int", "CAST(d AS INTEGER)"), C("cast_dec_int", "CAST(n AS INTEGER)"), C("cast_double_bigint", "CAST(d AS BIGINT)"), C("cast_int_varchar", "CAST(i AS VARCHAR)"),
        C("cast_double_varchar", "CAST(d AS VARCHAR)"), C("cast_dec_varchar", "CAST(n AS VARCHAR)"), C("cast_date_varchar", "CAST(dt AS VARCHAR)"), C("cast_ts_varchar", "CAST(ts AS VARCHAR)"),
        C("cast_int_double", "CAST(i AS DOUBLE)"), C("cast_int_dec", "CAST(i AS DECIMAL(10, 2))"), C("cast_double_dec", "CAST(d AS DECIMAL(10, 2))"), C("cast_dec_dec", "CAST(n AS DECIMAL(10, 0))"),
        C("cast_ts_date", "CAST(ts AS DATE)"), C("cast_date_ts", "CAST(dt AS TIMESTAMP)"), C("cast_int_bool", "CAST(i AS BOOLEAN)"), C("cast_str_bool", "CAST(s = 'abc' AS INTEGER)"),
        C("try_cast_str_int", "TRY_CAST(s AS INTEGER)"), C("try_cast_str_double", "TRY_CAST(s AS DOUBLE)"), C("try_cast_str_date", "TRY_CAST(s AS DATE)"), C("cast_str_int_literal", "CAST('12' AS INTEGER) + i"),
        C("cast_str_spaces_int", "CAST(' 12 ' AS INTEGER)"), C("cast_str_decimal_literal_int", "CAST('1.5' AS INTEGER)"), C("cast_varchar_len", "CAST(s AS VARCHAR(3))"),
        // text
        C("length", "length(s)"), C("upper", "upper(s)"), C("lower", "lower(s)"), C("substr2", "substr(s, 2, 2)"), C("substr0", "substr(s, 0, 2)"), C("substr_neg", "substr(s, -2)"), C("substr_neg_len", "substr(s, 2, -1)"),
        C("substr_from", "substr(s, 2)"), C("left", "left(s, 2)"), C("right", "right(s, 2)"), C("left_neg", "left(s, -1)"), C("concat_op", "s || 'x'"), C("strpos_concat", "strpos(s || 'x', 'x')"), C("strpos_concat_needle", "strpos(s, s || 'a')"), C("starts_with_concat", "starts_with(s || 'ab', 'ab')"), C("position_concat", "position('b' IN s || 'b')"), C("replace_concat", "replace(s || 'q', 'q', s || 'z')"), C("concat_fn", "concat(s, 'x')"), C("concat_int", "concat(s, i)"),
        C("concat_ws", "concat_ws('-', s, 'x')"), C("trim", "trim(s)"), C("ltrim", "ltrim(s)"), C("rtrim", "rtrim(s)"), C("replace", "replace(s, 'a', 'b')"), C("replace_empty", "replace(s, '', 'x')"),
        C("strpos", "strpos(s, 'b')"), C("position", "position('b' IN s)"), C("reverse", "reverse(s)"), C("lpad", "lpad(s, 5, '*')"), C("rpad", "rpad(s, 5, '*')"), C("lpad_short", "lpad(s, 2, '*')"), C("lpad_multi", "lpad(s, 7, 'ab')"), C("rpad_multi", "rpad(s, 6, 'xyz')"), C("lpad_exact", "lpad(s, 3, '-')"), C("lpad_zero", "lpad(s, 1, '-')"), C("split_part_first", "split_part(s, ',', 1)"), C("split_part_multichar", "split_part(s, 'b', 2)"), C("split_part_far", "split_part(s, ',', 9)"),
        C("repeat", "repeat(s, 2)"), C("like", "s LIKE 'a%'"), C("like_under", "s LIKE '_bc'"), C("ilike", "s ILIKE 'a%'"), C("not_like", "s NOT LIKE 'a%'"), C("starts_with", "starts_with(s, 'a')"),
        C("contains", "contains(s, 'b')"), C("eq", "s = 'abc'"), C("eq_trailing", "s = 'abc '"), C("lt", "s < 'b'"), C("ascii", "ascii(s)"), C("chr", "chr(65)"), C("split_part", "split_part(s, ',', 2)"),
        C("initcap_like", "upper(left(s, 1)) || lower(substr(s, 2))"), C("length_nonbmp", "length('😀a')"), C("upper_sharp_s", "upper('ß')"), C("lower_dotted_i", "lower('İ')"),
        C("min_str_cmp", "s > 'ABC'"),
        // nulls and logic
        C("coalesce", "coalesce(s, 'z')"), C("nullif", "nullif(i, 0)"), C("distinct_from", "i IS DISTINCT FROM j"), C("not_distinct", "i IS NOT DISTINCT FROM j"), C("case", "CASE WHEN i > j THEN 1 WHEN i = j THEN 0 ELSE -1 END"),
        C("between", "i BETWEEN 1 AND 7"), C("in_null", "i IN (7, NULL)"), C("not_in_null", "i NOT IN (7, NULL)"), C("and_null", "(i > 1) AND (j > 1)"), C("or_null", "(i > 1) OR (j > 1)"),
        C("bool_to_int", "CAST((i > 1) AS INTEGER)"), C("if_fn", "if(i > 1, 'y', 'n')"), C("ifnull", "ifnull(i, -1)"),
        C("empty_literal", "coalesce(s, '')"), C("ne_empty", "s <> ''"), C("nullif_empty", "nullif(s, '')"), C("eq_empty", "s = ''"), C("in_empty", "s IN ('', 'abc')"), C("concat_empty", "s || ''"),
        C("cast_double_int2", "CAST(d / 10000 AS INTEGER)"), C("cast_dec_smallint", "CAST(n AS SMALLINT)"), C("round_n_bigint", "CAST(round(n) AS BIGINT)"),
        // dates
        C("date_add", "dt + INTERVAL 1 DAY"), C("date_add_month", "dt + INTERVAL 1 MONTH"), C("date_sub", "dt - INTERVAL 1 MONTH"), C("date_diff_day", "date_diff('day', dt, DATE '2024-03-01')"),
        C("date_diff_month", "date_diff('month', dt, DATE '2024-03-01')"), C("date_diff_year", "date_diff('year', dt, DATE '2025-01-01')"), C("date_diff_quarter", "date_diff('quarter', dt, DATE '2024-06-30')"), C("date_diff_year_ts", "date_diff('year', ts, TIMESTAMP '2025-01-01 00:00:00')"), C("date_diff_month_neg", "date_diff('month', DATE '2024-03-01', dt)"), C("date_diff_week", "date_diff('week', dt, DATE '2024-03-01')"), C("date_part_year", "date_part('year', dt)"), C("extract_month", "extract(month FROM dt)"),
        C("year", "year(dt)"), C("month", "month(dt)"), C("day", "day(dt)"), C("dayofweek", "dayofweek(dt)"), C("dayofyear", "dayofyear(dt)"), C("week", "week(dt)"), C("quarter", "quarter(dt)"),
        C("date_trunc_month", "date_trunc('month', dt)"), C("date_trunc_year", "date_trunc('year', dt)"), C("date_trunc_day_ts", "date_trunc('day', ts)"), C("date_trunc_hour", "date_trunc('hour', ts)"),
        C("last_day", "last_day(dt)"), C("date_sub_dates", "dt - DATE '2024-01-01'"), C("date_plus_int", "dt + 3"), C("date_minus_int", "dt - 3"), C("date_plus_col", "dt + i"), C("int_plus_date", "j + dt"), C("date_plus_neg", "dt + (-40)"), C("ts_add", "ts + INTERVAL 90 MINUTE"), C("ts_diff", "date_diff('second', ts, TIMESTAMP '2024-03-01 00:00:00')"),
        C("hour", "hour(ts)"), C("minute", "minute(ts)"), C("second", "second(ts)"), C("year_ts", "year(ts)"), C("dp_quarter", "date_part('quarter', dt)"), C("dp_dow", "date_part('dow', dt)"),
        C("dp_doy", "date_part('doy', dt)"), C("dp_week", "date_part('week', dt)"), C("dp_isodow", "date_part('isodow', dt)"), C("dp_epoch", "date_part('epoch', ts)"), C("dp_hour", "date_part('hour', ts)"), C("make_date", "make_date(2024, 2, 29)"), C("dt_lt", "dt < DATE '2024-02-01'"), C("dt_eq_ts", "dt = CAST(ts AS DATE)"),
        // JSON (a document built from the text column, so every row differs) and regular expressions
        C("json_string", "json_extract_string('{\"k\": \"' || replace(coalesce(s, 'null'), '\"', '') || '\", \"n\": 5}', '$.k')"), C("json_number", "json_extract_string('{\"k\": 1, \"n\": 5.5}', '$.n')"),
        C("json_nested", "json_extract_string('{\"a\": {\"b\": \"deep\"}}', '$.a.b')"), C("json_missing", "json_extract_string('{\"a\": 1}', '$.zz')"), C("json_array_item", "json_extract_string('{\"arr\": [\"x\", \"y\"]}', '$.arr[1]')"),
        C("json_bool", "json_extract_string('{\"t\": true}', '$.t')"), C("json_null_value", "json_extract_string('{\"t\": null}', '$.t')"), C("json_object_value", "json_extract_string('{\"o\": {\"x\": 1}}', '$.o')"),
        C("json_arrow", "'{\"k\": \"v\"}'::JSON ->> '$.k'"), C("json_null_input", "json_extract_string(CAST(s AS JSON), '$.k')"),
        C("json_array_length", "json_array_length('[1, 2, 3]')"), C("json_valid", "json_valid('{\"a\": 1}')"),
        C("re_matches", "regexp_matches(s, '^a')"), C("re_matches_inner", "regexp_matches(s, 'b.')"), C("re_matches_case", "regexp_matches(s, 'ABC')"), C("re_full_match", "regexp_full_match(s, 'a.c')"),
        C("re_replace_first", "regexp_replace(s, 'a', 'x')"), C("re_replace_global", "regexp_replace(s, 'a', 'x', 'g')"), C("re_replace_group", "regexp_replace(s, '(a)(b)', '\\2\\1')"),
        C("empty_is_null", "s IS NULL"), C("empty_eq", "s = ''"), C("empty_coalesce", "coalesce(s, 'none')"), C("empty_count", "CASE WHEN s = '' THEN 1 ELSE 0 END"),
        C("re_replace_first_multi", "regexp_replace(s, '[a-c]', 'x')"), C("re_replace_global_multi", "regexp_replace(s, '[a-c]', 'x', 'g')"), C("re_extract_unmatched_group", "regexp_extract(s, 'a(z)?b', 1)"), C("re_full_match_alt", "regexp_full_match(s, 'a|abc')"),
        C("re_extract_group", "regexp_extract(s, 'a(b)', 1)"), C("re_extract_whole", "regexp_extract(s, 'b.')"), C("re_extract_none", "regexp_extract(s, 'zzz')"),
        // window functions (ordered by the unique id unless the case is about ties or NULLs)
        C("w_row_number", "row_number() OVER (ORDER BY id)"), C("w_rank_ties", "rank() OVER (ORDER BY i)"), C("w_dense_rank_ties", "dense_rank() OVER (ORDER BY i)"),
        C("w_lag", "lag(i) OVER (ORDER BY id)"), C("w_lag2_default", "lag(i, 2, -1) OVER (ORDER BY id)"), C("w_lead", "lead(i) OVER (ORDER BY id)"), C("w_lead2_default", "lead(i, 2, -1) OVER (ORDER BY id)"),
        C("w_lead_text", "lead(s) OVER (ORDER BY id)"), C("w_lead_date", "lead(dt) OVER (ORDER BY id)"),
        C("w_ntile3", "ntile(3) OVER (ORDER BY id)"), C("w_ntile5", "ntile(5) OVER (ORDER BY id)"), C("w_ntile_ties", "ntile(3) OVER (ORDER BY i, id)"),
        C("w_percent_rank", "percent_rank() OVER (ORDER BY id)"), C("w_percent_rank_ties", "percent_rank() OVER (ORDER BY i)"), C("w_cume_dist", "cume_dist() OVER (ORDER BY id)"), C("w_cume_dist_ties", "cume_dist() OVER (ORDER BY i)"),
        C("w_first_value", "first_value(i) OVER (ORDER BY id)"), C("w_last_value_default", "last_value(i) OVER (ORDER BY id)"), C("w_last_value_full", "last_value(i) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)"),
        C("w_nth_value", "nth_value(i, 2) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)"), C("w_first_value_ignore", "first_value(i IGNORE NULLS) OVER (ORDER BY id)"),
        C("w_sum_rows", "sum(i) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"), C("w_sum_range_ties", "sum(i) OVER (ORDER BY i)"), C("w_avg_rows3", "avg(i) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW)"),
        C("w_count_all", "count(*) OVER ()"), C("w_min_part", "min(i) OVER (PARTITION BY i > 3)"), C("w_sum_unbounded_following", "sum(i) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING)"),
        C("w_rows_following", "sum(i) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING)"), C("w_order_desc_nulls", "row_number() OVER (ORDER BY i DESC)"), C("w_order_nulls_first", "row_number() OVER (ORDER BY i NULLS FIRST, id)"),
        // aggregates over the whole table
        A("sum_int", "sum(i)"), A("sum_dec", "sum(n)"), A("sum_double", "sum(d)"), A("avg_int", "avg(i)"), A("avg_dec", "avg(n)"), A("min_str", "min(s)"), A("max_str", "max(s)"), A("count_all", "count(*)"),
        A("count_col", "count(s)"), A("count_distinct", "count(DISTINCT s)"), A("stddev", "stddev(d)"), A("stddev_pop", "stddev_pop(d)"), A("var_samp", "var_samp(d)"), A("var_pop", "var_pop(d)"),
        A("string_agg", "string_agg(s, ',' ORDER BY id)"), A("string_agg_sep", "string_agg(s, ' | ' ORDER BY id DESC)"), A("string_agg_filter", "string_agg(s, '-' ORDER BY id) FILTER (WHERE id > 1)"),
        A("quantile", "quantile_cont(d, 0.9)"), A("min_date", "min(dt)"), A("max_ts", "max(ts)"), A("bool_and", "bool_and(i > 0)"), A("bool_or", "bool_or(i > 5)"), A("median", "median(d)"),
        A("sum_empty", "sum(i) FILTER (WHERE id > 100)"), A("count_filter", "count(*) FILTER (WHERE i > 0)"), A("min_int_null", "min(i)"),
        A("sum_bigint_overflow", "sum(CAST(i AS BIGINT)) * 1000000000"), A("avg_double", "avg(d)"), A("corr", "corr(i, j)"), A("approx_free_mode", "mode(i)"),
    ];

    // Differences that are understood, as "<engine>:<case id>" to why. Each is a row of the support matrix, a diagnostic the linter already raises (DDB-305, the function is not covered),
    // or a note in docs/research/engine-differences/README.md. A case here must still differ: if it starts to agree, the test says so and the entry should go.
    private static readonly string DivisionByZero = "op.div: DuckDB 1.5 gives Infinity and NaN for a division by zero; the engines (rewritten to NULLIF) give NULL";
    private static readonly string Supplementary = "fn.length and docs: SQL Server counts and cuts UTF-16 code units, so a character outside the BMP (an emoji) is two";

    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>
    {
        ["sqlserver:int_div"] = DivisionByZero, ["sqlserver:dec_div"] = DivisionByZero, ["sqlserver:div_double"] = DivisionByZero, ["sqlserver:div_zero_double"] = DivisionByZero, ["sqlserver:int_div_zero"] = DivisionByZero,
        ["postgres:int_div"] = DivisionByZero, ["postgres:dec_div"] = DivisionByZero, ["postgres:div_double"] = DivisionByZero, ["postgres:div_zero_double"] = DivisionByZero, ["postgres:int_div_zero"] = DivisionByZero,
        ["sqlserver:exp"] = "exp() of a very large number raises an overflow error on the engine; DuckDB gives Infinity",
        ["postgres:exp"] = "exp() of a very large number raises an overflow error on the engine; DuckDB gives Infinity",
        ["sqlserver:cast_double_varchar"] = "the text form of a double differs (1e+010, 10000000000, 10000000000.0): cast to DECIMAL first (docs)",
        ["postgres:cast_double_varchar"] = "the text form of a double differs (1e+010, 10000000000, 10000000000.0): cast to DECIMAL first (docs)",
        ["sqlserver:cast_ts_varchar"] = "CAST(datetime2 AS VARCHAR) always shows six fractional digits; DuckDB shows none when they are zero (docs)",
        ["sqlserver:cast_str_decimal_literal_int"] = "DuckDB rounds the text '1.5' to 2; the engines refuse it (an error, not a wrong answer)",
        ["postgres:cast_str_decimal_literal_int"] = "DuckDB rounds the text '1.5' to 2; the engines refuse it (an error, not a wrong answer)",
        ["postgres:try_cast_str_date"] = "fn.try_cast: PostgreSQL has no try-cast; a bad date raises (the rule covers numbers)",
        ["sqlserver:length"] = Supplementary, ["sqlserver:length_nonbmp"] = Supplementary, ["sqlserver:substr2"] = Supplementary, ["sqlserver:substr0"] = Supplementary, ["sqlserver:substr_from"] = Supplementary,
        ["sqlserver:left"] = Supplementary, ["sqlserver:right"] = Supplementary, ["sqlserver:reverse"] = Supplementary, ["sqlserver:ascii"] = "ASCII() is the first byte, DuckDB's ascii() is the code point (and '' is NULL, not 0); UNICODE() is the equivalent",
        ["sqlserver:upper_sharp_s"] = "str.case_mapping: SQL Server leaves 'ß' alone, DuckDB makes it 'ẞ'",
        ["postgres:upper"] = "str.case_mapping: under the C collation PostgreSQL's upper() changes ASCII only",
        ["sqlserver:eq_trailing"] = "str.eq: SQL Server ignores trailing spaces in comparisons; the string profile (trailing_space) says so",
        ["postgres:substr_neg"] = "fn.substring_negative: a negative start does not count from the end on PostgreSQL",
        ["sqlserver:substr_neg"] = "fn.substring_negative: a negative start does not count from the end on SQL Server",
        ["sqlserver:substr_neg_len"] = "a negative length is an error on the engines; DuckDB returns an empty string",
        ["postgres:substr_neg_len"] = "a negative length is an error on the engines; DuckDB returns an empty string",
        ["sqlserver:left_neg"] = "a negative count is an error on SQL Server; DuckDB drops that many characters from the end",
       
       
       
                ["sqlserver:lpad"] = Supplementary, ["sqlserver:rpad"] = Supplementary, ["sqlserver:lpad_short"] = Supplementary, ["sqlserver:lpad_multi"] = Supplementary, ["sqlserver:rpad_multi"] = Supplementary, ["sqlserver:lpad_exact"] = Supplementary, ["sqlserver:lpad_zero"] = Supplementary,
        ["sqlserver:strpos_concat"] = Supplementary, ["sqlserver:position_concat"] = Supplementary,
        ["sqlserver:dec_product_wide"] = "type.decimal_product_wide",
        ["sqlserver:json_object_value"] = "fn.json_extract_string: JSON_VALUE gives NULL for an object or an array", ["postgres:json_object_value"] = "fn.json_extract_string: the text of an object keeps the document's spacing",
        ["sqlserver:json_array_length"] = "fn.json_array_length", ["sqlserver:json_valid"] = "fn.json_other", ["postgres:json_valid"] = "fn.json_other",
        ["sqlserver:re_matches"] = "fn.regexp", ["sqlserver:re_matches_inner"] = "fn.regexp", ["sqlserver:re_matches_case"] = "fn.regexp", ["sqlserver:re_full_match"] = "fn.regexp_full_match",
        ["sqlserver:re_replace_first"] = "fn.regexp_replace", ["sqlserver:re_replace_global"] = "fn.regexp_replace", ["sqlserver:re_replace_group"] = "fn.regexp_replace",
        ["sqlserver:re_replace_first_multi"] = "fn.regexp_replace", ["sqlserver:re_replace_global_multi"] = "fn.regexp_replace", ["sqlserver:re_extract_unmatched_group"] = "fn.regexp_extract", ["sqlserver:re_full_match_alt"] = "fn.regexp_full_match",
        ["sqlserver:re_extract_group"] = "fn.regexp_extract", ["sqlserver:re_extract_whole"] = "fn.regexp_extract", ["sqlserver:re_extract_none"] = "fn.regexp_extract",
        ["sqlserver:w_nth_value"] = "fn.nth_value",
        ["sqlserver:contains"] = "fn.contains", ["postgres:contains"] = "fn.contains",
        ["sqlserver:corr"] = "fn.corr", ["sqlserver:approx_free_mode"] = "fn.mode", ["postgres:approx_free_mode"] = "fn.mode",
        ["sqlserver:split_part"] = "fn.split_part", ["sqlserver:split_part_first"] = "fn.split_part", ["sqlserver:split_part_multichar"] = "fn.split_part", ["sqlserver:split_part_far"] = "fn.split_part", 
    };

    /// <summary>Documented differences of SQL Server 2022 that a 2025 run at compatibility level 170 does not have: the regular expression functions exist there, and the tool writes them (target rules from version 17).</summary>
    private static readonly HashSet<string> FixedOn2025 = ["re_matches", "re_matches_inner", "re_matches_case", "re_full_match", "re_full_match_alt", "re_replace_first", "re_replace_global", "re_replace_group", "re_replace_first_multi", "re_replace_global_multi", "re_extract_group", "re_extract_whole", "re_extract_none", "re_extract_unmatched_group"];
    /// <summary>What a 2025 server does at level 160 that 2022 does not: REGEXP_REPLACE is there (REGEXP_LIKE and REGEXP_SUBSTR are not). A project at version 16 does not use it.</summary>
    private static readonly HashSet<string> FixedOn2025At160 = ["re_replace_first", "re_replace_group"];

    private static string KnownKey(string name, string id) =>
        Known.ContainsKey($"{name}:{id}") ? $"{name}:{id}"
        : name == "sqlserver2025" && FixedOn2025.Contains(id) ? $"{name}:{id}"
        : name == "sqlserver2025-160" && FixedOn2025At160.Contains(id) ? $"{name}:{id}"
        : name.StartsWith("sqlserver2025", StringComparison.Ordinal) ? $"sqlserver:{id}" : $"{name}:{id}";      // a 2025 run is held to the documented differences of SQL Server unless it has its own entry

    /// <summary>The probe row that holds the empty string (id 4: see <see cref="EngineProbe.Seed"/>).</summary>
    private const string EmptyStringRow = "4|";

    /// <summary>
    /// For an engine that has no empty string: the answers differ, but only in the row that holds one (every row that is on one side only starts with the id of that row). That is the engine
    /// storing `''` as NULL, a class of difference that is the same everywhere (docs/research/engine-differences), not a finding about the query.
    /// </summary>
    internal static bool OnlyTheEmptyStringRowDiffers(string expected, string actual)
    {
        var a = expected.Split("; ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var b = actual.Split("; ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var onlyOne = a.Except(b).Concat(b.Except(a)).ToList();
        return onlyOne.Count > 0 && onlyOne.All(r => r.StartsWith(EmptyStringRow, StringComparison.Ordinal));
    }

    /// <summary>The checked-in lists of the cases that agree with DuckDB on the engines that are only probed (`Baselines/&lt;engine&gt;.txt`); `DDB_PROBE_BASELINE=update` rewrites them.</summary>
    private static string BaselineDirectory([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Baselines");

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("sqlserver2025")]
    [InlineData("sqlserver2025-160")]
    [InlineData("postgres")]
    [InlineData("oracle")]
    [InlineData("spark")]
    [InlineData("bigquery")]
    public async Task Scalar_expressions_and_aggregates_give_DuckDBs_answer_or_a_documented_difference(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        await probe.CreateTableAsync();

        var report = new StringBuilder();
        var unexpected = new List<string>();
        var stale = new List<string>();
        var preview = name is "oracle" or "spark" or "bigquery";      // dialects that are only probed so far: a baseline of the cases that agree, instead of a list of the differences
        var statuses = new Dictionary<string, string>();
        foreach (var c in Cases)
        {
            var expected = probe.OnDuckDb(c.Sql);
            var (rendered, refused) = probe.Render(c.Sql);
            string status, detail = "";
            if (refused != null) { status = "refused"; detail = refused; }
            else
            {
                var actual = await probe.OnEngineAsync(rendered!);
                if (expected.Error != null) { status = actual.Error != null ? "both-error" : "duckdb-errors"; detail = $"duckdb: {expected.Error}; engine: {actual.Error ?? actual.Rows}"; }
                else if (actual.Error != null) { status = "engine-error"; detail = $"{actual.Error}\n        sql    {rendered!.Replace("\n", " ")}"; }
                else if (actual.Rows == expected.Rows) status = "same";
                else
                {
                    status = engine.EmptyStringIsNull && OnlyTheEmptyStringRowDiffers(expected.Rows, actual.Rows) ? "empty-string" : "DIFFERENT";
                    detail = $"duckdb [{expected.Rows}]\n        engine [{actual.Rows}]\n        sql    {rendered!.Replace("\n", " ")}";
                }
            }
            report.AppendLine($"{status,-13} {c.Id,-24} {c.Sql}\n{(detail.Length > 0 ? "        " + detail + "\n" : "")}");
            var bad = status is "DIFFERENT" or "engine-error" or "duckdb-errors" || (status == "refused" && detail.StartsWith("CRASH", StringComparison.Ordinal));
            statuses[c.Id] = status;
            var key = KnownKey(name, c.Id);
            if (preview) continue;
            if (bad && !Known.ContainsKey(key)) unexpected.Add($"{c.Id}: {status}");
            if (!bad && Known.ContainsKey(key) && status is "same") stale.Add(c.Id);
        }
        if (Environment.GetEnvironmentVariable("DDB_PROBE_OUT") is { Length: > 0 } path) File.AppendAllText($"{path}.{name}.txt", report.ToString());
        if (preview)
        {
            var baselinePath = Path.Combine(BaselineDirectory(), name + ".txt");
            var agreeing = statuses.Where(s => s.Value == "same").Select(s => s.Key).Order(StringComparer.Ordinal).ToList();
            if (Environment.GetEnvironmentVariable("DDB_PROBE_BASELINE") == "update") File.WriteAllText(baselinePath, string.Join("\n", agreeing) + "\n");
            var baseline = File.Exists(baselinePath) ? File.ReadAllLines(baselinePath).Where(l => l.Length > 0).ToHashSet() : [];
            var regressed = baseline.Where(id => statuses.TryGetValue(id, out var s) && s != "same").Order(StringComparer.Ordinal).ToList();
            var summary = string.Join(", ", statuses.GroupBy(s => s.Value).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
            if (Environment.GetEnvironmentVariable("DDB_PROBE_OUT") is { Length: > 0 } outPath) File.AppendAllText($"{outPath}.{name}.summary.txt", summary + "\n");
            Assert.True(regressed.Count == 0, $"{name}: {regressed.Count} case(s) that agreed with DuckDB no longer do: {string.Join(", ", regressed)}  [now: {summary}]");
            return;
        }
        Assert.True(unexpected.Count == 0, $"{name}: {unexpected.Count} undocumented difference(s): {string.Join(", ", unexpected)}");
        Assert.True(stale.Count == 0, $"{name}: documented differences that are gone: {string.Join(", ", stale)}");
    }
}
