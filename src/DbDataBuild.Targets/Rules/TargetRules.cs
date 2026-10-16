using System.Globalization;
using System.Text.Json.Nodes;
using DbDataBuild.Core;
using DbDataBuild.Sql;

namespace DbDataBuild.Targets.Rules;

/// <summary>
/// The step between the lowered query and the transpile (DESIGN.md 7.6.1). The lowered query is in DuckDB's dialect and the same for every target; a few
/// functions behave differently on an engine, and polyglot can only translate the name. Each rule here rewrites the DuckDB-dialect AST into an equivalent
/// DuckDB-dialect expression built from functions that translate cleanly, so the rendered script for that target computes what DuckDB computes.
/// A rule fires only on a shape it recognises. Nothing here guesses at types: the lowerer, which knows them, marks the expressions that need a rule
/// (`round(CAST(x AS DOUBLE), n)`, `TRY_CAST(CAST(s AS VARCHAR) AS INTEGER)`), and the mark is removed when the rule fires.
/// </summary>
public static partial class TargetRules
{
    public sealed record Result(string Sql, IReadOnlyList<string> Rules);

    public const string LengthKeepsTrailingSpaces = "length-trailing-spaces";
    public const string RoundDouble = "round-double";
    public const string FloatWidth = "float-width";
    public const string TryCastParse = "try-cast-parse";
    public const string DoubleToInt = "double-to-int";
    public const string WeekdayIndependentOfDateFirst = "weekday-datefirst";
    public const string PadToLength = "pad-to-length";
    public const string DatePlusDays = "date-plus-days";
    public const string ConcatAsPlus = "concat-plus";
    public const string DateDiffBoundaries = "date-diff-boundaries";
    public const string DateDiffWeeks = "date-diff-weeks";
    public const string JsonExtractString = "json-extract-string";
    public const string JsonArrayLength = "json-array-length";
    public const string RegexpFullMatch = "regexp-full-match";
    public const string RegexpExtract = "regexp-extract";
    public const string SplitPart = "split-part";
    public const string StringAggAsArrayToString = "string-agg-array";
    public const string DivisionByZeroIsInfinity = "division-by-zero";
    public const string ConcatSkipsNull = "concat-skips-null";
    public const string RegexpReplaceFlags = "regexp-replace-flags";
    public const string RegexpReplaceFirst = "regexp-replace-first";
    public const string SubstringBounds = "substring-bounds";
    public const string WeekOfYear = "week-of-year";
    public const string ModAsFunction = "mod-function";
    public const string VarcharLength = "varchar-length";
    public const string LeftRightAsSubstr = "left-right-substr";
    public const string DateDiffArgumentOrder = "date-diff-argument-order";
    public const string NowKeepsTheZone = "now-with-zone";
    public const string LogIsBaseTen = "log-base-ten";
    public const string LogWithBase = "log-with-base";
    public const string HyperbolicFunctionsRule = FunctionTemplates.HyperbolicFunctions;
    public const string IntervalAsDateAdd = "interval-dateadd";
    public const string ValuesInChunks = "values-chunks";

    /// <summary>SQL Server (and Fabric, which follows it) takes at most 1000 row value expressions in one VALUES constructor (error 10738).</summary>
    public const int ValuesRowLimit = 1000;

    /// <summary>Applies the rules of <paramref name="target"/> to a DuckDB-dialect query. Returns the text unchanged, byte for byte, when no rule fires.</summary>
    /// <param name="version">The configured engine version (`targets.<target>.version`), when one is: a rule that needs a newer engine only fires from that version on.</param>
    public static Result Apply(string sql, string target, RewritePolicy? policy = null, int? version = null)
    {
        var rules = RulesFor(target, version);
        if (policy is { IsDefault: false }) rules.RemoveWhere(r => !policy.Allows(r));
        if (rules.Count == 0) return new(sql, []);
        var parsed = Polyglot.Parse(sql, Dialects.Canonical);
        if (!parsed.Ok) return new(sql, []);
        var fired = new SortedSet<string>(StringComparer.Ordinal);
        var tree = Rewrite(ParseTree(parsed.Data!), target, rules, fired);
        if (fired.Count == 0) return new(sql, []);
        var text = Polyglot.Generate(tree.ToJsonString(), Dialects.Canonical);
        if (!text.Ok) throw new InvalidOperationException($"Target rules ({string.Join(", ", fired)}) produced an AST polyglot cannot print: {text.Error}. This is a tool bug.");
        var formatted = Polyglot.Format(Unwrap(text.Data!), Dialects.Canonical);
        return new(formatted.Ok ? Unwrap(formatted.Data!) : Unwrap(text.Data!), fired.ToList());
    }

    // Some rules write a name polyglot would turn back into what it replaced (MOD into %) or into what the engine lacks (VARCHAR into CLOB). They write a marker, and Finish puts the engine's
    // spelling in after the transpile.
    private static readonly (string Marker, string Spelling, string Target)[] Markers =
    [
        (@"\bddb_mod\s*\(", "MOD(", "oracle"), (@"\bddb_mod\s*\(", "MOD(", "bigquery"),
        (@"\bddb_varchar\b", "VARCHAR2(4000)", "oracle"), (@"\bddb_date_diff\s*\(", "DATE_DIFF(", "bigquery"),
        (@"\bddb_regexp_replace\s*\(", "REGEXP_REPLACE(", "sqlserver"),
        (@"\bddb_sysdatetimeoffset\s*\(", "SYSDATETIMEOFFSET(", "sqlserver"),
        (@"\bddb_atn2\s*\(", "ATN2(", "sqlserver"), (@"\bddb_atn2\s*\(", "ATN2(", "fabric"),
        (@"\bddb_datepart_(\w+)\s*\(", "DATEPART($1, ", "sqlserver"), (@"\bddb_datepart_(\w+)\s*\(", "DATEPART($1, ", "fabric"),
        (@"\bddb_isoweek\s*\(", "DATEPART(ISO_WEEK, ", "sqlserver"), (@"\bddb_isoweek\s*\(", "DATEPART(ISO_WEEK, ", "fabric"),
        (@"\bddb_datediff_big_(\w+)\s*\(", "DATEDIFF_BIG($1, ", "sqlserver"), (@"\bddb_datediff_big_(\w+)\s*\(", "DATEDIFF_BIG($1, ", "fabric"),
    ];

    /// <summary>Replaces the markers of the rules of <paramref name="target"/> in the transpiled text. Returns the text unchanged when it holds none.</summary>
    public static string Finish(string transpiled, string target)
    {
        if (target == "sqlserver") transpiled = TsqlLiterals.Nationalize(transpiled);
        if (target is "sqlserver" or "fabric") transpiled = ExpandOperatorMarkers(transpiled);
        if (target == "postgres") transpiled = ExpandPostgresMarkers(transpiled);
        foreach (var (marker, spelling, markerTarget) in Markers)
            if (markerTarget == target) transpiled = System.Text.RegularExpressions.Regex.Replace(transpiled, marker, spelling, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return transpiled;
    }

    /// <summary>The AST of a statement. A query of a few dozen joins and expressions nests deeper than the 64 levels `JsonNode.Parse` allows by default.</summary>
    private static JsonNode ParseTree(string astJson) => JsonNode.Parse(astJson, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 4096 })!;

    private static string Unwrap(string data)
    {
        // Generate and Format return a JSON array of statements
        var n = JsonNode.Parse(data);
        return n is JsonArray a ? string.Join(";\n", a.Select(x => x!.GetValue<string>())) : data;
    }

    /// <summary>The names of the rules applied for a target, before any policy.</summary>
    public static IReadOnlyCollection<string> RulesOf(string target, int? version = null) => RulesFor(target, version);

    /// <summary>SQL Server 2025 (version 17: the T-SQL of database compatibility level 170) is the first with regular expressions. A project that says 16 gets none of these rules, and a query that needs them is reported by the matrix.</summary>
    internal const int SqlServerWithRegularExpressions = 17;
    private static readonly string[] SqlServerRegularExpressions = [RegexpFullMatch, RegexpExtract, RegexpReplaceFirst];

    private static HashSet<string> RulesFor(string target, int? version = null)
    {
        var rules = BaseRules(target, version);
        rules.UnionWith(FunctionTemplates.RulesFor(target));
        return rules;
    }

    private static HashSet<string> BaseRules(string target, int? version) => target switch
    {
        "sqlserver" => [FloatWidth, ValuesInChunks, NowKeepsTheZone, LogIsBaseTen, IntervalAsDateAdd, AgeParts, BitOperators, ShiftLeft, ShiftRight, FactorialTable, LengthKeepsTrailingSpaces, RoundDouble, TryCastParse, DoubleToInt, WeekdayIndependentOfDateFirst, PadToLength, DatePlusDays, ConcatAsPlus, DateDiffWeeks, ..(version >= SqlServerWithRegularExpressions ? SqlServerRegularExpressions : [])],
        "fabric" => [FloatWidth, ValuesInChunks, LogIsBaseTen, IntervalAsDateAdd, AgeParts, BitOperators, ShiftLeft, ShiftRight, FactorialTable, LengthKeepsTrailingSpaces, RoundDouble, TryCastParse, DoubleToInt, WeekdayIndependentOfDateFirst, PadToLength, DatePlusDays, ConcatAsPlus, DateDiffWeeks],
        "postgres" => [FloatWidth,CovarianceExact, NormalizedIntervalStrip, AgeSecond, LogWithBase, TruncDigits, ShiftLeft, ShiftRight, FactorialTable, RoundDouble, TryCastParse, SplitPart, StringAggAsArrayToString, DateDiffBoundaries, DateDiffWeeks, JsonExtractString, JsonArrayLength, RegexpFullMatch, RegexpExtract],
        "oracle" => [ModAsFunction, VarcharLength, LeftRightAsSubstr],
        "bigquery" => [ModAsFunction, DateDiffArgumentOrder, DateDiffWeeks],
        "spark" => [DivisionByZeroIsInfinity, ConcatSkipsNull, SplitPart, DateDiffBoundaries, DateDiffWeeks, WeekdayIndependentOfDateFirst, DoubleToInt, RegexpFullMatch, RegexpReplaceFlags, SubstringBounds, WeekOfYear],
        _ => [],
    };

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------

    private static JsonNode Rewrite(JsonNode node, string target, HashSet<string> rules, SortedSet<string> fired)
    {
        switch (node)
        {
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) if (a[i] is { } item && Rewrite(item, target, rules, fired) is var r && !ReferenceEquals(r, item)) a[i] = r;
                return a;
            case JsonObject o:
                if (rules.Contains(IntervalAsDateAdd)) MarkDateArithmetic(o);
                if (rules.Contains(FloatWidth) && FloatType(o)) fired.Add(FloatWidth);
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (o[key] is { } item && Rewrite(item, target, rules, fired) is var r && !ReferenceEquals(r, item)) o[key] = r;
                if (o.Count == 1) return Single(o, target, rules, fired) ?? o;
                return o;
            default:
                return node;
        }
    }

    private static JsonNode? Single(JsonObject o, string target, HashSet<string> rules, SortedSet<string> fired)
    {
        var (kind, body) = (o.First().Key, o.First().Value as JsonObject);
        if (body == null) return null;
        if (FunctionRewrite(kind, body, target, rules, fired) is { } functionRule) return functionRule;
        switch (kind)
        {
            case "function" when rules.Contains(NowKeepsTheZone) && body["name"]?.GetValue<string>() is "now" && body["args"] is JsonArray { Count: 0 }:
                // DuckDB's now() is a point in time (a timestamp with a zone). The transpile writes GETDATE(), the server's local clock without a zone, which a column with a zone reads as UTC: wrong by the
                // server's offset on any server that is not at UTC. SYSDATETIMEOFFSET() carries the offset.
                fired.Add(NowKeepsTheZone);
                return Template("ddb_sysdatetimeoffset()");
            case "log" when rules.Contains(LogIsBaseTen) && body["base"] is null && body["this"] is { } logOf:
                // DuckDB's log(x) is the base-10 logarithm and the transpile writes LOG(x), which is the natural logarithm in T-SQL (log(2.5): 0.3979 and 0.9163). log(b, x) with a base is written LOG(x, b) and agrees.
                fired.Add(LogIsBaseTen);
                return Template("log10(__x)", ("__x", logOf));
            case "log" when rules.Contains(LogWithBase) && body["base"] is { } logBase && body["this"] is { } logArgument:
                // DuckDB computes log(b, x) in double arithmetic as ln(x) / ln(b). PostgreSQL's two-argument log takes numeric (log(integer, double precision) does not exist) and answers in numeric.
                fired.Add(LogWithBase);
                return Template("ln(CAST(__x AS DOUBLE)) / ln(CAST(__b AS DOUBLE))", ("__x", logArgument), ("__b", logBase));
            case "length" when rules.Contains(LengthKeepsTrailingSpaces) && body["this"] is { } x:
                // LEN ignores trailing spaces; appending a character and taking one off counts them. NULL stays NULL.
                fired.Add(LengthKeepsTrailingSpaces);
                return Template("length(__x || 'x') - 1", ("__x", x));
            case "function" when rules.Contains(WeekdayIndependentOfDateFirst) && IsWeekdayPart(body, out var isoWeekday, out var day):
                // DATEPART(weekday) counts from whatever @@DATEFIRST says; DuckDB's dow is Sunday = 0 and isodow Monday = 1, whatever the session. Counting days from a known Sunday (1900-01-07) or Monday does not depend on it.
                fired.Add(WeekdayIndependentOfDateFirst);
                return Template(isoWeekday ? "((date_diff('day', DATE '1900-01-01', CAST(__x AS DATE)) % 7) + 7) % 7 + 1" : "((date_diff('day', DATE '1900-01-07', CAST(__x AS DATE)) % 7) + 7) % 7", ("__x", day));
            case "function" when rules.Contains(PadToLength) && IsPad(body, out var padLeft, out var padded, out var padLength, out var padWith):
                // SQL Server has no LPAD or RPAD before 2025. DuckDB cuts a string that is too long to n characters and pads a short one from the left of the pad text, repeated and cut to fit.
                fired.Add(PadToLength);
                return Template(padLeft
                    ? "CASE WHEN __n <= length(__s || 'x') - 1 THEN left(__s, __n) ELSE left(repeat(__p, __n), __n - (length(__s || 'x') - 1)) || __s END"
                    : "CASE WHEN __n <= length(__s || 'x') - 1 THEN left(__s, __n) ELSE __s || left(repeat(__p, __n), __n - (length(__s || 'x') - 1)) END",
                    ("__s", padded), ("__n", padLength), ("__p", padWith));
            case "function" when rules.Contains(SplitPart) && IsArrayExtractOfSplit(body, out var splitText, out var splitBy, out var splitIndex):
                // DuckDB's split_part is a macro over string_split and array_extract in the plan; PostgreSQL has split_part itself, which also gives '' past the last piece. Its position is an integer.
                fired.Add(SplitPart);
                return Template("split_part(__a, __b, CAST(__n AS INTEGER))", ("__a", splitText), ("__b", splitBy), ("__n", splitIndex));
            case "function" when rules.Contains(RoundDouble) && IsRoundOfDouble(body, out var arg, out var digits):
                fired.Add(RoundDouble);
                return RoundTemplate(arg, digits);
            case "cast" when rules.Contains(DoubleToInt) && IsDoubleToInt(body, out var number):
                // SQL Server's cast to an integer truncates; DuckDB rounds to the nearest, a half to the even neighbour. Scaling by a half, rounding half away from zero and doubling does that.
                fired.Add(DoubleToInt);
                body["this"] = Template("CASE WHEN abs(__x - floor(__x)) = 0.5 THEN 2 * round(__x * 0.5, 0) ELSE round(__x, 0) END", ("__x", number));
                return o;
            case "function" when rules.Contains(DateDiffBoundaries) && IsDateDiffInCalendarUnits(body, out var diffUnit, out var diffFrom, out var diffTo):
                // DuckDB counts the boundaries crossed between two dates (2023-12-31 to 2024-01-01 is one year); PostgreSQL's age() counts whole elapsed years and months. The calendar fields give the boundaries.
                fired.Add(DateDiffBoundaries);
                return diffUnit switch
                {
                    "year" => Template("date_part('year', __b) - date_part('year', __a)", ("__a", diffFrom), ("__b", diffTo)),
                    "quarter" => Template("(date_part('year', __b) - date_part('year', __a)) * 4 + (date_part('quarter', __b) - date_part('quarter', __a))", ("__a", diffFrom), ("__b", diffTo)),
                    _ => Template("(date_part('year', __b) - date_part('year', __a)) * 12 + (date_part('month', __b) - date_part('month', __a))", ("__a", diffFrom), ("__b", diffTo)),
                };
            case "function" when rules.Contains(DateDiffWeeks) && IsDateDiffInWeeks(body, out var weekFrom, out var weekTo):
                // DuckDB's weeks are whole periods of seven days between the two dates, counted toward zero (the days between, divided by seven). SQL Server counts week boundaries from whatever DATEFIRST says,
                // and PostgreSQL has no week unit (and its cast to an integer rounds where DuckDB cuts).
                fired.Add(DateDiffWeeks);
                return Template(target == "bigquery"
                    ? "CAST(sign(ddb_date_diff(__b, __a, DAY)) * floor(abs(ddb_date_diff(__b, __a, DAY)) / 7.0) AS BIGINT)"
                    : "CAST(sign(date_diff('day', __a, __b)) * floor(abs(date_diff('day', __a, __b)) / 7.0) AS BIGINT)", ("__a", weekFrom), ("__b", weekTo));
            case "function" when rules.Contains(JsonExtractString) && IsJsonExtractString(body, out var jsonText, out var jsonKeys):
                // polyglot writes `x ->> '$.a.b'` for PostgreSQL, which takes a key, not a path, and no operator takes text on the left. A simple path (names and array positions) is a list of keys of
                // json_extract_path_text, which gives the scalar as text, NULL for a missing path, and the text of an object or array (with PostgreSQL's spacing).
                fired.Add(JsonExtractString);
                // (polyglot keeps only the first key of a call with several, so the keys are chained: -> for the steps in between and ->> for the last, a position as a number and a name as text)
                var chain = "CAST(__x AS JSON)";
                for (var i = 0; i < jsonKeys.Count; i++) chain = $"{(i == jsonKeys.Count - 1 ? "json_extract_path_text" : "json_extract_path")}({chain}, {jsonKeys[i]})";
                // (in parentheses: directly inside a CAST polyglot writes the last key as a path, `->> '$.gears'`, which finds nothing)
                return Template($"({chain})", ("__x", jsonText));
            case "json_array_length" when rules.Contains(JsonArrayLength) && body["this"] is { } jsonArray:
                // PostgreSQL's json_array_length takes json, not text
                fired.Add(JsonArrayLength);
                return Template("json_array_length(CAST(__x AS JSON))", ("__x", jsonArray));
            case "function" when rules.Contains(RegexpFullMatch) && IsRegexp(body, "regexp_full_match", 2, out var fullText, out var fullPattern, out _):
                // regexp_full_match must match the whole text; polyglot writes the unanchored `~`. The pattern is wrapped in a group that does not capture and anchored.
                fired.Add(RegexpFullMatch);
                return Template("regexp_matches(__s, ('^(?:' || __p || ')$'))", ("__s", fullText), ("__p", fullPattern));
            case "function" when rules.Contains(RegexpExtract) && IsRegexp(body, "regexp_extract", 2, out var extractText, out var extractPattern, out var extractGroup):
                fired.Add(RegexpExtract);
                // DuckDB gives '' when nothing matches (and NULL for a NULL text). PostgreSQL has no regexp_extract: regexp_match returns the groups of the first match as an array without the whole match, so
                // wrapping the pattern in one more group puts the whole match first and group n is element n + 1. SQL Server 2025's REGEXP_SUBSTR takes the group, and gives NULL for no match
                // (and for a group that did not take part, which DuckDB also gives as '').
                return target == "sqlserver"
                    ? Template($"CASE WHEN __s IS NULL THEN NULL ELSE coalesce(regexp_substr(__s, __p, 1, 1, 'c', {extractGroup}), '') END", ("__s", extractText), ("__p", extractPattern))
                    : Template($"CASE WHEN __s IS NULL THEN NULL ELSE coalesce((regexp_match(__s, '(' || __p || ')'))[{extractGroup + 1}], '') END", ("__s", extractText), ("__p", extractPattern));
            case "function" when rules.Contains(RegexpReplaceFirst) && IsRegexpReplaceFirstOrAll(body):
                // DuckDB replaces the first match, or every match with the 'g' flag. SQL Server's REGEXP_REPLACE replaces every match unless it is given the occurrence: 1 for the first, 0 for all.
                fired.Add(RegexpReplaceFirst);
                return RegexpReplaceWithOccurrence(o, body);
            case "div" when rules.Contains(DivisionByZeroIsInfinity) && body["left"] is { } dividend && body["right"] is { } divisor:
                // DuckDB's double division by zero is Infinity (NaN for 0 / 0), never an error; Spark (ANSI mode, the default of 4.0) raises DIVIDE_BY_ZERO.
                fired.Add(DivisionByZeroIsInfinity);
                return Template("CASE WHEN __b = 0 THEN CASE WHEN __a > 0 THEN CAST('Infinity' AS DOUBLE) WHEN __a < 0 THEN CAST('-Infinity' AS DOUBLE) WHEN __a = 0 THEN CAST('NaN' AS DOUBLE) END ELSE __a / __b END", ("__a", dividend), ("__b", divisor));
            case "mod" when rules.Contains(ModAsFunction) && body["left"] is { } modLeft && body["right"] is { } modRight:
                // the transpile leaves `%`, which neither Oracle nor BigQuery has; both have MOD(a, b), which takes the sign of the dividend as DuckDB's % does
                fired.Add(ModAsFunction);
                return Template("ddb_mod(__a, __b)", ("__a", modLeft), ("__b", modRight));
            case "cast" when rules.Contains(VarcharLength) && body["to"] is JsonObject { } castTo && castTo["data_type"]?.GetValue<string>() == "var_char" && castTo["length"] == null:
                // a VARCHAR without a length is a CLOB on Oracle, which most functions and comparisons refuse; 4000 is the longest VARCHAR2
                fired.Add(VarcharLength);
                body["to"] = new JsonObject { ["data_type"] = "custom", ["name"] = "ddb_varchar" };
                return o;
            case "function" when rules.Contains(LeftRightAsSubstr) && IsLeftRight(body, out var lrRight, out var lrText, out var lrCount):
                fired.Add(LeftRightAsSubstr);
                return lrRight ? Template("substr(__s, -__n)", ("__s", lrText), ("__n", lrCount)) : Template("substr(__s, 1, __n)", ("__s", lrText), ("__n", lrCount));
            case "function" when rules.Contains(DateDiffArgumentOrder) && IsDateDiffInDays(body, out var ddUnit, out var ddFrom, out var ddTo):
                // BigQuery's is DATE_DIFF(later, earlier, PART) with the part as a keyword; it counts boundaries crossed, as DuckDB does
                fired.Add(DateDiffArgumentOrder);
                return Template($"ddb_date_diff(__b, __a, {ddUnit})", ("__a", ddFrom), ("__b", ddTo));
            case "substring" when rules.Contains(SubstringBounds) && SubstringWithinBounds(body) is { } bounded:
                // DuckDB counts position 0 as before the text (substr('abc', 0, 2) is 'a') and a negative length as characters to the left of the position; Spark treats 0 as 1 and a negative length as empty.
                fired.Add(SubstringBounds);
                body["start"] = Template(bounded.Start.ToString(CultureInfo.InvariantCulture));
                body["length"] = Template(bounded.Length.ToString(CultureInfo.InvariantCulture));
                return o;
            case "function" when rules.Contains(SubstringBounds) && IsLeftOfNegative(body, out var leftText, out var leftDrop):
                // left(s, -n) is all but the last n characters in DuckDB; Spark's is empty.
                fired.Add(SubstringBounds);
                return Template($"substring(__s, 1, greatest(length(__s) - {leftDrop}, 0))", ("__s", leftText));
            case "function" when rules.Contains(WeekOfYear) && string.Equals(body["name"]?.GetValue<string>(), "week", StringComparison.OrdinalIgnoreCase) && body["args"] is JsonArray { Count: 1 } weekArgs:
                // DuckDB's week() is the ISO week number; Spark has no function of that name, but EXTRACT(WEEK ...) is the ISO week too.
                fired.Add(WeekOfYear);
                return Template("date_part('week', __d)", ("__d", weekArgs[0]!));
            case "function" when rules.Contains(ConcatSkipsNull) && string.Equals(body["name"]?.GetValue<string>(), "concat", StringComparison.OrdinalIgnoreCase) && body["args"] is JsonArray concatArgs:
                // DuckDB's concat() treats NULL as ''; Spark's gives NULL. concat_ws skips NULL on both.
                fired.Add(ConcatSkipsNull);
                body["name"] = "concat_ws";
                concatArgs.Insert(0, Template("''"));
                return o;
            case "function" when rules.Contains(RegexpReplaceFlags) && IsRegexpReplaceWithFlags(body):
                // Spark replaces every match and takes the 4th argument as a start position, and it writes a group reference as $1 where DuckDB writes \1. The global flag is dropped (the default there);
                // a replacement without it (first match only) stays as it is: the matrix says so.
                fired.Add(RegexpReplaceFlags);
                return RegexpReplaceForSpark(o, body);
            case "concat" when rules.Contains(ConcatAsPlus) && body["left"] is { } concatLeft && body["right"] is { } concatRight:
                // The transpile turns `||` into `+` for SQL Server, except inside the arguments of the functions it rewrites into another shape (strpos becomes CHARINDEX with the arguments swapped, starts_with
                // becomes LEFT ... = ...): `strpos(a || b, 'x')` came out as `CHARINDEX('x', a || b)`, which T-SQL does not parse. Writing the plus here makes every `||` come out the same. The lowered query
                // casts a non-text operand to text itself, so a `+` never adds two numbers; both operands NULL-propagate like `||`.
                fired.Add(ConcatAsPlus);
                return Template("__a + __b", ("__a", concatLeft), ("__b", concatRight));
            case "string_agg" when rules.Contains(StringAggAsArrayToString) && body["separator"] is { } separator:
            {
                // polyglot writes LISTAGG for PostgreSQL, which has no such function. array_to_string(array_agg(x ORDER BY ...), sep) skips NULLs and gives NULL for no rows, as string_agg does.
                fired.Add(StringAggAsArrayToString);
                var agg = new JsonObject
                {
                    ["this"] = body["this"]!.DeepClone(), ["distinct"] = body["distinct"]?.DeepClone() ?? false, ["filter"] = body["filter"]?.DeepClone(),
                    ["order_by"] = body["order_by"]?.DeepClone() ?? new JsonArray(), ["name"] = "array_agg",
                };
                return new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = "array_to_string", ["args"] = new JsonArray(new JsonObject { ["array_agg"] = agg }, separator.DeepClone()),
                        ["distinct"] = false, ["use_bracket_syntax"] = false, ["no_parens"] = false,
                    },
                };
            }
            case "subquery" when rules.Contains(ValuesInChunks) && ValuesInChunksOf(body):
                fired.Add(ValuesInChunks);
                return o;
            case "add" or "sub" when rules.Contains(IntervalAsDateAdd) && IntervalArithmetic(kind, body, fired) is { } dateAdded:
                return dateAdded;
            case "neg" when rules.Contains(IntervalAsDateAdd) && body["this"] is { } negated && IntervalOf(negated) is { } negatedParts:
                fired.Add(IntervalAsDateAdd);
                return IntervalConstant(negatedParts.Negated);
            case "add" or "sub" when rules.Contains(DatePlusDays) && IsDatePlusDays(kind, body, out var dateSide, out var daysSide, out var subtract):
                // SQL Server refuses `date + int` (error 206). An interval of that many days is what DATEADD says.
                fired.Add(DatePlusDays);
                return Template(subtract ? "CAST(__d - INTERVAL (__n) DAY AS DATE)" : "CAST(__d + INTERVAL (__n) DAY AS DATE)", ("__d", dateSide), ("__n", daysSide));
            case "try_cast" when rules.Contains(TryCastParse) && TryCastTemplate(body, target) is { } replaced:
                fired.Add(TryCastParse);
                return replaced;
        }
        return null;
    }

    // ---- intervals on SQL Server ----------------------------------------------------------------------------------------------------------------------------
    // An interval is months, days and microseconds in DuckDB, and adding one to a date moves the months first (cutting the day back to the end of a shorter month), then the days, then the time. SQL Server has no
    // interval: the transpile reads the text `1 month 2 days 03:04:05` as one unit (the first) and drops the rest, and an interval that is not on the right of the date is not translated at all. Each part becomes
    // one DATEADD, in that order, which is what DuckDB does; nothing that is not exact is rewritten (an interval text this does not read is left for the matrix to refuse: row op.interval_text).

    private const string DateOperandMark = "ddb_date_operand";

    /// <summary>
    /// The lowerer writes `CAST(date + interval AS TIMESTAMP)` when a DATE is one of the operands (DuckDB's answer is a timestamp). That cast is the mark the target step reads: the date side of the sum is
    /// then made a timestamp before any hour, minute or second is added (DATEADD refuses those on a DATE), while a timestamp with a zone, which also has no cast around it, is left as it is.
    /// </summary>
    private static void MarkDateArithmetic(JsonObject o)
    {
        if (o.Count != 1 || o["cast"] is not JsonObject cast || cast["to"]?["data_type"]?.GetValue<string>() != "timestamp") return;
        var inner = cast["this"];
        while (inner?["paren"]?["this"] is { } deeper) inner = deeper;
        if (inner is JsonObject { Count: 1 } sum && sum.First().Key is "add" or "sub" && sum.First().Value is JsonObject sumBody) sumBody[DateOperandMark] = true;
    }

    private static readonly HashSet<string> DateAddUnits = ["Year", "Quarter", "Month", "Week", "Day"];

    /// <summary>The parts of a constant interval expression: INTERVAL n UNIT, CAST('text' AS INTERVAL), and parentheses, negation and sums and differences of those. Null for anything else.</summary>
    private static IntervalParts? IntervalOf(JsonNode? n)
    {
        if (n is not JsonObject { Count: 1 } o) return null;
        var (kind, body) = (o.First().Key, o.First().Value as JsonObject);
        if (body == null) return null;
        switch (kind)
        {
            case "paren": return IntervalOf(body["this"]);
            case "neg": return IntervalOf(body["this"])?.Negated;
            case "cast":
                if (body["to"]?["data_type"]?.GetValue<string>() != "interval" || body["this"]?["literal"] is not JsonObject text || text["literal_type"]?.GetValue<string>() != "string") return null;
                return IntervalParts.TryParse(text["value"]?.GetValue<string>() ?? "");
            case "interval" when body["unit"] is null && (body["this"]?["literal"]) is JsonObject { } written && (written["literal_type"]?.GetValue<string>()) is "string":
                // INTERVAL '1 month 2 days' written out in a model that is not lowered: the same text as the cast
                return IntervalParts.TryParse(written["value"]?.GetValue<string>() ?? "");
            case "interval" when body["unit"] is JsonObject { } unit && unit["type"]?.GetValue<string>() == "simple" && unit["unit"]?.GetValue<string>() is { } unitName && IntervalCount(body["this"]) is { } count:
                return IntervalParts.OfUnit(count, unitName);
            case "mul": return ScaledInterval(body);
            case "add" or "sub" when IntervalOf(body["left"]) is { } l && IntervalOf(body["right"]) is { } r:
                try { return kind == "add" ? checked(l + r) : checked(l + r.Negated); } catch (OverflowException) { return null; }
            default: return null;
        }
    }

    /// <summary>An interval times a whole-number constant, either way round: each of its three parts times that number, which is what DuckDB does. Null for anything else (a column, a fraction, an overflow).</summary>
    private static IntervalParts? ScaledInterval(JsonObject body)
    {
        var (interval, factor) = (IntervalOf(body["left"]), WholeConstant(body["right"]));
        if (interval is null || factor is null) (interval, factor) = (IntervalOf(body["right"]), WholeConstant(body["left"]));
        if (interval is not { } i || factor is not { } k) return null;
        try { return checked(new IntervalParts(i.Months * k, i.Days * k, i.Microseconds * k)); } catch (OverflowException) { return null; }
    }

    /// <summary>A whole number written as a literal, in parentheses, negated, or under a cast to an integer type (the lowerer casts the constants of a VALUES row).</summary>
    private static long? WholeConstant(JsonNode? n)
    {
        while (n?["paren"]?["this"] is { } inner) n = inner;
        if (n?["neg"]?["this"] is { } negated) return WholeConstant(negated) is { } v ? -v : null;
        if (n?["cast"] is JsonObject cast && cast["to"]?["data_type"]?.GetValue<string>() is "tiny_int" or "small_int" or "int" or "big_int") return WholeConstant(cast["this"]);
        return n?["literal"] is JsonObject lit && lit["literal_type"]?.GetValue<string>() == "number" && long.TryParse(lit["value"]?.GetValue<string>(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) ? whole : null;
    }

    /// <summary>The number of an `INTERVAL n UNIT`: a literal (the transpile reads `INTERVAL '3' DAY` and `INTERVAL 3 DAY` alike) or its negative.</summary>
    private static decimal? IntervalCount(JsonNode? n)
    {
        var negative = n?["neg"]?["this"] is { };
        var literal = negative ? n!["neg"]!["this"] : n;
        if (literal?["literal"] is not JsonObject lit || lit["literal_type"]?.GetValue<string>() is not ("number" or "string") || lit["value"]?.GetValue<string>() is not { } text) return null;
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return null;
        return negative ? -v : v;
    }

    private static JsonNode IntervalConstant(IntervalParts p) => Template($"CAST('{p.ToText()}' AS INTERVAL)");

    /// <summary>`date + interval`, `interval + date`, `date - interval` and the sum or difference of two constant intervals; null when the expression is none of those or the interval is not one this reads.</summary>
    private static JsonNode? IntervalArithmetic(string kind, JsonObject body, SortedSet<string> fired)
    {
        var dateOperand = body.Remove(DateOperandMark);
        if (body["left"] is not { } left || body["right"] is not { } right) return null;
        var (l, r) = (IntervalOf(left), IntervalOf(right));
        if (l is { } lp && r is { } rp)
        {
            fired.Add(IntervalAsDateAdd);                       // two constants: one constant, which the date above it (if any) reads
            try { return IntervalConstant(kind == "add" ? checked(lp + rp) : checked(lp + rp.Negated)); } catch (OverflowException) { return null; }
        }
        JsonNode date; IntervalParts parts;
        if (r is { } rightParts && l is null) { date = left; parts = kind == "sub" ? rightParts.Negated : rightParts; }
        else if (l is { } leftParts && kind == "add") { date = right; parts = leftParts; }
        else return null;
        // a single unit on the right, as the transpile already writes it (DATEADD(MONTH, n, d); the time units too unless the date side is a DATE), stays as it is
        if (IsPlainDateAddUnit(right, dateOperand) && l is null) return null;
        var steps = new List<string>();
        if (parts.Months != 0) steps.Add($"INTERVAL {parts.Months} MONTH");
        if (parts.Days != 0) steps.Add($"INTERVAL {parts.Days} DAY");
        var micros = parts.Microseconds;
        foreach (var (size, unit) in new[] { (3_600_000_000L, "HOUR"), (60_000_000L, "MINUTE"), (1_000_000L, "SECOND"), (1L, "MICROSECOND") })
        {
            var count = micros / size;
            micros -= count * size;
            if (count != 0) steps.Add($"INTERVAL {count} {unit}");
        }
        fired.Add(IntervalAsDateAdd);
        // a DATE takes no hour, minute or second in DATEADD: the date side is a timestamp from the first time unit on
        var alreadyTimestamp = date["cast"]?["to"]?["data_type"]?.GetValue<string>() == "timestamp";
        var expression = parts.Microseconds != 0 && dateOperand && !alreadyTimestamp ? "CAST(__d AS TIMESTAMP)" : "__d";
        foreach (var step in steps) expression = $"({expression} + {step})";
        return Template(expression, ("__d", date));
    }

    private static bool IsPlainDateAddUnit(JsonNode? n, bool dateOperand)
    {
        while (n?["paren"]?["this"] is { } inner) n = inner;
        return n?["interval"] is JsonObject { } i && i["unit"]?["type"]?.GetValue<string>() == "simple" && i["unit"]?["unit"]?.GetValue<string>() is { } u && (DateAddUnits.Contains(u) || !dateOperand && u is "Hour" or "Minute" or "Second") && IntervalCount(i["this"]) is { } c && c == decimal.Truncate(c);
    }

    /// <summary>lpad(s, n, p) or rpad(s, n, p) with a whole-number literal n (up to 4000, what REPLICATE keeps of a non-MAX string) and a non-empty literal pad. Anything else is left for the matrix to refuse.</summary>
    private static bool IsPad(JsonObject f, out bool left, out JsonNode s, out JsonNode n, out JsonNode pad)
    {
        left = false; s = n = pad = null!;
        var name = f["name"]?.GetValue<string>();
        if (!string.Equals(name, "lpad", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "rpad", StringComparison.OrdinalIgnoreCase)) return false;
        if (f["args"] is not JsonArray { Count: 3 } args || args[0] is not { } text) return false;
        if (args[1]?["literal"] is not JsonObject count || count["literal_type"]?.GetValue<string>() != "number" ||
            !int.TryParse(count["value"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 1 or > 4000) return false;
        if (args[2]?["literal"] is not JsonObject with || with["literal_type"]?.GetValue<string>() != "string" || string.IsNullOrEmpty(with["value"]?.GetValue<string>())) return false;
        left = string.Equals(name, "lpad", StringComparison.OrdinalIgnoreCase);
        s = text; n = args[1]!; pad = args[2]!;
        return true;
    }

    private static bool IsArrayExtractOfSplit(JsonObject f, out JsonNode text, out JsonNode by, out JsonNode index)
    {
        text = by = index = null!;
        if (!string.Equals(f["name"]?.GetValue<string>(), "array_extract", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 2 } outer) return false;
        if (outer[0] is not JsonObject { Count: 1 } inner || inner["function"] is not JsonObject split || !string.Equals(split["name"]?.GetValue<string>(), "string_split", StringComparison.OrdinalIgnoreCase) ||
            split["args"] is not JsonArray { Count: 2 } parts) return false;
        text = parts[0]!; by = parts[1]!; index = outer[1]!;
        return true;
    }

    /// <summary>`CAST(d AS DATE) + CAST(n AS INTEGER)` (either way round for +) or `CAST(d AS DATE) - CAST(n AS INTEGER)`, the shape the lowerer gives a date and a number of days.</summary>
    private static bool IsDatePlusDays(string kind, JsonObject b, out JsonNode date, out JsonNode days, out bool subtract)
    {
        date = days = null!; subtract = kind == "sub";
        if (b["left"] is not JsonObject { Count: 1 } l || b["right"] is not JsonObject { Count: 1 } r || l["cast"] is not JsonObject lc || r["cast"] is not JsonObject rc) return false;
        static string? Type(JsonObject c) => c["to"]?["data_type"]?.GetValue<string>();
        static bool Whole(string? t) => t is "tiny_int" or "small_int" or "int" or "big_int";
        if (Type(lc) == "date" && Whole(Type(rc))) { date = lc["this"]!; days = rc["this"]!; return true; }
        if (kind == "add" && Whole(Type(lc)) && Type(rc) == "date") { date = rc["this"]!; days = lc["this"]!; return true; }
        return false;
    }

    private static bool IsDateDiffInCalendarUnits(JsonObject f, out string unit, out JsonNode from, out JsonNode to)
    {
        unit = ""; from = to = null!;
        if (!string.Equals(f["name"]?.GetValue<string>(), "date_diff", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 3 } args) return false;
        var part = args[0]?["literal"]?["value"]?.GetValue<string>()?.ToLowerInvariant();
        if (part is not ("year" or "month" or "quarter") || args[1] is not { } a || args[2] is not { } b) return false;
        unit = part; from = a; to = b;
        return true;
    }

    private static bool IsDateDiffInWeeks(JsonObject f, out JsonNode from, out JsonNode to)
    {
        from = to = null!;
        if (!string.Equals(f["name"]?.GetValue<string>(), "date_diff", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 3 } args) return false;
        if (args[0]?["literal"]?["value"]?.GetValue<string>()?.ToLowerInvariant() != "week" || args[1] is not { } a || args[2] is not { } b) return false;
        from = a; to = b;
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]*|\[[0-9]+\])+$")]
    private static partial System.Text.RegularExpressions.Regex SimpleJsonPath();

    [System.Text.RegularExpressions.GeneratedRegex(@"\.([A-Za-z_][A-Za-z0-9_]*)|\[([0-9]+)\]")]
    private static partial System.Text.RegularExpressions.Regex JsonPathStep();

    /// <summary>json_extract_string(x, '$.a.b[1]') with a literal path of names and positions.</summary>
    private static bool IsJsonExtractString(JsonObject f, out JsonNode text, out List<string> keys)
    {
        text = null!; keys = [];
        if (!string.Equals(f["name"]?.GetValue<string>(), "json_extract_string", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 2 } args) return false;
        if (args[1]?["literal"] is not JsonObject lit || lit["literal_type"]?.GetValue<string>() != "string" || lit["value"]?.GetValue<string>() is not { } path || !SimpleJsonPath().IsMatch(path)) return false;
        keys = JsonPathStep().Matches(path).Select(m => m.Groups[1].Success ? "'" + m.Groups[1].Value + "'" : m.Groups[2].Value).ToList();      // a name as a text literal, a position as a number
        text = args[0]!;
        return true;
    }

    /// <summary>A regexp function with a literal group number (0 when it takes none): `regexp_extract(s, p)` and `regexp_extract(s, p, 2)`.</summary>
    private static bool IsRegexp(JsonObject f, string name, int minArgs, out JsonNode text, out JsonNode pattern, out int group)
    {
        text = pattern = null!; group = 0;
        if (!string.Equals(f["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray args || args.Count < minArgs || args.Count > 3) return false;
        if (args.Count == 3 && (args[2]?["literal"] is not JsonObject g || g["literal_type"]?.GetValue<string>() != "number" || !int.TryParse(g["value"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out group))) return false;
        if (name != "regexp_extract" && args.Count != minArgs) return false;
        text = args[0]!; pattern = args[1]!;
        return true;
    }

    private static int? IntegerLiteral(JsonNode? n)
    {
        if (n?["neg"]?["this"]?["literal"] is JsonObject negative && negative["literal_type"]?.GetValue<string>() == "number" && int.TryParse(negative["value"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return -m;
        if (n?["literal"] is JsonObject lit && lit["literal_type"]?.GetValue<string>() == "number" && int.TryParse(lit["value"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return v;
        return null;
    }

    /// <summary>The start and length Spark needs to give what DuckDB gives for a literal start of 0 or less, or a literal negative length; null when neither applies.</summary>
    private static (int Start, int Length)? SubstringWithinBounds(JsonObject f)
    {
        if (IntegerLiteral(f["start"]) is not { } start || IntegerLiteral(f["length"]) is not { } length || start < 0) return null;      // a negative start counts from the end: left to the engine
        if (length < 0)
        {
            if (start < 1) return (1, 0);
            var from = Math.Max(start + length, 1);
            return (from, start - from);
        }
        if (start < 1) return (1, Math.Max(length + start - 1, 0));
        return null;
    }

    private static bool IsLeftOfNegative(JsonObject f, out JsonNode text, out int drop)
    {
        text = null!; drop = 0;
        if (!string.Equals(f["name"]?.GetValue<string>(), "left", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 2 } args || IntegerLiteral(args[1]) is not { } n || n >= 0) return false;
        text = args[0]!; drop = -n;
        return true;
    }

    private static bool IsLeftRight(JsonObject f, out bool right, out JsonNode text, out JsonNode count)
    {
        right = false; text = count = null!;
        var name = f["name"]?.GetValue<string>();
        if (!string.Equals(name, "left", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "right", StringComparison.OrdinalIgnoreCase)) return false;
        if (f["args"] is not JsonArray { Count: 2 } args || IntegerLiteral(args[1]) is not { } n || n < 1) return false;     // zero and negative counts mean something else in DuckDB: left to the matrix
        right = string.Equals(name, "right", StringComparison.OrdinalIgnoreCase); text = args[0]!; count = args[1]!;
        return true;
    }

    private static bool IsDateDiffInDays(JsonObject f, out string unit, out JsonNode from, out JsonNode to)
    {
        unit = ""; from = to = null!;
        if (!string.Equals(f["name"]?.GetValue<string>(), "date_diff", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 3 } args) return false;
        var part = args[0]?["literal"]?["value"]?.GetValue<string>()?.ToLowerInvariant();
        if (part is not ("day" or "month" or "year" or "quarter") || args[1] is not { } a || args[2] is not { } b) return false;
        unit = part.ToUpperInvariant(); from = a; to = b;
        return true;
    }

    /// <summary>`regexp_replace(s, p, r)` and `regexp_replace(s, p, r, 'g')`: the two forms whose meaning is settled. Any other option string is left as it is.</summary>
    private static bool IsRegexpReplaceFirstOrAll(JsonObject f)
    {
        if (!string.Equals(f["name"]?.GetValue<string>(), "regexp_replace", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray args) return false;
        return args.Count == 3 || args.Count == 4 && args[3]?["literal"]?["value"]?.GetValue<string>() == "g";
    }

    private static JsonNode RegexpReplaceWithOccurrence(JsonObject o, JsonObject f)
    {
        // written under a marker name: the transpile reads a call of regexp_replace as DuckDB's (text, pattern, replacement, options) and drops the arguments it does not know
        var args = (JsonArray)f["args"]!;
        var all = args.Count == 4;
        if (all) args.RemoveAt(3);
        args.Add(Template("1"));                   // start at the first character
        args.Add(Template(all ? "0" : "1"));       // the occurrence: every match, or the first
        f["name"] = "ddb_regexp_replace";
        return o;
    }

    private static bool IsRegexpReplaceWithFlags(JsonObject f)
    {
        if (!string.Equals(f["name"]?.GetValue<string>(), "regexp_replace", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray args) return false;
        var literalFlags = args.Count == 4 && args[3]?["literal"]?["value"]?.GetValue<string>() == "g";
        var literalReplacement = args.Count >= 3 && args[2]?["literal"]?["value"]?.GetValue<string>() is { } r && r.Contains('\\');
        return literalFlags || literalReplacement;
    }

    private static JsonNode RegexpReplaceForSpark(JsonObject o, JsonObject f)
    {
        var args = (JsonArray)f["args"]!;
        if (args.Count == 4) args.RemoveAt(3);
        if (args[2]?["literal"] is JsonObject lit && lit["value"]?.GetValue<string>() is { } text)
            lit["value"] = System.Text.RegularExpressions.Regex.Replace(text, @"\\([0-9])", "$$$1");
        return o;
    }

    private static bool IsWeekdayPart(JsonObject f, out bool iso, out JsonNode day)
    {
        iso = false; day = null!;
        if (!string.Equals(f["name"]?.GetValue<string>(), "date_part", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray { Count: 2 } args) return false;
        var part = args[0]?["literal"]?["value"]?.GetValue<string>()?.ToLowerInvariant();
        if (part is not ("dow" or "dayofweek" or "isodow") || args[1] is not { } x) return false;
        iso = part == "isodow";
        day = x;
        return true;
    }

    private static bool IsDoubleToInt(JsonObject cast, out JsonNode x)
    {
        x = null!;
        if (cast["to"]?["data_type"]?.GetValue<string>() is not ("tiny_int" or "small_int" or "int" or "big_int")) return false;
        if (cast["this"] is not JsonObject { Count: 1 } mark || mark["cast"] is not JsonObject inner || inner["to"]?["data_type"]?.GetValue<string>() != "double" || inner["this"] is not { } value) return false;
        x = value;
        return true;
    }

    // ---- FLOAT -----------------------------------------------------------------------------------------------------------------------------------------------
    // DuckDB's FLOAT (FLOAT4, REAL) is 32 bits, its DOUBLE (FLOAT8, DOUBLE PRECISION) 64, and FLOAT(n) is the first for n up to 24 and the second for 25 to 53 (n outside 1 to 53 is a DuckDB parse error,
    // so such text never gets here). polyglot's reader takes a FLOAT for the engines' own FLOAT, which is 8 bytes on SQL Server (a column definition or a VALUES cell came out as FLOAT), and keeps
    // FLOAT(n), FLOAT4 and FLOAT8 as written (`REAL(24)` and `FLOAT8` are not types of SQL Server). The spelling REAL is written as REAL by both engines and DOUBLE as FLOAT and DOUBLE PRECISION,
    // so every type node of the 32-bit kind is rewritten to the REAL spelling and of the 64-bit kind to DOUBLE, whatever the position (cast, column definition, cell).

    /// <summary>Rewrites a FLOAT, FLOAT(n), FLOAT4 or FLOAT8 type node in place to REAL or DOUBLE. Returns whether it changed.</summary>
    private static bool FloatType(JsonObject type)
    {
        switch (type["data_type"] is JsonValue kind && kind.TryGetValue<string>(out var kindName) ? kindName : null)
        {
            case "float":
                var precision = type["precision"] is JsonValue p && p.TryGetValue<int>(out var bits) ? bits : (int?)null;
                if (precision is < 1 or > 53) return false;
                if (precision > 24) { type.Clear(); type["data_type"] = "double"; type["precision"] = null; type["scale"] = null; return true; }
                if (precision == null && type["real_spelling"] is JsonValue spelled && spelled.TryGetValue<bool>(out var isReal) && isReal) return false;
                type["precision"] = null; type["scale"] = null; type["real_spelling"] = true;
                return true;
            case "custom" when type["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name) && (string.Equals(name, "float4", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "float8", StringComparison.OrdinalIgnoreCase)):
                var wide = name.EndsWith('8');
                type.Clear();
                type["data_type"] = wide ? "double" : "float"; type["precision"] = null; type["scale"] = null;
                if (!wide) type["real_spelling"] = true;
                return true;
            default:
                return false;
        }
    }

    // ---- round(double, n) -----------------------------------------------------------------------------------------------------------------------------------
    // DuckDB rounds the scaled double half away from zero: round(2.675, 2) = 2.68 and round(0.285, 2) = 0.28. SQL Server rounds the decimal text (2.67), and PostgreSQL has no
    // round(double precision, integer) at all (and rounds a lone double half to even). Scaling, rounding half away from zero and unscaling is what DuckDB does.

    private static bool IsRoundOfDouble(JsonObject f, out JsonNode arg, out int digits)
    {
        arg = null!; digits = 0;
        if (!string.Equals(f["name"]?.GetValue<string>(), "round", StringComparison.OrdinalIgnoreCase) || f["args"] is not JsonArray args || args.Count is < 1 or > 2) return false;
        if (args[0] is not JsonObject { Count: 1 } c || c["cast"] is not JsonObject cast || cast["to"]?["data_type"]?.GetValue<string>() != "double" || cast["this"] is not { } inner) return false;
        if (args.Count == 2)
        {
            var negative = args[1] is JsonObject { Count: 1 } n && n["neg"] is JsonObject neg;
            var literal = negative ? ((JsonObject)args[1]!)["neg"]!["this"] : args[1];
            if (literal?["literal"] is not JsonObject lit || lit["literal_type"]?.GetValue<string>() != "number" || !int.TryParse(lit["value"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out digits)) return false;
            if (negative) digits = -digits;
            if (Math.Abs(digits) > 15) return false;
        }
        arg = inner["cast"]?["to"]?["data_type"]?.GetValue<string>() == "float" ? args[0]! : inner;      // the lowerer writes a 32-bit operand as CAST(CAST(x AS FLOAT) AS DOUBLE): the widening stays, because REAL * 100.0 is a REAL in T-SQL and 1.005 (as a REAL) times 100 is 100.5 there, 100.4999995 in a double
        return true;
    }

    private static JsonNode RoundTemplate(JsonNode x, int digits)
    {
        var p = Math.Pow(10, Math.Abs(digits)).ToString("R", CultureInfo.InvariantCulture) + (Math.Abs(digits) < 15 ? ".0" : "");
        var sql = digits switch
        {
            0 => "sign(__x) * floor(abs(__x) + 0.5)",
            > 0 => $"(sign(__x * {p}) * floor(abs(__x * {p}) + 0.5)) / {p}",
            _ => $"(sign(__x / {p}) * floor(abs(__x / {p}) + 0.5)) * {p}",
        };
        return Template(sql, ("__x", x));
    }

    // ---- TRY_CAST of a string ------------------------------------------------------------------------------------------------------------------------------
    // DuckDB: '' gives NULL, surrounding spaces are ignored. SQL Server: TRY_CAST('' AS INT) is 0 and TRY_CAST('' AS DATE) is 1900-01-01. PostgreSQL has no TRY_CAST and raises on bad data.
    // Where DuckDB accepts a number the engines refuse ('12.7' as INTEGER is 13, '1e3' as INTEGER is 1000) the result stays NULL on the engines; the matrix row says so.

    private static readonly Dictionary<string, (long Lo, long Hi)> IntegerRanges = new()
    {
        ["tiny_int"] = (sbyte.MinValue, sbyte.MaxValue), ["small_int"] = (short.MinValue, short.MaxValue), ["int"] = (int.MinValue, int.MaxValue), ["big_int"] = (long.MinValue, long.MaxValue),
    };

    private const string IntegerPattern = @"^\s*[+-]?[0-9]+\s*$";
    private const string NumberPattern = @"^\s*[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?\s*$";

    private static JsonNode? TryCastTemplate(JsonObject tc, string target)
    {
        // only the marked form: TRY_CAST(CAST(s AS VARCHAR) AS <number or date>)
        if (tc["this"] is not JsonObject { Count: 1 } mark || mark["cast"] is not JsonObject mc || mc["to"]?["data_type"]?.GetValue<string>() != "var_char" || mc["this"] is not { } x) return null;
        if (tc["to"] is not JsonObject to || to["data_type"]?.GetValue<string>() is not { } type) return null;
        var numeric = IntegerRanges.ContainsKey(type) || type is "decimal" or "double" or "float";
        var temporal = type is "date" or "timestamp";
        if (!numeric && !temporal) return null;

        if (target is "sqlserver" or "fabric")
        {
            var blank = Template("nullif(trim(__x), '')", ("__x", x));
            var copy = (JsonObject)JsonNode.Parse(tc.ToJsonString())!;
            copy["this"] = blank;
            return new JsonObject { ["try_cast"] = copy };
        }
        if (target != "postgres" || temporal) return null;     // PostgreSQL cannot test a date without raising

        var cast = new JsonObject { ["cast"] = new JsonObject { ["this"] = x.DeepClone(), ["to"] = to.DeepClone(), ["double_colon_syntax"] = false } };
        if (IntegerRanges.TryGetValue(type, out var range))
            return Template($"CASE WHEN regexp_matches(__x, '{IntegerPattern}') THEN CASE WHEN CAST(__x AS DECIMAL(30, 0)) BETWEEN {range.Lo} AND {range.Hi} THEN __cast END END", ("__x", x), ("__cast", cast));
        if (type == "decimal")
        {
            var (p, s) = (to["precision"]?.GetValue<int>() ?? 18, to["scale"]?.GetValue<int>() ?? 3);
            var bound = "1" + new string('0', Math.Max(p - s, 0));
            if (s > 37) return null;
            return Template($"CASE WHEN regexp_matches(__x, '{NumberPattern}') THEN CASE WHEN abs(CAST(__x AS DECIMAL(38, {s}))) < {bound} THEN __cast END END", ("__x", x), ("__cast", cast));
        }
        return Template($"CASE WHEN regexp_matches(__x, '{NumberPattern}') THEN __cast END", ("__x", x), ("__cast", cast));
    }

    // ---- VALUES lists on SQL Server -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// `(VALUES ...) AS t(a, b)` of more than <see cref="ValuesRowLimit"/> rows becomes `(SELECT a, b FROM (VALUES first 1000) AS g(a, b) UNION ALL SELECT a, b FROM (VALUES next 1000) AS g(a, b) ...) AS t(a, b)`.
    /// The lowerer names the columns and casts every cell to the column's type, so the branches agree on their types. A list without column names (nothing the lowerer writes) is left for the engine to refuse.
    /// </summary>
    private static bool ValuesInChunksOf(JsonObject subquery)
    {
        if (subquery["this"] is not JsonObject { Count: 1 } inner || inner["values"] is not JsonObject values || values["expressions"] is not JsonArray rows || rows.Count <= ValuesRowLimit) return false;
        if (ColumnList(subquery) is not { } list) return false;
        var parsed = Polyglot.Parse($"SELECT {list} FROM (VALUES (1)) AS ddb_chunk({list})", Dialects.Canonical);
        if (!parsed.Ok) throw new InvalidOperationException($"The VALUES chunk template does not parse: {parsed.Error}. This is a tool bug.");
        var template = JsonNode.Parse(parsed.Data!)![0]!["select"]!;
        JsonNode? chain = null;
        foreach (var chunk in rows.Chunk(ValuesRowLimit))
        {
            var select = template.DeepClone();
            var chunkValues = select["from"]!["expressions"]![0]!["subquery"]!["this"]!["values"]!;
            chunkValues["expressions"] = new JsonArray(chunk.Select(r => r!.DeepClone()).ToArray());
            var branch = new JsonObject { ["select"] = select };
            chain = chain is null ? branch : new JsonObject
            {
                ["union"] = new JsonObject
                {
                    ["left"] = chain, ["right"] = branch, ["all"] = true, ["distinct"] = false, ["with"] = null, ["order_by"] = null, ["limit"] = null, ["offset"] = null,
                    ["by_name"] = false, ["corresponding"] = false, ["strict"] = false,
                },
            };
        }
        subquery["this"] = chain;
        return true;
    }

    /// <summary>The column names of `(VALUES ...) AS t(a, b)` as a list of identifiers, or null when the list has none.</summary>
    private static string? ColumnList(JsonObject subquery)
    {
        if (subquery["column_aliases"] is not JsonArray { Count: > 0 } aliases) return null;
        var names = aliases.Select(a => a?["name"]?.GetValue<string>()).ToList();
        return names.Any(n => n is null) ? null : string.Join(", ", names.Select(n => System.Text.RegularExpressions.Regex.IsMatch(n!, "^[a-z_][a-z0-9_]*$") ? n! : "\"" + n!.Replace("\"", "\"\"") + "\""));
    }

    // ---- the transpile, with the cells of a VALUES list ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The transpile of the rules' output. polyglot rewrites a function, an operator or an interval for the engine wherever an expression stands except in the cells of a VALUES list that is a table in FROM,
    /// where it only changes the types (measured on the pinned polyglot, entry 180: `d + INTERVAL 1 MONTH` stays an interval, `ceil`, `ln`, `starts_with`, `||`, `date_diff('day', ...)`, `current_date`
    /// and `regexp_matches` reach SQL Server or PostgreSQL as DuckDB wrote them). A cell that is more than a constant is therefore replaced by a placeholder, the cells are transpiled together as the select
    /// list of one query (which is where polyglot does its work), and their text goes back where the placeholders are. Everything else is the transpile as it was: a query with no such cell is passed through unchanged.
    /// </summary>
    public static (PolyglotOutcome Outcome, string? Sql) Transpile(string ruledSql, string from, string to)
    {
        // (a statement with no VALUES list has nothing to do here, and the parse is the costly part of it)
        if (!ruledSql.Contains("VALUES", StringComparison.OrdinalIgnoreCase)) return Polyglot.TranspileOne(ruledSql, from, to);
        var parsed = Polyglot.Parse(ruledSql, from);
        if (!parsed.Ok || parsed.Data is null) return Polyglot.TranspileOne(ruledSql, from, to);
        var tree = ParseTree(parsed.Data);
        var cells = new List<JsonNode>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!CellsToPlaceholders(tree, cells, seen)) return Polyglot.TranspileOne(ruledSql, from, to);
        var main = Polyglot.Generate(tree.ToJsonString(), from);
        if (!main.Ok) throw new InvalidOperationException($"The VALUES placeholders produced an AST polyglot cannot print: {main.Error}. This is a tool bug.");
        // one query whose select list is the cells, each under an alias that marks where it ends
        var batch = Polyglot.Parse("SELECT " + string.Join(", ", cells.Select((_, i) => $"1 AS ddb_c{i}")), from);
        if (!batch.Ok) throw new InvalidOperationException($"The VALUES cell template does not parse: {batch.Error}. This is a tool bug.");
        var batchTree = ParseTree(batch.Data!);
        var items = batchTree[0]!["select"]!["expressions"]!.AsArray();
        for (var i = 0; i < cells.Count; i++) items[i]!["alias"]!["this"] = cells[i];
        var batchText = Polyglot.Generate(batchTree.ToJsonString(), from);
        if (!batchText.Ok) throw new InvalidOperationException($"The VALUES cells produced an AST polyglot cannot print: {batchText.Error}. This is a tool bug.");
        var (mainOutcome, mainSql) = Polyglot.TranspileOne(Unwrap(main.Data!), from, to);
        if (mainSql is null) return (mainOutcome, null);
        var (cellOutcome, cellSql) = Polyglot.TranspileOne(Unwrap(batchText.Data!), from, to);
        if (cellSql is null) return (cellOutcome, null);
        // the select list: `SELECT e0 AS ddb_c0, e1 AS ddb_c1, ...`; an expression ends where its alias begins
        var pieces = new List<string>();
        var start = "SELECT ".Length;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(cellSql, @"\s+AS\s+ddb_c(\d+)\s*(?:,\s*|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) != pieces.Count) throw new InvalidOperationException($"The transpiled VALUES cells are out of order: {cellSql}. This is a tool bug.");
            pieces.Add(cellSql[start..m.Index]);
            start = m.Index + m.Length;
        }
        if (pieces.Count != cells.Count || !cellSql.StartsWith("SELECT ", StringComparison.Ordinal)) throw new InvalidOperationException($"The transpiled VALUES cells could not be split ({pieces.Count} of {cells.Count}): {cellSql}. This is a tool bug.");
        var text = System.Text.RegularExpressions.Regex.Replace(mainSql, @"\bddb_cell_(\d+)\b", m => pieces[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return (mainOutcome, text);
    }

    /// <summary>Replaces each cell of each VALUES list that is more than a constant by the column `ddb_cell_N`, N being the position of its text in <paramref name="cells"/> (equal cells share one). True when any cell was replaced.</summary>
    private static bool CellsToPlaceholders(JsonNode node, List<JsonNode> cells, Dictionary<string, int> seen)
    {
        var replaced = false;
        switch (node)
        {
            case JsonArray a:
                foreach (var item in a) if (item is not null) replaced |= CellsToPlaceholders(item, cells, seen);
                break;
            case JsonObject o:
                if (o["values"] is JsonObject { } values && values["expressions"] is JsonArray rows)
                    foreach (var row in rows)
                        if (row?["expressions"] is JsonArray rowCells)
                            for (var i = 0; i < rowCells.Count; i++)
                            {
                                if (rowCells[i] is not { } cell || IsConstantCell(cell)) continue;
                                var key = cell.ToJsonString();
                                if (!seen.TryGetValue(key, out var n)) { n = cells.Count; seen[key] = n; cells.Add(cell.DeepClone()); }
                                rowCells[i] = Template($"ddb_cell_{n}");
                                replaced = true;
                            }
                foreach (var (key, child) in o.ToList())
                    if (key != "values" && child is not null) replaced |= CellsToPlaceholders(child, cells, seen);
                break;
        }
        return replaced;
    }

    /// <summary>A literal, NULL, a negated literal, or a cast of one of those: nothing for polyglot to rewrite but the type, which it does in a cell.</summary>
    private static bool IsConstantCell(JsonNode n)
    {
        if (n is not JsonObject { Count: 1 } o) return false;
        var (kind, body) = (o.First().Key, o.First().Value);
        return kind switch
        {
            "literal" or "null" or "boolean" => true,
            "neg" or "paren" => body?["this"] is { } inner && IsConstantCell(inner),
            // (a cast to text is left to the select list unless it is of text or NULL: T-SQL's CAST(x AS VARCHAR) is 30 characters, the select list's translation is VARCHAR(MAX))
            "cast" => body?["this"] is { } castOf && IsConstantCell(castOf) &&
                (body["to"]?["data_type"]?.GetValue<string>() is not ("var_char" or "char" or "text" or "string") || castOf is JsonObject { } nullLiteral && nullLiteral.ContainsKey("null") || castOf["literal"]?["literal_type"]?.GetValue<string>() == "string"),
            _ => false,
        };
    }

    // ---- templates -------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Parses a DuckDB-dialect expression and puts clones of the given nodes where the placeholder columns are.</summary>
    private static JsonNode Template(string expression, params (string Name, JsonNode Value)[] holes)
    {
        var parsed = Polyglot.Parse("SELECT " + expression, Dialects.Canonical);
        if (!parsed.Ok) throw new InvalidOperationException($"Target rule template `{expression}` does not parse: {parsed.Error}. This is a tool bug.");
        var tree = JsonNode.Parse(parsed.Data!)!;
        var expr = tree[0]!["select"]!["expressions"]![0]!;
        expr.Parent!.AsArray().Remove(expr);
        return Fill(expr, holes);
    }

    private static JsonNode Fill(JsonNode node, (string Name, JsonNode Value)[] holes)
    {
        switch (node)
        {
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) if (a[i] is { } item && Fill(item, holes) is var r && !ReferenceEquals(r, item)) a[i] = r;
                return a;
            case JsonObject o:
                if (o.Count == 1 && o["column"] is JsonObject col && col["name"]?["name"]?.GetValue<string>() is { } n && holes.FirstOrDefault(h => h.Name == n) is { Value: not null } hole)
                    return hole.Value.DeepClone();
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (o[key] is { } item && Fill(item, holes) is var r && !ReferenceEquals(r, item)) o[key] = r;
                return o;
            default:
                return node;
        }
    }
}
