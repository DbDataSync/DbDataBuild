using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// The one place that says whether the commands that update a project's metadata read the connections when nobody said (`project update` and `project model update`). Follow-up V-F1 may change it (to a flag, or a
/// setting per project or connection): everything that decides goes through <see cref="Online"/>.
/// </summary>
internal static class UpdateDefaults
{
    /// <summary>True: the update commands read the connections, and `--offline` is how a person says not to.</summary>
    public const bool OnlineByDefault = true;

    public static bool Online(bool offlineFlag) => OnlineByDefault && !offlineFlag;
}

/// <summary>A mapped model compared with its table on one connection.</summary>
internal sealed record CaptureRow(string Connection, ImportCommand.Row Row);

/// <summary>A connection the capture used or skipped, and why.</summary>
internal sealed record CaptureConnection(string Connection, string Driver, string Status, string? Reason);

internal sealed record CaptureOutcome(List<CaptureRow> MappedModels, List<CaptureConnection> Connections, List<string> Notes, List<Diagnostic> Diagnostics, List<(string Name, string Reason)> Skipped);

/// <summary>
/// The online part of `project update` and `project model update`, in one piece of code that both call: the metadata of every mapped model of the project (the columns, types, primary key as grain, foreign keys and indexes
/// `project import` writes), read from the connection it is on, including the mapped models that copies read from. It reuses <see cref="ImportCommand.GatherAsync"/>; a mapped model is never ignored: if it cannot be
/// read, the outcome says that and why. A connection whose read login is missing or that cannot be reached is skipped with a plain note, and the rest still updates. A native model has no catalog entry and is said to be skipped.
/// </summary>
internal static class ConnectionCapture
{
    public static async Task<CaptureOutcome> GatherAsync(ProjectContext ctx, UpdateSelection selection, Func<string, string?> env, SessionOpener open)
    {
        var outcome = new CaptureOutcome([], [], [], [], []);
        // a copy that is selected brings the table it reads with it
        var copyOrigins = ctx.Project.BuiltModels.Where(s => s.Definition.IsCopy && selection.Includes(s.Definition.Name)).Select(s => s.Definition.From).Where(f => f != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = ctx.Project.DeclaredModels.Where(d => selection.Includes(d.Name) || copyOrigins.Contains(d.Name)).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        foreach (var native in wanted.Where(d => d.IsNative))
            outcome.Skipped.Add((native.Name, "a native model is computed by a query of the engine, so it has no table in a catalog; its declared columns are checked when it is used"));
        var mapped = wanted.Where(d => !d.IsNative).ToList();
        if (mapped.Count == 0) return outcome;

        // each connection is looked at once: its login, then whether it answers
        var logins = new Dictionary<string, (LoginSettings? Login, string? Problem)>(StringComparer.Ordinal);
        (LoginSettings? Login, string? Problem) LoginOf(ConnectionConfig c)
        {
            if (logins.TryGetValue(c.Name, out var known)) return known;
            var (login, missing) = LoginSettings.FromEnvironment(c, Login.Read, env);
            return logins[c.Name] = login != null ? (login, null) : (null, $"its read login is not set ({LoginSettings.VariableName(c.Name, Login.Read)})");
        }

        var byConnection = new Dictionary<string, List<DeclaredModel>>(StringComparer.Ordinal);
        foreach (var d in mapped)
        {
            var candidates = (d.Connections ?? ctx.Config.DefaultConnections).Where(ctx.Config.Connections.ContainsKey).ToList();
            var chosen = candidates.FirstOrDefault(c => LoginOf(ctx.Config.Connections[c]).Login != null && !outcome.Connections.Any(x => x.Connection == c && x.Status == "skipped"));
            if (chosen == null)
            {
                var why = candidates.Count == 0 ? "it is on no connection of the project" : string.Join("; ", candidates.Select(c => $"`{c}`: {LoginOf(ctx.Config.Connections[c]).Problem ?? "it could not be reached"}"));
                outcome.Skipped.Add((d.Name, $"not compared with its table: {why}"));
                foreach (var c in candidates) if (!outcome.Connections.Any(x => x.Connection == c)) outcome.Connections.Add(new(c, ctx.Config.Connections[c].Engine, "skipped", LoginOf(ctx.Config.Connections[c]).Problem));
                continue;
            }
            if (!byConnection.TryGetValue(chosen, out var list)) byConnection[chosen] = list = [];
            list.Add(d);
        }

        foreach (var (name, descriptors) in byConnection.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var connection = ctx.Config.Connections[name];
            var login = logins[name].Login!;
            var patterns = descriptors.Select(d => { var (s, t) = ImportCommand.Split(d.Name)!.Value; return (ImportCommand.Glob(s), ImportCommand.Glob(t), (string?)s); }).ToList();
            try
            {
                var (rows, skipped, diags, notes) = await ImportCommand.GatherAsync(ctx, ctx.Root, connection, login, patterns, open);
                outcome.MappedModels.AddRange(rows.Select(r => new CaptureRow(name, r)));
                outcome.Diagnostics.AddRange(diags);
                foreach (var n in notes) if (!outcome.Notes.Contains(n)) outcome.Notes.Add(n);
                foreach (var s in skipped) outcome.Skipped.Add((s.Object, s.Reason));
                outcome.Connections.Add(new(name, connection.Engine, "read", null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var why = ex switch
                {
                    GateRefusedException g => g.Diagnostic.Found,
                    CatalogUnavailableException c => c.Message,
                    _ => $"it could not be read ({DriverFailure.Describe(ex)})",
                };
                outcome.Connections.Add(new(name, connection.Engine, "skipped", why));
                foreach (var d in descriptors) outcome.Skipped.Add((d.Name, $"not compared with its table: `{name}` {why}"));
            }
        }
        outcome.Connections.Sort((a, b) => string.CompareOrdinal(a.Connection, b.Connection));
        return outcome;
    }

    /// <summary>Models the connections gave, as rows of the JSON document.</summary>
    public static object MappedModelsPayload(CaptureOutcome o) => o.MappedModels.OrderBy(r => r.Row.Name, StringComparer.Ordinal).Select(r => (object)new
    {
        name = r.Row.Name, object_kind = r.Row.Kind, file = r.Row.File, connection = r.Connection, status = r.Row.Status,
        changes = r.Row.Changes.Select(c => new { kind = c.Kind.ToString().ToLowerInvariant(), column = c.Column, detail = c.Detail }).ToList(),
    }).Concat(o.Skipped.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => (object)new { name = s.Name, status = "skipped", reason = s.Reason, changes = new List<object>() })).ToList();

    /// <summary>
    /// Says what was found, then (unless <paramref name="check"/>) writes the mapped models that changed. In check mode every difference is DDB-461 and the count is returned. <paramref name="confirm"/>, when given,
    /// is asked before anything is written (the interactive `project model update`).
    /// </summary>
    public static (int Exit, int Differences, List<string> Written) Finish(string root, CaptureOutcome o, bool check, Func<int, bool>? confirm, TextWriter output, TextWriter error)
    {
        output.WriteLine();
        output.WriteLine("Mapped models, compared with their tables:");
        foreach (var c in o.Connections.Where(c => c.Status == "skipped")) output.WriteLine($"  connection `{c.Connection}` was skipped: {c.Reason}");
        foreach (var r in o.MappedModels.OrderBy(r => r.Row.Name, StringComparer.Ordinal))
        {
            output.WriteLine($"  {r.Row.Status,-9} {r.Row.Name}  (on {r.Connection})");
            foreach (var change in r.Row.Changes.Take(8)) output.WriteLine($"            {(change.Column == null ? "" : change.Column + ": ")}{change.Kind.ToString().ToLowerInvariant()} {change.Detail}".TrimEnd());
        }
        foreach (var s in o.Skipped.OrderBy(s => s.Name, StringComparer.Ordinal)) output.WriteLine($"  skipped   {s.Name}: {s.Reason}");
        if (o.MappedModels.Count == 0 && o.Skipped.Count == 0) output.WriteLine("  the project has no mapped model in scope");
        foreach (var n in o.Notes) output.WriteLine($"  note: {n}");
        foreach (var d in o.Diagnostics) error.Diag(d);

        var changed = o.MappedModels.Where(r => r.Row.Status is "new" or "changed").ToList();
        var stale = o.MappedModels.Where(r => r.Row.Status is "stale").ToList();
        foreach (var r in changed) { output.WriteLine(); output.Write(UnifiedDiff.Create(r.Row.OldText, r.Row.NewText!, r.Row.File!)); }
        var written = new List<string>();
        if (check)
        {
            foreach (var r in changed)
                error.Diag(new Diagnostic(DiagnosticCatalog.ProjectUpdateDiffers, new(r.Row.File!, 0, 0), $"`{r.Row.File}` differs from the table on `{r.Connection}`: {string.Join("; ", r.Row.Changes.Select(c => c.Column == null ? c.Kind.ToString().ToLowerInvariant() : $"{c.Kind.ToString().ToLowerInvariant()} {c.Column} ({c.Detail})"))}."));
            return (changed.Count == 0 ? CliApp.ExitOk : CliApp.ExitFindings, changed.Count, written);
        }
        if (changed.Count == 0) { output.WriteLine(); output.WriteLine("Every mapped model in scope matches its table."); return (CliApp.ExitOk, 0, written); }
        if (confirm != null && !confirm(changed.Count)) { output.WriteLine("Nothing was written."); return (CliApp.ExitFindings, changed.Count, written); }
        var (files, failed) = Write(root, o, output, error);
        output.WriteLine($"Wrote {files.Count} mapped model(s).");
        return (failed ? CliApp.ExitFindings : CliApp.ExitOk, 0, files);
    }

    /// <summary>Writes the changed mapped models (each only if its file is as it was read). Returns the files written and whether one could not be.</summary>
    public static (List<string> Written, bool Failed) Write(string root, CaptureOutcome o, TextWriter output, TextWriter error)
    {
        var written = new List<string>();
        var failed = false;
        foreach (var r in o.MappedModels.Where(r => r.Row.Status is "new" or "changed").OrderBy(r => r.Row.File, StringComparer.Ordinal))
        {
            var path = Path.Combine(root, r.Row.File!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var problem = DefinitionFile.WriteIfUnchanged(path, r.Row.File!, r.Row.ExpectedHash, r.Row.NewText!);
            if (problem != null) { error.Diag(problem); failed = true; }
            else { output.WriteLine($"wrote {r.Row.File}"); written.Add(r.Row.File!); }
        }
        return (written, failed);
    }
}
