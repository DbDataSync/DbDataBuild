using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>
/// The ODBC driver says something went wrong with a catalog function. Only the function's name and the SQLSTATE are kept: the driver's message can quote a name or a path, so it is never repeated.
/// <see cref="NotSupported"/> is the driver saying it does not offer the function (IM001, HYC00, HY106 and friends): the caller then says the catalog does not have that part.
/// </summary>
public sealed class OdbcCatalogException(string function, string sqlState) : Exception($"The ODBC driver's {function} failed (SQLSTATE {sqlState}).")
{
    public string Function { get; } = function;
    public string SqlState { get; } = sqlState;

    /// <summary>The driver does not implement this catalog function, or this part of it.</summary>
    public bool NotSupported => SqlState is "IM001" or "HYC00" or "HY106" or "HYC01" or "S1C00" or "IM017";
}

/// <summary>
/// The catalog functions of ODBC, as the rows of the result sets the specification fixes (columns are read by position, as text, null for NULL). Only these five and the schema listing exist here: nothing that executes
/// a statement, which is why the tool's rule that statements to an origin go through <see cref="ReadSession"/> is not touched (a test scans the native file for any executing call).
/// </summary>
internal interface IOdbcCatalogFunctions : IAsyncDisposable
{
    /// <summary>`SQLTables` with the schema list request (`TABLE_SCHEM` = `%`, the others empty): the schema names, column 2 of each row.</summary>
    Task<IReadOnlyList<string?[]>> SchemasAsync(CancellationToken ct);
    /// <summary>`SQLTables` of the tables and views of a schema: TABLE_CAT, TABLE_SCHEM, TABLE_NAME, TABLE_TYPE, REMARKS.</summary>
    Task<IReadOnlyList<string?[]>> TablesAsync(string schema, CancellationToken ct);
    /// <summary>`SQLColumns` of every table of a schema (table null) or one: the 18 columns of the specification.</summary>
    Task<IReadOnlyList<string?[]>> ColumnsAsync(string schema, string? table, CancellationToken ct);
    /// <summary>`SQLPrimaryKeys`: TABLE_CAT, TABLE_SCHEM, TABLE_NAME, COLUMN_NAME, KEY_SEQ, PK_NAME.</summary>
    Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string schema, string table, CancellationToken ct);
    /// <summary>`SQLForeignKeys` of the keys a table has: PKTABLE_CAT, PKTABLE_SCHEM, PKTABLE_NAME, PKCOLUMN_NAME, FKTABLE_CAT, FKTABLE_SCHEM, FKTABLE_NAME, FKCOLUMN_NAME, KEY_SEQ, UPDATE_RULE, DELETE_RULE, FK_NAME, PK_NAME.</summary>
    Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string schema, string table, CancellationToken ct);
    /// <summary>`SQLStatistics` of a table, all indexes: TABLE_CAT, TABLE_SCHEM, TABLE_NAME, NON_UNIQUE, INDEX_QUALIFIER, INDEX_NAME, TYPE, ORDINAL_POSITION, COLUMN_NAME, ASC_OR_DESC, CARDINALITY, PAGES, FILTER_CONDITION.</summary>
    Task<IReadOnlyList<string?[]>> StatisticsAsync(string schema, string table, CancellationToken ct);
}

/// <summary>
/// A catalog read through ODBC's own functions (<c>catalog: odbc</c> in the dialect): the answers of <see cref="IOdbcCatalogFunctions"/> turned into the shapes the tool compares and imports. Pure over those rows, so it
/// is tested with a fake driver; <see cref="OdbcNativeFunctions"/> is the real one.
/// </summary>
internal sealed class OdbcCatalog(IOdbcCatalogFunctions functions, DialectProfile dialect) : IMetadataCatalog, IAsyncDisposable
{
    private static readonly string[] OwnSchemas = ["information_schema", "pg_catalog", "sys"];

    public async Task<IReadOnlyList<string>?> SchemasAsync(CancellationToken ct)
    {
        IReadOnlyList<string?[]> rows;
        try { rows = await functions.SchemasAsync(ct); }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return null; }
        return rows.Select(r => r.ElementAtOrDefault(1)).Where(s => !string.IsNullOrEmpty(s) && !OwnSchemas.Contains(s, StringComparer.OrdinalIgnoreCase)).Select(s => s!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    // SQLTables takes a pattern for the schema (`_` and `%` are wildcards), so what comes back is filtered to the exact name; a view and a table are told apart by TABLE_TYPE
    private async Task<List<(string Name, bool IsView)>> ObjectsAsync(string schema, CancellationToken ct)
    {
        var rows = await functions.TablesAsync(schema, ct);
        return rows.Where(r => Same(r.ElementAtOrDefault(1), schema) && r.ElementAtOrDefault(2) is { Length: > 0 } && IsTableOrView(r.ElementAtOrDefault(3)))
            .Select(r => (r[2]!, (r[3] ?? "").Contains("VIEW", StringComparison.OrdinalIgnoreCase))).DistinctBy(x => x.Item1, StringComparer.Ordinal).OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    private static bool IsTableOrView(string? type) => type != null && !type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) && !type.Contains("INDEX", StringComparison.OrdinalIgnoreCase) && !type.Contains("SEQUENCE", StringComparison.OrdinalIgnoreCase)
        && (type.Contains("TABLE", StringComparison.OrdinalIgnoreCase) || type.Contains("VIEW", StringComparison.OrdinalIgnoreCase));

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    public async Task<IReadOnlyList<MetadataTable>?> TablesAsync(string schema, string? table, CancellationToken ct)
    {
        List<(string Name, bool IsView)> objects;
        IReadOnlyList<string?[]> columns;
        try
        {
            objects = await ObjectsAsync(schema, ct);
            if (table != null) objects = objects.Where(o => Same(o.Name, table)).ToList();
            columns = objects.Count == 0 ? [] : await functions.ColumnsAsync(schema, table, ct);
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return null; }
        var byTable = columns.Where(r => Same(r.ElementAtOrDefault(1), schema)).GroupBy(r => r.ElementAtOrDefault(2) ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return objects.Where(o => byTable.ContainsKey(o.Name)).Select(o => new MetadataTable(schema, o.Name, o.IsView,
            byTable[o.Name].OrderBy(r => Number(r.ElementAtOrDefault(16)) ?? int.MaxValue).Select(Column).ToList())).ToList();
    }

    /// <summary>
    /// A column from the 18 columns of SQLColumns. Its type is named the way the dialect's `types:` does when the driver's own TYPE_NAME is mapped there; otherwise by the standard name of its ODBC type code (so
    /// `ansi` needs no mapping for the usual ones), and by TYPE_NAME for a code the standard has no name for (a vendor type, which the profile maps or the import reports).
    /// </summary>
    internal ColumnShape Column(string?[] r)
    {
        var typeName = (r.ElementAtOrDefault(5) ?? "").ToLowerInvariant();
        var code = Number(r.ElementAtOrDefault(4));
        var type = dialect.Types.ContainsKey(typeName) ? typeName : code is { } c && StandardName(c) is { Length: > 0 } standard ? standard : typeName;
        var size = Number(r.ElementAtOrDefault(6));
        var digits = Number(r.ElementAtOrDefault(8));
        var text = type is "character" or "character varying" or "text" or "char" or "varchar" or "nchar" or "nvarchar" or "bpchar";
        var numeric = type is "decimal" or "numeric";
        var nullable = Number(r.ElementAtOrDefault(10)) is not 0 && !string.Equals(r.ElementAtOrDefault(17), "NO", StringComparison.OrdinalIgnoreCase);
        return new(r.ElementAtOrDefault(3) ?? "", type, text && size is > 0 and < int.MaxValue ? size : null, numeric ? size : null, numeric ? digits ?? 0 : null, nullable, null, null);
    }

    private static int? Number(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The standard (`information_schema`) name of an ODBC SQL data type code; empty for a code that has none.</summary>
    internal static string StandardName(int code) => code switch
    {
        1 or -8 => "character", 12 or -9 => "character varying", -1 or -10 => "text", 2 => "numeric", 3 => "decimal", 4 => "integer", 5 or -6 => "smallint", 6 or 8 => "double precision", 7 => "real",
        -5 => "bigint", -7 => "boolean", 91 or 9 => "date", 92 or 10 => "time", 93 or 11 => "timestamp", -2 or -3 or -4 => "blob", -11 => "uuid", _ => "",
    };

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> PrimaryKeysAsync(string schema, CancellationToken ct)
    {
        try
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (name, isView) in await ObjectsAsync(schema, ct))
            {
                if (isView) continue;
                var rows = (await functions.PrimaryKeysAsync(schema, name, ct)).Where(r => Same(r.ElementAtOrDefault(1), schema) && Same(r.ElementAtOrDefault(2), name) && r.ElementAtOrDefault(3) != null).ToList();
                if (rows.Count > 0) result[name] = rows.OrderBy(r => Number(r.ElementAtOrDefault(4)) ?? 0).Select(r => r[3]!).ToList();
            }
            return result;
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return null; }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?> ForeignKeysAsync(string schema, CancellationToken ct)
    {
        try
        {
            var result = new Dictionary<string, IReadOnlyList<MetadataForeignKey>>(StringComparer.Ordinal);
            foreach (var (name, isView) in await ObjectsAsync(schema, ct))
            {
                if (isView) continue;
                var rows = (await functions.ForeignKeysAsync(schema, name, ct)).Where(r => Same(r.ElementAtOrDefault(5), schema) && Same(r.ElementAtOrDefault(6), name)).ToList();
                if (rows.Count == 0) continue;
                // one key is the rows with one FK_NAME; a driver that gives none starts a new key where KEY_SEQ goes back to 1
                var keys = new List<List<string?[]>>();
                foreach (var r in rows.OrderBy(r => r.ElementAtOrDefault(11) ?? "", StringComparer.Ordinal).ThenBy(r => Number(r.ElementAtOrDefault(8)) ?? 0))
                {
                    var sameKey = keys.Count > 0 && r.ElementAtOrDefault(11) is { } fk && fk == keys[^1][0].ElementAtOrDefault(11) && (Number(r.ElementAtOrDefault(8)) ?? 0) > (Number(keys[^1][^1].ElementAtOrDefault(8)) ?? 0);
                    if (sameKey) keys[^1].Add(r); else keys.Add([r]);
                }
                result[name] = keys.Select((k, i) => new MetadataForeignKey(name, k[0].ElementAtOrDefault(11) ?? $"fk_{name}_{k[0].ElementAtOrDefault(2)}_{i + 1}", k.Select(r => r.ElementAtOrDefault(7) ?? "").ToList(),
                    k[0].ElementAtOrDefault(1) ?? schema, k[0].ElementAtOrDefault(2) ?? "", k.Select(r => r.ElementAtOrDefault(3) ?? "").ToList())).ToList();
            }
            return result;
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return null; }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataIndex>>?> IndexesAsync(string schema, CancellationToken ct)
    {
        try
        {
            var result = new Dictionary<string, IReadOnlyList<MetadataIndex>>(StringComparer.Ordinal);
            foreach (var (name, isView) in await ObjectsAsync(schema, ct))
            {
                if (isView) continue;
                // TYPE 0 is the table's own statistics row, not an index; an index with no name cannot be exported
                var rows = (await functions.StatisticsAsync(schema, name, ct)).Where(r => Same(r.ElementAtOrDefault(1), schema) && Same(r.ElementAtOrDefault(2), name) && Number(r.ElementAtOrDefault(6)) != 0 && !string.IsNullOrEmpty(r.ElementAtOrDefault(5))).ToList();
                if (rows.Count == 0) continue;
                result[name] = rows.GroupBy(r => r[5]!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
                    new MetadataIndex(name, g.Key, Number(g.First().ElementAtOrDefault(3)) == 0, g.OrderBy(r => Number(r.ElementAtOrDefault(7)) ?? 0)
                        .Select(r => (r.ElementAtOrDefault(8) ?? "<expression>") + (r.ElementAtOrDefault(9) == "D" ? " desc" : "")).ToList(), [])).ToList();
            }
            return result;
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return null; }
    }

    public ValueTask DisposeAsync() => functions.DisposeAsync();
}

/// <summary>
/// The real driver manager's catalog functions, called directly (System.Data.Odbc offers `GetSchema` for tables, columns and indexes, and nothing for primary and foreign keys). It opens a connection of its own with the
/// same login string, because the provider keeps its handle to itself. It calls only the catalog functions: no statement is prepared or executed through it.
/// </summary>
internal sealed class OdbcNativeFunctions(string connectionString) : IOdbcCatalogFunctions
{
    private const short HandleEnv = 1, HandleDbc = 2, HandleStmt = 3, NullTerminated = -3;
    private nint environment, connection;
    private readonly SemaphoreSlim one = new(1, 1);

    static OdbcNativeFunctions()
    {
        // the driver manager's library has a different name on each system; the provider System.Data.Odbc uses the same names
        try { NativeLibrary.SetDllImportResolver(typeof(OdbcNativeFunctions).Assembly, (name, _, _) => name != "ddb-odbc" ? 0 : Load()); }
        catch (InvalidOperationException) { /* a resolver is already set for this assembly */ }
    }

    private static nint Load()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "odbc32.dll" } : OperatingSystem.IsMacOS() ? new[] { "libodbc.2.dylib", "libodbc.dylib" } : new[] { "libodbc.so.2", "libodbc.so.1", "libodbc.so" };
        foreach (var n in names) if (NativeLibrary.TryLoad(n, out var h)) return h;
        throw new DllNotFoundException("the ODBC driver manager");
    }

    [DllImport("ddb-odbc")] private static extern short SQLAllocHandle(short handleType, nint input, out nint output);
    [DllImport("ddb-odbc")] private static extern short SQLFreeHandle(short handleType, nint handle);
    [DllImport("ddb-odbc")] private static extern short SQLSetEnvAttr(nint env, int attribute, nint value, int length);
    [DllImport("ddb-odbc")] private static extern short SQLSetConnectAttr(nint dbc, int attribute, nint value, int length);
    [DllImport("ddb-odbc")] private static extern short SQLDriverConnectW(nint dbc, nint window, [MarshalAs(UnmanagedType.LPWStr)] string input, short inputLength, nint output, short outputMax, out short outputLength, ushort completion);
    [DllImport("ddb-odbc")] private static extern short SQLDisconnect(nint dbc);
    [DllImport("ddb-odbc")] private static extern short SQLTablesW(nint stmt, [MarshalAs(UnmanagedType.LPWStr)] string? catalog, short catalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? schema, short schemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? table, short tableLength, [MarshalAs(UnmanagedType.LPWStr)] string? types, short typesLength);
    [DllImport("ddb-odbc")] private static extern short SQLColumnsW(nint stmt, [MarshalAs(UnmanagedType.LPWStr)] string? catalog, short catalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? schema, short schemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? table, short tableLength, [MarshalAs(UnmanagedType.LPWStr)] string? column, short columnLength);
    [DllImport("ddb-odbc")] private static extern short SQLPrimaryKeysW(nint stmt, [MarshalAs(UnmanagedType.LPWStr)] string? catalog, short catalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? schema, short schemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? table, short tableLength);
    [DllImport("ddb-odbc")] private static extern short SQLForeignKeysW(nint stmt, [MarshalAs(UnmanagedType.LPWStr)] string? pkCatalog, short pkCatalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? pkSchema, short pkSchemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? pkTable, short pkTableLength, [MarshalAs(UnmanagedType.LPWStr)] string? fkCatalog, short fkCatalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? fkSchema, short fkSchemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? fkTable, short fkTableLength);
    [DllImport("ddb-odbc")] private static extern short SQLStatisticsW(nint stmt, [MarshalAs(UnmanagedType.LPWStr)] string? catalog, short catalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? schema, short schemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? table, short tableLength, ushort unique, ushort reserved);
    [DllImport("ddb-odbc")] private static extern short SQLNumResultCols(nint stmt, out short columns);
    [DllImport("ddb-odbc")] private static extern short SQLFetch(nint stmt);
    [DllImport("ddb-odbc")] private static extern short SQLGetData(nint stmt, ushort column, short targetType, byte[] buffer, nint bufferLength, out nint indicator);
    [DllImport("ddb-odbc")] private static extern short SQLGetDiagRecW(short handleType, nint handle, short record, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder state, out int nativeError,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder message, short messageMax, out short messageLength);

    private static bool Ok(short ret) => ret is 0 or 1;

    private static string State(short handleType, nint handle)
    {
        var state = new StringBuilder(8);
        try { return Ok(SQLGetDiagRecW(handleType, handle, 1, state, out _, new StringBuilder(16), 16, out _)) ? state.ToString() : "HY000"; }
        catch (Exception) { return "HY000"; }
    }

    private void Connect()
    {
        if (connection != 0) return;
        if (!Ok(SQLAllocHandle(HandleEnv, 0, out var env))) throw new OdbcCatalogException("SQLAllocHandle", "HY001");
        environment = env;
        SQLSetEnvAttr(env, 200, 3, 0);                                                             // SQL_ATTR_ODBC_VERSION = SQL_OV_ODBC3
        if (!Ok(SQLAllocHandle(HandleDbc, env, out var dbc))) throw new OdbcCatalogException("SQLAllocHandle", State(HandleEnv, env));
        SQLSetConnectAttr(dbc, 103, 30, 0);                                                        // SQL_ATTR_LOGIN_TIMEOUT
        var ret = SQLDriverConnectW(dbc, 0, OdbcLogin.Normalize(connectionString), NullTerminated, 0, 0, out _, 0);   // SQL_DRIVER_NOPROMPT
        if (!Ok(ret)) { var state = State(HandleDbc, dbc); SQLFreeHandle(HandleDbc, dbc); throw new OdbcCatalogException("SQLDriverConnect", state); }
        connection = dbc;
    }

    private static short Len(string? s) => s == null ? (short)0 : NullTerminated;

    // one catalog function: a statement handle for it, every row of its result as text, the handle freed
    private async Task<IReadOnlyList<string?[]>> RunAsync(string function, Func<nint, short> call, CancellationToken ct)
    {
        await one.WaitAsync(ct);
        try { return await Task.Run(() => Fetch(function, call, ct), ct); }
        finally { one.Release(); }
    }

    private List<string?[]> Fetch(string function, Func<nint, short> call, CancellationToken ct)
    {
        Connect();
        if (!Ok(SQLAllocHandle(HandleStmt, connection, out var stmt))) throw new OdbcCatalogException(function, State(HandleDbc, connection));
        try
        {
            var ret = call(stmt);
            if (!Ok(ret)) throw new OdbcCatalogException(function, State(HandleStmt, stmt));
            SQLNumResultCols(stmt, out var width);
            var rows = new List<string?[]>();
            var buffer = new byte[4096];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                ret = SQLFetch(stmt);
                if (ret == 100) break;
                if (!Ok(ret)) throw new OdbcCatalogException(function, State(HandleStmt, stmt));
                var row = new string?[width];
                for (ushort c = 1; c <= width; c++) row[c - 1] = Text(stmt, c, buffer, function);
                rows.Add(row);
            }
            return rows;
        }
        finally { SQLFreeHandle(HandleStmt, stmt); }
    }

    private static string? Text(nint stmt, ushort column, byte[] buffer, string function)
    {
        var text = new StringBuilder();
        while (true)
        {
            var ret = SQLGetData(stmt, column, -8, buffer, buffer.Length, out var indicator);       // SQL_C_WCHAR
            if (ret == 100) break;
            if (!Ok(ret)) throw new OdbcCatalogException(function, State(HandleStmt, stmt));
            if (indicator == -1) return text.Length == 0 ? null : text.ToString();                    // SQL_NULL_DATA
            var available = ret == 1 && (indicator == -4 || indicator >= buffer.Length) ? buffer.Length - 2 : (int)Math.Min(indicator, buffer.Length - 2);   // truncated: a full buffer less the terminator
            text.Append(Encoding.Unicode.GetString(buffer, 0, Math.Max(0, available - available % 2)));
            if (ret == 0) break;
        }
        return text.ToString();
    }

    public Task<IReadOnlyList<string?[]>> SchemasAsync(CancellationToken ct) =>
        RunAsync("SQLTables", s => SQLTablesW(s, "", 0, "%", NullTerminated, "", 0, null, 0), ct);

    public Task<IReadOnlyList<string?[]>> TablesAsync(string schema, CancellationToken ct) =>
        RunAsync("SQLTables", s => SQLTablesW(s, null, 0, schema, NullTerminated, null, 0, "TABLE,VIEW", NullTerminated), ct);

    public Task<IReadOnlyList<string?[]>> ColumnsAsync(string schema, string? table, CancellationToken ct) =>
        RunAsync("SQLColumns", s => SQLColumnsW(s, null, 0, schema, NullTerminated, table, Len(table), null, 0), ct);

    public Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string schema, string table, CancellationToken ct) =>
        RunAsync("SQLPrimaryKeys", s => SQLPrimaryKeysW(s, null, 0, schema, NullTerminated, table, NullTerminated), ct);

    public Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string schema, string table, CancellationToken ct) =>
        RunAsync("SQLForeignKeys", s => SQLForeignKeysW(s, null, 0, null, 0, null, 0, null, 0, schema, NullTerminated, table, NullTerminated), ct);

    public Task<IReadOnlyList<string?[]>> StatisticsAsync(string schema, string table, CancellationToken ct) =>
        RunAsync("SQLStatistics", s => SQLStatisticsW(s, null, 0, schema, NullTerminated, table, NullTerminated, 1, 0), ct);       // SQL_INDEX_ALL, SQL_QUICK

    public ValueTask DisposeAsync()
    {
        if (connection != 0) { SQLDisconnect(connection); SQLFreeHandle(HandleDbc, connection); connection = 0; }
        if (environment != 0) { SQLFreeHandle(HandleEnv, environment); environment = 0; }
        return ValueTask.CompletedTask;
    }
}
