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
    private const string DuckDbForeignKeys = "SELECT table_name, constraint_name, array_to_string(constraint_column_names, chr(31)), referenced_table, array_to_string(referenced_column_names, chr(31)) FROM duckdb_constraints() WHERE constraint_type = 'FOREIGN KEY' AND database_name = current_database() AND schema_name = ? ORDER BY table_name, constraint_name";

    public static async Task<IReadOnlyList<string>> SchemasAsync(ReadSession read, string target, CancellationToken ct = default)
    {
        var rows = await read.QueryAsync(target == "duckdb" ? DuckDbSchemas : target == "postgres" ? PostgresSchemas : SqlServerSchemas, null, ct);
        return rows.Select(r => (string)r[0]!).ToList();
    }

    /// <summary>Primary key columns, in key order, per table name of the schema name.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> PrimaryKeysAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
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
        if (target == "duckdb")
        {
            // DuckDB does not say which schema the referenced table is in: it is read as the same one
            var keys = await read.QueryAsync(DuckDbForeignKeys, [new GateParameter("schema", DbType.String, schema)], ct);
            return keys.GroupBy(r => (string)r[0]!, StringComparer.Ordinal).ToDictionary(
                table => table.Key,
                table => (IReadOnlyList<State.ForeignKeyShape>)table.Select(r => new State.ForeignKeyShape((string)r[1]!, ((string)r[2]!).Split('\u001f').ToList(), schema, (string)r[3]!, ((string)r[4]!).Split('\u001f').ToList())).ToList(),
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
}
