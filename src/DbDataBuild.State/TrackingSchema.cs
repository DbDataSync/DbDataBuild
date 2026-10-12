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
    /// <summary>Layout version. 2 added `metadata_document` and its views; 3 made `metadata_columns` cover source descriptors too (a `kind` column); 4 keyed every record by the `connection` it describes, so one tracking store can hold any number of connections' records; `init` upgrades an older layout by creating what is missing; 5 told a deploy from a refresh and recorded the project's structure on each event (`lane`, `project_hash`); 6 added `object_info`, the title, description, tags and properties of the project, its connections, models, sources, columns and indexes as rows (the documents hold them as well).</summary>
    public const int Version = 6;

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
        new("metadata_document", "Project, source, model and plan metadata as JSON documents, for introspection with SQL. Append-only; the views metadata_current and metadata_columns show the latest.",
        [
            C("connection", TrackingType.Name),
            C("kind", TrackingType.Short), C("subject", TrackingType.Name), C("document", TrackingType.Json), C("document_hash", TrackingType.Hash),
            C("recorded_utc", TrackingType.TimestampUtc), C("tool_version", TrackingType.Short), C("plan_id", TrackingType.Short, true), C("git_commit", TrackingType.Short, true),
        ], ["connection", "kind", "subject", "recorded_utc"]),
    ];

    /// <summary>
    /// Columns that layout 5 added to tables that layout 4 has: `lane` (deploy or refresh) and `project_hash` (the structure the compiled project expected when a deploy ran) on each event. The init script adds them to a table that lacks them,
    /// so initializing a layout 4 store brings it to this one.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Column)> AddedInLayout5 = [("migration_log", "lane"), ("migration_log", "project_hash")];

    /// <summary>The table layout 6 added. The init script creates it when it is missing (a store of layout 5 is brought to 6 by `init`, which alters nothing), and the layout is recorded only when it is there.</summary>
    public const string AddedInLayout6 = "object_info";

    public static TrackingTable Table(string name) => Tables.First(t => t.Name == name);
}

/// <summary>
/// Where the records about one data connection are kept: the engine and schema name of its tracking store, and the name of the data connection, which is part of every record's key. The engine is the
/// tracking connection's (it can differ from the data connection's: that is what central tracking is).
/// </summary>
public sealed record TrackingScope(string Engine, string SchemaName, string Connection);
