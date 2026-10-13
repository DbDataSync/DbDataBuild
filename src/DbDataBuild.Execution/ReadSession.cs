using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Core;

namespace DbDataBuild.Execution;

/// <summary>The read side: catalog and tracking-table queries on the read login. No method here can execute a data-changing statement.</summary>
public sealed class ReadSession : IAsyncDisposable
{
    private readonly DbConnection connection;
    private ReadSession(DbConnection connection, string engine) { this.connection = connection; Engine = engine; }

    /// <summary>The engine of the connection this session reads (sqlserver, fabric, postgres): what the catalog and tracking queries are written for.</summary>
    public string Engine { get; }

    public static async Task<ReadSession> OpenAsync(LoginSettings read, CancellationToken ct = default)
    {
        if (read.Login != Login.Read) throw new ArgumentException("A read session needs the read login.", nameof(read));
        return new ReadSession(await read.OpenAsync(ct), read.Engine);
    }

    /// <summary>Opens a session on a login of either kind, to ask what that login may do (`connection inspect`). Every statement is still guarded as a read: only SELECTs go through it, whichever login it is.</summary>
    public static async Task<ReadSession> OpenForInspectionAsync(LoginSettings login, CancellationToken ct = default) => new(await login.OpenAsync(ct), login.Engine);

    /// <summary>For tests only: told the text of every query this process reads (a test proves that a command asked the connection nothing about its structure).</summary>
    internal static Action<string>? Observer { get; set; }

    internal static ReadSession ForTesting(DbConnection connection, string engine = "sqlserver") => new(connection, engine);

    /// <summary>Seconds a query of <see cref="QueryAsync"/> may run before the driver gives up (the driver's own default, 30, when null; 0 is no limit). A comparison of two large tables is one query that scans each.</summary>
    public int? CommandTimeoutSeconds { get; set; }

    /// <param name="timeoutSeconds">For this query only (a scan of a table: a resolver, a range's bounds, a count of values); otherwise <see cref="CommandTimeoutSeconds"/>, otherwise the driver's default.</param>
    public async Task<IReadOnlyList<IReadOnlyList<object?>>> QueryAsync(string sql, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default, int? timeoutSeconds = null)
    {
        if (ReadGuard.Check(sql) is { } refused) throw new GateRefusedException(refused);
        Observer?.Invoke(sql);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if ((timeoutSeconds ?? CommandTimeoutSeconds) is { } seconds) cmd.CommandTimeout = seconds;
        foreach (var p in parameters ?? [])
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<IReadOnlyList<object?>>();
        while (await rd.ReadAsync(ct))
            rows.Add(Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? null : rd.GetValue(i)).ToList());
        return rows;
    }

    /// <summary>
    /// Reads at most <paramref name="maxRows"/> rows of one SELECT with the names of its columns, and stops (the command is cancelled, the rest is never fetched). A resolver of an input must return exactly one row, so the
    /// caller asks for two and counts. Guarded like every read.
    /// </summary>
    public async Task<(IReadOnlyList<string> Names, IReadOnlyList<IReadOnlyList<object?>> Rows)> QueryFirstRowsAsync(string sql, int maxRows, int timeoutSeconds, CancellationToken ct = default)
    {
        if (ReadGuard.Check(sql) is { } refused) throw new GateRefusedException(refused);
        Observer?.Invoke(sql);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;
        var rd = await cmd.ExecuteReaderAsync(ct);
        var names = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToList();
        var rows = new List<IReadOnlyList<object?>>();
        try
        {
            while (rows.Count < maxRows && await rd.ReadAsync(ct))
                rows.Add(Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? null : rd.GetValue(i)).ToList());
        }
        finally
        {
            // more rows may follow and the caller does not want them: cancel instead of draining, and let a cancellation that comes back from closing be
            if (rows.Count >= maxRows) { try { cmd.Cancel(); } catch (Exception) { /* the statement ended */ } }
            try { await rd.DisposeAsync(); } catch (Exception) when (rows.Count >= maxRows) { /* the cancelled rest */ }
        }
        return (names, rows);
    }

    /// <summary>
    /// Opens a streaming read: one SELECT, the rows handed over as they arrive instead of being collected (a copy reads whole tables). Guarded like every read. The stream owns the command and the reader
    /// and must be disposed.
    /// </summary>
    public async Task<RowStream> OpenStreamAsync(string sql, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default)
    {
        if (ReadGuard.Check(sql) is { } refused) throw new GateRefusedException(refused);
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        foreach (var p in parameters ?? [])
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        try { return new RowStream(cmd, await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct)); }
        catch { await cmd.DisposeAsync(); throw; }
    }

    /// <summary>
    /// The definition of a routine as the engine holds it (`OBJECT_DEFINITION` on SQL Server, `pg_get_functiondef` on PostgreSQL; the name of an overloaded PostgreSQL function carries its argument types), or null when
    /// the engine returns none: no such routine, more than one, or no permission to see it. One catalog query on the read login.
    /// </summary>
    public async Task<string?> RoutineDefinitionAsync(string engine, string name, CancellationToken ct = default)
    {
        var sql = engine == "postgres"
            ? $"SELECT pg_get_functiondef(CAST(@name AS {(name.Contains('(') ? "regprocedure" : "regproc")}))"
            : "SELECT OBJECT_DEFINITION(OBJECT_ID(@name))";
        try
        {
            var rows = await QueryAsync(sql, [new GateParameter("name", DbType.String, name)], ct);
            return rows.Count == 1 ? rows[0][0] as string : null;
        }
        catch (DbException) { return null; }
    }

    /// <summary>
    /// The columns a SELECT would return, asked of the engine without running it (the driver's schema-only mode: `sp_describe_first_result_set` on SQL Server, a parse-and-describe on PostgreSQL). Guarded like
    /// every read. Null when the engine cannot say (dynamic SQL, a temporary table); the caller says it was not checked.
    /// </summary>
    public async Task<IReadOnlyList<DbDataBuild.State.ColumnShape>?> DescribeAsync(string sql, string engine, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default)
    {
        if (ReadGuard.Check(sql) is { } refused) throw new GateRefusedException(refused);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters ?? [])
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, ct);
            var postgres = engine == "postgres";
            var columns = new List<DbDataBuild.State.ColumnShape>();
            foreach (var c in await rd.GetColumnSchemaAsync(ct))
            {
                var type = (c.DataTypeName ?? "").ToLowerInvariant();
                if (type.Length == 0) return null;
                var text = type is "char" or "varchar" or "nchar" or "nvarchar" or "character varying" or "character" or "bpchar";
                var numeric = type is "decimal" or "numeric";
                var temporal = type.StartsWith("timestamp") || type is "datetime2" or "time" or "datetimeoffset";
                columns.Add(new(c.ColumnName, type, text && c.ColumnSize is > 0 and < int.MaxValue ? c.ColumnSize : null, numeric ? c.NumericPrecision : null,
                    numeric || temporal ? c.NumericScale : null, c.AllowDBNull != false, null));
            }
            return columns;
        }
        catch (DbException) { return null; }
    }

    /// <summary>
    /// Runs a **native command** (a call that returns rows: `EXEC proc @x = @p`, `CALL proc(@p)`) on the read login and streams its first result set. It is not a SELECT, so the read guard does not apply; what
    /// holds it read-only is the login's permissions and the transaction it runs in, which is **rolled back** when the stream is disposed (PostgreSQL's read login is also read-only at the session level).
    /// Only a connection that allows native commands is asked to (the caller checks).
    /// </summary>
    public async Task<RowStream> OpenCommandStreamAsync(string text, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default)
    {
        var transaction = await connection.BeginTransactionAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = text;
        cmd.CommandTimeout = 0;
        foreach (var p in parameters ?? [])
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        try { return new RowStream(cmd, await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct), transaction); }
        catch { await cmd.DisposeAsync(); await transaction.RollbackAsync(CancellationToken.None); await transaction.DisposeAsync(); throw; }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>The rows of one streaming read. <see cref="Names"/> are the columns the query returned, in order.</summary>
public sealed class RowStream : IAsyncDisposable
{
    private readonly DbCommand command;
    private readonly DbDataReader reader;
    private readonly DbTransaction? transaction;
    internal RowStream(DbCommand command, DbDataReader reader, DbTransaction? transaction = null) { this.command = command; this.reader = reader; this.transaction = transaction; }

    public IReadOnlyList<string> Names => Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();

    /// <summary>Each row as an array of the driver's own values (null for SQL NULL). A value the driver cannot represent is a <see cref="TransferException"/> naming the column.</summary>
    public async IAsyncEnumerable<object?[]> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var width = reader.FieldCount;
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[width];
            for (var i = 0; i < width; i++)
            {
                try { row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i); }
                catch (OverflowException) { throw new TransferException($"column `{reader.GetName(i)}`: a value does not fit what the driver can read (a decimal of more than 28 digits)."); }
            }
            yield return row;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await command.DisposeAsync();
        if (transaction != null) { await transaction.RollbackAsync(CancellationToken.None); await transaction.DisposeAsync(); }     // a command's effects, if it had any, are never kept
    }
}
