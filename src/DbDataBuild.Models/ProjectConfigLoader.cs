using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>Loads <c>dbdatabuild.yml</c> with the strict YAML rules. Keys that are absent take the built-in default; nothing is inferred.</summary>
public static class ProjectConfigLoader
{
    private static readonly string[] TopKeys = ["defaults", "parameters", "connections", "tracking", "string_semantics", "policy", "hook_groups", "dialects", "metadata", "lowering", "lint", "rewrites", "model_layout", "tests", "plans", "refresh", "retention", "timezone", .. YamlFieldReader.InfoKeys];
    private static readonly string[] SemanticsKeys = ["case", "accent", "trailing_space", "collations"];
    private static readonly string[] ConnectionKeys = ["engine", "version", "parameters", "tracking", "allow_native_commands", "max_parallel", "string_semantics", "plans", "refresh", "timezone", .. YamlFieldReader.InfoKeys];
    private static readonly string[] CollationEngines = ["duckdb", "sqlserver", "fabric", "postgres"];

    /// <summary>Loads the project's config. A missing file yields the defaults and a DDB-109 warning; a bad file yields errors and the defaults.</summary>
    public static ProjectConfig LoadFromProject(string projectRoot, List<Diagnostic> diags)
    {
        var path = Path.Combine(projectRoot, ProductInfo.ConfigFile);
        if (!File.Exists(path))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.ConfigNotFound, new(ProductInfo.ConfigFile, 0, 0),
                $"`{ProductInfo.ConfigFile}` was not found under {projectRoot}. Built-in defaults apply."));
            return ProjectConfig.Default;
        }
        var loaded = Load(File.ReadAllText(path), ProductInfo.ConfigFile, diags) ?? ProjectConfig.Default;
        return DialectLockFile.ApplyFromProject(projectRoot, loaded, diags);      // the dialects `project update` committed for Flight SQL connections that name none
    }

    public static ProjectConfig? Load(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null) return diags.Count > before ? null : ProjectConfig.Default; // an empty file means all defaults
        var cfg = new Reader(file, diags).Read(root);
        return diags.Count > before ? null : cfg;
    }

    private sealed class Reader(string file, List<Diagnostic> diags) : YamlFieldReader(file, diags)
    {
        private readonly Dictionary<string, int> lines = [];

        /// <summary>The connections whose `engine` says `readonly`, found before the connections are read in full (a tracking setting may name a connection declared further down).</summary>
        private IReadOnlySet<string> readOnlyConnections = new HashSet<string>();

        private static HashSet<string> ReadOnlyNames(YamlMapping top)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (top.Get("connections") is not YamlMapping all) return names;
            foreach (var e in all.Entries)
            {
                if (e.Value is not YamlMapping m || m.Get("engine") is not { } en) continue;
                var words = en is YamlScalar s ? ConnectionKeywords.Words(s.Value) : en is YamlSequence q ? q.Items.OfType<YamlScalar>().SelectMany(i => ConnectionKeywords.Words(i.Value)).ToList() : [];
                if (words.Contains(ConnectionKeywords.ReadOnlyKeyword)) names.Add(e.Key.Value);
            }
            return names;
        }

        public ProjectConfig? Read(YamlNode root)
        {
            if (root is not YamlMapping top)
            {
                Add(DiagnosticCatalog.InvalidValue, root, $"{ProductInfo.ConfigFile} must be a mapping of settings.");
                return null;
            }
            CheckKeys(top, TopKeys, "the configuration");
            var d = ProjectConfig.Default;
            readOnlyConnections = ReadOnlyNames(top);

            var dialects = ReadDialects(top);
            var connections = ReadConnections(top, dialects);
            var (declared, defaults) = ReadDefaults(top, connections.Keys.ToHashSet(StringComparer.Ordinal));
            IReadOnlyList<string> targets = declared ?? d.DefaultConnections;
            var tracking = ReadTracking(top, connections.Keys.ToHashSet(StringComparer.Ordinal)) ?? d.Tracking;
            var semantics = ReadSemantics(top, d.StringSemantics);
            var policy = ReadPolicy(top, d.Policy);
            return new ProjectConfig(targets, connections, tracking, semantics, policy, lines, ReadHookGroups(top, connections.Keys.ToHashSet(StringComparer.Ordinal)), ReadMetadata(top), ReadLowering(top), ReadLint(top, "indexes"), ReadLint(top, "slices"), ReadRewrites(top)) { Defaults = defaults, Parameters = ReadParameters(top, "`parameters`") ?? new Dictionary<string, ParameterValue>(), Layout = ReadLayout(top), TestGateTags = ReadTestGate(top), Lifecycle = ReadLifecycle(top), RefreshPlans = ReadRefreshPlans(top), Timezone = ReadTimezone(top, "`timezone`"), DeclaredInfo = ReadInfo(top), Dialects = dialects };
        }

        // ---- dialects ----

        private static readonly string[] DialectKeys = ["extends", "quote", "parameters", "ping", "catalog", "identifier_case", "limit", "types"];

        /// <summary>
        /// `dialects:` maps names to profiles (docs/research/read-origins.md section 5): what an origin needs to know about the SQL it takes. A profile starts from what it `extends` (a built-in or another profile of the
        /// project; `ansi` when it says nothing) and changes the fields it names. A name may not be a keyword of `engine`, so the classes cannot collide.
        /// </summary>
        private Dictionary<string, DialectProfile> ReadDialects(YamlMapping top)
        {
            var result = new Dictionary<string, DialectProfile>(StringComparer.Ordinal);
            if (top.Get("dialects") is not { } node) return result;
            if (node is not YamlMapping all) { Add(DiagnosticCatalog.DialectProfileInvalid, node, "`dialects` must map dialect names to profiles."); return result; }
            var declared = all.Entries.GroupBy(e => e.Key.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var resolving = new HashSet<string>(StringComparer.Ordinal);
            var failed = new HashSet<string>(StringComparer.Ordinal);

            DialectProfile? Resolve(string name)
            {
                if (result.TryGetValue(name, out var done)) return done;
                if (failed.Contains(name)) return null;
                if (!declared.TryGetValue(name, out var entry)) return DialectProfiles.BuiltIn.GetValueOrDefault(name);
                if (!resolving.Add(name)) { Add(DiagnosticCatalog.DialectProfileInvalid, entry.Key, $"The dialect `{name}` extends itself, directly or through others."); failed.Add(name); return null; }
                try
                {
                    var profile = Build(entry);
                    if (profile != null) result[name] = profile; else failed.Add(name);
                    return profile;
                }
                finally { resolving.Remove(name); }
            }

            DialectProfile? Build(YamlEntry entry)
            {
                var name = entry.Key.Value;
                var where = $"`dialects.{name}`";
                if (ConnectionKeywords.Reserved.Contains(name)) { Add(DiagnosticCatalog.DialectProfileInvalid, entry.Key, $"{where}: `{name}` is a keyword of `engine` and cannot name a dialect.", $"Reserved: {string.Join(", ", ConnectionKeywords.Reserved)}."); return null; }
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9_]*$")) { Add(DiagnosticCatalog.DialectProfileInvalid, entry.Key, $"{where}: `{name}` is not a valid dialect name.", "Lower-case letters, digits and underscores, starting with a letter."); return null; }
                if (entry.Value is not YamlMapping settings) { Add(DiagnosticCatalog.DialectProfileInvalid, entry.Value, $"{where} must be a mapping (for example `{{ extends: ansi, quote: '`' }}`)."); return null; }
                CheckKeys(settings, DialectKeys, where);
                string? Text(string key)
                {
                    if (settings.Get(key) is not { } n) return null;
                    if (n is YamlScalar { Value.Length: > 0 } sc) return sc.Value;
                    Add(DiagnosticCatalog.DialectProfileInvalid, n, $"{where}.{key} must be a non-empty string.");
                    return null;
                }
                var extends = Text("extends");
                var baseProfile = DialectProfiles.Ansi;
                if (extends != null)
                {
                    var b = Resolve(extends);
                    if (b == null)
                    {
                        if (!declared.ContainsKey(extends)) Add(DiagnosticCatalog.DialectProfileInvalid, settings.Get("extends")!, $"{where}.extends is `{extends}`, which is not a dialect.", $"Built in: {string.Join(", ", DialectProfiles.BuiltIn.Keys.Order(StringComparer.Ordinal))}; or another entry of `dialects:`.");
                        return null;
                    }
                    baseProfile = b;
                }
                Dictionary<string, string>? types = null;
                if (settings.Get("types") is { } tn)
                {
                    if (tn is not YamlMapping tm) Add(DiagnosticCatalog.DialectProfileInvalid, tn, $"{where}.types must map the catalog's type names to logical types (`int8: BIGINT`).");
                    else
                    {
                        types = [];
                        foreach (var te in tm.Entries)
                        {
                            if (te.Value is YamlScalar { Value.Length: > 0 } tv) types[te.Key.Value] = tv.Value;
                            else Add(DiagnosticCatalog.DialectProfileInvalid, te.Value, $"{where}.types.{te.Key.Value} must be a logical type name.");
                        }
                    }
                }
                var (profile, problems) = DialectProfiles.Build(name, baseProfile, extends, Text("quote"), Text("parameters"), Text("ping"), Text("catalog"), Text("identifier_case"), Text("limit"), types);
                foreach (var problem in problems) Add(DiagnosticCatalog.DialectProfileInvalid, entry.Key, $"{where}: {problem}");
                return profile;
            }

            foreach (var e in all.Entries) Resolve(e.Key.Value);
            return result;
        }

        // ---- plans, refresh, retention ----

        /// <summary>`timezone: Europe/Berlin`: an IANA zone name the machine knows. It decides what `current_date` and `now()` mean in an `expression` resolver; without it they mean UTC, never the machine's own zone.</summary>
        private string? ReadTimezone(YamlMapping m, string where)
        {
            if (m.Get("timezone") is not { } node) return null;
            if (node is YamlScalar { Value.Length: > 0 } s && Timezones.IsKnown(s.Value)) return s.Value;
            Add(DiagnosticCatalog.InvalidValue, node, $"{where} is {(node is YamlScalar sc ? $"`{sc.Value}`" : "not a name")}.", "An IANA time zone name such as `UTC`, `Europe/Berlin` or `America/New_York`.");
            return null;
        }

        private TEnum? ReadChoice<TEnum>(YamlMapping m, string key, string where) where TEnum : struct, Enum
        {
            if (m.Get(key) is not { } node) return null;
            if (node is YamlScalar s && s.Value == s.Value.ToLowerInvariant() && System.Enum.TryParse<TEnum>(s.Value, ignoreCase: true, out var value) && System.Enum.IsDefined(value)) return value;
            Add(DiagnosticCatalog.InvalidValue, node, $"`{where}.{key}` is {(node is YamlScalar sc ? $"`{sc.Value}`" : "not a string")}.", $"One of: {string.Join(", ", System.Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()))}.");
            return null;
        }

        private bool? ReadFlag(YamlMapping m, string key, string where)
        {
            if (m.Get(key) is not { } node) return null;
            if (node is YamlScalar { Value: "true" or "false" } s) return s.Value == "true";
            Add(DiagnosticCatalog.InvalidValue, node, $"`{where}.{key}` must be true or false (lowercase).");
            return null;
        }

        /// <summary>
        /// `plans: { deploy: { keep, audit, require_clean_tree }, refresh: { keep, audit } }` and `refresh: { check, on_fail }`, at the project or inside a connection. <paramref name="scope"/> is the path to the
        /// mapping for messages: empty at the top, `connections.dev` inside a connection.
        /// </summary>
        private LifecycleOverride? ReadLifecycleOverride(YamlMapping m, string scope)
        {
            string W(string name) => scope.Length == 0 ? name : scope + "." + name;
            PlanKeep? deployKeep = null, refreshKeep = null; AuditLevel? deployAudit = null, refreshAudit = null; bool? clean = null; RefreshCheck? check = null; OnFail? onFail = null;
            if (m.Get("plans") is { } plansNode)
            {
                if (plansNode is not YamlMapping plans) Add(DiagnosticCatalog.InvalidValue, plansNode, $"`{W("plans")}` must be a mapping with `deploy` and `refresh`.");
                else
                {
                    CheckKeys(plans, ["deploy", "refresh"], $"`{W("plans")}`");
                    if (plans.Get("deploy") is { } d)
                    {
                        if (d is not YamlMapping dm) Add(DiagnosticCatalog.InvalidValue, d, $"`{W("plans.deploy")}` must be a mapping (`keep`, `audit`, `require_clean_tree`).");
                        else
                        {
                            CheckKeys(dm, ["keep", "audit", "require_clean_tree"], $"`{W("plans.deploy")}`");
                            deployKeep = ReadChoice<PlanKeep>(dm, "keep", W("plans.deploy")); deployAudit = ReadChoice<AuditLevel>(dm, "audit", W("plans.deploy"));
                            clean = ReadFlag(dm, "require_clean_tree", W("plans.deploy"));
                        }
                    }
                    if (plans.Get("refresh") is { } r)
                    {
                        if (r is not YamlMapping rm) Add(DiagnosticCatalog.InvalidValue, r, $"`{W("plans.refresh")}` must be a mapping (`keep`, `audit`).");
                        else
                        {
                            CheckKeys(rm, ["keep", "audit"], $"`{W("plans.refresh")}`");
                            refreshKeep = ReadChoice<PlanKeep>(rm, "keep", W("plans.refresh")); refreshAudit = ReadChoice<AuditLevel>(rm, "audit", W("plans.refresh"));
                        }
                    }
                }
            }
            if (m.Get("refresh") is { } refreshNode)
            {
                if (refreshNode is not YamlMapping rf) Add(DiagnosticCatalog.InvalidValue, refreshNode, $"`{W("refresh")}` must be a mapping (`check`, `on_fail`).");
                else
                {
                    CheckKeys(rf, scope.Length == 0 ? ["check", "on_fail", "plans"] : ["check", "on_fail"], $"`{W("refresh")}`");
                    check = ReadChoice<RefreshCheck>(rf, "check", W("refresh")); onFail = ReadChoice<OnFail>(rf, "on_fail", W("refresh"));
                }
            }
            var result = new LifecycleOverride(deployKeep, deployAudit, refreshKeep, refreshAudit, clean, check, onFail);
            return result == new LifecycleOverride() ? null : result;
        }

        /// <summary>
        /// `refresh: { plans: { hourly: { title, description, tags, properties, on_failure, on_busy, max_wait, max_runtime, expected_every, parallel, check, on_fail } } }`: the refresh plans the project declares, so a model that names another is
        /// told. A plan may be an empty mapping (`daily: {}`) or null. The rules are validated and kept in the compiled plan file.
        /// </summary>
        private Dictionary<string, RefreshPlanSettings> ReadRefreshPlans(YamlMapping top)
        {
            var result = new Dictionary<string, RefreshPlanSettings>(StringComparer.Ordinal);
            if ((top.Get("refresh") as YamlMapping)?.Get("plans") is not { } node) return result;
            if (node is not YamlMapping plans) { Add(DiagnosticCatalog.InvalidValue, node, "`refresh.plans` must be a mapping of plan names to their settings, for example `plans: { hourly: { expected_every: 1h } }`."); return result; }
            foreach (var e in plans.Entries)
            {
                var name = e.Key.Value;
                if (!RefreshPlanSettings.NameForm.IsMatch(name)) { Add(DiagnosticCatalog.InvalidValue, e.Key, $"`{name}` is not a refresh plan name.", "Lowercase snake_case, such as `hourly` or `daily`."); continue; }
                if (e.Value is not YamlMapping m)
                {
                    if (e.Value is YamlScalar { Quoted: false, Value: "" or "null" or "~" }) { result[name] = new(name, ObjectInfo.Empty, null, null, null, null, null, null, e.Key.Line); continue; }
                    Add(DiagnosticCatalog.InvalidValue, e.Value, $"`refresh.plans.{name}` must be a mapping of settings (`{{}}` for none).");
                    continue;
                }
                CheckKeys(m, [.. YamlFieldReader.InfoKeys, "on_failure", "on_busy", "expected_every", "parallel", "check", "on_fail", "max_wait", "max_runtime"], $"`refresh.plans.{name}`");
                var where = $"refresh.plans.{name}";
                string? Word(string key, IReadOnlyList<string> allowed)
                {
                    if (m.Get(key) is not { } n) return null;
                    if (n is YamlScalar s && allowed.Contains(s.Value)) return s.Value;
                    Add(DiagnosticCatalog.InvalidValue, n, $"`{where}.{key}` is {(n is YamlScalar sc ? $"`{sc.Value}`" : "not a single word")}.", $"One of: {string.Join(", ", allowed)}.");
                    return null;
                }
                var onFailure = Word("on_failure", OnFailureModes.All);
                var onBusy = Word("on_busy", OnBusyModes.All);
                EveryDuration? every = null;
                if (m.Get("expected_every") is { } en)
                {
                    every = en is YamlScalar es ? EveryDuration.TryParse(es.Value) : null;
                    if (every == null) Add(DiagnosticCatalog.InvalidValue, en, $"`{where}.expected_every` is not a span.", "A whole number and a unit, for example `15m`, `1h`, `1d` or `1w`.");
                }
                EveryDuration? Span(string key)
                {
                    if (m.Get(key) is not { } sn) return null;
                    var span = sn is YamlScalar ss ? EveryDuration.TryParse(ss.Value) : null;
                    if (span == null) Add(DiagnosticCatalog.InvalidValue, sn, $"`{where}.{key}` is not a span.", "A whole number and a unit, for example `30s`, `15m`, `1h` or `1d`.");
                    return span;
                }
                var maxWait = Span("max_wait");
                var maxRuntime = Span("max_runtime");
                int? parallel = null;
                if (m.Get("parallel") is { } pn)
                {
                    if (pn is YamlScalar ps && int.TryParse(ps.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 64) parallel = count;
                    else Add(DiagnosticCatalog.InvalidValue, pn, $"`{where}.parallel` must be a whole number from 1 to 64.");
                }
                var check = ReadChoice<RefreshCheck>(m, "check", where);
                var onFail = ReadChoice<OnFail>(m, "on_fail", where);
                result[name] = new RefreshPlanSettings(name, ReadInfo(m) ?? ObjectInfo.Empty, onFailure, onBusy, every, parallel, check, onFail, e.Key.Line, maxWait, maxRuntime);
            }
            return result;
        }

        private LifecycleSettings ReadLifecycle(YamlMapping top)
        {
            var settings = ReadLifecycleOverride(top, "")?.Apply(LifecycleSettings.Default) ?? LifecycleSettings.Default;
            if (top.Get("retention") is { } node)
            {
                if (node is not YamlMapping r) Add(DiagnosticCatalog.InvalidValue, node, "`retention` must be a mapping (`statement_logs_days`).");
                else
                {
                    CheckKeys(r, ["statement_logs_days"], "`retention`");
                    if (r.Get("statement_logs_days") is { } days)
                    {
                        if (days is YamlScalar s && int.TryParse(s.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) settings = settings with { StatementLogDays = n };
                        else Add(DiagnosticCatalog.InvalidValue, days, "`retention.statement_logs_days` must be a whole number of days (0 keeps them all).");
                    }
                }
            }
            return settings;
        }

        private bool ReadLint(YamlMapping top, string key)
        {
            if (top.Get("lint") is not { } node) return true;
            if (node is not YamlMapping m) { if (key == "indexes") Add(DiagnosticCatalog.InvalidValue, node, "`lint` must be a mapping."); return true; }
            if (key == "indexes") CheckKeys(m, ["indexes", "slices"], "`lint`");
            if (m.Get(key) is not { } v) return true;
            if (v is YamlScalar s && s.Value is "true" or "false") return s.Value == "true";
            Add(DiagnosticCatalog.InvalidValue, v, $"`lint.{key}` must be true or false (lowercase).");
            return true;
        }

        private IReadOnlyList<string> ReadTestGate(YamlMapping top)
        {
            if (top.Get("tests") is not { } node) return [];
            if (node is not YamlMapping tests) { Add(DiagnosticCatalog.InvalidValue, node, "`tests` must be a mapping."); return []; }
            CheckKeys(tests, ["gate"], "`tests`");
            if (tests.Get("gate") is not { } gateNode) return [];
            if (gateNode is not YamlMapping gate) { Add(DiagnosticCatalog.InvalidValue, gateNode, "`tests.gate` must be a mapping with `tags`."); return []; }
            CheckKeys(gate, ["tags"], "`tests.gate`");
            if (gate.Get("tags") is not { } tagsNode) { Add(DiagnosticCatalog.MissingKey, gate, "`tests.gate` needs `tags`: the tags of the tests that must pass before a plan is made."); return []; }
            if (tagsNode is not YamlSequence { Items.Count: > 0 } list || list.Items.Any(i => i is not YamlScalar { Value.Length: > 0 }))
            { Add(DiagnosticCatalog.InvalidValue, tagsNode, "`tests.gate.tags` must be a list of tags (the `tags` of tests under tests/)."); return []; }
            return list.Items.Cast<YamlScalar>().Select(i => i.Value).Distinct(StringComparer.Ordinal).ToList();
        }

        private ModelLayout ReadLayout(YamlMapping top)
        {
            if (top.Get("model_layout") is not { } node) return ModelLayout.Folder;
            var names = new Dictionary<string, ModelLayout> { ["folder"] = ModelLayout.Folder, ["dotted"] = ModelLayout.Dotted, ["object"] = ModelLayout.Object, ["none"] = ModelLayout.None };
            if (node is YamlScalar s && names.TryGetValue(s.Value, out var layout)) return layout;
            Add(DiagnosticCatalog.InvalidValue, node, $"`model_layout` is {(node is YamlScalar sc ? $"`{sc.Value}`" : "not a string")}.",
                "`folder` (schema_name/object_name.yml), `dotted` (schema_name.object_name.yml), `object` (object.yml) or `none` (the files may be named anything).");
            return ModelLayout.Folder;
        }

        private bool ReadLowering(YamlMapping top)
        {
            if (top.Get("lowering") is not { } node) return true;
            if (node is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, node, "`lowering` must be a mapping."); return true; }
            CheckKeys(m, ["enabled"], "`lowering`");
            if (m.Get("enabled") is not { } v) return true;
            if (v is YamlScalar s && s.Value is "true" or "false") return s.Value == "true";
            Add(DiagnosticCatalog.InvalidValue, v, "`lowering.enabled` must be true or false (lowercase).");
            return true;
        }

        private bool ReadMetadata(YamlMapping top)
        {
            if (top.Get("metadata") is not { } node) return false;
            if (node is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, node, "`metadata` must be a mapping."); return false; }
            CheckKeys(m, ["store_on_apply"], "`metadata`");
            if (m.Get("store_on_apply") is not { } v) return false;
            if (v is YamlScalar s && s.Value is "true" or "false") return s.Value == "true";
            Add(DiagnosticCatalog.InvalidValue, v, "`metadata.store_on_apply` must be true or false (lowercase).");
            return false;
        }

        private Dictionary<string, IReadOnlyList<HookDefinition>> ReadHookGroups(YamlMapping top, IReadOnlySet<string> connections)
        {
            var result = new Dictionary<string, IReadOnlyList<HookDefinition>>(StringComparer.Ordinal);
            if (top.Get("hook_groups") is not { } node) return result;
            if (node is not YamlMapping groups) { Add(DiagnosticCatalog.InvalidValue, node, "`hook_groups` must map group names to lists of hooks."); return result; }
            foreach (var e in groups.Entries)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(e.Key.Value, @"^[A-Za-z_][A-Za-z0-9_\-]*$")) { Add(DiagnosticCatalog.InvalidValue, e.Key, $"`{e.Key.Value}` is not a valid hook group name."); continue; }
                result[e.Key.Value] = HookReader.ReadList(e.Value, allowUse: false, $"the hook group `{e.Key.Value}`", (d, n, f) => Add(d, n, f), connections);
            }
            return result;
        }

        /// <summary>
        /// `defaults:` holds the model settings every model inherits from this file (<see cref="ModelDefinitionLoader.LayeredKeys"/>); a folder's `_dbdatabuild.yml` has the same section. Only the shape is checked here,
        /// the values are checked where they are merged into a model. The connections are read as well, for the commands that need to know where a model with no list of its own lives.
        /// </summary>
        private (List<string>? Connections, YamlMapping? Defaults) ReadDefaults(YamlMapping top, IReadOnlySet<string> connections)
        {
            if (top.Get("defaults") is not { } node) return (null, null);
            if (node is not YamlMapping map) { Add(DiagnosticCatalog.InvalidValue, node, "`defaults` must be a mapping of model settings."); return (null, null); }
            foreach (var e in map.Entries.Where(e => !ModelDefinitionLoader.LayeredKeys.Contains(e.Key.Value.TrimEnd('=', '-', '+'))))
                Add(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in `defaults`.", $"Keys: {string.Join(", ", ModelDefinitionLoader.LayeredKeys.Order(StringComparer.Ordinal))}.");
            List<string>? list = null;
            if (map.Entries.FirstOrDefault(e => e.Key.Value is "connections" or "connections=" or "connections+") is { } c)
            {
                if (c.Value is not YamlSequence seq || seq.Items.Count == 0 || seq.Items.Any(i => i is not YamlScalar { Value.Length: > 0 }))
                    Add(DiagnosticCatalog.InvalidValue, c.Value, "`connections` must be a non-empty list of connection names, for example `connections: [warehouse]`.");
                else
                {
                    list = [];
                    foreach (var t in seq.Items.Cast<YamlScalar>())
                    {
                        if (list.Contains(t.Value)) Add(DiagnosticCatalog.InvalidValue, t, $"`{t.Value}` is listed twice.");
                        else if (!connections.Contains(t.Value)) Add(DiagnosticCatalog.InvalidValue, t, $"Unknown connection `{t.Value}`.", $"One of: {string.Join(", ", connections.Order(StringComparer.Ordinal))}.");
                        list.Add(t.Value);
                    }
                }
            }
            return (list, map);
        }

        private Dictionary<string, ParameterValue>? ReadParameters(YamlMapping owner, string where) =>
            owner.Get("parameters") is { } node ? ParameterReferences.Read(node, where, (d, n, f) => Add(d, n, f)) : null;

        /// <summary>
        /// `connections:` maps names to `{ engine, version }`. A connection named after an engine needs no entry (and may only be declared with that engine, to set its version); any other name needs `engine`.
        /// Names become the environment variables of the logins (`DBDATABUILD_&lt;NAME&gt;_READ`), so they are letters, digits and underscores, and two names that differ only in case would share one.
        /// </summary>
        private Dictionary<string, ConnectionConfig> ReadConnections(YamlMapping top, IReadOnlyDictionary<string, DialectProfile> dialects)
        {
            var result = new Dictionary<string, ConnectionConfig>(ConnectionConfig.Implicit, StringComparer.Ordinal);
            if (top.Get("connections") is not { } node) return result;
            if (node is not YamlMapping connections) { Add(DiagnosticCatalog.InvalidValue, node, "`connections` must map connection names to settings."); return result; }
            var upper = new Dictionary<string, string>(StringComparer.Ordinal);
            var declared = new HashSet<string>(TargetNames.All.Concat(connections.Entries.Select(x => x.Key.Value)), StringComparer.Ordinal);     // a tracking connection may be declared later in the file
            foreach (var e in connections.Entries)
            {
                var name = e.Key.Value;
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*$"))
                { Add(DiagnosticCatalog.InvalidValue, e.Key, $"`{name}` is not a valid connection name.", "Letters, digits and underscores, starting with a letter: the name is part of the login's environment variable."); continue; }
                if (upper.TryGetValue(name.ToUpperInvariant(), out var other))
                { Add(DiagnosticCatalog.DuplicateKey, e.Key, $"The connection names `{other}` and `{name}` differ only in case and would share the login variable `DBDATABUILD_{name.ToUpperInvariant()}_READ`."); continue; }
                upper[name.ToUpperInvariant()] = name;
                if (e.Value is not YamlMapping settings) { Add(DiagnosticCatalog.InvalidValue, e.Value, $"`connections.{name}` must be a mapping (for example `{{ engine: postgres }}`)."); continue; }
                CheckKeys(settings, ConnectionKeys, $"`connections.{name}`");
                var isEngineName = TargetNames.All.Contains(name);
                string? engine = isEngineName ? name : null;
                ConnectionKind? kind = null;
                if (settings.Get("engine") is { } en)
                {
                    var words = en is YamlScalar { Value.Length: > 0 } es ? ConnectionKeywords.Words(es.Value)
                        : en is YamlSequence { Items.Count: > 0 } seq && seq.Items.All(i => i is YamlScalar { Value.Length: > 0 }) ? seq.Items.Cast<YamlScalar>().SelectMany(i => ConnectionKeywords.Words(i.Value)).ToList() : null;
                    if (words == null) Add(DiagnosticCatalog.InvalidValue, en, "`engine` is a list of keywords: a string such as `readonly postgres`, or a YAML list.", EngineChoices(dialects));
                    else
                    {
                        var (parsed, problem, fix) = ConnectionKeywords.Parse(words, dialects.Keys.ToHashSet(StringComparer.Ordinal));
                        if (parsed == null) Add(DiagnosticCatalog.InvalidValue, en, problem!, fix);
                        else if (!ConnectionKeywords.IsBuilt(parsed.Driver)) Add(DiagnosticCatalog.InvalidValue, en, $"The driver `{parsed.Driver}` is listed but not built yet in this release.", $"Built: {string.Join(", ", ConnectionKeywords.Drivers.Where(ConnectionKeywords.IsBuilt))}.");
                        else if (isEngineName && parsed.Driver != name) Add(DiagnosticCatalog.InvalidValue, en, $"The connection `{name}` is named after an engine, so its driver is `{name}`, not `{parsed.Driver}`.", "Give the connection another name.");
                        else
                        {
                            engine = parsed.Driver; kind = parsed;
                            if (ProfileOf(parsed, dialects) is { Catalog: CatalogStyle.Odbc } && parsed.Driver != "odbc")
                                Add(DiagnosticCatalog.DialectProfileInvalid, en, $"The dialect `{parsed.Dialect}` says `catalog: odbc`, which reads the catalog with an ODBC driver's functions, and the connection `{name}` is `{parsed.Driver}`.", "Use a dialect with `catalog: information_schema` (or `none`) for this connection; Flight SQL reads its own metadata calls without a setting.");
                        }
                    }
                }
                else if (!isEngineName) Add(DiagnosticCatalog.MissingKey, e.Key, $"The connection `{name}` needs an `engine`.", EngineChoices(dialects));
                int? version = null;
                if (settings.Get("version") is { } v)
                {
                    if (v is YamlScalar s && int.TryParse(s.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0) version = n;
                    else Add(DiagnosticCatalog.InvalidValue, v, "`version` must be the engine's major version as a positive integer (SQL Server 2022 is 16, 2025 is 17).");
                }
                var parameters = ReadParameters(settings, $"`connections.{name}.parameters`");
                var ownTracking = ReadConnectionTracking(settings, name, declared);
                var allowCommands = false;
                if (settings.Get("allow_native_commands") is { } ac)
                {
                    if (ac is YamlScalar { Value: "true" or "false" } acs) allowCommands = acs.Value == "true";
                    else Add(DiagnosticCatalog.InvalidValue, ac, "`allow_native_commands` is `true` or `false` (lowercase).");
                    if (allowCommands && kind is { IsBuildTarget: false }) { Add(DiagnosticCatalog.InvalidValue, ac, $"`allow_native_commands` is for SQL Server and PostgreSQL connections; `{kind.Describe()}` is read with SELECTs only."); allowCommands = false; }
                }
                int? maxParallel = null;
                if (settings.Get("max_parallel") is { } mp)
                {
                    if (mp is YamlScalar mps && int.TryParse(mps.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var cap) && cap is >= 1 and <= 64) maxParallel = cap;
                    else Add(DiagnosticCatalog.InvalidValue, mp, $"`connections.{name}.max_parallel` must be a whole number from 1 to 64.");
                }
                if (engine != null) result[name] = new ConnectionConfig(name, engine, version, e.Key.Line, parameters, ownTracking, allowCommands, ReadSemanticsOverride(settings, name), ReadLifecycleOverride(settings, $"connections.{name}"), ReadInfo(settings)) { Timezone = ReadTimezone(settings, $"`connections.{name}.timezone`"), MaxParallel = maxParallel, DeclaredKind = kind, Profile = ProfileOf(kind, dialects) };
            }
            return result;
        }

        /// <summary>The profile of a connection that reads through a driver of its own: the project's profile or the built-in one the keyword names, as that driver uses it (<see cref="DialectProfiles.ForDriver"/>); null for the others.</summary>
        private static DialectProfile? ProfileOf(ConnectionKind? kind, IReadOnlyDictionary<string, DialectProfile> dialects)
        {
            if (kind == null) return null;
            if (dialects.TryGetValue(kind.Dialect, out var own)) return DialectProfiles.ForDriver(kind.Driver, own);
            if (kind.Driver is not ("odbc" or "flightsql") || !DialectProfiles.BuiltIn.TryGetValue(kind.Dialect, out var builtIn)) return null;
            var adjusted = DialectProfiles.ForDriver(kind.Driver, builtIn);
            return ReferenceEquals(adjusted, builtIn) ? null : adjusted;      // `ansi` and `duckdb` are used as they are
        }

        private static string EngineChoices(IReadOnlyDictionary<string, DialectProfile> dialects) =>
            $"Keywords of `engine`, in any order. Access: readonly. Driver: {string.Join(", ", ConnectionKeywords.Drivers)}. Dialect: {string.Join(", ", ConnectionKeywords.BuiltInDialects.Concat(dialects.Keys.Order(StringComparer.Ordinal)))}.";

        private static readonly System.Text.RegularExpressions.Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$");

        /// <summary>`tracking: { connection: audit, schema: dbdatabuild }`, or `tracking: none`. The connection must be one the project has; the schema name a plain identifier.</summary>
        private TrackingConfig? ReadTracking(YamlMapping top, IReadOnlySet<string> connections)
        {
            if (top.Get("tracking") is not { } node) return null;
            if (node is YamlScalar { Value: "none" }) return new TrackingConfig(null, ProductInfo.TrackingSchemaName, Disabled: true);
            var t = ReadTrackingMapping(node, connections, "`tracking`");
            return t == null ? null : new TrackingConfig(t.Value.Connection, t.Value.SchemaName ?? ProductInfo.TrackingSchemaName);
        }

        private (string? Connection, string? SchemaName)? ReadTrackingMapping(YamlNode node, IReadOnlySet<string> connections, string where)
        {
            if (node is not YamlMapping map) { Add(DiagnosticCatalog.InvalidValue, node, $"{where} is `none`, or a mapping with `connection` and `schema`."); return null; }
            CheckKeys(map, ["connection", "schema"], where);
            string? connection = null, schema = null;
            if (map.Get("connection") is { } c)
            {
                if (c is YamlScalar { Value.Length: > 0 } rs && readOnlyConnections.Contains(rs.Value))
                    Add(DiagnosticCatalog.ReadOnlyConnectionNotWritable, c, $"{where}.connection is `{rs.Value}`, which is `readonly`: the tool never writes there, and tracking tables are written.", fix: "Name a connection that is not readonly, or use `tracking: none`.");
                else if (c is YamlScalar { Value.Length: > 0 } cs && connections.Contains(cs.Value)) connection = cs.Value;
                else Add(DiagnosticCatalog.InvalidValue, c, $"{where}.connection is not a connection of this project.", $"One of: {string.Join(", ", connections.Order(StringComparer.Ordinal))}.");
            }
            if (map.Get("schema") is { } s)
            {
                if (s is YamlScalar ss && Identifier.IsMatch(ss.Value)) schema = ss.Value;
                else Add(DiagnosticCatalog.InvalidValue, s, $"{where}.schema must be a plain identifier.", "Letters, digits and underscores, not starting with a digit.");
            }
            return (connection, schema);
        }

        /// <summary>A connection's own `tracking:`: `none`, or another tracking connection (and schema).</summary>
        private ConnectionTracking? ReadConnectionTracking(YamlMapping settings, string name, IReadOnlySet<string> connections)
        {
            if (settings.Get("tracking") is not { } node) return null;
            if (node is YamlScalar { Value: "none" }) return new ConnectionTracking(true, null, null);
            var t = ReadTrackingMapping(node, connections, $"`connections.{name}.tracking`");
            return t == null ? null : new ConnectionTracking(false, t.Value.Connection, t.Value.SchemaName);
        }

        private StringSemantics ReadSemantics(YamlMapping top, StringSemantics defaults)
        {
            if (top.Get("string_semantics") is not { } node) return defaults;
            if (node is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, node, "`string_semantics` must be a mapping."); return defaults; }
            CheckKeys(m, SemanticsKeys, "`string_semantics`");

            lines["string_semantics"] = m.Line;
            var @case = Enum(m, "case", defaults.Case);
            var accent = Enum(m, "accent", defaults.Accent);
            var trailing = Enum(m, "trailing_space", defaults.TrailingSpace);
            var collations = defaults.Collations;
            if (m.Get("collations") is { } cn)
            {
                var read = ReadCollations(cn);
                if (read != null) collations = read;
            }
            return new StringSemantics(@case, accent, trailing, collations);
        }

        /// <summary>A connection's own `string_semantics`: only what it says. The line numbers kept for the project's setting are left as they were.</summary>
        private StringSemanticsOverride? ReadSemanticsOverride(YamlMapping settings, string connection)
        {
            if (settings.Get("string_semantics") is not { } node) return null;
            if (node is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, node, $"`connections.{connection}.string_semantics` must be a mapping."); return null; }
            CheckKeys(m, SemanticsKeys, $"`connections.{connection}.string_semantics`");
            T? Option<T>(string key) where T : struct, System.Enum
            {
                if (m.Get(key) is not { } n) return null;
                var names = System.Enum.GetNames<T>().Select(x => x.ToLowerInvariant()).ToList();
                if (n is YamlScalar s && names.Contains(s.Value) && System.Enum.TryParse<T>(s.Value, ignoreCase: true, out var v)) return v;
                Add(DiagnosticCatalog.InvalidValue, n, $"`{key}` is {(n is YamlScalar sc ? $"`{sc.Value}`" : "not a string")}.", $"One of: {string.Join(", ", names)}.");
                return null;
            }
            var saved = lines.Where(kv => kv.Key.StartsWith("string_semantics", StringComparison.Ordinal)).ToList();      // ReadCollations records where the project's collations were written
            var collations = m.Get("collations") is { } cn ? ReadCollations(cn) : null;
            foreach (var key in lines.Keys.Where(k => k.StartsWith("string_semantics", StringComparison.Ordinal)).ToList()) lines.Remove(key);
            foreach (var (k, v) in saved) lines[k] = v;
            return new StringSemanticsOverride(Option<CaseSensitivity>("case"), Option<AccentSensitivity>("accent"), Option<TrailingSpace>("trailing_space"), collations?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        }

        private T Enum<T>(YamlMapping m, string key, T fallback) where T : struct, System.Enum
        {
            if (m.Get(key) is not { } node) return fallback;
            lines["string_semantics." + key] = node.Line;
            var names = System.Enum.GetNames<T>().Select(n => n.ToLowerInvariant()).ToList();
            if (node is YamlScalar s && names.Contains(s.Value) && System.Enum.TryParse<T>(s.Value, ignoreCase: true, out var v)) return v;
            Add(DiagnosticCatalog.InvalidValue, node, $"`{key}` is {(node is YamlScalar sc ? $"`{sc.Value}`" : "not a string")}.", $"One of: {string.Join(", ", names)}.");
            return fallback;
        }

        private Dictionary<string, IReadOnlyDictionary<string, string>>? ReadCollations(YamlNode node)
        {
            if (node is not YamlMapping logical) { Add(DiagnosticCatalog.InvalidValue, node, "`collations` must map logical names to per-engine collation names."); return null; }
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            lines["string_semantics.collations"] = logical.Line;
            foreach (var e in logical.Entries)
            {
                lines[$"string_semantics.collations.{e.Key.Value}"] = e.Key.Line;
                if (e.Value is not YamlMapping engines) { Add(DiagnosticCatalog.InvalidValue, e.Value, $"`collations.{e.Key.Value}` must map engines to collation names."); continue; }
                CheckKeys(engines, CollationEngines, $"`collations.{e.Key.Value}`");
                var perEngine = new Dictionary<string, string>();
                foreach (var ee in engines.Entries.Where(x => CollationEngines.Contains(x.Key.Value)))
                {
                    if (ee.Value is YamlScalar s && s.Value.Length > 0) { perEngine[ee.Key.Value] = s.Value; lines[$"string_semantics.collations.{e.Key.Value}.{ee.Key.Value}"] = ee.Key.Line; }
                    else Add(DiagnosticCatalog.InvalidValue, ee.Value, $"`collations.{e.Key.Value}.{ee.Key.Value}` must be a non-empty collation name.");
                }
                result[e.Key.Value] = perEngine;
            }
            return result;
        }

        private Dictionary<string, Severity> ReadPolicy(YamlMapping top, IReadOnlyDictionary<string, Severity> defaults)
        {
            var result = new Dictionary<string, Severity>(defaults);
            if (top.Get("policy") is not { } node) return result;
            if (node is not YamlMapping policy) { Add(DiagnosticCatalog.InvalidValue, node, "`policy` must be a mapping."); return result; }
            CheckKeys(policy, ["severity"], "`policy`");
            if (policy.Get("severity") is not { } sev) return result;
            if (sev is not YamlMapping sm) { Add(DiagnosticCatalog.InvalidValue, sev, "`policy.severity` must map finding kinds to a severity."); return result; }
            CheckKeys(sm, PolicyKeys.All, "`policy.severity`");
            foreach (var e in sm.Entries.Where(x => PolicyKeys.All.Contains(x.Key.Value)))
            {
                if (e.Value is YamlScalar s && s.Value is "error" or "warning" or "note")
                    result[e.Key.Value] = s.Value switch { "error" => Severity.Error, "warning" => Severity.Warning, _ => Severity.Note };
                else Add(DiagnosticCatalog.InvalidValue, e.Value, $"`policy.severity.{e.Key.Value}` must be error, warning or note.",
                    "`unsupported` findings are always errors and cannot be configured.");
            }
            return result;
        }
    }
}
