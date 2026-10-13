using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// The views of refresh plans (docs/research/refresh-plans.md, section 7): `project show refresh` (offline, from the compiled files), the plan view of `connection monitor` (the runs of a plan, complete and partial apart,
/// and each member's last success and failure) and the one line per plan of `connection status`.
/// </summary>
internal static class RefreshViews
{
    /// <summary>The names of the plans compiled for a connection (`rendered/&lt;connection&gt;/refresh/*.yml`), in order.</summary>
    public static IReadOnlyList<string> PlanNames(string root, string connection)
    {
        var dir = Path.Combine(root, RenderCommand.RenderedDir, connection, RefreshPlanDocument.FolderName);
        return Directory.Exists(dir) ? [.. Directory.EnumerateFiles(dir, "*.yml").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal)] : [];
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalDays} d ago";
    }

    private static string Utc(DateTime when) => when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>How a run ended in words: a run that was written as started and never finished was interrupted.</summary>
    private static string Outcome(string status) => status switch { "started" => "interrupted", _ => status };

    /// <summary>
    /// Whether a plan is keeping to its `expected_every`: <see cref="Overdue"/> when its last run began longer ago than that (or it never ran), <see cref="Slow"/> when its last run took longer than that to finish. A run still under way is
    /// neither slow nor does it make the plan overdue until it has been going that long. The slowest load of the last run is named when it is slow.
    /// </summary>
    internal sealed record PlanHealth(string? ExpectedEvery, bool Overdue, bool Slow, long? LastRunSeconds, string? SlowestLoad, long? SlowestMs, long? AgeSeconds)
    {
        public static readonly PlanHealth None = new(null, false, false, null, null, null, null);
    }

    /// <summary>The `expected_every` a compiled plan declares, or null.</summary>
    internal static EveryDuration? ExpectedEveryOf(string root, string connection, string plan)
    {
        var path = Path.Combine(root, RenderCommand.RenderedDir, RefreshPlanDocument.PathOf(connection, plan));
        if (!File.Exists(path)) return null;
        var file = RefreshPlanDocument.Parse(File.ReadAllText(path), path, []);
        return file?.Settings.ExpectedEvery is { } text ? EveryDuration.TryParse(text) : null;
    }

    /// <summary>Works out the health of a plan from its last run; the slowest load is read from the tracking tables only when the run was slow.</summary>
    internal static async Task<PlanHealth> HealthAsync(ReadSession read, TrackingScope scope, EveryDuration? expected, AuditLog.PlanRun? last)
    {
        if (expected == null) return PlanHealth.None;
        var now = DateTime.UtcNow;
        if (last == null) return new PlanHealth(expected.Text, Overdue: true, Slow: false, null, null, null, null);
        var age = (long)(now - last.StartedUtc).TotalSeconds;
        var took = (long)(last.LastUtc - last.StartedUtc).TotalSeconds;
        var finished = last.Status != "started";
        var slow = finished && took > expected.Seconds;
        // a run that has not finished is overdue when it has been going on for the span: it is not keeping to its schedule either
        var overdue = age > expected.Seconds && (finished || age > expected.Seconds);
        string? slowest = null; long? slowestMs = null;
        if (slow && await AuditLog.SlowestLoadOfRunAsync(read, scope, last.Id) is { } load) (slowest, slowestMs) = ($"{load.Model}.{load.Membership}", load.DurationMs);
        return new PlanHealth(expected.Text, overdue, slow, took, slowest, slowestMs, age);
    }

    private static string Span(long seconds) => seconds < 120 ? $"{seconds} s" : seconds < 7200 ? $"{seconds / 60} min" : seconds < 172800 ? $"{seconds / 3600} h" : $"{seconds / 86400} d";

    /// <summary>The warning lines for a plan that is overdue or slow (none when it keeps to its schedule).</summary>
    internal static IEnumerable<string> HealthWarnings(string plan, PlanHealth h, AuditLog.PlanRun? last)
    {
        if (h.Overdue)
            yield return last == null ? $"OVERDUE: plan {plan} is meant to run every {h.ExpectedEvery} and has never run."
                                      : $"OVERDUE: plan {plan} is meant to run every {h.ExpectedEvery}; its last run began {Span(h.AgeSeconds ?? 0)} ago.";
        if (h.Slow)
            yield return $"SLOW: the last run of plan {plan} took {Span(h.LastRunSeconds ?? 0)}, longer than its expected {h.ExpectedEvery}{(h.SlowestLoad != null ? $"; the slowest load was {h.SlowestLoad} ({Span((h.SlowestMs ?? 0) / 1000)})" : "")}.";
    }

    private static object HealthJson(PlanHealth h) => new { expected_every = h.ExpectedEvery, overdue = h.Overdue, slow = h.Slow, last_run_seconds = h.LastRunSeconds, age_seconds = h.AgeSeconds, slowest_load = h.SlowestLoad, slowest_load_ms = h.SlowestMs };

    // ---------------- project show refresh ----------------

    public static int Show(CommandSpec spec, string root, string? planArg, string? connectionArg, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        var config = ProjectConfigLoader.LoadFromProject(root, []);
        if (connectionArg != null && !config.Connections.ContainsKey(connectionArg)) { error.WriteLine($"Unknown connection `{connectionArg}`. One of: {string.Join(", ", config.Connections.Keys.Order(StringComparer.Ordinal))}."); return CliApp.ExitUsage; }
        var diags = new List<Diagnostic>();
        var rows = new List<(string Connection, RefreshPlanFile Plan, LoadsFile Loads)>();
        var renderedRoot = Path.Combine(root, RenderCommand.RenderedDir);
        var connections = Directory.Exists(renderedRoot) ? Directory.EnumerateDirectories(renderedRoot).Select(Path.GetFileName).Where(n => n != "lowered" && n != null).Select(n => n!).Order(StringComparer.Ordinal).ToList() : [];
        foreach (var connection in connections.Where(c => connectionArg == null || c == connectionArg))
        {
            var loadsPath = Path.Combine(renderedRoot, connection, LoadsDocument.FileName);
            if (!File.Exists(loadsPath)) continue;
            var loads = LoadsDocument.Parse(File.ReadAllText(loadsPath), $"{RenderCommand.RenderedDir}/{connection}/{LoadsDocument.FileName}", diags);
            if (loads == null) continue;
            foreach (var name in PlanNames(root, connection).Where(n => planArg == null || n == planArg))
            {
                var path = $"{RenderCommand.RenderedDir}/{RefreshPlanDocument.PathOf(connection, name)}";
                if (RefreshPlanDocument.Parse(File.ReadAllText(Path.Combine(root, path)), path, diags) is { } plan) rows.Add((connection, plan, loads));
            }
        }
        foreach (var d in diags) error.Diag(d);
        if (rows.Count == 0 && diags.Count == 0)
        {
            if (planArg != null) { error.WriteLine($"No compiled refresh plan is called `{planArg}`{(connectionArg != null ? $" for `{connectionArg}`" : "")}. Run `{ProductInfo.Cli} project compile` if a model's `refresh:` was changed."); return CliApp.ExitUsage; }
            output.Payload("plans", Array.Empty<object>());
            output.WriteLine("There are no compiled refresh plans. Run `project compile`.");
            output.Next("project compile");
            return CliApp.ExitFindings;
        }

        object Member(RefreshMember m, LoadsFile loads)
        {
            var entry = loads.Loads.FirstOrDefault(l => l.Model == m.Model && l.Load == m.Load && l.Hash == m.LoadPlanHash);
            var inputs = (entry?.Inputs ?? []).Select(i => m.Inputs.FirstOrDefault(g => g.Name == i.Name) is { } given
                ? new { name = i.Name, type = i.Type, source = "membership", value = (string?)given.Value }
                : new { name = i.Name, type = i.Type, source = i.Source, value = i.Value }).ToList();
            return new { member = m.Name, model = m.Model, load = m.Load, load_plan = m.LoadPlanHash, strategy = entry?.Strategy, on_failure = m.OnFailure, inputs };
        }
        output.Payload("plans", rows.Select(r => new
        {
            connection = r.Connection, name = r.Plan.Name, title = r.Plan.Info.Title, description = r.Plan.Info.Description, hash = r.Plan.Hash, members = r.Plan.Members.Count,
            settings = new { on_failure = r.Plan.Settings.OnFailure, on_busy = r.Plan.Settings.OnBusy, expected_every = r.Plan.Settings.ExpectedEvery, parallel = r.Plan.Settings.Parallel, check = r.Plan.Settings.Check, on_fail = r.Plan.Settings.OnFail },
            load_members = planArg == null ? null : r.Plan.Members.Select(m => Member(m, r.Loads)).ToList(),
            excluded = r.Plan.Excluded.Select(x => new { model = x.Model, member = x.Member, reason = x.Reason }).ToList(),
        }).ToList());

        if (planArg == null)
        {
            output.WriteLine();
            var width = Math.Max(4, rows.Max(r => r.Plan.Name.Length));
            output.WriteLine($"{"plan".PadRight(width)}  connection  members  left out  hash          about");
            foreach (var r in rows)
                output.WriteLine($"{r.Plan.Name.PadRight(width)}  {r.Connection,-10}  {r.Plan.Members.Count,7}  {r.Plan.Excluded.Count,8}  {r.Plan.Hash[..12]}  {r.Plan.Info.Title ?? ""}");
            output.Next(rows.Select(r => $"project show refresh {r.Plan.Name}{(rows.Count(x => x.Plan.Name == r.Plan.Name) > 1 ? $" --connection {r.Connection}" : "")}").Distinct().Take(3).ToArray());
            return CliApp.ExitOk;
        }
        foreach (var r in rows)
        {
            output.WriteLine();
            output.WriteLine($"Plan {r.Plan.Name} on {r.Connection}  (hash {r.Plan.Hash[..12]})");
            if (r.Plan.Info.Title != null) output.WriteLine($"  {r.Plan.Info.Title}");
            if (r.Plan.Info.Description != null) output.WriteLine($"  {r.Plan.Info.Description}");
            var s = r.Plan.Settings;
            var settings = new[] { s.OnFailure != null ? $"on_failure {s.OnFailure}" : null, s.OnBusy != null ? $"on_busy {s.OnBusy}" : null, s.ExpectedEvery != null ? $"expected_every {s.ExpectedEvery}" : null, s.Parallel != null ? $"parallel {s.Parallel}" : null, s.Check != null ? $"check {s.Check}" : null, s.OnFail != null ? $"on_fail {s.OnFail}" : null }.Where(x => x != null);
            if (settings.Any()) output.WriteLine($"  settings: {string.Join(", ", settings)}");
            output.WriteLine($"  members, in the order they run ({r.Plan.Members.Count}):");
            var position = 0;
            foreach (var m in r.Plan.Members)
            {
                var entry = r.Loads.Loads.FirstOrDefault(l => l.Model == m.Model && l.Load == m.Load && l.Hash == m.LoadPlanHash);
                output.WriteLine($"  {++position,3}. {m.FullName}  [{entry?.Strategy ?? "load plan missing"}]  load plan {m.LoadPlanHash[..12]}{(m.OnFailure != null ? $"  on_failure {m.OnFailure}" : "")}");
                foreach (var i in entry?.Inputs ?? [])
                {
                    var given = m.Inputs.FirstOrDefault(g => g.Name == i.Name);
                    output.WriteLine($"         @{i.Name} ({i.Type}): {(given != null ? $"{given.Value}  (membership)" : i.Source == RefreshParameter.FromParameter ? $"{i.Value}  (parameter)" : i.Source == RefreshParameter.FromResolver ? "found when it runs  (resolver)" : "no default")}");
                }
            }
            if (r.Plan.Excluded.Count > 0)
            {
                output.WriteLine($"  left out ({r.Plan.Excluded.Count}):");
                foreach (var x in r.Plan.Excluded) output.WriteLine($"       {x.Model}{(x.Member != "-" ? "." + x.Member : "")}: {x.Reason}");
            }
        }
        output.Next(rows.Select(r => $"connection refresh {r.Connection} {r.Plan.Name} --dry-run").Take(2).ToArray());
        return CliApp.ExitOk;
    }

    // ---------------- connection monitor <plan> ----------------

    /// <summary>The runs of one plan: the last complete run and the last partial one apart, every recent run, and for each member its last success and failure.</summary>
    public static async Task<int> MonitorPlanAsync(ReadSession read, TrackingScope scope, string plan, int last, string root, string connection, TextWriter output, TextWriter error)
    {
        var known = PlanNames(root, connection);
        var runs = await AuditLog.PlanRunsAsync(read, scope, plan, Math.Max(last, 20));
        if (runs.Count == 0 && !known.Contains(plan))
        {
            error.Diag(new Diagnostic(DiagnosticCatalog.RefreshPlanUnknown, new($"connection:{connection}", 0, 0), $"`{plan}` is not a refresh plan of `{connection}`, and no run of it is recorded. {(known.Count == 0 ? "It has no compiled plans." : "Its plans: " + string.Join(", ", known) + ".")}"));
            return CliApp.ExitUsage;
        }
        var loads = await AuditLog.PlanLoadsAsync(read, scope, plan);
        var lastComplete = runs.FirstOrDefault(r => !r.Partial);
        var lastPartial = runs.FirstOrDefault(r => r.Partial);
        object? Run(AuditLog.PlanRun? r) => r == null ? null : new { id = r.Id, status = Outcome(r.Status), started_utc = Utc(r.StartedUtc), last_utc = Utc(r.LastUtc), complete = !r.Partial, selection = r.Selection, selected = r.Selected, members = r.Total };
        var members = loads.GroupBy(l => (l.Model, l.Membership)).OrderBy(g => g.Key.Model, StringComparer.Ordinal).ThenBy(g => g.Key.Membership, StringComparer.Ordinal).Select(g =>
        {
            var latest = g.First(); var ok = g.FirstOrDefault(l => l.Status == "ok"); var failed = g.FirstOrDefault(l => l.Status.StartsWith("failed", StringComparison.Ordinal));
            return (g.Key.Model, g.Key.Membership, Latest: latest, Ok: ok, Failed: failed);
        }).ToList();
        var health = await HealthAsync(read, scope, ExpectedEveryOf(root, connection, plan), runs.FirstOrDefault());
        output.Payload("connection", connection);
        output.Payload("plan", new
        {
            name = plan, health = HealthJson(health), last_complete = Run(lastComplete), last_partial = Run(lastPartial), runs = runs.Take(last).Select(Run).ToList(),
            members = members.Select(m => new { model = m.Model, member = m.Membership, last_status = m.Latest.Status, last_success_utc = m.Ok == null ? null : Utc(m.Ok.StartedUtc), last_failure_utc = m.Failed == null ? null : Utc(m.Failed.StartedUtc), last_duration_ms = m.Latest.DurationMs, last_rows = m.Latest.Rows }).ToList(),
        });
        output.WriteLine();
        output.WriteLine($"Plan {plan} on {connection}");
        foreach (var warning in HealthWarnings(plan, health, runs.FirstOrDefault())) output.WriteLine("  " + warning);
        string RunText(AuditLog.PlanRun? r) => r == null ? "none recorded" : $"{r.Id}  {Outcome(r.Status).ToUpperInvariant()}  {Utc(r.LastUtc)} UTC ({Ago(r.LastUtc)}){(r.Partial ? $"  {r.Selected} of {r.Total} member(s){(r.Selection != null ? ": " + r.Selection : "")}" : "")}";
        output.WriteLine($"  Last complete run  {RunText(lastComplete)}");
        output.WriteLine($"  Last partial run   {RunText(lastPartial)}");
        if (lastComplete == null && lastPartial != null) output.WriteLine("  This plan has only ever run in part.");
        output.WriteLine();
        output.WriteLine($"Runs (newest first, {Math.Min(runs.Count, last)})");
        foreach (var r in runs.Take(last)) output.WriteLine($"  {Utc(r.StartedUtc)}  {r.Id}  {Outcome(r.Status),-11}  {(r.Partial ? $"partial {r.Selected}/{r.Total}" : "complete")}{(r.Selection != null ? "  " + r.Selection : "")}");
        if (runs.Count == 0) output.WriteLine("  none");
        output.WriteLine();
        output.WriteLine($"Members ({members.Count})");
        foreach (var m in members)
            output.WriteLine($"  {m.Model}.{m.Membership}  last {m.Latest.Status}{(m.Latest.DurationMs != null ? $" in {m.Latest.DurationMs} ms" : "")}{(m.Latest.Rows != null ? $", {m.Latest.Rows} row(s)" : "")}  |  success {(m.Ok == null ? "never" : Utc(m.Ok.StartedUtc))}  |  failure {(m.Failed == null ? "none" : Utc(m.Failed.StartedUtc))}");
        if (members.Count == 0) output.WriteLine("  none");
        output.Next($"connection refresh {connection} {plan}");
        return runs.Count > 0 && runs[0].Status != "completed" ? CliApp.ExitFindings : CliApp.ExitOk;          // overdue and slow are warnings: they do not change the exit code
    }

    // ---------------- connection status ----------------

    /// <summary>One line per plan (or the one asked for): the last run, how it ended and its age; complete and partial runs apart. Null when the tracking tables cannot say.</summary>
    public static List<(string Plan, AuditLog.PlanRun? Last, AuditLog.PlanRun? LastComplete, PlanHealth Health)>? PlanLines(ProjectConfig config, string connection, string root, string? only, Func<string, string?> env, out string? why)
    {
        why = null;
        var names = PlanNames(root, connection).Where(n => only == null || n == only).ToList();
        if (names.Count == 0) return [];
        if (config.TrackingOf(connection).Target is not { } t) { why = "unknown: nothing is tracked"; return null; }
        var (login, _) = LoginSettings.FromEnvironment(t.Connection, t.Engine, Login.Read, env);
        if (login == null) { why = "unknown: the tracking connection's read login is not set"; return null; }
        try
        {
            var scope = new TrackingScope(t.Engine, t.SchemaName, connection);
            return Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login);
                var all = await AuditLog.PlanRunsAsync(read, scope, null, 200);
                var lines = new List<(string, AuditLog.PlanRun?, AuditLog.PlanRun?, PlanHealth)>();
                foreach (var n in names)
                {
                    var last = all.FirstOrDefault(r => r.Plan == n);
                    lines.Add((n, last, all.FirstOrDefault(r => r.Plan == n && !r.Partial), await HealthAsync(read, scope, ExpectedEveryOf(root, connection, n), last)));
                }
                return lines;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { why = $"unknown: the runs could not be read ({ex.GetType().Name}{CliApp.DriverNumber(ex)})"; return null; }
    }

    public static void PrintPlanLines(TextWriter output, List<(string Plan, AuditLog.PlanRun? Last, AuditLog.PlanRun? LastComplete, PlanHealth Health)>? lines, string? why)
    {
        if (lines == null) { output.WriteLine($"Plans      {why}"); return; }
        object Line((string Plan, AuditLog.PlanRun? Last, AuditLog.PlanRun? LastComplete, PlanHealth Health) l) => new
        {
            plan = l.Plan, last_run = l.Last == null ? null : l.Last.Id, outcome = l.Last == null ? "never run" : Outcome(l.Last.Status), utc = l.Last == null ? null : Utc(l.Last.LastUtc),
            complete = l.Last == null ? (bool?)null : !l.Last.Partial, last_complete_utc = l.LastComplete == null ? null : Utc(l.LastComplete.LastUtc), health = HealthJson(l.Health),
        };
        output.Payload("plans", lines.Select(Line).ToList());
        foreach (var l in lines)
        {
            output.WriteLine($"Plan {l.Plan,-10} {(l.Last == null ? "never run" : $"{l.Last.Id}  {Outcome(l.Last.Status).ToUpperInvariant()}  {Utc(l.Last.LastUtc)} UTC ({Ago(l.Last.LastUtc)}){(l.Last.Partial ? $"  PARTIAL {l.Last.Selected}/{l.Last.Total}" : "")}{(l.LastComplete == null && l.Last != null ? "  (never run in full)" : "")}")}");
            foreach (var warning in HealthWarnings(l.Plan, l.Health, l.Last)) output.WriteLine("           " + warning);
        }
    }
}
