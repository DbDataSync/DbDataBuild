using System.Collections.Frozen;

namespace DbDataBuild.Models;

/// <summary>How a statement names its parameters: `@name` (SQL Server, PostgreSQL through Npgsql), `?` bound by position, or `$1`, `$2`.</summary>
public enum ParameterStyle { NamedAt, Positional, Numbered }

/// <summary>
/// Where a dialect's catalog is read: the standard `information_schema` views; the ODBC driver's own catalog functions (`SQLTables`, `SQLColumns`, `SQLPrimaryKeys`, `SQLForeignKeys`, `SQLStatistics`), for a system with no
/// `information_schema` (only for an ODBC connection); or nowhere (a table of such an origin is declared by hand and not checked). The order is the order of <see cref="DialectProfiles.CatalogStyles"/>.
/// </summary>
public enum CatalogStyle { InformationSchema, Odbc, None }

/// <summary>What an unquoted name becomes in the origin.</summary>
public enum IdentifierCase { Preserve, Upper, Lower }

/// <summary>How a bounded read is written: `LIMIT n`, `SELECT TOP n`, `FETCH FIRST n ROWS ONLY`.</summary>
public enum LimitStyle { Limit, Top, Fetch }

/// <summary>
/// What an origin needs to know about the SQL a system takes (docs/research/read-origins.md section 5). It is **data**: a table of settings, not code, and not an <c>ITarget</c> (nothing is rendered for an
/// origin; the tool only writes the shape of one read and, for `project import`, reads the catalog). The three engines the tool builds on, DuckDB and `ansi` are built in; a project adds its own with
/// `dialects:` and `extends:`.
/// </summary>
/// <param name="Name">The keyword that names the dialect in a connection's `engine`.</param>
/// <param name="QuoteOpen">The characters that open and close a quoted identifier (`"`, `` ` ``, `[` and `]`).</param>
/// <param name="Ping">The statement `connection inspect` sends to see the connection answers.</param>
/// <param name="Types">Catalog type names (lower case) to logical type names (`BIGINT`, `VARCHAR`, `DECIMAL`...). `project import` and the structure check use them; a copy converts by the declared type and never needs them.</param>
/// <param name="Extends">The dialect this one was made from; null for a built-in.</param>
public sealed record DialectProfile(string Name, string QuoteOpen, string QuoteClose, ParameterStyle Parameters, string Ping, CatalogStyle Catalog, IdentifierCase Case, LimitStyle Limit,
    IReadOnlyDictionary<string, string> Types, string? Extends = null)
{
    /// <summary>An identifier in this dialect's quotes; a closing quote inside the name is doubled.</summary>
    public string Quote(string identifier) => QuoteOpen + identifier.Replace(QuoteClose, QuoteClose + QuoteClose, StringComparison.Ordinal) + QuoteClose;

    /// <summary>
    /// A SELECT bounded to <paramref name="rows"/> rows, in this dialect's <see cref="Limit"/> style, with the original as a derived table: the one way the tool makes a bounded read of an origin (to learn the columns a
    /// native query returns when the driver cannot say without running it, with 0 rows).
    /// </summary>
    public string Bounded(string select, int rows)
    {
        var inner = select.Trim().TrimEnd(';');
        var n = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Limit switch
        {
            LimitStyle.Top => $"SELECT TOP {n} * FROM ({inner}) AS ddb_described",
            LimitStyle.Fetch => $"SELECT * FROM ({inner}) AS ddb_described FETCH FIRST {n} ROWS ONLY",
            _ => $"SELECT * FROM ({inner}) AS ddb_described LIMIT {n}",
        };
    }

    /// <summary>What the origin makes of a name written without quotes: upper case, lower case, or the name as it is (<see cref="Case"/>). Used where the tool looks a declared name up in a catalog.</summary>
    public string Fold(string name) => Case switch
    {
        IdentifierCase.Upper => name.ToUpperInvariant(),
        IdentifierCase.Lower => name.ToLowerInvariant(),
        _ => name,
    };

    /// <summary>`"schema"."name"` in this dialect.</summary>
    public string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    /// <summary>The text of one parameter in a statement: `@watermark`, `?`, or `$1` (<paramref name="ordinal"/> is its place, counting from 1, among the parameters of the statement).</summary>
    public string Placeholder(string name, int ordinal) => Parameters switch
    {
        ParameterStyle.NamedAt => "@" + name,
        ParameterStyle.Positional => "?",
        _ => "$" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>The built-in dialects, and the closed vocabulary a user's profile is written in.</summary>
public static class DialectProfiles
{
    public static readonly IReadOnlyList<string> ParameterStyles = ["named_at", "positional", "numbered"];
    public static readonly IReadOnlyList<string> CatalogStyles = ["information_schema", "odbc", "none"];
    public static readonly IReadOnlyList<string> IdentifierCases = ["preserve", "upper", "lower"];
    public static readonly IReadOnlyList<string> LimitStyles = ["limit", "top", "fetch"];

    /// <summary>The logical types a catalog type may be mapped to in a profile's `types:`. A parameterised one (`DECIMAL`, `VARCHAR`) takes its length, precision and scale from the catalog.</summary>
    public static readonly IReadOnlyList<string> LogicalTypeNames =
        ["BIGINT", "INTEGER", "SMALLINT", "BOOLEAN", "DECIMAL", "DOUBLE", "FLOAT", "DATE", "TIMESTAMP", "TIME", "TIMESTAMP WITH TIME ZONE", "UUID", "BLOB", "VARCHAR"];

    /// <summary>The type names the standard `information_schema` uses, which is what `ansi` and a profile that extends it start with.</summary>
    public static readonly IReadOnlyDictionary<string, string> AnsiTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bigint"] = "BIGINT", ["integer"] = "INTEGER", ["int"] = "INTEGER", ["smallint"] = "SMALLINT", ["boolean"] = "BOOLEAN",
        ["numeric"] = "DECIMAL", ["decimal"] = "DECIMAL", ["real"] = "FLOAT", ["double precision"] = "DOUBLE", ["float"] = "DOUBLE",
        ["date"] = "DATE", ["time"] = "TIME", ["time without time zone"] = "TIME",
        ["timestamp"] = "TIMESTAMP", ["timestamp without time zone"] = "TIMESTAMP", ["timestamp with time zone"] = "TIMESTAMP WITH TIME ZONE",
        ["character varying"] = "VARCHAR", ["varchar"] = "VARCHAR", ["character"] = "VARCHAR", ["char"] = "VARCHAR", ["text"] = "VARCHAR",
        ["binary large object"] = "BLOB", ["blob"] = "BLOB", ["uuid"] = "UUID",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // The names an engine's own types go by when its catalog is read through a driver of its own (ODBC's TYPE_NAME, a Flight SQL server's information_schema): on top of the standard ones, for a built-in engine dialect used
    // with `odbc` or `flightsql` (`readonly flightsql postgres`) and for a project's profile that extends one. The three engines and DuckDB read their own catalogs with their own code and do not use these.
    private static readonly IReadOnlyDictionary<string, string> SqlServerExtraTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tinyint"] = "SMALLINT", ["bit"] = "BOOLEAN", ["nvarchar"] = "VARCHAR", ["nchar"] = "VARCHAR", ["ntext"] = "VARCHAR", ["datetime"] = "TIMESTAMP", ["datetime2"] = "TIMESTAMP", ["smalldatetime"] = "TIMESTAMP",
        ["datetimeoffset"] = "TIMESTAMP WITH TIME ZONE", ["uniqueidentifier"] = "UUID", ["varbinary"] = "BLOB", ["binary"] = "BLOB", ["image"] = "BLOB",
    };
    private static readonly IReadOnlyDictionary<string, string> DuckDbExtraTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["double"] = "DOUBLE", ["float"] = "FLOAT", ["tinyint"] = "SMALLINT", ["timestamptz"] = "TIMESTAMP WITH TIME ZONE", ["timestamp_s"] = "TIMESTAMP", ["timestamp_ms"] = "TIMESTAMP",
    };
    private static readonly IReadOnlyDictionary<string, string> PostgresExtraTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["int2"] = "SMALLINT", ["int4"] = "INTEGER", ["int8"] = "BIGINT", ["float4"] = "FLOAT", ["float8"] = "DOUBLE", ["bool"] = "BOOLEAN", ["bpchar"] = "VARCHAR", ["timestamptz"] = "TIMESTAMP WITH TIME ZONE",
        ["bytea"] = "BLOB", ["timetz"] = "TIME", ["time with time zone"] = "TIME",
    };

    private static IReadOnlyDictionary<string, string> OnTopOfAnsi(IReadOnlyDictionary<string, string> own) =>
        AnsiTypes.Concat(own).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    // sqlserver, postgres and fabric read their own catalogs with their own code (CatalogReader), whose types are the ones ImportedTypes knows; `Types` is for a driver of another kind that names one of these dialects
    public static readonly DialectProfile SqlServer = new("sqlserver", "[", "]", ParameterStyle.NamedAt, "SELECT 1", CatalogStyle.InformationSchema, IdentifierCase.Preserve, LimitStyle.Top, OnTopOfAnsi(SqlServerExtraTypes));
    public static readonly DialectProfile Fabric = SqlServer with { Name = "fabric" };
    public static readonly DialectProfile Postgres = new("postgres", "\"", "\"", ParameterStyle.NamedAt, "SELECT 1", CatalogStyle.InformationSchema, IdentifierCase.Lower, LimitStyle.Limit, OnTopOfAnsi(PostgresExtraTypes));
    public static readonly DialectProfile DuckDb = new("duckdb", "\"", "\"", ParameterStyle.Positional, "SELECT 1", CatalogStyle.InformationSchema, IdentifierCase.Preserve, LimitStyle.Limit, OnTopOfAnsi(DuckDbExtraTypes));
    public static readonly DialectProfile Ansi = new("ansi", "\"", "\"", ParameterStyle.Positional, "SELECT 1", CatalogStyle.InformationSchema, IdentifierCase.Preserve, LimitStyle.Fetch, AnsiTypes);

    /// <summary>
    /// The profile a connection of this driver really uses. `odbc` and `flightsql` bind their parameters by position, so a built-in engine dialect used with one of them (`readonly flightsql postgres`: the system behind
    /// a proxy is PostgreSQL) keeps its quoting, limit style, case and ping but takes `?` for parameters; a project's profile that extends one and says `named_at` is read the same way, because nothing on those drivers could
    /// bind it. Any other driver's profile is as written.
    /// </summary>
    public static DialectProfile ForDriver(string driver, DialectProfile profile) =>
        driver is "odbc" or "flightsql" && profile.Parameters == ParameterStyle.NamedAt ? profile with { Parameters = ParameterStyle.Positional } : profile;

    public static readonly IReadOnlyDictionary<string, DialectProfile> BuiltIn = new Dictionary<string, DialectProfile>(StringComparer.Ordinal)
    {
        ["ansi"] = Ansi, ["duckdb"] = DuckDb, ["sqlserver"] = SqlServer, ["postgres"] = Postgres, ["fabric"] = Fabric,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The dialect of an engine the tool builds on, or of DuckDB, by the engine's name; null for any other name.</summary>
    public static DialectProfile? OfEngine(string engine) => BuiltIn.TryGetValue(engine, out var p) && engine != "ansi" ? p : null;

    /// <summary>
    /// Builds a profile from the settings a project wrote. Every field left out is the base's (`extends:`, `ansi` when none is named). Returns the problems instead of a profile when a value is not one of
    /// the closed choices; each is a message the loader reports at the node.
    /// </summary>
    public static (DialectProfile? Profile, IReadOnlyList<string> Problems) Build(string name, DialectProfile baseProfile, string? extends, string? quote, string? parameters, string? ping, string? catalog,
        string? identifierCase, string? limit, IReadOnlyDictionary<string, string>? types)
    {
        var problems = new List<string>();
        var (open, close) = (baseProfile.QuoteOpen, baseProfile.QuoteClose);
        if (quote != null)
        {
            var q = quote.Replace(" ", "", StringComparison.Ordinal);
            if (q is "\"" or "`") (open, close) = (q, q);
            else if (q == "[]") (open, close) = ("[", "]");
            else problems.Add($"`quote` is `{quote}`; it is one of: `\"`, `` ` ``, `[ ]`.");
        }
        var style = baseProfile.Parameters;
        if (parameters != null)
        {
            var i = ParameterStyles.ToList().IndexOf(parameters);
            if (i < 0) problems.Add($"`parameters` is `{parameters}`; it is one of: {string.Join(", ", ParameterStyles)}."); else style = (ParameterStyle)i;
        }
        var cat = baseProfile.Catalog;
        if (catalog != null)
        {
            var i = CatalogStyles.ToList().IndexOf(catalog);
            if (i < 0) problems.Add($"`catalog` is `{catalog}`; it is one of: {string.Join(", ", CatalogStyles)}."); else cat = (CatalogStyle)i;
        }
        var @case = baseProfile.Case;
        if (identifierCase != null)
        {
            var i = IdentifierCases.ToList().IndexOf(identifierCase);
            if (i < 0) problems.Add($"`identifier_case` is `{identifierCase}`; it is one of: {string.Join(", ", IdentifierCases)}."); else @case = (IdentifierCase)i;
        }
        var lim = baseProfile.Limit;
        if (limit != null)
        {
            var i = LimitStyles.ToList().IndexOf(limit);
            if (i < 0) problems.Add($"`limit` is `{limit}`; it is one of: {string.Join(", ", LimitStyles)}."); else lim = (LimitStyle)i;
        }
        if (ping != null && (ping.Trim().Length == 0 || ReadGuardText.IsNotSelect(ping))) problems.Add("`ping` must be one SELECT statement, for example `SELECT 1`.");
        var merged = new Dictionary<string, string>(baseProfile.Types, StringComparer.Ordinal);
        foreach (var (native, logical) in types ?? new Dictionary<string, string>())
        {
            if (!LogicalTypeNames.Contains(logical.ToUpperInvariant())) problems.Add($"`types.{native}` is `{logical}`; it is one of: {string.Join(", ", LogicalTypeNames)}.");
            else merged[native.ToLowerInvariant()] = logical.ToUpperInvariant();
        }
        if (problems.Count > 0) return (null, problems);
        return (new DialectProfile(name, open, close, style, ping?.Trim() ?? baseProfile.Ping, cat, @case, lim, merged, extends ?? baseProfile.Name), []);
    }
}

/// <summary>The one check on a profile's `ping` that the model layer can make: it begins with SELECT (the read guard of the execution layer, which sends it, refuses anything else again).</summary>
internal static class ReadGuardText
{
    public static bool IsNotSelect(string sql) => !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.TrimEnd().TrimEnd(';').Contains(';', StringComparison.Ordinal);
}
