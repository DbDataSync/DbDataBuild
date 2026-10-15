using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Execution;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>Opens the read side of a login. The command's one way to a connection, so a test can give it a source of its own.</summary>
internal delegate Task<ReadSession> SessionOpener(LoginSettings login, CancellationToken ct);

/// <summary>
/// `dbdatabuild project update [selectors]` (effect: target read-only, writes project files): the one command that brings in everything that comes from connections. It is `project model update` for every model
/// (the definitions from their queries, then the metadata of every mapped model and of the tables copies read from, from the connections they are on: `ConnectionCapture`) plus the dialect of each connection whose
/// server can say it, committed in <c>dialects.lock.yml</c> so that compile and the plans stay offline and a refresh can tell that the server has changed. `--offline` does only the part that needs no connection; `--check`
/// writes nothing and fails when anything would change. A connection whose read login is missing or that cannot be reached is said and skipped; the rest still updates.
/// </summary>
internal static class ProjectUpdateCommand
{
    /// <summary>For tests only: the way to a connection, in place of the real one, for the thread (async flow) that sets it.</summary>
    internal static readonly AsyncLocal<SessionOpener?> TestOpener = new();

    private sealed class Tally { public int Differences; }

    private sealed record DialectRow(string Connection, string Driver, string Status, string? Dialect, ServerFacts? Reported, string? ProfileHash, IReadOnlyList<string> Changes, string? Note, DialectLockEntry? Entry);

    public static int Run(CommandSpec spec, string root, string[] selectors, bool offline, bool check, FileInfo? answers, bool acceptInferred, TextWriter output, TextWriter error, Func<string, string?> env,
        SessionOpener? open = null)
    {
        if (check && (answers != null || acceptInferred)) { error.WriteLine("--check asks nothing and writes nothing, so it cannot be combined with --answers or --accept-inferred."); return CliApp.ExitUsage; }
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}{(offline ? "" : " + reads the connections")}  |  {(check ? "check: writes nothing" : "writes the project's files")}");
        var ctx = ProjectContext.Load(root);
        foreach (var d in ctx.Diagnostics.Where(d => d.Code is "DDB-457" or "DDB-459")) error.Diag(d);
        var selection = UpdateSelection.Resolve(ctx, selectors, error);
        if (selection == null) return CliApp.ExitUsage;
        output.Payload("mode", check ? "check" : "write");
        output.Payload("online", !offline);

        var exit = CliApp.ExitOk;
        var tally = new Tally();
        var written = new List<string>();

        // ---- the part that needs no connection: the definitions of the models, from their queries ----
        var offlineExit = RunDefinitions(spec, ctx, selection, answers, acceptInferred, check, output, error, written, tally);
        if (offlineExit == CliApp.ExitUsage) return offlineExit;
        if (offlineExit != CliApp.ExitOk) exit = CliApp.ExitFindings;

        // ---- the part that reads the connections ----
        if (offline) output.WriteLine("Offline: no connection was read, so no dialect was asked and no mapped model was compared with its table.");
        else
        {
            var dialectExit = DialectsAsync(spec, ctx, selection, check, output, error, env, open ?? TestOpener.Value ?? ReadSession.OpenAsync, written, tally).GetAwaiter().GetResult();
            if (dialectExit != CliApp.ExitOk) exit = CliApp.ExitFindings;
            // the metadata of the mapped models and of the tables copies read: the same code `project model update` runs
            var outcome = ConnectionCapture.GatherAsync(ctx, selection, env, open ?? TestOpener.Value ?? ReadSession.OpenAsync).GetAwaiter().GetResult();
            output.Payload("connections", outcome.Connections.Select(c => new { connection = c.Connection, driver = c.Driver, status = c.Status, reason = c.Reason }).ToList());
            output.Payload("mapped_models", ConnectionCapture.MappedModelsPayload(outcome));
            output.Payload("catalog_notes", outcome.Notes);
            var (captureExit, captureDifferences, captureWritten) = ConnectionCapture.Finish(root, outcome, check, null, output, error);
            tally.Differences += captureDifferences;
            written.AddRange(captureWritten);
            if (captureExit != CliApp.ExitOk && !check) exit = CliApp.ExitFindings;
        }

        output.Payload("differences", tally.Differences);
        output.Payload("written", written);
        if (check && tally.Differences > 0)
        {
            output.WriteLine($"{tally.Differences} difference(s): the project is not up to date with its connections. Run `{ProductInfo.Cli} {spec.Name}{(offline ? " --offline" : "")}` to bring it up to date.");
            return CliApp.ExitFindings;
        }
        if (check && exit == CliApp.ExitOk) output.WriteLine("Up to date: nothing would change.");
        if (!check && written.Count > 0 && exit == CliApp.ExitOk) output.Next("project compile");
        return exit;
    }

    // ---- definitions: `project model update`'s own work, run through it ----

    private static int RunDefinitions(CommandSpec spec, ProjectContext ctx, UpdateSelection selection, FileInfo? answers, bool acceptInferred, bool check, TextWriter output, TextWriter error, List<string> written, Tally tally)
    {
        // the definitions that have a query: a copy and a mapped model have none to define from (their columns are the table's), and are the connections' part
        var paths = selection.Everything ? [] : ctx.Project.BuiltModels.Where(s => selection.Includes(s.Definition.Name) && !s.Definition.IsCopy && s.DefinitionFile.EndsWith(".yml", StringComparison.Ordinal)).Select(s => s.QueryFile).ToArray();
        if (!selection.Everything && paths.Length == 0) return CliApp.ExitOk;
        if (!Directory.Exists(Path.Combine(ctx.Root, ProjectValidator.ModelsDir))) return CliApp.ExitOk;      // a project with no models yet has no definitions to update
        var report = CommandReport.Create(true, spec.Name, TextWriter.Null, TextWriter.Null);
        var exit = DefineCommand.Run(spec, ctx.Root, paths, answers, write: !check, check, acceptInferred, report.Output, report.Error, TextReader.Null, interactive: false);
        foreach (var line in report.MessageLines.Skip(1)) if (!line.StartsWith("Config:", StringComparison.Ordinal)) output.WriteLine(line);
        foreach (var line in report.ErrorLines) error.WriteLine(line);
        foreach (var d in report.Diagnostics) error.Diag(d);
        if (exit == CliApp.ExitUsage) return exit;
        var count = report.Data.TryGetValue("differences", out var diff) && diff is int n ? n : 0;
        tally.Differences += count;
        if (report.Data.TryGetValue("written", out var files) && files is IEnumerable<string> list) written.AddRange(list);
        output.Payload("definitions", new
        {
            checked_models = report.Data.TryGetValue("definitions", out var defs) && defs is int c ? (int?)c : null,
            models = report.Data.GetValueOrDefault("models"),
        });
        return exit;
    }

    // ---- dialects ----

    private static async Task<int> DialectsAsync(CommandSpec spec, ProjectContext ctx, UpdateSelection selection, bool check, TextWriter output, TextWriter error, Func<string, string?> env, SessionOpener open,
        List<string> written, Tally tally)
    {
        var config = ctx.Config;
        var inScope = selection.Everything ? config.Connections.Where(c => c.Value.DeclaredKind != null).Select(c => c.Key).ToHashSet(StringComparer.Ordinal) : selection.ConnectionsOf(ctx).ToHashSet(StringComparer.Ordinal);
        var rows = new List<DialectRow>();
        var entries = config.Connections.Values.Where(c => c.DialectLock != null).ToDictionary(c => c.Name, c => c.DialectLock!, StringComparer.Ordinal);
        var kept = new Dictionary<string, DialectLockEntry>(entries, StringComparer.Ordinal);
        var exit = CliApp.ExitOk;

        foreach (var connection in config.Connections.Values.Where(c => c.DeclaredKind != null && inScope.Contains(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var kind = connection.Kind;
            if (!kind.DialectIsDiscoverable)
            {
                rows.Add(new(connection.Name, kind.Driver, kind.DialectDeclared && kind.Driver is "odbc" or "flightsql" ? "explicit" : "nothing_to_discover", kind.Dialect, null, null, [],
                    kind.Driver == "flightsql" ? $"`engine` names the dialect (`{kind.Dialect}`), which always wins; the server was not asked" : $"{kind.Driver} has nothing to discover beyond what `engine` says (dialect `{kind.Dialect}`)", null));
                kept.Remove(connection.Name);      // an explicit keyword wins: a stale entry is dropped
                continue;
            }
            var (login, missing) = LoginSettings.FromEnvironment(connection, Login.Read, env);
            if (login == null) { rows.Add(Skipped(connection, $"its read login is not set ({LoginSettings.VariableName(connection.Name, Login.Read)}), so its server was not asked; the committed dialect, if any, is kept")); continue; }
            ServerFacts? facts;
            try
            {
                await using var session = await open(login, default);
                facts = await session.ServerFactsAsync();
            }
            catch (GateRefusedException ex) { rows.Add(Skipped(connection, ex.Diagnostic.Found)); continue; }
            catch (Exception ex) when (ex is not OperationCanceledException) { rows.Add(Skipped(connection, $"it could not be reached ({DriverFailure.Describe(ex)}), so its server was not asked; the committed dialect, if any, is kept")); continue; }
            var note = facts == null ? "the server does not implement GetSqlInfo, so it says nothing about itself" : facts.IsEmpty ? "the server answered GetSqlInfo with nothing the tool reads" : null;
            facts ??= ServerFacts.Nothing;
            var chosen = DialectDiscovery.Choose(facts, config.Dialects);
            var entry = DialectLockEntry.From(connection.Name, facts, chosen);
            entries.TryGetValue(connection.Name, out var committed);
            var changes = committed == null ? [] : DialectLockComparison.Differences(committed, chosen, facts).ToList();
            var versionOnly = committed != null && changes.Count == 0 && !committed.SameContentAs(entry);
            var status = committed == null ? "new" : changes.Count > 0 ? "changed" : versionOnly ? "version_changed" : "unchanged";
            kept[connection.Name] = entry;
            rows.Add(new(connection.Name, kind.Driver, status, chosen.Dialect, facts, entry.Hash, changes, string.Join("; ", new[] { note }.Concat(chosen.Reasons.Select(r => r)).Where(x => x != null)), entry));
        }
        // an entry for a connection the project no longer has, or that now names its dialect, goes (an entry for one out of scope stays)
        foreach (var gone in kept.Keys.Where(k => !config.Connections.TryGetValue(k, out var c) || !c.Kind.DialectIsDiscoverable).ToList()) kept.Remove(gone);
        foreach (var gone in entries.Keys.Where(k => !config.Connections.ContainsKey(k)).ToList()) { kept.Remove(gone); rows.Add(new(gone, "flightsql", "removed", null, null, null, [], "the project has no such connection any more", null)); }

        // ---- say what was found ----
        output.WriteLine();
        output.WriteLine("Dialects:");
        if (rows.Count == 0) output.WriteLine("  no connection in scope");
        foreach (var r in rows)
        {
            var what = r.Status switch
            {
                "new" => $"found: {Describe(r)}",
                "changed" => $"CHANGED: {Describe(r)} ({string.Join("; ", r.Changes)})",
                "version_changed" => $"unchanged, the server's version is now {r.Reported?.ServerVersion ?? "unreported"}",
                "unchanged" => $"unchanged: {Describe(r)}",
                _ => r.Note ?? r.Status,
            };
            output.WriteLine($"  {r.Connection,-18} {r.Driver,-9} {what}");
            if (r.Status is "new" or "changed" or "unchanged" && r.Note is { Length: > 0 } n) output.WriteLine($"  {"",-18} {"",-9} ({n})");
        }
        if (rows.Any(r => r.Driver is "odbc" or "duckdb" or "sqlserver" or "postgres" or "fabric" && r.Status == "nothing_to_discover"))
            output.WriteLine("  (ODBC, DuckDB and the built-in engines have nothing to discover beyond what `engine` and the driver say; only a Flight SQL server is asked.)");

        // ---- the committed file ----
        var path = Path.Combine(ctx.Root, ProductInfo.DialectLockFile);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var fileStatus = "unchanged";
        string? newText = kept.Count == 0 ? null : DialectLockFile.Render(kept.Values);
        if (existing != newText && !(existing == null && newText == null))
            fileStatus = existing == null ? "new" : newText == null ? "removed" : "changed";
        // a file that did not load (DDB-457) is rewritten from the servers only when every connection in it was asked again; otherwise it is left alone
        var damaged = ctx.Diagnostics.Any(d => d.Code == "DDB-457");
        if (damaged && rows.Any(r => r.Status == "skipped")) { fileStatus = "unchanged"; newText = existing; }
        output.Payload("dialects", rows.Select(r => new
        {
            connection = r.Connection, driver = r.Driver, status = r.Status, dialect = r.Dialect, profile_hash = r.ProfileHash,
            server = r.Reported?.ServerName, server_version = r.Reported?.ServerVersion, identifier_quote = r.Reported?.IdentifierQuote, identifier_case = r.Reported?.IdentifierCase, read_only = r.Reported?.ReadOnly,
            changes = r.Changes, note = r.Note,
        }).ToList());
        output.Payload("dialects_file", new { path = ProductInfo.DialectLockFile, status = fileStatus });
        if (fileStatus != "unchanged")
        {
            output.WriteLine();
            output.Write(UnifiedDiff.Create(existing, newText ?? "", ProductInfo.DialectLockFile));
            if (newText == null) output.WriteLine($"{ProductInfo.DialectLockFile} would be deleted: no connection needs an entry.");
            var meaningful = rows.Count(r => r.Status is "new" or "changed" or "removed");
            if (check)
            {
                if (meaningful > 0 || fileStatus is "new" or "removed" || damaged)
                {
                    foreach (var r in rows.Where(r => r.Status is "new" or "changed" or "removed"))
                        error.Diag(new Diagnostic(DiagnosticCatalog.ProjectUpdateDiffers, new(ProductInfo.DialectLockFile, 0, 0), r.Status switch
                        {
                            "new" => $"{ProductInfo.DialectLockFile} has no entry for `{r.Connection}`, whose server reports the dialect `{r.Dialect}`.",
                            "changed" => $"The server of `{r.Connection}` now reports a different dialect than {ProductInfo.DialectLockFile} says: {string.Join("; ", r.Changes)}.",
                            _ => $"{ProductInfo.DialectLockFile} has an entry for `{r.Connection}`, which the project does not use.",
                        }));
                    tally.Differences += Math.Max(meaningful, 1);
                }
            }
            else
            {
                if (newText == null) File.Delete(path);
                else
                {
                    var problem = DefinitionFile.WriteIfUnchanged(path, ProductInfo.DialectLockFile, existing == null ? null : DefinitionFile.Hash(File.ReadAllBytes(path)), newText);
                    if (problem != null) { error.Diag(problem); return CliApp.ExitFindings; }
                }
                written.Add(ProductInfo.DialectLockFile);
                output.WriteLine(newText == null ? $"Deleted {ProductInfo.DialectLockFile}." : $"Wrote {ProductInfo.DialectLockFile}. Commit it: compile and the plans read it, and a refresh fails the origin if the server stops agreeing with it.");
            }
        }
        else if (rows.Any(r => r.Status == "skipped") == false) output.WriteLine($"{ProductInfo.DialectLockFile} is up to date.");
        return exit;
    }

    private static DialectRow Skipped(ConnectionConfig connection, string reason) =>
        new(connection.Name, connection.Kind.Driver, "skipped", connection.DialectLock?.Dialect, null, connection.DialectLock?.Hash, [], reason, null);

    private static string Describe(DialectRow r) =>
        $"dialect `{r.Dialect}`{(r.Reported?.ServerName is { } s ? $" (server {s}{(r.Reported.ServerVersion is { } v ? " " + v : "")})" : "")}";
}
