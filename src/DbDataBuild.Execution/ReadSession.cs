using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>The read side: catalog and tracking-table queries on the read login. No method here can execute a data-changing statement. Every statement goes through an <see cref="IRowSource"/> (the ADO.NET one for the engines and DuckDB).</summary>
public sealed class ReadSession : IAsyncDisposable
{
    private readonly IRowSource source;
    private ReadSession(IRowSource source, string engine) { this.source = source; Engine = engine; }

    /// <summary>The engine of the connection this session reads (sqlserver, fabric, postgres, duckdb): what the catalog and tracking queries are written for.</summary>
    public string Engine { get; }

    public static async Task<ReadSession> OpenAsync(LoginSettings read, CancellationToken ct = default)
    {
        if (read.Login != Login.Read) throw new ArgumentException("A read session needs the read login.", nameof(read));
        return new ReadSession(await read.OpenRowSourceAsync(ct), read.Engine);
    }

    /// <summary>Opens a session on a login of either kind, to ask what that login may do (`connection inspect`). Every statement is still guarded as a read: only SELECTs go through it, whichever login it is.</summary>
    public static async Task<ReadSession> OpenForInspectionAsync(LoginSettings login, CancellationToken ct = default) => new(await login.OpenRowSourceAsync(ct), login.Engine);

    /// <summary>For tests only: told the text of every query this process reads (a test proves that a command asked the connection nothing about its structure).</summary>
    internal static Action<string>? Observer { get; set; }

    internal static ReadSession ForTesting(DbConnection connection, string engine = "sqlserver") => new(new AdoRowSource(connection, DbDataBuild.Models.ParameterStyle.NamedAt), engine);

    /// <summary>For tests only: a session over a source of the test's own.</summary>
    internal static ReadSession ForTesting(IRowSource source, string engine) => new(source, engine);

    /// <summary>Seconds a query of <see cref="QueryAsync"/> may run before the driver gives up (the driver's own default, 30, when null; 0 is no limit). A comparison of two large tables is one query that scans each.</summary>
    public int? CommandTimeoutSeconds { get; set; }

    private static async Task<IReadOnlyList<object?>> RowOf(IRowReader rd, int width, CancellationToken ct)
    {
        var row = new object?[width];
        for (var i = 0; i < width; i++) row[i] = await rd.ValueAsync(i, ct);
        return row;
    }

    /// <param name="timeoutSeconds">For this query only (a scan of a table: a resolver, a range's bounds, a count of values); otherwise <see cref="CommandTimeoutSeconds"/>, otherwise the driver's default.</param>
    public async Task<IReadOnlyList<IReadOnlyList<object?>>> QueryAsync(string sql, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default, int? timeoutSeconds = null)
    {
        if (ReadGuard.Check(sql) is { } refused) throw new GateRefusedException(refused);
        Observer?.Invoke(sql);
        await using var rd = await source.ExecuteAsync(sql, parameters ?? [], timeoutSeconds ?? CommandTimeoutSeconds, false, ct);
        var width = rd.Names.Count;
        var rows = new List<IReadOnlyList<object?>>();
        while (await rd.ReadAsync(ct)) rows.Add(await RowOf(rd, width, ct));
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
        var rd = await source.ExecuteAsync(sql, [], timeoutSeconds, false, ct);
        var names = rd.Names.ToList();
        var rows = new List<IReadOnlyList<object?>>();
        try
        {
            while (rows.Count < maxRows && await rd.ReadAsync(ct)) rows.Add(await RowOf(rd, names.Count, ct));
        }
        finally
        {
            // more rows may follow and the caller does not want them: cancel instead of draining, and let a cancellation that comes back from closing be
            if (rows.Count >= maxRows) rd.Cancel();
            try { await rd.DisposeAsync(); } catch (Exception) when (rows.Count >= maxRows) { /* the cancelled rest */ }
        }
        return (names, rows);
    }

    /// <summary>
    /// Opens a streaming read: one SELECT, the rows handed over as they arrive instead of being collected (a copy reads whole tables). Guarded like every read. The stream owns the command and the reader
    /// and must be disposed. <paramref name="parameters"/> are bound in the order given when the origin's parameters are positional.
    /// </summary>
    public async Task<RowStream> OpenStreamAsync(string sql, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default)
    {
        if (ReadGuard.Check(sql) is { } refused) throw new GateRefusedException(refused);
        return new RowStream(await source.ExecuteAsync(sql, parameters ?? [], 0, true, ct));
    }

    /// <summary>
    /// The definition of a routine as the engine holds it (`OBJECT_DEFINITION` on SQL Server, `pg_get_functiondef` on PostgreSQL; the name of an overloaded PostgreSQL function carries its argument types), or null when
    /// the engine returns none: no such routine, more than one, or no permission to see it. One catalog query on the read login.
    /// </summary>
    public async Task<string?> RoutineDefinitionAsync(string engine, string name, CancellationToken ct = default)
    {
        if (engine == "duckdb") return null;       // a DuckDB file holds no routines to watch
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
        return await source.DescribeAsync(sql, parameters ?? [], ct);
    }

    /// <summary>
    /// Runs a **native command** (a call that returns rows: `EXEC proc @x = @p`, `CALL proc(@p)`) on the read login and streams its first result set. It is not a SELECT, so the read guard does not apply; what
    /// holds it read-only is the login's permissions and the transaction it runs in, which is **rolled back** when the stream is disposed (PostgreSQL's read login is also read-only at the session level).
    /// Only a connection that allows native commands is asked to (the caller checks).
    /// </summary>
    public async Task<RowStream> OpenCommandStreamAsync(string text, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default) =>
        new(await source.ExecuteCommandAsync(text, parameters ?? [], ct));

    private IDisposable? lease;

    /// <summary>Ties a slot of a <see cref="SessionLimits"/> to this session: it is given back when the session is disposed.</summary>
    internal void HoldLease(IDisposable held) => lease = held;

    public async ValueTask DisposeAsync()
    {
        try { await source.DisposeAsync(); }
        finally { lease?.Dispose(); lease = null; }
    }
}

/// <summary>The rows of one streaming read. <see cref="Names"/> are the columns the query returned, in order.</summary>
public sealed class RowStream : IAsyncDisposable
{
    private readonly IRowReader reader;
    internal RowStream(IRowReader reader) { this.reader = reader; }

    public IReadOnlyList<string> Names => reader.Names;

    /// <summary>Each row as an array of the driver's own values (null for SQL NULL). A value the driver cannot represent is a <see cref="TransferException"/> naming the column.</summary>
    public async IAsyncEnumerable<object?[]> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var names = reader.Names;
        var width = names.Count;
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[width];
            for (var i = 0; i < width; i++)
            {
                try { row[i] = await reader.ValueAsync(i, ct); }
                catch (OverflowException) { throw new TransferException($"column `{names[i]}`: a value does not fit what the driver can read (a decimal of more than 28 digits)."); }
            }
            yield return row;
        }
    }

    public ValueTask DisposeAsync() => reader.DisposeAsync();
}

// The ADO.NET implementation of the row source lives here, with the session, because this file and MutationGate.cs are the only places GateInvariantTests lets hand text to a driver.

/// <summary>An ADO.NET connection as a row source. <paramref name="style"/> says how the origin takes parameters: by name (`@name`) or by position (`?`, `$1`).</summary>
internal class AdoRowSource(DbConnection connection, ParameterStyle style) : IRowSource
{
    protected DbConnection Connection => connection;

    protected void Bind(DbCommand cmd, IReadOnlyList<GateParameter> parameters)
    {
        foreach (var p in parameters)
        {
            var dp = cmd.CreateParameter();
            if (style == ParameterStyle.NamedAt) dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
    }

    public async Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (timeoutSeconds is { } seconds) cmd.CommandTimeout = seconds;
        Bind(cmd, parameters);
        try { return new AdoRowReader(cmd, await cmd.ExecuteReaderAsync(streaming ? CommandBehavior.SequentialAccess : CommandBehavior.Default, ct), null); }
        catch { await cmd.DisposeAsync(); throw; }
    }

    public async Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = text;
        cmd.CommandTimeout = 0;
        Bind(cmd, parameters);
        try { return new AdoRowReader(cmd, await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct), transaction); }
        catch { await cmd.DisposeAsync(); await transaction.RollbackAsync(CancellationToken.None); await transaction.DisposeAsync(); throw; }
    }

    public virtual async Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, parameters);
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);
            var columns = new List<ColumnShape>();
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

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

internal sealed class AdoRowReader(DbCommand command, DbDataReader reader, DbTransaction? transaction) : IRowReader
{
    public IReadOnlyList<string> Names => Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();

    public Task<bool> ReadAsync(CancellationToken ct) => reader.ReadAsync(ct);

    public async ValueTask<object?> ValueAsync(int column, CancellationToken ct) => await reader.IsDBNullAsync(column, ct) ? null : reader.GetValue(column);

    public void Cancel() { try { command.Cancel(); } catch (Exception) { /* the statement ended */ } }

    public async ValueTask DisposeAsync()
    {
        try { await reader.DisposeAsync(); }
        finally
        {
            try { await command.DisposeAsync(); }
            finally
            {
                if (transaction != null) { await transaction.RollbackAsync(CancellationToken.None); await transaction.DisposeAsync(); }     // a command's effects, if it had any, are never kept
            }
        }
    }
}

/// <summary>
/// DuckDB's connection as a row source. It answers `DescribeAsync` without reading rows by running the SELECT as a derived table with `LIMIT 0` (the driver has no schema-only mode); the parameters are bound as for a read, so a
/// native text with `?` placeholders can be described.
/// </summary>
internal sealed class DuckDbRowSource(DbConnection connection, ParameterStyle style) : AdoRowSource(connection, style)
{
    public override async Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM ({sql.Trim().TrimEnd(';')}) AS ddb_described LIMIT 0";
        Bind(cmd, parameters);
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var columns = new List<ColumnShape>();
            for (var i = 0; i < rd.FieldCount; i++)
            {
                var declared = (rd.GetDataTypeName(i) ?? "").ToLowerInvariant();
                if (declared.Length == 0) return null;
                var open = declared.IndexOf('(');
                var type = open < 0 ? declared : declared[..open].Trim();
                int? precision = null, scale = null;
                if (type == "decimal" && open >= 0)
                {
                    var parts = declared[(open + 1)..].TrimEnd(')').Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var p) && int.TryParse(parts[1], out var sc)) (precision, scale) = (p, sc);
                }
                columns.Add(new(rd.GetName(i), type, null, precision, scale, true, null, null));
            }
            return columns;
        }
        catch (DbException) { return null; }
    }
}
