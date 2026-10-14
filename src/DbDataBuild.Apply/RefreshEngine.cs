using System.Diagnostics;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Apply;

/// <summary>
/// How a load of a refresh ended, as `run_log.status` records it (docs/research/refresh-plans.md, section 6). The layout has one column for it, so a reason is part of the text after a colon: `ok` and `failed` are what they
/// always were, and anything else says what happened. A person reads the state (`done`, `failed`, `skipped`, `not run`); a program reads the whole text.
/// </summary>
public static class LoadStatus
{
    public const string Done = "ok";
    public const string Failed = "failed";
    public const string FailedCheck = "failed: check";
    public const string FailedMaxRuntime = "failed: max runtime";
    public const string FailedBusy = "failed: busy";
    public const string FailedInput = "failed: input";
    public const string FailedOrigin = "failed: origin";
    public const string SkippedUpstreamFailed = "skipped: upstream failed";
    public const string SkippedBusy = "skipped: busy";
    public const string SkippedStopped = "skipped: stopped";
    public const string SkippedCheckFailed = "skipped: check failed";
    public const string SkippedNoValue = "skipped: no value";
    public const string NotRun = "not run";

    /// <summary>`done`, `failed`, `skipped` or `not run`.</summary>
    public static string StateOf(string status) => status == Done ? "done" : status.StartsWith("failed", StringComparison.Ordinal) ? "failed" : status.StartsWith("skipped", StringComparison.Ordinal) ? "skipped" : "not run";

    /// <summary>The reason after the state (`upstream failed`, `check`, `busy` ...), or null.</summary>
    public static string? ReasonOf(string status) => status.IndexOf(": ", StringComparison.Ordinal) is var i and > 0 ? status[(i + 2)..] : null;

    /// <summary>Whether a later run should do this load again (`--only-failed`): anything that did not end `ok`.</summary>
    public static bool NeedsAnotherRun(string status) => status != Done;
}

/// <summary>
/// One load of a refresh as the engine runs it: where it stands in the plan, what it is called, and the rules that apply to it (already worked out from the plan and the membership).
/// </summary>
/// <param name="LoadStepId">The id of the plan step that is the load itself (the hooks around it have ids of their own); the row of a load that never ran is written under it.</param>
/// <param name="OnFailure">`stop`, `continue` or `ignore`.</param>
/// <param name="OnBusy">`skip`, `wait` or `fail`.</param>
/// <param name="MaxWait">With `wait`: how long to wait for a busy load; <see cref="Timeout.InfiniteTimeSpan"/> until it is free.</param>
/// <param name="MaxRuntime">Opt-in: past it the statement in flight is cancelled, the transaction rolls back and the load fails.</param>
/// <param name="CheckFinding">The finding of the refresh check about this load's object, when it blocks: the load fails with kind `check` instead of running.</param>
public sealed record RefreshUnit(int Position, string Membership, string Model, string Load, string Display, string LoadPlanHash, string? DefinitionHash, string LoadStepId,
    string OnFailure, string OnBusy, TimeSpan MaxWait, TimeSpan? MaxRuntime, string? MaxRuntimeText, Diagnostic? CheckFinding);

/// <summary>A load made ready to run just before its turn: its steps with the values of every input, and the inputs as the event records them. A <see cref="Problem"/> says why it cannot run (an input that has no value, a resolver that failed).</summary>
public sealed record PreparedLoad(IReadOnlyList<PlanStep> Steps, string InputsJson, string? Problem = null, string ProblemStatus = LoadStatus.FailedInput);

/// <summary>Makes a load ready: resolves its inputs against the connection (read-only) at the moment its turn comes, so a load sees what the loads before it wrote.</summary>
public delegate Task<PreparedLoad> PrepareLoad(RefreshUnit unit, ReadSession read, CancellationToken ct);

/// <summary>The loads of a refresh in dependency order, how to make each ready, and the models downstream of a model (from the dependency graph in `loads.yml`; a file compiled before the graph was carried reads the project, only when something fails or is skipped).</summary>
public sealed record RefreshExecution(IReadOnlyList<RefreshUnit> Units, PrepareLoad Prepare, Func<string, IReadOnlySet<string>> DownstreamOf);

/// <summary>A load of a refresh and how it ended. <see cref="Note"/> is set on a load that ran although something upstream of it was skipped because it was busy.</summary>
public sealed record LoadOutcome(RefreshUnit Unit, string Status, long? Rows, long? DurationMs, string? Detail, string? Note)
{
    public string State => LoadStatus.StateOf(Status);
    public string? Reason => LoadStatus.ReasonOf(Status);
}

/// <summary>How a refresh ended: `completed`, `completed_with_failures`, `stopped` or `failed`, with every load and the failures that were ignored.</summary>
public sealed record RefreshSummary(string Outcome, IReadOnlyList<LoadOutcome> Loads, IReadOnlyList<string> Warnings)
{
    public const string Completed = "completed", CompletedWithFailures = "completed_with_failures", Stopped = "stopped", Failed = "failed";
}

/// <summary>
/// The sessions one load of a refresh runs on: a read session, the gate of a write session, and the tracker that writes the load's records. A refresh that runs one load at a time uses the run's own; one that runs loads
/// together gives each concurrent load a worker of its own, since a connection runs one statement at a time.
/// </summary>
public sealed class RefreshWorker(ReadSession reader, MutationGate gate, Tracker tracker, IReadOnlyList<IAsyncDisposable>? owned = null) : IAsyncDisposable
{
    public ReadSession Reader { get; } = reader;
    public MutationGate Gate { get; } = gate;
    public Tracker Tracker { get; } = tracker;

    public async ValueTask DisposeAsync()
    {
        foreach (var o in owned ?? []) { try { await o.DisposeAsync(); } catch (Exception) { /* closing */ } }
    }
}

/// <summary>Hands the workers of a run out and takes them back; opens one more only when none is idle (a run never has more workers than loads running at once).</summary>
internal sealed class WorkerPool(RefreshWorker first, Func<CancellationToken, Task<RefreshWorker>>? open) : IAsyncDisposable
{
    private readonly Stack<RefreshWorker> idle = new(open == null ? [first] : []);
    private readonly List<RefreshWorker> opened = [];

    public async Task<RefreshWorker> TakeAsync(CancellationToken ct)
    {
        lock (idle) { if (idle.Count > 0) return idle.Pop(); }
        if (open == null) throw new InvalidOperationException("This run has one session and it is in use.");
        var worker = await open(ct);
        lock (idle) opened.Add(worker);
        return worker;
    }

    public void Return(RefreshWorker worker) { lock (idle) idle.Push(worker); }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in opened) await w.DisposeAsync();
    }
}

/// <summary>
/// Runs the loads of a refresh (docs/research/refresh-plans.md, section 6): a lock per load instead of one for the connection, the failure rule of each load, a busy load by its `on_busy`, a load past its
/// `max_runtime` cancelled, and one row in `run_log` for every load whether it ran or not. A load starts when the loads of the plan that it reads have finished, with at most `parallel` running at once
/// (<see cref="RefreshScheduler"/>); with 1 that is one after another, in the order of the plan, exactly as before there was a `parallel`.
/// </summary>
internal static class RefreshEngine
{
    public static async Task<ApplyResult> RunAsync(Plan plan, string planText, string planHash, ReadSession reader, Tracker tracker, MutationGate gate, Func<CancellationToken, Task<RefreshWorker>>? openWorker, ApplyOptions o, Guid runId, string connectionLock, Action<string> progress, CancellationToken ct)
    {
        var exec = o.Execution!;
        if (!gate.DryRun)
        {
            await tracker.MigrationAsync("migration:start", plan.Id, planHash, planText, o.GitCommit, o.Invoker, "started", Hashing.Sha256Hex(""), null, o.Lane, o.ProjectHash, o.Run?.PlanName, ApplyEngine.SelectionOf(o), ct);
            foreach (var m in o.Run?.Members ?? [])
                await tracker.PlanMemberAsync($"member:{m.Position}", plan.Id, m.Position, m.Membership, m.Model, m.Load, m.LoadPlanHash, m.Selected, ct);
        }

        var units = exec.Units;
        // a dry run executes nothing and prints what it resolved: one load at a time keeps that text in the order of the plan
        var parallel = gate.DryRun || openWorker == null ? 1 : Math.Max(1, Math.Min(o.Parallel, units.Count));
        await using var pool = new WorkerPool(new RefreshWorker(reader, gate, tracker), parallel > 1 ? openWorker : null);

        var results = new LoadOutcome?[units.Count];
        var preparedSteps = new List<PlanStep>?[units.Count];
        var refusals = new List<Diagnostic>();
        var failedModels = new List<string>();                  // loads that failed under `continue`: what reads them is skipped
        var skippedModels = new List<(int Index, string Model, string Reason)>();      // loads skipped because they were busy or an input found nothing and said skip_load: what reads them runs, and says so
        var halted = false; var stopTriggered = false; var operatorStopped = false;

        bool Reads(IEnumerable<string> upstream, string model) => upstream.Any(u => exec.DownstreamOf(u).Contains(model));

        async Task<LoadOutcome> NoRunAsync(RefreshUnit unit, string status, string? detail, string inputs = "[]")
        {
            if (!gate.DryRun)
                await tracker.UnrunLoadAsync(unit.LoadStepId, runId, unit.Model, unit.Load, status, plan.Id, o.GitCommit, unit.DefinitionHash, new RefreshStepRecord(unit.Membership, unit.LoadPlanHash, inputs), CancellationToken.None);
            return new LoadOutcome(unit, status, null, null, detail, null);
        }

        // what a load waits for. One at a time: the load before it. Together: the loads that it reads (the graph in `loads.yml`), and an earlier load of the same model, since a model's loads
        // exclude each other through its lock
        IReadOnlyList<int> Dependencies(int i)
        {
            if (parallel == 1) return Enumerable.Range(0, i).ToList();
            return Enumerable.Range(0, i).Where(j => string.Equals(units[j].Model, units[i].Model, StringComparison.OrdinalIgnoreCase) || Reads([units[j].Model], units[i].Model)).ToList();
        }

        async Task<Func<Task>?> Admit(int i)
        {
            var unit = units[i];
            if (halted) { results[i] = await NoRunAsync(unit, operatorStopped ? LoadStatus.SkippedStopped : LoadStatus.NotRun, operatorStopped ? "the operator stopped the refresh" : "an earlier load failed under on_failure stop"); return null; }
            if (o.StopRequested?.Invoke() == true)
            {
                halted = operatorStopped = true;
                refusals.Add(new Diagnostic(DiagnosticCatalog.ApplyStopped, new($"load:{unit.Display}", 0, 0), $"The refresh was stopped before {unit.Display}. The loads before it finished; it and the later loads did not run."));
                results[i] = await NoRunAsync(unit, LoadStatus.SkippedStopped, "the operator stopped the refresh");
                return null;
            }
            if (Reads(failedModels, unit.Model)) { results[i] = await NoRunAsync(unit, LoadStatus.SkippedUpstreamFailed, "a load it reads failed"); return null; }

            progress($"load {unit.Position}/{units.Count}: {unit.Display}");
            var upstreamSkipped = skippedModels.OrderBy(k => k.Index).FirstOrDefault(k => exec.DownstreamOf(k.Model).Contains(unit.Model));
            var note = upstreamSkipped.Model != null ? $"upstream skipped: {upstreamSkipped.Reason}" : null;
            RefreshWorker worker;
            try { worker = await pool.TakeAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the connection would not give another session: the load fails, the failure rules apply
                results[i] = await NoRunAsync(unit, LoadStatus.Failed, $"a session for it could not be opened ({(DriverFailure.Prerequisite(ex) ?? DriverFailure.FlightSql(ex) ?? ex.GetType().Name + SessionNumber(ex))})");
                return null;
            }
            return async () =>
            {
                try { results[i] = await RunUnitAsync(unit, note, plan, worker.Reader, worker.Tracker, worker.Gate, o, runId, connectionLock, preparedSteps, i, ct); }
                finally { pool.Return(worker); }
            };
        }

        Task Finished(int i)
        {
            var unit = units[i]; var result = results[i]!;
            if (result.State == "failed")
            {
                switch (unit.OnFailure)
                {
                    case OnFailureModes.Stop: halted = stopTriggered = true; break;
                    case OnFailureModes.Ignore: break;                            // a warning, written from the results at the end
                    default: failedModels.Add(unit.Model); break;
                }
            }
            else if (result.Status == LoadStatus.SkippedBusy) skippedModels.Add((i, unit.Model, "busy"));
            else if (result.Status == LoadStatus.SkippedNoValue) skippedModels.Add((i, unit.Model, "no value"));
            return Task.CompletedTask;
        }

        await RefreshScheduler.RunAsync(units.Count, parallel, Dependencies, () => halted, Admit, Finished, ct);

        var all = results.Select(r => r!).ToList();                      // in the order of the plan, whichever order the loads ended in
        var warnings = all.Where(r => r.State == "failed" && r.Unit.OnFailure == OnFailureModes.Ignore)
            .Select(r => $"{r.Unit.Display}: {r.Status}{(r.Detail != null ? " (" + r.Detail + ")" : "")}; ignored (on_failure: ignore), what reads it runs on the data it had").ToList();

        // how it ended: a stop wins; an ignored failure is a warning and nothing more; a busy skip is not a failure
        var problems = all.Where(r => r.State is "failed" or "not run" || (r.State == "skipped" && r.Status is not (LoadStatus.SkippedBusy or LoadStatus.SkippedNoValue))).ToList();
        var ignored = all.Where(r => r.State == "failed" && r.Unit.OnFailure == OnFailureModes.Ignore).ToList();
        var counted = problems.Except(ignored).ToList();
        var outcome = stopTriggered || operatorStopped ? RefreshSummary.Stopped
            : counted.Count == 0 ? RefreshSummary.Completed
            : all.Any(r => r.State == "done") ? RefreshSummary.CompletedWithFailures : RefreshSummary.Failed;
        if (!gate.DryRun) await tracker.MigrationAsync("migration:" + outcome, plan.Id, planHash, planText, o.GitCommit, o.Invoker, outcome, null, null, o.Lane, o.ProjectHash, o.Run?.PlanName, ApplyEngine.SelectionOf(o), CancellationToken.None);

        var stepOutcomes = all.Select(r => new StepOutcome(r.Unit.LoadStepId, $"load {r.Unit.Display}", gate.DryRun && r.State == "done" ? "dry-run" : r.State == "done" ? "ok" : r.State == "failed" ? "failed" : r.State, r.Status == LoadStatus.Done ? r.Note : r.Detail is { } d ? $"{r.Status}: {d}" : r.Status)).ToList();
        return new ApplyResult(stepOutcomes, refusals, new RefreshSummary(outcome, all, warnings), preparedSteps.Where(p => p != null).SelectMany(p => p!).ToList());
    }

    /// <summary>`, error 1234` for a database exception, else nothing: what went wrong without the driver's message, which can quote data.</summary>
    private static string SessionNumber(Exception ex) => ex is System.Data.Common.DbException { ErrorCode: var code } && code != 0 ? $", error {code}" : "";

    private static async Task<LoadOutcome> RunUnitAsync(RefreshUnit unit, string? note, Plan plan, ReadSession reader, Tracker tracker, MutationGate gate, ApplyOptions o, Guid runId, string connectionLock, List<PlanStep>?[] preparedSteps, int index, CancellationToken ct)
    {
        var exec = o.Execution!;
        var clock = Stopwatch.StartNew();
        async Task<LoadOutcome> Ended(string status, string? detail, string inputs = "[]", bool writeRow = true)
        {
            if (writeRow && !gate.DryRun)
                await tracker.UnrunLoadAsync(unit.LoadStepId, runId, unit.Model, unit.Load, status, plan.Id, o.GitCommit, unit.DefinitionHash, new RefreshStepRecord(unit.Membership, unit.LoadPlanHash, inputs), CancellationToken.None);
            return new LoadOutcome(unit, status, null, clock.ElapsedMilliseconds, detail, note);
        }

        if (unit.CheckFinding != null) return await Ended(LoadStatus.FailedCheck, unit.CheckFinding.Found);

        // the connection's lock is held shared and the load's exclusive for as long as the load runs: a deploy (exclusive on the connection) and another run of the same load are kept out, everything else runs beside it
        var loadLock = $"{connectionLock}:{unit.Model}";
        var wait = unit.OnBusy == OnBusyModes.Wait ? unit.MaxWait : TimeSpan.Zero;
        var heldConnection = false; var heldLoad = false;
        try
        {
            heldConnection = await gate.AcquireApplicationLockAsync(connectionLock, shared: true, wait, ct);
            heldLoad = heldConnection && await gate.AcquireApplicationLockAsync(loadLock, shared: false, wait, ct);
            if (!heldLoad)
            {
                var why = heldConnection ? $"another run holds the load `{unit.Model}`" : "a deploy holds the connection";
                return unit.OnBusy == OnBusyModes.Fail || unit.OnBusy == OnBusyModes.Wait
                    ? await Ended(LoadStatus.FailedBusy, unit.OnBusy == OnBusyModes.Wait ? $"{why}, and it was still held after {(unit.MaxWait == Timeout.InfiniteTimeSpan ? "waiting" : "max_wait")}" : why)
                    : await Ended(LoadStatus.SkippedBusy, why);
            }

            PreparedLoad prepared;
            try { prepared = await exec.Prepare(unit, reader, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return await Ended(LoadStatus.FailedInput, $"its inputs could not be resolved ({ex.GetType().Name})"); }
            if (prepared.Problem != null) return await Ended(prepared.ProblemStatus, prepared.Problem, prepared.InputsJson);
            preparedSteps[index] = [.. prepared.Steps];

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (unit.MaxRuntime is { } span) limit.CancelAfter(span);
            var record = new RefreshStepRecord(unit.Membership, unit.LoadPlanHash, prepared.InputsJson);
            var measure = new StepMeasure();
            var loadStarted = false;
            foreach (var step in prepared.Steps)
            {
                if (step.Type is StepType.Load or StepType.Backfill) loadStarted = true;          // from here the load has a row of its own
                try
                {
                    // each origin is checked just before its own transfer; the origins before it stay loaded (each is its own unit), and this load fails
                    if (step.Type == StepType.Transfer && !gate.DryRun && o.CheckOrigin != null && await o.CheckOrigin(step, ct) is { Kind: not OriginVerdictKind.Ok } verdict)
                    {
                        var detail = $"origin `{step.Transfer!.Origin}`: {verdict.Detail}";
                        if (!gate.DryRun) await tracker.UnrunLoadAsync(step.Id, runId, unit.Model, unit.Load, LoadStatus.FailedOrigin, plan.Id, o.GitCommit, unit.DefinitionHash, record, CancellationToken.None);
                        return await Ended(LoadStatus.FailedOrigin, detail, prepared.InputsJson, writeRow: false);
                    }
                    var diagnostic = await ApplyEngine.ExecuteStepAsync(plan, step, reader, gate, tracker, o, runId, record, measure, limit, limit.Token);
                    if (diagnostic != null) return await Failed(LoadStatus.Failed, diagnostic.Found);
                }
                catch (Exception) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    return await Failed(LoadStatus.FailedMaxRuntime, $"it ran past its max_runtime of {unit.MaxRuntimeText} and the statement was cancelled; the transaction was rolled back");
                }
                catch (GateRefusedException ex) { return await Failed(LoadStatus.Failed, ex.Diagnostic.Found); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // type only: a driver message can quote data (DESIGN.md 14.2); the statement log has the error number
                    return await Failed(LoadStatus.Failed, $"{step.Description} failed with {ex.GetType().Name}");
                }
            }
            return new LoadOutcome(unit, LoadStatus.Done, measure.Rows, measure.DurationMs ?? clock.ElapsedMilliseconds, null, note);

            async Task<LoadOutcome> Failed(string status, string detail)
            {
                // a failure before the load itself began (a hook, say) leaves no row for the load: write it, so the load has one
                return await Ended(status, detail, prepared.InputsJson, writeRow: !loadStarted);
            }
        }
        finally
        {
            try
            {
                if (heldLoad) await gate.ReleaseApplicationLockAsync(loadLock, shared: false, CancellationToken.None);
                if (heldConnection) await gate.ReleaseApplicationLockAsync(connectionLock, shared: true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* closing the connection releases them */ }
        }
    }
}

/// <summary>What one load measured: its rows and how long its statement took.</summary>
internal sealed class StepMeasure
{
    public long? Rows { get; set; }
    public long? DurationMs { get; set; }
}
