using System.Data;
using DbDataBuild.Core;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>
/// Metadata documents in the target (`metadata_document`, DESIGN.md 12): JSON text with its hash, append-only. A document is written only when it differs from the latest one
/// for the same kind and subject, so republishing an unchanged project writes nothing.
/// </summary>
/// <summary>One row of `object_info`: <paramref name="Element"/> is empty for the subject itself, or `column:name`, `index:name` or `connection:name` for a part of it; <paramref name="Item"/> is `title`, `description`, `tag` or `property`; <paramref name="Key"/> is the tag or the property key (empty for a title and a description); a property has a type and its value as text.</summary>
public sealed record InfoRow(string Element, string Item, string Key, string? ValueType, string? Value);

public static class MetadataStore
{
    private static string C(string target, string n) => TrackingDdl.For(target).Quote(n);

    /// <summary>The hash of the newest document per `subject_kind|subject`.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LatestHashesAsync(ReadSession read, TrackingScope scope, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync($"SELECT m.{c("subject_kind")}, m.{c("subject")}, m.{c("document_hash")} FROM {c(scope.SchemaName)}.{c("metadata_current")} m WHERE m.{c("connection")} = @connection",
            [new GateParameter("connection", DbType.String, scope.Connection)], ct);
        return rows.ToDictionary(r => $"{((string)r[0]!).Trim()}|{r[1]}", r => ((string)r[2]!).Trim(), StringComparer.Ordinal);
    }

    /// <summary>The text of the newest document of a subject kind and subject, or null when there is none.</summary>
    public static async Task<string?> LatestDocumentAsync(ReadSession read, TrackingScope scope, string kind, string subject, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync($"SELECT m.{c("document")} FROM {c(scope.SchemaName)}.{c("metadata_current")} m WHERE m.{c("connection")} = @connection AND m.{c("subject_kind")} = @subject_kind AND m.{c("subject")} = @subject",
            [new GateParameter("connection", DbType.String, scope.Connection), new GateParameter("subject_kind", DbType.String, kind), new GateParameter("subject", DbType.String, subject)], ct);
        return rows.Count == 0 ? null : rows[0][0]?.ToString();
    }

    /// <summary>
    /// Replaces the rows of `object_info` for one subject (a project or a model of any kind) with <paramref name="rows"/>: the title, description, tags and properties the subject's document says, one row each. The old rows go first and
    /// the document is stored after, so an interrupted publish leaves a document that still differs from the latest one and the next publish writes both again.
    /// </summary>
    public static async Task ReplaceInfoAsync(MutationGate gate, TrackingScope scope, string stepId, string kind, string subject, IReadOnlyList<InfoRow> rows, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var table = $"{c(scope.SchemaName)}.{c("object_info")}";
        await gate.ExecuteAsync(GateStatement.Tracking($"{stepId}-info-delete", $"DELETE FROM {table} WHERE {c("connection")} = @connection AND {c("subject_kind")} = @subject_kind AND {c("subject")} = @subject",
            [new("connection", DbType.String, scope.Connection), new("subject_kind", DbType.AnsiString, kind), new("subject", DbType.String, subject)]), ct);
        var recorded = TrackingClock.NextUtc();
        const int PerStatement = 50;       // 5 values a row beside the 4 shared ones: well under the 2100 parameters SQL Server takes in one statement
        for (var from = 0; from < rows.Count; from += PerStatement)
        {
            var chunk = rows.Skip(from).Take(PerStatement).ToList();
            var parameters = new List<GateParameter> { new("connection", DbType.String, scope.Connection), new("subject_kind", DbType.AnsiString, kind), new("subject", DbType.String, subject), new("recorded_utc", DbType.DateTime2, recorded) };
            var values = new List<string>();
            for (var i = 0; i < chunk.Count; i++)
            {
                values.Add($"(@connection, @subject_kind, @subject, @element{i}, @item{i}, @item_key{i}, @value_type{i}, @value{i}, @recorded_utc)");
                parameters.Add(new($"element{i}", DbType.String, chunk[i].Element)); parameters.Add(new($"item{i}", DbType.AnsiString, chunk[i].Item)); parameters.Add(new($"item_key{i}", DbType.AnsiString, chunk[i].Key));
                parameters.Add(new($"value_type{i}", DbType.AnsiString, chunk[i].ValueType)); parameters.Add(new($"value{i}", DbType.String, chunk[i].Value));
            }
            var text = $"INSERT INTO {table} ({c("connection")}, {c("subject_kind")}, {c("subject")}, {c("element")}, {c("item")}, {c("item_key")}, {c("value_type")}, {c("value")}, {c("recorded_utc")}) VALUES {string.Join(", ", values)}";
            await gate.ExecuteAsync(GateStatement.Tracking($"{stepId}-info-{from / PerStatement + 1:00}", text, parameters), ct);
        }
    }

    /// <summary>The rows of `object_info` of one subject, for tests and for `connection status`: element, item, key, type, value.</summary>
    public static async Task<IReadOnlyList<InfoRow>> InfoAsync(ReadSession read, TrackingScope scope, string kind, string subject, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync($"SELECT i.{c("element")}, i.{c("item")}, i.{c("item_key")}, i.{c("value_type")}, i.{c("value")} FROM {c(scope.SchemaName)}.{c("object_info")} i WHERE i.{c("connection")} = @connection AND i.{c("subject_kind")} = @kind AND i.{c("subject")} = @subject",
            [new GateParameter("connection", DbType.String, scope.Connection), new GateParameter("kind", DbType.String, kind), new GateParameter("subject", DbType.String, subject)], ct);
        return rows.Select(r => new InfoRow(((string)r[0]!), ((string)r[1]!).Trim(), ((string)r[2]!).Trim(), (r[3] as string)?.Trim(), r[4] as string)).ToList();
    }

    public static string Key(string subjectKind, string subject) => $"{subjectKind}|{subject}";

    /// <summary>The rows of `metadata_dependency` for the connection: what the models (and native models) depend on, as last published.</summary>
    public static async Task<IReadOnlyList<DependencyRow>> DependenciesAsync(ReadSession read, TrackingScope scope, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync($"SELECT d.{c("subject")}, d.{c("depends_on")}, d.{c("depends_on_category")}, d.{c("depends_on_kind")}, d.{c("depth")} FROM {c(scope.SchemaName)}.{c("metadata_dependency")} d WHERE d.{c("connection")} = @connection",
            [new GateParameter("connection", DbType.String, scope.Connection)], ct);
        return rows.Select(r => new DependencyRow((string)r[0]!, (string)r[1]!, ((string)r[2]!).Trim(), (r[3] as string)?.Trim(), Convert.ToInt32(r[4]))).ToList();
    }

    /// <summary>
    /// What a sync of the dependency rows would change: the subjects whose rows differ from <paramref name="stored"/> (or that are stored and no longer computed, when <paramref name="all"/>). <paramref name="computed"/> is the
    /// project's rows; <paramref name="subjects"/> are the subjects this publish is about (every subject when <paramref name="all"/>).
    /// </summary>
    public static IReadOnlyList<string> DependencySubjectsToWrite(IReadOnlyList<DependencyRow> computed, IReadOnlyList<DependencyRow> stored, IReadOnlySet<string> subjects, bool all)
    {
        static string Shape(IEnumerable<DependencyRow> rows) => string.Join("\n", rows.OrderBy(r => r.DependsOn, StringComparer.Ordinal).Select(r => $"{r.DependsOn}|{r.DependsOnCategory}|{r.DependsOnKind}|{r.Depth}"));
        var want = computed.GroupBy(r => r.Subject, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Shape(g), StringComparer.Ordinal);
        var have = stored.GroupBy(r => r.Subject, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Shape(g), StringComparer.Ordinal);
        var names = want.Keys.Where(k => all || subjects.Contains(k)).Concat(all ? have.Keys : have.Keys.Where(subjects.Contains)).Distinct(StringComparer.Ordinal);
        return names.Where(n => want.GetValueOrDefault(n) != have.GetValueOrDefault(n)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Replaces the dependency rows of some subjects: their old rows go, the computed ones are written (a subject that is no longer in <paramref name="computed"/> just loses its rows). Not part of the document write, and found by
    /// comparing with what is stored, so a publish that stopped half way is finished by the next one.
    /// </summary>
    public static async Task ReplaceDependenciesAsync(MutationGate gate, TrackingScope scope, string stepId, IReadOnlyList<string> subjects, IReadOnlyList<DependencyRow> computed, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var table = $"{c(scope.SchemaName)}.{c("metadata_dependency")}";
        var n = 0;
        foreach (var subject in subjects)
            await gate.ExecuteAsync(GateStatement.Tracking($"{stepId}-dep-delete-{++n:000}", $"DELETE FROM {table} WHERE {c("connection")} = @connection AND {c("subject")} = @subject",
                [new("connection", DbType.String, scope.Connection), new("subject", DbType.String, subject)]), ct);
        var set = subjects.ToHashSet(StringComparer.Ordinal);
        var rows = computed.Where(r => set.Contains(r.Subject)).ToList();
        const int PerStatement = 100;      // 5 values a row and the connection: far below the 2100 parameters SQL Server takes in one statement
        for (var start = 0; start < rows.Count; start += PerStatement)
        {
            var chunk = rows.Skip(start).Take(PerStatement).ToList();
            var parameters = new List<GateParameter> { new("connection", DbType.String, scope.Connection) };
            var values = new List<string>();
            for (var i = 0; i < chunk.Count; i++)
            {
                values.Add($"(@connection, @subject{i}, @depends_on{i}, @category{i}, @kind{i}, @depth{i})");
                parameters.Add(new($"subject{i}", DbType.String, chunk[i].Subject)); parameters.Add(new($"depends_on{i}", DbType.String, chunk[i].DependsOn));
                parameters.Add(new($"category{i}", DbType.AnsiString, chunk[i].DependsOnCategory)); parameters.Add(new($"kind{i}", DbType.AnsiString, chunk[i].DependsOnKind));
                parameters.Add(new($"depth{i}", DbType.Int32, chunk[i].Depth));
            }
            await gate.ExecuteAsync(GateStatement.Tracking($"{stepId}-dep-{start / PerStatement + 1:000}",
                $"INSERT INTO {table} ({c("connection")}, {c("subject")}, {c("depends_on")}, {c("depends_on_category")}, {c("depends_on_kind")}, {c("depth")}) VALUES {string.Join(", ", values)}", parameters), ct);
        }
    }

    /// <summary>Stores one document. The JSON is bound as a parameter (cast to jsonb on PostgreSQL), never interpolated.</summary>
    public static Task StoreAsync(MutationGate gate, TrackingScope scope, string stepId, string subjectKind, string subject, string json, string hash, string toolVersion, string? planId, string? gitCommit, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var document = scope.Engine == "postgres" ? "CAST(@document AS jsonb)" : "@document";
        var text = $"INSERT INTO {c(scope.SchemaName)}.{c("metadata_document")} ({c("connection")}, {c("subject_kind")}, {c("subject")}, {c("document")}, {c("document_hash")}, {c("recorded_utc")}, {c("tool_version")}, {c("plan_id")}, {c("git_commit")}, {c("model_kind")}) " +
                   $"VALUES (@connection, @subject_kind, @subject, {document}, @document_hash, @recorded_utc, @tool_version, @plan_id, @git_commit, @model_kind)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            new("connection", DbType.String, scope.Connection), new("subject_kind", DbType.AnsiString, subjectKind), new("subject", DbType.String, subject), new("document", DbType.String, json), new("document_hash", DbType.AnsiStringFixedLength, hash),
            new("recorded_utc", DbType.DateTime2, TrackingClock.NextUtc()), new("tool_version", DbType.AnsiString, toolVersion), new("plan_id", DbType.AnsiString, planId), new("git_commit", DbType.AnsiString, gitCommit),
            new("model_kind", DbType.AnsiString, MetadataDependencies.ModelKindOf(subjectKind, json)),
        ]), ct);
    }
}
