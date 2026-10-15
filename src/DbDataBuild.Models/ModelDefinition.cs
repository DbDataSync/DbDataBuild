namespace DbDataBuild.Models;

public static class ModelKinds
{
    public const string View = "view";
    public const string Full = "full";
    public const string IncrementalByUniqueKey = "incremental_by_unique_key";
    public const string IncrementalByTimeRange = "incremental_by_time_range";

    /// <summary>A table filled by copying the rows of another model that lives on another connection: no query, the columns are the origin's, always persisted.</summary>
    public const string Copy = "copy";
    public static readonly IReadOnlyList<string> All = [View, Full, IncrementalByUniqueKey, IncrementalByTimeRange, Copy];

    /// <summary>A model that maps a table that exists: the tool declares its columns and never creates it.</summary>
    public const string Mapped = "mapped";

    /// <summary>A model whose rows an engine-native query computes: declared columns, never built.</summary>
    public const string Native = "native";

    /// <summary>The two kinds the tool declares and does not build.</summary>
    public static readonly IReadOnlyList<string> Declared = [Mapped, Native];

    /// <summary>All seven kinds, the built ones first.</summary>
    public static readonly IReadOnlyList<string> Every = [.. All, .. Declared];

    /// <summary>True for a kind the tool builds (it has a query or an origin to load from).</summary>
    public static bool IsBuilt(string kind) => All.Contains(kind);
}

public static class TargetNames
{
    public const string SqlServer = "sqlserver";
    public const string Fabric = "fabric";
    public const string Postgres = "postgres";
    public static readonly IReadOnlyList<string> All = [SqlServer, Fabric, Postgres];
}

/// <param name="Trimmed">Declared: no value of this column ends in a space. Trailing spaces are the one difference between engines that data can make go away (SQL Server ignores them in a comparison, PostgreSQL keeps them), so a column that has none compares the same everywhere; `check` counts the rows that break the declaration.</param>
public sealed record ColumnDefinition(string Name, string Type, bool Nullable = true, string? Collation = null, int Line = 0, int CollationLine = 0, bool Trimmed = false, ObjectInfo? Info = null);

public sealed record RenameDefinition(string From, string To);

/// <summary>
/// How a copy from several connections keeps each origin's rows apart: the rows of one origin are the rows whose <see cref="Column"/> has the origin's <see cref="Value"/>, and a run replaces only those. The value
/// may reference the origin's own parameters (`${origin.store_id}`) or the destination connection's (`${connection.region}`), so nothing is added to the data that the systems do not already say about themselves.
/// </summary>
/// <param name="Type">The column's type when the origin has no such column and the copy adds one (the value is written into every row of that origin).</param>
public sealed record CopySlice(string Column, string Value, string? Type = null, int Line = 0)
{
    public const string Fail = "fail";
    public const string Skip = "skip";
}

/// <summary>
/// An incremental copy: only the rows of the origin whose <see cref="Column"/> is at or after the newest value the destination holds (minus <see cref="Lookback"/>, for rows that arrive late or change) are read
/// and loaded, replacing the rows with the same unique key. The first run, with nothing in the destination, reads everything.
/// </summary>
public sealed record CopyWatermark(string Column, string? Lookback, int Line = 0);

public sealed record ModelDefinition(
    string Name,
    string Kind,
    IReadOnlyList<string> UniqueKey,
    string? TimeColumn,
    string? Lookback,
    IReadOnlyList<string> Grain,
    IReadOnlyList<string>? Targets,   // null: project default applies
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<RenameDefinition> Renames,
    IReadOnlyList<LoadOperation>? DeclaredLoads = null,
    IReadOnlyList<IndexDefinition>? DeclaredIndexes = null,
    IReadOnlyList<HookDefinition>? DeclaredHooks = null,
    IReadOnlyList<string>? DeclaredLintIgnore = null,
    RewriteSettings? Rewrites = null,
    string? From = null,
    int FromLine = 0,
    CopySlice? Slice = null,
    string OnMismatch = CopySlice.Fail,
    bool SliceColumnAdded = false,
    CopyWatermark? Watermark = null,
    IReadOnlyDictionary<string, ParameterValue>? DeclaredParameters = null,
    bool LocalCopy = false,
    ObjectInfo? DeclaredInfo = null,
    IReadOnlyList<RefreshMembership>? DeclaredRefresh = null)
{
    /// <summary>The refresh plans the model's loads are in (`refresh:`, merged down the project files like `tags`). Empty when it says nothing, which puts a model with a default load in the plan `default`.</summary>
    public IReadOnlyList<RefreshMembership> Refresh => DeclaredRefresh ?? [];

    /// <summary>The model's title, description, tags and properties. Tags and properties are the model's file merged onto the project files above it (`defaults:` of each, the nearest winning); the title and description are the model's own and never inherited.</summary>
    public ObjectInfo Info => DeclaredInfo ?? ObjectInfo.Empty;

    /// <summary>The model's tags (`tags:` in its file and in the project files above it, merged): labels that `tag:` selects by.</summary>
    public IReadOnlyList<string> Tags => Info.Tags;

    /// <summary>The model's own parameters (`parameters:` in its file, never inherited). Referenced as `${model.name}`.</summary>
    public IReadOnlyDictionary<string, ParameterValue> Parameters => DeclaredParameters ?? new Dictionary<string, ParameterValue>();

    /// <summary>True for a model that copies another one (<see cref="ModelKinds.Copy"/>): `From` is the model it copies.</summary>
    public bool IsCopy => Kind == ModelKinds.Copy;

    /// <summary>Diagnostic codes of advisory lints (DDB-223, DDB-224, DDB-225, DDB-236, DDB-239) the operator has silenced for this model (`lint_ignore:`).</summary>
    public IReadOnlyList<string> LintIgnore => DeclaredLintIgnore ?? [];

    public IReadOnlyList<IndexDefinition> Indexes => DeclaredIndexes ?? [];

    /// <summary>Hook entries in the order written (the order they run in); groups are expanded by <see cref="HookReader.Resolve"/>.</summary>
    public IReadOnlyList<HookDefinition> Hooks => DeclaredHooks ?? [];

    /// <summary>Declared load operations (the `loads:` block). Empty when the kind supplies its single default (see <see cref="LoadPlan"/>).</summary>
    public IReadOnlyList<LoadOperation> Loads => DeclaredLoads ?? [];
}
