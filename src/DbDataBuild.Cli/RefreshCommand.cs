using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataBuild.Apply;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection refresh [connection] [plan] [selector ...]`: run the loads of a refresh plan that `project compile` wrote (`rendered/&lt;connection&gt;/refresh/&lt;plan&gt;.yml`, the loads it names in
/// `loads.yml`), or one load named directly (`model.load`, no plan). It reads the compiled files and the committed scripts, not the models (selectors that name models by their settings, `tag:`, `property:`, `path:`, `connection:` or `changed:`, are the one
/// thing that loads the project; the order, the skipping and the selectors `+model` and `kind:` use the dependency graph in `loads.yml`); it asks the connection nothing about its structure unless the chosen check says to (`refresh.check`: none, project, objects, live); it never changes structure, and each run is an event of
/// its own that records the plan, its hash, the selection and the inputs each load ran with.
/// </summary>
internal static class RefreshCommand
{
    private static readonly Regex PlanForm = new("^[a-z][a-z0-9_]*$");
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One load to run: the load plan, the membership it runs as (null when a person named the load directly), and the inputs given by the person running it.</summary>
    internal sealed record RunItem(LoadPlanEntry Entry, RefreshMember? Member, int Position)
    {
        public string Membership => Member?.Name ?? Entry.Load;
        public string Display => Member?.FullName ?? Entry.FullName;
    }

    /// <summary>An input as it will run: its value (null for one a resolver finds when the load runs) and where the value came from.</summary>
    /// <param name="Free">An input no script binds (a value only resolvers use): it is recorded in the event and never bound to the load.</param>
    /// <param name="ResolverHash">The hash of the resolver that found the value, when one did.</param>
    internal sealed record EffectiveInput(string Name, string Type, string Source, string? Value, bool Free = false, string? ResolverHash = null, long DurationMs = 0);

    /// <summary>The frozen instant of a run, to the second. A test sets it to prove the clock a resolver sees is the run's.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>For tests only: told the limits of each run when it starts, so a test can read how many sessions an origin had open at once.</summary>
    internal static Action<SessionLimits>? LimitsObserver { get; set; }

    internal sealed record InputFlag(string? Target, string Name, string Value, string Text);

    public static int Run(CommandSpec spec, string root, string? targetArg, string[] args, string? checkArg, string? onFailArg, bool allowDirty, bool dryRun, string[] inputArgs, bool onlyFailed, int? parallelArg, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var configDiags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, configDiags);
        foreach (var d in configDiags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (configDiags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;
        var connection = CommandTargets.Resolve(config, targetArg, error);
        if (connection == null) return CliApp.ExitUsage;

        var settings = config.LifecycleOf(connection.Name);
        if (checkArg != null && !Enum.TryParse<RefreshCheck>(checkArg, ignoreCase: true, out _)) { error.WriteLine($"--check is none, project, objects or live, not `{checkArg}`."); return CliApp.ExitUsage; }
        if (onFailArg != null && !Enum.TryParse<OnFail>(onFailArg, ignoreCase: true, out _)) { error.WriteLine($"--on-fail is block or warn, not `{onFailArg}`."); return CliApp.ExitUsage; }
        if (parallelArg is < 1 or > 64) { error.WriteLine($"--parallel is a whole number from 1 to 64, not {parallelArg}."); return CliApp.ExitUsage; }
        var flags = new List<InputFlag>();
        foreach (var text in inputArgs)
        {
            var eq = text.IndexOf('=');
            if (eq <= 0 || eq == text.Length - 1) { error.WriteLine($"--input is `[model.]name=value`, not `{text}`."); return CliApp.ExitUsage; }
            var key = text[..eq]; var dot = key.LastIndexOf('.');
            var (target, name) = dot < 0 ? ((string?)null, key) : (key[..dot], key[(dot + 1)..]);
            if (name.Length == 0 || (target != null && target.Length == 0)) { error.WriteLine($"--input is `[model.]name=value`, not `{text}`."); return CliApp.ExitUsage; }
            flags.Add(new(target, name, text[(eq + 1)..], text));
        }

        string Header(string? plan) => $"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}{(dryRun ? " (DRY RUN: nothing will be executed)" : "")}  |  connection: {connection.Name}{(plan != null ? $"  |  plan: {plan}" : "")}";

        // ---- the compiled files: every load, and the plans ----
        var renderedDir = Path.Combine(root, RenderCommand.RenderedDir, connection.Name);
        string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        var loadsPath = Path.Combine(renderedDir, LoadsDocument.FileName);
        var planNames = Directory.Exists(Path.Combine(renderedDir, RefreshPlanDocument.FolderName))
            ? Directory.EnumerateFiles(Path.Combine(renderedDir, RefreshPlanDocument.FolderName), "*.yml").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToList() : [];
        if (!File.Exists(loadsPath))
        {
            output.WriteLine(Header(null));
            error.Diag(new Diagnostic(DiagnosticCatalog.RefreshPlanMissing, new(Relative(loadsPath), 0, 0), $"There is no `{Relative(loadsPath)}`."));
            output.WriteLine("Nothing was executed.");
            output.Next("project compile");
            return CliApp.ExitFindings;
        }
        var parseDiags = new List<Diagnostic>();
        var loadsFile = LoadsDocument.Parse(File.ReadAllText(loadsPath), Relative(loadsPath), parseDiags);
        if (loadsFile == null) { output.WriteLine(Header(null)); foreach (var d in parseDiags) error.Diag(d); output.WriteLine("Nothing was executed."); output.Next("project compile"); return CliApp.ExitFindings; }
        if (loadsFile.Connection != connection.Name)
        {
            output.WriteLine(Header(null));
            error.Diag(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(Relative(loadsPath), 0, 0), $"The loads file is for `{loadsFile.Connection}`, not `{connection.Name}`."));
            return CliApp.ExitFindings;
        }

        // ---- what was asked for: a plan (default `default`), or loads named directly ----
        var rest = args.ToList();
        string? planName = null;
        var direct = new List<LoadPlanEntry>();
        if (rest.Count > 0 && loadsFile.Loads.FirstOrDefault(l => l.FullName == rest[0]) != null)
        {
            foreach (var a in rest)
            {
                if (loadsFile.Loads.FirstOrDefault(l => l.FullName == a) is { } load) { if (!direct.Contains(load)) direct.Add(load); }
                else { error.WriteLine($"`{a}` is not a load of `{connection.Name}` (a load named directly is `model.load`, for example `{loadsFile.Loads[0].FullName}`), and a load named directly takes no plan or selector."); return CliApp.ExitUsage; }
            }
            rest.Clear();
        }
        else if (rest.Count > 0 && PlanForm.IsMatch(rest[0]) && rest[0] != "all")
        {
            planName = rest[0];
            rest.RemoveAt(0);
            if (!planNames.Contains(planName))
            {
                output.WriteLine(Header(null));
                error.Diag(new Diagnostic(DiagnosticCatalog.RefreshPlanUnknown, new($"connection:{connection.Name}", 0, 0), $"`{planName}` is not a refresh plan of `{connection.Name}`. {(planNames.Count == 0 ? "It has none." : "Its plans: " + string.Join(", ", planNames) + ".")}"));
                output.Payload("plans", planNames);
                return CliApp.ExitUsage;
            }
        }
        else if (planNames.Contains(RefreshPlanSettings.DefaultName)) planName = RefreshPlanSettings.DefaultName;
        else
        {
            output.WriteLine(Header(null));
            output.Payload("plans", planNames);
            output.WriteLine(planNames.Count == 0 ? $"`{connection.Name}` has no refresh plans: no model has a load that is in one. Declare them with `refresh:` in a model, then `project compile`." : $"`{connection.Name}` has no plan called `default`. Name one: {string.Join(", ", planNames)}.");
            if (planNames.Count > 0) output.Next(planNames.Select(p => $"connection refresh {connection.Name} {p}").ToArray());
            return planNames.Count == 0 ? CliApp.ExitFindings : CliApp.ExitUsage;
        }

        RefreshPlanFile? plan = null;
        string? planRelative = null;
        string planText = "";
        var items = new List<RunItem>();
        var members = new List<RefreshRunMember>();
        string planHash;
        string selection;
        if (planName != null)
        {
            var planPath = Path.Combine(renderedDir, RefreshPlanDocument.FolderName, planName + ".yml");
            planRelative = Relative(planPath);
            planText = File.ReadAllText(planPath);
            plan = RefreshPlanDocument.Parse(planText, planRelative, parseDiags);
            if (plan == null) { output.WriteLine(Header(planName)); foreach (var d in parseDiags) error.Diag(d); output.WriteLine("Nothing was executed."); output.Next("project compile"); return CliApp.ExitFindings; }
            if (plan.Connection != connection.Name || plan.Name != planName)
            {
                output.WriteLine(Header(planName));
                error.Diag(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(planRelative, 0, 0), $"The refresh plan is for `{plan.Name}` on `{plan.Connection}`, not `{planName}` on `{connection.Name}`."));
                return CliApp.ExitFindings;
            }
            var selected = Select(plan, rest, root, error, loadsFile);
            if (selected == null) return CliApp.ExitUsage;
            planHash = plan.Hash;
            selection = string.Join(' ', rest);
            var position = 0;
            foreach (var m in plan.Members)
            {
                position++;
                var entry = loadsFile.Loads.FirstOrDefault(l => l.Model == m.Model && l.Load == m.Load && l.Hash == m.LoadPlanHash);
                var chosen = selected.Contains(m.FullName);
                if (entry == null)
                {
                    output.WriteLine(Header(planName));
                    error.Diag(new Diagnostic(DiagnosticCatalog.RefreshFileChanged, new(planRelative, 0, 0), $"The refresh plan names the load plan {m.LoadPlanHash[..12]} of `{m.Model}.{m.Load}`, which `{Relative(loadsPath)}` does not have (it was compiled again since)."));
                    output.WriteLine("Nothing was executed.");
                    output.Next("project compile");
                    return CliApp.ExitFindings;
                }
                members.Add(new(position, m.Name, m.Model, m.Load, m.LoadPlanHash, chosen));
                if (chosen) items.Add(new(entry, m, position));
            }
            if (items.Count == 0)
            {
                output.WriteLine(Header(planName));
                if (plan.Members.Count == 0) { output.WriteLine($"The plan `{planName}` has no members{(plan.Excluded.Count > 0 ? $" ({plan.Excluded.Count} left out; `project show refresh {planName}` says why)" : "")}. Nothing to do."); return CliApp.ExitOk; }
                error.WriteLine($"No member of the plan `{planName}` matches {string.Join(", ", rest.Select(m => $"`{m}`"))}. The plan has: {string.Join(", ", plan.Members.Select(m => m.FullName))}.");
                return CliApp.ExitUsage;
            }
        }
        else
        {
            if (loadsFile.Loads.Count == 0) { output.WriteLine(Header(null)); error.WriteLine($"`{connection.Name}` has no load plans."); return CliApp.ExitUsage; }
            var position = 0;
            foreach (var e in direct.OrderBy(d => loadsFile.Loads.ToList().IndexOf(d)))
            {
                position++;
                items.Add(new(e, null, position));
                members.Add(new(position, e.Load, e.Model, e.Load, e.Hash, true));
            }
            planHash = Hashing.Sha256Hex(string.Join("\n", items.Select(i => i.Entry.Hash)));
            selection = string.Join(' ', direct.Select(d => d.FullName));
        }
        // ---- one instant for the whole run: what `now()` and `current_date` mean to every resolver of every load ----
        var started = Now();
        var context = new InputRunContext(new DateTimeOffset(started.UtcDateTime.Ticks - started.UtcDateTime.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero), config.TimezoneOf(connection.Name), planName, connection.Name);
        // ---- the logins and where the records are, which `--only-failed` and the check both read ----
        // ---- the check level: a flag, the plan's own, the connection's, the project's ----
        var check = checkArg != null ? Enum.Parse<RefreshCheck>(checkArg, ignoreCase: true) : plan?.Settings.Check is { } pc && Enum.TryParse<RefreshCheck>(pc, ignoreCase: true, out var planCheck) ? planCheck : settings.Check;
        var onFail = onFailArg != null ? Enum.Parse<OnFail>(onFailArg, ignoreCase: true) : plan?.Settings.OnFail is { } pf && Enum.TryParse<OnFail>(pf, ignoreCase: true, out var planOnFail) ? planOnFail : settings.OnFail;
        output.WriteLine(Header(planName) + $"  |  check: {LifecycleSettings.Name(check)}");
        var (read, readMissing) = LoginSettings.FromEnvironment(connection, Login.Read, env);
        if (readMissing != null) { error.Diag(readMissing); output.WriteLine("Nothing was executed."); return CliApp.ExitFindings; }
        var tracking = config.TrackingOf(connection.Name);
        LoginSettings? trackingRead = null;
        if (tracking.Target is { } tt && tt.Connection != connection.Name)
        {
            var (tr, trm) = LoginSettings.FromEnvironment(tt.Connection, tt.Engine, Login.Read, env);
            if (tr == null) { error.Diag(trm!); output.WriteLine("Nothing was executed."); return CliApp.ExitFindings; }
            trackingRead = tr;
        }
        var scope = tracking.Target is { } ts ? new TrackingScope(ts.Engine, ts.SchemaName, connection.Name) : null;

        // ---- --only-failed: the loads the plan's last run left failed, skipped or not run ----
        if (onlyFailed)
        {
            if (planName == null) { error.WriteLine("--only-failed repeats the last run of a plan: name the plan (`connection refresh <connection> <plan> --only-failed`)."); return CliApp.ExitUsage; }
            if (scope == null) { error.WriteLine($"--only-failed reads the last run of the plan from the tracking tables, and nothing is tracked for `{connection.Name}`."); return CliApp.ExitUsage; }
            (string? EventId, IReadOnlyList<AuditLog.LoadState> Loads) last;
            try
            {
                last = Task.Run(async () =>
                {
                    await using var own = trackingRead == null ? null : await ReadSession.OpenAsync(trackingRead);
                    await using var data = own == null ? await ReadSession.OpenAsync(read!) : null;
                    return await AuditLog.LastPlanRunLoadsAsync(own ?? data!, scope, planName);
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error.WriteLine($"--only-failed could not read the last run of `{planName}` ({ex.GetType().Name}{CliApp.DriverNumber(ex)})."); return CliApp.ExitFindings; }
            if (last.EventId == null) { error.WriteLine($"No run of `{planName}` is recorded on `{connection.Name}`, so there is nothing to repeat. Run the plan first."); return CliApp.ExitUsage; }
            var again = items.Where(i => last.Loads.Any(l => l.Model == i.Entry.Model && l.Membership == i.Membership && LoadStatus.NeedsAnotherRun(l.Status))).ToList();
            output.WriteLine($"--only-failed: the last run of `{planName}` is {last.EventId}; {again.Count} of its {last.Loads.Count} load(s) did not end done.");
            if (again.Count == 0)
            {
                output.Payload("plan", planName);
                output.Payload("last_run", last.EventId);
                output.Payload("loads", Array.Empty<object>());
                output.WriteLine("Nothing to do.");
                return CliApp.ExitOk;
            }
            items = again;
            members = members.Select(m => m with { Selected = items.Any(i => i.Position == m.Position) }).ToList();
            selection = (selection.Length == 0 ? "" : selection + " ") + "--only-failed";
        }

        // ---- the inputs: a flag, the membership, a project parameter, what the connection finds ----
        var inputs = new Dictionary<RunItem, List<EffectiveInput>>();
        if (!ResolveInputs(items, flags, connection.Name, inputs, error)) { output.WriteLine("Nothing was executed."); return CliApp.ExitUsage; }

        output.WriteLine(plan != null
            ? $"Refresh plan {planRelative}: {items.Count} of {plan.Members.Count} member(s){(rest.Count > 0 ? $" ({string.Join(" ", rest)})" : "")}{(plan.Excluded.Count > 0 ? $"; {plan.Excluded.Count} left out: {string.Join(", ", plan.Excluded.Take(3).Select(x => x.Model))}{(plan.Excluded.Count > 3 ? ", …" : "")}" : "")}."
            : $"{items.Count} load(s) named directly (no plan).");

        // ---- the files the loads run, against their hashes ----
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = new List<Diagnostic>();
        string? ReadFile(string file, string hash, string what)
        {
            var path = Path.GetFullPath(Path.Combine(file.StartsWith("hooks/", StringComparison.Ordinal) || !file.Contains('/') ? root : Path.Combine(root, RenderCommand.RenderedDir), file));
            if (!File.Exists(path)) { changed.Add(new Diagnostic(DiagnosticCatalog.RefreshFileChanged, new(file, 0, 0), $"The {what} `{file}`, which the compiled refresh files name, does not exist.")); return null; }
            var text = File.ReadAllText(path);
            if (Hashing.ScriptHash(text) != hash) { changed.Add(new Diagnostic(DiagnosticCatalog.RefreshFileChanged, new(file, 0, 0), $"The {what} `{file}` is not the one `{LoadsDocument.FileName}` was compiled from (its content has another hash).")); return null; }
            return text;
        }
        foreach (var l in items.Select(i => i.Entry).Distinct())
        {
            if (ReadFile(l.File, l.FileHash, "load script") is { } script) scripts[l.File] = script;
            if (l.ResolverFile != null && ReadFile(l.ResolverFile, l.ResolverHash ?? "", "resolver") is { } resolver) scripts[l.ResolverFile] = resolver;
            foreach (var h in l.Before.Concat(l.After))
                if (ReadFile(h.File, h.FileHash, "hook script") is { } hook) scripts[h.File] = hook;
        }
        if (changed.Count > 0) { foreach (var d in changed) error.Diag(d); output.WriteLine("Nothing was executed."); output.Next("project compile"); return CliApp.ExitFindings; }

        // ---- the check ----
        var findings = new List<Diagnostic>();
        var requires = items.Select(i => i.Entry).DistinctBy(e => e.Model).Select(e => (Object: e.Model, e.ShapeHash)).ToList();
        if (RefreshChecks.NeedsTracking(check, connection.Name, scope) is { } untracked) findings.Add(untracked);
        // a check that cannot run (nothing to read it from) stops a blocking refresh before the connection is opened
        if (!(findings.Count > 0 && onFail == OnFail.Block))
            try
            {
                Task.Run(async () =>
                {
                    await using var data = await ReadSession.OpenAsync(read!);
                    await using var own = trackingRead == null ? null : await ReadSession.OpenAsync(trackingRead);
                    var track = scope == null ? null : own ?? data;
                    if (check != RefreshCheck.None && findings.Count == 0) findings.AddRange(await RefreshChecks.RunAsync(check, connection.Name, loadsFile.ProjectHash, requires, connection.Engine, data, track, scope));
                }).GetAwaiter().GetResult();
            }
            catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); output.WriteLine("Nothing was executed."); return CliApp.ExitFindings; }

        // a finding about an object is a failure of the loads of that object (kind `check`) and the failure rules apply; one about the connection as a whole stops a blocking refresh before anything runs
        var objectsOfRun = items.Select(i => i.Entry.Model).ToHashSet(StringComparer.Ordinal);
        var perObject = onFail == OnFail.Block ? findings.Where(f => objectsOfRun.Contains(f.Location.File)).ToList() : [];
        var wholeConnection = onFail == OnFail.Block ? findings.Except(perObject).ToList() : [];
        foreach (var d in findings) error.Diag(d with { SeverityOverride = onFail == OnFail.Warn ? Severity.Warning : null });
        output.Payload("plan", planName);
        output.Payload("plan_hash", planHash);
        output.Payload("selection", selection.Length == 0 ? null : selection);
        output.Payload("check", LifecycleSettings.Name(check));
        output.Payload("on_fail", LifecycleSettings.Name(onFail));
        // how many loads run together: the flag, else the plan's `parallel`, else 1; the destination's `max_parallel` caps it, and a dry run executes nothing so it goes one at a time
        var requestedParallel = parallelArg ?? plan?.Settings.Parallel ?? 1;
        var parallel = dryRun ? 1 : Math.Min(requestedParallel, connection.MaxParallel ?? requestedParallel);
        output.Payload("parallel", parallel);
        // the scheduler orders loads by the graph `loads.yml` carries (compiled with the loads, hashed with them): no project file is read for it. A file compiled before the graph was carried has none, so its loads run one at a time, said aloud
        var loadGraph = LoadGraph.Of(loadsFile);
        var (effective, parallelWarning) = ParallelFor(parallel, loadGraph != null);
        parallel = effective;
        var parallelWarnings = parallelWarning == null ? new List<string>() : [parallelWarning];
        output.Payload("parallel_effective", parallel);
        if (requestedParallel > 1)
            output.WriteLine(parallelWarnings.Count > 0 ? parallelWarnings[0][..parallelWarnings[0].IndexOf(';', StringComparison.Ordinal)]
                : dryRun ? $"parallel {requestedParallel} asked for; a dry run goes one load at a time."
                : parallel < requestedParallel ? $"parallel {requestedParallel} asked for; {parallel} load(s) at a time (`max_parallel: {connection.MaxParallel}` on `{connection.Name}`)."
                : $"parallel {parallel}: up to {parallel} loads at a time, each when the loads it reads are done.");
        output.Payload("loads", items.Select(i => new { model = i.Entry.Model, operation = i.Entry.Load, member = i.Membership, inputs = inputs[i].Select(x => new { name = x.Name, type = x.Type, source = x.Source, value = x.Value }).ToList() }).ToList());
        output.Payload("findings", findings.Select(f => f.Code).ToList());
        if (wholeConnection.Count > 0)
        {
            output.WriteLine($"Nothing was executed: {wholeConnection.Count} finding(s) from the {LifecycleSettings.Name(check)} check about the connection as a whole (every load is skipped: check failed). A deploy brings the connection to the structure the project expects; `--check none` skips the check and `--on-fail warn` runs anyway.");
            output.Payload("outcome", RefreshSummary.Failed);
            output.Next("connection deploy");
            return CliApp.ExitFindings;
        }

        // ---- the loads, with the rules that apply to each ----
        var (commit, dirty) = GitInfo.Read(root);
        var id = PlanDocument.CreateRunId(DateTime.UtcNow);
        var steps = new List<PlanStep>();
        var units = new List<RefreshUnit>();
        var stepsOf = new Dictionary<int, List<PlanStep>>();
        var byUnit = new Dictionary<int, RunItem>();
        var maxWait = plan?.Settings.MaxWait is { } mw && EveryDuration.TryParse(mw) is { } mwSpan ? mwSpan.Span : Timeout.InfiniteTimeSpan;
        foreach (var item in items)
        {
            var own = Skeleton(item, inputs[item], scripts);
            var ids = new List<PlanStep>();
            foreach (var st in own) { var numbered = st with { Id = (steps.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }; steps.Add(numbered); ids.Add(numbered); }
            var maxRuntimeText = item.Entry.MaxRuntime ?? plan?.Settings.MaxRuntime;
            units.Add(new RefreshUnit(item.Position, item.Membership, item.Entry.Model, item.Entry.Load, item.Display, item.Entry.Hash, item.Entry.DefinitionHash, ids.First(x => x.Type == StepType.Load).Id,
                item.Member?.OnFailure ?? plan?.Settings.OnFailure ?? OnFailureModes.Continue, plan?.Settings.OnBusy ?? OnBusyModes.Skip, maxWait,
                maxRuntimeText != null && EveryDuration.TryParse(maxRuntimeText) is { } mr ? mr.Span : null, maxRuntimeText, perObject.FirstOrDefault(f => f.Location.File == item.Entry.Model)));
            stepsOf[item.Position] = ids;
            byUnit[item.Position] = item;
        }
        IReadOnlySet<string>? everyModel = null;
        DependencyGraph? projectGraph = null;
        IReadOnlySet<string> Downstream(string model)
        {
            if (loadGraph != null) return loadGraph.Descendants(model).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // a loads file compiled before the graph was carried: it is read from the project when a load fails or is skipped, and only then. A project that cannot be read leaves nothing to go by, so everything after is taken as reading it
            try { return (projectGraph ??= ProjectContext.Load(root).Graph).Descendants(model).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return everyModel ??= units.Select(u => u.Model).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        }
        var limits = new SessionLimits(config.Connections.Where(c => c.Value.MaxParallel != null).ToDictionary(c => c.Key, c => c.Value.MaxParallel!.Value, StringComparer.Ordinal));
        LimitsObserver?.Invoke(limits);
        var originAccess = new OriginAccess(config, env, limits);
        var execution = new RefreshExecution(units, (unit, session, ct) => PrepareAsync(byUnit[unit.Position], stepsOf[unit.Position], inputs[byUnit[unit.Position]], scripts, context, session, originAccess, dryRun ? output.WriteLine : null, ct), Downstream);

        var run = new RefreshRunInfo(planName, selection, members);
        // the transfers of a copy are not in the plan the engine opens up front: an origin that cannot be reached fails its own load (`failed: origin`), it does not refuse the refresh
        var executable = new Plan(id, connection.Name, commit, dirty, ProductInfo.Version, [], [], steps.Where(s => s.Transfer == null).ToList(), []);
        var originNames = items.SelectMany(i => i.Entry.Copy?.Origins.Select(o => o.Connection) ?? []).Distinct(StringComparer.Ordinal).ToList();
        var audit = settings.Refresh.Audit;
        var exit = ApplyCommand.RunPlan(spec, executable, audit == AuditLevel.Full ? (planText.Length > 0 ? planText : "direct: " + string.Join(", ", items.Select(i => $"{i.Entry.FullName} {i.Entry.Hash}")) + "\n") : "", planRelative ?? Relative(loadsPath), root, dryRun, allowRisky: false, allowDestructive: [], allowDirty,
            new ApplyCommand.ApplyMode(Refresh: true, PlanHash: planHash, RecordShapes: check == RefreshCheck.Live, Header: false, Run: run, Execution: execution, OnlyFailed: onlyFailed, Origins: originNames, Parallel: parallel, Limits: limits, Warnings: parallelWarnings), output, error, env);
        // a dry run resolved the inputs (the resolvers ran, read-only): the values it would have used, and where each came from
        if (dryRun)
            output.Payload("resolved_inputs", items.Select(i => new { model = i.Entry.Model, operation = i.Entry.Load, member = i.Membership, inputs = inputs[i].Select(x => new { name = x.Name, type = x.Type, source = x.Source, value = x.Value, resolver_hash = x.ResolverHash }).ToList() }).ToList());
        return exit;
    }

    /// <summary>
    /// The scheduler orders loads by the graph `loads.yml` carries. With more than one load at a time asked for and no graph in the file (it was compiled by an older version) the run goes one load at a time, in plan order
    /// (every earlier load then counts as read), and the warning says so, so a person who asked for 8 and got 1 is told to compile again.
    /// </summary>
    internal static (int Parallel, string? Warning) ParallelFor(int parallel, bool hasGraph)
    {
        if (parallel <= 1 || hasGraph) return (parallel, null);
        return (1, $"parallel: 1 (the loads file has no dependency graph, as one compiled by an older version has none; loads run in plan order; `project compile` writes it); {parallel} was asked for");
    }

    /// <summary>
    /// The members of a plan the selectors choose, by their `model.member` names. No selector chooses all. `load:name` chooses by the load's name; anything else is the model selector grammar of every command (<see cref="ModelSelection"/>:
    /// names and patterns, files, `+model`, `kind:`, `tag:`, `property:`, `connection:`, `path:`, `exclude:`). When the loads file carries the facts the grammar reads and its dependency graph (entry 171), the grammar runs over them and the
    /// project's files are not read; only `changed:` needs the working tree (and its revision history), and a file compiled before the facts were carried needs the project for every term.
    /// </summary>
    internal static HashSet<string>? Select(RefreshPlanFile plan, List<string> selectors, string root, TextWriter error, LoadsFile? loads = null)
    {
        var all = plan.Members.Select(m => m.FullName).ToHashSet(StringComparer.Ordinal);
        if (selectors.Count == 0) return all;
        var loadTerms = new List<string>(); var notLoad = new List<string>(); var modelTerms = new List<string>();
        foreach (var s in selectors)
        {
            if (s.StartsWith("load:", StringComparison.Ordinal)) loadTerms.Add(s[5..]);
            else if (s.StartsWith("exclude:load:", StringComparison.Ordinal)) notLoad.Add(s[13..]);
            else modelTerms.Add(s);
        }
        HashSet<string>? models = null;                                                    // null: every model
        if (modelTerms.Count > 0)
        {
            var graph = loads == null ? null : LoadGraph.Of(loads);
            List<string>? names;
            if (loads?.Models != null && graph != null && !modelTerms.Any(NeedsTheProject))
            {
                var facts = loads.Models.Select(f => f.ToProjectModel()).ToList();
                var host = new SelectionHost(facts.SelectMany(f => f.Connections).Distinct(StringComparer.Ordinal).ToList()) { Scope = "the models this loads file loads" };
                names = new ModelSelection(facts, graph, host).Select(modelTerms, error);
            }
            else names = ProjectContext.Load(root).SelectModels(modelTerms, error)?.Select(m => m.Name).ToList();
            if (names == null) return null;
            models = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in plan.Members)
            if ((models == null || models.Contains(m.Model)) && (loadTerms.Count == 0 || loadTerms.Contains(m.Load)) && !notLoad.Contains(m.Load)) result.Add(m.FullName);
        return result;
    }

    /// <summary>True for the one kind of term the loads file cannot answer: `changed:` reads the working tree.</summary>
    private static bool NeedsTheProject(string argument) =>
        (argument.StartsWith("exclude:", StringComparison.Ordinal) ? argument["exclude:".Length..] : argument)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(part => ModelSelector.Parse(part).Core.StartsWith("changed:", StringComparison.Ordinal));

    /// <summary>
    /// The value each input of each load runs with, and its source, strongest first: a `--input` flag, the membership's value, the project's parameter, and (found when the load runs) the resolver, then `initial`. An override
    /// replaces the default, so a resolver it replaces is never run. An input nobody gives a value for stops the run: a scheduled refresh has no one to ask.
    /// </summary>
    internal static bool ResolveInputs(List<RunItem> items, List<InputFlag> flags, string connection, Dictionary<RunItem, List<EffectiveInput>> result, TextWriter error)
    {
        var ok = true;
        foreach (var item in items) result[item] = [];
        foreach (var flag in flags)
        {
            var candidates = items.Where(i => i.Entry.Inputs.Any(p => p.Name == flag.Name && p.Provides.Count == 0) && (flag.Target == null || flag.Target == i.Display || flag.Target == i.Entry.Model || flag.Target == i.Entry.FullName)).ToList();
            if (candidates.Count == 0)
            {
                error.WriteLine(flag.Target == null
                    ? $"--input `{flag.Text}`: none of the loads to run has an input called `{flag.Name}`. {Describe(items)}"
                    : $"--input `{flag.Text}`: `{flag.Target}` is not a load to run with an input called `{flag.Name}`. {Describe(items)}");
                ok = false; continue;
            }
            if (candidates.Count > 1)
            {
                error.WriteLine($"--input `{flag.Text}` fits more than one load ({string.Join(", ", candidates.Select(c => c.Display))}): name the load, for example `--input {candidates[0].Display}.{flag.Name}={flag.Value}`.");
                ok = false; continue;
            }
        }
        if (!ok) return false;
        foreach (var item in items)
            foreach (var p in item.Entry.Inputs.Where(p => p.Provides.Count == 0))
            {
                var flag = flags.Where(f => f.Name == p.Name && (f.Target == null || f.Target == item.Display || f.Target == item.Entry.Model || f.Target == item.Entry.FullName)).LastOrDefault();
                var given = item.Member?.Inputs.FirstOrDefault(x => x.Name == p.Name);
                string? value; string source;
                if (flag != null) (value, source) = (flag.Value, "flag");
                else if (given != null) (value, source) = (given.Value, "membership");
                else if (p.Source == RefreshParameter.FromParameter) (value, source) = (p.Value, "parameter");
                else if (p.Source == RefreshParameter.FromDefault) (value, source) = (p.Value, "default");
                else if (p.Source == RefreshParameter.FromResolver) (value, source) = (null, "resolver");
                else if (p.Source is RefreshParameter.Computed or RefreshParameter.Provided) (value, source) = (null, "computed");
                else
                {
                    error.Diag(new Diagnostic(DiagnosticCatalog.RefreshInputMissing, new(item.Entry.File, 0, 0), $"`{item.Display}` needs a value for `{p.Name}` ({p.Type}), which has no default: give it with `--input {item.Entry.FullName}.{p.Name}=<value>`."));
                    ok = false; continue;
                }
                if (flag != null || given != null)
                {
                    if (!RefreshPlanBuilder.InputFits(p.Type, value!)) { error.WriteLine($"The value `{value}` of the input `{p.Name}` of `{item.Display}` is not a {p.Type}."); ok = false; continue; }
                }
                result[item].Add(new(p.Name, p.Type, source, value, p.Free));
            }
        return ok;

        static string Describe(List<RunItem> items) => items.SelectMany(i => i.Entry.Inputs.Where(p => p.Provides.Count == 0).Select(p => $"{i.Display}.{p.Name}")).ToList() is { Count: > 0 } all ? $"The inputs of the loads to run: {string.Join(", ", all)}." : "None of them takes an input.";
    }

    /// <summary>The steps of one load as far as they can be known before the run: the hooks before it, the load (a value that a resolver finds is still empty), the hooks after it.</summary>
    private static List<PlanStep> Skeleton(RunItem item, List<EffectiveInput> inputs, Dictionary<string, string> scripts)
    {
        var l = item.Entry;
        var parameters = inputs.Where(p => !p.Free).Select(p => new PlanParameter(p.Name, p.Type, p.Source, p.Value)).ToList();
        var suffix = item.Membership != l.Load ? $" as {item.Membership}" : "";
        PlanStep Hook(RefreshHook h) => new("", StepType.Hook, l.Model, $"hook {h.Name} ({h.Event})", scripts[h.File], RiskClass.Safe, ["hook.fired", $"event {h.Event}"], null, [], Operation: h.Event, FileHash: h.FileHash, Hook: h.Name, Effect: h.Effect);
        var load = new PlanStep("", StepType.Load, l.Model, $"load {l.Model} ({l.Load}{suffix})", scripts[l.File], RiskClass.Safe, ["load.routine"], null, parameters, l.ResolverFile == null ? null : scripts[l.ResolverFile], null, HasResolver: l.ResolverFile != null,
            FileHash: l.FileHash, Operation: l.Load, DefinitionHash: l.DefinitionHash);
        if (l.Copy is not { } copy) return l.Before.Select(Hook).Append(load).Concat(l.After.Select(Hook)).ToList();
        // a copy, as a deploy plans it: for each origin the rows are staged on the destination and then loaded from there (the hooks around each load), and the staging table is dropped at the end
        var all = new List<PlanStep>();
        foreach (var origin in copy.Origins)
        {
            var slice = origin.SliceColumn != null ? new PlanSlice(origin.SliceColumn, origin.SliceValue ?? "", origin.SliceAdded) : null;
            var spec = new TransferSpec(origin.Connection, origin.ReadText, copy.Staging, copy.Columns, slice);
            all.Add(new PlanStep("", StepType.Transfer, l.Model, $"copy {l.Model} from {origin.Connection} ({origin.Table})", copy.CreateStaging, RiskClass.Safe, [slice == null ? "copy.transfer" : "copy.transfer.slice"], null, [], Transfer: spec));
            all.AddRange(l.Before.Select(Hook));
            all.Add(load);
            all.AddRange(l.After.Select(Hook));
        }
        all.Add(new PlanStep("", StepType.Ddl, copy.Staging, $"drop staging table of {l.Model}", copy.DropStaging, RiskClass.Safe, ["copy.staging.drop"], null, []));
        return all;
    }

    /// <summary>
    /// A load made ready just before its turn: its computed inputs are resolved now (an expression in the tool, a `target` resolver on the connection), in dependency order, with the run's frozen instant, so a load sees what the
    /// loads before it wrote; a person's `--input` or a membership's value replaces the resolver, which is not run. Then the watermark is read from the connection. An input that cannot be resolved fails the load (`failed: input`) with a
    /// message that names the input and the resolver; one that finds NULL goes to its `on_null`. A copy is also checked against its origins first (`failed: origin`), reads the bound of an incremental copy from the destination, and
    /// resolves its inputs once for each origin (a `source` resolver runs at that origin).
    /// </summary>
    private static async Task<PreparedLoad> PrepareAsync(RunItem item, List<PlanStep> skeleton, List<EffectiveInput> inputs, Dictionary<string, string> scripts, InputRunContext context, ReadSession read, OriginAccess origins, Action<string>? report, CancellationToken ct)
    {
        var l = item.Entry;
        if (l.Copy is not { } copy)
        {
            var one = await ResolveLoadInputsAsync(item, l.Inputs, inputs, scripts, context, new SessionInputRunner(read), read, report, "", ct);
            if (one.Problem != null) return new PreparedLoad(skeleton, InputsJson([(null, inputs)], context, one.UsedClock), one.Problem, one.ProblemStatus);
            var steps = skeleton.Select(s => s.Type == StepType.Load ? s with { Parameters = one.Parameters!, ResolverResult = one.ResolverResult } : s).ToList();
            return new PreparedLoad(steps, InputsJson([(null, inputs)], context, one.UsedClock));
        }

        // an origin is checked (reachable, read login, SELECT 1) just before its own transfer, by the engine, so an origin that is down fails this load as `failed: origin` while the origins before it stay loaded

        // an incremental copy reads each origin from the newest value the destination holds for it, less the lookback; nothing there yet reads everything
        var bounds = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (copy.Watermark is { } w)
            foreach (var o in copy.Origins)
            {
                var newest = await TargetSnapshotReader.MaxAsync(read, read.Engine, l.Model, w.Column, w.Type, o.SliceColumn, o.SliceValue, ct);
                if (newest.Error != null) return new PreparedLoad(skeleton, InputsJson([(null, inputs)], context, false), $"the newest `{w.Column}` in the destination {newest.Error}");
                bounds[o.Connection] = PlanningSession.WatermarkBound(newest.Value, w.Type, w.Lookback);
            }

        var sets = new List<(string? Origin, List<EffectiveInput> Inputs)>();
        var outcomes = new List<LoadInputOutcome>();
        var usedClock = false;
        foreach (var o in copy.Origins)
        {
            var mine = inputs.ToList();
            await using var originRunner = new OriginInputRunner(origins.OpenAsync, o.Connection);
            var r = await ResolveLoadInputsAsync(item, o.Inputs.Count > 0 ? o.Inputs : l.Inputs, mine, scripts, context, new SourceOrTargetRunner(new SessionInputRunner(read), originRunner), read, report, copy.Origins.Count > 1 ? $"[{o.Connection}] " : "", ct);
            usedClock |= r.UsedClock;
            sets.Add((copy.Origins.Count > 1 ? o.Connection : null, mine));
            if (r.Problem != null) return new PreparedLoad(skeleton, InputsJson(sets, context, usedClock), copy.Origins.Count > 1 ? $"origin `{o.Connection}`: {r.Problem}" : r.Problem, r.ProblemStatus);
            outcomes.Add(r);
        }
        inputs.Clear(); inputs.AddRange(sets[0].Inputs);                                    // what a dry run and the document show

        var prepared = new List<PlanStep>();
        var at = -1;
        foreach (var s in skeleton)
        {
            if (s.Type == StepType.Transfer)
            {
                at++;
                var o = copy.Origins[at];
                var spec = s.Transfer!;
                if (copy.Watermark is { } mark && bounds.GetValueOrDefault(o.Connection) is { } bound && o.ReadWithWatermark != null)
                    spec = spec with { ReadText = o.ReadWithWatermark, Watermark = new PlanWatermark(mark.Column, mark.Type, bound), ParameterOrder = o.ParameterOrder };
                prepared.Add(s with { Transfer = spec });
            }
            else if (s.Type == StepType.Load) prepared.Add(s with { Parameters = outcomes[at].Parameters!, ResolverResult = outcomes[at].ResolverResult });
            else prepared.Add(s);
        }
        return new PreparedLoad(prepared, InputsJson(sets, context, usedClock));
    }

    /// <summary>What resolving the inputs of a load (for one origin, when it is a copy) came to: the values to bind, or why the load cannot run.</summary>
    private sealed record LoadInputOutcome(List<PlanParameter>? Parameters, string? ResolverResult, string? Problem, string ProblemStatus, bool UsedClock);

    private static async Task<LoadInputOutcome> ResolveLoadInputsAsync(RunItem item, IReadOnlyList<RefreshParameter> defs, List<EffectiveInput> inputs, Dictionary<string, string> scripts, InputRunContext context, IInputQueryRunner runner, ReadSession read, Action<string>? report, string prefix, CancellationToken ct)
    {
        var l = item.Entry;
        var usedClock = false;
        if (defs.Any(d => d.Source is RefreshParameter.Computed or RefreshParameter.Provided))
        {
            var given = inputs.Where(x => x.Source is "flag" or "membership").ToDictionary(x => x.Name, x => (x.Value!, x.Source), StringComparer.Ordinal);
            var resolution = await InputResolution.ResolveAsync(item.Display, defs.Where(d => d.Source != RefreshParameter.FromResolver).ToList(), given, context, runner,
                r => r.Text, ct);
            usedClock = resolution.UsedClock;
            foreach (var r in resolution.Inputs)
                if (inputs.FindIndex(x => x.Name == r.Name) is var at and >= 0 && inputs[at].Source == "computed")
                    inputs[at] = inputs[at] with { Source = r.Source, Value = r.Value, ResolverHash = r.ResolverHash, DurationMs = r.DurationMs };
            if (resolution.Failure != null) return new(null, null, resolution.Failure, LoadStatus.FailedInput, usedClock);
            if (resolution.SkipLoad) return new(null, null, "an input found NULL and its `on_null` is `skip_load`, so the load did not run", LoadStatus.SkippedNoValue, usedClock);
        }
        var parameters = new List<PlanParameter>();
        string? resolverResult = null;
        for (var i = 0; i < inputs.Count; i++)
        {
            var p = inputs[i];
            if (p.Free) continue;
            if (p.Source != "resolver") { parameters.Add(new(p.Name, p.Type, p.Source, p.Value)); continue; }
            var found = await TargetSnapshotReader.RunResolverAsync(read, scripts[l.ResolverFile!], p.Type, ct);
            if (found.Error != null)
                return new(null, null, $"{DiagnosticCatalog.ResolverResultInvalid.Code}: the resolver for `@{p.Name}` {found.Error}. A table that was never deployed has no watermark.", LoadStatus.FailedInput, usedClock);
            resolverResult = found.Value;
            var value = found.Value;
            var source = "resolver:target";
            if (value == null)
            {
                if (l.OnNull == WatermarkSpec.InitialLiteral && l.Initial != null) (value, source) = (l.Initial, "initial");
                else return new(null, null, $"{DiagnosticCatalog.ResolverResultInvalid.Code}: the resolver for `@{p.Name}` returned NULL and the model says `on_null: require_param`, so a person gives the value: `--input {l.FullName}.{p.Name}=<value>`.", LoadStatus.FailedInput, usedClock);
            }
            parameters.Add(new(p.Name, p.Type, source, value));
            inputs[i] = p with { Source = source, Value = value };                         // what the event records
        }
        // a dry run says which values the load would run with, and where each came from (the resolvers were run, read-only, to know)
        if (report != null)
            foreach (var x in inputs)
                report($"    {prefix}input {x.Name} = {x.Value ?? "NULL"}  [{x.Source}]{(x.ResolverHash != null ? $"  resolver {x.ResolverHash[..12]}" : "")}{(x.Free ? "  (no script binds it)" : "")}");
        return new(parameters, resolverResult, null, LoadStatus.FailedInput, usedClock);
    }

    /// <summary>The inputs as the event records them: each with where its value came from, the hash of the resolver that found it and how long that took (and, for a copy of several origins, the origin it was resolved for); and, when an expression read the clock, the run's instant.</summary>
    private static string InputsJson(IEnumerable<(string? Origin, List<EffectiveInput> Inputs)> sets, InputRunContext context, bool usedClock)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var (origin, inputs) in sets)
            foreach (var x in inputs)
            {
                var row = new Dictionary<string, object?> { ["name"] = x.Name, ["type"] = x.Type, ["source"] = x.Source, ["value"] = x.Value };
                if (origin != null) row["origin"] = origin;
                if (x.ResolverHash != null) { row["resolver_hash"] = x.ResolverHash; row["duration_ms"] = x.DurationMs; }
                rows.Add(row);
            }
        if (usedClock) rows.Add(new() { ["name"] = "run_started", ["type"] = "TIMESTAMP", ["source"] = "run", ["value"] = context.RunStartedText });
        return JsonSerializer.Serialize(rows, Json);
    }
}

/// <summary>
/// How a refresh reaches the origins of its copies: each by its connection name, on its read login from the environment (`DBDATABUILD_&lt;ORIGIN&gt;_READ`), through <see cref="ReadSession"/> only. A failure is described by its
/// type and error number, never by the driver's message.
/// </summary>
internal sealed record OriginAccess(ProjectConfig Config, Func<string, string?> Env, SessionLimits? Limits = null, SessionOpener? Open = null)
{
    public async Task<ReadSession> OpenAsync(string name, CancellationToken ct)
    {
        if (!Config.Connections.TryGetValue(name, out var connection)) throw new InvalidOperationException($"no connection {name}");
        var (login, missing) = LoginSettings.FromEnvironment(connection, Login.Read, Env);
        if (login == null) throw new GateRefusedException(missing!);
        return await (Limits ?? SessionLimits.None).OpenAsync(name, () => (Open ?? ReadSession.OpenAsync)(login, ct), ct);
    }

    /// <summary>Null when the origin answers on its read login; otherwise what is wrong, in words that quote nothing a driver said.</summary>
    public async Task<string?> CheckAsync(string name, CancellationToken ct)
    {
        if (!Config.Connections.ContainsKey(name)) return "it is not a connection of this project.";
        try
        {
            await using var session = await OpenAsync(name, ct);
            await session.QueryFirstRowsAsync(Config.DialectOf(name).Ping, 1, 30, ct);
            // a Flight SQL server whose dialect was committed (`project update`) is asked again: the committed read text is right only for that dialect, and it is never changed silently (DDB-460)
            if (Config.Connections[name] is { Kind.DialectIsDiscoverable: true, DialectLock: { } committed })
            {
                var facts = await session.ServerFactsAsync(ct) ?? ServerFacts.Nothing;
                var differences = DialectLockComparison.Differences(committed, DialectDiscovery.Choose(facts, Config.Dialects), facts);
                if (differences.Count > 0)
                    return $"the server now reports a different dialect than the one committed in {ProductInfo.DialectLockFile} ({string.Join("; ", differences)}). The committed reads were written for the old one ({DiagnosticCatalog.OriginDialectChanged.Code}); run `{ProductInfo.Cli} project update` and review the change.";
            }
            return null;
        }
        catch (GateRefusedException ex) { return ex.Diagnostic.Found; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return $"it could not be reached ({DbDataBuild.Execution.DriverFailure.Describe(ex)})."; }
    }
}

/// <summary>The safety checks before a refresh (`refresh.check`), from cheapest to strictest. Each compares what the compiled project expects of the objects the refresh uses with something recorded or live.</summary>
internal static class RefreshChecks
{
    /// <summary>The levels that read the tracking tables cannot run when nothing is tracked for the connection.</summary>
    public static Diagnostic? NeedsTracking(RefreshCheck level, string connection, TrackingScope? scope) =>
        level is RefreshCheck.Project or RefreshCheck.Objects && scope == null
            ? new Diagnostic(DiagnosticCatalog.RefreshCheckFailed, new($"connection:{connection}", 0, 0), $"The `{LifecycleSettings.Name(level)}` check reads the tracking tables, and nothing is tracked for `{connection}`. Use `--check live` (the catalog) or `--check none`.")
            : null;

    /// <param name="expected">The objects the loads to run use, each with the shape the compiled project expects of it.</param>
    public static async Task<IReadOnlyList<Diagnostic>> RunAsync(RefreshCheck level, string connection, string projectHash, IReadOnlyList<(string Object, string ShapeHash)> expected, string engine, ReadSession data, ReadSession? tracking, TrackingScope? scope)
    {
        var findings = new List<Diagnostic>();
        Diagnostic Behind(string what, string found) => new(DiagnosticCatalog.RefreshCheckFailed, new(what, 0, 0), found);
        if (level != RefreshCheck.Live && (tracking == null || scope == null))
        {
            findings.Add(Behind($"connection:{connection}", $"The `{LifecycleSettings.Name(level)}` check reads the tracking tables, and nothing is tracked for `{connection}`. Use `--check live` (the catalog) or `--check none`."));
            return findings;
        }
        if (scope != null && tracking != null)
        {
            var status = await TrackingStore.StatusAsync(tracking, scope.Engine, scope.SchemaName);
            if (status.AsDiagnostic(scope.SchemaName) is { } notReady) return [notReady];
        }
        switch (level)
        {
            case RefreshCheck.Project:
            {
                var (deployed, eventId) = await AuditLog.LastDeployedProjectAsync(tracking!, scope!);
                if (deployed == null) findings.Add(Behind($"connection:{connection}", "No completed deploy of this connection recorded the project's structure, so it cannot be said that this project was deployed here."));
                else if (deployed != projectHash) findings.Add(Behind($"connection:{connection}", $"The project expects structure {projectHash[..12]}, but the last completed deploy ({eventId}) recorded {deployed[..12]}."));
                break;
            }
            case RefreshCheck.Objects:
            {
                var recorded = await TrackingStore.LatestShapeHashesAsync(tracking!, scope!);
                foreach (var r in expected)
                    if (!recorded.TryGetValue(r.Object, out var have)) findings.Add(Behind(r.Object, $"`{r.Object}` has never been deployed by the tool (no recorded shape)."));
                    else if (have != r.ShapeHash) findings.Add(Behind(r.Object, $"`{r.Object}` was last deployed with shape {have[..12]}, but the project expects {r.ShapeHash[..12]}."));
                break;
            }
            case RefreshCheck.Live:
            {
                var schemas = expected.Select(r => DbDataBuild.Targets.Ddl.DdlGenerator.Split(r.Object).SchemaName);
                var snapshot = await TargetSnapshotReader.ReadAsync(data, tracking, scope, engine, schemas);
                foreach (var r in expected)
                {
                    var live = snapshot.Live.GetValueOrDefault(r.Object);
                    if (live == null) findings.Add(Behind(r.Object, $"`{r.Object}` does not exist on the connection."));
                    else if (scope != null && Drift.Classify(live, snapshot.RecordedShapeHashes.GetValueOrDefault(r.Object)) == ObjectState.OutOfBand)
                        findings.Add(Behind(r.Object, $"`{r.Object}` was changed outside the tool: its shape is {live.ShapeHash[..12]}, the last one recorded is {snapshot.RecordedShapeHashes[r.Object][..12]}, and the project expects {r.ShapeHash[..12]}."));
                    else if (live.ShapeHash != r.ShapeHash) findings.Add(Behind(r.Object, $"`{r.Object}` has shape {live.ShapeHash[..12]} on the connection, but the project expects {r.ShapeHash[..12]}."));
                }
                break;
            }
        }
        return findings;
    }
}
