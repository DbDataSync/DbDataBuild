using System.Security.Cryptography;
using System.Text;

namespace DbDataBuild.Models;

/// <summary>
/// What a Flight SQL server said about itself in its answer to `GetSqlInfo` (docs/research/read-origins.md section 10), by the standard ids: `FLIGHT_SQL_SERVER_NAME` (0), `FLIGHT_SQL_SERVER_VERSION` (1),
/// `FLIGHT_SQL_SERVER_READ_ONLY` (3), `SQL_IDENTIFIER_CASE` (503), `SQL_IDENTIFIER_QUOTE_CHAR` (504) and `SQL_QUOTED_IDENTIFIER_CASE` (505). Every member is null when the server did not say it. The two case members are the
/// words of the protocol's enum: `case_insensitive`, `uppercase`, `unknown`, or `value_N` for one this tool does not know.
/// </summary>
public sealed record ServerFacts(string? ServerName = null, string? ServerVersion = null, string? IdentifierQuote = null, string? IdentifierCase = null, string? QuotedIdentifierCase = null, bool? ReadOnly = null)
{
    public static readonly ServerFacts Nothing = new();

    /// <summary>True when the server said none of what the tool reads.</summary>
    public bool IsEmpty => this == Nothing;

    /// <summary>The words of the protocol's `SqlSupportedCaseSensitivity` enum.</summary>
    public static string CaseName(long value) => value switch { 0 => "unknown", 1 => "case_insensitive", 2 => "uppercase", _ => "value_" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
}

/// <summary>The dialect chosen for what a server reported: its name (a built-in, a project's own, or `ansi`), the profile to use, and why, in words.</summary>
public sealed record DiscoveredDialect(string Dialect, DialectProfile Profile, IReadOnlyList<string> Reasons);

/// <summary>
/// Chooses a dialect from what a Flight SQL server reported (`readonly flightsql` with no dialect keyword). The server's product name picks a dialect the tool knows (a table, so it can grow: a built-in, or a name from
/// the project's `dialects:` that the product name spells), otherwise `ansi`; the facts the server reported then correct that profile where they differ (the identifier quote character, an upper-case identifier case). Pure:
/// the same facts and the same dialects always give the same answer, which is what lets a refresh compare today's answer with the committed one.
/// </summary>
public static class DialectDiscovery
{
    /// <summary>Product name fragments (lower case) to the built-in dialect they mean, first match wins: a table, not a switch.</summary>
    private static readonly (string Fragment, string Dialect)[] Products =
    [
        ("fabric", "fabric"), ("sql server", "sqlserver"), ("sqlserver", "sqlserver"), ("mssql", "sqlserver"), ("azure sql", "sqlserver"),
        ("postgres", "postgres"), ("duckdb", "duckdb"),
    ];

    /// <summary>A product name as a dialect name: lower case, runs of other characters as `_` (`ClickHouse` is `clickhouse`).</summary>
    public static string Slug(string product) =>
        System.Text.RegularExpressions.Regex.Replace(product.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    public static DiscoveredDialect Choose(ServerFacts facts, IReadOnlyDictionary<string, DialectProfile> userDialects)
    {
        var reasons = new List<string>();
        DialectProfile? baseProfile = null;
        if (facts.ServerName is { Length: > 0 } product)
        {
            var slug = Slug(product);
            if (userDialects.TryGetValue(slug, out var own)) { baseProfile = DialectProfiles.ForDriver("flightsql", own); reasons.Add($"the server is `{product}`, which is the project's dialect `{slug}`"); }
            else if (DialectProfiles.BuiltIn.TryGetValue(slug, out var named) && slug != "ansi") { baseProfile = DialectProfiles.ForDriver("flightsql", named); reasons.Add($"the server is `{product}`, which is the built-in dialect `{slug}`"); }
            else
            {
                var lower = product.ToLowerInvariant();
                foreach (var (fragment, dialect) in Products)
                    if (lower.Contains(fragment, StringComparison.Ordinal)) { baseProfile = DialectProfiles.ForDriver("flightsql", DialectProfiles.BuiltIn[dialect]); reasons.Add($"the server is `{product}`, which the tool knows as the dialect `{dialect}`"); break; }
                if (baseProfile == null) reasons.Add($"the server is `{product}`, which names no dialect the tool knows");
            }
        }
        else reasons.Add("the server did not say what system it is");
        var profile = baseProfile ?? DialectProfiles.Ansi;
        if (baseProfile == null) reasons.Add("the dialect is `ansi`; add a profile under `dialects:` named after the product, or put the dialect in `engine`, to choose another");

        var name = profile.Name;
        if (facts.IdentifierQuote is { Length: > 0 } quote)
        {
            var (open, close) = quote switch { "\"" => ("\"", "\""), "`" => ("`", "`"), "[" => ("[", "]"), _ => (null, null) };
            if (open == null) reasons.Add($"the identifier quote the server reported (`{quote}`) is not one the tool writes; `{profile.QuoteOpen}` is kept");
            else if (open != profile.QuoteOpen) { profile = profile with { QuoteOpen = open, QuoteClose = close! }; reasons.Add($"the server reported the identifier quote `{quote}`, so names are quoted with it"); }
        }
        if (facts.IdentifierCase == "uppercase" && profile.Case != IdentifierCase.Upper) { profile = profile with { Case = IdentifierCase.Upper }; reasons.Add("the server reported that it folds identifiers to upper case"); }
        if (baseProfile != null && profile != baseProfile) profile = profile with { Extends = baseProfile.Name };
        return new DiscoveredDialect(name, profile, reasons);
    }

    /// <summary>
    /// The hash of everything in a profile that the committed text depends on: its name, how a name is quoted, how a parameter is written, the ping, the catalog, the identifier case and the limit style. (The type table is
    /// left out: it only serves `project import` and the structure check, and is the base's.) A refresh compares this, not the facts.
    /// </summary>
    public static string Hash(DialectProfile p)
    {
        var text = $"{p.Name}\n{p.QuoteOpen}{p.QuoteClose}\n{p.Parameters}\n{p.Ping}\n{p.Catalog}\n{p.Case}\n{p.Limit}\n";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
