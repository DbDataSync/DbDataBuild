using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>Writes the metadata documents of `metadata` into the target's `metadata_document` table, only the ones that changed. Used by `publish-metadata` and, when configured, after `apply`.</summary>
internal static class MetadataPublisher
{
    public sealed record Document(string Kind, string Subject, string Json, string Hash);
    public sealed record Result(IReadOnlyList<Document> Written, IReadOnlyList<Document> Unchanged, int DependencySubjectsWritten = 0, int DependencyRows = 0);

    /// <summary>The dependency rows of the project (`metadata_dependency`) and whether this publish is about every subject (then rows of subjects the project no longer has go too).</summary>
    public sealed record Dependencies(IReadOnlyList<DependencyRow> Rows, IReadOnlySet<string> Subjects, bool All);

    /// <summary>
    /// The dependency rows for a publish of the same models as <see cref="Collect"/>: the whole project's rows, of which the models published (and the native models they depend on) are the ones this publish writes; every row when all
    /// the models are published.
    /// </summary>
    public static Dependencies CollectDependencies(ProjectContext ctx, IEnumerable<string>? models)
    {
        var rows = MetadataBuilder.Dependencies(ctx);
        if (models == null) return new Dependencies(rows, new HashSet<string>(StringComparer.Ordinal), All: true);
        var subjects = models.ToHashSet(StringComparer.Ordinal);
        var natives = ctx.Project.NativeModels.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => subjects.Contains(r.Subject) && natives.Contains(r.DependsOn)).ToList()) subjects.Add(row.DependsOn);
        return new Dependencies(rows, subjects, All: false);
    }

    public static Document Make(string kind, string subject, object document)
    {
        var json = MetadataBuilder.ToStoredJson(document);
        return new Document(kind, subject, json, Hashing.ScriptHash(json));
    }

    /// <summary>The project document, the documents of the named models (all when null), and the documents of the sources those models read (every source when null).</summary>
    public static List<Document> Collect(ProjectContext ctx, IEnumerable<string>? models, Plan? plan)
    {
        var docs = new List<Document> { Make("project", "project", MetadataBuilder.Project(ctx)) };
        var wanted = models?.ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sourceConsumers = null;
        foreach (var s in ctx.Project.Sources.Where(s => wanted == null || wanted.Contains(s.Definition.Name)).OrderBy(s => s.Definition.Name, StringComparer.Ordinal))
            docs.Add(Make("model", s.Definition.Name, MetadataBuilder.Model(ctx, s, s.ReadQuery(ctx.Root, ctx.Config))));
        foreach (var d in ctx.Project.FileDescriptors.OrderBy(d => d.Name, StringComparer.Ordinal))      // mapped and native models alike (a native model had no stored document until entry 165)
        {
            // computed once for the whole project, so a source's document is the same whichever models are published
            sourceConsumers ??= MetadataBuilder.Consumers(ctx);
            if (wanted == null || sourceConsumers[d.Name].Any(wanted.Contains)) docs.Add(Make("source", d.Name, MetadataBuilder.Source(d, sourceConsumers[d.Name])));
        }
        if (plan != null) docs.Add(Make("plan", plan.Id, plan));
        return docs;
    }

    /// <param name="scope">Where the records go: the tracking store of the connection the documents describe. <paramref name="read"/> and <paramref name="write"/> are the logins of that tracking connection.</param>
    public static async Task<Result> PublishAsync(IReadOnlyList<Document> documents, TrackingScope scope, LoginSettings read, LoginSettings write, string command, string root, string? planId, string? gitCommit, Dependencies? dependencies = null)
    {
        await using var reader = await ReadSession.OpenAsync(read);
        var status = await TrackingStore.StatusAsync(reader, scope.Engine, scope.SchemaName);
        if (status.AsDiagnostic(scope.SchemaName) is { } notReady) throw new GateRefusedException(notReady);
        var latest = await MetadataStore.LatestHashesAsync(reader, scope);

        var written = new List<Document>();
        var unchanged = new List<Document>();
        var changed = documents.Where(d => !latest.TryGetValue(MetadataStore.Key(d.Kind, d.Subject), out var h) || h != d.Hash).ToList();
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
            if (MetadataInfoRows.HasRows(d.Kind)) await MetadataStore.ReplaceInfoAsync(gate, scope, step, d.Kind, d.Subject, MetadataInfoRows.From(d.Kind, d.Json));
            await MetadataStore.StoreAsync(gate, scope, step, d.Kind, d.Subject, d.Json, d.Hash, ProductInfo.Version, planId, gitCommit);
            written.Add(d);
        }
        if (dependencySubjects.Count > 0) await MetadataStore.ReplaceDependenciesAsync(gate, scope, "metadata-dependencies", dependencySubjects, dependencies!.Rows);
        return new Result(written, unchanged, dependencySubjects.Count, dependencies?.Rows.Count ?? 0);
    }
}
