using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project compile` and `dbdatabuild project show loads` (DESIGN.md 6.6 and 9.1). Effect class: repo files only, no target connection.
/// `render` prints by default; `--write` writes the committed `rendered/` tree; `--check` fails if the committed files differ from a fresh
/// render and writes nothing. `loads` prints the model x target x operation table with each pair's matrix status.
/// </summary>
internal static class RenderCommand
{
    public const string RenderedDir = "rendered";

    public static int Render(CommandSpec spec, string projectRoot, string[] models, string[] targets, bool write, bool check, bool content, TextWriter output, TextWriter error, bool header = true)
    {
        if (header) output.WriteLine($"{Core.ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (content && (write || check))
        {
            error.WriteLine("--content only goes with a plain render: --write and --check have their own documents.");
            return CliApp.ExitUsage;
        }
        if (write && check)
        {
            error.WriteLine("--write and --check cannot be combined: --check writes nothing.");
            return CliApp.ExitUsage;
        }
        var ctx = ProjectContext.Load(projectRoot);
        var unknown = targets.FirstOrDefault(t => !ctx.Config.Connections.ContainsKey(t));
        if (unknown != null)
        {
            error.WriteLine($"Unknown connection `{unknown}`. One of: {string.Join(", ", ctx.Config.Connections.Keys.Order(StringComparer.Ordinal))}.");
            return CliApp.ExitUsage;
        }
        var selected = ctx.Select(models, error);
        if (selected == null) return CliApp.ExitUsage;
        var wholeProject = models.Length == 0 && targets.Length == 0;

        var files = new List<RenderedFile>();
        var diags = new List<Diagnostic>(ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code || d.Severity != Severity.Error));
        var rendered = new List<(LoadedModel Model, RenderResult Render, IReadOnlyList<string> Targets)>();
        foreach (var m in selected)
        {
            var modelTargets = ctx.TargetsOf(m.Model.Definition).Where(t => targets.Length == 0 || targets.Contains(t)).ToList();
            var (result, _) = ctx.RenderModel(m.Model, m.Sql, modelTargets);
            files.AddRange(result.Files);
            diags.AddRange(result.Diagnostics);
            rendered.Add((m, result, modelTargets));
        }
        // the load plans and refresh plans of each connection are compiled from the whole project: a selection of models would leave loads out of it
        var planned = models.Length == 0 && !diags.Any(d => d.Severity == Severity.Error);
        if (planned)
        {
            // computed once for every connection: the same rows `publish` writes as metadata_dependency
            var dependencies = MetadataBuilder.Dependencies(ctx);
            foreach (var connection in rendered.SelectMany(r => r.Targets).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var mine = rendered.Where(r => r.Targets.Contains(connection)).Select(r => (r.Model, r.Render)).ToList();
                files.AddRange(RefreshPlanBuilder.Build(ctx, connection, mine, dependencies, diags).Files());
            }
        }
        foreach (var d in diags.DistinctBy(d => (d.Code, d.Location, d.Found))) error.Diag(d);
        var errors = diags.Count(d => d.Severity == Severity.Error);

        var root = Path.Combine(projectRoot, RenderedDir);
        if (check) return Check(root, files, selected, targets, wholeProject, errors, ctx.Config.Connections.Keys, output, error);

        if (write)
        {
            if (errors > 0)
            {
                output.WriteLine($"Nothing was written: {errors} error(s). Fix them and run render again.");
                return CliApp.ExitFindings;
            }
            var (wrote, removed) = Write(root, files, scopeDirs: Scope(files, selected, targets, wholeProject, root, ctx.Config.Connections.Keys));
            output.Payload("files", files.Select(f => new { path = $"{RenderedDir}/{f.Path}", hash = DbDataBuild.State.Hashing.ScriptHash(f.Content) }).ToList());
            output.Payload("wrote", wrote); output.Payload("removed", removed);
            foreach (var f in wrote) output.WriteLine($"wrote {RenderedDir}/{f}");
            foreach (var f in removed) output.WriteLine($"removed {RenderedDir}/{f}");
            output.WriteLine(wrote.Count == 0 && removed.Count == 0 ? $"{files.Count} rendered file(s) already up to date." : $"{files.Count} rendered file(s); {wrote.Count} written, {removed.Count} removed.");
            return CliApp.ExitOk;
        }

        if (content) output.Payload("files", files.Select(f => new { path = $"{RenderedDir}/{f.Path}", hash = DbDataBuild.State.Hashing.ScriptHash(f.Content), content = f.Content }).ToList());
        else output.Payload("files", files.Select(f => new { path = $"{RenderedDir}/{f.Path}", hash = DbDataBuild.State.Hashing.ScriptHash(f.Content) }).ToList());
        output.Payload("operations", files.Count);

        // default: print
        foreach (var f in files)
        {
            output.WriteLine();
            output.WriteLine($"===== {RenderedDir}/{f.Path} =====");
            output.Write(f.Content);
        }
        return errors == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
    }

    /// <summary>The (target, model) directories under rendered/ this run is responsible for.</summary>
    private static HashSet<string> Scope(IReadOnlyList<RenderedFile> files, IReadOnlyList<LoadedModel> selected, string[] targets, bool whole, string renderedRoot, IEnumerable<string> connections)
    {
        var scope = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files) { var parts = f.Path.Split('/'); scope.Add(parts.Length == 2 ? parts[0] : string.Join('/', parts.Take(2))); }       // a file straight under a connection (its loads.yml) puts the connection's own folder in scope, and one in `refresh/` puts that folder in scope
        // directories of selected models that rendered nothing this time (an operation was removed) are in scope too
        foreach (var m in selected)
        {
            foreach (var t in connections.Where(t => targets.Length == 0 || targets.Contains(t)))
                scope.Add($"{t}/{m.Model.Definition.Name}");
            scope.Add($"lowered/{m.Model.Definition.Name}");
        }
        if (whole && Directory.Exists(renderedRoot))
            foreach (var targetDir in Directory.EnumerateDirectories(renderedRoot))
                foreach (var modelDir in Directory.EnumerateDirectories(targetDir))
                    scope.Add($"{Path.GetFileName(targetDir)}/{Path.GetFileName(modelDir)}");   // models that no longer exist
        return scope;
    }

    /// <summary>Whether a file in a directory under `rendered/` is one the compiler writes (so a stale one may be removed): in a connection's `refresh/` folder every `.yml` is.</summary>
    private static bool IsGenerated(string dir, string fileName) =>
        fileName is "manifest.yml" or "lowered.sql" or LoadsDocument.FileName or RefreshPlanDocument.RetiredFileName || ((fileName.StartsWith("load.", StringComparison.Ordinal) || fileName.StartsWith("input.", StringComparison.Ordinal)) && fileName.EndsWith(".sql", StringComparison.Ordinal))
        || (dir.EndsWith("/" + RefreshPlanDocument.FolderName, StringComparison.Ordinal) && fileName.EndsWith(".yml", StringComparison.Ordinal));

    private static (List<string> Wrote, List<string> Removed) Write(string root, IReadOnlyList<RenderedFile> files, HashSet<string> scopeDirs)
    {
        var wrote = new List<string>();
        var removed = new List<string>();
        var desired = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);

        foreach (var f in files)
        {
            var path = Path.Combine(root, f.Path);
            if (File.Exists(path) && File.ReadAllText(path) == f.Content) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(f.Content));
            File.Move(temp, path, overwrite: true);
            wrote.Add(f.Path);
        }

        // stale generated files in the directories this run owns; anything else in them is left alone
        foreach (var dir in scopeDirs.Order(StringComparer.Ordinal))
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full).Where(f => IsGenerated(dir, Path.GetFileName(f))).Order(StringComparer.Ordinal))
            {
                var rel = $"{dir}/{Path.GetFileName(file)}";
                if (desired.ContainsKey(rel)) continue;
                File.Delete(file);
                removed.Add(rel);
            }
            if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        foreach (var targetDir in Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : [])
            if (!Directory.EnumerateFileSystemEntries(targetDir).Any()) Directory.Delete(targetDir);
        return (wrote, removed);
    }

    private static int Check(string root, IReadOnlyList<RenderedFile> files, IReadOnlyList<LoadedModel> selected, string[] targets, bool whole, int renderErrors, IEnumerable<string> connections, TextWriter output, TextWriter error)
    {
        var desired = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        var differences = new List<Diagnostic>();
        Diagnostic Out(string path, string what) => new(DiagnosticCatalog.RenderedFileOutOfDate, new($"{RenderedDir}/{path}", 0, 0), what);

        foreach (var (path, content) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            var full = Path.Combine(root, path);
            if (!File.Exists(full)) differences.Add(Out(path, $"`{RenderedDir}/{path}` is missing."));
            else if (File.ReadAllText(full) != content) differences.Add(Out(path, $"`{RenderedDir}/{path}` differs from a fresh render."));
        }
        foreach (var dir in Scope(files, selected, targets, whole, root, connections).Order(StringComparer.Ordinal))
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full).Where(f => IsGenerated(dir, Path.GetFileName(f))).Order(StringComparer.Ordinal))
            {
                var rel = $"{dir}/{Path.GetFileName(file)}";
                if (!desired.ContainsKey(rel)) differences.Add(Out(rel, $"`{RenderedDir}/{rel}` is committed but is no longer rendered."));
            }
        }
        foreach (var d in differences) error.Diag(d);
        output.Payload("files", desired.Select(d => new { path = $"{RenderedDir}/{d.Key}", hash = DbDataBuild.State.Hashing.ScriptHash(d.Value) }).ToList());
        output.Payload("out_of_date", differences.Select(d => d.Location.File).ToList());
        if (differences.Count == 0 && renderErrors == 0)
        {
            output.WriteLine($"OK: {desired.Count} rendered file(s) match a fresh render.");
            return CliApp.ExitOk;
        }
        output.WriteLine($"FAILED: {differences.Count} rendered file(s) out of date{(renderErrors > 0 ? $", {renderErrors} render error(s)" : "")}. Run `{Core.ProductInfo.Cli} project compile` and commit the result.");
        return CliApp.ExitFindings;
    }

    // ---------------- loads ----------------

    public static int Loads(CommandSpec spec, string projectRoot, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{Core.ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        var ctx = ProjectContext.Load(projectRoot);
        var rows = new List<string[]>();
        var diags = new List<Diagnostic>();
        foreach (var m in ctx.Select([], error)!)
        {
            var def = m.Model.Definition;
            var targets = ctx.TargetsOf(def);
            if (def.Kind == ModelKinds.View)
            {
                rows.Add([def.Name, string.Join(", ", targets), "-", "-", "-", "view: DDL only"]);
                continue;
            }
            var (result, _) = ctx.RenderModel(m.Model, m.Sql, targets);
            diags.AddRange(result.Diagnostics);
            foreach (var o in result.Operations.OrderBy(o => o.Target, StringComparer.Ordinal).ThenBy(o => o.Operation, StringComparer.Ordinal))
                rows.Add([o.Model, o.Target, o.Operation, o.Strategy, o.IsDefault ? "default" : "", o.Status + (o.Findings.Count > 0 ? $" ({string.Join(", ", o.Findings)})" : "")]);
        }
        foreach (var d in diags.DistinctBy(d => (d.Code, d.Found))) error.Diag(d);

        string[] header = ["model", "connection", "operation", "strategy", "default", "matrix status"];
        var widths = Enumerable.Range(0, header.Length).Select(i => Math.Max(header[i].Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
        output.Payload("operations", rows.Select(r => new { model = r[0], connection = r[1], operation = r[2], strategy = r[3], is_default = r[4] == "default", matrix_status = r[5] }).ToList());
        output.WriteLine(Line(header));
        foreach (var r in rows) output.WriteLine(Line(r));
        var unsupported = rows.Count(r => r[5].StartsWith("unsupported", StringComparison.Ordinal));
        output.WriteLine();
        output.WriteLine($"{rows.Count} row(s); {unsupported} unsupported. Status is the worst of the strategy's and the query's matrix statuses on that target.");
        return diags.Any(d => d.Severity == Severity.Error) || unsupported > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
    }
}
