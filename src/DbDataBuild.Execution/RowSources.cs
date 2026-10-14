using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>The rows of one statement, read one at a time. The reader owns the statement and must be disposed.</summary>
public interface IRowReader : IAsyncDisposable
{
    /// <summary>The names of the columns the statement returned, in order.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>Moves to the next row; false at the end.</summary>
    Task<bool> ReadAsync(CancellationToken ct);

    /// <summary>The value of a column of the current row as the driver's own object; null for SQL NULL.</summary>
    ValueTask<object?> ValueAsync(int column, CancellationToken ct);

    /// <summary>Asks the origin to stop the statement; the rest of the rows is never fetched. A statement that has ended already is not an error.</summary>
    void Cancel();
}

/// <summary>
/// One open read connection (docs/research/read-origins.md section 4): what <see cref="ReadSession"/> is built on. The ADO.NET implementation serves SQL Server, PostgreSQL and DuckDB (and ODBC);
/// a source that is not ADO.NET (Flight SQL, whose results are Arrow batches) implements the same few members. It does not guard anything: <see cref="ReadSession"/> checks that every text is a read
/// before it reaches a source, and is the only way to get to one.
/// </summary>
public interface IRowSource : IAsyncDisposable
{
    /// <summary>Runs one statement and returns its rows. <paramref name="parameters"/> are bound in the order given when the origin's parameters are positional, by name otherwise.</summary>
    /// <param name="timeoutSeconds">The limit the driver puts on the statement (its own default when null; 0 is none).</param>
    /// <param name="streaming">The caller reads the columns of a row in order, once, and may be reading a large table: the driver may stream.</param>
    Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct);

    /// <summary>Runs a native command (a call that returns rows) in a transaction that is rolled back when the reader is disposed. A source that has no such thing says so.</summary>
    Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct);

    /// <summary>The columns a SELECT would return, asked without running it; null when the origin cannot say.</summary>
    Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct);

    /// <summary>What the source says about itself without a query of ours (an ODBC driver's name and the server's version); null when it says nothing.</summary>
    string? ServerDescription => null;
}
