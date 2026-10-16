using System.Text;

namespace DbDataBuild.Tests.Conformance.FunctionMatrix;

/// <summary>
/// One query that exercises an overload: `Build(null)` is the whole set (every sample row at once); `Build(ids)` is the same query for one combination of sample rows
/// (alias to row id), which the run uses to find the row an error or a difference comes from. A variant that cannot be built says why in <see cref="NotProbed"/>.
/// </summary>
public sealed record Variant(string Label, Func<IReadOnlyDictionary<char, int>?, string> Build, IReadOnlyList<char> Aliases, bool CanIsolate, string? NotProbed = null);

/// <summary>
/// Builds the minimal query for each overload from its parameter types. Arguments come from the probe table (id, i, j, d, n, s, dt, ts: negatives, zero, NULL, an empty string, accented and
/// non-BMP text, a leap day, month ends, the largest INT), cast to the parameter's type, so the tool lowers a column reference as a model's query would; a second column argument comes from a
/// second alias of the same table (a cross join), so every pair of sample rows is tried. Arguments that are not data (a date part, a pattern, a format, a JSON path) are literals that make
/// sense for the function, and where several are valid (every date part) each one is its own variant.
/// </summary>
public static class ProbeBuilder
{
    private sealed class Counter { public int Value; }

    private static readonly string[] DatePartSpecifiers = ["year", "quarter", "month", "week", "day", "dayofweek", "isodow", "dayofyear", "hour", "minute", "second", "millisecond", "microsecond", "epoch", "decade", "century", "millennium", "isoyear", "yearweek", "era", "julian"];
    private static readonly string[] DateTruncSpecifiers = ["millennium", "century", "decade", "year", "quarter", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond"];
    private static readonly string[] DateDiffSpecifiers = ["millennium", "century", "decade", "year", "quarter", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond"];
    private static readonly string[] StrftimeFormats = ["%Y-%m-%d", "%H:%M:%S", "%A %B %d %Y %j", "%y-%b-%e %p %I", "%G-W%V-%u"];
    private const string JsonDocument = "'{\"k\": \"v\", \"n\": 5, \"a\": [1, 2]}'";

    private static readonly HashSet<string> JsonFunctions = new(StringComparer.Ordinal)
    {
        "->>", "from_json", "from_json_strict", "json_array_length", "json_contains", "json_exists", "json_extract", "json_extract_path", "json_extract_path_text", "json_extract_string",
        "json_keys", "json_pretty", "json_structure", "json_transform", "json_transform_strict", "json_type", "json_valid", "json_value", "json_merge_patch",
    };

    private static bool IsGeneric(string type) => type is "ANY" or "T" or "K" or "V" or "ANY[]" or "T[]" or "T[][]" or "K[]" or "V[]" or "MAP(K, V)" or "STRUCT(K, V)[]";

    /// <summary>The variants of an overload (several when the overload takes a generic value or a part name that has many valid spellings).</summary>
    public static IReadOnlyList<Variant> Variants(FunctionOverload f, Func<string, bool> duckAccepts)
    {
        var fixedTypes = f.Types.ToList();
        var generic = fixedTypes.Any(IsGeneric) || f.Varargs == "ANY";
        var kinds = generic ? new[] { "INTEGER", "VARCHAR" } : [""];
        var result = new List<Variant>();
        foreach (var kind in kinds)
        {
            // positions of the fixed parameters, then the extra arguments of a variadic function
            var types = fixedTypes.ToList();
            var names = f.ParameterNames.ToList();
            if (f.Varargs != null)
            {
                var extra = f.Name is "least" or "greatest" ? 2 : 2;
                var varType = f.Varargs == "ANY" ? "ANY" : f.Varargs;
                for (var k = 0; k < extra; k++) { types.Add(varType); names.Add("vararg" + k); }
            }
            while (names.Count < types.Count) names.Add("col" + names.Count);

            // each parameter yields its candidate expressions; the product (at most 40) is the variant list
            var options = new List<IReadOnlyList<(string Sql, string? Label, char? Alias)>>();
            var columnIndex = new Counter();
            string? notProbed = null;
            for (var pos = 0; pos < types.Count; pos++)
            {
                var o = Argument(f, pos, types[pos], names[pos], kind, columnIndex, out var why);
                if (o == null) { notProbed = why; break; }
                options.Add(o);
            }
            var label0 = kind.Length > 0 ? $"values as {kind}" : "";
            if (notProbed != null)
            {
                result.Add(new Variant(label0, _ => "", [], false, notProbed));
                continue;
            }
            foreach (var combo in Product(options).Take(40))
            {
                var label = string.Join(", ", new[] { label0 }.Concat(combo.Select(c => c.Label ?? "")).Where(l => l.Length > 0));
                var args = combo.Select(c => c.Sql).ToList();
                var aliases = combo.Where(c => c.Alias != null).Select(c => c.Alias!.Value).Distinct().ToList();
                result.Add(Make(f, label, args, aliases, duckAccepts));
            }
        }
        return result;
    }

    private static IEnumerable<List<(string Sql, string? Label, char? Alias)>> Product(List<IReadOnlyList<(string Sql, string? Label, char? Alias)>> options)
    {
        IEnumerable<List<(string, string?, char?)>> acc = [[]];
        foreach (var o in options) acc = acc.SelectMany(prefix => o.Select(x => prefix.Append(x).ToList()));
        return acc;
    }

    // ---- the query around the call ----

    public static Variant Make(FunctionOverload f, string label, List<string> args, List<char> aliasesUsed, Func<string, bool> duckAccepts)
    {
        var call = CallText(f, args, duckAccepts);
        var isAggregate = f.Kind is "aggregate" or "window";
        var aliases = aliasesUsed.OrderBy(c => c).ToList();
        if (isAggregate && aliases.Count == 0) aliases = ['a'];
        var canIsolate = f.Kind is not ("aggregate" or "window");
        return new Variant(label, ids => Assemble(f, call, aliases, ids), aliases, canIsolate);
    }

    private static string CallText(FunctionOverload f, List<string> args, Func<string, bool> duckAccepts)
    {
        var name = f.Name;
        if (f.Kind == "operator")
        {
            if (name == "!__postfix") return $"({args[0]} !)";
            return args.Count == 1 ? $"({name}{args[0]})" : $"({args[0]} {name} {args[1]})";
        }
        if (name == "count_star") return "count(*)";
        if (name == "position" && args.Count == 2) return $"position({args[1]} IN {args[0]})";
        var plain = $"{name}({string.Join(", ", args)})";
        if (args.Count == 0 && f.Kind is "scalar" or "macro" && !duckAccepts($"SELECT {plain} AS v")) return name;     // current_date and friends are keywords, not calls
        return plain;
    }

    private static string Assemble(FunctionOverload f, string call, IReadOnlyList<char> aliases, IReadOnlyDictionary<char, int>? only)
    {
        var sb = new StringBuilder("SELECT ");
        var isAggregate = f.Kind == "aggregate";
        if (!isAggregate) foreach (var a in aliases) sb.Append($"{a}.id AS {a}_id, ");
        sb.Append(f.Kind == "window" ? $"{call} OVER (ORDER BY a.id) AS v" : $"{call} AS v");
        if (aliases.Count > 0)
        {
            sb.Append(" FROM probe ").Append(aliases[0]);
            foreach (var a in aliases.Skip(1)) sb.Append($" CROSS JOIN probe {a}");
            var where = new List<string>();
            if (!isAggregate && only != null) where.AddRange(aliases.Where(only.ContainsKey).Select(a => $"{a}.id = {only[a]}"));
            if (aliases.Count == 3 && only == null) where.Add("c.id = b.id");            // three column arguments: 64 combinations, not 512
            if (where.Count > 0) sb.Append(" WHERE ").Append(string.Join(" AND ", where));
        }
        return sb.ToString();
    }

    // ---- arguments ----

    private static IReadOnlyList<(string Sql, string? Label, char? Alias)>? Argument(FunctionOverload f, int pos, string type, string pname, string kind, Counter columnIndex, out string? why)
    {
        why = null;
        var name = f.Name;
        (string, string?, char?) Lit(string sql, string? label = null) => (sql, label, null);
        IReadOnlyList<(string, string?, char?)> One(string sql, string? label = null) => [Lit(sql, label)];

        (string, string?, char?) ColArg(string expr)
        {
            var alias = "abc"[columnIndex.Value % 3];
            columnIndex.Value++;
            return (expr.Replace("{x}", alias.ToString()), null, alias);
        }
        IReadOnlyList<(string, string?, char?)> ColOne(string expr) => [ColArg(expr)];

        var isAggregate = f.Kind is "aggregate" or "window";
        var first = columnIndex.Value == 0;

        // ---- literals decided by the function and the parameter's name ----
        if (f.Kind == "window")
        {
            switch (name)
            {
                case "ntile": return One("3");
                case "lag" or "lead" when pos == 1: return One("1");
                case "lag" or "lead" when pos == 2: return One(kind == "VARCHAR" ? "'z'" : "-1");
                case "nth_value" when pos == 1: return One("2");
            }
        }
        if (isAggregate && pos > 0)
        {
            switch (pname)
            {
                case "pos" or "quantile": return One("0.5");
                case "k" or "N" or "col2" when type is "BIGINT" or "INTEGER": return One("2");
                case "sample_size": return One("10");
                case "col1" when name == "bitstring_agg": return One("0");
                case "col2" when name == "bitstring_agg": return One("10");
            }
            if (type == "VARCHAR" && pname == "arg") return One("','");
        }

        if (type == "VARCHAR" || type == "JSON")
        {
            var json = JsonFunctions.Contains(name);
            if (json && pos == 0) return One(type == "JSON" ? $"CAST({JsonDocument} AS JSON)" : JsonDocument);
            if (json && pos >= 1 && type == "JSON") return One("CAST('\"v\"' AS JSON)");
            if (json && pos >= 1 && type == "VARCHAR") return One(name == "json_valid" ? JsonDocument : name is "from_json" or "from_json_strict" or "json_transform" or "json_transform_strict" ? "'{\"k\": \"VARCHAR\"}'" : name == "json_contains" ? "'\"v\"'" : "'$.k'");
            if (type == "VARCHAR")
            {
                switch (name)
                {
                    case "date_part" or "datepart" when pos == 0: return DatePartSpecifiers.Select(p => Lit($"'{p}'", $"part '{p}'")).ToList();
                    case "date_trunc" or "datetrunc" or "trunc" when pos == 0 && type == "VARCHAR" && pname == "part": return DateTruncSpecifiers.Select(p => Lit($"'{p}'", $"part '{p}'")).ToList();
                    case "date_diff" or "datediff" or "date_sub" or "datesub" when pos == 0: return DateDiffSpecifiers.Select(p => Lit($"'{p}'", $"part '{p}'")).ToList();
                    case "timezone" when pos == 0: return One("'Asia/Tokyo'");
                    case "strftime" when pos == (f.Types[0] == "VARCHAR" && f.Types[1] != "VARCHAR" ? 0 : 1): return StrftimeFormats.Select(p => Lit($"'{p}'", $"format '{p}'")).ToList();
                    case "strptime" or "try_strptime" when pos == 0: return One("'2024-02-29 10:30:45'");
                    case "strptime" or "try_strptime" when pos == 1: return One("'%Y-%m-%d %H:%M:%S'");
                    case "printf": return One("'%s|%s'");
                    case "format": return One("'{}|{}'");
                    case "concat_ws" when pos == 0: return One("','");
                    case "parse_path" or "parse_dirname" or "parse_dirpath" or "parse_filename" when pos == 0: return One("'/a/b/c.txt'");
                    case "parse_path" or "parse_dirname" or "parse_dirpath" or "parse_filename" when pos == 1: return One("'/'");
                    case "current_setting": return One("'TimeZone'");
                    case "getenv": return One("'HOME'");
                    case "in_search_path": return One("'memory'");
                    case "error": return One("'boom'");
                    case "union_extract": return One("'a'");
                    case "struct_extract" or "array_extract" when pname == "entry": return One("'a'");
                    case "make_type": return One("'INTEGER'");
                    case "translate" when pos == 1: return One("'abc'");
                    case "translate" when pos == 2: return One("'xyz'");
                    case "like_escape" or "not_like_escape" or "ilike_escape" or "not_ilike_escape" when pos == 1: return One("'a!%'");
                    case "like_escape" or "not_like_escape" or "ilike_escape" or "not_ilike_escape" when pos == 2: return One("'!'");
                    case "time_bucket" when pname == "origin": return One("'2024-01-01'");
                    case "regexp_replace" when pos == 3 || pname == "options": return [Lit("'g'", "options 'g'"), Lit("'i'", "options 'i'")];
                    case "list_aggr" or "list_aggregate" or "array_aggr" or "array_aggregate" or "aggregate" when pname == "function_name": return One("'sum'");
                }
                switch (pname)
                {
                    case "regex": return One("'b.'");
                    case "options": return One("'i'");
                    case "replacement": return One("'x'");
                    case "separator": return One("','");
                    case "search_string": return One("'b'");
                    case "characters": return One("' '");
                    case "character": return One("'*'");
                    case "target": return One("'a'");
                    case "source" when name == "replace": return One("'a'");
                    case "format": return One("'%Y-%m-%d'");
                    case "text": return One("'2024-02-29'");
                }
                        // a plain string argument: a column; the second and later ones from another alias so every pair is tried
                return ColOne("{x}.s");
            }
        }

        // ---- values by type ----
        var integer = pos == 0 && first ? "{x}.i" : "{x}.j";
        switch (type)
        {
            case "INTEGER": return name == "regexp_extract" && pos == 2 ? One("1") : ColOne(integer);
            case "BIGINT": return ColOne($"CAST({integer} AS BIGINT)");
            case "HUGEINT": return ColOne($"CAST({integer} AS HUGEINT)");
            case "SMALLINT": return ColOne("CAST({x}.j AS SMALLINT)");
            case "TINYINT": return ColOne("CAST({x}.j AS TINYINT)");
            case "UTINYINT" or "USMALLINT" or "UINTEGER" or "UBIGINT" or "UHUGEINT": return ColOne($"CAST({{x}}.id AS {type})");
            case "DOUBLE": return ColOne("{x}.d");
            case "FLOAT": return ColOne("CAST({x}.d AS FLOAT)");
            case "DECIMAL": return ColOne("{x}.n");
            case "BOOLEAN": return name == "parse_filename" ? One("false") : ColOne("({x}.i > 1)");
            case "DATE": return ColOne("{x}.dt");
            case "TIMESTAMP": return ColOne("{x}.ts");
            case "TIMESTAMP_S": return ColOne("CAST({x}.ts AS TIMESTAMP_S)");
            case "TIMESTAMP_MS": return ColOne("CAST({x}.ts AS TIMESTAMP_MS)");
            case "TIMESTAMP_NS": return ColOne("CAST({x}.ts AS TIMESTAMP_NS)");
            case "TIMESTAMP WITH TIME ZONE": return ColOne("CAST({x}.ts AS TIMESTAMPTZ)");
            case "TIME": return ColOne("CAST({x}.ts AS TIME)");
            case "TIME_NS": return ColOne("CAST({x}.ts AS TIME_NS)");
            case "TIME WITH TIME ZONE": return One("CAST('10:30:45+02' AS TIME WITH TIME ZONE)");
            case "INTERVAL": return One(name == "time_bucket" ? "INTERVAL '1 month'" : "INTERVAL '1 month 2 days 03:04:05'");
            case "BLOB": return ColOne("CAST(CAST({x}.id AS VARCHAR) AS BLOB)");       // ASCII digits: DuckDB refuses a non-ASCII string as a BLOB
            case "BIT":
                why = "not probed: BIT (bit string) argument; SQL Server's bit is a boolean and PostgreSQL's bit string is another type, so the tool has no mapping for it";
                return null;
            case "UUID": return One("CAST('6bd6cc7d-2b35-4a3d-a5f1-5d0c5a2f2b11' AS UUID)");
            case "BIGNUM": return ColOne($"CAST({integer} AS BIGNUM)");
            case "NULL": return One("NULL");
            case "LAMBDA": return One(name is "reduce" or "array_reduce" or "list_reduce" ? "(x, y) -> x + y" : name.Contains("filter") || name.Contains("where") ? "x -> x > 1" : "x -> x + 1");
            case "STRUCT": return One("{'a': 1, 'b': 'x'}");
            case "STRUCT(\"year\" BIGINT, \"month\" BIGINT, \"day\" BIGINT)": return One("{'year': 2024, 'month': 2, 'day': 29}");
            case "ANY" or "T" or "K" or "V":
                return kind == "VARCHAR" ? ColOne("{x}.s") : ColOne(integer);
            case "ANY[]" or "T[]" or "K[]" or "V[]": return One(kind == "VARCHAR" ? "['a', 'b', 'c']" : "[1, 2, 3]");
            case "T[][]": return One(kind == "VARCHAR" ? "[['a'], ['b']]" : "[[1], [2]]");
            case "MAP(K, V)": return One(kind == "VARCHAR" ? "MAP {'a': 'x'}" : "MAP {'a': 1}");
            case "STRUCT(K, V)[]": return One(kind == "VARCHAR" ? "[{'key': 'a', 'value': 'x'}]" : "[{'key': 'a', 'value': 1}]");
            case "UNION" or "GEOMETRY" or "VARIANT" or "AGGREGATE_STATE<?>":
                why = $"not probed: {type} argument; no way to build such a value in a probe query";
                return null;
        }
        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            var elem = type[..^2];
            var literal = elem switch { "VARCHAR" => "['a', 'b']", "BOOLEAN" => "[true, false]", "DOUBLE" or "FLOAT" or "DECIMAL" => "[1.5, 2.5]", _ => "[1, 2, 3]" };
            return One(elem is "VARCHAR" or "BOOLEAN" ? literal : $"CAST({literal} AS {elem}[])");
        }
        if (type.EndsWith(']') && type.Contains('['))
        {
            var elem = type[..type.IndexOf('[')];
            return One($"CAST([1.5, 2.5, 3.5] AS {elem}[3])");
        }
        why = $"not probed: the generator has no sample value for the parameter type {type}";
        return null;
    }
}
