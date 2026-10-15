using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>A native model's text, ready to be spliced into the text of a query that reads it: its parameter references already placeholders (`@p_native_...`).</summary>
public sealed record NativeUse(string Name, string Text, IReadOnlyList<QueryParameter> Parameters, string Access = NativeQuery.Select);

/// <summary>
/// Inlining a native `select` (design: docs/research/native-queries.md). DuckDB binds a reading query against the native model's declared columns, as for any table; the native text is spliced in **after**
/// transpiling, by name, as a derived table, so it is never lowered or transpiled and the reading query's own text and hash do not change. Its parameters become placeholders of their own
/// (scope `native`, named after the model and the reference), so they cannot clash with the reader's.
/// </summary>
public static class NativeInline
{
    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "ON", "GROUP", "ORDER", "HAVING", "UNION", "INTERSECT", "EXCEPT", "LIMIT", "OFFSET", "WINDOW", "FETCH", "FOR",
        "USING", "NATURAL", "LATERAL", "TABLESAMPLE", "OPTION", "SELECT", "WITH", "PIVOT", "UNPIVOT", "QUALIFY", "SET", "VALUES",
    };

    private static string Id(string modelName) => Regex.Replace(modelName.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    /// <summary>The native model's text with each `${scope.name}` replaced by a placeholder of its own, and those placeholders as parameters (typed by the value the native model sees).</summary>
    public static NativeUse Prepare(DeclaredModel native, ProjectConfig config)
    {
        var q = native.Native!;
        var connection = (native.Connections ?? config.DefaultConnections)[0];
        var values = q.ParametersFor(config, connection);
        var parameters = new List<QueryParameter>();
        var text = ParameterReferences.Pattern.Replace(q.Text, m =>
        {
            var key = $"{m.Groups[1].Value}.{m.Groups[2].Value}";
            if (!values.TryGetValue(key, out var v)) return m.Value;                     // reported when the project is checked
            var p = new QueryParameter("native", $"{Id(native.Name)}__{m.Groups[1].Value}_{m.Groups[2].Value}", v.Type, "");
            if (!parameters.Any(x => x.Key == p.Key)) parameters.Add(p);
            return "@" + p.Placeholder;
        });
        return new NativeUse(native.Name, text, parameters, q.Access);
    }

    /// <summary>The value each placeholder of <paramref name="use"/> takes on <paramref name="connection"/>, keyed `native.<name>` like the plan's lookup.</summary>
    public static IReadOnlyDictionary<string, ParameterValue> Values(DeclaredModel native, NativeUse use, ProjectConfig config, string connection)
    {
        var values = native.Native!.ParametersFor(config, connection);
        var result = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var p in use.Parameters)
        {
            var source = p.Name[(Id(native.Name).Length + 2)..];                         // `<scope>_<name>`
            var scope = source[..source.IndexOf('_')];
            if (values.TryGetValue($"{scope}.{source[(scope.Length + 1)..]}", out var v)) result[p.Key] = v;
        }
        return result;
    }

    /// <summary>
    /// Replaces each reference to a native model in <paramref name="sql"/> (the target's text) with its text as a derived table. A reference that already has an alias keeps it; otherwise the table's own name
    /// is the alias, so `open_orders.col` still resolves. Returns the placeholders the spliced text introduced.
    /// </summary>
    public static (string Sql, IReadOnlyList<QueryParameter> Parameters) Splice(string sql, IReadOnlyList<NativeUse> uses, string engine)
    {
        var added = new List<QueryParameter>();
        foreach (var use in uses)
        {
            var dot = use.Name.IndexOf('.');
            var (schema, table) = dot < 0 ? ("", use.Name) : (use.Name[..dot], use.Name[(dot + 1)..]);
            string Ident(string s) => $@"(?:\[{Regex.Escape(s)}\]|""{Regex.Escape(s)}""|{Regex.Escape(s)})";
            var name = schema == "" ? Ident(table) : $@"{Ident(schema)}\s*\.\s*{Ident(table)}";
            var pattern = new Regex($@"(?<![\w\]""\.]){name}(?![\w\]""])(?<tail>\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*|\[[^\]]+\]|""[^""]+""))?", RegexOptions.IgnoreCase);
            var quoted = engine == "postgres" ? $"\"{table}\"" : $"[{table}]";
            var hit = false;
            sql = pattern.Replace(sql, m =>
            {
                hit = true;
                var alias = m.Groups["alias"];
                if (alias.Success && !NotAnAlias.Contains(alias.Value)) return $"({use.Text}){m.Groups["tail"].Value}";
                return $"({use.Text}) AS {quoted}{(alias.Success ? m.Groups["tail"].Value : "")}";
            });
            if (hit) foreach (var p in use.Parameters) if (!added.Any(x => x.Key == p.Key)) added.Add(p);
        }
        return (sql, added);
    }
}
