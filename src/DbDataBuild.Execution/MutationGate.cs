using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataBuild.Core;

namespace DbDataBuild.Execution;

public sealed class GateRefusedException(Diagnostic diagnostic) : Exception(diagnostic.Found)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>The write side of a connection. The one place a statement is handed to a driver (see the invariant test).</summary>
internal interface IWriteExecutor : IAsyncDisposable
{
    Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct);

    /// <summary>Writes the rows to a table with the engine's bulk route and returns how many were written. The default refuses: only the real executors know a bulk route.</summary>
    Task<long> BulkCopyAsync(string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct) =>
        throw new NotSupportedException("This executor has no bulk route.");

    /// <summary>Tries to take a session-level application lock without waiting, exclusive or shared. The lock lives as long as this connection (or until <see cref="UnlockAsync"/>).</summary>
    Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct);
    Task UnlockAsync(string resource, bool shared, CancellationToken ct);

    /// <summary>After a failed statement: leaves no transaction open (PostgreSQL refuses further statements in an aborted transaction).</summary>
    Task RecoverAsync(CancellationToken ct);
}

internal sealed class AdoWriteExecutor(DbConnection connection, bool postgres) : IWriteExecutor
{
    public async Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres
            ? (shared ? "SELECT pg_try_advisory_lock_shared(hashtextextended(@res, 0))" : "SELECT pg_try_advisory_lock(hashtextextended(@res, 0))")
            : $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = N'{(shared ? "Shared" : "Exclusive")}', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@res";
        p.DbType = DbType.String;
        p.Value = resource;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return postgres ? Convert.ToBoolean(result, CultureInfo.InvariantCulture) : Convert.ToInt32(result, CultureInfo.InvariantCulture) >= 0;
    }

    public async Task UnlockAsync(string resource, bool shared, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres ? (shared ? "SELECT pg_advisory_unlock_shared(hashtextextended(@res, 0))" : "SELECT pg_advisory_unlock(hashtextextended(@res, 0))") : "EXEC sp_releaseapplock @Resource = @res, @LockOwner = N'Session';";
        var p = cmd.CreateParameter();
        p.ParameterName = "@res";
        p.DbType = DbType.String;
        p.Value = resource;
        cmd.Parameters.Add(p);
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres ? "ROLLBACK;" : "IF @@TRANCOUNT > 0 ROLLBACK;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Rows per batch handed to SqlBulkCopy: the memory a copy holds at once, whatever the table's size.</summary>
    private const int Batch = 20_000;

    public async Task<long> BulkCopyAsync(string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct)
    {
        if (connection is Microsoft.Data.SqlClient.SqlConnection sql) return await BulkCopySqlServerAsync(sql, schema, table, columns, rows, ct);
        if (connection is Npgsql.NpgsqlConnection pg) return await BulkCopyPostgresAsync(pg, schema, table, columns, rows, ct);
        throw new NotSupportedException("No bulk route for this connection.");
    }

    private static Type ClrType(string logicalType)
    {
        var t = logicalType.Trim().ToUpperInvariant();
        return t switch
        {
            "BIGINT" => typeof(long), "INTEGER" or "INT" => typeof(int), "SMALLINT" or "TINYINT" => typeof(short), "DOUBLE" => typeof(double), "FLOAT" or "REAL" => typeof(float),
            "BOOLEAN" => typeof(bool), "DATE" or "TIMESTAMP" => typeof(DateTime), "TIME" => typeof(TimeSpan), "TIMESTAMP WITH TIME ZONE" => typeof(DateTimeOffset), "UUID" => typeof(Guid), "BLOB" => typeof(byte[]),
            _ when t.StartsWith("DECIMAL", StringComparison.Ordinal) => typeof(decimal),
            _ => typeof(string),
        };
    }

    private static async Task<long> BulkCopySqlServerAsync(Microsoft.Data.SqlClient.SqlConnection connection, string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct)
    {
        using var bulk = new Microsoft.Data.SqlClient.SqlBulkCopy(connection) { DestinationTableName = $"[{schema.Replace("]", "]]")}].[{table.Replace("]", "]]")}]", BulkCopyTimeout = 0, BatchSize = Batch };
        var buffer = new DataTable();
        for (var i = 0; i < columns.Count; i++)
        {
            buffer.Columns.Add(columns[i].Name, ClrType(columns[i].LogicalType));
            bulk.ColumnMappings.Add(i, columns[i].Name);                 // by name: the staging table was created from these columns, in this order
        }
        long total = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            buffer.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
            if (buffer.Rows.Count < Batch) continue;
            await bulk.WriteToServerAsync(buffer, ct);
            total += buffer.Rows.Count;
            buffer.Clear();
        }
        if (buffer.Rows.Count > 0) { await bulk.WriteToServerAsync(buffer, ct); total += buffer.Rows.Count; }
        return total;
    }

    private static NpgsqlTypes.NpgsqlDbType PgType(string logicalType)
    {
        var t = logicalType.Trim().ToUpperInvariant();
        return t switch
        {
            "BIGINT" => NpgsqlTypes.NpgsqlDbType.Bigint, "INTEGER" or "INT" => NpgsqlTypes.NpgsqlDbType.Integer, "SMALLINT" or "TINYINT" => NpgsqlTypes.NpgsqlDbType.Smallint,
            "DOUBLE" => NpgsqlTypes.NpgsqlDbType.Double, "FLOAT" or "REAL" => NpgsqlTypes.NpgsqlDbType.Real, "BOOLEAN" => NpgsqlTypes.NpgsqlDbType.Boolean,
            "DATE" => NpgsqlTypes.NpgsqlDbType.Date, "TIMESTAMP" => NpgsqlTypes.NpgsqlDbType.Timestamp, "TIME" => NpgsqlTypes.NpgsqlDbType.Time,
            "TIMESTAMP WITH TIME ZONE" => NpgsqlTypes.NpgsqlDbType.TimestampTz, "UUID" => NpgsqlTypes.NpgsqlDbType.Uuid, "BLOB" => NpgsqlTypes.NpgsqlDbType.Bytea,
            _ when t.StartsWith("DECIMAL", StringComparison.Ordinal) => NpgsqlTypes.NpgsqlDbType.Numeric,
            _ => NpgsqlTypes.NpgsqlDbType.Text,
        };
    }

    private static async Task<long> BulkCopyPostgresAsync(Npgsql.NpgsqlConnection connection, string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct)
    {
        static string Q(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
        var types = columns.Select(c => PgType(c.LogicalType)).ToArray();
        await using var importer = await connection.BeginBinaryImportAsync($"COPY {Q(schema)}.{Q(table)} ({string.Join(", ", columns.Select(c => Q(c.Name)))}) FROM STDIN (FORMAT BINARY)", ct);
        long total = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            await importer.StartRowAsync(ct);
            for (var i = 0; i < types.Length; i++)
            {
                if (row[i] == null) { await importer.WriteNullAsync(ct); continue; }
                // Npgsql writes a date from DateOnly, and a time of day from TimeSpan
                var value = types[i] == NpgsqlTypes.NpgsqlDbType.Date && row[i] is DateTime d ? DateOnly.FromDateTime(d) : row[i];
                await importer.WriteAsync(value, types[i], ct);
            }
            total++;
        }
        await importer.CompleteAsync(ct);
        return total;
    }

    public async Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = text;
        cmd.CommandTimeout = 0;           // a statement that has started is never abandoned because it is slow: the load of a large model runs as long as it takes (the person can stop between steps)
        foreach (var p in parameters)
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        // A load script is several statements (stage, delete, insert), and the driver's own total adds their counts: 251 rows loaded read as 502.
        // The rows of a script are those of the last statement that reports a count, which is the INSERT or MERGE of a load.
        long lastCounted = -1;
        if (cmd is Microsoft.Data.SqlClient.SqlCommand sql) sql.StatementCompleted += (_, e) => lastCounted = e.RecordCount;
        var total = await cmd.ExecuteNonQueryAsync(ct);
#pragma warning disable CS0618 // the batch API would need the script split by us; the per-statement counts of a command are what this reads
        if (cmd is Npgsql.NpgsqlCommand pg)
            foreach (var st in pg.Statements)
                if (st.StatementType is Npgsql.StatementType.Insert or Npgsql.StatementType.Update or Npgsql.StatementType.Delete or Npgsql.StatementType.Merge or Npgsql.StatementType.CreateTableAs or Npgsql.StatementType.Select)
                    lastCounted = (long)st.Rows;
#pragma warning restore CS0618
        return lastCounted >= 0 ? lastCounted : total;
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>
/// The single route to a write-capable connection (DESIGN.md 9.3). It accepts only <see cref="GateStatement"/>s, checks the statement's effect class against
/// what the running command permits, records the statement in the statement log before executing it, and records the outcome after.
/// If the record cannot be written, the statement is not executed. In dry-run mode the same checks and the same log entries happen and nothing is executed.
/// </summary>
public sealed class MutationGate : IAsyncDisposable
{
    private readonly IWriteExecutor? executor;
    private readonly IStatementLog log;
    private readonly string command;
    private readonly StatementKind permitted;
    private readonly Sequence sequence;

    public Guid RunId { get; }
    public bool DryRun { get; }

    /// <summary>
    /// What gates of one run share when a refresh runs loads side by side (each load on a session of its own): the ordinal of the statement log and the lock that keeps an ordinal and its line together, so the log of a run
    /// stays one ordered sequence whichever session wrote a line.
    /// </summary>
    private sealed class Sequence { public readonly object Lock = new(); public int Value; }

    private MutationGate(IWriteExecutor? executor, IStatementLog log, string command, StatementKind permitted, bool dryRun, Guid runId, Sequence? sequence = null)
    {
        this.executor = executor; this.log = log; this.command = command; this.permitted = permitted; DryRun = dryRun; RunId = runId; this.sequence = sequence ?? new Sequence();
    }

    /// <summary>
    /// Another gate of the same run: its own write session, the same command, permissions, statement log and ordinal. A refresh with `parallel` above 1 gives each concurrent load one, because a connection runs one
    /// statement at a time. Each sibling is still the only handle on its connection.
    /// </summary>
    public async Task<MutationGate> OpenSiblingAsync(LoginSettings write, StatementKind? permittedKinds = null, CancellationToken ct = default)
    {
        if (DryRun) throw new InvalidOperationException("A dry-run gate has no session to open a sibling of.");
        if (write.Login != Login.Write) throw new ArgumentException("The mutation gate needs the write login.", nameof(write));
        return new MutationGate(new AdoWriteExecutor(await write.OpenAsync(ct), write.Engine == "postgres"), log, command, permittedKinds ?? permitted, dryRun: false, RunId, sequence);
    }

    /// <summary>For tests: a gate of the same run on another executor, sharing the log and the ordinal.</summary>
    internal MutationGate SiblingForTesting(IWriteExecutor executor) => new(executor, log, command, permitted, DryRun, RunId, sequence);

    /// <summary>Opens the write login and wraps it. The returned gate is the only handle on the connection.</summary>
    public static async Task<MutationGate> OpenAsync(LoginSettings write, string command, StatementKind permitted, IStatementLog log, Guid runId, CancellationToken ct = default)
    {
        if (write.Login != Login.Write) throw new ArgumentException("The mutation gate needs the write login.", nameof(write));
        return new MutationGate(new AdoWriteExecutor(await write.OpenAsync(ct), write.Engine == "postgres"), log, command, permitted, dryRun: false, runId);
    }

    /// <summary>A gate that runs every check and writes every log entry but never touches a database.</summary>
    public static MutationGate DryRunGate(string command, StatementKind permitted, IStatementLog log, Guid runId) => new(null, log, command, permitted, dryRun: true, runId);

    internal static MutationGate ForTesting(IWriteExecutor executor, string command, StatementKind permitted, IStatementLog log, bool dryRun = false) =>
        new(executor, log, command, permitted, dryRun, Guid.NewGuid());

    /// <summary>Returns rows affected as the driver reports it (-1 for statements that do not report one, 0 in dry-run mode).</summary>
    public async Task<long> ExecuteAsync(GateStatement statement, CancellationToken ct = default)
    {
        if ((permitted & statement.Kind) == 0)
            throw new GateRefusedException(new Diagnostic(DiagnosticCatalog.GateRefused, new($"step:{statement.StepId}", 0, 0),
                $"`{command}` permits {Describe(permitted)} statements; step `{statement.StepId}` is a {statement.Kind.ToString().ToLowerInvariant()} statement. Nothing was executed."));

        var n = Begin(DryRun ? "dry-run" : "begin", statement);
        if (DryRun) return 0;

        try
        {
            var rows = await executor!.ExecuteAsync(statement.Text, statement.Parameters, ct);
            Record(n, "end", statement, includeText: false, outcome: $"ok rows={rows.ToString(CultureInfo.InvariantCulture)}");
            return rows;
        }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            // type and driver error number only: driver messages can quote data (DESIGN.md 14.2)
            var number = ex is DbException { ErrorCode: var code } && code != 0 ? $" code={code}" : "";
            TryRecord(n, "end", statement, $"failed {ex.GetType().Name}{number}");
            await RecoverAsync(statement.StepId, CancellationToken.None);          // not `ct`: a statement that was cancelled (a load past its max_runtime) still has a transaction to roll back
            throw;
        }
    }

    /// <summary>
    /// Writes the rows of a copy to the table the statement names, through the engine's bulk route, and returns the row count. The same checks, the same log entries (before and after) and the same
    /// dry-run behaviour as <see cref="ExecuteAsync"/>; in a dry run the rows are not read.
    /// </summary>
    public async Task<long> BulkCopyAsync(GateStatement statement, string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct = default)
    {
        if ((permitted & statement.Kind) == 0)
            throw new GateRefusedException(new Diagnostic(DiagnosticCatalog.GateRefused, new($"step:{statement.StepId}", 0, 0),
                $"`{command}` permits {Describe(permitted)} statements; step `{statement.StepId}` is a {statement.Kind.ToString().ToLowerInvariant()} statement. Nothing was executed."));
        var n = Begin(DryRun ? "dry-run" : "begin", statement);
        if (DryRun) return 0;
        try
        {
            var written = await executor!.BulkCopyAsync(schema, table, columns, rows, ct);
            Record(n, "end", statement, includeText: false, outcome: $"ok rows={written.ToString(CultureInfo.InvariantCulture)}");
            return written;
        }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            var number = ex is DbException { ErrorCode: var code } && code != 0 ? $" code={code}" : "";
            TryRecord(n, "end", statement, $"failed {ex.GetType().Name}{number}");
            await RecoverAsync(statement.StepId, ct);
            throw;
        }
    }

    /// <summary>Best-effort rollback of whatever the failed statement left open, so the tracking writes that follow a failure can run. Logged like any other statement.</summary>
    private async Task RecoverAsync(string stepId, CancellationToken ct)
    {
        try
        {
            var rollback = GateStatement.Tracking(stepId + ":rollback", "ROLLBACK (leave no transaction open)");
            Begin("recover", rollback);
            await executor!.RecoverAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the connection is unusable; the next statement will fail and say so */ }
    }

    /// <summary>
    /// Takes the application lock that makes `apply` mutually exclusive on one target (`sp_getapplock` on SQL Server, an advisory lock on PostgreSQL).
    /// Returns false when another apply holds it. The attempt and the outcome are in the statement log.
    /// </summary>
    public Task<bool> TryAcquireApplicationLockAsync(string resource, CancellationToken ct = default) => AcquireApplicationLockAsync(resource, shared: false, TimeSpan.Zero, ct);

    /// <summary>
    /// A lock that can be shared (a refresh holds the connection's lock shared while one load runs, so refreshes of different loads run side by side and a deploy, which takes it exclusive, conflicts with any of them) and
    /// waited for: <paramref name="maxWait"/> is how long to try again before giving up (<see cref="TimeSpan.Zero"/> tries once, <see cref="Timeout.InfiniteTimeSpan"/> until it is free). The attempt and the outcome are in the log once, not per retry.
    /// </summary>
    public async Task<bool> AcquireApplicationLockAsync(string resource, bool shared, TimeSpan maxWait, CancellationToken ct = default)
    {
        var s = GateStatement.Tracking("lock", $"acquire application lock {resource}{(shared ? " (shared)" : "")}");
        var n = Begin(DryRun ? "dry-run" : "begin", s);
        if (DryRun) return true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool granted;
        while (!(granted = await executor!.TryLockAsync(resource, shared, ct)) && (maxWait == Timeout.InfiniteTimeSpan || clock.Elapsed < maxWait))
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        Record(n, "end", s, includeText: false, outcome: granted ? "granted" : "held by another process");
        return granted;
    }

    public Task ReleaseApplicationLockAsync(string resource, CancellationToken ct = default) => ReleaseApplicationLockAsync(resource, shared: false, ct);

    public async Task ReleaseApplicationLockAsync(string resource, bool shared, CancellationToken ct = default)
    {
        if (DryRun) return;
        var s = GateStatement.Tracking("unlock", $"release application lock {resource}{(shared ? " (shared)" : "")}");
        var n = Begin("begin", s);
        await executor!.UnlockAsync(resource, shared, ct);
        Record(n, "end", s, includeText: false, outcome: "released");
    }

    /// <summary>Takes the next ordinal of the run and writes the opening line with it, as one step, so lines of sessions that run side by side stay in ordinal order.</summary>
    private int Begin(string phase, GateStatement s)
    {
        lock (sequence.Lock)
        {
            var n = ++sequence.Value;
            Record(n, phase, s, includeText: true, outcome: null);
            return n;
        }
    }

    private void Record(int n, string phase, GateStatement s, bool includeText, string? outcome)
    {
        try
        {
            log.Append(new StatementLogEntry(RunId, n, DateTime.UtcNow, command, phase, s.StepId, s.Kind.ToString(), s.Hash, includeText ? s.Text : null,
                includeText ? (s.BulkValues ? [("(bulk values)", $"{s.Parameters.Count.ToString(CultureInfo.InvariantCulture)} values, not logged")] : s.Parameters.Select(p => (p.Name, Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "NULL")).ToList()) : null, outcome));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GateRefusedException(new Diagnostic(DiagnosticCatalog.StatementLogUnavailable, new($"step:{s.StepId}", 0, 0),
                $"The statement log could not be written ({ex.GetType().Name}), so step `{s.StepId}` was not executed."));
        }
    }

    // after a failed statement the original error matters more than a failure to log it
    private void TryRecord(int n, string phase, GateStatement s, string outcome)
    {
        try { Record(n, phase, s, false, outcome); } catch (GateRefusedException) { }
    }

    private static string Describe(StatementKind k) => string.Join("/", Enum.GetValues<StatementKind>().Where(v => k.HasFlag(v)).Select(v => v.ToString().ToLowerInvariant()));

    public ValueTask DisposeAsync() => executor?.DisposeAsync() ?? ValueTask.CompletedTask;
}
