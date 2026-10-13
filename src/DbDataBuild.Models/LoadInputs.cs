using System.Globalization;
using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>The kinds of resolver an input may have (docs/research/refresh-plans.md, section 8): where the value is computed.</summary>
public static class InputResolverKinds
{
    /// <summary>A DuckDB expression, evaluated in the tool in a sandboxed in-memory database. Reads nothing from any connection.</summary>
    public const string Expression = "expression";

    /// <summary>A SELECT over the project's models, lowered for the load's engine and run on the load's connection (read login).</summary>
    public const string Target = "target";

    /// <summary>The same for a copy, run at the copy's origin in the origin's engine (the origin's read login).</summary>
    public const string Source = "source";

    public static readonly IReadOnlyList<string> All = [Expression, Target, Source];
}

/// <summary>What an input does when its resolver finds NULL: `fail` (the default: a person gives the value), `initial` (the input's `default`) or `skip_load` (the load does not run, which is not a failure).</summary>
public static class InputOnNull
{
    public const string Fail = "fail";
    public const string Initial = "initial";
    public const string SkipLoad = "skip_load";
    public static readonly IReadOnlyList<string> All = [Fail, Initial, SkipLoad];
}

/// <param name="Text">An expression: the DuckDB text as written. A target or source resolver: the SELECT in DuckDB dialect as written (the compiled load plan holds it lowered).</param>
public sealed record InputResolverSpec(string Kind, string Text);

/// <summary>
/// One entry of a load's `inputs:`. An input with a <see cref="Type"/> is a value the load needs; one with <see cref="Provides"/> is a resolver that computes several inputs at once (one row, named columns). The name of an
/// input that the load's script binds (`start` and `end` of a range load, a query parameter) is that parameter; any other name is a value only resolvers and memberships use (`days`).
/// </summary>
/// <param name="Type">Null for a resolver that provides others, and for `start` and `end` of a range load (the range column's type).</param>
/// <param name="Default">The value when there is no resolver; with a resolver, the value `on_null: initial` falls back to. May be a `${project.x}` or `${connection.x}` reference.</param>
public sealed record LoadInput(string Name, string? Type, string? Default, InputResolverSpec? Resolver, string OnNull, IReadOnlyList<string> Provides, int Line = 0)
{
    /// <summary>The input names one resolution of this entry answers: itself, or the names it provides.</summary>
    public IReadOnlyList<string> Names => Provides.Count > 0 ? Provides : [Name];
}

/// <summary>Types an input can have and how a value is written for one.</summary>
public static class InputValues
{
    /// <summary>The types an input may be declared with: the ones a script binds and a person can type.</summary>
    public static readonly IReadOnlyList<string> Types = ["DATE", "TIMESTAMP", "INTEGER", "BIGINT", "SMALLINT", "VARCHAR"];

    private static readonly Regex Sized = new(@"^\s*([A-Za-z]+)\s*(\(.*\))?\s*$", RegexOptions.CultureInvariant);

    /// <summary>The type as <see cref="Types"/> spells it (case and a length ignored), or null when an input cannot have it.</summary>
    public static string? Normalize(string type)
    {
        var m = Sized.Match(type);
        if (!m.Success) return null;
        var t = m.Groups[1].Value.ToUpperInvariant();
        if (t == "INT") t = "INTEGER";
        if (t is "TEXT" or "STRING") t = "VARCHAR";
        return Types.Contains(t) ? t : null;
    }

    /// <summary>Whether a value written for an input is one of the input's type. A type this does not know a form for takes any text.</summary>
    public static bool Fits(string type, string value)
    {
        var t = type.Trim().ToUpperInvariant();
        if (t == "DATE" || t.StartsWith("TIMESTAMP", StringComparison.Ordinal) || t is "BIGINT" or "INTEGER" or "INT" or "SMALLINT") return ColumnTypes.LiteralFits(t == "INT" ? "INTEGER" : type, value);
        if (t.StartsWith("DECIMAL", StringComparison.Ordinal) || t.StartsWith("NUMERIC", StringComparison.Ordinal) || t is "DOUBLE" or "REAL" or "FLOAT") return decimal.TryParse(value.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
        if (t is "BOOLEAN" or "BOOL") return value is "true" or "false";
        return true;
    }
}

/// <summary>The project and connection parameters a resolver's text may name (`${project.days}`, `${connection.store_id}`): written into the text as literals of their type, before DuckDB reads it.</summary>
public static class InputParameters
{
    /// <summary>The scopes a resolver may use: the model's own and an origin's make no sense where the value is computed on one connection.</summary>
    public static readonly IReadOnlyList<string> Scopes = ["project", "connection"];

    /// <summary>The references in a text, as `scope.name`.</summary>
    public static IReadOnlyList<string> Refs(string text) => ParameterReferences.In(text).Select(r => $"{r.Scope}.{r.Name}").ToList();

    /// <summary>A DuckDB literal of the parameter's type. A text or a name is quoted; a number is written as it is (validated when the parameter was read); a date or timestamp is a typed literal.</summary>
    public static string Literal(string type, string value)
    {
        var t = type.Trim().ToUpperInvariant();
        return t switch
        {
            "BIGINT" or "INTEGER" or "SMALLINT" when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) => n.ToString(CultureInfo.InvariantCulture),
            "DATE" => $"DATE '{value.Replace("'", "''")}'",
            "TIMESTAMP" => $"TIMESTAMP '{value.Replace("'", "''")}'",
            _ => "'" + value.Replace("'", "''") + "'",
        };
    }

    /// <summary>The text with every reference written as a literal. A reference <paramref name="lookup"/> cannot answer is left as written (the caller reports it).</summary>
    public static string Substitute(string text, Func<string, ParameterValue?> lookup) =>
        ParameterReferences.Substitute(text, (scope, name) => lookup($"{scope}.{name}") is { } v ? Literal(v.Type, v.Value) : null);
}

/// <summary>Time zone names (IANA) as the settings `timezone:` and the clock of a resolver use them.</summary>
public static class Timezones
{
    /// <summary>Whether the machine knows the zone. `UTC` is always known, so a machine without zone data still runs the default.</summary>
    public static bool IsKnown(string name) => name == "UTC" || Find(name) != null;

    public static TimeZoneInfo? Find(string name)
    {
        if (name == "UTC") return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(name); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException or System.Security.SecurityException) { return null; }
    }
}
