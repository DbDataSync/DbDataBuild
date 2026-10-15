using System.Text.RegularExpressions;
using DbDataBuild.Core;

namespace DbDataBuild.Models;

/// <summary>What a collation name says about string comparison. A null dimension means the name does not tell, which is never assumed to match.</summary>
public sealed record CollationTraits(CaseSensitivity? Case, AccentSensitivity? Accent, TrailingSpace? Trailing)
{
    public static readonly CollationTraits Unknown = new(null, null, null);
}

/// <summary>
/// Reads collation names offline (DESIGN.md 7.4). Rules per engine are data-like pure functions, enumerated by tests.
/// Behavior that no name can settle (Fabric trailing spaces, unrecognized names) stays unknown and is reported, not assumed.
/// </summary>
public static class CollationTraitsParser
{
    public const string DuckDb = "duckdb";

    public static CollationTraits Parse(string engine, string name) => engine switch
    {
        TargetNames.SqlServer => ParseSqlServerStyle(name, trailing: TrailingSpace.Ignored),   // ANSI padding: trailing spaces ignored in =, whatever the collation
        TargetNames.Fabric => ParseSqlServerStyle(name, trailing: null),                       // [VERIFY] DESIGN.md 7.4: Fabric trailing-space semantics
        TargetNames.Postgres => ParsePostgres(name),
        DuckDb => ParseDuckDb(name),
        _ => CollationTraits.Unknown,
    };

    // Windows and SQL collations: ..._CI_AS_KS_WS_SC_UTF8, SQL_Latin1_General_CP1_CI_AS, Latin1_General_BIN2
    private static CollationTraits ParseSqlServerStyle(string name, TrailingSpace? trailing)
    {
        var tokens = name.ToUpperInvariant().Split('_');
        var binary = tokens.Any(t => t is "BIN" or "BIN2");
        CaseSensitivity? @case = binary ? CaseSensitivity.Sensitive
            : tokens.Contains("CI") ? CaseSensitivity.Insensitive
            : tokens.Contains("CS") ? CaseSensitivity.Sensitive : null;
        AccentSensitivity? accent = binary ? AccentSensitivity.Sensitive
            : tokens.Contains("AI") ? AccentSensitivity.Insensitive
            : tokens.Contains("AS") ? AccentSensitivity.Sensitive : null;
        return new CollationTraits(@case, accent, trailing);
    }

    // Verified on DuckDB 1.5.4: NOCASE ignores case only, NOACCENT ignores accents only, they chain with '.', NFC and locale
    // collations stay case- and accent-sensitive, and trailing spaces are significant under every collation.
    private static CollationTraits ParseDuckDb(string name)
    {
        var @case = CaseSensitivity.Sensitive;
        var accent = AccentSensitivity.Sensitive;
        foreach (var part in name.Trim().ToUpperInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "NOCASE") @case = CaseSensitivity.Insensitive;
            else if (part == "NOACCENT") accent = AccentSensitivity.Insensitive;
            else if (part == "NFC" || Regex.IsMatch(part, "^[A-Z]{2,3}([_-][A-Z]{2})?$")) { /* sensitive, unchanged */ }
            else return new CollationTraits(null, null, TrailingSpace.Significant);
        }
        return new CollationTraits(@case, accent, TrailingSpace.Significant);
    }

    // libc and C locales are deterministic and case/accent-sensitive; ICU nondeterministic collations are recognized by their
    // strength key (-u-ks-level1 ignores case and accents, level2 ignores case). varchar `=` keeps trailing spaces in all of them.
    private static CollationTraits ParsePostgres(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n is "c" or "posix" or "ucs_basic" or "default" || Regex.IsMatch(n, @"^[a-z]{2,3}_[a-z]{2}(\.utf-?8)?$") ||
            Regex.IsMatch(n, @"^[a-z]{2,3}(-[a-z]{2})?-x-icu$"))
            return new CollationTraits(CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant);
        if (n.Contains("-u-ks-level1")) return new CollationTraits(CaseSensitivity.Insensitive, AccentSensitivity.Insensitive, TrailingSpace.Significant);
        if (n.Contains("-u-ks-level2")) return new CollationTraits(CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant);
        return new CollationTraits(null, null, TrailingSpace.Significant);
    }
}

/// <summary>Offline check that the configured collations can satisfy the project's string profile (reported before any connection exists).</summary>
public static class CollationChecker
{
    public const string DefaultLogicalName = "default";

    public static IReadOnlyList<Diagnostic> Check(ProjectConfig config, IReadOnlyList<BuiltModel> models)
    {
        var diags = new List<Diagnostic>();
        var cfgFile = ProductInfo.ConfigFile;
        int Line(string key) => config.Lines.TryGetValue(key, out var l) ? l : 0;

        // Connections in play: every model's connections (or the project defaults) plus DuckDB, which runs the offline emulation. The project's default connections count only when a model relies on them,
        // or when there are no models yet. Each connection is held to its own profile (the project's, with what the connection says over it); DuckDB to the project's.
        var used = models.SelectMany(s => s.Definition.Targets ?? config.DefaultConnections).Concat(models.Count == 0 ? config.DefaultConnections : [])
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var checks = used.Select(c => (Connection: (string?)c, Engine: config.EngineOf(c) ?? c, Semantics: config.SemanticsOf(c))).ToList();
        checks.Add((null, CollationTraitsParser.DuckDb, config.StringSemantics));
        var collations = config.StringSemantics.Collations;
        var engines = checks.Select(c => c.Engine).Distinct().OrderBy(e => e, StringComparer.Ordinal).ToList();

        if (!collations.ContainsKey(DefaultLogicalName) && checks.Any(c => !c.Semantics.Collations.ContainsKey(DefaultLogicalName)))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, new(cfgFile, Line("string_semantics.collations"), 1),
                $"`string_semantics.collations` has no `{DefaultLogicalName}` entry, which is the project default collation.",
                Fix: $"Add `{DefaultLogicalName}:` with an entry per engine ({string.Join(", ", engines)})."));
            return diags;
        }

        foreach (var (connection, engine, semantics) in checks)
        {
            var own = connection != null && config.Connections.TryGetValue(connection, out var cc) && cc.Semantics != null;
            var where = own ? $" (the connection `{connection}`)" : "";
            var at = own ? config.Connections[connection!].Line : Line($"string_semantics.collations.{DefaultLogicalName}.{engine}");
            if (!semantics.Collations.TryGetValue(DefaultLogicalName, out var defaults) || !defaults.TryGetValue(engine, out var name))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, new(cfgFile, at == 0 ? Line("string_semantics.collations") : at, 1),
                    $"No default collation is configured for `{engine}`{where}, which this project uses.",
                    Fix: $"Add `{engine}: <collation>` under `string_semantics.collations.{DefaultLogicalName}`{(own ? $" (or under `connections.{connection}.string_semantics`)" : "")}."));
                continue;
            }
            diags.AddRange(CheckProfile(config with { StringSemantics = semantics }, engine, name, new SourceLocation(cfgFile, at, 1)).Select(d => own ? d with { Found = $"{d.Found} (the connection `{connection}`)" } : d));
        }

        foreach (var built in models)
        {
            var modelConnections = (built.Definition.Targets ?? config.DefaultConnections).Select(c => (Connection: (string?)c, Engine: config.EngineOf(c) ?? c, Semantics: config.SemanticsOf(c))).Append((null, CollationTraitsParser.DuckDb, config.StringSemantics)).ToList();
            foreach (var column in built.Definition.Columns.Where(c => c.Collation != null))
            {
                var loc = new SourceLocation(built.DefinitionFile, column.CollationLine, 1);
                if (!collations.ContainsKey(column.Collation!) && !modelConnections.All(c => c.Semantics.Collations.ContainsKey(column.Collation!)))
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, loc,
                        $"Column `{column.Name}` uses collation `{column.Collation}`, which is not defined under `string_semantics.collations`.",
                        Fix: $"Define `{column.Collation}` under `string_semantics.collations` in {cfgFile}, or use `{DefaultLogicalName}`."));
                    continue;
                }
                foreach (var (connection, engine, semantics) in modelConnections)
                    if (!semantics.Collations.TryGetValue(column.Collation!, out var perEngine) || !perEngine.ContainsKey(engine))
                        diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, loc,
                            $"Column `{column.Name}` uses collation `{column.Collation}`, which has no entry for `{engine}`{(connection != null && semantics != config.StringSemantics ? $" (the connection `{connection}`)" : "")}.",
                            Fix: $"Add `{engine}: <collation>` under `string_semantics.collations.{column.Collation}`."));
            }
        }
        return diags.DistinctBy(d => (d.Code, d.Location, d.Found)).ToList();
    }

    /// <summary>
    /// The same profile check against what the live catalog reports (DESIGN.md 9.4, `check`). Text columns the model leaves on the default collation must have a live
    /// collation that satisfies the profile; a declared exception is not held to it. A text column on the database default (reported with no name) cannot be verified.
    /// </summary>
    /// <param name="liveColumns">Live text columns of one object: name and the collation the catalog reports (null for the database default).</param>
    public static IReadOnlyList<Diagnostic> CheckLive(ProjectConfig config, string engine, ModelDefinition model, IEnumerable<(string Column, string? Collation)> liveColumns)
    {
        var diags = new List<Diagnostic>();
        var declared = model.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (var (column, collation) in liveColumns.OrderBy(c => c.Column, StringComparer.Ordinal))
        {
            if (!declared.TryGetValue(column, out var def) || (def.Collation != null && def.Collation != DefaultLogicalName)) continue; // not ours, or a declared exception
            var loc = new SourceLocation($"{engine}:{model.Name}.{column}", 0, 0);
            if (collation == null)
                diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotVerifiable, loc, $"Column `{column}` of {model.Name} uses the database default collation on {engine}, so how it compares strings cannot be verified."));
            else
                diags.AddRange(CheckProfile(config, engine, collation, loc));
        }
        return diags;
    }

    private static IEnumerable<Diagnostic> CheckProfile(ProjectConfig config, string engine, string name, SourceLocation loc)
    {
        var profile = config.StringSemantics;
        var traits = CollationTraitsParser.Parse(engine, name);
        var failures = new List<string>();
        var unknown = new List<string>();

        Compare("case", profile.Case.ToString().ToLowerInvariant(), traits.Case?.ToString().ToLowerInvariant(), profile.Case == traits.Case);
        Compare("accent", profile.Accent.ToString().ToLowerInvariant(), traits.Accent?.ToString().ToLowerInvariant(), profile.Accent == traits.Accent);
        // DuckDB cannot ignore trailing spaces with any collation; the profile is met there by the offline rtrim() rewrite (DESIGN.md 7.4).
        if (engine != CollationTraitsParser.DuckDb)
            Compare("trailing_space", profile.TrailingSpace.ToString().ToLowerInvariant(), traits.Trailing?.ToString().ToLowerInvariant(), profile.TrailingSpace == traits.Trailing);

        void Compare(string dimension, string required, string? actual, bool ok)
        {
            if (actual == null) unknown.Add($"{dimension} (profile requires {required})");
            else if (!ok) failures.Add($"{dimension} is {actual} but the profile requires {required}");
        }

        if (failures.Count > 0)
            yield return new Diagnostic(DiagnosticCatalog.CollationCannotSatisfyProfile, loc,
                $"Collation `{name}` for {engine} cannot satisfy the string profile ({profile.Describe()}): {string.Join("; ", failures)}.",
                Fix: $"Change `string_semantics.collations.default.{engine}` to a collation with the required behavior, or change `string_semantics`.");
        if (unknown.Count > 0)
            yield return new Diagnostic(DiagnosticCatalog.CollationNotVerifiable, loc,
                $"Cannot tell from `{name}` how {engine} compares strings for: {string.Join("; ", unknown)}.");
    }
}
