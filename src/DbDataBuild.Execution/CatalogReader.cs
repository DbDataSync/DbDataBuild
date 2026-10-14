using System.Data;
using System.Globalization;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>Reads the live shape of the objects under a schema name, through the read session (DESIGN.md 12.2: catalog hashing on every target).</summary>
public static class CatalogReader
{
    // Column order matters to nothing here except ordinals; the hashes sort. Types are lower case without parameters.
    private const string SqlServerColumns = @"
SELECT o.name, o.type, c.column_id, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name, cc.definition
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.columns c ON c.object_id = o.object_id
JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
WHERE o.type IN ('U', 'V') AND s.name = @schema AND (@object = '' OR o.name = @object)
ORDER BY o.name, c.column_id";

    // One row per index: its table, name, whether it backs a PRIMARY KEY or UNIQUE constraint, uniqueness, key columns in order (with `desc` when descending) and included columns.
    // The canonical definition built from these is engine-neutral, so a declared index can be compared with a live one.
    private const string SqlServerIndexes = @"
SELECT o.name, i.name, CASE WHEN i.is_primary_key = 1 OR i.is_unique_constraint = 1 THEN 1 ELSE 0 END, i.is_unique,
  (SELECT STRING_AGG(CONCAT(col.name, CASE WHEN ic.is_descending_key = 1 THEN ' desc' ELSE '' END), ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
   FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0),
  (SELECT STRING_AGG(col.name, ',') WITHIN GROUP (ORDER BY ic.index_column_id)
   FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1),
  (SELECT MIN(p.data_compression_desc) FROM sys.partitions p WHERE p.object_id = i.object_id AND p.index_id = i.index_id),
  (SELECT COUNT(*) FROM sys.partitions p WHERE p.object_id = i.object_id AND p.index_id = i.index_id)
FROM sys.indexes i
JOIN sys.objects o ON o.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type = 'U' AND i.type > 0 AND s.name = @schema AND (@object = '' OR o.name = @object)
ORDER BY o.name, i.name";

    private const string PostgresColumns = @"
SELECT t.table_name, t.table_type, c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale,
       c.datetime_precision, c.is_nullable, c.collation_name, c.is_generated, c.generation_expression
FROM information_schema.tables t
JOIN information_schema.columns c ON c.table_schema = t.table_schema AND c.table_name = t.table_name
WHERE t.table_schema = @schema AND t.table_type IN ('BASE TABLE', 'VIEW') AND (@object = '' OR t.table_name = @object)
ORDER BY t.table_name, c.ordinal_position";

    private const string PostgresIndexes = @"
SELECT t.relname, ic.relname, CASE WHEN EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid) THEN 1 ELSE 0 END, CASE WHEN i.indisunique THEN 1 ELSE 0 END,
  (SELECT string_agg(COALESCE(a.attname, '<expression>') || CASE WHEN (i.indoption[k.ord - 1] & 1) = 1 THEN ' desc' ELSE '' END, ',' ORDER BY k.ord)
   FROM unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) LEFT JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum WHERE k.ord <= i.indnkeyatts),
  (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
   FROM unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum WHERE k.ord > i.indnkeyatts)
FROM pg_index i
JOIN pg_class ic ON ic.oid = i.indexrelid
JOIN pg_class t ON t.oid = i.indrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE n.nspname = @schema AND t.relkind = 'r' AND (@object = '' OR t.relname = @object)
ORDER BY t.relname, ic.relname";

    // DuckDB: the standard views, with the parameters by position (schema, object, object). Its indexes are not read: an ART index is rare in a file that is read, and its definition is a CREATE INDEX text.
    private const string DuckDbColumns = @"
SELECT t.table_name, t.table_type, c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_nullable
FROM information_schema.tables t
JOIN information_schema.columns c ON c.table_catalog = t.table_catalog AND c.table_schema = t.table_schema AND c.table_name = t.table_name
WHERE t.table_catalog = current_database() AND t.table_schema = ? AND t.table_type IN ('BASE TABLE', 'VIEW') AND (? = '' OR t.table_name = ?)
ORDER BY t.table_name, c.ordinal_position";

    /// <param name="objectName">Only this object of the schema name (a step that checks what it just did reads one object, not the hundreds a schema name may hold); null reads them all.</param>
    public static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadObjectsAsync(ReadSession read, string target, string schema, CancellationToken ct = default, string? objectName = null)
    {
        if (target == "duckdb") return await ReadDuckDbObjectsAsync(read, schema, objectName, ct);
        var p = new[] { new GateParameter("schema", DbType.String, schema), new GateParameter("object", DbType.String, objectName ?? "") };
        var postgres = target == "postgres";
        var columnRows = await read.QueryAsync(postgres ? PostgresColumns : SqlServerColumns, p, ct);
        var indexRows = await read.QueryAsync(postgres ? PostgresIndexes : SqlServerIndexes, p, ct);

        var objects = new Dictionary<string, (ObjectKind Kind, List<ColumnShape> Columns, List<PhysicalItem> Physical)>(StringComparer.Ordinal);
        foreach (var r in columnRows)
        {
            var name = (string)r[0]!;
            if (!objects.TryGetValue(name, out var o)) objects[name] = o = (KindOf(postgres, r[1]!), [], []);
            o.Columns.Add(postgres ? PostgresColumn(r) : SqlServerColumn(r));
        }
        foreach (var r in indexRows)
        {
            var table = (string)r[0]!;
            if (!objects.TryGetValue(table, out var o)) continue;
            var backsConstraint = Convert.ToInt32(r[2], CultureInfo.InvariantCulture) == 1;
            var unique = Convert.ToInt32(r[3], CultureInfo.InvariantCulture) == 1;
            o.Physical.Add(new(backsConstraint ? "constraint_index" : "index", (string)r[1]!, IndexText.Canonical(unique, (r[4] as string ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), (r[5] as string ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))));
            if (!postgres) o.Physical.Add(new("compression", (string)r[1]!, $"{r[6]} partitions={r[7]}"));
        }
        return objects.ToDictionary(kv => $"{schema}.{kv.Key}", kv => new ObjectShape(schema, kv.Key, kv.Value.Kind, kv.Value.Columns, kv.Value.Physical));
    }

    private static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadDuckDbObjectsAsync(ReadSession read, string schema, string? objectName, CancellationToken ct)
    {
        var name = objectName ?? "";
        var rows = await read.QueryAsync(DuckDbColumns, [new GateParameter("schema", DbType.String, schema), new GateParameter("object", DbType.String, name), new GateParameter("object", DbType.String, name)], ct);
        var objects = new Dictionary<string, (ObjectKind Kind, List<ColumnShape> Columns)>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var table = (string)r[0]!;
            if (!objects.TryGetValue(table, out var o)) objects[table] = o = ((string)r[1]! == "VIEW" ? ObjectKind.View : ObjectKind.Table, []);
            var declared = ((string)r[4]!).ToLowerInvariant();
            var type = declared.Contains('(') ? declared[..declared.IndexOf('(')].Trim() : declared;          // DECIMAL(18,3) is the type decimal with a precision and scale of its own
            var decimalType = type == "decimal";
            o.Columns.Add(new((string)r[3]!, type, Int(r[5]), decimalType ? Int(r[6]) : null, decimalType ? Int(r[7]) : null, (string?)r[8] == "YES", null, null));
        }
        return objects.ToDictionary(kv => $"{schema}.{kv.Key}", kv => new ObjectShape(schema, kv.Key, kv.Value.Kind, kv.Value.Columns, []));
    }

    private static ObjectKind KindOf(bool postgres, object kind) =>
        (postgres ? (string)kind == "VIEW" : ((string)kind).Trim() == "V") ? ObjectKind.View : ObjectKind.Table;

    private static int? Int(object? v) => v == null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static ColumnShape SqlServerColumn(IReadOnlyList<object?> r)
    {
        var type = ((string)r[4]!).ToLowerInvariant();
        var bytes = Int(r[5])!.Value;
        int? length = type switch
        {
            "char" or "varchar" or "binary" or "varbinary" => bytes,
            "nchar" or "nvarchar" => bytes == -1 ? -1 : bytes / 2,
            _ => null,
        };
        int? precision = type is "decimal" or "numeric" ? Int(r[6]) : null;
        int? scale = type is "decimal" or "numeric" or "datetime2" or "time" or "datetimeoffset" ? Int(r[7]) : null;
        return new((string)r[3]!, type, length, precision, scale, Convert.ToBoolean(r[8], CultureInfo.InvariantCulture), r[9] as string, r[10] as string);
    }

    private static ColumnShape PostgresColumn(IReadOnlyList<object?> r)
    {
        var type = ((string)r[4]!).ToLowerInvariant();
        var temporal = type.StartsWith("timestamp") || type.StartsWith("time");
        int? precision = type == "numeric" ? Int(r[6]) : null;
        int? scale = type == "numeric" ? Int(r[7]) : temporal ? Int(r[8]) : null;
        var generated = (string?)r[11] == "ALWAYS" ? r[12] as string : null;
        return new((string)r[3]!, type, Int(r[5]), precision, scale, (string?)r[9] == "YES", r[10] as string, generated);
    }
}
