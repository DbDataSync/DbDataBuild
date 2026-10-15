using System.Diagnostics;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

internal static class CommandTargets
{
    /// <summary>The connection names of the project (the engine-named ones included), for checks that run before the project is fully loaded. A damaged project file gives the engine names; the command that reads it reports the damage.</summary>
    public static IReadOnlyList<string> NamesOf(string projectRoot) =>
        [.. ProjectConfigLoader.LoadFromProject(projectRoot, []).Connections.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The connection a command works on: the flag, or the project's only default connection.</summary>
    /// <param name="readOnlyOk">Whether the command only reads the connection (`inspect`, `compare`, `monitor`, `status`, `project import`). A `readonly` connection is refused (DDB-455) by every command that writes to it or builds on it.</param>
    public static ConnectionConfig? Resolve(ProjectConfig config, string? arg, TextWriter error, bool readOnlyOk = false)
    {
        var name = arg ?? (config.DefaultConnections.Count == 1 ? config.DefaultConnections[0] : null);
        if (name == null) { error.WriteLine($"A connection is required: the project has {config.DefaultConnections.Count} default connections ({string.Join(", ", config.DefaultConnections)}). Name one after the command, for example `{ProductInfo.Cli} connection status {(config.DefaultConnections.Count > 0 ? config.DefaultConnections[0] : "<connection>")}`."); return null; }
        if (!config.Connections.TryGetValue(name, out var connection)) { error.WriteLine($"Unknown connection `{name}`. One of: {string.Join(", ", config.Connections.Keys.Order(StringComparer.Ordinal))}."); return null; }
        if (connection.ReadOnly && !readOnlyOk)
        {
            error.Diag(new Diagnostic(DiagnosticCatalog.ReadOnlyConnectionNotWritable, new($"connection:{connection.Name}", 0, 0),
                $"The connection `{connection.Name}` is `readonly` ({connection.Kind.Describe()}): this command writes to a connection or builds on it.",
                Fix: "Use it as an origin (a copy, a mapped or native model, a `source` resolver), or read it with `connection inspect`, `compare`, `monitor` and `project import`."));
            return null;
        }
        return connection;
    }
}

internal static class GitInfo
{
    /// <summary>
    /// The files (relative to <paramref name="root"/>, with `/`) that differ from <paramref name="gitRef"/> in the working tree, committed or not, and the untracked ones. Returns the reason instead when git cannot say
    /// (not a repository, no such ref).
    /// </summary>
    public static (HashSet<string>? Files, string? Error) ChangedFiles(string root, string gitRef)
    {
        (string? Out, string Err) Git(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p == null) return (null, "git could not be started");
                // both streams are read at once: git can write enough to stderr (line-ending warnings, on Windows) to block while standard output is being read
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return (null, "git did not finish within a minute"); }
                return (p.ExitCode == 0 ? o.GetAwaiter().GetResult() : null, e.GetAwaiter().GetResult().Split('\n')[0].Trim());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (null, "git is not available"); }
        }
        var diff = Git($"diff --name-only --relative {Quote(gitRef)} --");
        if (diff.Out == null) return (null, $"git could not compare with `{gitRef}`: {(diff.Err.Length > 0 ? diff.Err : "unknown error")}");
        var untracked = Git("ls-files --others --exclude-standard");
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in (diff.Out + "\n" + (untracked.Out ?? "")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) files.Add(line.Replace('\\', '/'));
        return (files, null);
    }

    private static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";

    /// <summary>The commit and whether the working tree has uncommitted changes. Null commit when this is not a git repository or git is unavailable.</summary>
    public static (string? Commit, bool Dirty) Read(string root)
    {
        static string? Run(string root, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var o = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();                                  // read at once: a full error pipe would block git while standard output is awaited
                if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return null; }
                return p.ExitCode == 0 ? o.GetAwaiter().GetResult().Trim() : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        }
        var commit = Run(root, "rev-parse HEAD");
        if (commit == null) return (null, false);
        // untracked files inside plans/ and .dbdatabuild/ are this tool's own output and do not make the tree dirty
        var status = Run(root, "status --porcelain -- . :(exclude)plans :(exclude).dbdatabuild");
        return (commit, !string.IsNullOrEmpty(status));
    }
}

/// <summary>
/// What `plan` and `check` share: the offline preflight, the read login, the live snapshot and the planner input. Nothing here writes to the target
/// or to the repository.
/// </summary>
internal sealed class PlanningSession
{
    public required string Root { get; init; }
    public required string Target { get; init; }
    public required ProjectContext Context { get; init; }
    public required PlanInput Input { get; init; }
    public required IReadOnlyList<Diagnostic> Warnings { get; init; }

    /// <summary>Returns the session, or null and the exit code after printing why not.</summary>
    public static (PlanningSession? Session, int Exit) Prepare(CommandSpec spec, string root, string? targetArg, string[] models, TextWriter output, TextWriter error, Func<string, string?> env,
        IReadOnlyDictionary<string, string>? operations = null, IReadOnlySet<string>? backfills = null, IReadOnlyList<InputPin>? pins = null)
    {
        var ctx = ProjectContext.Load(root);
        var connection = CommandTargets.Resolve(ctx.Config, targetArg, error, readOnlyOk: spec.Name == "connection status");
        if (connection == null) return (null, CliApp.ExitUsage);
        var target = connection.Name; var engine = connection.Engine;

        var (login, missing) = LoginSettings.FromEnvironment(connection, Login.Read, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {target}  |  login: {login?.Describe() ?? "none"}");
        output.Payload("effect", spec.Effect.Describe());
        output.Payload("login", login?.Describe());
        output.WriteLine($"Effective: {ctx.Config.Describe()}");
        if (missing != null) { error.Diag(missing); return (null, CliApp.ExitFindings); }

        var selected = ctx.Select(models, error);
        if (selected == null) return (null, CliApp.ExitUsage);
        foreach (var named in (operations ?? new Dictionary<string, string>()).Keys.Concat(backfills ?? new HashSet<string>()))
            if (!ctx.Project.BuiltModels.Any(m => m.Definition.Name == named)) { error.WriteLine($"`{named}` is not a model of this project."); return (null, CliApp.ExitUsage); }
        // each model's query as this connection reads it (a name given by a parameter may be another on another connection)
        var mine = selected.Where(m => ctx.TargetsOf(m.Model.Definition).Contains(target)).Select(m => m with { Sql = m.Model.ReadQuery(root, ctx.Config, target) }).ToList();
        foreach (var skipped in selected.Except(mine)) output.WriteLine($"note: {skipped.Model.Definition.Name} does not declare target `{target}` and is not planned.");

        // ---- offline preflight: nothing is planned from a project that does not validate (DESIGN.md 11) ----
        var findings = new List<Diagnostic>(ctx.Diagnostics.Where(d => d.Code != DiagnosticCatalog.OrphanFile.Code));
        var sources = mine.Select(m => m.Model).ToList();
        findings.AddRange(ProjectChecks.Run(sources, ctx.Config, [target], root, ctx.Lowering));
        findings.AddRange(ProjectChecks.Reachability(ctx, [target]));
        var defineTargets = mine.Select(m => new DefineTarget(m.Model.Definition.Name, m.Model.DefinitionFile, m.Model.QueryFile, m.Sql,
            File.ReadAllText(Path.Combine(root, m.Model.DefinitionFile)), m.Model.Definition, [], ctx.MacroSupportFor(m.Model, m.Sql))).ToList();
        var graph = new ModelGraph(ctx.Project.Models, ctx.Project.DeclaredModelsAndStaging);
        findings.AddRange(new DefineEngine(graph, ctx.Config, ctx.Linter).Check(defineTargets));

        // an origin of a copy is checked against its declaration (DDB-230) by the apply, just before that origin's own transfer
        findings.AddRange(NativeShapeCheck.Run(ctx, mine.SelectMany(m => ctx.BaseTablesOf(m.Model, m.Sql)), target, env));

        var renderedOps = new List<RenderedOperation>();
        foreach (var m in mine)
        {
            var (result, _) = ctx.RenderModel(m.Model, m.Sql, [target]);
            renderedOps.AddRange(result.Loads);
            foreach (var file in result.Files)
            {
                var path = Path.Combine(root, RenderCommand.RenderedDir, file.Path);
                if (!File.Exists(path)) findings.Add(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new($"{RenderCommand.RenderedDir}/{file.Path}", 0, 0), $"`{RenderCommand.RenderedDir}/{file.Path}` is missing."));
                else if (File.ReadAllText(path) != file.Content) findings.Add(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new($"{RenderCommand.RenderedDir}/{file.Path}", 0, 0), $"`{RenderCommand.RenderedDir}/{file.Path}` differs from a fresh render."));
            }
        }
        // one finding for all the rendered files that are missing or differ (the cause is one: `render --write` was not run), with the first few named
        var stale = findings.Where(d => d.Code == DiagnosticCatalog.RenderedFileOutOfDate.Code).DistinctBy(d => d.Location).ToList();
        if (stale.Count > 1)
        {
            findings.RemoveAll(d => d.Code == DiagnosticCatalog.RenderedFileOutOfDate.Code);
            findings.Add(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, stale[0].Location, $"{stale.Count} rendered files are missing or differ from a fresh render (for example {string.Join(", ", stale.Take(3).Select(d => $"`{d.Location.File}`"))}); run `{ProductInfo.Cli} project compile`."));
        }
        var distinct = findings.DistinctBy(d => (d.Code, d.Location, d.Found)).ToList();
        foreach (var d in distinct.Where(d => d.Severity != Severity.Error)) error.Diag(d);
        var errors = distinct.Where(d => d.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            foreach (var d in errors) error.Diag(d);
            output.WriteLine($"Nothing was planned: {errors.Count} error(s) in the project. Fix them (`{ProductInfo.Cli} project compile`, `{ProductInfo.Cli} project model update --check`, `{ProductInfo.Cli} project compile --check` show them).");
            return (null, CliApp.ExitFindings);
        }

        // ---- the test gate: the tests the project names must pass before anything is read from the target ----
        if (ctx.Config.TestGateTags.Count > 0)
        {
            var gateOut = new StringWriter(); var gateErr = new StringWriter();
            var gateExit = TestCommand.Run(CommandSpecs.All.First(s => s.Name == "project tests run"), root, [], [.. ctx.Config.TestGateTags], null, 5, false, gateOut, gateErr);
            var summary = gateOut.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.Contains("test(s):") || l.StartsWith("No tests", StringComparison.Ordinal)) ?? "";
            if (gateExit != CliApp.ExitOk)
            {
                error.Write(gateErr.ToString());
                output.Write(gateOut.ToString());
                output.WriteLine($"Nothing was planned: a test tagged {string.Join(", ", ctx.Config.TestGateTags)} (`tests.gate` in {ProductInfo.ConfigFile}) did not pass. Fix it, or run `{ProductInfo.Cli} project tests run --tag {ctx.Config.TestGateTags[0]}` to see it alone.");
                return (null, CliApp.ExitFindings);
            }
            output.WriteLine($"Test gate (tags {string.Join(", ", ctx.Config.TestGateTags)}): {summary.Trim()}");
        }

        // ---- the live side, on the read login ----
        var planned = mine.Select(m =>
        {
            var hash = ctx.DefinitionHashOf(m.Sql);
            // a copy reads its staging table, which the plan's own transfer step creates: it has no base table to wait for
            var bases = m.Model.Definition is { IsCopy: true, LocalCopy: false } ? [] : ctx.WithNativeReads(ctx.BaseTablesOf(m.Model, m.Sql));
            // views are transpiled from the lowered query too (errors were reported in the preflight, so a failed lowering here is not reachable)
            var body = ctx.Lowering.Enabled && ctx.Lowering.Lower(m.Model, m.Sql, m.Model.QueryParameterList(root, ctx.Config)).Model is { } lowered ? lowered.Sql : m.Sql;
            return new PlannedModel(m.Model.Definition, body, m.Model.QueryFile, hash, bases, HookLoader.Load(m.Model, ctx.Config, target, root, new List<Diagnostic>()), ctx.OriginsOf(m.Model.Definition, target).ToList(), Merge(m.Model.ParametersFor(ctx.Config, target), ctx.Lowering.NativeValuesFor(m.Sql, target)), ctx.Lowering.NativeUsesFor(m.Sql));
        }).ToList();

        // where the records about this connection are kept: itself, another connection (its read login is needed), or nowhere (a warning, unless that was chosen)
        var trackingResolution = ctx.Config.TrackingOf(target);
        LoginSettings? trackingRead = null;
        if (trackingResolution.Target is { } trackingTarget && trackingTarget.Connection != target)
        {
            var (tr, trm) = LoginSettings.FromEnvironment(trackingTarget.Connection, trackingTarget.Engine, Login.Read, env);
            if (tr == null) { error.Diag(trm!); return (null, CliApp.ExitFindings); }
            trackingRead = tr;
        }
        else if (trackingResolution.Target == null && !trackingResolution.Explicit)
            error.Diag(new Diagnostic(DiagnosticCatalog.TrackingNotConfigured, new($"connection:{target}", 0, 0), $"Nothing is tracked for `{target}`: plans are made from the declared shape against the live one, every change to an existing object is marked risky, and nothing is recorded when the plan is applied."));
        DbDataBuild.State.TrackingScope? trackingScope = trackingResolution.Target is { } ts ? new DbDataBuild.State.TrackingScope(ts.Engine, ts.SchemaName, target) : null;

        // the routines native models list under `track_definition`, against the last record in the tracking tables
        var watched = NativeDefinitions.InPlay(ctx, mine.Select(m => (m.Model, m.Sql)), target);
        if (watched.Count > 0)
        {
            var definitionFindings = new List<Diagnostic>();
            try
            {
                Task.Run(async () =>
                {
                    await using var ownRead = trackingRead == null || trackingScope == null ? null : await ReadSession.OpenAsync(trackingRead);
                    await using var targetRead = trackingScope != null && ownRead == null ? await ReadSession.OpenAsync(login!) : null;
                    definitionFindings.AddRange(NativeDefinitions.Check(ctx, watched, trackingScope, ownRead ?? targetRead, env));
                }).GetAwaiter().GetResult();
            }
            catch (GateRefusedException) { /* the tracking tables are reported as not ready by the snapshot just below */ }
            foreach (var d in definitionFindings) error.Diag(d);
            if (definitionFindings.Any(d => d.Severity == Severity.Error))
            {
                output.WriteLine("Nothing was planned: a routine a native model depends on changed (`policy.severity.native_definition_changed: error`).");
                return (null, CliApp.ExitFindings);
            }
        }

        TargetSnapshot snapshot;
        IReadOnlyList<ColumnHistoryEntry> history = [];
        var resolved = new Dictionary<string, ResolverOutcome>();
        var rangeBounds = new Dictionary<string, ColumnBounds>();
        var loadInputs = new Dictionary<string, PlannedLoadInputs>();
        var inputProblems = new List<string>();
        try
        {
            using var cts = new CancellationTokenSource();
            Dictionary<string, string?> marks;
            (snapshot, resolved, rangeBounds, marks, loadInputs, inputProblems) = Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login!);
                await using var ownTrackingRead = trackingRead == null ? null : await ReadSession.OpenAsync(trackingRead);
                var trackRead = trackingScope == null ? null : ownTrackingRead ?? read;
                if (trackingScope != null)
                {
                    var status = await TrackingStore.StatusAsync(trackRead!, trackingScope.Engine, trackingScope.SchemaName);
                    if (status.AsDiagnostic(trackingScope.SchemaName) is { } notReady) throw new GateRefusedException(notReady);
                }
                var snap = await TargetSnapshotReader.ReadAsync(read, trackRead, trackingScope, engine, planned.Select(p => SchemaNameOf(p.Definition.Name)));
                if (trackingScope != null) history = (await HistoryReader.ReadAsync(trackRead!, trackingScope)).Entries;
                var results = new Dictionary<string, ResolverOutcome>();
                foreach (var op in renderedOps.Where(o => (operations != null && operations.TryGetValue(o.Model, out var chosen) ? o.Operation == chosen : o.IsDefault) && o.Resolver != null && snap.Live.ContainsKey(o.Model)))
                {
                    var type = op.Parameters.First(p => p.Source == "resolver").Type;
                    var r = await TargetSnapshotReader.RunResolverAsync(read, op.Resolver!, type);
                    results[PlanInput.ResolverKey(op.Model, op.Operation)] = new ResolverOutcome(r.Value, r.Error);
                }
                // what the target holds in the column a chosen range load works on, so the plan can say whether the range overlaps it
                var bounds = new Dictionary<string, ColumnBounds>();
                foreach (var op in renderedOps.Where(o => (operations != null && operations.TryGetValue(o.Model, out var chosen) ? o.Operation == chosen : o.IsDefault) && snap.Live.ContainsKey(o.Model)))
                {
                    var def = planned.First(p => p.Definition.Name == op.Model).Definition;
                    if (RangeLoads.ColumnOf(def, target, op.Operation) is not { } column) continue;
                    var type = def.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.Type ?? "TIMESTAMP";
                    var (min, max, err) = await TargetSnapshotReader.ColumnBoundsAsync(read, engine, op.Model, column, type);
                    bounds[PlanInput.ResolverKey(op.Model, op.Operation)] = new ColumnBounds(min, max, err);
                }
                // an incremental copy reads each origin from the newest value the destination holds for it (less its lookback); nothing there yet reads everything
                var marks = new Dictionary<string, string?>();
                // the load chosen for a copy decides it: a `full_replace` load (`--op model=full`) reads every origin in full
                bool ReadsAll(ModelDefinition def) => LoadPlan.For(def, target).FirstOrDefault(o => operations != null && operations.TryGetValue(def.Name, out var chosen) ? o.Name == chosen : o.IsDefault)?.ReadsAll == true;
                foreach (var copy in planned.Where(p => p.Definition.IsCopy && p.Definition.Watermark != null && snap.Live.ContainsKey(p.Definition.Name) && !ReadsAll(p.Definition)))
                    foreach (var origin in copy.OriginList)
                    {
                        var def = copy.Definition;
                        var type = def.Columns.First(c => string.Equals(c.Name, def.Watermark!.Column, StringComparison.OrdinalIgnoreCase)).Type;
                        var max = await TargetSnapshotReader.MaxAsync(read, engine, def.Name, def.Watermark!.Column, type, def.Slice?.Column, origin.SliceValue);
                        if (max.Error != null) throw new GateRefusedException(new Diagnostic(DiagnosticCatalog.ResolverResultInvalid, new(def.Name, 0, 0), $"{def.Name}: the newest `{def.Watermark.Column}` in the destination {max.Error}."));
                        marks[$"{def.Name}|{origin.Connection}"] = WatermarkBound(max.Value, type, def.Watermark.Lookback);
                    }
                // the inputs the chosen loads declare: computed now, once, with one instant for the plan, and shown in it
                var chosenOps = renderedOps.Where(o => operations != null && operations.TryGetValue(o.Model, out var chosen) ? o.Operation == chosen : o.IsDefault).ToList();
                var now = RefreshCommand.Now();
                var planInstant = new DateTimeOffset(now.UtcDateTime.Ticks - now.UtcDateTime.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
                var (inputSets, problems) = await InputPlanning.ResolveAsync(ctx, target, planned, chosenOps, read, env, pins ?? [], new InputRunContext(planInstant, ctx.Config.TimezoneOf(target), null, target), CancellationToken.None);
                return (snap, results, bounds, marks, inputSets, problems);
            }).GetAwaiter().GetResult();
            foreach (var problem in inputProblems) error.WriteLine(problem);
            if (inputProblems.Count > 0) return (null, CliApp.ExitUsage);
            planned = planned.Select(p => p.Definition.Watermark == null ? p : p with { Origins = p.OriginList.Select(o => marks.TryGetValue($"{p.Definition.Name}|{o.Connection}", out var bound) ? o with { WatermarkValue = bound } : o).ToList() }).ToList();
        }
        catch (GateRefusedException ex)
        {
            error.Diag(ex.Diagnostic);
            return (null, CliApp.ExitFindings);
        }

        var historyFindings = HistoryConcerns.Check(ctx, planned.Select(p => p.Definition.Name).ToList(), history);
        foreach (var d in historyFindings) error.Diag(d);
        if (historyFindings.Any(d => d.Severity == Severity.Error))
        {
            output.WriteLine("Nothing was planned: a model reads a column whose history is inconsistent (`policy.severity.history_inconsistency: error`). Backfill it or acknowledge it.");
            return (null, CliApp.ExitFindings);
        }

        var loads = renderedOps.GroupBy(o => o.Model).ToDictionary(g => g.Key, g => (IReadOnlyList<RenderedLoad>)g
            .Select(o => new RenderedLoad(o.Operation, o.IsDefault, o.Script, DbDataBuild.State.Hashing.ScriptHash(o.Script), o.Resolver, o.Parameters, o.Watermark)).ToList());
        var input = new PlanInput(target, ctx.Config, planned, snapshot.Live, snapshot.Schemas, snapshot.RecordedShapeHashes, snapshot.LastViewStatementHashes,
            snapshot.LastLoadDefinitionHashes, snapshot.Acknowledged, loads, resolved, operations, backfills, rangeBounds) { Tracked = trackingScope != null, LoadInputs = loadInputs };
        return (new PlanningSession { Root = root, Target = target, Context = ctx, Input = input, Warnings = distinct.Where(d => d.Severity != Severity.Error).ToList() }, CliApp.ExitOk);
    }

    /// <summary>The lower bound of an incremental copy: the newest value the destination holds, less the lookback for a date or a time (rows that arrive late or change). Null (no row yet) reads everything.</summary>
    internal static string? WatermarkBound(string? newest, string type, string? lookback)
    {
        if (newest == null) return null;
        if (lookback == null || LoadDuration.TryParse(lookback) is not { } duration || !duration.FitsColumnType(type)) return newest;
        var at = duration.Before(DateTime.Parse(newest, System.Globalization.CultureInfo.InvariantCulture));
        return type.Trim().Equals("DATE", StringComparison.OrdinalIgnoreCase) ? at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : at.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyDictionary<string, ParameterValue> Merge(IReadOnlyDictionary<string, ParameterValue> a, IReadOnlyDictionary<string, ParameterValue> b) =>
        b.Count == 0 ? a : new Dictionary<string, ParameterValue>(a, StringComparer.Ordinal).Concat(b).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static string SchemaNameOf(string model) => DbDataBuild.Targets.Ddl.DdlGenerator.Split(model).SchemaName;
}
