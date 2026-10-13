using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>
/// The writing side of tracking for one run: the audit records of what `apply` does, kept in the tracking store of the connection being applied to (which may be another connection: central tracking). With no
/// tracking configured there is nothing to write, and every method here does nothing: <see cref="None"/>.
/// </summary>
public sealed class Tracker
{
    private readonly MutationGate? gate;
    private readonly TrackingScope? scope;

    private Tracker(MutationGate? gate, TrackingScope? scope) { this.gate = gate; this.scope = scope; }

    /// <summary>No tracking: records nothing, and finds nothing recorded.</summary>
    public static Tracker None { get; } = new(null, null);

    /// <param name="gate">The gate of the tracking connection (the data connection's own gate when they are the same connection).</param>
    public static Tracker For(MutationGate gate, TrackingScope scope) => new(gate, scope);

    public bool Enabled => gate != null;
    public TrackingScope? Scope => scope;

    public Task MigrationAsync(string stepId, string planId, string planHash, string planText, string? gitCommit, string appliedBy, string status, string? hashBefore, string? hashAfter, string? lane = null, string? projectHash = null, string? refreshPlan = null, string? selection = null, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : AuditLog.MigrationAsync(gate, scope!, stepId, planId, planHash, planText, gitCommit, appliedBy, status, hashBefore, hashAfter, lane, projectHash, refreshPlan, selection, ct);

    public Task PlanMemberAsync(string stepId, string planId, int position, string membership, string model, string loadName, string loadPlanHash, bool selected, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : AuditLog.PlanMemberAsync(gate, scope!, stepId, planId, position, membership, model, loadName, loadPlanHash, selected, ct);

    public Task UnrunLoadAsync(string stepId, Guid runId, string model, string loadName, string status, string planId, string? gitCommit, string? definitionHash, RefreshStepRecord refresh, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : AuditLog.UnrunLoadAsync(gate, scope!, stepId, runId, model, loadName, status, planId, gitCommit, definitionHash, refresh, ct);

    public Task BeginDdlAsync(string stepId, Guid ddlId, string objectName, string statementText, string statementHash, string? hashBefore, string invoker, string planId, string? gitCommit, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : AuditLog.BeginDdlAsync(gate, scope!, stepId, ddlId, objectName, statementText, statementHash, hashBefore, invoker, planId, gitCommit, ct);

    public Task FinishDdlAsync(string stepId, Guid ddlId, string status, string? hashAfter, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : AuditLog.FinishDdlAsync(gate, scope!, stepId, ddlId, status, hashAfter, ct);

    public Task BeginRunAsync(string stepId, Guid runId, string model, string operation, string planId, string? gitCommit, string? definitionHash, string? shapeStart, string? loadName, string? loadFileHash,
        string? resolverFileHash, string? parameters, string? watermarkUsed, RefreshStepRecord? refresh = null, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : AuditLog.BeginRunAsync(gate, scope!, stepId, runId, model, operation, planId, gitCommit, definitionHash, shapeStart, loadName, loadFileHash, resolverFileHash, parameters, watermarkUsed, refresh, ct);

    public Task FinishRunAsync(string stepId, Guid runId, string status, long? rows, string? shapeEnd, long? durationMs = null, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : AuditLog.FinishRunAsync(gate, scope!, stepId, runId, status, rows, shapeEnd, durationMs, ct);

    public Task IntervalAsync(string stepId, string model, Guid runId, string? rangeStart, string? rangeEnd, string shapeHash, string operation, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : AuditLog.IntervalAsync(gate, scope!, stepId, model, runId, rangeStart, rangeEnd, shapeHash, operation, ct);

    public Task RecordSchemaVersionAsync(string stepId, string objectName, string shapeHash, string? physicalHash, string source, string? planId, string? gitCommit, CancellationToken ct = default) =>
        gate == null ? Task.CompletedTask : TrackingStore.RecordSchemaVersionAsync(gate, scope!, stepId, objectName, shapeHash, physicalHash, source, planId, gitCommit, ct);
}
