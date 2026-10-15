using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `track_definition` (DESIGN.md 6.5.4): the routines a native model's text depends on are hashed when a plan is applied and the hash is kept in the tracking tables (a `native_definition` document per native
/// model and connection); the next plan reads the live definitions and compares. A change is a warning (DDB-234; `policy.severity.native_definition_changed` can make it an error). Nothing is recorded or compared
/// without tracking, and a routine the engine returns no definition for is reported as not checked (DDB-235), never as a change.
/// </summary>
internal static class NativeDefinitions
{
    public const string DocumentKind = "native_definition";

    /// <summary>A native model used on a connection: read by a model built there (inlined, or landed by a local copy) or the origin of a copy.</summary>
    public sealed record Use(DeclaredModel Native, string Connection)
    {
        public string Subject => $"{Native.Name}@{Connection}";
    }

    /// <summary>The native models with something to watch that the given models use on <paramref name="target"/>.</summary>
    public static IReadOnlyList<Use> InPlay(ProjectContext ctx, IEnumerable<(BuiltModel Source, string Sql)> models, string target)
    {
        var uses = new Dictionary<string, Use>(StringComparer.Ordinal);
        void Add(string? name, string connection)
        {
            if (name == null || ctx.Project.NativeModels.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) is not { Native.TrackDefinition.Count: > 0 } native) return;
            var use = new Use(native, connection);
            uses[use.Subject] = use;
        }
        foreach (var (source, sql) in models)
        {
            foreach (var table in ctx.BaseTablesOf(source, sql)) Add(table, target);
            foreach (var origin in ctx.OriginsOf(source.Definition, target)) Add(origin.Table, origin.Connection);
        }
        return uses.Values.OrderBy(u => u.Subject, StringComparer.Ordinal).ToList();
    }

    /// <summary>The hash of each tracked routine's live definition on the use's connection; null for one the engine returned nothing for.</summary>
    public static async Task<IReadOnlyDictionary<string, string?>> CurrentAsync(ProjectConfig config, Use use, LoginSettings login)
    {
        await using var read = await ReadSession.OpenAsync(login);
        var engine = config.EngineOf(use.Connection) ?? use.Connection;
        var result = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var routine in use.Native.Native!.TrackDefinition)
            result[routine] = await read.RoutineDefinitionAsync(engine, routine) is { } text ? Hashing.ScriptHash(text) : null;
        return result;
    }

    private static IReadOnlyDictionary<string, string> Parse(string? document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document == null) return result;
        try
        {
            if (JsonNode.Parse(document)?["routines"] is JsonObject routines)
                foreach (var (name, hash) in routines) if ((string?)hash is { } h) result[name] = h;
        }
        catch (JsonException) { }
        return result;
    }

    private static string Document(IReadOnlyDictionary<string, string> hashes) =>
        JsonSerializer.Serialize(new { routines = hashes.OrderBy(h => h.Key, StringComparer.Ordinal).ToDictionary(h => h.Key, h => h.Value) });

    /// <summary>The findings of comparing the live definitions with the last record. <paramref name="scope"/> null: nothing is tracked, so the routines are not checked.</summary>
    public static IReadOnlyList<Diagnostic> Check(ProjectContext ctx, IReadOnlyList<Use> uses, TrackingScope? scope, ReadSession? trackRead, Func<string, string?> env)
    {
        var findings = new List<Diagnostic>();
        foreach (var use in uses)
        {
            var at = new SourceLocation(use.Native.Native?.File ?? use.Native.Name, use.Native.Native?.Line ?? 0, 0);
            Diagnostic NotChecked(string why) => new(DiagnosticCatalog.NativeDefinitionNotChecked, at, $"{use.Native.Name} on `{use.Connection}`: the definitions of {string.Join(", ", use.Native.Native!.TrackDefinition.Select(r => $"`{r}`"))} were not checked: {why}");
            if (scope == null || trackRead == null) { findings.Add(NotChecked("nothing is tracked for this connection, so there is no record to compare with.")); continue; }
            var (login, missing) = LoginSettings.FromEnvironment(use.Connection, ctx.Config.EngineOf(use.Connection) ?? use.Connection, Login.Read, env);
            if (login == null) { findings.Add(NotChecked(missing?.Found ?? "no login.")); continue; }
            var current = Task.Run(() => CurrentAsync(ctx.Config, use, login)).GetAwaiter().GetResult();
            var recorded = Parse(Task.Run(() => MetadataStore.LatestDocumentAsync(trackRead, scope, DocumentKind, use.Subject)).GetAwaiter().GetResult());
            foreach (var (routine, hash) in current)
            {
                if (hash == null) { findings.Add(NotChecked($"the engine returned no definition for `{routine}` (no such routine, several of that name, or no permission to see it).")); continue; }
                if (recorded.TryGetValue(routine, out var before) && before != hash)
                    findings.Add(new Diagnostic(DiagnosticCatalog.NativeDefinitionChanged, at, $"{use.Native.Name} on `{use.Connection}`: the definition of `{routine}` changed since the last apply (recorded {before[..12]}, now {hash[..12]}).",
                        Fix: null) with { SeverityOverride = ctx.Config.Policy.GetValueOrDefault(PolicyKeys.NativeDefinitionChanged, Severity.Warning) });
            }
        }
        return findings;
    }

    /// <summary>After a successful apply: records the live definitions (the ones the load just used) when they differ from the last record.</summary>
    public static async Task RecordAsync(ProjectContext ctx, IReadOnlyList<Use> uses, TrackingScope scope, LoginSettings trackRead, LoginSettings trackWrite, Func<string, string?> env, string command, string root, string? planId, string? gitCommit)
    {
        if (uses.Count == 0) return;
        await using var reader = await ReadSession.OpenAsync(trackRead);
        var pending = new List<(Use Use, string Json, string Hash)>();
        foreach (var use in uses)
        {
            var (login, _) = LoginSettings.FromEnvironment(use.Connection, ctx.Config.EngineOf(use.Connection) ?? use.Connection, Login.Read, env);
            if (login == null) continue;
            var current = (await CurrentAsync(ctx.Config, use, login)).Where(c => c.Value != null).ToDictionary(c => c.Key, c => c.Value!, StringComparer.Ordinal);
            var recorded = Parse(await MetadataStore.LatestDocumentAsync(reader, scope, DocumentKind, use.Subject));
            // a routine that could not be read keeps its earlier hash, so a later plan still compares against what was last known
            foreach (var (name, hash) in recorded) if (!current.ContainsKey(name) && use.Native.Native!.TrackDefinition.Contains(name)) current[name] = hash;
            if (current.Count == 0 || current.OrderBy(c => c.Key, StringComparer.Ordinal).SequenceEqual(recorded.OrderBy(c => c.Key, StringComparer.Ordinal))) continue;
            var json = Document(current);
            pending.Add((use, json, Hashing.ScriptHash(json)));
        }
        if (pending.Count == 0) return;
        var runId = Guid.NewGuid();
        using var log = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), command, runId);
        await using var gate = await MutationGate.OpenAsync(trackWrite, command, StatementKind.Tracking, log, runId);
        var n = 0;
        foreach (var (use, json, hash) in pending)
            await MetadataStore.StoreAsync(gate, scope, $"definitions-{++n:000}", DocumentKind, use.Subject, json, hash, ProductInfo.Version, planId, gitCommit);
    }
}
