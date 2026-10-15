using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project import` (effect: target read-only; with --write it also writes mapped models under `models/`). Reads the columns, types, nullability and primary key of tables and views in the
/// target through the read login and exports them as mapped models (DESIGN.md 6.5.1), so models over those tables bind offline against what the tables really are. It never
/// runs a query against the data and never changes the target. Without arguments it refreshes the descriptors the project already has.
/// The live table wins for columns, types and nullability; a committed grain and a column the catalog cannot type are kept (MappedModelImport).
/// </summary>
internal static class ImportCommand
{
    internal sealed record Row(string Name, string Kind, string? File, string Status, IReadOnlyList<MappedModelChange> Changes, ImportedObject? Live, DeclaredModel? Descriptor, string? NewText, string? OldText, string? ExpectedHash);
    internal sealed record Skipped(string Object, string Reason);

    public static int Run(CommandSpec spec, string root, string? targetArg, string[] patterns, bool write, bool check, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (write && check) { error.WriteLine("--check writes nothing, so it cannot be combined with --write."); return CliApp.ExitUsage; }
        var ctx = ProjectContext.Load(root);
        var connection = CommandTargets.Resolve(ctx.Config, targetArg, error, readOnlyOk: true);
        if (connection == null) return CliApp.ExitUsage;
        var target = connection.Name; var engine = connection.Engine;
        var (login, missing) = LoginSettings.FromEnvironment(connection, Login.Read, env);

        var committed = ctx.Project.MappedModels.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var models = ctx.Project.Models.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // what to look at: the patterns, or (none given) every descriptor the project has
        var wanted = new List<(Regex SchemaName, Regex Table, string? LiteralSchema)>();
        var refresh = patterns.Length == 0;
        if (refresh)
        {
            if (committed.Count == 0)
            {
                error.WriteLine($"The project has no mapped models yet. Name what to import, for example `{ProductInfo.Cli} {spec.Name} staging.*` or `{ProductInfo.Cli} {spec.Name} sales.orders`.");
                return CliApp.ExitUsage;
            }
            foreach (var n in committed.Keys) { var (s, t) = Split(n)!.Value; wanted.Add((Glob(s), Glob(t), s)); }
        }
        else
            foreach (var p in patterns)
            {
                if (Split(p) is not { } st) { error.WriteLine($"`{p}` is not `schema_name.table_name`. Use `*` and `?` as wildcards, for example `staging.*` or `*.orders`."); return CliApp.ExitUsage; }
                wanted.Add((Glob(st.SchemaName), Glob(st.Table), st.SchemaName.AsSpan().IndexOfAny('*', '?') < 0 ? st.SchemaName : null));
            }

        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}{(write ? " + writes models/" : "")}  |  connection: {target}  |  login: {login?.Describe() ?? "none"}");
        if (missing != null) { error.Diag(missing); return CliApp.ExitFindings; }
        if (login!.Dialect.Catalog == CatalogStyle.None)
        {
            error.WriteLine($"`{target}` has the dialect `{login.Dialect.Name}`, which says `catalog: none`: its tables and columns are not read from a catalog, so they are not guessed. Declare each table by hand as a mapped model (its columns and types in a mapped model).");
            return CliApp.ExitFindings;
        }

        List<Row> rows; List<Skipped> skipped; List<Diagnostic> diags; List<string> catalogNotes;
        try { (rows, skipped, diags, catalogNotes) = GatherAsync(ctx, root, connection, login!, wanted, null).GetAwaiter().GetResult(); }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }
        catch (CatalogUnavailableException ex) { error.WriteLine(ex.Message); return CliApp.ExitFindings; }

        rows = rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        foreach (var d in diags) error.Diag(d);

        output.Payload("connection", target);
        output.Payload("mode", write ? "write" : check ? "check" : "preview");
        output.Payload("mapped_models", rows.Select(r => new
        {
            name = r.Name, object_kind = r.Kind, file = r.File, status = r.Status, grain = r.Descriptor?.Grain ?? [],
            indexes = (r.Descriptor?.Indexes ?? []).Select(i => new { name = i.Name, columns = i.Columns, unique = i.Unique, include = i.Include }).ToList(),
            foreign_keys = (r.Descriptor?.ForeignKeys ?? []).Select(f => new { name = f.Name, columns = f.Columns, references = new { table = f.Table, columns = f.ReferencedColumns } }).ToList(),
            notes = r.Live?.Notes ?? [],
            changes = r.Changes.Select(c => new { kind = c.Kind.ToString().ToLowerInvariant(), column = c.Column, detail = c.Detail }).ToList(),
            columns = r.Live?.Columns.Select(c => new { name = c.Name, native_type = c.NativeType, logical_type = c.LogicalType, fit = c.Fit.ToString().ToLowerInvariant(), reason = c.Reason.Length == 0 ? null : c.Reason, nullable = c.Nullable }).ToList(),
        }).ToList());
        output.Payload("skipped", skipped.Select(s => new { @object = s.Object, reason = s.Reason }).ToList());
        output.Payload("catalog_notes", catalogNotes);

        if (rows.Count == 0)
        {
            output.WriteLine(refresh ? "Nothing to refresh." : $"No table or view matched {string.Join(", ", patterns.Select(p => $"`{p}`"))} in {target}.");
            foreach (var s in skipped) output.WriteLine($"skipped {s.Object}: {s.Reason}");
            return CliApp.ExitOk;
        }

        output.WriteLine();
        foreach (var r in rows)
        {
            output.WriteLine($"{r.Status,-9} {r.Name}  ({r.Kind}{(r.Live != null ? $", {r.Live.Columns.Count} column(s)" : "")})");
            foreach (var note in r.Live?.Notes ?? []) output.WriteLine($"          note: {note}");
            foreach (var c in r.Live?.Columns ?? [])
                if (c.Fit != TypeFit.Exact)
                    output.WriteLine($"          {c.Name}: {c.NativeType} -> {c.LogicalType ?? "no logical type"}{(c.Reason.Length > 0 ? " (" + c.Reason + ")" : "")}");
        }
        foreach (var s in skipped) output.WriteLine($"skipped   {s.Object}: {s.Reason}");
        foreach (var note in catalogNotes) output.WriteLine($"note: {note}");

        var changed = rows.Where(r => r.Status is "new" or "changed").ToList();
        foreach (var r in changed) { output.WriteLine(); output.Write(UnifiedDiff.Create(r.OldText, r.NewText!, r.File!)); }
        var stale = rows.Count(r => r.Status == "stale");
        output.WriteLine();

        if (check)
        {
            if (changed.Count == 0 && stale == 0) { output.WriteLine($"{rows.Count} mapped model(s) match {target}."); return CliApp.ExitOk; }
            foreach (var r in changed) error.Diag(new Diagnostic(DiagnosticCatalog.MappedModelOutOfSync, new(r.File!, 0, 0), $"`{r.File}` differs from {target}: {string.Join("; ", r.Changes.Select(c => c.Column == null ? c.Kind.ToString().ToLowerInvariant() : $"{c.Kind.ToString().ToLowerInvariant()} {c.Column} ({c.Detail})"))}."));
            output.WriteLine($"{changed.Count} descriptor(s) differ from {target}{(stale > 0 ? $", {stale} describe(s) no table" : "")}.");
            return CliApp.ExitFindings;
        }
        if (changed.Count == 0) { output.WriteLine($"{rows.Count} mapped model(s) already match {target}; nothing to write."); return CliApp.ExitOk; }
        if (!write) { output.WriteLine($"{changed.Count} descriptor(s) would be written. Review the diff above, then run again with --write."); return CliApp.ExitOk; }

        var failed = false;
        var written = new List<string>();
        foreach (var r in changed)
        {
            var path = Path.Combine(root, r.File!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var problem = DefinitionFile.WriteIfUnchanged(path, r.File!, r.ExpectedHash, r.NewText!);
            if (problem != null) { error.Diag(problem); failed = true; }
            else { output.WriteLine($"wrote {r.File}"); written.Add(r.File!); }
        }
        output.Payload("written", written);
        output.WriteLine($"Wrote {written.Count} descriptor(s). Compile and check the definitions to see what they change for the models.");
        if (!failed) output.Next("project compile", "project model update --check");
        return failed ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    /// <summary>
    /// Reads the tables the patterns match on one connection and compares each with the mapped model the project has (or would write): the part of `project import` that `project update` and `project model update`
    /// share. Nothing is written. A model of the project is skipped, a name that cannot be a file is said, and a mapped model whose table is not there is `stale`.
    /// </summary>
    internal static async Task<(List<Row> Rows, List<Skipped> Skipped, List<Diagnostic> Diags, List<string> Notes)> GatherAsync(ProjectContext ctx, string root, ConnectionConfig connection, LoginSettings login,
        List<(Regex SchemaName, Regex Table, string? LiteralSchema)> wanted, SessionOpener? open)
    {
        var target = connection.Name; var engine = connection.Engine;
        var committed = ctx.Project.MappedModels.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var models = ctx.Project.Models.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>();
        var catalogNotes = new List<string>();
        var skipped = new List<Skipped>();
        var diags = new List<Diagnostic>();
        await using var read = await (open ?? ReadSession.OpenAsync)(login, default);
        var allSchemas = await SourceCatalogReader.SchemasAsync(read, engine);
        var schemas = allSchemas.Where(s => wanted.Any(w => w.SchemaName.IsMatch(s))).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in schemas)
        {
            if (string.Equals(schema, ctx.Config.TrackingSchemaName, StringComparison.OrdinalIgnoreCase)) { skipped.Add(new(schema, "the tracking schema name holds the tool's own tables")); continue; }
            var shapes = await CatalogReader.ReadObjectsAsync(read, engine, schema);
            var keys = await SourceCatalogReader.PrimaryKeysAsync(read, engine, schema);
            var foreignKeys = await SourceCatalogReader.ForeignKeysAsync(read, engine, schema);
            // the engines' shapes carry their indexes already; an origin's come from its own catalog where it has one (duckdb_indexes(), ODBC's SQLStatistics) and are said to be missing where it has none
            var indexes = await SourceCatalogReader.IndexesAsync(read, engine, schema);
            foreach (var (qualified, shape0) in shapes.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var shape = indexes != null && indexes.TryGetValue(shape0.Name, out var found) ? shape0 with { Physical = shape0.Physical.Concat(found).ToList() } : shape0;
                if (!wanted.Any(w => w.SchemaName.IsMatch(shape.SchemaName) && w.Table.IsMatch(shape.Name))) continue;
                seen.Add(qualified);
                if (models.Contains(qualified)) { skipped.Add(new(qualified, "it is a built model, not a table to map")); continue; }
                var live = MappedModelImport.Describe(engine, shape, keys.GetValueOrDefault(shape.Name), foreignKeys.GetValueOrDefault(shape.Name), ctx.Config.Layout, login.Dialect);
                // a table the project already has a mapped model for is written where that model's file is, whatever the layout says
                if (committed.TryGetValue(qualified, out var existing) && existing.File.Length > 0) live = live with { File = existing.File };
                if (live.File == null)
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.MappedModelNotImportable, new(qualified, 0, 0), $"`{qualified}` has a dot, slash or backslash in its schema name or table name, so it cannot be a path under models/."));
                    skipped.Add(new(qualified, "its name cannot be a file name"));
                    continue;
                }
                rows.Add(Compare(root, target, live, committed.GetValueOrDefault(qualified), diags));
            }
        }
        catalogNotes.AddRange(read.CatalogNotes);
        // descriptors the project has for tables the target does not show (in the schema names searched)
        foreach (var d in committed.Values.Where(d => wanted.Any(w => { var (s, t) = Split(d.Name)!.Value; return w.SchemaName.IsMatch(s) && w.Table.IsMatch(t); })))
        {
            var (s, _) = Split(d.Name)!.Value;
            if (seen.Contains(d.Name) || models.Contains(d.Name)) continue;
            var file = d.File.Length > 0 ? d.File : DeclaredModelWriter.PathFor(s, d.Name[(s.Length + 1)..], ctx.Config.Layout);
            diags.Add(new Diagnostic(DiagnosticCatalog.MappedModelNotImportable, new(file ?? d.Name, 0, 0), $"`{d.Name}` has a mapped model, but {target} shows no table or view of that name.", Fix: "Delete the descriptor or restore the table. The tool never deletes it for you."));
            rows.Add(new Row(d.Name, "unknown", file, "stale", [], null, d, null, null, null));
        }
        return (rows, skipped, diags, catalogNotes);
    }

    private static Row Compare(string root, string connection, ImportedObject live, DeclaredModel? committed, List<Diagnostic> diags)
    {
        var path = Path.Combine(root, live.File!);
        var exists = File.Exists(path);
        var kind = live.Kind.ToString().ToLowerInvariant();
        if (exists && committed == null)
        {
            // a file is there that did not load: a damaged descriptor is never overwritten unseen
            diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(live.File!, 0, 0), $"`{live.File}` exists but is not a valid mapped model, so it was left alone.", Fix: $"Fix it (`{ProductInfo.Cli} project compile` shows what is wrong), or delete it and import again."));
            return new Row(live.QualifiedName, kind, live.File, "invalid", [], live, null, null, null, null);
        }
        foreach (var c in live.Columns.Where(c => c.LogicalType == null))
        {
            var kept = committed?.Columns.Any(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)) == true;
            diags.Add(new Diagnostic(DiagnosticCatalog.MappedColumnNoLogicalType, new(live.File!, 0, 0),
                $"`{live.QualifiedName}.{c.Name}` is {c.NativeType}: {c.Reason}. {(kept ? "The committed declaration was kept." : "The column was left out of the descriptor.")}"));
        }
        // a new mapped model says which connection it exists on; a refresh keeps what the file already said
        var descriptor = MappedModelImport.ToDescriptor(live, committed) with { DeclaredConnections = committed == null ? [connection] : committed.Connections };
        var changes = MappedModelImport.Compare(committed, descriptor);
        var oldText = exists ? File.ReadAllText(path) : null;
        var hash = exists ? DefinitionFile.Hash(File.ReadAllBytes(path)) : null;
        var status = committed == null ? "new" : changes.Count == 0 ? "unchanged" : "changed";
        return new Row(live.QualifiedName, kind, live.File, status, changes, live, descriptor, status is "new" or "changed" ? DeclaredModelWriter.Yaml(descriptor) : null, oldText, hash);
    }

    internal static (string SchemaName, string Table)? Split(string name)
    {
        var i = name.IndexOf('.');
        return i <= 0 || i == name.Length - 1 ? null : (name[..i], name[(i + 1)..]);
    }

    internal static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
