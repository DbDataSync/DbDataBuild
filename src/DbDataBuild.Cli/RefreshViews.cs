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

    public static int Show(CommandSpec spec, string root, string? planArg, string? connectionArg, TextWriter output, TextWriter error, bool resolve = false)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (resolve && planArg == null) { error.WriteLine("--resolve evaluates the expression resolvers of the loads of one plan: name the plan (`project show refresh <plan> --resolve`)."); return CliApp.ExitUsage; }
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

        // `--resolve`: one instant for the whole view, the zone of the connection; only the resolvers that need no database run
        var started = RefreshCommand.Now();
        var instant = new DateTimeOffset(started.UtcDateTime.Ticks - started.UtcDateTime.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        Dictionary<string, InputResolutionResult> Resolved(string connection, RefreshPlanFile plan, LoadsFile loads)
        {
            var result = new Dictionary<string, InputResolutionResult>(StringComparer.Ordinal);
            if (!resolve) return result;
            foreach (var m in plan.Members)
            {
                var entry = loads.Loads.FirstOrDefault(l => l.Model == m.Model && l.Load == m.Load && l.Hash == m.LoadPlanHash);
                if (entry == null) continue;
                var given = m.Inputs.ToDictionary(g => g.Name, g => (g.Value, "membership"), StringComparer.Ordinal);
                result[m.FullName] = InputResolution.ResolveAsync(m.FullName, entry.Inputs.Where(i => i.Source != RefreshParameter.FromResolver).ToList(), given, new InputRunContext(instant, config.TimezoneOf(connection), plan.Name, connection), null, _ => null, CancellationToken.None).GetAwaiter().GetResult();
            }
            return result;
        }
        var resolvedByPlan = rows.ToDictionary(r => (r.Connection, r.Plan.Name), r => Resolved(r.Connection, r.Plan, r.Loads));
        var graphs = rows.ToDictionary(r => (r.Connection, r.Plan.Name), r => GraphOf(r.Plan, r.Loads));

        string Describe(RefreshParameter i, RefreshInputValue? given) =>
            given != null ? $"{given.Value}  (membership)"
            : i.Source == RefreshParameter.FromParameter ? $"{i.Value}  (parameter)"
            : i.Source == RefreshParameter.FromDefault ? $"{i.Value}  (default)"
            : i.Source == RefreshParameter.FromResolver ? "found when it runs  (resolver)"
            : i.Source == RefreshParameter.Computed && i.Resolver is { } r ? $"found when it runs  (resolver: {r.Kind} `{r.Text.ReplaceLineEndings(" ")}`{(i.Provides.Count > 0 ? ", provides " + string.Join(", ", i.Provides) : "")}){(i.OnNull != "fail" ? $"  on_null {i.OnNull}" : "")}"
            : i.Source == RefreshParameter.Provided ? $"found when it runs  (provided by `{i.ProvidedBy}`){(i.OnNull != "fail" ? $"  on_null {i.OnNull}" : "")}"
            : "no default";

        // the dependency graph the loads file carries, among the models of one plan: what each member waits for, with the model kind of each side and how many steps away it is (1 is direct)
        static List<LoadDependency>? GraphOf(RefreshPlanFile plan, LoadsFile loads)
        {
            if (loads.Dependencies == null) return null;
            var models = plan.Members.Select(m => m.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return loads.Dependencies.Where(d => models.Contains(d.Model) && models.Contains(d.DependsOn)).ToList();
        }
        object Member(string connection, string plan, RefreshMember m, LoadsFile loads, List<LoadDependency>? graph)
        {
            var entry = loads.Loads.FirstOrDefault(l => l.Model == m.Model && l.Load == m.Load && l.Hash == m.LoadPlanHash);
            resolvedByPlan[(connection, plan)].TryGetValue(m.FullName, out var res);
            var inputs = (entry?.Inputs ?? []).Where(i => i.Provides.Count == 0).Select(i =>
            {
                var given = m.Inputs.FirstOrDefault(g => g.Name == i.Name);
                var found = res?.Inputs.FirstOrDefault(x => x.Name == i.Name);
                return new
                {
                    name = i.Name, type = i.Type, source = given != null ? "membership" : i.Source, value = given != null ? given.Value : i.Value,
                    resolver = i.Resolver != null && given == null ? i.Resolver.Kind : i.ProvidedBy != null && given == null ? "provided" : null,
                    resolver_text = i.Resolver?.Text,
                    free = i.Free ? (bool?)true : null, on_null = i.OnNull == "fail" ? null : i.OnNull,
                    resolved_value = resolve && found != null && found.IsComputed ? found.Value : null, resolved_source = resolve && found != null ? found.Source : null,
                };
            }).ToList();
            var waits = graph?.Where(d => string.Equals(d.Model, m.Model, StringComparison.OrdinalIgnoreCase)).Select(d => new { depends_on = d.DependsOn, depends_on_kind = d.DependsOnKind, depth = d.Depth }).ToList();
            return new { member = m.Name, model = m.Model, load = m.Load, load_plan = m.LoadPlanHash, strategy = entry?.Strategy, on_failure = m.OnFailure, inputs, waits_for = waits, resolve_error = res?.Failure, not_resolved = res is { NotResolved.Count: > 0 } ? res.NotResolved : null };
        }
        if (resolve) output.Payload("resolved_at", instant.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        output.Payload("plans", rows.Select(r => new
        {
            connection = r.Connection, name = r.Plan.Name, title = r.Plan.Info.Title, description = r.Plan.Info.Description, hash = r.Plan.Hash, members = r.Plan.Members.Count,
            settings = new { on_failure = r.Plan.Settings.OnFailure, on_busy = r.Plan.Settings.OnBusy, expected_every = r.Plan.Settings.ExpectedEvery, parallel = r.Plan.Settings.Parallel, check = r.Plan.Settings.Check, on_fail = r.Plan.Settings.OnFail },
            load_members = planArg == null ? null : r.Plan.Members.Select(m => Member(r.Connection, r.Plan.Name, m, r.Loads, GraphOf(r.Plan, r.Loads))).ToList(),
            dependencies = GraphOf(r.Plan, r.Loads)?.Select(d => new { model = d.Model, model_kind = d.ModelKind, depends_on = d.DependsOn, depends_on_kind = d.DependsOnKind, depth = d.Depth }).ToList(),
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
            output.WriteLine($"Plan {r.Plan.Name} on {r.Connection}  (hash {r.Plan.Hash[..12]}){(resolve ? $"  resolved at {instant.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC, clock in {config.TimezoneOf(r.Connection)}" : "")}");
            if (r.Plan.Info.Title != null) output.WriteLine($"  {r.Plan.Info.Title}");
            if (r.Plan.Info.Description != null) output.WriteLine($"  {r.Plan.Info.Description}");
            var s = r.Plan.Settings;
            var settings = new[] { s.OnFailure != null ? $"on_failure {s.OnFailure}" : null, s.OnBusy != null ? $"on_busy {s.OnBusy}" : null, s.ExpectedEvery != null ? $"expected_every {s.ExpectedEvery}" : null, s.Parallel != null ? $"parallel {s.Parallel}" : null, s.Check != null ? $"check {s.Check}" : null, s.OnFail != null ? $"on_fail {s.OnFail}" : null }.Where(x => x != null);
            if (settings.Any()) output.WriteLine($"  settings: {string.Join(", ", settings)}");
            output.WriteLine(graphs[(r.Connection, r.Plan.Name)] is { } shown
                ? $"  dependency graph of the loads file: {shown.Count} edge(s) among these members (direct and through others; a member waits for what it reads)"
                : "  the loads file carries no dependency graph (compiled by an older version): its loads run one at a time, in plan order; `project compile` writes it");
            output.WriteLine($"  members, in the order they run ({r.Plan.Members.Count}):");
            var position = 0;
            foreach (var m in r.Plan.Members)
            {
                var entry = r.Loads.Loads.FirstOrDefault(l => l.Model == m.Model && l.Load == m.Load && l.Hash == m.LoadPlanHash);
                output.WriteLine($"  {++position,3}. {m.FullName}  [{entry?.Strategy ?? "load plan missing"}]  load plan {m.LoadPlanHash[..12]}{(m.OnFailure != null ? $"  on_failure {m.OnFailure}" : "")}");
                resolvedByPlan[(r.Connection, r.Plan.Name)].TryGetValue(m.FullName, out var resolution);
                foreach (var i in (entry?.Inputs ?? []).Where(i => i.Provides.Count == 0))
                {
                    var given = m.Inputs.FirstOrDefault(g => g.Name == i.Name);
                    output.WriteLine($"         {(i.Free ? "" : "@")}{i.Name} ({i.Type}): {Describe(i, given)}");
                    if (resolution?.Inputs.FirstOrDefault(x => x.Name == i.Name) is { IsComputed: true } now) output.WriteLine($"             now: {now.Value ?? "NULL"}  ({now.Source})");
                    else if (resolution != null && resolution.NotResolved.Contains(i.Name)) output.WriteLine("             not resolved here: it reads a database (`connection refresh --dry-run` runs it)");
                }
                if (resolution?.Failure != null) output.WriteLine($"         {resolution.Failure}");
                if (graphs[(r.Connection, r.Plan.Name)] is { } graph && graph.Where(d => string.Equals(d.Model, m.Model, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } waits)
                    output.WriteLine($"         waits for: {string.Join(", ", waits.Select(d => $"{d.DependsOn} [{d.DependsOnKind}]{(d.Depth > 1 ? $" ({d.Depth} steps)" : "")}"))}");
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
