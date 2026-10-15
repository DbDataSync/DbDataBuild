using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DuckDB.NET.Data;

namespace DbDataBuild.Targets.DuckDb;

public sealed record DuckColumn(string Name, string Type, bool Nullable);

public sealed record DuckTable(string SchemaName, string Name, IReadOnlyList<DuckColumn> Columns);

/// <summary>A statement of a macro file that DuckDB refused.</summary>
public sealed class PreludeException(string message) : Exception(message);

public sealed record DescribedColumn(string Name, string DuckDbType);

/// <summary>Either the output columns of a query, or why DuckDB could not describe it.</summary>
public sealed record DescribeResult(IReadOnlyList<DescribedColumn>? Columns, string? Error)
{
    public bool Ok => Columns != null;
}

/// <summary>
/// Asks DuckDB what a model query returns, offline (DESIGN.md 6.5): an empty in-memory schema is built from the upstream declared
/// columns, then the query is described, never run. External access is switched off first, so a query cannot read files or the network
/// while it is being bound. This is the offline engine, not a connection to any target.
/// </summary>
public static partial class QueryDescriber
{
    // Declared types come from repo files. Only plain type names are passed to DuckDB: DECIMAL(14, 2), VARCHAR(20), BIGINT[], TIMESTAMP WITH TIME ZONE.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*( [A-Za-z][A-Za-z0-9_]*)*(\(\s*[0-9]+\s*(,\s*[0-9]+\s*)?\))?(\[[0-9]*\])*$")]
    private static partial Regex TypePattern();

    public static bool IsPlainType(string type) => TypePattern().IsMatch(type.Trim());

    public static DescribeResult Describe(IReadOnlyList<DuckTable> upstream, string sql, DuckPrelude? prelude = null)
    {
        foreach (var column in upstream.SelectMany(t => t.Columns).Where(c => !IsPlainType(c.Type)))
            return new(null, $"The declared type `{column.Type}` of column `{column.Name}` is not a plain SQL type name.");

        using var connection = PlanningConnection.Open();
        try
        {
            Build(connection, upstream, prelude);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DESCRIBE " + sql.Trim().TrimEnd(';').TrimEnd();
            using var reader = cmd.ExecuteReader();
            var result = new List<DescribedColumn>();
            while (reader.Read()) result.Add(new DescribedColumn(reader.GetString(0), reader.GetString(1)));
            return new(result, null);
        }
        catch (PreludeException ex)
        {
            return new(null, ex.Message);
        }
        catch (DuckDBException ex)
        {
            return new(null, FirstLine(ex.Message));
        }
    }

    /// <summary>
    /// The bound, unoptimized logical plan of a query as DuckDB's JSON (`json_serialize_plan(..., optimize := false)`), against the same empty upstream schema as
    /// <see cref="Describe"/>. Macros, `*`, PIVOT, `GROUP BY ALL` and implicit casts are already expanded in it. The plan format is DuckDB's own and not a stable API;
    /// <see cref="DuckDbVersion"/> says which engine produced it.
    /// </summary>
    public static (string? Json, string? Error) SerializePlan(IReadOnlyList<DuckTable> upstream, string sql, DuckPrelude? prelude = null)
    {
        foreach (var column in upstream.SelectMany(t => t.Columns).Where(c => !IsPlainType(c.Type)))
            return (null, $"The declared type `{column.Type}` of column `{column.Name}` is not a plain SQL type name.");
        using var connection = PlanningConnection.Open(out var status);
        if (status.ClockWouldFreeze) return (null, status.Problem ?? "this DuckDB folds the clock functions into constants and they could not be replaced, so a lowered query would freeze the day of lowering");
        try
        {
            Build(connection, upstream, prelude);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = PlanStatement(sql);
            var json = cmd.ExecuteScalar() as string;
            return json == null ? (null, "DuckDB returned no plan.") : (json, null);
        }
        catch (PreludeException ex)
        {
            return (null, ex.Message);
        }
        catch (DuckDBException ex)
        {
            return (null, FirstLine(ex.Message));
        }
    }

    /// <summary>Creates the prelude against the upstream schema and says why DuckDB refused it, or null when it was accepted. Nothing is run.</summary>
    public static string? CheckPrelude(IReadOnlyList<DuckTable> upstream, DuckPrelude prelude)
    {
        foreach (var column in upstream.SelectMany(t => t.Columns).Where(c => !IsPlainType(c.Type)))
            return $"The declared type `{column.Type}` of column `{column.Name}` is not a plain SQL type name.";
        using var connection = PlanningConnection.Open();
        try
        {
            Build(connection, upstream, prelude);
            return null;
        }
        catch (PreludeException ex) { return ex.Message; }
        catch (DuckDBException ex) { return FirstLine(ex.Message); }
    }

    /// <summary>
    /// `json_serialize_plan` only accepts a constant, so the query text is a string literal. In DuckDB a standard string has no escape other than a doubled quote,
    /// so the text cannot end the literal; a second statement in it is rejected by the function itself (it parses exactly one).
    /// </summary>
    public static string PlanStatement(string sql) =>
        "SELECT json_serialize_plan('" + sql.Trim().TrimEnd(';').TrimEnd().Replace("'", "''") + "', optimize := false, skip_null := true, skip_empty := true)";

    /// <summary>The DuckDB engine version, for recording next to anything derived from its plans.</summary>
    public static string DuckDbVersion()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version()";
        return (string)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// The empty upstream schema, with the project's macros and types: types first (a declared column may have one), then the tables, then the macros (DuckDB binds the names in a macro when it creates it).
    /// A statement of a macro file that DuckDB refuses is reported as that, not as a problem of the query.
    /// </summary>
    public static void Build(DuckDBConnection connection, IReadOnlyList<DuckTable> upstream, DuckPrelude? prelude)
    {
        foreach (var statement in (prelude?.Schemas ?? []).Concat(prelude?.Types ?? [])) RunMacroStatement(connection, statement);
        foreach (var table in upstream)
        {
            Run(connection, $"CREATE SCHEMA IF NOT EXISTS {Quote(table.SchemaName)}");
            var columns = string.Join(", ", table.Columns.Select(c => $"{Quote(c.Name)} {c.Type.Trim()}{(c.Nullable ? "" : " NOT NULL")}"));
            Run(connection, $"CREATE TABLE {Quote(table.SchemaName)}.{Quote(table.Name)} ({columns})");
        }
        foreach (var statement in prelude?.Macros ?? []) RunMacroStatement(connection, statement);
    }

    private static void RunMacroStatement(DuckDBConnection connection, string statement)
    {
        try { Run(connection, statement); }
        catch (DuckDBException ex)
        {
            var head = statement.Trim().Split('\n', 2)[0].Trim();
            if (ClockDefault(ex.Message) is { } clock)
                throw new PreludeException($"{ClockDefaultCode}: a macro of the project ({(head.Length > 70 ? head[..70] + "..." : head)}) has a clock or session function as the default of parameter `{clock.Parameter}` ({clock.Default}); a default is a constant, bound when the macro is created");
            throw new PreludeException($"a macro or type of the project could not be created ({(head.Length > 70 ? head[..70] + "..." : head)}): {FirstLine(ex.Message)}");
        }
    }

    /// <summary>The diagnostic code that starts the error text when a macro's default is a clock or session function (see <see cref="ClockOverrides"/>); a caller that builds a diagnostic from the text looks for it.</summary>
    public const string ClockDefaultCode = "DDB-325";

    private static readonly Regex NotAConstantDefault = new(@"Default value '(?<default>.*)' for parameter '(?<parameter>[^']*)' is not a constant expression", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Lazy<Regex> ClockWords = new(() =>
        new Regex(@"(?<![A-Za-z0-9_])(" + string.Join("|", ClockOverrides.All.SelectMany(o => o.Keywords.Prepend(o.Name)).Distinct().Select(Regex.Escape)) + @")(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <summary>
    /// DuckDB's refusal of a macro default that is not a constant, when the default names one of the clock or session functions the planning connections replace (a default of `random()` is refused too, and says the same,
    /// but it is not ours to explain). With the replacements in place the call stays a call, so DuckDB refuses it; without them DuckDB 2.0 would fold it into the day of the lowering.
    /// </summary>
    internal static (string Parameter, string Default)? ClockDefault(string duckDbMessage)
    {
        var m = NotAConstantDefault.Match(duckDbMessage);
        if (!m.Success || !ClockWords.Value.IsMatch(m.Groups["default"].Value)) return null;
        return (m.Groups["parameter"].Value.Trim('"'), m.Groups["default"].Value);                   // 2.0 quotes the parameter's name
    }

    private static void Run(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] : line;
    }
}
