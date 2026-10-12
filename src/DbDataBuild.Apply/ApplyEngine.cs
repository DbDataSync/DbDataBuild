using System.Data;
using System.Globalization;
using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Execution;
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
/// <param name="RecordShapes">Whether each load records the shape of its object before and after (a catalog read per load). A deploy does; a refresh does only when it checks against the live catalog.</param>
public sealed record ApplyOptions(bool DryRun, bool AllowRisky, IReadOnlySet<string> AllowDestructive, string? GitCommit, bool GitDirty, string Invoker, Func<bool>? StopRequested = null, Func<string, CancellationToken, Task<ReadSession>>? OpenOrigin = null,
    string Lane = "deploy", string? ProjectHash = null, string? PlanHash = null, bool Refresh = false, bool RecordShapes = true, bool RecordText = true, RefreshRunInfo? Run = null);

/// <param name="Status">ok, dry-run, skipped (done in an earlier attempt), stopped (the operator stopped before it) or failed.</param>
public sealed record StepOutcome(string StepId, string Description, string Status, string? Detail = null);

public sealed record ApplyResult(IReadOnlyList<StepOutcome> Outcomes, IReadOnlyList<Diagnostic> Refusals)
{
    public bool Success => Refusals.Count == 0 && Outcomes.All(o => o.Status != "failed");
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

        // every connection the plan reads rows from answers before the first statement runs: a copy that cannot read its origin fails before it touches anything
        if (!o.DryRun)
            foreach (var origin in plan.Steps.Where(s => s.Transfer != null).Select(s => s.Transfer!.Origin).Distinct(StringComparer.Ordinal))
            {
                if (o.OpenOrigin == null) return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.StepFailed, new($"connection:{origin}", 0, 0), $"The plan reads rows from `{origin}`, and this run has no way to open it. Nothing was executed.")]);
                try { await using var probe = await o.OpenOrigin(origin, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException and not GateRefusedException)
                {
                    return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.StepFailed, new($"connection:{origin}", 0, 0), $"The connection `{origin}`, which the plan reads rows from, could not be opened ({ex.GetType().Name}). Nothing was executed.")]);
                }
            }

        await using var gate = o.DryRun
            ? MutationGate.DryRunGate("apply", StatementKind.Tracking | StatementKind.Data | StatementKind.Ddl, log, runId)
            : await MutationGate.OpenAsync(write!, "apply", StatementKind.Tracking | StatementKind.Data | StatementKind.Ddl, log, runId, ct);

        await using var ownTrackingGate = tracking != null && !sameConnection
            ? o.DryRun ? MutationGate.DryRunGate("apply", StatementKind.Tracking, log, runId) : await MutationGate.OpenAsync(tracking.Write!, "apply", StatementKind.Tracking, log, runId, ct)
            : null;
        var tracker = tracking == null ? Tracker.None : Tracker.For(sameConnection ? gate : ownTrackingGate!, scope!);

        var lockName = $"{ProductInfo.Cli}:{tracking?.SchemaName ?? ProductInfo.TrackingSchemaName}";
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
        foreach (var s in plan.Steps.Where(s => !o.Refresh && s.HasResolver && !done.Contains(s.Id)))
        {
            if (!snapshot.Live.ContainsKey(s.Object)) { if (s.ResolverResult != null) refusals.Add(Stale($"the resolver of step {s.Id} was planned against a table that no longer exists.")); continue; }
            var type = s.Parameters.FirstOrDefault(p => p.Source == "resolver")?.Type ?? "TEXT";
            var now = await TargetSnapshotReader.RunResolverAsync(reader, s.ResolverText!, type, ct);
            if (now.Error != null || now.Value != s.ResolverResult)
                refusals.Add(Stale($"the resolver of step {s.Id} returned {(now.Error != null ? now.Error : now.Value ?? "NULL")}, but the plan recorded {s.ResolverResult ?? "NULL"}."));
        }
        if (refusals.Count > 0) return new ApplyResult([], refusals);

        // ---- execute ----
        var outcomes = new List<StepOutcome>();
        var before = string.Join(",", plan.Bases.Select(b => b.LiveShapeHash ?? "-"));
        if (!gate.DryRun)
        {
            await tracker.MigrationAsync("migration:start", plan.Id, planHash, planText, o.GitCommit, o.Invoker, "started", Hashing.Sha256Hex(before), null, o.Lane, o.ProjectHash, o.Run?.PlanName, SelectionOf(o), ct);
            foreach (var m in o.Run?.Members ?? [])
                await tracker.PlanMemberAsync($"member:{m.Position}", plan.Id, m.Position, m.Membership, m.Model, m.Load, m.LoadPlanHash, m.Selected, ct);
        }

        foreach (var step in plan.Steps)
        {
            if (done.Contains(step.Id)) { outcomes.Add(new(step.Id, step.Description, "skipped", "finished in an earlier attempt")); continue; }
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
                var diagnostic = await ExecuteStepAsync(plan, step, reader, gate, tracker, o, runId, ct);
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

    /// <summary>What the event records as the selection: the text of the arguments that chose it, or nothing when the whole plan ran.</summary>
    private static string? SelectionOf(ApplyOptions o) => o.Run is { Selection.Length: > 0 } r ? r.Selection : null;

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

    /// <summary>What a refresh step belongs to, for its row in `run_log`; nothing for a step of a deploy.</summary>
    private static RefreshStepRecord? RefreshRecord(ApplyOptions o, PlanStep step) =>
        o.Run is { } run && run.Steps.TryGetValue(step.Id, out var info) ? new RefreshStepRecord(info.Membership, info.LoadPlanHash, info.InputsJson) : null;

    /// <summary>Executes one step. Returns a diagnostic when the step ran but its result is not what the plan promised.</summary>
    private static async Task<Diagnostic?> ExecuteStepAsync(Plan plan, PlanStep step, ReadSession reader, MutationGate gate, Tracker tracker, ApplyOptions o, Guid runId, CancellationToken ct)
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
                var json = JsonSerializer.Serialize(step.Parameters.Select(p => new { p.Name, p.Type, p.Source, p.Value }), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                var watermark = step.Parameters.FirstOrDefault(p => p.Name == "watermark")?.Value;
                if (!gate.DryRun)
                    await tracker.BeginRunAsync(step.Id, runId, step.Object, step.Operation ?? "load", plan.Id, o.GitCommit, step.DefinitionHash, shapeStart, step.Operation,
                        step.FileHash, step.ResolverText == null ? null : Hashing.ScriptHash(step.ResolverText), json, watermark, RefreshRecord(o, step), ct);
                long rows;
                try { rows = await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id, StatementKind.Data, step.Text, parameters), ct); }
                catch (Exception) when (!gate.DryRun)
                {
                    await tracker.FinishRunAsync(step.Id, runId, "failed", null, null, clock.ElapsedMilliseconds, ct);
                    throw;
                }
                if (gate.DryRun) return null;
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
                    await tracker.BeginRunAsync(step.Id, runId, step.Object, step.Operation ?? "hook", plan.Id, o.GitCommit, null, before?.ShapeHash, step.Hook, step.FileHash, null, null, null, RefreshRecord(o, step), ct);
                long rows;
                try { rows = await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id, kind, step.Text), ct); }
                catch (Exception) when (!gate.DryRun)
                {
                    await tracker.FinishRunAsync(step.Id, runId, "failed", null, null, clock.ElapsedMilliseconds, ct);
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
                return await TransferAsync(plan, step, reader, gate, tracker, o, runId, ct);

            default:
                throw new InvalidOperationException($"Step type {step.Type} cannot be applied yet.");
        }
    }

    /// <summary>
    /// A copy's transfer: the staging table is (re)created from the step's text, then the rows are read on the origin (a streaming read, one SELECT), converted by the declared type of each column and
    /// written to it with the engine's bulk route. The run is logged like a load (operation `transfer`, the row count, no values). The load that follows reads the staging table.
    /// </summary>
    private static async Task<Diagnostic?> TransferAsync(Plan plan, PlanStep step, ReadSession reader, MutationGate gate, Tracker tracker, ApplyOptions o, Guid runId, CancellationToken ct)
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
        await tracker.BeginRunAsync(step.Id, runId, step.Object, "transfer", plan.Id, o.GitCommit, null, null, $"from {spec.Origin}", null, null, null, null, null, ct);          // the origin is in the record, so `report` can say which origin last copied well
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
