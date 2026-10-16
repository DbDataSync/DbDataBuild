using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Conformance.FunctionMatrix;

/// <summary>One row of `duckdb_functions()`: an overload of a scalar, aggregate or window function, or a macro.</summary>
public sealed record FunctionOverload(
    string Schema, string Kind, string Name, string? AliasOf, IReadOnlyList<string> Types, IReadOnlyList<string> ParameterNames,
    string ReturnType, string? Varargs, string Description, string Stability, bool SideEffects, string? MacroDefinition)
{
    /// <summary>The name an alias points at (the group the overload belongs to in the report).</summary>
    public string Canonical => AliasOf ?? Name;

    public string Signature => $"{Name}({string.Join(", ", Types.Select((t, i) => ParameterNames.Count > i && ParameterNames[i].Length > 0 && Kind == "macro" ? ParameterNames[i] : t))}{(Varargs != null ? (Types.Count > 0 ? ", ..." : "...") + " " + Varargs : "")}) -> {ReturnType}";
}

public sealed record TableFunction(string Schema, string Kind, string Name, string? AliasOf, string Parameters, string Description);

/// <summary>What the DuckDB the tool ships (DuckDB.NET) says it has. Everything in the matrix starts here, not from a hand-written list.</summary>
public static class FunctionCatalog
{
    private const char Sep = '\u001f';

    // The window-only functions are listed as aggregates by DuckDB; they need an OVER clause.
    public static readonly IReadOnlySet<string> WindowOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "row_number", "rank", "dense_rank", "rank_dense", "percent_rank", "cume_dist", "ntile", "lag", "lead", "first_value", "last_value", "nth_value", "fill",
    };

    public static string KindOf(string kind, string name) =>
        !name.Any(char.IsLetter) || name == "!__postfix" ? "operator" : kind == "aggregate" ? (WindowOnly.Contains(name) ? "window" : "aggregate") : kind;

    public static (List<FunctionOverload> Functions, List<TableFunction> Tables, string Version) Enumerate(DuckDBConnection duck)
    {
        string Text(System.Data.IDataRecord r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var functions = new List<FunctionOverload>();
        var tables = new List<TableFunction>();
        using (var cmd = duck.CreateCommand())
        {
            cmd.CommandText = $@"SELECT schema_name, function_type, function_name, alias_of,
                coalesce(array_to_string(parameter_types, chr(31)), '') AS types, coalesce(array_to_string(parameters, chr(31)), '') AS names,
                coalesce(return_type, '') AS return_type, varargs, coalesce(description, '') AS description, coalesce(stability, '') AS stability,
                coalesce(has_side_effects, false) AS side_effects, macro_definition, coalesce(len(parameter_types), -1) AS arity
                FROM duckdb_functions()
                ORDER BY function_type, schema_name, function_name, parameter_types::VARCHAR, return_type";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var type = Text(r, 1);
                var name = Text(r, 2);
                List<string> types = Text(r, 4).Length == 0 ? [] : Text(r, 4).Split(Sep).ToList();
                List<string> names = Text(r, 5).Length == 0 ? [] : Text(r, 5).Split(Sep).ToList();
                if (type is "macro") types = names.Select(_ => "NULL").ToList();      // array_to_string drops the NULL type names DuckDB gives a macro's parameters
                if (type is "table" or "table_macro" or "pragma")
                {
                    tables.Add(new(Text(r, 0), type, name, r.IsDBNull(3) ? null : Text(r, 3), string.Join(", ", names.Select((n, i) => types.Count > i ? $"{n} {types[i]}" : n)), Text(r, 8)));
                    continue;
                }
                functions.Add(new(Text(r, 0), KindOf(type, name), name, r.IsDBNull(3) ? null : Text(r, 3), types, names, Text(r, 6), r.IsDBNull(7) ? null : Text(r, 7), Text(r, 8), Text(r, 9),
                    r.GetBoolean(10), r.IsDBNull(11) ? null : Text(r, 11)));
            }
        }
        string version;
        using (var cmd = duck.CreateCommand()) { cmd.CommandText = "SELECT version()"; version = (string)cmd.ExecuteScalar()!; }
        return (functions, tables, version);
    }

    /// <summary>Functions that are not probed at all, with the reason: they are not something a query calls (collations, compression helpers), or calling them has a side effect or needs state a probe cannot supply.</summary>
    public static string? NotProbedReason(FunctionOverload f)
    {
        var n = f.Name;
        if (n.StartsWith("icu_collate_", StringComparison.Ordinal)) return "internal: a collation (used through COLLATE), not a callable function";
        if (n.StartsWith("__internal_", StringComparison.Ordinal)) return "internal: a compression helper DuckDB's optimizer inserts; not callable by name";
        return n switch
        {
            "sleep_ms" or "pg_sleep" or "pg_sleep_for" or "pg_sleep_until" => "not probed: blocks the connection for the given time (a side effect)",
            "write_log" => "not probed: writes to DuckDB's log (a side effect)",
            "nextval" or "currval" => "not probed: needs a sequence (the tool has no sequences)",
            "generate_series" or "range" when f.Kind == "scalar" => "not probed: the scalar form returns a LIST (the tool refuses list values, DDB-324), and the probe values (up to 2147483647) would build lists of billions of elements",
            "getvariable" => "not probed: needs a SET VARIABLE in the session",
            "st_asbinary" or "st_astext" or "st_aswkb" or "st_aswkt" or "st_crs" or "st_geomfromwkb" or "st_intersects_extent" or "st_setcrs" => "not probed: GEOMETRY argument; there is no geometry value to build without a spatial table",
            _ when n.StartsWith("variant_", StringComparison.Ordinal) => "not probed: VARIANT argument; no way to build a VARIANT value in a probe",
            _ => null,
        };
    }
}
