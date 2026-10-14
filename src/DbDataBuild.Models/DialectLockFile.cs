using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>
/// One connection's committed dialect (`dialects.lock.yml`): what the server reported, the dialect it was mapped onto, the profile fields the committed text depends on, and the hash of those fields. The fields are
/// kept as text in the vocabulary of a `dialects:` profile, so the file reads like one.
/// </summary>
public sealed record DialectLockEntry(string Connection, ServerFacts Reported, string Dialect, string Quote, string Parameters, string Ping, string Catalog, string IdentifierCase, string Limit, string Hash, int Line = 0)
{
    /// <summary>The entry for a dialect just chosen from what a server reported.</summary>
    public static DialectLockEntry From(string connection, ServerFacts facts, DiscoveredDialect chosen)
    {
        var p = chosen.Profile;
        return new(connection, facts, chosen.Dialect, p.QuoteOpen == "[" ? "[]" : p.QuoteOpen, DialectProfiles.ParameterStyles[(int)p.Parameters], p.Ping, DialectProfiles.CatalogStyles[(int)p.Catalog],
            DialectProfiles.IdentifierCases[(int)p.Case], DialectProfiles.LimitStyles[(int)p.Limit], DialectDiscovery.Hash(p));
    }

    /// <summary>The same entry with a different line (the line is where it was read, and takes no part in equality of content).</summary>
    public bool SameContentAs(DialectLockEntry other) => this with { Line = 0 } == other with { Line = 0 };
}

/// <summary>
/// `dialects.lock.yml`: the dialect each `readonly flightsql` connection without a dialect keyword was found to have, committed so `project compile` and the plans stay offline (docs/research/read-origins.md section 10).
/// Only `project update` writes it. A damaged file is DDB-457 and every connection in it is then read as `ansi`; never a guess.
/// </summary>
public static class DialectLockFile
{
    public const int Format = 1;
    private static readonly string[] TopKeys = ["format", "connections"];
    private static readonly string[] EntryKeys = ["server", "server_version", "reported", "dialect", "profile", "profile_hash"];
    private static readonly string[] ReportedKeys = ["identifier_quote", "identifier_case", "quoted_identifier_case", "read_only"];
    private static readonly string[] ProfileKeys = ["quote", "parameters", "ping", "catalog", "identifier_case", "limit"];

    /// <summary>The text of the file for these entries: sorted by connection, LF line ends, no timestamp and nothing about the machine.</summary>
    public static string Render(IEnumerable<DialectLockEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLineLf($"# Written by `{ProductInfo.Cli} project update`. Commit it: compile and the plans read it and never ask a server.");
        sb.AppendLineLf("# An explicit dialect in a connection's `engine` wins over an entry here. Do not edit; run project update again instead.");
        sb.AppendLineLf($"format: {Format}");
        var list = entries.OrderBy(e => e.Connection, StringComparer.Ordinal).ToList();
        if (list.Count == 0) { sb.AppendLineLf("connections: {}"); return sb.ToString(); }
        sb.AppendLineLf("connections:");
        foreach (var e in list)
        {
            sb.Append("  ").Append(YamlText.Scalar(e.Connection)).AppendLineLf(":");
            if (e.Reported.ServerName != null) sb.Append("    server: ").AppendLineLf(YamlText.Scalar(e.Reported.ServerName));
            if (e.Reported.ServerVersion != null) sb.Append("    server_version: ").AppendLineLf(YamlText.Scalar(e.Reported.ServerVersion));
            var r = e.Reported;
            if (r.IdentifierQuote != null || r.IdentifierCase != null || r.QuotedIdentifierCase != null || r.ReadOnly != null)
            {
                sb.AppendLineLf("    reported:");
                if (r.IdentifierQuote != null) sb.Append("      identifier_quote: ").AppendLineLf(YamlText.Scalar(r.IdentifierQuote));
                if (r.IdentifierCase != null) sb.Append("      identifier_case: ").AppendLineLf(YamlText.Scalar(r.IdentifierCase));
                if (r.QuotedIdentifierCase != null) sb.Append("      quoted_identifier_case: ").AppendLineLf(YamlText.Scalar(r.QuotedIdentifierCase));
                if (r.ReadOnly != null) sb.Append("      read_only: ").AppendLineLf(r.ReadOnly.Value ? "true" : "false");
            }
            sb.Append("    dialect: ").AppendLineLf(YamlText.Scalar(e.Dialect));
            sb.AppendLineLf("    profile:");
            sb.Append("      quote: ").AppendLineLf(YamlText.Scalar(e.Quote));
            sb.Append("      parameters: ").AppendLineLf(YamlText.Scalar(e.Parameters));
            sb.Append("      ping: ").AppendLineLf(YamlText.Scalar(e.Ping));
            sb.Append("      catalog: ").AppendLineLf(YamlText.Scalar(e.Catalog));
            sb.Append("      identifier_case: ").AppendLineLf(YamlText.Scalar(e.IdentifierCase));
            sb.Append("      limit: ").AppendLineLf(YamlText.Scalar(e.Limit));
            sb.Append("    profile_hash: ").AppendLineLf(YamlText.Scalar(e.Hash));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The entries of the file's text, or null with DDB-457 when it is damaged: not a mapping, an unknown or missing key, a value outside the choices, or a hash that is not the hash of the profile it sits beside (the file
    /// was edited by hand). <paramref name="userDialects"/> are the project's own dialects, which an entry's `dialect` may name.
    /// </summary>
    public static IReadOnlyList<DialectLockEntry>? Parse(string text, string file, List<Diagnostic> diags, IReadOnlyDictionary<string, DialectProfile> userDialects)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null) return diags.Count > before ? null : [];
        var reader = new Reader(file, diags, userDialects);
        var result = reader.Read(root);
        return diags.Count > before ? null : result;
    }

    /// <summary>The profile an entry stands for: its dialect's base with the entry's fields laid over it. Null when a field cannot be built (the caller has already reported the file).</summary>
    public static DialectProfile? ProfileOf(DialectLockEntry entry, IReadOnlyDictionary<string, DialectProfile> userDialects, out IReadOnlyList<string> problems)
    {
        var baseProfile = userDialects.TryGetValue(entry.Dialect, out var own) ? own : DialectProfiles.BuiltIn.GetValueOrDefault(entry.Dialect);
        if (baseProfile == null) { problems = [$"the dialect `{entry.Dialect}` is neither a built-in nor declared under `dialects:`"]; return null; }
        baseProfile = DialectProfiles.ForDriver("flightsql", baseProfile);
        var (profile, found) = DialectProfiles.Build(baseProfile.Name, baseProfile, null, entry.Quote, entry.Parameters, entry.Ping, entry.Catalog, entry.IdentifierCase, entry.Limit, null);
        problems = found;
        return profile == null ? null : profile with { Extends = baseProfile.Extends };
    }

    private sealed class Reader(string file, List<Diagnostic> diags, IReadOnlyDictionary<string, DialectProfile> userDialects) : YamlFieldReader(file, diags)
    {
        private void Bad(YamlNode at, string found) => Add(DiagnosticCatalog.DialectLockInvalid, at, found);

        public IReadOnlyList<DialectLockEntry> Read(YamlNode root)
        {
            var result = new List<DialectLockEntry>();
            if (root is not YamlMapping top) { Bad(root, $"{ProductInfo.DialectLockFile} must be a mapping with `format` and `connections`."); return result; }
            foreach (var e in top.Entries.Where(e => !TopKeys.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}`. Keys: {string.Join(", ", TopKeys)}.");
            if (top.Get("format") is not YamlScalar { Value: var fmt } || fmt != Format.ToString(System.Globalization.CultureInfo.InvariantCulture)) Bad(top.Get("format") ?? top, $"`format` must be {Format}: this file was written for another version of the tool, or by hand.");
            if (top.Get("connections") is not YamlMapping all) { Bad(top.Get("connections") ?? top, "`connections` must map connection names to what was found on their servers."); return result; }
            foreach (var c in all.Entries)
            {
                if (c.Value is not YamlMapping m) { Bad(c.Value, $"The entry for `{c.Key.Value}` must be a mapping."); continue; }
                if (Entry(c.Key.Value, c.Key, m) is { } entry) result.Add(entry);
            }
            return result;
        }

        private string? Text(YamlMapping m, string key, YamlNode at, bool required = true)
        {
            var node = m.Get(key);
            if (node == null) { if (required) Bad(at, $"`{key}` is missing."); return null; }
            if (node is YamlScalar { Value.Length: > 0 } s) return s.Value;
            Bad(node, $"`{key}` must be a non-empty value.");
            return null;
        }

        private DialectLockEntry? Entry(string connection, YamlNode at, YamlMapping m)
        {
            foreach (var e in m.Entries.Where(e => !EntryKeys.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}` in the entry for `{connection}`. Keys: {string.Join(", ", EntryKeys)}.");
            var server = Text(m, "server", at, required: false);
            var version = Text(m, "server_version", at, required: false);
            string? quote = null, idCase = null, quotedCase = null; bool? readOnly = null;
            if (m.Get("reported") is { } rep)
            {
                if (rep is not YamlMapping rm) Bad(rep, "`reported` must be a mapping.");
                else
                {
                    foreach (var e in rm.Entries.Where(e => !ReportedKeys.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}` in `reported`. Keys: {string.Join(", ", ReportedKeys)}.");
                    quote = Text(rm, "identifier_quote", at, required: false); idCase = Text(rm, "identifier_case", at, required: false); quotedCase = Text(rm, "quoted_identifier_case", at, required: false);
                    if (rm.Get("read_only") is { } ro) { if (ro is YamlScalar { Value: "true" or "false" } rs) readOnly = rs.Value == "true"; else Bad(ro, "`read_only` is `true` or `false`."); }
                }
            }
            var dialect = Text(m, "dialect", at);
            var hash = Text(m, "profile_hash", at);
            string? pQuote = null, pParameters = null, pPing = null, pCatalog = null, pCase = null, pLimit = null;
            if (m.Get("profile") is not YamlMapping pm) Bad(m.Get("profile") ?? at, "`profile` is missing or is not a mapping.");
            else
            {
                foreach (var e in pm.Entries.Where(e => !ProfileKeys.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}` in `profile`. Keys: {string.Join(", ", ProfileKeys)}.");
                (pQuote, pParameters, pPing, pCatalog, pCase, pLimit) = (Text(pm, "quote", at), Text(pm, "parameters", at), Text(pm, "ping", at), Text(pm, "catalog", at), Text(pm, "identifier_case", at), Text(pm, "limit", at));
            }
            if (dialect == null || hash == null || pQuote == null || pParameters == null || pPing == null || pCatalog == null || pCase == null || pLimit == null) return null;
            var entry = new DialectLockEntry(connection, new ServerFacts(server, version, quote, idCase, quotedCase, readOnly), dialect, pQuote, pParameters, pPing, pCatalog, pCase, pLimit, hash, at.Line);
            var profile = ProfileOf(entry, userDialects, out var problems);
            if (profile == null) { foreach (var p in problems) Bad(at, $"The entry for `{connection}`: {p}"); return null; }
            if (DialectDiscovery.Hash(profile) != hash) { Bad(m.Get("profile_hash") ?? at, $"The entry for `{connection}`: `profile_hash` is not the hash of its `profile`, so the entry was edited by hand."); return null; }
            return entry;
        }
    }

    /// <summary>
    /// Lays the committed dialects over a loaded configuration: each entry becomes the profile of its connection (and is kept on it, for the refresh's comparison). An entry for a connection that is not a Flight SQL
    /// connection without a dialect keyword is DDB-459 and ignored: an explicit keyword always wins. A missing or unreadable file changes nothing; a damaged one is DDB-457 and every connection stays `ansi`.
    /// </summary>
    public static ProjectConfig ApplyFromProject(string projectRoot, ProjectConfig config, List<Diagnostic> diags)
    {
        var path = Path.Combine(projectRoot, ProductInfo.DialectLockFile);
        if (!File.Exists(path)) return config;
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.DialectLockInvalid, new(ProductInfo.DialectLockFile, 0, 0), $"{ProductInfo.DialectLockFile} could not be read ({ex.GetType().Name})."));
            return config;
        }
        var entries = Parse(text, ProductInfo.DialectLockFile, diags, config.Dialects);
        if (entries == null) return config;
        return Apply(config, entries, diags);
    }

    public static ProjectConfig Apply(ProjectConfig config, IReadOnlyList<DialectLockEntry> entries, List<Diagnostic> diags)
    {
        var connections = config.Connections.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!connections.TryGetValue(entry.Connection, out var connection) || !connection.Kind.DialectIsDiscoverable)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.DialectLockStale, new(ProductInfo.DialectLockFile, entry.Line, 1),
                    connections.ContainsKey(entry.Connection) ? $"The entry for `{entry.Connection}` is not used: its `engine` ({connections[entry.Connection].Kind.Describe()}) names the dialect itself, or is not a Flight SQL connection."
                        : $"The entry for `{entry.Connection}` is not used: the project has no such connection."));
                continue;
            }
            var profile = ProfileOf(entry, config.Dialects, out _);
            if (profile != null) connections[entry.Connection] = connection with { Profile = profile, DialectLock = entry };
        }
        return config with { Connections = connections };
    }

    /// <summary>The Flight SQL connections without a dialect keyword and without an entry in the file (an unreadable file leaves every one of them without), among <paramref name="connectionNames"/>.</summary>
    public static IReadOnlyList<string> Missing(ProjectConfig config, IEnumerable<string> connectionNames) =>
        connectionNames.Distinct(StringComparer.Ordinal).Where(n => config.Connections.TryGetValue(n, out var c) && c.Kind.DialectIsDiscoverable && c.DialectLock == null).Order(StringComparer.Ordinal).ToList();
}
