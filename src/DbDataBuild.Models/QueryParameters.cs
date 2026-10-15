using System.Globalization;
using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>
/// A parameter a model's query uses as a **value** (`WHERE region = ${project.region}`). DuckDB has to bind and lower the query offline, so each reference is first replaced by a **marker**: a literal of the
/// parameter's type that nothing else in the query is likely to be. After lowering and transpiling, the marker in the target's text becomes a placeholder (`@p_project_region`), which the plan fills with the
/// value and `apply` binds through the driver: the value is never written into the statement text, so the rendered file does not change when a value does.
/// </summary>
/// <param name="Marker">The literal DuckDB sees (`'__ddb_param_project_region__'`, `7400000000000000001`, `DATE '1000-01-02'`).</param>
public sealed record QueryParameter(string Scope, string Name, string Type, string Marker)
{
    public string Key => $"{Scope}.{Name}";

    /// <summary>The placeholder name in a rendered script (without `@`).</summary>
    public string Placeholder => $"p_{Scope}_{Name}";

    /// <summary>The scope and name of a placeholder, or null when the name is not one of these.</summary>
    public static string? KeyOf(string placeholder)
    {
        var m = Regex.Match(placeholder, "^p_([a-z]+)_([a-z][a-z0-9_]*)$");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : null;
    }
}

public static class QueryParameters
{
    public static bool Uses(string sql) => ParameterReferences.Pattern.IsMatch(sql);

    private static string MarkerFor(string type, int n, string scope, string name) => type switch
    {
        "BIGINT" => (7_400_000_000_000_000_000L + n).ToString(CultureInfo.InvariantCulture),
        "INTEGER" => (2_100_000_000 + n).ToString(CultureInfo.InvariantCulture),
        "SMALLINT" => (32_000 + n).ToString(CultureInfo.InvariantCulture),
        "DATE" => $"DATE '{DateText(n)}'",
        "TIMESTAMP" => $"TIMESTAMP '{DateText(n)} 00:00:00'",
        _ => $"'__ddb_param_{scope}_{name}__'",
    };

    private static string DateText(int n) => $"1000-{(n - 1) / 28 + 1:00}-{(n - 1) % 28 + 1:00}";

    /// <summary>
    /// The query with each reference to a **name** (a parameter of type NAME) replaced by the string it is on <paramref name="connection"/>: `snapshot_at(${connection.table}, ${connection.date_column})`
    /// becomes `snapshot_at('src.orders_snap', 'snap_date')`. This is done before anything else, so DuckDB binds the query for that name. References to values are left for <see cref="Mark"/>.
    /// </summary>
    public static string ResolveNames(string sql, BuiltModel built, ProjectConfig config, string connection)
    {
        if (!Uses(sql)) return sql;
        var values = built.ParametersFor(config, connection);
        return ParameterReferences.Substitute(sql, (scope, name) => values.TryGetValue($"{scope}.{name}", out var v) && v.Type == ParameterValue.Name ? v.NameLiteral : null);
    }

    /// <summary>True when the query refers to a name on any of the model's connections (so its text, and everything made from it, can differ between them).</summary>
    public static bool HasNames(string sql, BuiltModel built, ProjectConfig config)
    {
        foreach (var (scope, name) in ParameterReferences.In(sql))
            foreach (var c in built.Definition.Targets ?? config.DefaultConnections)
                if (built.ParametersFor(config, c).TryGetValue($"{scope}.{name}", out var v) && v.Type == ParameterValue.Name) return true;
        return false;
    }

    /// <summary>What is wrong with the names of a query: a reference that is a name on one connection of the model and not on another, or missing there. Names are not values: they are not bound, so a view may use them.</summary>
    public static IReadOnlyList<string> NameProblems(string sql, BuiltModel built, ProjectConfig config)
    {
        var problems = new List<string>();
        var connections = built.Definition.Targets ?? config.DefaultConnections;
        foreach (var (scope, name) in ParameterReferences.In(sql))
        {
            var key = $"{scope}.{name}";
            var types = connections.Select(c => built.ParametersFor(config, c).TryGetValue(key, out var v) ? v.Type : null).ToList();
            if (!types.Contains(ParameterValue.Name)) continue;
            for (var i = 0; i < connections.Count; i++)
                if (types[i] == null) problems.Add($"`${{{key}}}` is a name but has no value on the connection `{connections[i]}`");
                else if (types[i] != ParameterValue.Name) problems.Add($"`${{{key}}}` is a name on some of the model's connections and a {types[i]} on `{connections[i]}`: a parameter is one or the other");
        }
        return problems;
    }

    /// <summary>The problems with the references in a query, and the query with each reference replaced by its marker.</summary>
    public static (string Sql, IReadOnlyList<QueryParameter> Parameters, IReadOnlyList<string> Problems) Mark(string sql, BuiltModel built, ProjectConfig config)
    {
        var refs = ParameterReferences.In(sql);
        if (refs.Count == 0) return (sql, [], []);
        var problems = new List<string>();
        var def = built.Definition;
        if (def.Kind == ModelKinds.View) problems.Add("a view's query cannot use parameters: a view is DDL, and DDL binds no values (use a table, or write the value into the query)");
        var connections = def.Targets ?? config.DefaultConnections;
        var parameters = new List<QueryParameter>();
        foreach (var (scope, name) in refs.OrderBy(r => $"{r.Scope}.{r.Name}", StringComparer.Ordinal))
        {
            var key = $"{scope}.{name}";
            if (scope == "origin") { problems.Add($"`${{{key}}}` is only for a copy's slice; a query reads `project`, `connection` or `model` parameters"); continue; }
            if (scope is not ("project" or "connection" or "model")) { problems.Add($"`${{{key}}}` is not a parameter scope (`project`, `connection`, `model`)"); continue; }
            var types = new HashSet<string>();
            foreach (var c in connections)
            {
                if (built.ParametersFor(config, c).TryGetValue(key, out var v)) types.Add(v.Type);
                else problems.Add($"`${{{key}}}` has no value{(scope == "connection" ? $" on the connection `{c}` (`connections.{c}.parameters`)" : scope == "model" ? " (`parameters:` in the model's file)" : " (`parameters:` in a project file)")}");
            }
            if (types.Count > 1) problems.Add($"`${{{key}}}` has different types on the model's connections ({string.Join(", ", types.Order(StringComparer.Ordinal))})");
            parameters.Add(new QueryParameter(scope, name, types.FirstOrDefault() ?? ParameterValue.Text, ""));
        }
        parameters = parameters.Select((p, i) => p with { Marker = MarkerFor(p.Type, i + 1, p.Scope, p.Name) }).ToList();
        foreach (var p in parameters)
            if (Regex.IsMatch(sql, Regex.Escape(p.Marker.Contains('\'') ? p.Marker[(p.Marker.IndexOf('\'') + 1)..^1] : p.Marker)))
                problems.Add($"the query already contains `{p.Marker}`, which the tool uses to stand for `${{{p.Key}}}`");
        var marked = ParameterReferences.Pattern.Replace(sql, m => parameters.FirstOrDefault(p => p.Key == $"{m.Groups[1].Value}.{m.Groups[2].Value}")?.Marker ?? m.Value);
        return (marked, parameters, problems);
    }

    /// <summary>The query with each reference replaced by the **value** it has on a connection, as a literal of its type: for what DuckDB runs for real (`sample`, `test`).</summary>
    public static string WithValues(string sql, BuiltModel built, ProjectConfig config, string connection)
    {
        var values = built.ParametersFor(config, connection);
        return ParameterReferences.Substitute(sql, (scope, name) =>
        {
            if (!values.TryGetValue($"{scope}.{name}", out var v)) return null;
            return v.Type switch
            {
                "DATE" => $"DATE '{v.Value}'",
                "TIMESTAMP" => $"TIMESTAMP '{v.Value}'",
                ParameterValue.Text => "'" + v.Value.Replace("'", "''") + "'",
                ParameterValue.Name => v.NameLiteral,
                _ => long.Parse(v.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            };
        });
    }

    /// <summary>A lowered query for people: each marker back as the reference it stands for.</summary>
    public static string BackToReferences(string sql, IReadOnlyList<QueryParameter> parameters)
    {
        foreach (var p in parameters) sql = sql.Replace(p.Marker, $"${{{p.Key}}}", StringComparison.Ordinal);
        return sql;
    }

    /// <summary>Replaces each marker in a target's text with its placeholder. Returns the text, the parameters found, and those the lowering lost (folded into a constant, so no longer a value to bind).</summary>
    public static (string Sql, IReadOnlyList<QueryParameter> Used, IReadOnlyList<QueryParameter> Lost) Bind(string targetSql, IReadOnlyList<QueryParameter> parameters)
    {
        var used = new List<QueryParameter>();
        var lost = new List<QueryParameter>();
        foreach (var p in parameters)
        {
            var at = "@" + p.Placeholder;
            var inner = p.Marker.Contains('\'') ? p.Marker[(p.Marker.IndexOf('\'') + 1)..^1] : p.Marker;
            var escaped = Regex.Escape(inner);
            var pattern = p.Type switch
            {
                "BIGINT" or "INTEGER" or "SMALLINT" => $@"(?<![\w.]){escaped}(?![\w.])",
                "DATE" or "TIMESTAMP" => $@"CAST\('{escaped}' AS \w+(\(\d+(, ?\d+)?\))?\)|(?:DATE |TIMESTAMP )?N?'{escaped}'(::\w+)?",
                _ => $@"N?'{escaped}'",
            };
            var replaced = Regex.Replace(targetSql, pattern, at);
            if (replaced == targetSql) lost.Add(p); else { used.Add(p); targetSql = replaced; }
        }
        return (targetSql, used, lost);
    }
}
