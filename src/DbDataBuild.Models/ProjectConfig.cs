using DbDataBuild.Core;

namespace DbDataBuild.Models;

public enum CaseSensitivity { Sensitive, Insensitive }
public enum AccentSensitivity { Sensitive, Insensitive }
public enum TrailingSpace { Significant, Ignored }

/// <summary>
/// How a model's files are named (`model_layout`). The name of a model, `schema_name.object_name`, is always the `name:` in its definition file; the layout only says whether the files must spell it too, so a reader can
/// find a model from its name. `Folder`: `models/<schema name>/<object>.yml` (the path is the name; any depth: `models/a/b/c.yml` is `a.b.c`). `Dotted`: a file called `<schema name>.<object>.yml`, in any folder.
/// `Object`: a file called `<object>.yml` in any folder. `None`: no check (the name is the definition's, the file is anywhere).
/// </summary>
public enum ModelLayout { Folder, Dotted, Object, None }

/// <summary>String comparison profile (DESIGN.md 7.4). Logical collation name -> engine -> engine collation name.</summary>
public sealed record StringSemantics(
    CaseSensitivity Case,
    AccentSensitivity Accent,
    TrailingSpace TrailingSpace,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Collations)
{
    public string Describe() =>
        $"case={Case.ToString().ToLowerInvariant()}, accent={Accent.ToString().ToLowerInvariant()}, trailing_space={TrailingSpace.ToString().ToLowerInvariant()}";
}

/// <summary>What a connection says about string comparison instead of the project (`connections.<name>.string_semantics`): each field it leaves out is the project's, and a collation entry replaces the project's for that logical name and engine.</summary>
public sealed record StringSemanticsOverride(CaseSensitivity? Case, AccentSensitivity? Accent, TrailingSpace? TrailingSpace, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Collations);

/// <summary>Policy keys: the linter findings whose severity a project may change.</summary>
public static class PolicyKeys
{
    public const string Approximated = "approximated";
    public const string Emulated = "emulated";
    public const string Unverified = "unverified";
    public const string NotCovered = "not_covered";
    public const string NativeDefinitionChanged = "native_definition_changed";
    public const string HistoryInconsistency = "history_inconsistency";
    public static readonly IReadOnlyList<string> All = [Approximated, Emulated, Unverified, NotCovered, NativeDefinitionChanged, HistoryInconsistency];
}

/// <summary>
/// A named database endpoint: an engine (the SQL dialect: sqlserver, fabric, postgres) and an optional version of it. Its logins come from the environment (`DBDATABUILD_&lt;NAME&gt;_READ` and `_WRITE`),
/// never from the configuration. A connection named after an engine (`sqlserver`) exists without being declared; declaring it sets its version.
/// </summary>
/// <param name="Version">The T-SQL level (or major version) the tool generates for: SQL Server 2022 and 2025 at compatibility level 160 are 16, 2025 at 170 is 17.</param>
public sealed record ConnectionConfig(string Name, string Engine, int? Version = null, int Line = 0, IReadOnlyDictionary<string, ParameterValue>? DeclaredParameters = null, ConnectionTracking? Tracking = null, bool AllowNativeCommands = false, StringSemanticsOverride? Semantics = null, LifecycleOverride? Lifecycle = null, ObjectInfo? DeclaredInfo = null)
{
    /// <summary>The connection's own `timezone:` (an IANA name), which wins over the project's for the clock of its resolvers.</summary>
    public string? Timezone { get; init; }

    /// <summary>What `engine` said, parsed once: access, driver and dialect. <see cref="Engine"/> is its driver (`sqlserver`, `postgres`, `fabric` for the engines the tool builds on; `duckdb`, `odbc`, `flightsql` for origins).</summary>
    public ConnectionKind? DeclaredKind { get; init; }

    public ConnectionKind Kind => DeclaredKind ?? ConnectionKind.OfEngine(Engine);

    /// <summary>The project's own dialect profile (`dialects:`) that this connection's `engine` names; null when the dialect is a built-in.</summary>
    public DialectProfile? Profile { get; init; }

    /// <summary>The dialect `project update` found on the server and committed (`dialects.lock.yml`) for a Flight SQL connection with no dialect keyword; its profile is <see cref="Profile"/>. Null when nothing is committed.</summary>
    public DialectLockEntry? DialectLock { get; init; }

    /// <summary>The connection is only read: no write login, never a destination, a tracking connection or a target of a deploy.</summary>
    public bool ReadOnly => Kind.ReadOnly;

    /// <summary>How many sessions a refresh may have open at this connection at once (1 to 64); null is no limit. At an origin it counts the sessions that read rows for a copy, at the destination the loads that run together.</summary>
    public int? MaxParallel { get; init; }

    /// <summary>The connection's title, description, tags and properties (its own: the project's are not inherited).</summary>
    public ObjectInfo Info => DeclaredInfo ?? ObjectInfo.Empty;

    /// <summary>The values this connection carries (`parameters:`): what differs between connections of one application, such as a store id. Referenced as `${connection.name}`, or `${origin.name}` by a copy that reads from it.</summary>
    public IReadOnlyDictionary<string, ParameterValue> Parameters => DeclaredParameters ?? new Dictionary<string, ParameterValue>();

    /// <summary>The names of connections the tool knows without a declaration: one per engine, named after it.</summary>
    public static IReadOnlyDictionary<string, ConnectionConfig> Implicit { get; } =
        TargetNames.All.ToDictionary(e => e, e => new ConnectionConfig(e, e), StringComparer.Ordinal);
}

/// <summary>
/// The project's `tracking:` section: the connection that keeps the records of what the tool built (null: none is configured, which is "nothing is tracked", with a warning) and the schema name they live in there.
/// <see cref="Disabled"/> is the explicit choice `tracking: none`: not tracked, and no warning.
/// </summary>
public sealed record TrackingConfig(string? Connection, string SchemaName, bool Disabled = false)
{
    public static TrackingConfig Default { get; } = new(null, ProductInfo.TrackingSchemaName);
}

/// <summary>A connection's own `tracking:`: `none` (an explicit opt-out), or another tracking connection and, optionally, schema name.</summary>
public sealed record ConnectionTracking(bool None, string? Connection, string? SchemaName);

/// <summary>Where the records about one data connection are kept, resolved: the tracking connection, its engine, and the schema name there.</summary>
public sealed record TrackingTarget(string Connection, string Engine, string SchemaName);

/// <summary>What a connection's tracking resolved to. <see cref="Target"/> is null when nothing is tracked; <see cref="Explicit"/> says that was a choice (`tracking: none`), so no warning is due.</summary>
public sealed record TrackingResolution(TrackingTarget? Target, bool Explicit);

/// <summary>Project configuration (<c>dbdatabuild.yml</c>). Offline settings only: credentials never live here (DESIGN.md 9.2).</summary>
public sealed record ProjectConfig(
    IReadOnlyList<string> DefaultConnections,
    IReadOnlyDictionary<string, ConnectionConfig> Connections,
    TrackingConfig Tracking,
    StringSemantics StringSemantics,
    IReadOnlyDictionary<string, Severity> Policy,
    IReadOnlyDictionary<string, int>? SourceLines = null,
    IReadOnlyDictionary<string, IReadOnlyList<HookDefinition>>? DeclaredHookGroups = null,
    bool StoreMetadataOnApply = false,
    bool LoweringEnabled = true,
    bool LintIndexes = true,
    bool LintSlices = true,
    RewriteSettings? Rewrites = null)
{
    /// <summary>
    /// How strings compare on one connection: the project's `string_semantics`, with what the connection's own `string_semantics` says laid over it (a field it leaves out is the project's; a collation entry
    /// replaces the project's for that logical name and engine). SQL Server ignores trailing spaces in a comparison and PostgreSQL keeps them, so a project that uses both says so per connection.
    /// </summary>
    public StringSemantics SemanticsOf(string connection)
    {
        if (!Connections.TryGetValue(connection, out var c) || c.Semantics is not { } o) return StringSemantics;
        var collations = StringSemantics.Collations.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(kv.Value), StringComparer.Ordinal);
        foreach (var (logical, engines) in o.Collations ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
        {
            var merged = collations.TryGetValue(logical, out var existing) ? new Dictionary<string, string>(existing) : new Dictionary<string, string>();
            foreach (var (engine, name) in engines) merged[engine] = name;
            collations[logical] = merged;
        }
        return new StringSemantics(o.Case ?? StringSemantics.Case, o.Accent ?? StringSemantics.Accent, o.TrailingSpace ?? StringSemantics.TrailingSpace, collations);
    }

    /// <summary>
    /// The ways the given connections compare strings differently: for each of `case`, `accent` and `trailing_space` on which they do not all agree, each connection's value. Empty when they agree (one connection
    /// always does).
    /// </summary>
    public IReadOnlyList<(string Dimension, IReadOnlyList<(string Connection, string Value)> Values)> StringProfileDifferences(IReadOnlyList<string> connections)
    {
        var profiles = connections.Distinct(StringComparer.Ordinal).Select(c => (Connection: c, Semantics: SemanticsOf(c))).ToList();
        var result = new List<(string, IReadOnlyList<(string, string)>)>();
        void Add(string dimension, Func<StringSemantics, string> value)
        {
            var values = profiles.Select(p => (p.Connection, value(p.Semantics))).ToList();
            if (values.Select(v => v.Item2).Distinct().Count() > 1) result.Add((dimension, values));
        }
        Add("case", s => s.Case.ToString().ToLowerInvariant());
        Add("accent", s => s.Accent.ToString().ToLowerInvariant());
        Add("trailing_space", s => s.TrailingSpace.ToString().ToLowerInvariant());
        return result;
    }

    /// <summary>This configuration as one connection sees it: the same, with that connection's string semantics (what DDL and the collation checks read).</summary>
    public ProjectConfig ForConnection(string connection) => Connections.TryGetValue(connection, out var c) && c.Semantics != null ? this with { StringSemantics = SemanticsOf(connection) } : this;

    /// <summary>The project's own title, description, tags and properties (top-level keys of dbdatabuild.yml; `defaults:` holds the model settings and is a different thing).</summary>
    public ObjectInfo? DeclaredInfo { get; init; }

    public ObjectInfo Info => DeclaredInfo ?? ObjectInfo.Empty;

    /// <summary>The lifecycle settings of the project (`plans:`, `refresh:`, `retention:`): the built-in defaults when the file says nothing.</summary>
    public LifecycleSettings Lifecycle { get; init; } = LifecycleSettings.Default;

    /// <summary>The refresh plans the project declares (`refresh.plans`), by name. Empty when it declares none, in which case a model may name any plan; `default` is always valid.</summary>
    public IReadOnlyDictionary<string, RefreshPlanSettings> RefreshPlans { get; init; } = new Dictionary<string, RefreshPlanSettings>();

    /// <summary>The project's `timezone:`: the zone an `expression` resolver's `current_date` and `now()` mean (an IANA name). Null: UTC.</summary>
    public string? Timezone { get; init; }

    /// <summary>The zone the clock of a resolver on <paramref name="connection"/> means: the connection's own `timezone:`, else the project's, else UTC. The machine's zone is never used.</summary>
    public string TimezoneOf(string connection) => (Connections.TryGetValue(connection, out var c) ? c.Timezone : null) ?? Timezone ?? "UTC";

    /// <summary>The lifecycle settings in force on one connection: the project's with what the connection says laid over them.</summary>
    public LifecycleSettings LifecycleOf(string connection) => Connections.TryGetValue(connection, out var c) && c.Lifecycle is { } own ? own.Apply(Lifecycle) : Lifecycle;

    /// <summary>How the files of a model must be named (`model_layout`; the default checks that the path is the name, as before).</summary>
    public ModelLayout Layout { get; init; } = ModelLayout.Folder;

    /// <summary>The tags of the tests that `plan`, `check` and `run` run first (`tests: { gate: { tags: [...] } }`); a failure of one refuses the plan. Empty: no gate.</summary>
    public IReadOnlyList<string> TestGateTags { get; init; } = [];

    /// <summary>Named, ordered sets of hooks that models reference with `use:` (`hook_groups:` in dbdatabuild.yml).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HookDefinition>> HookGroups => DeclaredHookGroups ?? new Dictionary<string, IReadOnlyList<HookDefinition>>();

    /// <summary>1-based lines in dbdatabuild.yml of settings that were present, keyed by dotted path (for diagnostics). Empty for defaults.</summary>
    public IReadOnlyDictionary<string, int> Lines { get; } = SourceLines ?? new Dictionary<string, int>();

    /// <summary>The `defaults:` section of dbdatabuild.yml, the first layer of every model's settings (null when there is none). Its nodes are positions in dbdatabuild.yml.</summary>
    public Yaml.YamlMapping? Defaults { get; init; }

    /// <summary>The project's own `parameters:` (the root file's; the folder files above a model override them for it). Referenced as `${project.name}`.</summary>
    public IReadOnlyDictionary<string, ParameterValue> Parameters { get; init; } = new Dictionary<string, ParameterValue>();

    /// <summary>The schema name of the tracking tables (the project's; a connection may name another connection but keeps this schema name unless it says its own).</summary>
    public string TrackingSchemaName => Tracking.SchemaName;

    /// <summary>
    /// Where the records about <paramref name="connection"/> are kept: its own `tracking:`, else the project's. Nothing is tracked unless a project says where (`tracking: { connection: audit }`); `tracking: none`
    /// is the explicit opt-out, and the only difference between the two is that the first earns a warning.
    /// </summary>
    public TrackingResolution TrackingOf(string connection)
    {
        Connections.TryGetValue(connection, out var c);
        if (c?.Tracking is { } own)
        {
            if (own.None) return new(null, true);
            var name = own.Connection ?? Tracking.Connection;
            return name != null && Connections.TryGetValue(name, out var t) ? new(new TrackingTarget(name, t.Engine, own.SchemaName ?? Tracking.SchemaName), true) : new(null, false);
        }
        if (Tracking.Disabled) return new(null, true);
        return Tracking.Connection is { } p && Connections.TryGetValue(p, out var pc) ? new(new TrackingTarget(p, pc.Engine, Tracking.SchemaName), true) : new(null, false);
    }

    /// <summary>The project's own dialect profiles (`dialects:`), resolved against what they extend, by name.</summary>
    public IReadOnlyDictionary<string, DialectProfile> Dialects { get; init; } = new Dictionary<string, DialectProfile>();

    /// <summary>The SQL dialect an origin takes: the built-in or the project's profile that the connection's `engine` names. A connection the project does not have is read as the engine of that name.</summary>
    public DialectProfile DialectOf(string connection)
    {
        if (Connections.TryGetValue(connection, out var withProfile) && withProfile.Profile != null) return withProfile.Profile;
        var name = Connections.TryGetValue(connection, out var c) ? c.Kind.Dialect : connection;
        return Dialects.TryGetValue(name, out var own) ? own : DbDataBuild.Models.DialectProfiles.BuiltIn.TryGetValue(name, out var builtIn) ? builtIn : DbDataBuild.Models.DialectProfiles.Ansi;
    }

    /// <summary>Whether a connection is only read. A connection the project does not have is not.</summary>
    public bool IsReadOnly(string connection) => Connections.TryGetValue(connection, out var c) && c.ReadOnly;

    /// <summary>The engine of a connection, or null when the project has no such connection.</summary>
    public string? EngineOf(string connection) => Connections.TryGetValue(connection, out var c) ? c.Engine : null;

    /// <summary>The version a connection is configured with, per connection (two servers of one engine can differ).</summary>
    public IReadOnlyDictionary<string, int> TargetVersions => Connections.Where(c => c.Value.Version != null).ToDictionary(c => c.Key, c => c.Value.Version!.Value, StringComparer.Ordinal);

    /// <summary>Built-in defaults, used when no file exists. Printed in every command header so they are never hidden.</summary>
    public static readonly ProjectConfig Default = new(
        [TargetNames.SqlServer],
        ConnectionConfig.Implicit,
        TrackingConfig.Default,
        new StringSemantics(CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored,
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["default"] = new Dictionary<string, string>
                {
                    ["duckdb"] = "NOCASE",
                    ["sqlserver"] = "Latin1_General_100_CI_AS",
                    ["fabric"] = "Latin1_General_100_CI_AS_KS_WS_SC_UTF8", // [VERIFY] DESIGN.md 7.4
                },
            }),
        new Dictionary<string, Severity>
        {
            [PolicyKeys.Approximated] = Severity.Warning,
            [PolicyKeys.Emulated] = Severity.Note,
            [PolicyKeys.Unverified] = Severity.Warning,
            [PolicyKeys.NotCovered] = Severity.Warning,
            [PolicyKeys.NativeDefinitionChanged] = Severity.Warning,
            [PolicyKeys.HistoryInconsistency] = Severity.Warning,
        });

    public string Describe() =>
        $"default connections: {string.Join(", ", DefaultConnections)}; string semantics: {StringSemantics.Describe()}" +
        string.Concat(Connections.Where(c => c.Value.Semantics != null).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $" (on {c.Key}: {SemanticsOf(c.Key).Describe()})")) + "; " +
        $"target versions: {(TargetVersions.Count == 0 ? "not set" : string.Join(", ", TargetVersions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key} {v.Value}")))}" +
        (Lifecycle == LifecycleSettings.Default ? "" : $"; {Lifecycle.Describe()}") +
        (Connections.Any(c => !ConnectionConfig.Implicit.ContainsKey(c.Key) || c.Value.Engine != c.Key || c.Value.ReadOnly)
            ? $"; connections: {string.Join(", ", Connections.Where(c => !ConnectionConfig.Implicit.ContainsKey(c.Key) || c.Value.Engine != c.Key || c.Value.ReadOnly).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} ({c.Value.Kind.Describe()})"))}" : "");
}
