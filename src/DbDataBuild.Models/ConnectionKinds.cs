namespace DbDataBuild.Models;

/// <summary>Whether the tool may write on a connection. `readonly` is a keyword of `engine`; the default is both.</summary>
public enum ConnectionAccess { ReadWrite, ReadOnly }

/// <summary>
/// What a connection's `engine` setting says, parsed once (docs/research/read-origins.md section 3): <see cref="Access"/>, the <see cref="Driver"/> (how the tool connects: `sqlserver`, `postgres`, `fabric`, `duckdb`, `odbc`,
/// `flightsql`) and the <see cref="Dialect"/> (the name of the SQL profile the origin takes: a built-in or one of the project's `dialects:`). No code reads the text of `engine` after this.
/// </summary>
/// <param name="DialectDeclared">True when `engine` names the dialect itself (a keyword), or the driver brings its own. False for `odbc` and `flightsql` with no dialect keyword: the dialect is then `ansi`, or, for Flight SQL, the one `project update` found on the server and committed.</param>
public sealed record ConnectionKind(ConnectionAccess Access, string Driver, string Dialect, bool DialectDeclared = true)
{
    /// <summary>A Flight SQL connection whose dialect is left to the server: no dialect keyword. `project update` asks it and commits the answer (`dialects.lock.yml`).</summary>
    public bool DialectIsDiscoverable => Driver == "flightsql" && !DialectDeclared;

    public bool ReadOnly => Access == ConnectionAccess.ReadOnly;

    /// <summary>The kind of a connection that is only named after an engine the tool builds on (read and write, the engine's own dialect).</summary>
    public static ConnectionKind OfEngine(string engine) => new(ConnectionAccess.ReadWrite, engine, engine);

    /// <summary>The engine of the tool's own targets: the three it builds on. A connection of any other driver is an origin only.</summary>
    public bool IsBuildTarget => TargetNames.All.Contains(Driver);

    /// <summary>`readonly postgres`, `readonly odbc ansi`: the keywords back as text, for messages and the metadata document.</summary>
    public string Describe() => string.Join(' ', new[] { ReadOnly ? "readonly" : null, Driver, ConnectionKeywords.BringsOwnDialect(Driver) ? null : Dialect }.Where(x => x != null));
}

/// <summary>The closed classes of the keywords of `engine`, and the rules between them.</summary>
public static class ConnectionKeywords
{
    public const string ReadOnlyKeyword = "readonly";
    public static readonly IReadOnlyList<string> Drivers = ["sqlserver", "postgres", "fabric", "duckdb", "odbc", "flightsql"];
    public static readonly IReadOnlyList<string> BuiltInDialects = ["ansi", "duckdb", "sqlserver", "postgres", "fabric"];

    /// <summary>Drivers that are only a driver: a keyword for one of these is never a dialect.</summary>
    private static readonly string[] DriverOnly = ["odbc", "flightsql"];

    /// <summary>The words a project's own dialect may not be called: every keyword of every class.</summary>
    public static IReadOnlyList<string> Reserved { get; } = [ReadOnlyKeyword, .. Drivers, .. BuiltInDialects.Except(Drivers)];

    /// <summary>The drivers that bring their own dialect: a dialect keyword with one of them is refused.</summary>
    public static bool BringsOwnDialect(string driver) => driver is "sqlserver" or "postgres" or "fabric" or "duckdb";

    /// <summary>The drivers that can only be read from, so `engine` must say `readonly` (a later release can lift the rule here, and nowhere else).</summary>
    public static bool NeedsReadOnly(string driver) => driver is "odbc" or "flightsql" or "duckdb";

    /// <summary>Drivers the tool lists but does not have yet; a connection of one is refused with a message that says so.</summary>
    public static bool IsBuilt(string driver) => driver is "sqlserver" or "postgres" or "fabric" or "duckdb" or "odbc" or "flightsql";

    /// <summary>Splits the text of `engine` into words: separated by spaces and/or commas.</summary>
    public static IReadOnlyList<string> Words(string text) => text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Sorts the keywords into the classes and checks the rules. The order of the words does not matter. A word that is both a driver and a dialect (`postgres`, `duckdb`...) is the driver, unless the connection
    /// has a driver of its own (`odbc`, `flightsql`), when it is the dialect. Returns the kind, or a message and what to do about it.
    /// </summary>
    /// <param name="userDialects">The names of the project's `dialects:`.</param>
    public static (ConnectionKind? Kind, string? Error, string? Fix) Parse(IReadOnlyList<string> words, IReadOnlySet<string> userDialects)
    {
        var choices = $"access: {ReadOnlyKeyword}; driver: {string.Join(", ", Drivers)}; dialect: {string.Join(", ", BuiltInDialects)}{(userDialects.Count == 0 ? "" : ", " + string.Join(", ", userDialects.Order(StringComparer.Ordinal)))}.";
        if (words.Count == 0) return (null, "`engine` is empty.", $"Name a driver, for example `engine: postgres` or `engine: readonly duckdb`. {choices}");
        var repeated = words.GroupBy(w => w, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (repeated != null) return (null, $"`{repeated.Key}` is repeated in `engine`.", $"Say each keyword once. {choices}");
        var unknown = words.FirstOrDefault(w => w != ReadOnlyKeyword && !Drivers.Contains(w) && !BuiltInDialects.Contains(w) && !userDialects.Contains(w));
        if (unknown != null) return (null, $"`{unknown}` is not a keyword of `engine`.", $"Keywords: {choices} A dialect of your own is declared under `dialects:`.");

        var readOnly = words.Contains(ReadOnlyKeyword);
        var rest = words.Where(w => w != ReadOnlyKeyword).ToList();
        var driverOnly = rest.Where(w => DriverOnly.Contains(w)).ToList();
        if (driverOnly.Count > 1) return (null, $"`engine` names two drivers: {string.Join(" and ", driverOnly.Select(w => $"`{w}`"))}.", $"A connection has one driver. {choices}");
        string? driver = driverOnly.FirstOrDefault();
        var dialects = new List<string>();
        foreach (var w in rest.Where(w => !DriverOnly.Contains(w)))
        {
            if (driver == null && Drivers.Contains(w)) { driver = w; continue; }       // the first of the words that can be a driver is the driver
            if (!BuiltInDialects.Contains(w) && !userDialects.Contains(w)) return (null, $"`{w}` is a driver, and `engine` already has one (`{driver}`).", $"A connection has one driver. {choices}");
            dialects.Add(w);
        }
        if (driver == null) return (null, "`engine` names no driver.", $"Add one of: {string.Join(", ", Drivers)}.");
        if (dialects.Count > 1) return (null, $"`engine` names two dialects: {string.Join(" and ", dialects.Select(w => $"`{w}`"))}.", $"A connection has one dialect. {choices}");
        if (dialects.Count == 1 && BringsOwnDialect(driver)) return (null, $"`{driver}` already has a dialect, so `{dialects[0]}` is not allowed with it.", $"Take `{dialects[0]}` out of `engine`: `{(readOnly ? "readonly " : "")}{driver}`. A dialect is for `odbc` and `flightsql`.");
        if (NeedsReadOnly(driver) && !readOnly) return (null, $"`{driver}` can only be read from, so `engine` needs `readonly`.", $"Write `engine: readonly {driver}`.");
        return (new ConnectionKind(readOnly ? ConnectionAccess.ReadOnly : ConnectionAccess.ReadWrite, driver, BringsOwnDialect(driver) ? driver : dialects.FirstOrDefault() ?? "ansi", BringsOwnDialect(driver) || dialects.Count > 0), null, null);
    }
}
