namespace DbDataBuild.State;

/// <summary>The few column types the tracking tables use. Each target maps them to its own native type (DESIGN.md 12).</summary>
public enum TrackingType { Name, Short, Hash, Long, BigInt, Int, Guid, TimestampUtc, Json }

public sealed record TrackingColumn(string Name, TrackingType Type, bool Nullable = false);

/// <param name="PrimaryKey">Column names. Every tracking table has one, so each target can create it without target-specific options.</param>
public sealed record TrackingTable(string Name, string Purpose, IReadOnlyList<TrackingColumn> Columns, IReadOnlyList<string> PrimaryKey);

/// <summary>
/// The tracking tables as one logical definition (DESIGN.md 12). Differences from the illustrative T-SQL there, all deliberate:
/// log identifiers are tool-generated GUIDs rather than IDENTITY (IDENTITY differs across engines and is unconfirmed on Fabric; order comes from the timestamps),
/// and a `tracking_version` table records the layout version so a later tool can detect and migrate an older layout.
/// </summary>
public static class TrackingSchema
{
    /// <summary>Layout version. 2 added `metadata_document` and its views; 3 made `metadata_columns` cover source descriptors too (a `kind` column); 4 keyed every record by the `connection` it describes, so one tracking store can hold any number of connections' records; `init` upgrades an older layout by creating what is missing; 5 told a deploy from a refresh and recorded the project's structure on each event (`lane`, `project_hash`); 6 added `object_info`, the title, description, tags and properties of the project, its connections, models, sources, columns and indexes as rows (the documents hold them as well); 7 recorded refresh plans: the plan, the selection and the plan's members as run on each event, and the membership, load plan, inputs with their sources, attempt and duration on each load row; 8 recorded the kind of each model beside its document (`model_kind` on `metadata_document`, and so on the view `metadata_current`) and what each model or native model depends on as rows (the table `metadata_dependency`); 9 made every model, mapped and native ones included, one subject kind (`model`; the subject kind of a stored document is `subject_kind`, as on `object_info`, and a mapped model's old `source` rows become `model`; the table `metadata_dependency` says what a dependency is with `depends_on_category` (model, macro, unknown) and the model kind of a model in `depends_on_kind`, null for the others).</summary>
    public const int Version = 9;

    private static TrackingColumn C(string n, TrackingType t, bool nullable = false) => new(n, t, nullable);

    public static readonly IReadOnlyList<TrackingTable> Tables =
    [
        new("tracking_version", "Layout version of these tables, so a later tool can detect and migrate an older layout.",
        [
            C("version", TrackingType.Int), C("tool_version", TrackingType.Short), C("applied_utc", TrackingType.TimestampUtc),
        ], ["version"]),
        new("schema_version", "Every distinct shape an object has had, and whether the tool or an out-of-band change produced it.",
        [
            C("connection", TrackingType.Name),
            C("object_name", TrackingType.Name), C("shape_hash", TrackingType.Hash), C("physical_hash", TrackingType.Hash, true),
            C("first_seen_utc", TrackingType.TimestampUtc), C("source", TrackingType.Short), C("plan_id", TrackingType.Short, true), C("git_commit", TrackingType.Short, true),
        ], ["connection", "object_name", "first_seen_utc"]),
        new("ddl_log", "Every DDL statement the tool issued, written before execution and updated with the outcome.",
        [
            C("connection", TrackingType.Name),
            C("ddl_id", TrackingType.Guid), C("object_name", TrackingType.Name), C("statement_hash", TrackingType.Hash), C("statement_text", TrackingType.Long),
            C("hash_before", TrackingType.Hash, true), C("hash_after", TrackingType.Hash, true), C("invoker", TrackingType.Name), C("plan_id", TrackingType.Short, true),
            C("git_commit", TrackingType.Short, true), C("executed_utc", TrackingType.TimestampUtc), C("status", TrackingType.Short),
        ], ["ddl_id"]),
        new("run_log", "One row per executed step: what ran, with which parameters, and the hashes around it.",
        [
            C("connection", TrackingType.Name),
            C("run_id", TrackingType.Guid), C("step_id", TrackingType.Short), C("model", TrackingType.Name), C("operation", TrackingType.Short),
            C("rows_affected", TrackingType.BigInt, true), C("status", TrackingType.Short), C("plan_id", TrackingType.Short, true), C("git_commit", TrackingType.Short, true),
            C("definition_hash", TrackingType.Hash, true), C("shape_hash_start", TrackingType.Hash, true), C("shape_hash_end", TrackingType.Hash, true),
            C("load_name", TrackingType.Short, true), C("load_file_hash", TrackingType.Hash, true), C("resolver_file_hash", TrackingType.Hash, true),
            C("parameters", TrackingType.Long, true), C("watermark_used", TrackingType.Short, true),
            C("started_utc", TrackingType.TimestampUtc), C("ended_utc", TrackingType.TimestampUtc, true),
            C("membership", TrackingType.Short, true), C("load_plan_hash", TrackingType.Hash, true), C("inputs", TrackingType.Long, true), C("attempt", TrackingType.Int, true), C("duration_ms", TrackingType.BigInt, true),
        ], ["run_id", "step_id"]),
        new("operation_interval", "Which range of data each load or backfill produced, under which shape.",
        [
            C("connection", TrackingType.Name),
            C("interval_id", TrackingType.Guid), C("model", TrackingType.Name), C("run_id", TrackingType.Guid), C("range_start", TrackingType.Short, true),
            C("range_end", TrackingType.Short, true), C("shape_hash", TrackingType.Hash), C("operation", TrackingType.Short),
        ], ["interval_id"]),
        new("block_log", "Blocks raised by planning and the human acknowledgement that cleared them.",
        [
            C("connection", TrackingType.Name),
            C("block_id", TrackingType.Guid), C("model", TrackingType.Name), C("code", TrackingType.Short), C("detail", TrackingType.Long),
            C("created_utc", TrackingType.TimestampUtc), C("ack_by", TrackingType.Name, true), C("ack_reason", TrackingType.Long, true), C("ack_utc", TrackingType.TimestampUtc, true),
        ], ["block_id"]),
        new("migration_log", "Every applied plan: its hash and text, so the target's audit trail is self-contained.",
        [
            C("connection", TrackingType.Name),
            C("plan_id", TrackingType.Short), C("plan_hash", TrackingType.Hash), C("plan_text", TrackingType.Long), C("git_commit", TrackingType.Short, true),
            C("applied_by", TrackingType.Name), C("applied_utc", TrackingType.TimestampUtc), C("hash_before", TrackingType.Hash, true), C("hash_after", TrackingType.Hash, true),
            C("status", TrackingType.Short), C("lane", TrackingType.Short, true), C("project_hash", TrackingType.Hash, true),
            C("refresh_plan", TrackingType.Short, true), C("selection", TrackingType.Long, true),
        ], ["connection", "plan_id", "applied_utc"]),
        new("plan_store", "Plans kept in the database (`plans.deploy.keep: database`): the text of a deploy plan, once per plan, for a plan made by one person or job and applied by another.",
        [
            C("connection", TrackingType.Name),
            C("plan_id", TrackingType.Short), C("plan_hash", TrackingType.Hash), C("plan_text", TrackingType.Long), C("stored_by", TrackingType.Name), C("stored_utc", TrackingType.TimestampUtc),
        ], ["connection", "plan_id"]),
        new("object_info", "What a person says about an object in words (title, description, tags, properties), one row each, taken from the metadata documents when they are published. Nothing here is written into a deployed object.",
        [
            C("connection", TrackingType.Name),
            C("subject_kind", TrackingType.Short), C("subject", TrackingType.Name), C("element", TrackingType.Name), C("item", TrackingType.Short), C("item_key", TrackingType.Short),
            C("value_type", TrackingType.Short, true), C("value", TrackingType.Long, true), C("recorded_utc", TrackingType.TimestampUtc),
        ], ["connection", "subject_kind", "subject", "element", "item", "item_key"]),
        new("refresh_plan_member", "The members of a refresh plan as they were when an event ran it, in order, and whether the run's selection included each: what was in `hourly` at that run.",
        [
            C("connection", TrackingType.Name),
            C("plan_id", TrackingType.Short), C("position", TrackingType.Int), C("membership", TrackingType.Short), C("model", TrackingType.Name), C("load_name", TrackingType.Short),
            C("load_plan_hash", TrackingType.Hash), C("selected", TrackingType.Short),
        ], ["connection", "plan_id", "position"]),
        new("metadata_document", "Project, model and plan metadata as JSON documents, for introspection with SQL. Append-only; the views metadata_current and metadata_columns show the latest.",
        [
            C("connection", TrackingType.Name),
            C("subject_kind", TrackingType.Short), C("subject", TrackingType.Name), C("document", TrackingType.Json), C("document_hash", TrackingType.Hash),
            C("recorded_utc", TrackingType.TimestampUtc), C("tool_version", TrackingType.Short), C("plan_id", TrackingType.Short, true), C("git_commit", TrackingType.Short, true),
            C("model_kind", TrackingType.Short, true),
        ], ["connection", "subject_kind", "subject", "recorded_utc"]),
        new("metadata_dependency", "What each model, and each native model that says what it reads, depends on, one row each: depth 1 is a direct edge, a greater depth is a thing it depends on through others (the shortest path); `depends_on_category` says what it is (model, macro, unknown) and `depends_on_kind` which of the seven model kinds when it is a model. Written with the documents when they are published, from the committed project; the views metadata_upstream and metadata_ancestors of the metadata rules read the same rows.",
        [
            C("connection", TrackingType.Name),
            C("subject", TrackingType.Name), C("depends_on", TrackingType.Name), C("depends_on_category", TrackingType.Short), C("depends_on_kind", TrackingType.Short, true), C("depth", TrackingType.Int),
        ], ["connection", "subject", "depends_on"]),
    ];

    /// <summary>
    /// Columns that layout 5 added to tables that layout 4 has: `lane` (deploy or refresh) and `project_hash` (the structure the compiled project expected when a deploy ran) on each event. The init script adds them to a table that lacks them,
    /// so initializing a layout 4 store brings it to this one.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> AddedInLayout5 = [("migration_log", "lane"), ("migration_log", "project_hash")];

    /// <summary>The table layout 6 added. The init script creates it when it is missing (a store of layout 5 is brought to 6 by `init`, which alters nothing), and the layout is recorded only when it is there.</summary>
    public const string AddedInLayout6 = "object_info";

    /// <summary>
    /// What layout 7 added to a store of layout 6: columns on tables it has (the init script never alters a table, so `init --upgrade` adds them), and the table of a plan's members (the init script creates it). The layout is
    /// recorded only when the last column and the table are there.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> AddedInLayout7 =
    [
        ("migration_log", "refresh_plan"), ("migration_log", "selection"),
        ("run_log", "membership"), ("run_log", "load_plan_hash"), ("run_log", "inputs"), ("run_log", "attempt"), ("run_log", "duration_ms"),
    ];

    public const string TableAddedInLayout7 = "refresh_plan_member";

    /// <summary>
    /// What layout 8 added to a store of layout 7: a column on a table it has (the init script never alters a table, so `init --upgrade` adds it) and the table of dependencies (the init script creates it). Documents stored before
    /// layout 8 have no `model_kind`; the next publish writes the documents again (their text changed with the model document's version), and with them the kind.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> AddedInLayout8 = [("metadata_document", "model_kind")];

    public const string TableAddedInLayout8 = "metadata_dependency";

    /// <summary>
    /// What layout 9 changed in a store of layout 8: the column `kind` of `metadata_document` is `subject_kind` (the init script never alters a table, so `init --upgrade` renames it), the rows of subject kind `source` (mapped and
    /// native models) are of subject kind `model` in `metadata_document` and `object_info`, and the views are made again. A document stored before keeps its old text until the next publish writes the model's again.
    /// </summary>
    public static readonly (string Table, string OldColumn, string Column) RenamedInLayout9 = ("metadata_document", "kind", "subject_kind");

    /// <summary>
    /// The other change of layout 9, in the table `metadata_dependency`: `depends_on_category` is new (what sort of thing the dependency is: model, macro, unknown) and `depends_on_kind` may be null (it is the model kind, so a macro or an unknown table has
    /// none). The upgrade empties the table (its rows are derived from the project and the next publish writes them again), makes `depends_on_kind` nullable and adds the column.
    /// </summary>
    public const string DependencyCategoryColumn = "depends_on_category";

    public static TrackingTable Table(string name) => Tables.First(t => t.Name == name);
}

/// <summary>
/// Where the records about one data connection are kept: the engine and schema name of its tracking store, and the name of the data connection, which is part of every record's key. The engine is the
/// tracking connection's (it can differ from the data connection's: that is what central tracking is).
/// </summary>
public sealed record TrackingScope(string Engine, string SchemaName, string Connection);
