using DbDataBuild.State;
using DbDataBuild.Apply;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection deploy --apply-plan &lt;plan&gt;` (DESIGN.md 10.3). Effect class: target writes, exactly what the plan states. It refuses a plan that was edited, a stale plan, a plan that needs
/// an allowance it was not given, and a dirty working tree. `--dry-run` runs the same checks and the same code path and executes nothing.
/// </summary>
internal static class ApplyCommand
{
    /// <summary>`metadata.store_on_apply`: after a successful apply, store the project, the touched models and the plan as JSON documents. A failure here never turns a good apply into a bad one.</summary>
    private static void StoreMetadata(Plan plan, string root, TrackingScope scope, LoginSettings read, LoginSettings write, string? commit, TextWriter output, TextWriter error)
    {
        try
        {
            var ctx = ProjectContext.Load(root);
            if (ctx.Diagnostics.Any(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code))
            {
                output.WriteLine("note: metadata was not stored: the project has errors now (`project compile` shows them).");
                return;
            }
            var touched = plan.Steps.Select(s => s.Object).Distinct(StringComparer.Ordinal).ToList();
            var prepared = MetadataPublisher.Prepare(ctx, touched, plan);
            var stored = Task.Run(() => MetadataPublisher.PublishAsync(prepared.Documents, scope, read, write, "apply-metadata", root, plan.Id, commit, prepared.Dependencies)).GetAwaiter().GetResult();
            output.WriteLine($"Metadata stored: {stored.Written.Count} document(s) written, {stored.Unchanged.Count} unchanged.");
            output.Payload("metadata_stored", stored.Written.Select(d => new { subject_kind = d.SubjectKind, subject = d.Subject, hash = d.Hash }).ToList());
        }
        catch (Exception ex) when (ex is GateRefusedException or IOException or InvalidOperationException)
        {
            output.WriteLine($"note: the plan was applied, but its metadata was not stored ({ex.GetType().Name}).");
        }
    }

    /// <summary>After a successful apply: the definitions of the routines `track_definition` lists, for the native models the plan's models use. A failure here never turns a good apply into a bad one.</summary>
    private static void RecordDefinitions(Plan plan, string root, TrackingScope scope, LoginSettings read, LoginSettings write, Func<string, string?> env, string? commit, TextWriter output)
    {
        try
        {
            var ctx = ProjectContext.Load(root);
            var names = plan.Steps.Select(s => s.Object).ToHashSet(StringComparer.Ordinal);
            var uses = NativeDefinitions.InPlay(ctx, ctx.Project.BuiltModels.Where(s => names.Contains(s.Definition.Name)).Select(s => (s, s.ReadQuery(ctx.Root, ctx.Config))), plan.Connection);
            if (uses.Count == 0) return;
            Task.Run(() => NativeDefinitions.RecordAsync(ctx, uses, scope, read, write, env, "apply-definitions", root, plan.Id, commit)).GetAwaiter().GetResult();
            output.WriteLine($"Routine definitions recorded for {uses.Count} native model use(s).");
        }
        catch (Exception ex) when (ex is GateRefusedException or IOException or InvalidOperationException)
        {
            output.WriteLine($"note: the plan was applied, but the routine definitions were not recorded ({ex.GetType().Name}).");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex StoredPlanId = new(@"^\d{4}-\d{2}-\d{2}-[0-9a-f]{8}$");

    /// <summary>A plan given by id rather than by file: the text kept in the tracking tables (`plans.deploy.keep: database`), or null after saying why there is none.</summary>
    private static string? ReadStored(CommandSpec spec, string root, string? connectionArg, string id, TextWriter error, Func<string, string?> env)
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, diags);
        foreach (var d in diags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (diags.Any(d => d.Severity == Severity.Error)) return null;
        var connection = CommandTargets.Resolve(config, connectionArg, error);
        if (connection == null) return null;
        var tracking = CommandTracking.Require(config, connection, env, needWrite: false, error, spec.Name);
        if (tracking == null) return null;
        try
        {
            var text = Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(tracking.Read);
                if ((await TrackingStore.StatusAsync(read, tracking.Scope.Engine, tracking.Scope.SchemaName)).AsDiagnostic(tracking.Scope.SchemaName) is { } notReady) throw new GateRefusedException(notReady);
                return await AuditLog.ReadStoredPlanAsync(read, tracking.Scope, id);
            }).GetAwaiter().GetResult();
            if (text == null) error.WriteLine($"No plan `{id}` is kept in the tracking tables for the connection `{connection.Name}`, and there is no file of that name.");
            return text;
        }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return null; }
    }

    /// <summary>The computed inputs that moved since the plan, one line each, with the value the plan was made with and the value now.</summary>
    internal static void WriteMoved(IReadOnlyList<MovedInput> moved, TextWriter output)
    {
        output.WriteLine($"{moved.Count} computed input(s) are not what the plan was made with:");
        foreach (var m in moved) output.WriteLine($"  {m.Model}.{m.Operation}  {m.Name}: planned {m.Planned ?? "NULL"}, now {m.Now ?? "NULL"}");
    }

    public static int Run(CommandSpec spec, string planPath, string root, bool dryRun, bool allowRisky, string[] allowDestructive, bool allowDirty,
        TextWriter output, TextWriter error, Func<string, string?> env, string? connectionArg = null, bool afterPlan = false, IReadOnlyList<InputPin>? pins = null, Func<IReadOnlyList<MovedInput>, bool>? approveMoved = null)
    {
        string planText, label;
        var fromDatabase = !File.Exists(planPath) && StoredPlanId.IsMatch(Path.GetFileName(planPath));
        if (fromDatabase)
        {
            var stored = ReadStored(spec, root, connectionArg, Path.GetFileName(planPath), error, env);
            if (stored == null) return CliApp.ExitFindings;
            (planText, label) = (stored, Path.GetFileName(planPath));
        }
        else
        {
            if (!File.Exists(planPath)) { error.WriteLine($"Plan file `{planPath}` does not exist."); return CliApp.ExitUsage; }
            (planText, label) = (File.ReadAllText(planPath), Path.GetRelativePath(root, planPath).Replace('\\', '/'));
        }
        var diags = new List<Diagnostic>();
        var plan = PlanDocument.Parse(planText, label, diags);
        if (plan == null)
        {
            foreach (var d in diags) error.Diag(d);
            return CliApp.ExitFindings;
        }
        var exit = RunPlan(spec, plan, planText, label, root, dryRun, allowRisky, allowDestructive, allowDirty, new ApplyMode(Header: !afterPlan, Summary: !afterPlan, InputPins: pins, ApproveMoved: approveMoved), output, error, env);
        // a plan kept only for the moment (`plans.deploy.keep: ephemeral` puts it under .dbdatabuild/plans/) goes when it has been applied
        if (exit == CliApp.ExitOk && !dryRun && !fromDatabase && Path.GetFullPath(planPath).StartsWith(Path.GetFullPath(Path.Combine(root, PlanCommand.EphemeralPlansDir)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            foreach (var file in new[] { planPath, planPath.Replace(".plan.yml", ".plan.md", StringComparison.Ordinal) })
                try { File.Delete(file); } catch (IOException) { /* left; it is under .dbdatabuild */ }
            output.WriteLine("The plan was ephemeral and has been removed; it stays in the tracking tables as far as the audit level keeps it.");
        }
        return exit;
    }

    /// <summary>
    /// How a plan is run: a deploy (the default) verifies the plan against the live objects, records shapes, and continues what it started; a refresh is a plan built in memory from the compiled refresh plan,
    /// which is run as it is, under its own id, and records the hash of the compiled plan.
    /// </summary>
    /// <param name="PlanHash">What to record as the plan's hash instead of the hash of the plan as built (a refresh records the compiled plan's).</param>
    /// <param name="RecordShapes">Whether each load records the shape of its object before and after.</param>
    /// <param name="Header">Whether to print the command header (not when the command printed one a moment ago).</param>
    /// <param name="Summary">Whether to print the line that names the plan and the objects it may touch (not after the command printed its own summary).</param>
    /// <param name="Run">For a refresh: the plan it ran, the selection and the plan's members, which the event records.</param>
    /// <param name="Execution">For a refresh: its loads with the rules of each and how to make each ready (see <see cref="RefreshExecution"/>).</param>
    /// <param name="OnlyFailed">Whether the loads were chosen by `--only-failed` (the hints that follow say so).</param>
    internal sealed record ApplyMode(bool Refresh = false, string? PlanHash = null, bool RecordShapes = true, bool Header = true, bool Summary = true, RefreshRunInfo? Run = null, RefreshExecution? Execution = null, bool OnlyFailed = false, IReadOnlyList<InputPin>? InputPins = null, Func<IReadOnlyList<MovedInput>, bool>? ApproveMoved = null, IReadOnlyList<string>? Origins = null, int Parallel = 1, SessionLimits? Limits = null, IReadOnlyList<string>? Warnings = null);

    /// <summary>` hourly` when the refresh ran a plan, so the next commands name it; nothing for a load named directly.</summary>
    private static string PlanArgument(ApplyMode mode) => mode.Run?.PlanName is { } name ? " " + name : "";

    internal static int RunPlan(CommandSpec spec, Plan plan, string planText, string planLabel, string root, bool dryRun, bool allowRisky, string[] allowDestructive, bool allowDirty, ApplyMode mode,
        TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var configDiags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, configDiags);
        foreach (var d in configDiags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (configDiags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;

        if (!config.Connections.TryGetValue(plan.Connection, out var connection))
        {
            error.WriteLine($"The plan is for the connection `{plan.Connection}`, which this project does not have. Connections: {string.Join(", ", config.Connections.Keys.Order(StringComparer.Ordinal))}.");
            return CliApp.ExitFindings;
        }
        var (read, readMissing) = LoginSettings.FromEnvironment(connection, Login.Read, env);
        var (write, writeMissing) = LoginSettings.FromEnvironment(connection, Login.Write, env);
        // a plan with a copy reads rows from other connections: each needs its read login, and is opened by name when the transfer runs
        var originLogins = new Dictionary<string, LoginSettings>(StringComparer.Ordinal);
        var originMissing = new List<Diagnostic>();
        foreach (var origin in plan.Steps.Where(s => s.Transfer != null).Select(s => s.Transfer!.Origin).Distinct(StringComparer.Ordinal))
        {
            if (!config.Connections.TryGetValue(origin, out var originConnection)) { originMissing.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new($"connection:{origin}", 0, 0), $"The plan reads rows from the connection `{origin}`, which this project does not have.")); continue; }
            var (originLogin, _) = LoginSettings.FromEnvironment(originConnection, Login.Read, env);
            // a login that is missing is not a refusal here: that origin is checked when its own transfer's turn comes, and the others are not held back by it
            if (originLogin != null) originLogins[origin] = originLogin;
        }
        // a refresh opens the origins of its copies by name when a copy runs; a login that is missing is not a refusal here: that copy fails with `failed: origin` and the failure rules apply
        foreach (var origin in mode.Origins ?? [])
            if (config.Connections.TryGetValue(origin, out var refreshOrigin) && LoginSettings.FromEnvironment(refreshOrigin, Login.Read, env).Item1 is { } refreshLogin) originLogins[origin] = refreshLogin;
        // where the records go: this connection, another one (central tracking), or nowhere (untracked, with a warning unless that was chosen)
        var tracking = config.TrackingOf(plan.Connection);
        LoginSettings? trackRead = null, trackWrite = null;
        var trackMissing = new List<Diagnostic>();
        if (tracking.Target is { } trackTarget)
        {
            if (trackTarget.Connection == connection.Name) { trackRead = read; trackWrite = write; }
            else
            {
                var (tr, trm) = LoginSettings.FromEnvironment(trackTarget.Connection, trackTarget.Engine, Login.Read, env);
                var (tw, twm) = LoginSettings.FromEnvironment(trackTarget.Connection, trackTarget.Engine, Login.Write, env);
                (trackRead, trackWrite) = (tr, tw);
                if (trm != null) trackMissing.Add(trm);
                if (!dryRun && twm != null) trackMissing.Add(twm);
            }
        }
        else if (!tracking.Explicit)
            error.Diag(new Diagnostic(DiagnosticCatalog.TrackingNotConfigured, new($"connection:{plan.Connection}", 0, 0), $"Nothing is tracked for `{plan.Connection}`: this apply records nothing (no drift baseline, no history, no resume)."));
        // a plan that runs a native command on a connection that does not allow them (the configuration changed since the plan) is refused, whatever the plan says
        foreach (var step in plan.Steps.Where(s => s.Transfer is { Command: true }))
            if (config.Connections.TryGetValue(step.Transfer!.Origin, out var host) && !host.AllowNativeCommands)
                originMissing.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new($"step:{step.Id}", 0, 0), $"Step {step.Id} runs a native command on `{step.Transfer.Origin}`, which does not allow native commands (`connections.{step.Transfer.Origin}.allow_native_commands`)."));
        var logins = dryRun ? $"read {read?.Describe() ?? "none"}; nothing is written" : $"read {read?.Describe() ?? "none"}, write {write?.Describe() ?? "none"}";
        if (mode.Header) output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}{(dryRun ? " (DRY RUN: nothing will be executed)" : "")}  |  connection: {plan.Connection}  |  login: {logins}");
        output.Payload("effect", spec.Effect.Describe());
        output.Payload("connection", plan.Connection);
        output.Payload("dry_run", dryRun);
        output.Payload("plan_id", plan.Id);
        if (mode.Refresh && mode.Run != null) output.Payload("refresh_plan", mode.Run.PlanName);
        if (mode.Summary) output.WriteLine($"Plan {plan.Id}: {plan.Steps.Count} step(s); objects that may be touched: {string.Join(", ", plan.Steps.Select(s => s.Object).Distinct(StringComparer.Ordinal))}");

        var (commit, dirty) = GitInfo.Read(root);
        // run on its own (not inside the terminal interface, which has its own stop), the first Ctrl-C asks the apply to stop after the step that is running, and the second ends the process: a statement that has started is
        // not abandoned by one keypress
        Func<bool>? stopRequested = CommandContext.Hooks?.StopRequested;
        ConsoleCancelEventHandler? onInterrupt = null;
        if (stopRequested == null && !dryRun)
        {
            var asked = false;
            onInterrupt = (_, e) =>
            {
                if (asked) return;                                  // the second one is the default: the process ends
                asked = true; e.Cancel = true;
                output.WriteLine("Interrupt received: the apply stops after the step that is running (press Ctrl-C again to end it now; running the same plan again continues it).");
            };
            Console.CancelKeyPress += onInterrupt;
            stopRequested = () => asked;
        }
        // what an event records follows the project's audit level for its lane: the event and its steps (minimal); who and the commit (standard); the text of the plan and of each statement (full)
        var lifecycle = config.LifecycleOf(plan.Connection);
        var audit = (mode.Refresh ? lifecycle.Refresh : lifecycle.Deploy).Audit;
        if (audit != AuditLevel.Full) planText = "";
        // each origin of a copy is checked just before its own transfer: it answers on its read login and, in a deploy, its table still has what the copy declares (DDB-230; `on_mismatch` says fail or leave it out)
        Func<PlanStep, CancellationToken, Task<OriginVerdict>>? checkOrigin = null;
        if (plan.Steps.Any(st => st.Transfer != null) || mode.Origins is { Count: > 0 })
        {
            var access = new OriginAccess(config, env, mode.Limits);
            ProjectContext? structureContext = null;
            checkOrigin = async (step, token) =>
            {
                var originName = step.Transfer!.Origin;
                if (await access.CheckAsync(originName, token) is { } why) return new OriginVerdict(OriginVerdictKind.Fail, why);
                if (mode.Refresh) return OriginVerdict.Fine;            // a refresh asks no connection about structure: the transfer's own check of the columns it reads is the net
                structureContext ??= ProjectContext.Load(root);
                var copyModel = structureContext.Project.Models.FirstOrDefault(m => m.IsCopy && m.Name == step.Object);
                var copyOrigin = copyModel == null ? null : structureContext.OriginsOf(copyModel, plan.Connection).FirstOrDefault(x => x.Connection == originName);
                if (copyModel == null || copyOrigin == null) return OriginVerdict.Fine;
                var found = await CopyOriginCheck.RunOneAsync(structureContext, copyModel, copyOrigin, env);
                foreach (var finding in found.Findings.Where(f => f.Severity != Severity.Error)) error.Diag(finding);
                if (found.Skipped) return new OriginVerdict(OriginVerdictKind.Skip, $"the origin `{originName}` differs from its declaration and the copy says `on_mismatch: skip`: it is left out of this apply");
                if (found.Findings.FirstOrDefault(f => f.Severity == Severity.Error) is { } differs) return new OriginVerdict(OriginVerdictKind.Fail, differs.Found, differs);
                return OriginVerdict.Fine;
            };
        }
        var options = new ApplyOptions(dryRun, allowRisky, allowDestructive.ToHashSet(StringComparer.Ordinal), audit == AuditLevel.Minimal ? null : commit, dirty, audit == AuditLevel.Minimal ? "" : write?.User ?? Environment.UserName, stopRequested,
            (name, token) => originLogins.TryGetValue(name, out var login) ? (mode.Limits ?? SessionLimits.None).OpenAsync(name, () => ReadSession.OpenAsync(login, token), token) : throw new InvalidOperationException($"no read login for connection {name}"),
            Lane: mode.Refresh ? "refresh" : "deploy", ProjectHash: mode.Refresh ? null : plan.ProjectHash, PlanHash: mode.PlanHash, Refresh: mode.Refresh, RecordShapes: mode.RecordShapes, RecordText: audit == AuditLevel.Full, Run: mode.Run, Execution: mode.Execution,
            InputPins: mode.InputPins, ApproveMoved: mode.ApproveMoved, Timezone: config.TimezoneOf(plan.Connection), Clock: RefreshCommand.Now, Parallel: mode.Parallel, CheckOrigin: checkOrigin);

        // refusals that need no connection come first
        var offline = new List<Diagnostic>(ApplyEngine.CheckAllowances(plan, options));
        if (dirty && !allowDirty && config.LifecycleOf(plan.Connection).RequireCleanTree)
        {
            var dirtyDiag = new Diagnostic(DiagnosticCatalog.DirtyWorkingTree, new("git", 0, 0), $"The working tree at `{root}` has uncommitted changes (commit {commit?[..Math.Min(12, commit.Length)]}).");
            if (dryRun) error.Diag(dirtyDiag with { SeverityOverride = Severity.Warning }); else offline.Add(dirtyDiag);
        }
        if (readMissing != null) offline.Add(readMissing);
        offline.AddRange(trackMissing);
        if (!dryRun) offline.AddRange(originMissing);
        if (!dryRun && writeMissing != null) offline.Add(writeMissing);
        if (offline.Count > 0)
        {
            foreach (var d in offline) error.Diag(d);
            output.WriteLine("Nothing was executed.");
            return CliApp.ExitFindings;
        }

        var runId = Guid.NewGuid();
        var hooks = CommandContext.Hooks;       // captured here: the apply runs on another thread
        ApplyResult result;
        string? logPath = null;
        try
        {
            if (!dryRun && StatementLogRetention.Prune(Path.Combine(root, InitCommand.StatementLogDir), lifecycle.StatementLogDays, DateTime.UtcNow) is > 0 and var pruned)
                output.WriteLine($"Removed {pruned} statement log(s) older than {lifecycle.StatementLogDays} day(s) (retention.statement_logs_days).");
            using var log = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), dryRun ? "apply-dry-run" : "apply", runId);
            logPath = Path.GetRelativePath(root, log.Path);
            output.WriteLine($"Statement log: {logPath}");
            result = Task.Run(() => ApplyEngine.RunAsync(plan, planText, read!, write, tracking.Target is { } tt ? new ApplyTracking(trackRead!, trackWrite, tt.SchemaName) : null, options, log, runId, line => { output.WriteLine(line); hooks?.Progress?.Invoke(line); })).GetAwaiter().GetResult();
        }
        finally { if (onInterrupt != null) Console.CancelKeyPress -= onInterrupt; }

        if (dryRun)
            foreach (var step in result.PreparedSteps ?? plan.Steps)
            {
                output.WriteLine();
                output.WriteLine($"-- step {step.Id} [{step.Type.ToString().ToLowerInvariant()}, {step.Risk.ToString().ToLowerInvariant()}] {step.Description}");
                if (step.Type != StepType.Track) output.WriteLine(step.Text.TrimEnd());
                foreach (var p in step.Parameters) output.WriteLine($"-- @{p.Name} ({p.Type}) = {p.Value ?? "NULL"}");
            }
        if (!mode.Refresh && result.Success && !dryRun && tracking.Target is { } definitionTarget) RecordDefinitions(plan, root, new TrackingScope(definitionTarget.Engine, definitionTarget.SchemaName, plan.Connection), trackRead!, trackWrite!, env, commit, output);
        if (!mode.Refresh && result.Success && !dryRun && config.StoreMetadataOnApply)
        {
            if (tracking.Target is { } storeTarget) StoreMetadata(plan, root, new TrackingScope(storeTarget.Engine, storeTarget.SchemaName, plan.Connection), trackRead!, trackWrite!, commit, output, error);
            else output.WriteLine("note: metadata was not stored: nothing is tracked for this connection.");
        }
        output.Payload("outcomes", result.Outcomes.Select(o => new { step = o.StepId, description = o.Description, status = o.Status, detail = o.Detail }).ToList());
        if (result.Refresh is { } refreshed)
        {
            output.Payload("outcome", refreshed.Outcome);
            output.Payload("results", refreshed.Loads.Select(l => new { model = l.Unit.Model, member = l.Unit.Membership, load = l.Unit.Load, state = l.State, reason = l.Reason, status = l.Status, rows = l.Rows, duration_ms = l.DurationMs, detail = l.Detail, note = l.Note }).ToList());
            output.Payload("warnings", (mode.Warnings ?? []).Concat(refreshed.Warnings).ToList());
        }
        if (result.Moved is { Count: > 0 } movedInputs)
        {
            output.Payload("moved_inputs", movedInputs.Select(m => new { model = m.Model, operation = m.Operation, name = m.Name, planned = m.Planned, now = m.Now, @override = m.Override }).ToList());
            WriteMoved(movedInputs, output);
            output.WriteLine("Nothing was executed. To continue with the new values, run the same command again with these added (an interactive apply asks instead; `--yes` does not approve them):");
            output.WriteLine("  " + string.Join(" ", movedInputs.Select(m => m.Override)));
        }
        output.Payload("statement_log", logPath?.Replace('\\', '/'));
        output.Payload("success", result.Success);
        if (dryRun) output.Payload("statements", (result.PreparedSteps ?? plan.Steps).Where(s => s.Type != StepType.Track).Select(s => new { step = s.Id, type = s.Type, risk = s.Risk, text = s.Text, parameters = s.Parameters }).ToList());
        foreach (var d in result.Refusals) error.Diag(d);
        output.WriteLine();
        foreach (var o in result.Outcomes) output.WriteLine($"  step {o.StepId}: {o.Status}{(o.Detail != null && o.Status != "ok" ? " (" + o.Detail + ")" : "")}  {o.Description}");
        var planRelative = planLabel;
        if (result.Refresh is { } summary && !dryRun)
        {
            // one line that says how it ended and names what did not: failed loads, and the loads that were skipped or not run
            var named = mode.Run?.PlanName is { } planNamed ? $" (plan {planNamed})" : "";
            var counts = $"{summary.Loads.Count(l => l.State == "done")} done, {summary.Loads.Count(l => l.State == "failed")} failed, {summary.Loads.Count(l => l.State == "skipped")} skipped, {summary.Loads.Count(l => l.State == "not run")} not run";
            output.WriteLine($"Refresh {plan.Id}{named}: {summary.Outcome.Replace('_', ' ')}. {counts}.");
            foreach (var group in summary.Loads.Where(l => l.State != "done").GroupBy(l => l.State))
                output.WriteLine($"  {group.Key}: {string.Join("; ", group.Select(l => $"{l.Unit.Display}{(l.Reason != null ? $" ({l.Reason})" : "")}"))}");
            foreach (var note in summary.Loads.Where(l => l.Note != null)) output.WriteLine($"  {note.Unit.Display} ran: {note.Note}");
            foreach (var w in (mode.Warnings ?? []).Concat(summary.Warnings)) output.WriteLine($"  warning: {w}");
            if (summary.Loads.Any(l => l.Status == LoadStatus.FailedCheck))
                output.Next("connection deploy", $"connection refresh {plan.Connection}{PlanArgument(mode)} --only-failed");
            else if (summary.Outcome != RefreshSummary.Completed)
                output.Next($"connection refresh {plan.Connection}{PlanArgument(mode)} --only-failed", $"connection monitor {plan.Connection}{PlanArgument(mode)}");
            else if (summary.Loads.Any(l => l.Status == LoadStatus.SkippedBusy)) output.Next($"connection refresh {plan.Connection}{PlanArgument(mode)} --only-failed", $"connection monitor {plan.Connection}{PlanArgument(mode)}");
            else output.Next($"connection monitor {plan.Connection}{PlanArgument(mode)}");
            return result.Success ? CliApp.ExitOk : CliApp.ExitFindings;
        }
        if (result.Success)
        {
            output.WriteLine(dryRun ? "Dry run complete: every check passed and nothing was executed." : mode.Refresh ? $"Refresh {plan.Id}{(mode.Run?.PlanName is { } named ? $" (plan {named})" : "")}: {result.Outcomes.Count(o => o.Status == "ok")} step(s) ran." : $"Applied plan {plan.Id}: {result.Outcomes.Count(o => o.Status == "ok")} step(s) executed.");
            if (dryRun) output.Next(mode.Refresh ? $"connection refresh {plan.Connection}{PlanArgument(mode)}" : $"connection deploy --apply-plan {planRelative}");
            else output.Next($"connection monitor{(mode.Refresh ? $" {plan.Connection}{PlanArgument(mode)}" : "")}");
        }
        else
        {
            output.WriteLine($"{(mode.Refresh ? "Refresh" : "Plan")} {plan.Id}{(mode.Run?.PlanName is { } failedPlan ? $" (plan {failedPlan})" : "")} did not complete. {result.Outcomes.Count(o => o.Status == "ok")} step(s) ran before the stop; see the statement log {logPath}.");
            // a plan that stopped part-way continues when it is applied again; a refused one has to be planned again
            if (result.Outcomes.Count > 0) output.Next(mode.Refresh ? $"connection monitor {plan.Connection}{PlanArgument(mode)}" : $"connection deploy --apply-plan {planRelative}", mode.Refresh ? $"connection refresh {plan.Connection}{PlanArgument(mode)}" : "connection monitor");
        }
        // a deploy that cannot get the lock of the whole connection (a refresh holds a load, or another deploy runs) is busy, not failed
        if (!mode.Refresh && result.Refusals.Any(d => d.Code == DiagnosticCatalog.ApplyLockHeld.Code)) return CliApp.ExitBusy;
        return result.Success ? CliApp.ExitOk : CliApp.ExitFindings;
    }
}
