using System.Data;
using System.Globalization;

namespace DbDataBuild.Execution;

/// <summary>What `import` reads beyond the shapes <see cref="CatalogReader"/> gives: which schema names exist, and each table's primary key (it seeds a new descriptor's grain). Read session only.</summary>
public static class SourceCatalogReader
{
    // sys and INFORMATION_SCHEMA are the engine's; db_* are the schema names of fixed database roles; guest is empty. pg_* are PostgreSQL's catalogs and temp schema names.
    private const string SqlServerSchemas = "SELECT name FROM sys.schemas WHERE name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest') AND name NOT LIKE 'db[_]%' ORDER BY name";
    private const string PostgresSchemas = "SELECT schema_name FROM information_schema.schemata WHERE schema_name <> 'information_schema' AND schema_name NOT LIKE 'pg\\_%' ORDER BY schema_name";

    private const string SqlServerPrimaryKeys = @"
SELECT o.name, col.name, ic.key_ordinal
FROM sys.indexes i
JOIN sys.objects o ON o.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
WHERE i.is_primary_key = 1 AND ic.is_included_column = 0 AND o.type = 'U' AND s.name = @schema
ORDER BY o.name, ic.key_ordinal";

    private const string PostgresPrimaryKeys = @"
SELECT t.relname, a.attname, k.ord
FROM pg_index i
JOIN pg_class t ON t.oid = i.indrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
WHERE i.indisprimary AND k.ord <= i.indnkeyatts AND n.nspname = @schema
ORDER BY t.relname, k.ord";

    // one row per column of a foreign key: its table, the key's name, the column, the referenced schema, table and column, and the position in the key
    private const string SqlServerForeignKeys = @"
SELECT o.name, fk.name, pc.name, rs.name, ro.name, rc.name, fkc.constraint_column_id
FROM sys.foreign_keys fk
JOIN sys.objects o ON o.object_id = fk.parent_object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE s.name = @schema
ORDER BY o.name, fk.name, fkc.constraint_column_id";

    private const string PostgresForeignKeys = @"
SELECT t.relname, c.conname, a.attname, rn.nspname, rt.relname, ra.attname, k.ord
FROM pg_constraint c
JOIN pg_class t ON t.oid = c.conrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
JOIN pg_class rt ON rt.oid = c.confrelid
JOIN pg_namespace rn ON rn.oid = rt.relnamespace
CROSS JOIN LATERAL unnest(c.conkey, c.confkey) WITH ORDINALITY AS k(attnum, refnum, ord)
JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
JOIN pg_attribute ra ON ra.attrelid = c.confrelid AND ra.attnum = k.refnum
WHERE c.contype = 'f' AND n.nspname = @schema
ORDER BY t.relname, c.conname, k.ord";

    // DuckDB: its own schemas are information_schema and pg_catalog (`main` is where tables go when no schema is named); the constraints come from duckdb_constraints(), parameters by position
    private const string DuckDbSchemas = "SELECT schema_name FROM information_schema.schemata WHERE catalog_name = current_database() AND schema_name NOT IN ('information_schema', 'pg_catalog') ORDER BY schema_name";
    private const string DuckDbPrimaryKeys = "SELECT table_name, array_to_string(constraint_column_names, chr(31)) FROM duckdb_constraints() WHERE constraint_type = 'PRIMARY KEY' AND database_name = current_database() AND schema_name = ? ORDER BY table_name";
    // duckdb_indexes(): the columns of an index are in the CREATE INDEX text (its `expressions` column is a VARCHAR that spells a list); ART indexes are ascending and cannot be INCLUDE-d
    private const string DuckDbIndexes = "SELECT table_name, index_name, is_unique, sql FROM duckdb_indexes() WHERE database_name = current_database() AND schema_name = ? ORDER BY table_name, index_name";
    private const string DuckDbForeignKeys = "SELECT table_name, constraint_name, array_to_string(constraint_column_names, chr(31)), referenced_table, array_to_string(referenced_column_names, chr(31)), constraint_text FROM duckdb_constraints() WHERE constraint_type = 'FOREIGN KEY' AND database_name = current_database() AND schema_name = ? ORDER BY table_name, constraint_name";

    // A source known by its ODBC driver: the standard views. sys and pg_catalog are the engines' own and are left out whichever engine is behind the driver; every other schema name is the person's to select with the pattern.
    private const string AnsiSchemas = "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('information_schema', 'pg_catalog', 'sys') ORDER BY schema_name";
    private const string AnsiPrimaryKeys = @"
SELECT kcu.table_name, kcu.column_name, kcu.ordinal_position
FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu ON kcu.constraint_schema = tc.constraint_schema AND kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = ?
ORDER BY kcu.table_name, kcu.ordinal_position";
    private const string AnsiForeignKeys = @"
SELECT kcu.table_name, kcu.constraint_name, kcu.column_name, rkcu.table_schema, rkcu.table_name, rkcu.column_name, kcu.ordinal_position
FROM information_schema.referential_constraints rc
JOIN information_schema.key_column_usage kcu ON kcu.constraint_schema = rc.constraint_schema AND kcu.constraint_name = rc.constraint_name
JOIN information_schema.key_column_usage rkcu ON rkcu.constraint_schema = rc.unique_constraint_schema AND rkcu.constraint_name = rc.unique_constraint_name AND rkcu.ordinal_position = kcu.position_in_unique_constraint
WHERE kcu.table_schema = ?
ORDER BY kcu.table_name, kcu.constraint_name, kcu.ordinal_position";

    private static bool IsAnsiSource(string target) => target is "odbc" or "flightsql";

    // For a Flight SQL server the metadata calls come first and `information_schema` is the fallback; for an ODBC source whose dialect says `catalog: odbc` the driver's catalog functions are the only way.
    private static IMetadataCatalog? FlightSqlMetadata(ReadSession read, string target) => target == "flightsql" || OdbcFunctions(read, target) ? read.Metadata : null;

    private static bool OdbcFunctions(ReadSession read, string target) => target == "odbc" && read.Dialect.Catalog == Models.CatalogStyle.Odbc;

    private static async Task<T> FlightSqlFallbackAsync<T>(ReadSession read, Func<Task<T>> informationSchema)
    {
        try { return await informationSchema(); }
        catch (Exception ex) when (DriverFailure.Rpc(ex) is { } rpc && rpc.StatusCode is not (Grpc.Core.StatusCode.Unavailable or Grpc.Core.StatusCode.Unauthenticated or Grpc.Core.StatusCode.PermissionDenied or Grpc.Core.StatusCode.DeadlineExceeded or Grpc.Core.StatusCode.Cancelled))
        { throw CatalogUnavailableException.FlightSqlWithoutCatalog(read.Dialect.Name); }
    }

    private static void RequireCatalog(ReadSession read)
    {
        if (read.Dialect.Catalog == Models.CatalogStyle.None) throw new CatalogUnavailableException(read.Dialect.Name);
    }

    public static async Task<IReadOnlyList<string>> SchemasAsync(ReadSession read, string target, CancellationToken ct = default)
    {
        if (IsAnsiSource(target))
        {
            RequireCatalog(read);
            if (FlightSqlMetadata(read, target) is { } metadata && await metadata.SchemasAsync(ct) is { } named) return named.Where(n => n is not ("information_schema" or "pg_catalog" or "sys")).ToList();
            if (OdbcFunctions(read, target)) throw CatalogUnavailableException.OdbcWithoutFunction(read.Dialect.Name, "SQLTables");
            Task<IReadOnlyList<string>> ask() => ReadSchemaNamesAsync(read, ct);
            return target == "flightsql" ? await FlightSqlFallbackAsync(read, ask) : await ask();
        }
        var rows = await read.QueryAsync(target == "duckdb" ? DuckDbSchemas : target == "postgres" ? PostgresSchemas : SqlServerSchemas, null, ct);
        return rows.Select(r => (string)r[0]!).ToList();
    }

    private static async Task<IReadOnlyList<string>> ReadSchemaNamesAsync(ReadSession read, CancellationToken ct) =>
        (await read.QueryAsync(AnsiSchemas, null, ct)).Select(r => r[0]!.ToString()!).ToList();

    /// <summary>Primary key columns, in key order, per table name of the schema name.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> PrimaryKeysAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        if (IsAnsiSource(target))
        {
            RequireCatalog(read);
            if (FlightSqlMetadata(read, target) is { } metadata && await metadata.PrimaryKeysAsync(schema, ct) is { } offered) return offered;
            if (OdbcFunctions(read, target)) { read.Note("primary keys were not read: the ODBC driver does not offer SQLPrimaryKeys, so a descriptor's grain is declared by hand"); return new Dictionary<string, IReadOnlyList<string>>(); }
            var ansi = target == "flightsql" ? await FlightSqlFallbackAsync(read, () => read.QueryAsync(AnsiPrimaryKeys, [new GateParameter("schema", DbType.String, schema)], ct))
                : await read.QueryAsync(AnsiPrimaryKeys, [new GateParameter("schema", DbType.String, schema)], ct);
            return ansi.GroupBy(r => r[0]!.ToString()!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.OrderBy(r => Convert.ToInt32(r[2], CultureInfo.InvariantCulture)).Select(r => r[1]!.ToString()!).ToList(), StringComparer.Ordinal);
        }
        if (target == "duckdb")
        {
            var keys = await read.QueryAsync(DuckDbPrimaryKeys, [new GateParameter("schema", DbType.String, schema)], ct);
            return keys.ToDictionary(r => (string)r[0]!, r => (IReadOnlyList<string>)((string)r[1]!).Split('\u001f').ToList(), StringComparer.Ordinal);
        }
        var rows = await read.QueryAsync(target == "postgres" ? PostgresPrimaryKeys : SqlServerPrimaryKeys, [new GateParameter("schema", DbType.String, schema)], ct);
        return rows.GroupBy(r => (string)r[0]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.OrderBy(r => Convert.ToInt32(r[2], CultureInfo.InvariantCulture)).Select(r => (string)r[1]!).ToList(), StringComparer.Ordinal);
    }

    /// <summary>Foreign keys per table name of the schema name, columns in key order.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<State.ForeignKeyShape>>> ForeignKeysAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        if (IsAnsiSource(target))
        {
            RequireCatalog(read);
            if (FlightSqlMetadata(read, target) is { } metadata && await metadata.ForeignKeysAsync(schema, ct) is { } offered)
                return offered.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<State.ForeignKeyShape>)kv.Value.Select(k => new State.ForeignKeyShape(k.Name, k.Columns, k.ReferencedSchema, k.ReferencedTable, k.ReferencedColumns)).ToList(), StringComparer.Ordinal);
            if (OdbcFunctions(read, target)) { read.Note("foreign keys were not read: the ODBC driver does not offer SQLForeignKeys"); return new Dictionary<string, IReadOnlyList<State.ForeignKeyShape>>(); }
            var ansi = target == "flightsql" ? await FlightSqlFallbackAsync(read, () => read.QueryAsync(AnsiForeignKeys, [new GateParameter("schema", DbType.String, schema)], ct))
                : await read.QueryAsync(AnsiForeignKeys, [new GateParameter("schema", DbType.String, schema)], ct);
            return ansi.GroupBy(r => r[0]!.ToString()!, StringComparer.Ordinal).ToDictionary(
                table => table.Key,
                table => (IReadOnlyList<State.ForeignKeyShape>)table.GroupBy(r => r[1]!.ToString()!, StringComparer.Ordinal).Select(fk =>
                {
                    var ordered = fk.OrderBy(r => Convert.ToInt32(r[6], CultureInfo.InvariantCulture)).ToList();
                    return new State.ForeignKeyShape(fk.Key, ordered.Select(r => r[2]!.ToString()!).ToList(), ordered[0][3]!.ToString()!, ordered[0][4]!.ToString()!, ordered.Select(r => r[5]!.ToString()!).ToList());
                }).ToList(),
                StringComparer.Ordinal);
        }
        if (target == "duckdb")
        {
            // duckdb_constraints() has no column for the schema of the referenced table; its constraint text does (`FOREIGN KEY (a) REFERENCES s.p(id)`), and says none when the table is in the key's own schema.
            // (DuckDB 1.5 refuses a foreign key across schemas, so today the answer is always the key's own schema; reading the text keeps it true if a later release allows more.)
            var keys = await read.QueryAsync(DuckDbForeignKeys, [new GateParameter("schema", DbType.String, schema)], ct);
            return keys.GroupBy(r => (string)r[0]!, StringComparer.Ordinal).ToDictionary(
                table => table.Key,
                table => (IReadOnlyList<State.ForeignKeyShape>)table.Select(r => new State.ForeignKeyShape((string)r[1]!, ((string)r[2]!).Split('\u001f').ToList(), DuckDbReferencedSchema(r[5] as string, (string)r[3]!, schema), (string)r[3]!, ((string)r[4]!).Split('\u001f').ToList())).ToList(),
                StringComparer.Ordinal);
        }
        var rows = await read.QueryAsync(target == "postgres" ? PostgresForeignKeys : SqlServerForeignKeys, [new GateParameter("schema", DbType.String, schema)], ct);
        return rows.GroupBy(r => (string)r[0]!, StringComparer.Ordinal).ToDictionary(
            table => table.Key,
            table => (IReadOnlyList<State.ForeignKeyShape>)table.GroupBy(r => (string)r[1]!, StringComparer.Ordinal).Select(fk =>
            {
                var ordered = fk.OrderBy(r => Convert.ToInt32(r[6], CultureInfo.InvariantCulture)).ToList();
                return new State.ForeignKeyShape(fk.Key, ordered.Select(r => (string)r[2]!).ToList(), (string)ordered[0][3]!, (string)ordered[0][4]!, ordered.Select(r => (string)r[5]!).ToList());
            }).ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The schema a DuckDB foreign key points to, from the key's constraint text: what stands between `REFERENCES ` and the referenced table's name (`s.` or `catalog.s.`), or the key's own schema when the text names none.
    /// A name with a dot in it (quoted) cannot be told from a qualifier by its text; the part after the last dot is taken.
    /// </summary>
    internal static string DuckDbReferencedSchema(string? constraintText, string referencedTable, string ownSchema)
    {
        if (constraintText == null) return ownSchema;
        var start = constraintText.IndexOf("REFERENCES ", StringComparison.Ordinal);
        var at = constraintText.LastIndexOf(referencedTable + "(", StringComparison.Ordinal);
        if (start < 0 || at < 0 || at < start + 11) return ownSchema;
        var qualifier = constraintText[(start + 11)..at].TrimEnd('.').Trim('"');
        if (qualifier.Length == 0) return ownSchema;
        var dot = qualifier.LastIndexOf('.');
        return (dot < 0 ? qualifier : qualifier[(dot + 1)..]).Trim('"');
    }

    /// <summary>
    /// The key columns of a DuckDB index from its `CREATE INDEX` text, or `&lt;expression&gt;` where a key is an expression (the importer says such an index was not exported). Null when the text is not a
    /// CREATE INDEX this reader understands.
    /// </summary>
    internal static IReadOnlyList<string>? DuckDbIndexColumns(string? createIndex)
    {
        if (createIndex == null) return null;
        var on = createIndex.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
        var open = on < 0 ? -1 : createIndex.IndexOf('(', on);
        var close = createIndex.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        var columns = new List<string>();
        var (depth, quoted, from) = (0, false, open + 1);
        for (var i = open + 1; i <= close; i++)
        {
            var c = createIndex[i];
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            else if ((c == ',' && depth == 0) || (c == ')' && depth == 0 && i == close))
            {
                columns.Add(IndexKey(createIndex[from..i].Trim()));
                from = i + 1;
            }
        }
        return columns;
    }

    private static string IndexKey(string text)
    {
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && !text[1..^1].Replace("\"\"", "", StringComparison.Ordinal).Contains('"', StringComparison.Ordinal)) return text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(ch => char.IsLetterOrDigit(ch) || ch == '_') ? text : "<expression>";
    }

    /// <summary>
    /// The indexes of the tables of a schema name where the origin's catalog says them, per table: DuckDB's `duckdb_indexes()`, an ODBC driver's `SQLStatistics`. Null where there is nothing to add: the three engines
    /// give theirs with the columns (<see cref="CatalogReader"/>); a Flight SQL server has no metadata call for indexes and `information_schema` has no indexes view, so those say so in <see cref="ReadSession.CatalogNotes"/>
    /// instead of guessing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<State.PhysicalItem>>?> IndexesAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        static IReadOnlyDictionary<string, IReadOnlyList<State.PhysicalItem>> Items(IEnumerable<(string Table, string Name, bool Unique, IReadOnlyList<string> Columns, IReadOnlyList<string> Include)> indexes) =>
            indexes.GroupBy(i => i.Table, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => (IReadOnlyList<State.PhysicalItem>)g.Select(i => new State.PhysicalItem("index", i.Name, State.IndexText.Canonical(i.Unique, i.Columns, i.Include))).ToList(), StringComparer.Ordinal);

        if (target == "duckdb")
        {
            var rows = await read.QueryAsync(DuckDbIndexes, [new GateParameter("schema", DbType.String, schema)], ct);
            var parsed = rows.Select(r => (Table: (string)r[0]!, Name: (string)r[1]!, Unique: Convert.ToBoolean(r[2], CultureInfo.InvariantCulture), Columns: DuckDbIndexColumns(r[3] as string))).ToList();
            foreach (var bad in parsed.Where(p => p.Columns == null)) read.Note($"index {bad.Name} of {schema}.{bad.Table} was not understood and was not exported");
            return Items(parsed.Where(p => p.Columns != null).Select(p => (p.Table, p.Name, p.Unique, p.Columns!, (IReadOnlyList<string>)[])));
        }
        if (target == "flightsql")
        {
            read.Note("indexes were not read: Flight SQL has no metadata call for indexes, so declare them by hand");
            return null;
        }
        if (target != "odbc" || read.Dialect.Catalog == Models.CatalogStyle.None) return null;
        try
        {
            if (read.Metadata is { } metadata && await metadata.IndexesAsync(schema, ct) is { } offered)
                return Items(offered.Values.SelectMany(x => x).Select(i => (i.Table, i.Name, i.Unique, i.Columns, i.Include)));
            read.Note("indexes were not read: the ODBC driver does not offer SQLStatistics, so declare them by hand");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the catalog functions need the driver manager's library to load and the driver to answer; an import without indexes is still an import
            read.Note($"indexes were not read: SQLStatistics failed ({DriverFailure.Describe(ex)}), so declare them by hand");
        }
        return null;
    }
}
