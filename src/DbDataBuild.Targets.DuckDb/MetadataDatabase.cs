using System.Globalization;
using System.Text.Json;
using DbDataBuild.Core;
using DuckDB.NET.Data;

namespace DbDataBuild.Targets.DuckDb;

/// <summary>One metadata document to load: what `metadata` prints and `publish-metadata` stores.</summary>
public sealed record MetadataDocument(string SubjectKind, string Subject, string Json, string Hash);

public enum RuleOutcome { Ran, NotASelect, CouldNotRun }

/// <summary>The result of running one rule: its columns, the first rows as plain data, and how many rows there were in all.</summary>
public sealed record RuleRun(RuleOutcome Outcome, string? Message, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, int Count);

/// <summary>
/// The metadata documents as relational views in an in-memory DuckDB, so a project can write rules over them in SQL (DESIGN.md 9.8). `metadata_current` and `metadata_columns` have the
/// names and columns of the views `init` creates in a target; the rest flatten the documents into one row per thing a rule is likely to ask about. Built from the same documents
/// `metadata` prints and `publish-metadata` stores, so what a rule sees is exactly what is published. File and network access are off, like every DuckDB the tool opens; nothing here
/// touches a target.
/// </summary>
public sealed class MetadataDatabase : IDisposable
{
    private const string Table = "CREATE TABLE metadata_current(connection VARCHAR, subject_kind VARCHAR, subject VARCHAR, document JSON, document_hash VARCHAR, recorded_utc TIMESTAMP, tool_version VARCHAR, plan_id VARCHAR, git_commit VARCHAR, model_kind VARCHAR)";

    private const string DependencyTable = "CREATE TABLE metadata_dependency(connection VARCHAR, subject VARCHAR, depends_on VARCHAR, depends_on_category VARCHAR, depends_on_kind VARCHAR, depth INTEGER)";

    private const string Views = """
CREATE VIEW metadata_columns AS
SELECT m.connection AS connection, m.subject AS model, c->>'name' AS column_name, c->>'logical_type' AS logical_type, (c->>'nullable')::BOOLEAN AS nullable, c->>'collation' AS collation,
  c->'native'->'sqlserver'->>'type' AS sqlserver_type, c->'native'->'postgres'->>'type' AS postgres_type, c->'native'->'fabric'->>'type' AS fabric_type,
  c->'lineage'->>'inferred_nullability' AS inferred_nullability, c->'lineage'->'upstream' AS upstream, m.model_kind AS model_kind
FROM metadata_current m, unnest(CAST(m.document->'columns' AS JSON[])) AS t(c)
WHERE m.subject_kind = 'model';

CREATE VIEW metadata_models AS
SELECT m.subject AS model, m.model_kind AS kind, m.document->'kind_settings' AS kind_settings,
  CAST(m.document->'kind_settings'->'unique_key' AS VARCHAR[]) AS unique_key, m.document->'kind_settings'->>'time_column' AS time_column, m.document->'kind_settings'->>'lookback' AS lookback,
  CAST(m.document->'grain' AS VARCHAR[]) AS grain, CAST(m.document->'connections' AS VARCHAR[]) AS connections,
  m.document->'files'->>'definition' AS definition_file, m.document->'files'->>'query' AS query_file, m.document->>'definition_hash' AS definition_hash,
  json_array_length(m.document->'columns') AS column_count,
  m.document->>'title' AS title, m.document->>'description' AS description, CAST(m.document->'tags' AS VARCHAR[]) AS tags
FROM metadata_current m WHERE m.subject_kind = 'model';

CREATE VIEW metadata_upstream AS
SELECT d.subject AS model, d.depends_on AS upstream, d.depends_on_category AS upstream_category, d.depends_on_kind AS upstream_kind FROM metadata_dependency d WHERE d.depth = 1;

CREATE VIEW metadata_ancestors AS
SELECT d.subject AS model, d.depends_on AS ancestor, d.depth AS depth, d.depends_on_category AS ancestor_category, d.depends_on_kind AS ancestor_kind FROM metadata_dependency d;

CREATE VIEW metadata_lineage AS
SELECT m.subject AS model, c->>'name' AS column_name, c->'lineage'->>'transform' AS transform, c->'lineage'->>'cast_type' AS cast_type,
  u->>'table' AS upstream_table, u->>'column' AS upstream_column
FROM metadata_current m, unnest(CAST(m.document->'columns' AS JSON[])) AS t(c),
  unnest(CASE WHEN json_array_length(c->'lineage'->'upstream') > 0 THEN CAST(c->'lineage'->'upstream' AS JSON[]) ELSE [NULL::JSON] END) AS v(u)
WHERE m.subject_kind = 'model' AND m.model_kind NOT IN ('mapped', 'native');

CREATE VIEW metadata_native_types AS
SELECT m.subject AS model, c->>'name' AS column_name, k.conn AS connection, json_extract_string(c->'native', '$."' || k.conn || '"."type"') AS native_type,
  json_extract_string(c->'native', '$."' || k.conn || '"."collation"') AS collation, json_extract_string(c->'native', '$."' || k.conn || '"."error"') AS error
FROM metadata_current m, unnest(CAST(m.document->'columns' AS JSON[])) AS t(c), unnest(json_keys(c->'native')) AS k(conn)
WHERE m.subject_kind = 'model';

CREATE VIEW metadata_indexes AS
SELECT m.subject AS model, i->>'name' AS index_name, CAST(i->'columns' AS VARCHAR[]) AS columns, (i->>'unique')::BOOLEAN AS is_unique,
  CAST(i->'include' AS VARCHAR[]) AS include, CAST(i->'connections' AS VARCHAR[]) AS connections
FROM metadata_current m, unnest(CAST(m.document->'indexes' AS JSON[])) AS t(i) WHERE m.subject_kind = 'model';

CREATE VIEW metadata_foreign_keys AS
SELECT m.subject AS model, f->>'name' AS foreign_key_name, CAST(f->'columns' AS VARCHAR[]) AS columns, f->'references'->>'table' AS referenced_table, CAST(f->'references'->'columns' AS VARCHAR[]) AS referenced_columns
FROM metadata_current m, unnest(CAST(m.document->'foreign_keys' AS JSON[])) AS t(f) WHERE m.subject_kind = 'model';

CREATE VIEW metadata_loads AS
SELECT m.subject AS model, l->>'connection' AS connection, l->>'operation' AS operation, l->>'strategy' AS strategy, (l->>'is_default')::BOOLEAN AS is_default,
  l->>'matrix_status' AS matrix_status, CAST(l->'findings' AS VARCHAR[]) AS findings, l->>'script_path' AS script_path, l->>'script_hash' AS script_hash
FROM metadata_current m, unnest(CAST(m.document->'loads' AS JSON[])) AS t(l) WHERE m.subject_kind = 'model';

CREATE VIEW metadata_hooks AS
SELECT m.subject AS model, k.connection, h->>'name' AS hook_name, h->>'event' AS event, h->>'group' AS hook_group, h->>'script' AS script, h->>'effect' AS effect, h->>'risk' AS risk
FROM metadata_current m, unnest(json_keys(m.document->'hooks')) AS k(connection),
  unnest(CAST(json_extract(m.document, '$.hooks."' || k.connection || '"') AS JSON[])) AS t(h) WHERE m.subject_kind = 'model';

CREATE VIEW metadata_info AS
WITH parts AS (
  SELECT m.subject_kind AS subject_kind, m.subject AS subject, '' AS element, m.document AS d FROM metadata_current m WHERE m.subject_kind IN ('project', 'model')
  UNION ALL
  SELECT m.subject_kind, m.subject, 'column:' || (c->>'name'), c FROM metadata_current m, unnest(CAST(m.document->'columns' AS JSON[])) AS t(c) WHERE m.subject_kind = 'model'
  UNION ALL
  SELECT m.subject_kind, m.subject, 'index:' || (i->>'name'), i FROM metadata_current m, unnest(CAST(m.document->'indexes' AS JSON[])) AS t(i) WHERE m.subject_kind = 'model'
  UNION ALL
  SELECT m.subject_kind, m.subject, 'connection:' || k.name, json_extract(m.document, '$.config.connections."' || k.name || '"')
  FROM metadata_current m, unnest(json_keys(m.document->'config'->'connections')) AS k(name) WHERE m.subject_kind = 'project'
)
SELECT subject_kind, subject, element, 'title' AS item, '' AS item_key, CAST(NULL AS VARCHAR) AS value_type, d->>'title' AS value FROM parts WHERE d->>'title' IS NOT NULL
UNION ALL
SELECT subject_kind, subject, element, 'description', '', NULL, d->>'description' FROM parts WHERE d->>'description' IS NOT NULL
UNION ALL
SELECT subject_kind, subject, element, 'tag', x.tag, NULL, NULL FROM parts, unnest(CAST(d->'tags' AS VARCHAR[])) AS x(tag)
UNION ALL
SELECT subject_kind, subject, element, 'property', k.key,
  CASE json_type(json_extract(d->'properties', '$."' || k.key || '"')) WHEN 'VARCHAR' THEN 'text' WHEN 'BOOLEAN' THEN 'boolean' ELSE 'number' END,
  json_extract_string(d->'properties', '$."' || k.key || '"')
FROM parts, unnest(json_keys(d->'properties')) AS k(key);

CREATE VIEW metadata_index_advice AS
SELECT m.subject AS model, a->>'code' AS code, a->>'severity' AS severity, a->>'reason' AS reason, CAST(a->'columns' AS VARCHAR[]) AS columns,
  a->>'suggested' AS suggested_name, a->>'existing_index' AS existing_index, (a->>'silenced')::BOOLEAN AS silenced
FROM metadata_current m, unnest(CAST(m.document->'index_advice' AS JSON[])) AS t(a) WHERE m.subject_kind = 'model';
""";

    /// <summary>The names of the views, for documentation and tests.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "metadata_current", "metadata_dependency", "metadata_columns", "metadata_models", "metadata_upstream", "metadata_ancestors", "metadata_lineage",
        "metadata_native_types", "metadata_indexes", "metadata_foreign_keys", "metadata_loads", "metadata_hooks", "metadata_index_advice", "metadata_info",
    ];

    private readonly DuckDBConnection db;

    private MetadataDatabase(DuckDBConnection db) => this.db = db;

    /// <param name="dependencies">The dependency rows (`metadata_dependency`) the project computes, the same that `publish` stores. Without them the rows are worked out from the documents' `upstream` and `native.reads`.</param>
    public static MetadataDatabase Open(IEnumerable<MetadataDocument> documents, string toolVersion, IEnumerable<DependencyRow>? dependencies = null)
    {
        var all = documents.ToList();
        var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        try
        {
            foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false" }) Exec(db, setting);
            Exec(db, Table);
            Exec(db, DependencyTable);
            foreach (var d in all)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO metadata_current VALUES (NULL, $subject_kind, $subject, CAST($document AS JSON), $hash, NULL, $version, NULL, NULL, $model_kind)";
                cmd.Parameters.Add(new DuckDBParameter("model_kind", MetadataDependencies.ModelKindOf(d.SubjectKind, d.Json)));
                cmd.Parameters.Add(new DuckDBParameter("subject_kind", d.SubjectKind));
                cmd.Parameters.Add(new DuckDBParameter("subject", d.Subject));
                cmd.Parameters.Add(new DuckDBParameter("document", d.Json));
                cmd.Parameters.Add(new DuckDBParameter("hash", d.Hash));
                cmd.Parameters.Add(new DuckDBParameter("version", toolVersion));
                cmd.ExecuteNonQuery();
            }
            foreach (var row in dependencies ?? MetadataDependencies.FromDocuments(all.Select(d => (d.SubjectKind, d.Subject, d.Json))))
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO metadata_dependency VALUES (NULL, $subject, $depends_on, $category, $kind, $depth)";
                cmd.Parameters.Add(new DuckDBParameter("subject", row.Subject));
                cmd.Parameters.Add(new DuckDBParameter("depends_on", row.DependsOn));
                cmd.Parameters.Add(new DuckDBParameter("category", row.DependsOnCategory));
                cmd.Parameters.Add(new DuckDBParameter("kind", row.DependsOnKind));
                cmd.Parameters.Add(new DuckDBParameter("depth", row.Depth));
                cmd.ExecuteNonQuery();
            }
            foreach (var statement in Views.Split(";\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Exec(db, statement.TrimEnd(';'));
            return new MetadataDatabase(db);
        }
        catch { db.Dispose(); throw; }
    }

    /// <summary>Runs one rule: a single SELECT, checked by DuckDB's own parser before anything runs. Reads at most <paramref name="keep"/> rows into the result and counts the rest.</summary>
    public RuleRun Run(string sql, int keep) => DuckSelect.Run(db, sql, keep);

    public void Dispose() => db.Dispose();

    private static void Exec(DuckDBConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Runs one SELECT on a DuckDB connection: parsed first (exactly one SELECT), then read; used for metadata rules and for the `assert` of model tests.</summary>
public static class DuckSelect
{
    public static RuleRun Run(DuckDBConnection db, string sql, int keep)
    {
        string? serialized;
        using (var check = db.CreateCommand())
        {
            check.CommandText = $"SELECT json_serialize_sql('{sql.Replace("'", "''")}')";   // the function takes a constant, not a parameter; a string literal needs only the quote doubled
            serialized = check.ExecuteScalar() as string;
        }
        using (var doc = JsonDocument.Parse(serialized ?? "{\"error\":true,\"error_message\":\"DuckDB could not read the text\"}"))
        {
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetBoolean())
                return new RuleRun(RuleOutcome.NotASelect, Trim(doc.RootElement.TryGetProperty("error_message", out var m) ? m.GetString() ?? "" : ""), [], [], 0);
            if (doc.RootElement.TryGetProperty("statements", out var statements) && statements.GetArrayLength() != 1)
                return new RuleRun(RuleOutcome.NotASelect, $"it has {statements.GetArrayLength()} statements; a test is one query", [], [], 0);
        }

        var columns = new List<string>();
        var rows = new List<Dictionary<string, object?>>();
        var count = 0;
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
            while (reader.Read())
            {
                count++;
                if (rows.Count >= keep) continue;
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var key = row.ContainsKey(columns[i]) ? $"{columns[i]}_{i}" : columns[i];     // a rule may select two columns of one name
                    row[key] = Plain(reader.IsDBNull(i) ? null : reader.GetValue(i));
                }
                rows.Add(row);
            }
        }
        catch (DuckDBException ex)
        {
            return new RuleRun(RuleOutcome.CouldNotRun, Trim(ex.Message), [], [], 0);
        }
        return new RuleRun(RuleOutcome.Ran, null, columns, rows, count);
    }

    private static string Trim(string message) => string.Join(" ", message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(2));

    /// <summary>A value as plain JSON data: lists stay lists, dates are ISO text.</summary>
    private static object? Plain(object? v) => v switch
    {
        null => null,
        string or bool or int or long or short or byte or double or float or decimal => v,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        System.Collections.IEnumerable list => list.Cast<object?>().Select(Plain).ToList(),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };
}
