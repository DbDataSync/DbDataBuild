using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>Writes the metadata documents of `metadata` into the target's `metadata_document` table, only the ones that changed. Used by `publish-metadata` and, when configured, after `apply`.</summary>
internal static class MetadataPublisher
{
    public sealed record Document(string SubjectKind, string Subject, string Json, string Hash);
    public sealed record Result(IReadOnlyList<Document> Written, IReadOnlyList<Document> Unchanged, int DependencySubjectsWritten = 0, int DependencyRows = 0);

    /// <summary>The dependency rows of the project (`metadata_dependency`) and whether this publish is about every subject (then rows of subjects the project no longer has go too).</summary>
    public sealed record Dependencies(IReadOnlyList<DependencyRow> Rows, IReadOnlySet<string> Subjects, bool All);

    /// <summary>What a publish writes: the documents and the dependency rows.</summary>
    public sealed record Prepared(List<Document> Documents, Dependencies Dependencies);

    public static Document Make(string subjectKind, string subject, object document)
    {
        var json = MetadataBuilder.ToStoredJson(document);
        return new Document(subjectKind, subject, json, Hashing.ScriptHash(json));
    }

    /// <summary>
    /// The project document, the documents of the named models of any kind (all when null) and of the declared models those read directly, and the plan's when given; with the dependency rows: the whole project's, of which the
    /// models published (and the native models they depend on) are the ones this publish writes, every row when all the models are published. The rows are computed once and serve both.
    /// </summary>
    public static Prepared Prepare(ProjectContext ctx, IEnumerable<string>? models, Plan? plan)
    {
        var rows = MetadataBuilder.Dependencies(ctx);
        var docs = new List<Document> { Make("project", "project", MetadataBuilder.Project(ctx)) };
        if (models == null)
        {
            docs.AddRange(MetadataBuilder.Models(ctx).Select(m => Make("model", m.Name, m.Document)));
            if (plan != null) docs.Add(Make("plan", plan.Id, plan));
            return new Prepared(docs, new Dependencies(rows, new HashSet<string>(StringComparer.Ordinal), All: true));
        }
        var subjects = models.ToHashSet(StringComparer.Ordinal);
        var declared = ctx.Project.DeclaredModels.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var natives = ctx.Project.NativeModels.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = subjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => r.Depth == 1 && subjects.Contains(r.Subject) && declared.Contains(r.DependsOn))) named.Add(row.DependsOn);      // the declared models a published model reads travel with it
        foreach (var row in rows.Where(r => subjects.Contains(r.Subject) && natives.Contains(r.DependsOn)).ToList()) subjects.Add(row.DependsOn);
        docs.AddRange(MetadataBuilder.Models(ctx, named).Select(m => Make("model", m.Name, m.Document)));
        if (plan != null) docs.Add(Make("plan", plan.Id, plan));
        return new Prepared(docs, new Dependencies(rows, subjects, All: false));
    }

    /// <summary>The documents alone (for the rules of the project's tests, which run over what a publish would store).</summary>
    public static List<Document> Collect(ProjectContext ctx, IEnumerable<string>? models, Plan? plan) => Prepare(ctx, models, plan).Documents;

    /// <param name="scope">Where the records go: the tracking store of the connection the documents describe. <paramref name="read"/> and <paramref name="write"/> are the logins of that tracking connection.</param>
    public static async Task<Result> PublishAsync(IReadOnlyList<Document> documents, TrackingScope scope, LoginSettings read, LoginSettings write, string command, string root, string? planId, string? gitCommit, Dependencies? dependencies = null)
    {
        await using var reader = await ReadSession.OpenAsync(read);
        var status = await TrackingStore.StatusAsync(reader, scope.Engine, scope.SchemaName);
        if (status.AsDiagnostic(scope.SchemaName) is { } notReady) throw new GateRefusedException(notReady);
        var latest = await MetadataStore.LatestHashesAsync(reader, scope);

        var written = new List<Document>();
        var unchanged = new List<Document>();
        var changed = documents.Where(d => !latest.TryGetValue(MetadataStore.Key(d.SubjectKind, d.Subject), out var h) || h != d.Hash).ToList();
        unchanged.AddRange(documents.Except(changed));

        // the dependency rows are found by comparing with the stored ones, not by what changed above: a publish that stopped between the two is finished by the next
        IReadOnlyList<string> dependencySubjects = [];
        if (dependencies != null)
        {
            var stored = await MetadataStore.DependenciesAsync(reader, scope);
            dependencySubjects = MetadataStore.DependencySubjectsToWrite(dependencies.Rows, stored, dependencies.Subjects, dependencies.All);
        }
        if (changed.Count == 0 && dependencySubjects.Count == 0) return new Result(written, unchanged, 0, dependencies?.Rows.Count ?? 0);

        var runId = Guid.NewGuid();
        using var log = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), command, runId);
        await using var gate = await MutationGate.OpenAsync(write, command, StatementKind.Tracking, log, runId);
        var n = 0;
        foreach (var d in changed)
        {
            var step = $"metadata-{++n:000}";
            // the rows first, the document after: an interrupted publish leaves a document that still differs from the latest, so the next one writes both again
            if (MetadataInfoRows.HasRows(d.SubjectKind)) await MetadataStore.ReplaceInfoAsync(gate, scope, step, d.SubjectKind, d.Subject, MetadataInfoRows.From(d.SubjectKind, d.Json));
            await MetadataStore.StoreAsync(gate, scope, step, d.SubjectKind, d.Subject, d.Json, d.Hash, ProductInfo.Version, planId, gitCommit);
            written.Add(d);
        }
        if (dependencySubjects.Count > 0) await MetadataStore.ReplaceDependenciesAsync(gate, scope, "metadata-dependencies", dependencySubjects, dependencies!.Rows);
        return new Result(written, unchanged, dependencySubjects.Count, dependencies?.Rows.Count ?? 0);
    }
}
