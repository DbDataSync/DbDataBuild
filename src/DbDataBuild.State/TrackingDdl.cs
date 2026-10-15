using DbDataBuild.Core;
using System.Text;

namespace DbDataBuild.State;

/// <summary>One statement of the init script. The id is stable, so the statement log and a reviewer can refer to it.</summary>
public sealed record InitStatement(string Id, string Description, string Text);

/// <summary>Creates the tracking tables, and the schema name they live in, for one engine from the logical definition. Scripts are idempotent: running one twice changes nothing.</summary>
public interface ITrackingDdl
{
    string Target { get; }
    /// <summary>True when the output has not been run on that engine (no Fabric engine has been available).</summary>
    bool Unverified { get; }
    string Quote(string identifier);
    IReadOnlyList<InitStatement> InitScript(string schema, string toolVersion);

    /// <summary>
    /// Brings tracking tables of an older layout (before 4: no `connection` column in any record) to this one, in place: each table that exists without the column gets it, every existing row is
    /// given <paramref name="connection"/>, and the primary key becomes the one that includes it; the two views are dropped (the init script that follows creates them again). Safe to run on a layout
    /// that is current (it changes nothing but the views). Run before <see cref="InitScript"/>.
    /// </summary>
    IReadOnlyList<InitStatement> UpgradeScript(string schema, string connection);
}

public static class TrackingDdl
{
    public static ITrackingDdl For(string target) => target switch
    {
        "sqlserver" => new TSqlTrackingDdl("sqlserver", unverified: false),
        "fabric" => new TSqlTrackingDdl("fabric", unverified: true),
        "postgres" => new PostgresTrackingDdl(),
        _ => throw new ArgumentException($"Unknown target `{target}`.", nameof(target)),
    };

    /// <summary>The reviewable text of an init script: each statement under a comment naming its id.</summary>
    public static string Render(IReadOnlyList<InitStatement> script)
    {
        var sb = new StringBuilder();
        foreach (var s in script) sb.Append("-- ").Append(s.Id).Append(": ").AppendLineLf(s.Description).AppendLineLf(s.Text.TrimEnd()).AppendLineLf();
        return sb.ToString();
    }

    internal static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
}

internal sealed class TSqlTrackingDdl(string target, bool unverified) : ITrackingDdl
{
    public string Target => target;
    public bool Unverified => unverified;
    public string Quote(string id) => "[" + id.Replace("]", "]]") + "]";

    // Fabric has no NVARCHAR, and its keys must be declared NONCLUSTERED ... NOT ENFORCED (Microsoft's T-SQL surface area page, 2026-09-30).
    private string Native(TrackingType t) => t switch
    {
        TrackingType.Name => target == "fabric" ? "varchar(512)" : "nvarchar(512)",
        TrackingType.Short => target == "fabric" ? "varchar(128)" : "varchar(128)",
        TrackingType.Hash => "char(64)",
        TrackingType.Long => target == "fabric" ? "varchar(max)" : "nvarchar(max)",
        TrackingType.BigInt => "bigint",
        TrackingType.Int => "int",
        TrackingType.Guid => "uniqueidentifier",
        TrackingType.TimestampUtc => target == "fabric" ? "datetime2(6)" : "datetime2(3)",
        TrackingType.Json => target == "fabric" ? "varchar(max)" : "nvarchar(max)",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public IReadOnlyList<InitStatement> UpgradeScript(string schema, string connection)
    {
        if (target == "fabric") throw new NotSupportedException("The upgrade of an older tracking layout has not been verified on Fabric.");
        var s = Quote(schema);
        var list = new List<InitStatement>();
        var n = 0;
        foreach (var t in TrackingSchema.Tables.Where(t => t.Columns.Any(c => c.Name == "connection")))
        {
            var obj = $"{s}.{Quote(t.Name)}";
            var key = string.Join(", ", t.PrimaryKey.Select(Quote));
            var column = t.Columns.First(c => c.Name == "connection");
            list.Add(new($"upgrade-{++n:00}", $"table {t.Name}: add `connection`, set to {connection} on the rows it has, and put it in the primary key",
                $"IF OBJECT_ID({TrackingDdl.Literal(schema + "." + t.Name)}, N'U') IS NOT NULL AND COL_LENGTH({TrackingDdl.Literal(schema + "." + t.Name)}, 'connection') IS NULL\nBEGIN\n" +
                $"  ALTER TABLE {obj} ADD {Quote("connection")} {Native(column.Type)} NOT NULL CONSTRAINT {Quote("df_" + t.Name + "_connection")} DEFAULT {TrackingDdl.Literal(connection)};\n" +
                $"  ALTER TABLE {obj} DROP CONSTRAINT {Quote("df_" + t.Name + "_connection")};\n" +
                $"  ALTER TABLE {obj} DROP CONSTRAINT {Quote("pk_" + t.Name)};\n" +
                $"  ALTER TABLE {obj} ADD CONSTRAINT {Quote("pk_" + t.Name)} PRIMARY KEY ({key});\nEND;"));
        }
        foreach (var (table, column) in TrackingSchema.AddedInLayout5)
        {
            var c = TrackingSchema.Table(table).Columns.First(x => x.Name == column);
            list.Add(new($"upgrade-{++n:00}", $"table {table}: add `{column}` (layout 5) when it is not there",
                $"IF OBJECT_ID({TrackingDdl.Literal(schema + "." + table)}, N'U') IS NOT NULL AND COL_LENGTH({TrackingDdl.Literal(schema + "." + table)}, {TrackingDdl.Literal(column)}) IS NULL\n  ALTER TABLE {s}.{Quote(table)} ADD {Quote(column)} {Native(c.Type)} NULL;"));
        }
        foreach (var (table, column) in TrackingSchema.AddedInLayout7)
        {
            var c = TrackingSchema.Table(table).Columns.First(x => x.Name == column);
            list.Add(new($"upgrade-{++n:00}", $"table {table}: add `{column}` (layout 7) when it is not there",
                $"IF OBJECT_ID({TrackingDdl.Literal(schema + "." + table)}, N'U') IS NOT NULL AND COL_LENGTH({TrackingDdl.Literal(schema + "." + table)}, {TrackingDdl.Literal(column)}) IS NULL\n  ALTER TABLE {s}.{Quote(table)} ADD {Quote(column)} {Native(c.Type)} NULL;"));
        }
        foreach (var (table, column) in TrackingSchema.AddedInLayout8)
        {
            var c = TrackingSchema.Table(table).Columns.First(x => x.Name == column);
            list.Add(new($"upgrade-{++n:00}", $"table {table}: add `{column}` (layout 8) when it is not there",
                $"IF OBJECT_ID({TrackingDdl.Literal(schema + "." + table)}, N'U') IS NOT NULL AND COL_LENGTH({TrackingDdl.Literal(schema + "." + table)}, {TrackingDdl.Literal(column)}) IS NULL\n  ALTER TABLE {s}.{Quote(table)} ADD {Quote(column)} {Native(c.Type)} NULL;"));
        }
        {
            var r = TrackingSchema.RenamedInLayout9;
            var md = TrackingDdl.Literal(schema + "." + r.Table);
            var oi = TrackingDdl.Literal(schema + ".object_info");
            list.Add(new($"upgrade-{++n:00}", $"table {r.Table}: rename `{r.OldColumn}` to `{r.Column}` (layout 9) when it is not done",
                $"IF OBJECT_ID({md}, N'U') IS NOT NULL AND COL_LENGTH({md}, {TrackingDdl.Literal(r.OldColumn)}) IS NOT NULL AND COL_LENGTH({md}, {TrackingDdl.Literal(r.Column)}) IS NULL\n  EXEC sp_rename {TrackingDdl.Literal(s + "." + Quote(r.Table) + "." + Quote(r.OldColumn))}, {TrackingDdl.Literal(r.Column)}, N'COLUMN';"));
            list.Add(new($"upgrade-{++n:00}", "tables metadata_document and object_info: a mapped or native model is subject kind `model`, not `source` (layout 9)",
                $"IF OBJECT_ID({md}, N'U') IS NOT NULL AND COL_LENGTH({md}, {TrackingDdl.Literal(r.Column)}) IS NOT NULL\n  EXEC({TrackingDdl.Literal($"UPDATE {s}.{Quote(r.Table)} SET {Quote(r.Column)} = 'model' WHERE {Quote(r.Column)} = 'source'")});\n" +
                $"IF OBJECT_ID({oi}, N'U') IS NOT NULL\nBEGIN\n  EXEC({TrackingDdl.Literal($"DELETE o FROM {s}.[object_info] o WHERE o.[subject_kind] = 'source' AND EXISTS (SELECT 1 FROM {s}.[object_info] m WHERE m.[connection] = o.[connection] AND m.[subject_kind] = 'model' AND m.[subject] = o.[subject])")});\n" +
                $"  EXEC({TrackingDdl.Literal($"UPDATE {s}.[object_info] SET [subject_kind] = 'model' WHERE [subject_kind] = 'source'")});\nEND;"));
            var dep = TrackingDdl.Literal(schema + "." + TrackingSchema.TableAddedInLayout8);
            var depTable = $"{s}.{Quote(TrackingSchema.TableAddedInLayout8)}";
            list.Add(new($"upgrade-{++n:00}", $"table {TrackingSchema.TableAddedInLayout8}: `depends_on_category` is new and `depends_on_kind` may be null; the rows are derived from the project, so they go and the next publish writes them again (layout 9)",
                $"IF OBJECT_ID({dep}, N'U') IS NOT NULL AND COL_LENGTH({dep}, {TrackingDdl.Literal(TrackingSchema.DependencyCategoryColumn)}) IS NULL\nBEGIN\n  DELETE FROM {depTable};\n" +
                $"  ALTER TABLE {depTable} ALTER COLUMN {Quote("depends_on_kind")} {Native(TrackingType.Short)} NULL;\n  ALTER TABLE {depTable} ADD {Quote(TrackingSchema.DependencyCategoryColumn)} {Native(TrackingType.Short)} NOT NULL;\nEND;"));
        }
        list.Add(new($"upgrade-{++n:00}", "drop the views of the older layout (the init script creates them again)",
            $"IF OBJECT_ID({TrackingDdl.Literal(schema + ".metadata_columns")}, N'V') IS NOT NULL DROP VIEW {s}.[metadata_columns];\nIF OBJECT_ID({TrackingDdl.Literal(schema + ".metadata_current")}, N'V') IS NOT NULL DROP VIEW {s}.[metadata_current];"));
        return list;
    }

    public IReadOnlyList<InitStatement> InitScript(string schema, string toolVersion)
    {
        var s = Quote(schema);
        var list = new List<InitStatement>
        {
            new("init-00", $"schema {schema}", $"IF SCHEMA_ID({TrackingDdl.Literal(schema)}) IS NULL EXEC({TrackingDdl.Literal("CREATE SCHEMA " + s)});"),
        };
        var n = 1;
        foreach (var t in TrackingSchema.Tables)
        {
            var cols = string.Join(",\n", t.Columns.Select(c => $"  {Quote(c.Name)} {Native(c.Type)} {(c.Nullable ? "NULL" : "NOT NULL")}"));
            var key = string.Join(", ", t.PrimaryKey.Select(Quote));
            var pk = target == "fabric" ? $"PRIMARY KEY NONCLUSTERED ({key}) NOT ENFORCED" : $"PRIMARY KEY ({key})";
            var obj = $"{s}.{Quote(t.Name)}";
            // a JSON column must hold JSON (Fabric does not have ISJSON constraints verified)
            var checks = target == "fabric" ? "" : string.Concat(t.Columns.Where(c => c.Type == TrackingType.Json).Select(c => $",\n  CONSTRAINT {Quote($"ck_{t.Name}_{c.Name}")} CHECK (ISJSON({Quote(c.Name)}) = 1)"));
            list.Add(new($"init-{n++:00}", $"table {t.Name}: {t.Purpose}",
                $"IF OBJECT_ID({TrackingDdl.Literal(schema + "." + t.Name)}, N'U') IS NULL\nCREATE TABLE {obj} (\n{cols},\n  CONSTRAINT {Quote("pk_" + t.Name)} {pk}{checks}\n);"));
        }
        foreach (var (id, text) in TrackingViews.TSql(s, schema, target == "fabric"))
            list.Add(new($"init-{n++:00}", id, text));
        var v = $"{s}.{Quote("tracking_version")}";
        // the version is recorded only when the newest column of the newest layout is there: a store of an older layout is not claimed by a script that never alters it (`--upgrade` does that)
        var last = TrackingSchema.AddedInLayout7[^1];
        var layout8 = TrackingSchema.AddedInLayout8[^1];
        var layout5 = TrackingSchema.AddedInLayout5[^1];
        list.Add(new($"init-{n}", "record the layout version",
            $"IF NOT EXISTS (SELECT 1 FROM {v} WHERE [version] = {TrackingSchema.Version}) AND COL_LENGTH({TrackingDdl.Literal(schema + "." + last.Table)}, {TrackingDdl.Literal(last.Column)}) IS NOT NULL AND COL_LENGTH({TrackingDdl.Literal(schema + "." + layout8.Table)}, {TrackingDdl.Literal(layout8.Column)}) IS NOT NULL AND COL_LENGTH({TrackingDdl.Literal(schema + "." + layout5.Table)}, {TrackingDdl.Literal(layout5.Column)}) IS NOT NULL AND OBJECT_ID({TrackingDdl.Literal(schema + "." + TrackingSchema.AddedInLayout6)}, N'U') IS NOT NULL AND OBJECT_ID({TrackingDdl.Literal(schema + "." + TrackingSchema.TableAddedInLayout8)}, N'U') IS NOT NULL AND COL_LENGTH({TrackingDdl.Literal(schema + "." + TrackingSchema.TableAddedInLayout8)}, {TrackingDdl.Literal(TrackingSchema.DependencyCategoryColumn)}) IS NOT NULL AND COL_LENGTH({TrackingDdl.Literal(schema + "." + TrackingSchema.RenamedInLayout9.Table)}, {TrackingDdl.Literal(TrackingSchema.RenamedInLayout9.Column)}) IS NOT NULL\nINSERT INTO {v} ([version], [tool_version], [applied_utc]) VALUES ({TrackingSchema.Version}, {TrackingDdl.Literal(toolVersion)}, SYSUTCDATETIME());"));
        return list;
    }
}

internal sealed class PostgresTrackingDdl : ITrackingDdl
{
    public string Target => "postgres";
    public bool Unverified => false;
    public string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    private static string Native(TrackingType t) => t switch
    {
        TrackingType.Name => "varchar(512)",
        TrackingType.Short => "varchar(128)",
        TrackingType.Hash => "char(64)",
        TrackingType.Long => "text",
        TrackingType.BigInt => "bigint",
        TrackingType.Int => "integer",
        TrackingType.Guid => "uuid",
        TrackingType.TimestampUtc => "timestamp(3)",
        TrackingType.Json => "jsonb",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public IReadOnlyList<InitStatement> UpgradeScript(string schema, string connection)
    {
        var s = Quote(schema);
        var list = new List<InitStatement>();
        var n = 0;
        foreach (var t in TrackingSchema.Tables.Where(t => t.Columns.Any(c => c.Name == "connection")))
        {
            var obj = $"{s}.{Quote(t.Name)}";
            var key = string.Join(", ", t.PrimaryKey.Select(Quote));
            var column = t.Columns.First(c => c.Name == "connection");
            list.Add(new($"upgrade-{++n:00}", $"table {t.Name}: add `connection`, set to {connection} on the rows it has, and put it in the primary key",
                $"DO $$\nBEGIN\n  IF to_regclass({TrackingDdl.Literal(schema + "." + t.Name)}) IS NOT NULL AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = {TrackingDdl.Literal(schema)} AND table_name = {TrackingDdl.Literal(t.Name)} AND column_name = 'connection') THEN\n" +
                $"    ALTER TABLE {obj} ADD COLUMN {Quote("connection")} {Native(column.Type)} NOT NULL DEFAULT {TrackingDdl.Literal(connection)};\n" +
                $"    ALTER TABLE {obj} ALTER COLUMN {Quote("connection")} DROP DEFAULT;\n" +
                $"    ALTER TABLE {obj} DROP CONSTRAINT {Quote("pk_" + t.Name)};\n" +
                $"    ALTER TABLE {obj} ADD CONSTRAINT {Quote("pk_" + t.Name)} PRIMARY KEY ({key});\n  END IF;\nEND $$;"));
        }
        foreach (var (table, column) in TrackingSchema.AddedInLayout5)
        {
            var c = TrackingSchema.Table(table).Columns.First(x => x.Name == column);
            list.Add(new($"upgrade-{++n:00}", $"table {table}: add `{column}` (layout 5) when it is not there",
                $"ALTER TABLE IF EXISTS {s}.{Quote(table)} ADD COLUMN IF NOT EXISTS {Quote(column)} {Native(c.Type)} NULL;"));
        }
        foreach (var (table, column) in TrackingSchema.AddedInLayout7)
        {
            var c = TrackingSchema.Table(table).Columns.First(x => x.Name == column);
            list.Add(new($"upgrade-{++n:00}", $"table {table}: add `{column}` (layout 7) when it is not there",
                $"ALTER TABLE IF EXISTS {s}.{Quote(table)} ADD COLUMN IF NOT EXISTS {Quote(column)} {Native(c.Type)} NULL;"));
        }
        foreach (var (table, column) in TrackingSchema.AddedInLayout8)
        {
            var c = TrackingSchema.Table(table).Columns.First(x => x.Name == column);
            list.Add(new($"upgrade-{++n:00}", $"table {table}: add `{column}` (layout 8) when it is not there",
                $"ALTER TABLE IF EXISTS {s}.{Quote(table)} ADD COLUMN IF NOT EXISTS {Quote(column)} {Native(c.Type)} NULL;"));
        }
        {
            var r = TrackingSchema.RenamedInLayout9;
            string Has(string table, string column) => $"EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = {TrackingDdl.Literal(schema)} AND table_name = {TrackingDdl.Literal(table)} AND column_name = {TrackingDdl.Literal(column)})";
            list.Add(new($"upgrade-{++n:00}", $"table {r.Table}: rename `{r.OldColumn}` to `{r.Column}` (layout 9) when it is not done",
                $"DO $$\nBEGIN\n  IF {Has(r.Table, r.OldColumn)} AND NOT {Has(r.Table, r.Column)} THEN\n    ALTER TABLE {s}.{Quote(r.Table)} RENAME COLUMN {Quote(r.OldColumn)} TO {Quote(r.Column)};\n  END IF;\nEND $$;"));
            list.Add(new($"upgrade-{++n:00}", "tables metadata_document and object_info: a mapped or native model is subject kind `model`, not `source` (layout 9)",
                $"DO $$\nBEGIN\n  IF {Has(r.Table, r.Column)} THEN\n    UPDATE {s}.{Quote(r.Table)} SET {Quote(r.Column)} = 'model' WHERE {Quote(r.Column)} = 'source';\n  END IF;\n" +
                $"  IF {Has("object_info", "subject_kind")} THEN\n    DELETE FROM {s}.\"object_info\" o WHERE o.\"subject_kind\" = 'source' AND EXISTS (SELECT 1 FROM {s}.\"object_info\" m WHERE m.\"connection\" = o.\"connection\" AND m.\"subject_kind\" = 'model' AND m.\"subject\" = o.\"subject\");\n" +
                $"    UPDATE {s}.\"object_info\" SET \"subject_kind\" = 'model' WHERE \"subject_kind\" = 'source';\n  END IF;\nEND $$;"));
            var depTable = $"{s}.{Quote(TrackingSchema.TableAddedInLayout8)}";
            list.Add(new($"upgrade-{++n:00}", $"table {TrackingSchema.TableAddedInLayout8}: `depends_on_category` is new and `depends_on_kind` may be null; the rows are derived from the project, so they go and the next publish writes them again (layout 9)",
                $"DO $$\nBEGIN\n  IF to_regclass({TrackingDdl.Literal(schema + "." + TrackingSchema.TableAddedInLayout8)}) IS NOT NULL AND NOT {Has(TrackingSchema.TableAddedInLayout8, TrackingSchema.DependencyCategoryColumn)} THEN\n" +
                $"    DELETE FROM {depTable};\n    ALTER TABLE {depTable} ALTER COLUMN {Quote("depends_on_kind")} DROP NOT NULL;\n    ALTER TABLE {depTable} ADD COLUMN {Quote(TrackingSchema.DependencyCategoryColumn)} {Native(TrackingType.Short)} NOT NULL;\n  END IF;\nEND $$;"));
        }
        list.Add(new($"upgrade-{++n:00}", "drop the views of the older layout (the init script creates them again)",
            $"DROP VIEW IF EXISTS {s}.\"metadata_columns\";\nDROP VIEW IF EXISTS {s}.\"metadata_current\";"));
        return list;
    }

    public IReadOnlyList<InitStatement> InitScript(string schema, string toolVersion)
    {
        var s = Quote(schema);
        var list = new List<InitStatement> { new("init-00", $"schema {schema}", $"CREATE SCHEMA IF NOT EXISTS {s};") };
        var n = 1;
        foreach (var t in TrackingSchema.Tables)
        {
            var cols = string.Join(",\n", t.Columns.Select(c => $"  {Quote(c.Name)} {Native(c.Type)} {(c.Nullable ? "NULL" : "NOT NULL")}"));
            var key = string.Join(", ", t.PrimaryKey.Select(Quote));
            list.Add(new($"init-{n++:00}", $"table {t.Name}: {t.Purpose}",
                $"CREATE TABLE IF NOT EXISTS {s}.{Quote(t.Name)} (\n{cols},\n  CONSTRAINT {Quote("pk_" + t.Name)} PRIMARY KEY ({key})\n);"));
        }
        foreach (var (id, text) in TrackingViews.Postgres(s, schema))
            list.Add(new($"init-{n++:00}", id, text));
        var v = $"{s}.{Quote("tracking_version")}";
        var last = TrackingSchema.AddedInLayout7[^1];
        var layout8 = TrackingSchema.AddedInLayout8[^1];
        var layout5 = TrackingSchema.AddedInLayout5[^1];
        list.Add(new($"init-{n}", "record the layout version",
            $"INSERT INTO {v} (\"version\", \"tool_version\", \"applied_utc\")\nSELECT {TrackingSchema.Version}, {TrackingDdl.Literal(toolVersion)}, (now() AT TIME ZONE 'utc')\n" +
            $"WHERE NOT EXISTS (SELECT 1 FROM {v} WHERE \"version\" = {TrackingSchema.Version}) AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = {TrackingDdl.Literal(schema)} AND table_name = {TrackingDdl.Literal(last.Table)} AND column_name = {TrackingDdl.Literal(last.Column)}) AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = {TrackingDdl.Literal(schema)} AND table_name = {TrackingDdl.Literal(layout5.Table)} AND column_name = {TrackingDdl.Literal(layout5.Column)}) AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = {TrackingDdl.Literal(schema)} AND table_name = {TrackingDdl.Literal(layout8.Table)} AND column_name = {TrackingDdl.Literal(layout8.Column)}) AND to_regclass({TrackingDdl.Literal(schema + "." + TrackingSchema.AddedInLayout6)}) IS NOT NULL AND to_regclass({TrackingDdl.Literal(schema + "." + TrackingSchema.TableAddedInLayout8)}) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = {TrackingDdl.Literal(schema)} AND table_name = {TrackingDdl.Literal(TrackingSchema.TableAddedInLayout8)} AND column_name = {TrackingDdl.Literal(TrackingSchema.DependencyCategoryColumn)}) AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = {TrackingDdl.Literal(schema)} AND table_name = {TrackingDdl.Literal(TrackingSchema.RenamedInLayout9.Table)} AND column_name = {TrackingDdl.Literal(TrackingSchema.RenamedInLayout9.Column)});"));
        return list;
    }
}

/// <summary>
/// The introspection views over `metadata_document`: the latest document per subject, and the columns of every model unpacked into rows (`model_kind` says which sort of model; a declared model has no lineage). They are made
/// only when the table has `subject_kind` (layout 9): a store of an older layout keeps the views it has until `init --upgrade` has renamed the column.
/// </summary>
internal static class TrackingViews
{
    public static IEnumerable<(string Description, string Text)> TSql(string schema, string schemaName, bool fabric)
    {
        string Guarded(string create) => $"IF COL_LENGTH({TrackingDdl.Literal(schemaName + ".metadata_document")}, 'subject_kind') IS NOT NULL\n  EXEC({TrackingDdl.Literal(create)});";
        yield return ("view metadata_current: the latest document per connection, subject kind and subject",
            Guarded($"CREATE OR ALTER VIEW {schema}.[metadata_current] AS SELECT m.[connection], m.[subject_kind], m.[subject], m.[document], m.[document_hash], m.[recorded_utc], m.[tool_version], m.[plan_id], m.[git_commit], m.[model_kind] " +
            $"FROM {schema}.[metadata_document] m WHERE m.[recorded_utc] = (SELECT MAX(x.[recorded_utc]) FROM {schema}.[metadata_document] x WHERE x.[connection] = m.[connection] AND x.[subject_kind] = m.[subject_kind] AND x.[subject] = m.[subject])"));
        if (fabric) yield break; // OPENJSON on Fabric is not verified
        yield return ("view metadata_columns: one row per column of a model of any kind, from the latest documents",
            Guarded($"CREATE OR ALTER VIEW {schema}.[metadata_columns] AS\nSELECT m.[connection], m.[subject] AS [model], c.[name] AS [column_name], c.[logical_type], c.[nullable], c.[collation],\n" +
            "  JSON_VALUE(c.[native], '$.sqlserver.type') AS [sqlserver_type], JSON_VALUE(c.[native], '$.postgres.type') AS [postgres_type], JSON_VALUE(c.[native], '$.fabric.type') AS [fabric_type],\n" +
            "  JSON_VALUE(c.[lineage], '$.inferred_nullability') AS [inferred_nullability], JSON_QUERY(c.[lineage], '$.upstream') AS [upstream], m.[model_kind]\n" +
            $"FROM {schema}.[metadata_current] m\nCROSS APPLY OPENJSON(m.[document], '$.columns') WITH (\n  [name] nvarchar(256) '$.name', [logical_type] nvarchar(128) '$.logical_type', [nullable] bit '$.nullable', [collation] nvarchar(128) '$.collation',\n" +
            "  [native] nvarchar(max) '$.native' AS JSON, [lineage] nvarchar(max) '$.lineage' AS JSON) c\nWHERE m.[subject_kind] = 'model'"));
    }

    public static IEnumerable<(string Description, string Text)> Postgres(string schema, string schemaName)
    {
        string Guarded(string create) =>
            $"DO $$\nBEGIN\n  IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = {TrackingDdl.Literal(schemaName)} AND table_name = 'metadata_document' AND column_name = 'subject_kind') THEN\n    EXECUTE {TrackingDdl.Literal(create)};\n  END IF;\nEND $$;";
        yield return ("view metadata_current: the latest document per connection, subject kind and subject",
            Guarded($"CREATE OR REPLACE VIEW {schema}.\"metadata_current\" AS SELECT m.\"connection\", m.\"subject_kind\", m.\"subject\", m.\"document\", m.\"document_hash\", m.\"recorded_utc\", m.\"tool_version\", m.\"plan_id\", m.\"git_commit\", m.\"model_kind\" " +
            $"FROM {schema}.\"metadata_document\" m WHERE m.\"recorded_utc\" = (SELECT MAX(x.\"recorded_utc\") FROM {schema}.\"metadata_document\" x WHERE x.\"connection\" = m.\"connection\" AND x.\"subject_kind\" = m.\"subject_kind\" AND x.\"subject\" = m.\"subject\")"));
        yield return ("view metadata_columns: one row per column of a model of any kind, from the latest documents",
            Guarded($"CREATE OR REPLACE VIEW {schema}.\"metadata_columns\" AS\nSELECT m.\"connection\", m.\"subject\" AS \"model\", c ->> 'name' AS \"column_name\", c ->> 'logical_type' AS \"logical_type\", (c ->> 'nullable')::boolean AS \"nullable\", c ->> 'collation' AS \"collation\",\n" +
            "  c -> 'native' -> 'sqlserver' ->> 'type' AS \"sqlserver_type\", c -> 'native' -> 'postgres' ->> 'type' AS \"postgres_type\", c -> 'native' -> 'fabric' ->> 'type' AS \"fabric_type\",\n" +
            "  c -> 'lineage' ->> 'inferred_nullability' AS \"inferred_nullability\", c -> 'lineage' -> 'upstream' AS \"upstream\", m.\"model_kind\"\n" +
            $"FROM {schema}.\"metadata_current\" m\nCROSS JOIN LATERAL jsonb_array_elements(m.\"document\" -> 'columns') AS c\nWHERE m.\"subject_kind\" = 'model'"));
    }
}
