using System.Data;
using DbDataBuild.Core;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

public enum TrackingState { Missing, Ready, UnknownLayout }

public sealed record TrackingStatus(TrackingState State, int? Version)
{
    /// <summary>The DDB-505 diagnostic for a state that is not ready, or null.</summary>
    public Diagnostic? AsDiagnostic(string schema) => State switch
    {
        TrackingState.Ready => null,
        TrackingState.Missing => new Diagnostic(DiagnosticCatalog.TrackingNotInitialized, new($"schema:{schema}", 0, 0), $"The tracking tables do not exist under the schema name `{schema}` on the target."),
        _ => new Diagnostic(DiagnosticCatalog.TrackingNotInitialized, new($"schema:{schema}", 0, 0),
            $"The tracking tables under the schema name `{schema}` are at layout version {(Version?.ToString() ?? "unreadable")}; this tool knows version {TrackingSchema.Version}. A layout older than 4 has no `connection` column in its records, and layout 4 has no `lane` or `project_hash`: `connection init --upgrade --apply` adds them (every existing record is given the connection that is initialized) and replaces the views. Layout 5 lacks the table `object_info`, layout 6 the columns and the table of the refresh plans (layout 7), and layout 7 the column `model_kind` of `metadata_document` and the table `metadata_dependency` (layout 8): the upgrade adds them (init alone only creates tables). Or name another schema with `tracking: {{ schema: ... }}` and run `init` there."),
    };
}

/// <summary>
/// Strictly increasing UTC timestamps at the tracking tables' millisecond precision, so two records for one object written in the same millisecond
/// still order (the newest `schema_version` row is found by timestamp).
/// </summary>
internal static class TrackingClock
{
    private static long last;
    /// <summary>The value is UTC wall-clock time with Kind Unspecified: the columns are zone-less (`datetime2`, `timestamp`) and Npgsql refuses UTC-kind values for them.</summary>
    public static DateTime NextUtc()
    {
        var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        long observed, next;
        do { observed = Interlocked.Read(ref last); next = Math.Max(now, observed + 1); } while (Interlocked.CompareExchange(ref last, next, observed) != observed);
        return new DateTime(next * TimeSpan.TicksPerMillisecond, DateTimeKind.Unspecified);
    }
}

/// <summary>Reads and writes the tracking tables. Reads use the read session, writes go through the gate as tracking statements.</summary>
public static class TrackingStore
{
    private static string Quote(string target, string id) => TrackingDdl.For(target).Quote(id);

    /// <summary>Runs the upgrade script of an older layout, then the init script, through the gate (`init --upgrade`).</summary>
    public static async Task UpgradeAsync(MutationGate gate, string target, string schema, string connection, CancellationToken ct = default)
    {
        foreach (var s in TrackingDdl.For(target).UpgradeScript(schema, connection))
            await gate.ExecuteAsync(GateStatement.Tracking(s.Id, s.Text), ct);
        await InitAsync(gate, target, schema, ct);
    }

    /// <summary>Runs the init script through the gate. Idempotent: a second run changes nothing.</summary>
    public static async Task InitAsync(MutationGate gate, string target, string schema, CancellationToken ct = default)
    {
        foreach (var s in TrackingDdl.For(target).InitScript(schema, ProductInfo.Version))
            await gate.ExecuteAsync(GateStatement.Tracking(s.Id, s.Text), ct);
    }

    public static async Task<TrackingStatus> StatusAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        // the version table is read as it is: asking the catalog whether it exists would be a metadata query, and a refresh asks the connection none
        var q = Quote(target, schema);
        IReadOnlyList<IReadOnlyList<object?>> rows;
        try { rows = await read.QueryAsync($"SELECT {Quote(target, "version")} FROM {q}.{Quote(target, "tracking_version")}", null, ct); }
        catch (Exception ex) when (IsMissingObject(ex)) { return new(TrackingState.Missing, null); }
        var versions = rows.Select(r => Convert.ToInt32(r[0])).ToList();
        if (versions.Count == 0) return new(TrackingState.UnknownLayout, null);
        var newest = versions.Max();
        return new(newest == TrackingSchema.Version ? TrackingState.Ready : TrackingState.UnknownLayout, newest);
    }

    /// <summary>The error a table or schema that is not there gives: SQL Server 208 (invalid object name), PostgreSQL 42P01 (undefined table) or 3F000 (invalid schema name). Read by name, as the driver's error is.</summary>
    private static bool IsMissingObject(Exception ex)
    {
        if (ex is not System.Data.Common.DbException) return false;
        return (ex.GetType().GetProperty("Number")?.GetValue(ex)?.ToString() == "208") || (ex.GetType().GetProperty("SqlState")?.GetValue(ex)?.ToString() is "42P01" or "3F000");
    }

    /// <summary>The shape hash of the newest `schema_version` row for each object, or no entry when the tool has never recorded it.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LatestShapeHashesAsync(ReadSession read, TrackingScope scope, CancellationToken ct = default)
    {
        string C(string n) => Quote(scope.Engine, n);
        var rows = await read.QueryAsync(
            $"SELECT sv.{C("object_name")}, sv.{C("shape_hash")} FROM {C(scope.SchemaName)}.{C("schema_version")} sv WHERE sv.{C("connection")} = @connection " +
            $"AND sv.{C("first_seen_utc")} = (SELECT MAX(x.{C("first_seen_utc")}) FROM {C(scope.SchemaName)}.{C("schema_version")} x WHERE x.{C("connection")} = sv.{C("connection")} AND x.{C("object_name")} = sv.{C("object_name")})",
            [new GateParameter("connection", DbType.String, scope.Connection)], ct);
        return rows.ToDictionary(r => (string)r[0]!, r => ((string)r[1]!).Trim());
    }

    /// <summary>Records a shape as the newest known state of an object. `source` is `tool` or `out_of_band` (DESIGN.md 12).</summary>
    public static Task RecordSchemaVersionAsync(MutationGate gate, TrackingScope scope, string stepId, string objectName, string shapeHash, string? physicalHash,
        string source, string? planId, string? gitCommit, CancellationToken ct = default)
    {
        string C(string n) => Quote(scope.Engine, n);
        var text = $"INSERT INTO {C(scope.SchemaName)}.{C("schema_version")} ({C("connection")}, {C("object_name")}, {C("shape_hash")}, {C("physical_hash")}, {C("first_seen_utc")}, {C("source")}, {C("plan_id")}, {C("git_commit")}) " +
                   "VALUES (@connection, @object_name, @shape_hash, @physical_hash, @first_seen_utc, @source, @plan_id, @git_commit)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            new("connection", DbType.String, scope.Connection), new("object_name", DbType.String, objectName), new("shape_hash", DbType.AnsiStringFixedLength, shapeHash), new("physical_hash", DbType.AnsiStringFixedLength, physicalHash),
            new("first_seen_utc", DbType.DateTime2, TrackingClock.NextUtc()), new("source", DbType.AnsiString, source), new("plan_id", DbType.AnsiString, planId), new("git_commit", DbType.AnsiString, gitCommit),
        ]), ct);
    }
}

/// <summary>What a refresh load row records about the plan it ran in: the membership, the load plan by hash, and the inputs with their sources as JSON.</summary>
public sealed record RefreshStepRecord(string Membership, string LoadPlanHash, string InputsJson);

/// <summary>What the tracking tables say about one plan, for refusing a double apply and for `apply --resume`.</summary>
/// <param name="MigrationStatuses">Statuses of its `migration_log` rows, oldest first (started, completed, failed).</param>
/// <param name="CompletedDdlHashes">Statement hashes of its DDL that finished `ok`.</param>
/// <param name="CompletedRunSteps">Step ids of its loads that finished `ok`.</param>
/// <param name="RecordedShapes">`schema_version` rows written by this plan: object and shape hash.</param>
public sealed record PlanProgress(IReadOnlyList<string> MigrationStatuses, IReadOnlySet<string> CompletedDdlHashes, IReadOnlySet<string> CompletedRunSteps, IReadOnlyList<(string Object, string ShapeHash)> RecordedShapes);

/// <summary>Writers and readers for the audit tables (DESIGN.md 12): migration, DDL and run logs. Writes go through the gate as tracking statements.</summary>
public static partial class AuditLog
{
    private static string C(string engine, string n) => TrackingDdl.For(engine).Quote(n);
    private static string T(TrackingScope scope, string table) => $"{C(scope.Engine, scope.SchemaName)}.{C(scope.Engine, table)}";
    private static GateParameter Connection(TrackingScope scope) => new("connection", DbType.String, scope.Connection);

    private static GateParameter S(string name, string? v, int length = 0) => new(name, v == null ? DbType.String : DbType.String, v);
    private static GateParameter A(string name, string? v) => new(name, DbType.AnsiString, v);
    private static GateParameter Fixed(string name, string? v) => new(name, DbType.AnsiStringFixedLength, v);

    public static Task MigrationAsync(MutationGate gate, TrackingScope scope, string stepId, string planId, string planHash, string planText, string? gitCommit, string appliedBy,
        string status, string? hashBefore, string? hashAfter, string? lane = null, string? projectHash = null, string? refreshPlan = null, string? selection = null, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"INSERT INTO {T(scope, "migration_log")} ({c("connection")}, {c("plan_id")}, {c("plan_hash")}, {c("plan_text")}, {c("git_commit")}, {c("applied_by")}, {c("applied_utc")}, {c("hash_before")}, {c("hash_after")}, {c("status")}, {c("lane")}, {c("project_hash")}, {c("refresh_plan")}, {c("selection")}) " +
                   "VALUES (@connection, @plan_id, @plan_hash, @plan_text, @git_commit, @applied_by, @applied_utc, @hash_before, @hash_after, @status, @lane, @project_hash, @refresh_plan, @selection)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            Connection(scope), A("plan_id", planId), Fixed("plan_hash", planHash), S("plan_text", planText), A("git_commit", gitCommit), S("applied_by", appliedBy),
            new("applied_utc", DbType.DateTime2, TrackingClock.NextUtc()), Fixed("hash_before", hashBefore), Fixed("hash_after", hashAfter), A("status", status), A("lane", lane), Fixed("project_hash", projectHash), A("refresh_plan", refreshPlan), S("selection", selection),
        ]), ct);
    }

    /// <summary>Keeps a deploy plan in the tracking tables (`plans.deploy.keep: database`). The plan's own hash covers its text, so a plan edited in the table is refused when it is read back.</summary>
    public static Task StorePlanAsync(MutationGate gate, TrackingScope scope, string stepId, string planId, string planHash, string planText, string storedBy, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"INSERT INTO {T(scope, "plan_store")} ({c("connection")}, {c("plan_id")}, {c("plan_hash")}, {c("plan_text")}, {c("stored_by")}, {c("stored_utc")}) VALUES (@connection, @plan_id, @plan_hash, @plan_text, @stored_by, @stored_utc)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            Connection(scope), A("plan_id", planId), Fixed("plan_hash", planHash), S("plan_text", planText), S("stored_by", storedBy), new("stored_utc", DbType.DateTime2, TrackingClock.NextUtc()),
        ]), ct);
    }

    /// <summary>The text of a plan kept in the database, or null when there is none of that id for the connection.</summary>
    public static async Task<string?> ReadStoredPlanAsync(ReadSession read, TrackingScope scope, string planId, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync($"SELECT p.{c("plan_text")} FROM {T(scope, "plan_store")} p WHERE p.{c("connection")} = @connection AND p.{c("plan_id")} = @plan_id", [Connection(scope), new GateParameter("plan_id", DbType.AnsiString, planId)], ct);
        return rows.Count == 0 ? null : (string?)rows[0][0];
    }

    /// <summary>One run of a plan: a deploy or a refresh. The rows of `migration_log` that share an id are its states (started, failed, completed); this is the last of them, with when the first was written.</summary>
    public sealed record EventRow(string Id, string Lane, string Status, DateTime StartedUtc, DateTime LastUtc, string By, string? Commit, string? ProjectHash);

    /// <summary>The newest <paramref name="count"/> events of a connection, newest first. A row written before layout 5 has no lane: an id that starts with `ref-` is a refresh, anything else a deploy.</summary>
    public static async Task<IReadOnlyList<EventRow>> EventsAsync(ReadSession read, TrackingScope scope, int count, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var cols = $"{c("plan_id")}, {c("lane")}, {c("status")}, {c("applied_by")}, {c("git_commit")}, {c("applied_utc")}, {c("project_hash")}";
        // a plan that stopped and continued has several rows; take enough rows to hold `count` events
        var limit = Math.Max(count * 6, 30);
        var sql = scope.Engine == "postgres"
            ? $"SELECT {cols} FROM {T(scope, "migration_log")} WHERE {c("connection")} = @connection ORDER BY {c("applied_utc")} DESC LIMIT {limit}"
            : $"SELECT TOP ({limit}) {cols} FROM {T(scope, "migration_log")} WHERE {c("connection")} = @connection ORDER BY {c("applied_utc")} DESC";
        var rows = await read.QueryAsync(sql, [Connection(scope)], ct);
        string Text(object? v) => (v as string)?.Trim() ?? "";
        DateTime When(object? v) => v is DateTime d ? d : DateTime.MinValue;
        return rows.GroupBy(r => Text(r[0])).Select(g =>
        {
            var newest = g.First();                                                   // the rows come newest first
            var lane = g.Select(r => Text(r[1])).FirstOrDefault(x => x.Length > 0) ?? (g.Key.StartsWith("ref-", StringComparison.Ordinal) ? "refresh" : "deploy");
            return new EventRow(g.Key, lane, Text(newest[2]), g.Min(r => When(r[5])), When(newest[5]), Text(newest[3]), Text(newest[4]) is { Length: > 0 } cm ? cm : null, g.Select(r => Text(r[6])).FirstOrDefault(x => x.Length > 0));
        }).OrderByDescending(e => e.LastUtc).Take(count).ToList();
    }

    /// <summary>The structure the compiled project expected when the connection was last deployed completely: the project hash and the id of that deploy event, or nulls when no completed deploy recorded one.</summary>
    public static async Task<(string? ProjectHash, string? EventId)> LastDeployedProjectAsync(ReadSession read, TrackingScope scope, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var filter = $"m.{c("connection")} = @connection AND m.{c("lane")} = 'deploy' AND m.{c("status")} = 'completed' AND m.{c("project_hash")} IS NOT NULL";
        var rows = await read.QueryAsync(
            $"SELECT m.{c("project_hash")}, m.{c("plan_id")} FROM {T(scope, "migration_log")} m WHERE {filter} AND m.{c("applied_utc")} = (SELECT MAX(m.{c("applied_utc")}) FROM {T(scope, "migration_log")} m WHERE {filter})",
            [Connection(scope)], ct);
        return rows.Count == 0 ? (null, null) : (((string)rows[0][0]!).Trim(), (string?)rows[0][1]);
    }

    public static Task BeginDdlAsync(MutationGate gate, TrackingScope scope, string stepId, Guid ddlId, string objectName, string statementText, string statementHash,
        string? hashBefore, string invoker, string planId, string? gitCommit, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"INSERT INTO {T(scope, "ddl_log")} ({c("connection")}, {c("ddl_id")}, {c("object_name")}, {c("statement_hash")}, {c("statement_text")}, {c("hash_before")}, {c("hash_after")}, {c("invoker")}, {c("plan_id")}, {c("git_commit")}, {c("executed_utc")}, {c("status")}) " +
                   "VALUES (@connection, @ddl_id, @object_name, @statement_hash, @statement_text, @hash_before, NULL, @invoker, @plan_id, @git_commit, @executed_utc, 'started')";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            Connection(scope), new("ddl_id", DbType.Guid, ddlId), S("object_name", objectName), Fixed("statement_hash", statementHash), S("statement_text", statementText), Fixed("hash_before", hashBefore),
            S("invoker", invoker), A("plan_id", planId), A("git_commit", gitCommit), new("executed_utc", DbType.DateTime2, TrackingClock.NextUtc()),
        ]), ct);
    }

    public static Task FinishDdlAsync(MutationGate gate, TrackingScope scope, string stepId, Guid ddlId, string status, string? hashAfter, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"UPDATE {T(scope, "ddl_log")} SET {c("status")} = @status, {c("hash_after")} = @hash_after WHERE {c("ddl_id")} = @ddl_id";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text, [A("status", status), Fixed("hash_after", hashAfter), new("ddl_id", DbType.Guid, ddlId)]), ct);
    }

    public static Task BeginRunAsync(MutationGate gate, TrackingScope scope, string stepId, Guid runId, string model, string operation, string planId, string? gitCommit,
        string? definitionHash, string? shapeStart, string? loadName, string? loadFileHash, string? resolverFileHash, string? parameters, string? watermarkUsed, RefreshStepRecord? refresh = null, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"INSERT INTO {T(scope, "run_log")} ({c("connection")}, {c("run_id")}, {c("step_id")}, {c("model")}, {c("operation")}, {c("rows_affected")}, {c("status")}, {c("plan_id")}, {c("git_commit")}, " +
                   $"{c("definition_hash")}, {c("shape_hash_start")}, {c("shape_hash_end")}, {c("load_name")}, {c("load_file_hash")}, {c("resolver_file_hash")}, {c("parameters")}, {c("watermark_used")}, {c("started_utc")}, {c("ended_utc")}, " +
                   $"{c("membership")}, {c("load_plan_hash")}, {c("inputs")}, {c("attempt")}, {c("duration_ms")}) " +
                   "VALUES (@connection, @run_id, @step_id, @model, @operation, NULL, 'started', @plan_id, @git_commit, @definition_hash, @shape_start, NULL, @load_name, @load_file_hash, @resolver_file_hash, @parameters, @watermark_used, @started_utc, NULL, @membership, @load_plan_hash, @inputs, @attempt, NULL)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            Connection(scope), new("run_id", DbType.Guid, runId), A("step_id", stepId), S("model", model), A("operation", operation), A("plan_id", planId), A("git_commit", gitCommit), Fixed("definition_hash", definitionHash),
            Fixed("shape_start", shapeStart), A("load_name", loadName), Fixed("load_file_hash", loadFileHash), Fixed("resolver_file_hash", resolverFileHash), S("parameters", parameters),
            A("watermark_used", watermarkUsed), new("started_utc", DbType.DateTime2, TrackingClock.NextUtc()),
            A("membership", refresh?.Membership), Fixed("load_plan_hash", refresh?.LoadPlanHash), S("inputs", refresh?.InputsJson), new("attempt", DbType.Int32, refresh == null ? null : 1),
        ]), ct);
    }

    public static Task FinishRunAsync(MutationGate gate, TrackingScope scope, string stepId, Guid runId, string status, long? rows, string? shapeEnd, long? durationMs = null, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"UPDATE {T(scope, "run_log")} SET {c("status")} = @status, {c("rows_affected")} = @rows, {c("shape_hash_end")} = @shape_end, {c("ended_utc")} = @ended_utc, {c("duration_ms")} = @duration_ms WHERE {c("run_id")} = @run_id AND {c("step_id")} = @step_id";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
            [A("status", status), new("rows", DbType.Int64, rows), Fixed("shape_end", shapeEnd), new("ended_utc", DbType.DateTime2, TrackingClock.NextUtc()), new("duration_ms", DbType.Int64, durationMs), new("run_id", DbType.Guid, runId), A("step_id", stepId)]), ct);
    }

    /// <summary>
    /// A load of a refresh that did not run, in one row: `skipped: upstream failed`, `skipped: busy`, `skipped: stopped`, `not run` (the status says why; the layout has no column for a reason, and `ok` and `failed` stay as they were). The row has no
    /// duration and no rows, and its time is when the run decided.
    /// </summary>
    public static Task UnrunLoadAsync(MutationGate gate, TrackingScope scope, string stepId, Guid runId, string model, string loadName, string status, string planId, string? gitCommit, string? definitionHash, RefreshStepRecord refresh, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"INSERT INTO {T(scope, "run_log")} ({c("connection")}, {c("run_id")}, {c("step_id")}, {c("model")}, {c("operation")}, {c("rows_affected")}, {c("status")}, {c("plan_id")}, {c("git_commit")}, " +
                   $"{c("definition_hash")}, {c("shape_hash_start")}, {c("shape_hash_end")}, {c("load_name")}, {c("load_file_hash")}, {c("resolver_file_hash")}, {c("parameters")}, {c("watermark_used")}, {c("started_utc")}, {c("ended_utc")}, " +
                   $"{c("membership")}, {c("load_plan_hash")}, {c("inputs")}, {c("attempt")}, {c("duration_ms")}) " +
                   "VALUES (@connection, @run_id, @step_id, @model, @operation, NULL, @status, @plan_id, @git_commit, @definition_hash, NULL, NULL, @load_name, NULL, NULL, NULL, NULL, @started_utc, @ended_utc, @membership, @load_plan_hash, @inputs, NULL, NULL)";
        var now = TrackingClock.NextUtc();
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            Connection(scope), new("run_id", DbType.Guid, runId), A("step_id", stepId), S("model", model), A("operation", loadName), A("status", status), A("plan_id", planId), A("git_commit", gitCommit), Fixed("definition_hash", definitionHash),
            A("load_name", loadName), new("started_utc", DbType.DateTime2, now), new("ended_utc", DbType.DateTime2, now), A("membership", refresh.Membership), Fixed("load_plan_hash", refresh.LoadPlanHash), S("inputs", refresh.InputsJson),
        ]), ct);
    }

    /// <summary>The members of the refresh plan as they were when an event ran it, in order, with whether the run's selection included each (one row per member).</summary>
    public static Task PlanMemberAsync(MutationGate gate, TrackingScope scope, string stepId, string planId, int position, string membership, string model, string loadName, string loadPlanHash, bool selected, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"INSERT INTO {T(scope, "refresh_plan_member")} ({c("connection")}, {c("plan_id")}, {c("position")}, {c("membership")}, {c("model")}, {c("load_name")}, {c("load_plan_hash")}, {c("selected")}) VALUES (@connection, @plan_id, @position, @membership, @model, @load_name, @load_plan_hash, @selected)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
            [Connection(scope), A("plan_id", planId), new("position", DbType.Int32, position), A("membership", membership), S("model", model), A("load_name", loadName), Fixed("load_plan_hash", loadPlanHash), A("selected", selected ? "selected" : "not selected")]), ct);
    }

    public static Task IntervalAsync(MutationGate gate, TrackingScope scope, string stepId, string model, Guid runId, string? rangeStart, string? rangeEnd, string shapeHash, string operation, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var text = $"INSERT INTO {T(scope, "operation_interval")} ({c("connection")}, {c("interval_id")}, {c("model")}, {c("run_id")}, {c("range_start")}, {c("range_end")}, {c("shape_hash")}, {c("operation")}) " +
                   "VALUES (@connection, @interval_id, @model, @run_id, @range_start, @range_end, @shape_hash, @operation)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
            [Connection(scope), new("interval_id", DbType.Guid, Guid.NewGuid()), S("model", model), new("run_id", DbType.Guid, runId), A("range_start", rangeStart), A("range_end", rangeEnd), Fixed("shape_hash", shapeHash), A("operation", operation)]), ct);
    }

    /// <summary>A person's acknowledgement of a block, recorded so planning accepts exactly that block (code, object, hash) from now on.</summary>
    public static Task AcknowledgeAsync(MutationGate gate, TrackingScope scope, string stepId, string model, string code, string detail, string by, string reason, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var now = TrackingClock.NextUtc();
        var text = $"INSERT INTO {T(scope, "block_log")} ({c("connection")}, {c("block_id")}, {c("model")}, {c("code")}, {c("detail")}, {c("created_utc")}, {c("ack_by")}, {c("ack_reason")}, {c("ack_utc")}) " +
                   "VALUES (@connection, @block_id, @model, @code, @detail, @created_utc, @ack_by, @ack_reason, @ack_utc)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            Connection(scope), new("block_id", DbType.Guid, Guid.NewGuid()), S("model", model), A("code", code), S("detail", detail), new("created_utc", DbType.DateTime2, now), S("ack_by", by), S("ack_reason", reason),
            new("ack_utc", DbType.DateTime2, now),
        ]), ct);
    }

    public static async Task<PlanProgress> ProgressAsync(ReadSession read, TrackingScope scope, string planId, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var p = new[] { new GateParameter("plan_id", DbType.AnsiString, planId) };
        var migrations = await read.QueryAsync($"SELECT m.{c("status")} FROM {T(scope, "migration_log")} m WHERE m.{c("plan_id")} = @plan_id ORDER BY m.{c("applied_utc")}", p, ct);
        var ddl = await read.QueryAsync($"SELECT d.{c("statement_hash")} FROM {T(scope, "ddl_log")} d WHERE d.{c("plan_id")} = @plan_id AND d.{c("status")} = 'ok'", p, ct);
        var runs = await read.QueryAsync($"SELECT r.{c("step_id")} FROM {T(scope, "run_log")} r WHERE r.{c("plan_id")} = @plan_id AND r.{c("status")} = 'ok'", p, ct);
        var shapes = await read.QueryAsync($"SELECT s.{c("object_name")}, s.{c("shape_hash")} FROM {T(scope, "schema_version")} s WHERE s.{c("plan_id")} = @plan_id ORDER BY s.{c("first_seen_utc")}", p, ct);
        return new PlanProgress(migrations.Select(r => ((string)r[0]!).Trim()).ToList(), ddl.Select(r => ((string)r[0]!).Trim()).ToHashSet(), runs.Select(r => ((string)r[0]!).Trim()).ToHashSet(),
            shapes.Select(r => ((string)r[0]!, ((string)r[1]!).Trim())).ToList());
    }
}
