using System.Data;
using System.Globalization;
using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Apply;

/// <summary>Where the records of an apply are kept: the logins of the tracking connection (which may be the connection being applied to, or another) and the schema name of its tracking tables there.</summary>
public sealed record ApplyTracking(LoginSettings Read, LoginSettings? Write, string SchemaName);

/// <param name="OpenOrigin">Opens the read session of the connection a `transfer` step reads from (by name). A plan with a transfer step cannot be applied for real without it.</param>
/// <param name="AllowDestructive">Object names (`marts.fct`) whose destructive steps are allowed. Never "all".</param>
/// <param name="Lane">`deploy` or `refresh`: recorded on the event.</param>
/// <param name="ProjectHash">The structure the compiled project expected of the connection, recorded on a deploy event (so a refresh can ask whether this project was deployed).</param>
/// <param name="PlanHash">The hash to record for the plan, when it is not the hash of the plan as the engine sees it (a refresh records the compiled plan's).</param>
/// <param name="Refresh">A refresh runs routine loads only and asks the connection nothing about its structure: no plan is verified against the live objects, there is nothing to continue, and the id of every run is its own.</param>
/// <param name="Run">For a refresh: the plan it ran (null for a load named directly), the selection, the plan's members as run, and what each step belongs to; recorded on the event and on each load row.</param>
/// <param name="RecordText">Whether the text of each DDL statement is kept in `ddl_log` (audit `full`); the hash always is.</param>
/// <param name="Execution">For a refresh: its loads in order with the rules of each, and how to make each ready just before its turn (the lock, the failure rules and the resolvers act per load).</param>
/// <param name="RecordShapes">Whether each load records the shape of its object before and after (a catalog read per load). A deploy does; a refresh does only when it checks against the live catalog.</param>
/// <param name="InputPins">`--input` values given when the plan is applied: they replace the value in the plan and the resolver (which is not run); the plan's value is recorded beside them.</param>
/// <param name="ApproveMoved">Asked when a computed input is not what the plan was made with: true continues with the new values (an interactive deployer said yes). Without it the apply stops (DDB-454).</param>
/// <param name="Timezone">The zone the clock of an expression resolver means when inputs are computed again.</param>
/// <param name="Parallel">For a refresh: how many loads may run at once (1 is one after another). Each runs on sessions of its own.</param>
/// <param name="CheckOrigin">Asked just before each transfer step runs (never in a dry run): is this origin reachable, with its read login, and (a deploy) does its table still have what the copy declares? Each origin is checked at its own turn, not all before the first statement.</param>
public sealed record ApplyOptions(bool DryRun, bool AllowRisky, IReadOnlySet<string> AllowDestructive, string? GitCommit, bool GitDirty, string Invoker, Func<bool>? StopRequested = null, Func<string, CancellationToken, Task<ReadSession>>? OpenOrigin = null,
    string Lane = "deploy", string? ProjectHash = null, string? PlanHash = null, bool Refresh = false, bool RecordShapes = true, bool RecordText = true, RefreshRunInfo? Run = null, RefreshExecution? Execution = null,
    IReadOnlyList<InputPin>? InputPins = null, Func<IReadOnlyList<MovedInput>, bool>? ApproveMoved = null, string Timezone = "UTC", Func<DateTimeOffset>? Clock = null, int Parallel = 1, Func<PlanStep, CancellationToken, Task<OriginVerdict>>? CheckOrigin = null);

/// <summary>What the check of an origin found: it is fine, the origin is to be left out (`on_mismatch: skip`: its transfer and the load of its rows do not run) or the run stops (a refresh: the load fails as `failed: origin`).</summary>
public enum OriginVerdictKind { Ok, Skip, Fail }

/// <param name="Detail">Said to the person: what is wrong with the origin, in words that quote nothing a driver said.</param>
/// <param name="Finding">The diagnostic to report when the verdict is a failure that has a code of its own (DDB-230).</param>
public sealed record OriginVerdict(OriginVerdictKind Kind, string? Detail = null, Diagnostic? Finding = null)
{
    public static readonly OriginVerdict Fine = new(OriginVerdictKind.Ok);
}

/// <param name="Status">ok, dry-run, skipped (done in an earlier attempt), stopped (the operator stopped before it) or failed.</param>
public sealed record StepOutcome(string StepId, string Description, string Status, string? Detail = null);

/// <param name="Refresh">For a refresh: how each load ended and how the run ended. Its steps are then one outcome per load.</param>
/// <param name="PreparedSteps">For a refresh: the steps as they ran, with every input resolved (a dry run prints these).</param>
public sealed record ApplyResult(IReadOnlyList<StepOutcome> Outcomes, IReadOnlyList<Diagnostic> Refusals, RefreshSummary? Refresh = null, IReadOnlyList<PlanStep>? PreparedSteps = null, IReadOnlyList<MovedInput>? Moved = null)
{
    public bool Success => Refusals.Count == 0 && (Refresh != null ? Refresh.Outcome == RefreshSummary.Completed : Outcomes.All(o => o.Status != "failed"));
}

/// <summary>
/// `apply` (DESIGN.md 10.3). Verifies the plan against the live engine, takes the application lock, and executes exactly the plan's recorded statements through
/// the mutation gate, one step at a time, recording each in the tracking tables. Anything unexpected stops the run. Running a plan that stopped part-way continues it; running a completed one is refused.
/// </summary>
public static class ApplyEngine
{
    /// <summary>The refusals that need no connection: risk allowances (DESIGN.md 10.4).</summary>
    public static IReadOnlyList<Diagnostic> CheckAllowances(Plan plan, ApplyOptions o)
    {
        var refusals = new List<Diagnostic>();
        var risky = plan.Steps.Where(s => s.Risk == RiskClass.Risky).ToList();
        if (risky.Count > 0 && !o.AllowRisky)
            refusals.Add(new Diagnostic(DiagnosticCatalog.StepNeedsAllowance, new($"plan:{plan.Id}", 0, 0),
                $"The plan has {risky.Count} risky step(s) ({string.Join(", ", risky.Select(s => $"{s.Id}: {s.Description}"))}) and `--allow-risky` was not given."));
        foreach (var obj in plan.Steps.Where(s => s.Risk == RiskClass.Destructive).Select(s => s.Object).Distinct(StringComparer.Ordinal))
            if (!o.AllowDestructive.Contains(obj))
                refusals.Add(new Diagnostic(DiagnosticCatalog.StepNeedsAllowance, new($"plan:{plan.Id}", 0, 0),
                    $"The plan has destructive step(s) on `{obj}` ({string.Join(", ", plan.Steps.Where(s => s.Risk == RiskClass.Destructive && s.Object == obj).Select(s => $"{s.Id}: {s.Description}"))}) and `--allow-destructive {obj}` was not given."));
        return refusals;
    }

    public static async Task<ApplyResult> RunAsync(Plan plan, string planText, LoginSettings read, LoginSettings? write, ApplyTracking? tracking, ApplyOptions o, IStatementLog log, Guid runId, Action<string> progress, CancellationToken ct = default)
    {
        var engine = read.Engine;
        var refusals = new List<Diagnostic>(CheckAllowances(plan, o));
        if (refusals.Count > 0) return new ApplyResult([], refusals);
        if (!o.DryRun && write == null) throw new ArgumentException("A real apply needs the write login.", nameof(write));
        var planHash = o.PlanHash ?? PlanDocument.ContentHash(plan);


        await using var reader = await ReadSession.OpenAsync(read, ct);
        // the tracking connection: the data connection itself (its session and gate are shared), or another one with its own read and write sessions
        var sameConnection = tracking != null && string.Equals(tracking.Read.Connection, read.Connection, StringComparison.Ordinal);
        await using var ownTrackingReader = tracking != null && !sameConnection ? await ReadSession.OpenAsync(tracking.Read, ct) : null;
        var trackReader = tracking == null ? null : sameConnection ? reader : ownTrackingReader;
        TrackingScope? scope = tracking == null ? null : new TrackingScope(tracking.Read.Engine, tracking.SchemaName, read.Connection);
        if (tracking != null && scope != null)
        {
            var status = await TrackingStore.StatusAsync(trackReader!, scope.Engine, scope.SchemaName, ct);
            if (status.AsDiagnostic(scope.SchemaName) is { } notReady) return new ApplyResult([], [notReady]);
        }

        // an origin is checked when its own transfer's turn comes (ApplyOptions.CheckOrigin), not all before the first statement: one unreachable system must not refuse the origins that are fine

        await using var gate = o.DryRun
            ? MutationGate.DryRunGate("apply", StatementKind.Tracking | StatementKind.Data | StatementKind.Ddl, log, runId)
            : await MutationGate.OpenAsync(write!, "apply", StatementKind.Tracking | StatementKind.Data | StatementKind.Ddl, log, runId, ct);

        await using var ownTrackingGate = tracking != null && !sameConnection
            ? o.DryRun ? MutationGate.DryRunGate("apply", StatementKind.Tracking, log, runId) : await MutationGate.OpenAsync(tracking.Write!, "apply", StatementKind.Tracking, log, runId, ct)
            : null;
        var tracker = tracking == null ? Tracker.None : Tracker.For(sameConnection ? gate : ownTrackingGate!, scope!);

        var lockName = $"{ProductInfo.Cli}:{tracking?.SchemaName ?? ProductInfo.TrackingSchemaName}";
        // a refresh locks per load, not the connection: a deploy takes the whole connection (below) and conflicts with any load that is running
        if (o.Refresh && o.Execution != null)
        {
            // loads that run together each get a read session, a write session and (when the records go to another connection) a tracking session of their own, all on the same statement log and ordinal
            Func<CancellationToken, Task<RefreshWorker>>? openWorker = o.DryRun || o.Parallel <= 1 ? null : async token =>
            {
                var owned = new List<IAsyncDisposable>();
                try
                {
                    var workerReader = await ReadSession.OpenAsync(read, token); owned.Add(workerReader);
                    var workerGate = await gate.OpenSiblingAsync(write!, ct: token); owned.Add(workerGate);
                    var workerTracker = Tracker.None;
                    if (tracking != null)
                    {
                        var trackGate = workerGate;
                        if (!sameConnection) { trackGate = await ownTrackingGate!.OpenSiblingAsync(tracking.Write!, ct: token); owned.Add(trackGate); }
                        workerTracker = Tracker.For(trackGate, scope!);
                    }
                    return new RefreshWorker(workerReader, workerGate, workerTracker, owned);
                }
                catch { foreach (var d in owned) { try { await d.DisposeAsync(); } catch (Exception) { /* closing */ } } throw; }
            };
            return await RefreshEngine.RunAsync(plan, planText, planHash, reader, tracker, gate, openWorker, o, runId, lockName, progress, ct);
        }
        if (!await gate.TryAcquireApplicationLockAsync(lockName, ct))
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.ApplyLockHeld, new($"lock:{lockName}", 0, 0), $"Another apply holds the application lock `{lockName}` on this target.")]);
        try
        {
            return await RunLockedAsync(plan, planText, planHash, reader, trackReader, scope, tracker, gate, o, runId, progress, ct);
        }
        finally
        {
            try { await gate.ReleaseApplicationLockAsync(lockName, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { /* closing the connection releases it */ }
        }
    }

    private static async Task<ApplyResult> RunLockedAsync(Plan plan, string planText, string planHash, ReadSession reader, ReadSession? trackReader, TrackingScope? scope, Tracker tracker, MutationGate gate, ApplyOptions o, Guid runId, Action<string> progress, CancellationToken ct)
    {
        var engine = reader.Engine;
        var objects = plan.Steps.Select(s => s.Object).Concat(plan.Bases.Select(b => b.Object)).Distinct(StringComparer.Ordinal).ToList();
        var schemas = objects.Select(x => DdlGenerator.Split(x).SchemaName).Distinct(StringComparer.Ordinal).ToList();

        // ---- what this plan already did (resume) ----
        var progressInfo = scope == null || o.Refresh ? new PlanProgress([], new HashSet<string>(), new HashSet<string>(), []) : await AuditLog.ProgressAsync(trackReader!, scope, plan.Id, ct);
        var refusals = new List<Diagnostic>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        if (progressInfo.MigrationStatuses.Contains("completed"))
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.PlanAlreadyStarted, new($"plan:{plan.Id}", 0, 0), $"Plan {plan.Id} was already applied to this target. A plan is applied once.")]);
        // a plan that was started and did not finish is continued by running it again: the steps that finished are skipped (after the check below)

        foreach (var s in plan.Steps)
            if (IsDone(s, progressInfo)) done.Add(s.Id);
        var firstTodo = plan.Steps.ToList().FindIndex(s => !done.Contains(s.Id));
        if (firstTodo >= 0 && plan.Steps.Skip(firstTodo).Any(s => done.Contains(s.Id)))
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.PlanAlreadyStarted, new($"plan:{plan.Id}", 0, 0), "The steps that finished are not a prefix of the plan, so it cannot be resumed. Generate a new plan.")]);

        // ---- verify the plan against the live target (a mismatch is a stale plan, never guessed around) ----
        // a refresh asks the connection nothing about its structure: what it needs was checked (or deliberately not) before it started
        var snapshot = o.Refresh ? null! : await TargetSnapshotReader.ReadAsync(reader, trackReader, scope, engine, schemas, ct);
        var lastDone = plan.Steps.Where(s => done.Contains(s.Id) && s.Type == StepType.Ddl && s.HashAfter != null).GroupBy(s => s.Object).ToDictionary(g => g.Key, g => g.Last());
        foreach (var b in o.Refresh ? [] : plan.Bases)
        {
            var live = snapshot.Live.GetValueOrDefault(b.Object);
            if (lastDone.TryGetValue(b.Object, out var step))
            {
                if (live?.ShapeHash != step.HashAfter)
                    refusals.Add(Stale($"{b.Object} is not in the state step {step.Id} left it in (expected {step.HashAfter![..12]}, found {live?.ShapeHash[..12] ?? "missing"})."));
                continue;
            }
            if (done.Count > 0 && plan.Steps.Any(s => done.Contains(s.Id) && s.Object == b.Object && s.Type == StepType.Track)) continue; // the track step already recorded it
            var recorded = snapshot.RecordedShapeHashes.GetValueOrDefault(b.Object);
            var state = scope == null ? (live == null ? ObjectState.Missing : ObjectState.InSync) : Drift.Classify(live, recorded);
            if (state != b.State || live?.ShapeHash != b.LiveShapeHash || recorded != b.RecordedShapeHash)
                refusals.Add(Stale($"{b.Object} changed since the plan was made (planned: {b.State} {b.LiveShapeHash?[..12] ?? "-"}, now: {state} {live?.ShapeHash[..12] ?? "-"})."));
        }

        // ---- the computed inputs, computed again: any that is not what the plan was made with stops the apply unless the deployer approves the new value ----
        var steps = plan.Steps;
        IReadOnlyList<MovedInput> moved = [];
        if (!o.Refresh && refusals.Count == 0)
            (steps, moved) = await InputsAtApplyAsync(plan, done, snapshot, reader, o, refusals, ct);
        if (refusals.Count > 0) return new ApplyResult([], refusals);
        if (moved.Count > 0 && o.ApproveMoved?.Invoke(moved) != true)
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.InputsMoved, new("plan", 0, 0),
                $"{moved.Count} computed input(s) are not what the plan was made with: {string.Join("; ", moved.Select(m => $"{m.Model}.{m.Operation} `{m.Name}` was {m.Planned ?? "NULL"} and is now {m.Now ?? "NULL"}"))}.")], Moved: moved);

        // ---- execute ----
        var outcomes = new List<StepOutcome>();
        var before = string.Join(",", plan.Bases.Select(b => b.LiveShapeHash ?? "-"));
        if (!gate.DryRun)
        {
            await tracker.MigrationAsync("migration:start", plan.Id, planHash, planText, o.GitCommit, o.Invoker, "started", Hashing.Sha256Hex(before), null, o.Lane, o.ProjectHash, o.Run?.PlanName, SelectionOf(o), ct);
            foreach (var m in o.Run?.Members ?? [])
                await tracker.PlanMemberAsync($"member:{m.Position}", plan.Id, m.Position, m.Membership, m.Model, m.Load, m.LoadPlanHash, m.Selected, ct);
        }

        string? leftOut = null;                                  // the model whose current origin was left out: the hooks and the load of that origin's rows do not run
        foreach (var step in steps)
        {
            if (done.Contains(step.Id)) { outcomes.Add(new(step.Id, step.Description, "skipped", "finished in an earlier attempt")); continue; }
            if (leftOut != null)
            {
                if (step.Object == leftOut && step.Type is StepType.Hook or StepType.Load or StepType.Backfill) { outcomes.Add(new(step.Id, step.Description, "skipped", "its origin was left out")); continue; }
                leftOut = null;
            }
            // an operator's request to stop is honoured between steps only: a statement that has started is never abandoned
            if (o.StopRequested?.Invoke() == true)
            {
                outcomes.Add(new(step.Id, step.Description, "stopped", "stopped by the operator before it started"));
                await FinishMigration(tracker, gate, plan, planHash, planText, o, "failed", ct);
                return new ApplyResult(outcomes, [new Diagnostic(DiagnosticCatalog.ApplyStopped, new($"step:{step.Id}", 0, 0), $"Apply was stopped before step {step.Id} ({step.Description}). The steps before it finished; it and the later steps did not run.")]);
            }
            progress($"step {step.Id}/{plan.Steps.Count}: {step.Description}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (step.Type == StepType.Transfer && !gate.DryRun && o.CheckOrigin != null)
                {
                    var verdict = await o.CheckOrigin(step, ct);
                    if (verdict.Kind == OriginVerdictKind.Skip)
                    {
                        outcomes.Add(new(step.Id, step.Description, "skipped", verdict.Detail));
                        leftOut = step.Object;
                        continue;
                    }
                    if (verdict.Kind == OriginVerdictKind.Fail)
                    {
                        var found = verdict.Finding ?? new Diagnostic(DiagnosticCatalog.StepFailed, new($"step:{step.Id}", 0, 0), $"step {step.Id} ({step.Description}): origin `{step.Transfer!.Origin}`: {verdict.Detail} Later steps did not run.");
                        outcomes.Add(new(step.Id, step.Description, "failed", verdict.Detail));
                        await FinishMigration(tracker, gate, plan, planHash, planText, o, "failed", ct);
                        return new ApplyResult(outcomes, [found]);
                    }
                }
                var diagnostic = await ExecuteStepAsync(plan, step, reader, gate, tracker, o, runId, null, null, null, ct);
                if (diagnostic != null)
                {
                    outcomes.Add(new(step.Id, step.Description, "failed", diagnostic.Found));
                    await FinishMigration(tracker, gate, plan, planHash, planText, o, "failed", ct);
                    return new ApplyResult(outcomes, [diagnostic]);
                }
                outcomes.Add(new(step.Id, step.Description, gate.DryRun ? "dry-run" : "ok"));
                progress($"  step {step.Id} done in {clock.Elapsed.TotalSeconds:0.0}s");
            }
            catch (GateRefusedException ex)
            {
                outcomes.Add(new(step.Id, step.Description, "failed", ex.Diagnostic.Found));
                await FinishMigration(tracker, gate, plan, planHash, planText, o, "failed", ct);
                return new ApplyResult(outcomes, [ex.Diagnostic]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // type only: a driver message can quote data (DESIGN.md 14.2); the statement log has the error number
                var detail = $"step {step.Id} ({step.Description}) failed with {ex.GetType().Name}";
                outcomes.Add(new(step.Id, step.Description, "failed", detail));
                await FinishMigration(tracker, gate, plan, planHash, planText, o, "failed", ct);
                return new ApplyResult(outcomes, [new Diagnostic(DiagnosticCatalog.StepFailed, new($"step:{step.Id}", 0, 0), detail + ". Later steps did not run.")]);
            }
        }
        await FinishMigration(tracker, gate, plan, planHash, planText, o, "completed", ct);
        return new ApplyResult(outcomes, []);
    }

    /// <summary>
    /// Computes the inputs of every load that has still to run again (docs/research/refresh-plans.md, section 8, item 7): the declared ones with their resolvers (an expression on the clock of now, a `target` resolver on the read
    /// session, a `source` resolver at the origin) and the watermark. A value pinned with `--input` is not computed: it replaces the plan's and the resolver is not run. An input whose value is not the plan's is returned as
    /// moved, with the steps carrying the new values (so an approved apply loads what it was shown); a value the plan recorded is kept beside the new one in the load's record.
    /// </summary>
    private static async Task<(IReadOnlyList<PlanStep> Steps, IReadOnlyList<MovedInput> Moved)> InputsAtApplyAsync(Plan plan, HashSet<string> done, TargetSnapshot snapshot, ReadSession reader, ApplyOptions o, List<Diagnostic> refusals, CancellationToken ct)
    {
        var pins = o.InputPins ?? [];
        var moved = new List<MovedInput>();
        var steps = new List<PlanStep>();
        var matched = new HashSet<InputPin>();
        var now = (o.Clock ?? (() => DateTimeOffset.UtcNow))();
        var instant = new DateTimeOffset(now.UtcDateTime.Ticks - now.UtcDateTime.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        foreach (var step in plan.Steps)
        {
            if (done.Contains(step.Id) || step.Type is not (StepType.Load or StepType.Backfill)) { steps.Add(step); continue; }
            var operation = step.Operation ?? "default";
            var own = pins.Where(p => p.Matches(step.Object, operation) && (step.Parameters.Any(x => x.Name == p.Name) || step.Inputs?.Any(i => i.Name == p.Name && i.Def.Provides.Count == 0) == true)).ToList();
            foreach (var p in own) matched.Add(p);
            var parameters = step.Parameters.ToList();
            var inputs = step.Inputs?.ToList();
            void Set(string name, string? value, string source, string? planned)
            {
                var at = parameters.FindIndex(x => x.Name == name);
                if (at >= 0) parameters[at] = parameters[at] with { Value = value, Source = source, Planned = planned != value ? planned : null };
            }

            // the declared inputs: all of them computed again, in dependency order
            if (inputs is { Count: > 0 })
            {
                var given = inputs.Where(i => i.Source == "flag" && i.Value != null).ToDictionary(i => i.Name, i => (i.Value!, "flag"), StringComparer.Ordinal);
                foreach (var p in own.Where(p => inputs.Any(i => i.Name == p.Name))) given[p.Name] = (p.Value, "flag");
                var origin = inputs.Select(i => i.Origin).FirstOrDefault(x => x != null);
                await using var originRunner = origin == null || o.OpenOrigin == null ? null : new OriginInputRunner(o.OpenOrigin, origin);
                IInputQueryRunner runner = originRunner == null ? new SessionInputRunner(reader) : new SourceOrTargetRunner(new SessionInputRunner(reader), originRunner);
                var load = $"{step.Object}.{operation}";
                var res = await InputResolution.ResolveAsync(load, inputs.Select(i => i.Def).ToList(), given, new InputRunContext(instant, o.Timezone, null, plan.Connection), runner, r => r.Text, ct);
                if (res.Failure != null) { refusals.Add(new Diagnostic(DiagnosticCatalog.InputNotResolved, new($"step:{step.Id}", 0, 0), res.Failure[(DiagnosticCatalog.InputNotResolved.Code.Length + 2)..])); steps.Add(step); continue; }
                if (res.SkipLoad) { refusals.Add(new Diagnostic(DiagnosticCatalog.InputsMoved, new($"step:{step.Id}", 0, 0), $"{load}: an input now finds NULL and says `on_null: skip_load`, which the plan did not expect.")); steps.Add(step); continue; }
                for (var k = 0; k < inputs.Count; k++)
                {
                    var planned = inputs[k];
                    if (res.Inputs.FirstOrDefault(r => r.Name == planned.Name) is not { } fresh) continue;
                    var movedNow = planned.IsComputed && !string.Equals(planned.Value, fresh.Value, StringComparison.Ordinal) && !given.ContainsKey(planned.Name);
                    if (movedNow) moved.Add(new MovedInput(step.Id, step.Object, operation, planned.Name, planned.Value, fresh.Value));
                    if (!string.Equals(planned.Value, fresh.Value, StringComparison.Ordinal) || fresh.Source != planned.Source)
                    {
                        inputs[k] = planned with { Source = fresh.Source, Value = fresh.Value, ResolverHash = fresh.ResolverHash ?? planned.ResolverHash };
                        if (!planned.Def.Free) Set(planned.Name, fresh.Value, fresh.Source, planned.Value);
                    }
                }
            }

            // a value given at the command for a parameter the declared inputs do not cover (the `start` of a range load, the watermark)
            foreach (var p in own.Where(p => inputs?.Any(i => i.Name == p.Name) != true))
            {
                var at = parameters.FindIndex(x => x.Name == p.Name);
                if (at < 0) continue;
                if (!InputValues.Fits(parameters[at].Type, p.Value)) { refusals.Add(new Diagnostic(DiagnosticCatalog.InputNotResolved, new($"step:{step.Id}", 0, 0), $"--input `{p.Text}`: `{p.Value}` is not a {parameters[at].Type}.")); continue; }
                Set(p.Name, p.Value, "flag", parameters[at].Value);
            }

            // the watermark: its resolver runs again, unless a value was given for it
            string? resultNow = step.ResolverResult;
            if (step.HasResolver && own.All(p => p.Name != "watermark"))
            {
                if (!snapshot.Live.ContainsKey(step.Object)) { if (step.ResolverResult != null) refusals.Add(Stale($"the resolver of step {step.Id} was planned against a table that no longer exists.")); }
                else
                {
                    var type = step.Parameters.FirstOrDefault(p => p.Source == "resolver")?.Type ?? "TEXT";
                    var again = await TargetSnapshotReader.RunResolverAsync(reader, step.ResolverText!, type, ct);
                    if (again.Error != null) refusals.Add(new Diagnostic(DiagnosticCatalog.InputNotResolved, new($"step:{step.Id}", 0, 0), $"the resolver of step {step.Id} returned {again.Error}, but the plan recorded {step.ResolverResult ?? "NULL"}."));
                    else if (again.Value != step.ResolverResult)
                    {
                        moved.Add(new MovedInput(step.Id, step.Object, operation, "watermark", step.ResolverResult, again.Value));
                        resultNow = again.Value;
                        if (again.Value != null) Set("watermark", again.Value, "resolver", step.Parameters.FirstOrDefault(p => p.Name == "watermark")?.Value);
                    }
                }
            }
            steps.Add(step with { Parameters = parameters, Inputs = inputs, ResolverResult = resultNow });
        }
        foreach (var unmatched in pins.Where(p => !matched.Contains(p)))
            refusals.Add(new Diagnostic(DiagnosticCatalog.InputNotResolved, new("plan", 0, 0), $"--input `{unmatched.Text}`: none of the loads still to run in this plan has an input called `{unmatched.Name}`{(unmatched.Target != null ? $" in `{unmatched.Target}`" : "")}."));
        return (steps, moved);
    }

    /// <summary>What the event records as the selection: the text of the arguments that chose it, or nothing when the whole plan ran.</summary>
    internal static string? SelectionOf(ApplyOptions o) => o.Run is { Selection.Length: > 0 } r ? r.Selection : null;

    private static Diagnostic Stale(string detail) => new(DiagnosticCatalog.PlanIsStale, new("plan", 0, 0), detail);

    private static bool IsDone(PlanStep s, PlanProgress p) => s.Type switch
    {
        StepType.Ddl => p.CompletedDdlHashes.Contains(Hashing.ScriptHash(s.Text)),
        StepType.Load or StepType.Backfill or StepType.Hook or StepType.Transfer => p.CompletedRunSteps.Contains(s.Id),
        StepType.Track => s.HashAfter != null && p.RecordedShapes.Contains((s.Object, s.HashAfter)),
        _ => false,
    };

    private static async Task FinishMigration(Tracker tracker, MutationGate gate, Plan plan, string planHash, string planText, ApplyOptions o, string status, CancellationToken ct)
    {
        if (gate.DryRun) return;
        try { await tracker.MigrationAsync("migration:" + status, plan.Id, planHash, planText, o.GitCommit, o.Invoker, status, null, null, o.Lane, o.ProjectHash, o.Run?.PlanName, SelectionOf(o), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the failure that got us here matters more; the statement log has what ran */ }
    }

    private static async Task<ObjectShape?> LiveAsync(ReadSession reader, string engine, string obj, CancellationToken ct)
    {
        var (schema, name) = DdlGenerator.Split(obj);
        return (await CatalogReader.ReadObjectsAsync(reader, engine, schema, ct, objectName: name)).GetValueOrDefault(obj);
    }

    /// <summary>The status a load that threw is recorded with: past its `max_runtime` when the limit is what cancelled it.</summary>
    private static string FailureStatus(CancellationTokenSource? limit) => limit is { IsCancellationRequested: true } ? LoadStatus.FailedMaxRuntime : LoadStatus.Failed;

    /// <summary>Executes one step. Returns a diagnostic when the step ran but its result is not what the plan promised.</summary>
    /// <param name="refresh">For a step of a refresh: what its row in `run_log` records about the plan; nothing for a step of a deploy.</param>
    /// <param name="measure">Filled with the rows and the time of a load.</param>
    /// <param name="limit">The source of the load's `max_runtime`: when it is what cancelled the statement, the row says so.</param>
    internal static async Task<Diagnostic?> ExecuteStepAsync(Plan plan, PlanStep step, ReadSession reader, MutationGate gate, Tracker tracker, ApplyOptions o, Guid runId, RefreshStepRecord? refresh, StepMeasure? measure, CancellationTokenSource? limit, CancellationToken ct)
    {
        var engine = reader.Engine;
        switch (step.Type)
        {
            case StepType.Ddl:
            {
                var beforeShape = gate.DryRun ? null : await LiveAsync(reader, engine, step.Object, ct);
                var ddlId = Guid.NewGuid();
                var hash = Hashing.ScriptHash(step.Text);
                if (!gate.DryRun) await tracker.BeginDdlAsync(step.Id + ":log", ddlId, step.Object, o.RecordText ? step.Text : "", hash, beforeShape?.ShapeHash, o.Invoker, plan.Id, o.GitCommit, ct);
                try { await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id, StatementKind.Ddl, step.Text), ct); }
                catch (Exception) when (!gate.DryRun)
                {
                    await tracker.FinishDdlAsync(step.Id + ":log", ddlId, "failed", null, ct);
                    throw;
                }
                if (gate.DryRun) return null;

                var after = await LiveAsync(reader, engine, step.Object, ct);
                if (step.HashAfter != null && after?.ShapeHash != step.HashAfter)
                {
                    await tracker.FinishDdlAsync(step.Id + ":log", ddlId, "mismatch", after?.ShapeHash, ct);
                    return new Diagnostic(DiagnosticCatalog.StepResultDiffers, new($"step:{step.Id}", 0, 0),
                        $"After step {step.Id} ({step.Description}) `{step.Object}` has shape {after?.ShapeHash[..12] ?? "missing"}, but the plan promised {step.HashAfter[..12]}. Nothing after this step ran.");
                }
                if (step.Expect != null && IndexMismatch(step.Expect, after) is { } mismatch)
                {
                    await tracker.FinishDdlAsync(step.Id + ":log", ddlId, "mismatch", after?.ShapeHash, ct);
                    return new Diagnostic(DiagnosticCatalog.StepResultDiffers, new($"step:{step.Id}", 0, 0), $"After step {step.Id} ({step.Description}) {mismatch}. Nothing after this step ran.");
                }
                await tracker.FinishDdlAsync(step.Id + ":log", ddlId, "ok", after?.ShapeHash, ct);
                if (after != null)
                    await tracker.RecordSchemaVersionAsync(step.Id + ":version", step.Object, after.ShapeHash, after.PhysicalHash, "tool", plan.Id, o.GitCommit, ct);
                return null;
            }

            case StepType.Track:
            {
                if (gate.DryRun) return null; // a tracking write has no target statement to show beyond the plan's own description
                var live = await LiveAsync(reader, engine, step.Object, ct);
                if (live == null || live.ShapeHash != step.HashAfter)
                    return new Diagnostic(DiagnosticCatalog.StepResultDiffers, new($"step:{step.Id}", 0, 0), $"`{step.Object}` is not in the shape the track step recorded; nothing was recorded.");
                await tracker.RecordSchemaVersionAsync(step.Id + ":version", step.Object, live.ShapeHash, live.PhysicalHash, step.ShapeSource ?? "tool", plan.Id, o.GitCommit, ct);
                return null;
            }

            case StepType.Load:
            case StepType.Backfill:
            {
                var parameters = step.Parameters.Select(ToGate).ToList();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var shapeStart = gate.DryRun || !o.RecordShapes ? null : (await LiveAsync(reader, engine, step.Object, ct))?.ShapeHash;
                var json = JsonSerializer.Serialize(step.Parameters.Select(p => p.Planned == null ? (object)new { p.Name, p.Type, p.Source, p.Value } : new { p.Name, p.Type, p.Source, p.Value, p.Planned }), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                var watermark = step.Parameters.FirstOrDefault(p => p.Name == "watermark")?.Value;
                if (!gate.DryRun)
                    await tracker.BeginRunAsync(step.Id, runId, step.Object, step.Operation ?? "load", plan.Id, o.GitCommit, step.DefinitionHash, shapeStart, step.Operation,
                        step.FileHash, step.ResolverText == null ? null : Hashing.ScriptHash(step.ResolverText), json, watermark, refresh, ct);
                long rows;
                try { rows = await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id, StatementKind.Data, step.Text, parameters), ct); }
                catch (Exception) when (!gate.DryRun)
                {
                    await tracker.FinishRunAsync(step.Id, runId, FailureStatus(limit), null, null, clock.ElapsedMilliseconds, CancellationToken.None);
                    throw;
                }
                if (gate.DryRun) return null;
                if (measure != null) (measure.Rows, measure.DurationMs) = (rows, clock.ElapsedMilliseconds);
                var shapeEnd = o.RecordShapes ? (await LiveAsync(reader, engine, step.Object, ct))?.ShapeHash : null;
                await tracker.FinishRunAsync(step.Id, runId, "ok", rows, shapeEnd, clock.ElapsedMilliseconds, ct);
                // the range of data this operation produced, under the shape it produced it in (DESIGN.md 12.3 reads these)
                var start = step.Parameters.FirstOrDefault(p => p.Name is "start" or "watermark")?.Value;
                var end = step.Parameters.FirstOrDefault(p => p.Name == "end")?.Value;
                if (start != null || end != null)
                    await tracker.IntervalAsync(step.Id + ":interval", step.Object, runId, start, end, shapeEnd ?? "", step.Type == StepType.Backfill ? "backfill" : "load", ct);
                return null;
            }

            case StepType.Hook:
            {
                // a hook is native SQL run exactly as committed; it is logged like a load (operation = its event, load_name = its name)
                var kind = step.Effect == "data" ? StatementKind.Data : StatementKind.Ddl;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var before = gate.DryRun || !o.RecordShapes ? null : await LiveAsync(reader, engine, step.Object, ct);
                if (!gate.DryRun)
                    await tracker.BeginRunAsync(step.Id, runId, step.Object, step.Operation ?? "hook", plan.Id, o.GitCommit, null, before?.ShapeHash, step.Hook, step.FileHash, null, null, null, refresh, ct);
                long rows;
                try { rows = await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id, kind, step.Text), ct); }
                catch (Exception) when (!gate.DryRun)
                {
                    await tracker.FinishRunAsync(step.Id, runId, FailureStatus(limit), null, null, clock.ElapsedMilliseconds, CancellationToken.None);
                    throw;
                }
                if (gate.DryRun) return null;
                var after = o.RecordShapes ? await LiveAsync(reader, engine, step.Object, ct) : null;
                await tracker.FinishRunAsync(step.Id, runId, "ok", rows, after?.ShapeHash, clock.ElapsedMilliseconds, ct);
                // a script that changed the object's shape was the operator's own decision: record the new shape so it is not mistaken for an outside change
                if (kind == StatementKind.Ddl && after != null && before != null && after.ShapeHash != before.ShapeHash)
                    await tracker.RecordSchemaVersionAsync(step.Id + ":version", step.Object, after.ShapeHash, after.PhysicalHash, "hook", plan.Id, o.GitCommit, ct);
                return null;
            }

            case StepType.Transfer:
                return await TransferAsync(plan, step, reader, gate, tracker, o, runId, refresh, ct);

            default:
                throw new InvalidOperationException($"Step type {step.Type} cannot be applied yet.");
        }
    }

    /// <summary>
    /// A copy's transfer: the staging table is (re)created from the step's text, then the rows are read on the origin (a streaming read, one SELECT), converted by the declared type of each column and
    /// written to it with the engine's bulk route. The run is logged like a load (operation `transfer`, the row count, no values). The load that follows reads the staging table.
    /// </summary>
    private static async Task<Diagnostic?> TransferAsync(Plan plan, PlanStep step, ReadSession reader, MutationGate gate, Tracker tracker, ApplyOptions o, Guid runId, RefreshStepRecord? refresh, CancellationToken ct)
    {
        var spec = step.Transfer!;
        var engine = reader.Engine;
        var columns = spec.Columns.Select(c => new TransferColumn(c.Name, c.Type)).ToList();
        var (stagingSchema, stagingTable) = DdlGenerator.Split(spec.Staging);
        var statement = GateStatement.BulkCopy(step.Id, spec.Staging, columns);

        if (gate.DryRun)
        {
            await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id + ":staging", StatementKind.Ddl, step.Text), ct);
            await gate.BulkCopyAsync(statement, stagingSchema, stagingTable, columns, EmptyRows(), ct);
            return null;
        }

        await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id + ":staging", StatementKind.Ddl, step.Text), ct);
        await tracker.BeginRunAsync(step.Id, runId, step.Object, "transfer", plan.Id, o.GitCommit, null, null, $"from {spec.Origin}", null, null, null, null, refresh, ct);          // the origin is in the record, so `report` can say which origin last copied well
        try
        {
            await using var origin = await o.OpenOrigin!(spec.Origin, ct);
            await using var stream = spec.Command ? await origin.OpenCommandStreamAsync(spec.ReadText, OriginParameters(spec), ct) : await origin.OpenStreamAsync(spec.ReadText, OriginParameters(spec), ct);
            var names = stream.Names;
            // what the origin returns: every column of the copy, except the slice column the copy adds (that one is written here, not read)
            var read = spec.Slice is { Added: true } added ? columns.Where(c => !string.Equals(c.Name, added.Column, StringComparison.OrdinalIgnoreCase)).ToList() : columns.ToList();
            // a command returns whatever its procedure selects: its columns are matched to the declared ones by name (extra ones are ignored), not by position
            int[]? project = null;
            if (spec.Command && read.All(c => names.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase))))
                project = read.Select(c => names.ToList().FindIndex(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (project == null && !names.SequenceEqual(read.Select(c => c.Name), StringComparer.OrdinalIgnoreCase))
            {
                await tracker.FinishRunAsync(step.Id, runId, "failed", null, null, null, ct);
                return new Diagnostic(DiagnosticCatalog.StepResultDiffers, new($"step:{step.Id}", 0, 0),
                    $"The read on `{spec.Origin}` returned the columns [{string.Join(", ", names)}], but the plan expects [{string.Join(", ", read.Select(c => c.Name))}]. Nothing was copied.");
            }
            var written = await gate.BulkCopyAsync(statement, stagingSchema, stagingTable, columns, Converted(project == null ? stream.ReadAsync(ct) : Projected(stream.ReadAsync(ct), project, ct), columns, read.Count, spec.Slice, ct), ct);
            await tracker.FinishRunAsync(step.Id, runId, "ok", written, null, null, ct);
            return null;
        }
        catch (TransferException ex)
        {
            await tracker.FinishRunAsync(step.Id, runId, "failed", null, null, null, ct);
            return new Diagnostic(DiagnosticCatalog.StepFailed, new($"step:{step.Id}", 0, 0), $"step {step.Id} ({step.Description}): {ex.Message} Later steps did not run.");
        }
        catch (Exception)
        {
            await tracker.FinishRunAsync(step.Id, runId, "failed", null, null, null, ct);
            throw;
        }
    }

    /// <summary>What a transfer's read binds: the parameters of a native origin's text, and an incremental copy's `@watermark`.</summary>
    private static IReadOnlyList<GateParameter>? OriginParameters(TransferSpec spec)
    {
        var list = (spec.Parameters ?? []).Select(ToGate).ToList();
        if (spec.Watermark != null) list.Add(ToGate(new PlanParameter("watermark", spec.Watermark.Type, "resolver", spec.Watermark.Value)));
        // an origin that binds by position gets the values in the order the plan recorded (a name used twice in the text is passed twice)
        if (spec.ParameterOrder is { Count: > 0 } order)
        {
            var byName = list.ToDictionary(x => x.Name.TrimStart('@'), StringComparer.Ordinal);
            return order.Select(name => byName[name]).ToList();
        }
        return list.Count == 0 ? null : list;
    }

    private static async IAsyncEnumerable<object?[]> Projected(IAsyncEnumerable<object?[]> rows, int[] positions, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in rows.WithCancellation(ct)) yield return positions.Select(i => row[i]).ToArray();
    }

    private static async IAsyncEnumerable<object?[]> EmptyRows() { await Task.CompletedTask; yield break; }

    /// <summary>
    /// The rows of the origin in the declared types. With a slice that the copy adds, its value is written into every row (typed like the column); with a slice the data already has, a row that holds any other value is
    /// an error that names the column, so an origin cannot write into another's slice.
    /// </summary>
    private static async IAsyncEnumerable<object?[]> Converted(IAsyncEnumerable<object?[]> rows, IReadOnlyList<TransferColumn> columns, int readWidth, PlanSlice? slice,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sliceIndex = slice == null ? -1 : columns.ToList().FindIndex(c => string.Equals(c.Name, slice.Column, StringComparison.OrdinalIgnoreCase));
        var expected = slice == null ? null : TransferValues.Convert(slice.Value, columns[sliceIndex]);
        await foreach (var row in rows.WithCancellation(ct))
        {
            var full = row;
            if (slice is { Added: true })
            {
                full = new object?[columns.Count];
                for (var i = 0; i < readWidth; i++) full[i < sliceIndex ? i : i + 1] = row[i];          // the slice column is not among the read ones: it is the added one (last, or wherever the copy put it)
                full[sliceIndex] = expected;
            }
            for (var i = 0; i < columns.Count; i++)
                if (!(slice is { Added: true } && i == sliceIndex)) full[i] = TransferValues.Convert(full[i], columns[i]);
            if (slice is { Added: false } && !Equals(full[sliceIndex], expected))
                throw new TransferException($"column `{slice.Column}`: a row of the origin does not hold the slice value the plan names for it.");
            yield return full;
        }
    }

    /// <summary>Checks an index step's post-condition (`index:&lt;name&gt;=&lt;canonical&gt;`) against the live object. Returns what is wrong, or null.</summary>
    private static string? IndexMismatch(string expect, ObjectShape? live)
    {
        var body = expect["index:".Length..];
        var eq = body.IndexOf('=');
        var (name, canonical) = (body[..eq], body[(eq + 1)..]);
        var found = live?.Physical.FirstOrDefault(p => p.Kind == "index" && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found == null) return $"index `{name}` does not exist on `{live?.QualifiedName ?? "the object"}`";
        return string.Equals(found.Definition, canonical, StringComparison.OrdinalIgnoreCase) ? null : $"index `{name}` is `{found.Definition}`, but the plan promised `{canonical}`";
    }

    /// <summary>A plan parameter as a bound value: the plan holds invariant-culture text, the driver gets a typed value.</summary>
    public static GateParameter ToGate(PlanParameter p)
    {
        var type = p.Type.Trim().ToUpperInvariant();
        if (p.Value == null)
            return new(p.Name, type.StartsWith("TIMESTAMP") ? DbType.DateTime2 : type == "DATE" ? DbType.Date : type is "BIGINT" or "INTEGER" or "SMALLINT" ? DbType.Int64 : DbType.String, null);
        if (type == "DATE") return new(p.Name, DbType.Date, DateTime.ParseExact(p.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (type.StartsWith("TIMESTAMP"))
            return new(p.Name, DbType.DateTime2, DateTime.ParseExact(p.Value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFF", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None));
        if (type is "BIGINT" or "INTEGER" or "SMALLINT") return new(p.Name, DbType.Int64, long.Parse(p.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        return new(p.Name, DbType.String, p.Value);
    }
}
