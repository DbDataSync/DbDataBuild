using System.Data;
using System.Globalization;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>Reads of the audit tables about refresh plans: the runs of a plan, and the loads those runs did.</summary>
public static partial class AuditLog
{
    /// <summary>One run of a refresh plan as the tracking tables say: how it ended, when, the selection it was given, and how many of the plan's members that selection held.</summary>
    public sealed record PlanRun(string Id, string Plan, string Status, DateTime StartedUtc, DateTime LastUtc, string? Selection, int Selected, int Total)
    {
        /// <summary>A run that was asked for part of the plan. A plan that has only ever run in part is not healthy.</summary>
        public bool Partial => Selected < Total;
    }

    /// <summary>The newest <paramref name="count"/> runs of refresh plans on a connection (of one plan when <paramref name="plan"/> is given), newest first. A load named directly has no plan and is not here.</summary>
    public static async Task<IReadOnlyList<PlanRun>> PlanRunsAsync(ReadSession read, TrackingScope scope, string? plan, int count, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var limit = Math.Max(count * 6, 60);
        var filter = $"{c("connection")} = @connection AND {c("refresh_plan")} IS NOT NULL" + (plan == null ? "" : $" AND {c("refresh_plan")} = @plan");
        var cols = $"{c("plan_id")}, {c("refresh_plan")}, {c("status")}, {c("applied_utc")}, {c("selection")}";
        var sql = scope.Engine == "postgres"
            ? $"SELECT {cols} FROM {T(scope, "migration_log")} WHERE {filter} ORDER BY {c("applied_utc")} DESC LIMIT {limit}"
            : $"SELECT TOP ({limit}) {cols} FROM {T(scope, "migration_log")} WHERE {filter} ORDER BY {c("applied_utc")} DESC";
        var parameters = plan == null ? new[] { Connection(scope) } : [Connection(scope), new GateParameter("plan", DbType.AnsiString, plan)];
        var rows = await read.QueryAsync(sql, parameters, ct);
        static string Text(object? v) => (v as string)?.Trim() ?? "";
        static DateTime When(object? v) => v is DateTime d ? d : DateTime.MinValue;
        var events = rows.GroupBy(r => Text(r[0])).Select(g => (Id: g.Key, Plan: Text(g.First()[1]), Status: Text(g.First()[2]), Started: g.Min(r => When(r[3])), Last: When(g.First()[3]), Selection: g.Select(r => r[4] as string).FirstOrDefault(x => !string.IsNullOrEmpty(x))))
            .OrderByDescending(e => e.Last).Take(count).ToList();
        var result = new List<PlanRun>();
        foreach (var e in events)
        {
            var members = await read.QueryAsync($"SELECT {c("selected")} FROM {T(scope, "refresh_plan_member")} WHERE {c("connection")} = @connection AND {c("plan_id")} = @plan_id",
                [Connection(scope), new GateParameter("plan_id", DbType.AnsiString, e.Id)], ct);
            result.Add(new PlanRun(e.Id, e.Plan, e.Status, e.Started, e.Last, e.Selection, members.Count(m => Text(m[0]) == "selected"), members.Count));
        }
        return result;
    }

    /// <summary>A load a run of a plan did: how it ended, when and for how long.</summary>
    public sealed record PlanLoadRow(string Model, string Membership, string Status, DateTime StartedUtc, long? DurationMs, long? Rows, string RunId);

    /// <summary>The loads the runs of a plan did, newest first (hooks are left out).</summary>
    public static async Task<IReadOnlyList<PlanLoadRow>> PlanLoadsAsync(ReadSession read, TrackingScope scope, string plan, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync(
            $"SELECT r.{c("model")}, r.{c("membership")}, r.{c("status")}, r.{c("started_utc")}, r.{c("duration_ms")}, r.{c("rows_affected")}, r.{c("plan_id")} FROM {T(scope, "run_log")} r " +
            $"WHERE r.{c("connection")} = @connection AND r.{c("membership")} IS NOT NULL AND r.{c("operation")} NOT IN ('pre_load', 'post_load', 'transfer') AND r.{c("plan_id")} IN (SELECT m.{c("plan_id")} FROM {T(scope, "migration_log")} m WHERE m.{c("connection")} = @connection AND m.{c("refresh_plan")} = @plan) " +
            $"ORDER BY r.{c("started_utc")} DESC", [Connection(scope), new GateParameter("plan", DbType.AnsiString, plan)], ct);
        static string Text(object? v) => (v as string)?.Trim() ?? "";
        static long? Long(object? v) => v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        return rows.Select(r => new PlanLoadRow(Text(r[0]), Text(r[1]), Text(r[2]), r[3] is DateTime d ? d : DateTime.MinValue, Long(r[4]), Long(r[5]), Text(r[6]))).ToList();
    }

    /// <summary>A load as the last run of a plan left it: `ok`, `failed...`, `skipped: ...` or `not run`.</summary>
    public sealed record LoadState(string Model, string Membership, string Status);

    /// <summary>The event that last ran a plan (any selection) and the state each of its loads ended in, or nulls when the plan was never run. `--only-failed` runs the loads of it that did not end `ok`.</summary>
    public static async Task<(string? EventId, IReadOnlyList<LoadState> Loads)> LastPlanRunLoadsAsync(ReadSession read, TrackingScope scope, string plan, CancellationToken ct = default)
    {
        var runs = await PlanRunsAsync(read, scope, plan, 1, ct);
        if (runs.Count == 0) return (null, []);
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync(
            $"SELECT r.{c("model")}, r.{c("membership")}, r.{c("status")} FROM {T(scope, "run_log")} r WHERE r.{c("connection")} = @connection AND r.{c("plan_id")} = @plan_id AND r.{c("membership")} IS NOT NULL ORDER BY r.{c("started_utc")}",
            [Connection(scope), new GateParameter("plan_id", DbType.AnsiString, runs[0].Id)], ct);
        static string Text(object? v) => (v as string)?.Trim() ?? "";
        // a load and its hooks are one load: it ended `ok` only if every row of it did
        var loads = rows.GroupBy(r => (Model: Text(r[0]), Membership: Text(r[1]))).Select(g => new LoadState(g.Key.Model, g.Key.Membership, g.Select(r => Text(r[2])).FirstOrDefault(x => x != "ok") ?? "ok")).ToList();
        return (runs[0].Id, loads);
    }

    /// <summary>The load of a run that took longest, its hooks counted with it (they are part of the time the load took in the plan), or null when the run recorded no durations.</summary>
    public static async Task<PlanLoadRow?> SlowestLoadOfRunAsync(ReadSession read, TrackingScope scope, string eventId, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync(
            $"SELECT r.{c("model")}, r.{c("membership")}, r.{c("status")}, r.{c("started_utc")}, r.{c("duration_ms")}, r.{c("rows_affected")}, r.{c("plan_id")} FROM {T(scope, "run_log")} r " +
            $"WHERE r.{c("connection")} = @connection AND r.{c("plan_id")} = @plan_id AND r.{c("membership")} IS NOT NULL AND r.{c("duration_ms")} IS NOT NULL",
            [Connection(scope), new GateParameter("plan_id", DbType.AnsiString, eventId)], ct);
        static string Text(object? v) => (v as string)?.Trim() ?? "";
        static long Long(object? v) => v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        return rows.GroupBy(r => (Model: Text(r[0]), Membership: Text(r[1])))
            .Select(g => new PlanLoadRow(g.Key.Model, g.Key.Membership, Text(g.First()[2]), g.Min(r => r[3] is DateTime d ? d : DateTime.MaxValue), g.Sum(r => Long(r[4])), null, eventId))
            .OrderByDescending(l => l.DurationMs).FirstOrDefault();
    }
}
