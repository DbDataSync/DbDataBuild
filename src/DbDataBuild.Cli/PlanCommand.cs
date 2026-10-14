using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection deploy` and `dbdatabuild connection status` (DESIGN.md 9.1, 10.1, 11). Effect class: target read-only; `plan` also writes plan files under the project.
/// Planning refuses a project that does not validate, asks every open question (interactively, or from `--answers`, never by default), and writes a plan
/// only when nothing is open. Blocked models are reported, not planned, and everything downstream of them is skipped.
/// </summary>
internal static class PlanCommand
{
    public const string PlansDir = "plans";

    /// <summary>Where a plan goes when the project keeps deploy plans only for the moment (`plans.deploy.keep: ephemeral`): inside the folder git ignores, and removed once the plan has been applied.</summary>
    public const string EphemeralPlansDir = ".dbdatabuild/plans";
    private const int MaxRounds = 12;

    public static int Plan(CommandSpec spec, string root, string? targetArg, string[] models, FileInfo? answersFile, bool acceptInferred, DirectoryInfo? outDir, string[] ops, string[] backfillArgs, string[] paramArgs,
        TextWriter output, TextWriter error, TextReader input, bool interactive, Func<string, string?> env, Action<string>? written = null, bool hintApply = true, IReadOnlyList<InputPin>? pins = null)
    {
        // ---- answers file first: a bad file is a usage problem and must not need a database ----
        AnswerFile? file = null;
        var answersPath = answersFile?.FullName ?? QuestionResolver.NoAnswersFile;
        if (answersFile != null)
        {
            var fileDiags = new List<Diagnostic>();
            file = AnswerFileLoader.Load(File.ReadAllText(answersFile.FullName), answersFile.Name, fileDiags);
            if (file == null)
            {
                foreach (var d in fileDiags) error.Diag(d);
                return CliApp.ExitFindings;
            }
        }
        // --param model.operation.parameter=value is the answer to that parameter's question, so a scheduled backfill needs no file
        if (!ParseParams(paramArgs, file, error, out var paramAnswers)) return CliApp.ExitUsage;
        if (paramAnswers.Count > 0) { file = new AnswerFile([.. file?.Answers ?? [], .. paramAnswers]); if (answersFile == null) answersPath = "--param"; }

        if (!ParseOps(ops, "--op", error, out var operations) || !ParseOps(backfillArgs, "--backfill", error, out var backfillOps)) return CliApp.ExitUsage;
        foreach (var (model, op) in backfillOps)
            if (!operations.TryAdd(model, op) && operations[model] != op) { error.WriteLine($"`{model}` is given two different operations (--op {operations[model]}, --backfill {op})."); return CliApp.ExitUsage; }

        var (session, exit) = PlanningSession.Prepare(spec, root, targetArg, models, output, error, env, operations, backfillOps.Keys.ToHashSet(StringComparer.Ordinal), pins);
        if (session == null) return exit;
        // ---- ask, plan again, until nothing is open ----
        IPrompter? prompter = interactive ? new ConsolePrompter(input, output) : null;
        var answers = new List<ResolvedAnswer>();
        var asked = new HashSet<string>();
        PlanResult result;
        var round = 0;
        while (true)
        {
            result = Planner.Plan(session.Input, answers);
            if (result.Complete) break;
            if (++round > MaxRounds) throw new InvalidOperationException("Planning did not settle: questions keep changing between rounds. This is a tool bug.");
            var fresh = result.Questions.Where(q => !answers.Any(a => a.QuestionId == q.Id)).ToList();
            var resolution = QuestionResolver.Resolve(fresh, file, answersPath, prompter, new ResolveOptions(acceptInferred, WarnOnUnknownAnswers: false));
            foreach (var d in resolution.Diagnostics) error.Diag(d);
            if (!resolution.Complete)
            {
                output.Payload("open_questions", resolution.Unanswered.Select(QuestionJson).ToList());
                output.WriteLine($"Nothing was planned: {resolution.Unanswered.Count} question(s) are open.");
                if (answersFile == null && paramAnswers.Count == 0 && !interactive && !acceptInferred)
                    output.WriteLine("This run is not interactive and has no --answers file: answer in a terminal, or give the answers with --answers or --param.");
                return CliApp.ExitFindings;
            }
            answers.AddRange(resolution.Answers);
            foreach (var q in fresh) asked.Add(q.Id);
        }
        if (file != null)
            foreach (var a in file.Answers.Where(a => !result.UsedAnswers.Any(u => u.QuestionId == a.QuestionId)))
                error.Diag(new Diagnostic(DiagnosticCatalog.AnswerForUnknownQuestion, a.Location, $"The answers file has an answer for `{a.QuestionId}`, which was not asked in this run."));

        foreach (var d in result.Skipped) error.Diag(d);
        foreach (var d in result.Blocks) error.Diag(d);

        if (result.Steps.Count == 0)
        {
            output.WriteLine(result.Blocks.Count > 0
                ? $"Nothing was planned: {result.Blocks.Count} blocked model(s) and {result.Skipped.Count} skipped."
                : "Nothing to do: every model matches its target and no load applies.");
            return result.Blocks.Count > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
        }

        output.Payload("connection", session.Target);
        output.Payload("questions_asked", asked.Count);
        output.Payload("answers", result.UsedAnswers);
        output.Payload("noticed", result.Noticed);
        output.Payload("blocked", result.Blocks.Select(b => b.Code).ToList());
        output.Payload("skipped", result.Skipped.Select(b => b.Code).ToList());

        // ---- build, write ----
        var (commit, dirty) = GitInfo.Read(root);
        var draft = new DbDataBuild.Planning.Plan("", session.Target, commit, dirty, ProductInfo.Version, result.Bases, result.UsedAnswers, result.Steps, result.Noticed,
            RefreshPlanBuilder.ProjectHash(session.Context, session.Target, RefreshPlanBuilder.ModelsOf(session.Context, session.Target)));
        var plan = draft with { Id = PlanDocument.CreateId(DateOnly.FromDateTime(DateTime.UtcNow), draft) };
        var keep = session.Context.Config.LifecycleOf(session.Target).Deploy.Keep;
        if (keep == PlanKeep.Database && outDir == null) return StoreInDatabase(spec, root, session, plan, result, written, hintApply, output, error, env);
        var dir = outDir?.FullName ?? Path.Combine(root, keep == PlanKeep.Ephemeral ? EphemeralPlansDir : PlansDir, session.Target);
        var yamlPath = Path.Combine(dir, plan.Id + ".plan.yml");
        var mdPath = Path.Combine(dir, plan.Id + ".plan.md");
        Directory.CreateDirectory(dir);
        WriteAtomic(yamlPath, PlanDocument.Serialize(plan));
        WriteAtomic(mdPath, PlanReport.Markdown(plan, result.Blocks, result.Skipped));

        var risky = plan.Steps.Count(s => s.Risk == RiskClass.Risky);
        var destructive = plan.Steps.Count(s => s.Risk == RiskClass.Destructive);
        output.Payload("plan", plan);
        output.Payload("files", new { plan = Path.GetRelativePath(root, yamlPath).Replace('\\', '/'), report = Path.GetRelativePath(root, mdPath).Replace('\\', '/') });
        output.WriteLine($"Plan {plan.Id}: {plan.Steps.Count} step(s) ({plan.Steps.Count(s => s.Type == StepType.Ddl)} ddl, {plan.Steps.Count(s => s.Type == StepType.Load)} load, {plan.Steps.Count(s => s.Type == StepType.Backfill)} backfill, {plan.Steps.Count(s => s.Type == StepType.Hook)} hook, {plan.Steps.Count(s => s.Type == StepType.Track)} track); {risky} risky, {destructive} destructive.");
        // the inputs that were computed for this plan: apply computes them again, and stops if any is another value
        var computed = plan.Steps.Where(s => s.Inputs != null).SelectMany(s => s.Inputs!.Where(i => i.Source != "group").Select(i => (Step: s, Input: i))).ToList();
        foreach (var (step, i) in computed)
            output.WriteLine($"  input {step.Object}.{step.Operation ?? "default"} `{i.Name}` = {i.Value ?? "NULL"}  [{i.Source}{(i.Origin != null ? " at " + i.Origin : "")}]");
        foreach (var step in plan.Steps.Where(s => s.HasResolver))
            output.WriteLine($"  input {step.Object}.{step.Operation ?? "default"} `watermark` = {step.ResolverResult ?? "NULL"}  [resolver:target]");
        if (computed.Any(c => c.Input.IsComputed) || plan.Steps.Any(s => s.HasResolver)) output.WriteLine("  Apply computes these again; if one is another value then it stops, shows both and lets you approve the new one (--input pins a value now).");
        output.Payload("inputs", computed.Select(c => new { model = c.Step.Object, operation = c.Step.Operation ?? "default", name = c.Input.Name, type = c.Input.Type, source = c.Input.Source, value = c.Input.Value, origin = c.Input.Origin }).ToList());
        output.WriteLine($"  report: {Path.GetRelativePath(root, mdPath)}");
        output.WriteLine($"  plan:   {Path.GetRelativePath(root, yamlPath)}");
        if (result.Blocks.Count + result.Skipped.Count > 0)
            output.WriteLine($"  NOT planned: {result.Blocks.Count} blocked, {result.Skipped.Count} skipped (see above and the report).");
        written?.Invoke(yamlPath);
        if (hintApply)
        {
            output.WriteLine("Read the report before applying the plan.");
            output.Next($"connection deploy --apply-plan {Path.GetRelativePath(root, yamlPath).Replace('\\', '/')}");
        }
        return result.Blocks.Count > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    /// <summary>
    /// `plans.deploy.keep: database`: the plan is kept in the tracking tables and nowhere else, to be applied by id (by another person or a job that has the write login). No file is written. Needs the tracking
    /// connection's write login: that is where the plan goes.
    /// </summary>
    private static int StoreInDatabase(CommandSpec spec, string root, PlanningSession session, DbDataBuild.Planning.Plan plan, PlanResult result, Action<string>? written, bool hintApply, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var config = session.Context.Config;
        var tracking = CommandTracking.Require(config, config.Connections[session.Target], env, needWrite: true, error, spec.Name);
        if (tracking?.Write == null) { output.WriteLine("Nothing was planned into the database: `plans.deploy.keep` is `database`, and the tracking connection's write login is needed to keep the plan there."); return CliApp.ExitFindings; }
        var text = PlanDocument.Serialize(plan);
        try
        {
            Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(tracking.Read);
                if ((await TrackingStore.StatusAsync(read, tracking.Scope.Engine, tracking.Scope.SchemaName)).AsDiagnostic(tracking.Scope.SchemaName) is { } notReady) throw new GateRefusedException(notReady);
                var runId = Guid.NewGuid();
                using var log = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), "store-plan", runId);
                await using var gate = await MutationGate.OpenAsync(tracking.Write, spec.Name, StatementKind.Tracking, log, runId);
                await AuditLog.StorePlanAsync(gate, tracking.Scope, "plan:store", plan.Id, PlanDocument.ContentHash(plan), text, tracking.Write.User ?? Environment.UserName);
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }
        output.Payload("plan", plan);
        output.Payload("stored_plan", plan.Id);
        output.WriteLine($"Plan {plan.Id}: {plan.Steps.Count} step(s) ({plan.Steps.Count(s => s.Type == StepType.Ddl)} ddl, {plan.Steps.Count(s => s.Type == StepType.Load)} load, {plan.Steps.Count(s => s.Type == StepType.Backfill)} backfill, {plan.Steps.Count(s => s.Type == StepType.Hook)} hook), kept in the tracking tables of `{tracking.Target.Connection}` (`plans.deploy.keep: database`); no file was written.");
        if (result.Blocks.Count + result.Skipped.Count > 0) output.WriteLine($"  NOT planned: {result.Blocks.Count} blocked, {result.Skipped.Count} skipped (see above).");
        written?.Invoke(plan.Id);
        if (hintApply) output.Next($"connection deploy --apply-plan {plan.Id}");
        return result.Blocks.Count > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    /// <summary>`model=operation` pairs. A model may be named once.</summary>
    /// <summary>`model.operation.parameter=value`: the model name has dots of its own, the operation and parameter names do not, so the last two parts before `=` are those.</summary>
    internal static bool ParseParams(string[] args, AnswerFile? file, TextWriter error, out List<Answer> answers)
    {
        answers = [];
        foreach (var a in args)
        {
            var eq = a.IndexOf('=');
            var parts = eq <= 0 ? [] : a[..eq].Split('.');
            if (parts.Length < 3 || parts.Any(p => p.Length == 0) || eq == a.Length - 1)
            {
                error.WriteLine($"--param takes model.operation.parameter=value (for example marts.fct_events.reload_period.start=2024-01-01), not `{a}`.");
                return false;
            }
            var id = QuestionIds.Param(string.Join('.', parts[..^2]), parts[^2], parts[^1]);
            if (answers.Any(x => x.QuestionId == id) || file?.Answers.Any(x => x.QuestionId == id) == true)
            {
                error.WriteLine($"The parameter in `{a}` is answered more than once (--param and the answers file both name {id}).");
                return false;
            }
            answers.Add(new Answer(id, "provide", a[(eq + 1)..], null, false, new SourceLocation("--param", 0, 0)));
        }
        return true;
    }

    private static bool ParseOps(string[] args, string flag, TextWriter error, out Dictionary<string, string> result)
    {
        result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in args)
        {
            var i = a.IndexOf('=');
            if (i <= 0 || i == a.Length - 1) { error.WriteLine($"{flag} takes model=operation (for example marts.fct_orders=reload_period), not `{a}`."); return false; }
            if (!result.TryAdd(a[..i], a[(i + 1)..])) { error.WriteLine($"{flag} names `{a[..i]}` more than once."); return false; }
        }
        return true;
    }

    /// <summary>`check`: the findings a plan would act on, without asking anything or writing anything.</summary>
    /// <summary>The newest deploy and the newest refresh of a connection, from the tracking tables; or why they are not known.</summary>
    private static (AuditLog.EventRow? Deploy, AuditLog.EventRow? Refresh, string? Why) LastEvents(ProjectConfig config, string connection, Func<string, string?> env)
    {
        if (config.TrackingOf(connection).Target is not { } t) return (null, null, "unknown: nothing is tracked");
        var (login, _) = LoginSettings.FromEnvironment(t.Connection, t.Engine, DbDataBuild.Execution.Login.Read, env);
        if (login == null) return (null, null, "unknown: the tracking connection's read login is not set");
        try
        {
            return Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login);
                var events = await AuditLog.EventsAsync(read, new TrackingScope(t.Engine, t.SchemaName, connection), 20);
                return (events.FirstOrDefault(e => e.Lane == "deploy"), events.FirstOrDefault(e => e.Lane == "refresh"), (string?)null);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, null, $"unknown: the events could not be read ({ex.GetType().Name}{CliApp.DriverNumber(ex)})"); }
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalDays} d ago";
    }

    public static int Check(CommandSpec spec, string root, string? targetArg, string[] models, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        // the first argument is a refresh plan when it names one compiled for the connection
        string? onlyPlan = null;
        if (models.Length > 0 && RefreshViews.PlanNames(root, targetArg ?? ProjectConfigLoader.LoadFromProject(root, []).DefaultConnections.FirstOrDefault() ?? "").Contains(models[0])) { onlyPlan = models[0]; models = models[1..]; }
        var (session, exit) = PlanningSession.Prepare(spec, root, targetArg, models, output, error, env);
        if (session == null) return exit;
        var result = Planner.Plan(session.Input, []);
        foreach (var d in result.Blocks.Concat(result.Skipped)) error.Diag(d);

        // the string profile, against what the catalog says (DESIGN.md 9.4)
        var liveCollation = new List<Diagnostic>();
        foreach (var m in session.Input.Models)
            if (session.Input.Live.TryGetValue(m.Definition.Name, out var shape))
                liveCollation.AddRange(CollationChecker.CheckLive(session.Context.Config.ForConnection(session.Target), session.Context.Config.EngineOf(session.Target) ?? session.Target, m.Definition,
                    shape.Columns.Where(c => c.Type is "nvarchar" or "varchar" or "character varying" or "char" or "text").Select(c => (c.Name, c.Collation))));
        foreach (var d in liveCollation) error.Diag(d);

        // the columns declared `trimmed`, counted on the connection (no value is read)
        var trimmedFindings = new List<Diagnostic>();
        var (checkLogin, _) = LoginSettings.FromEnvironment(session.Target, session.Context.Config.EngineOf(session.Target) ?? session.Target, DbDataBuild.Execution.Login.Read, env);
        if (checkLogin != null) trimmedFindings.AddRange(TrimmedCheck.Run(session.Context, session.Target, checkLogin));
        foreach (var d in trimmedFindings) error.Diag(d);

        var stepsNow = result.Steps;
        output.Payload("connection", session.Target);
        output.Payload("objects", result.Bases.OrderBy(b => b.Object, StringComparer.Ordinal).Select(b => new { name = b.Object, state = b.State, live_shape_hash = b.LiveShapeHash, recorded_shape_hash = b.RecordedShapeHash }).ToList());
        output.Payload("preview", new
        {
            steps = stepsNow.Count, risky = stepsNow.Count(s => s.Risk == RiskClass.Risky), destructive = stepsNow.Count(s => s.Risk == RiskClass.Destructive),
            questions = result.Questions.Select(QuestionJson).ToList(), blocked = result.Blocks.Select(b => b.Code).ToList(), skipped = result.Skipped.Select(b => b.Code).ToList(),
        });
        output.Payload("noticed", result.Noticed);

        // ---- the landing view: structure, the last deploy and refresh, and the policy in force ----
        var drifted = result.Bases.Count(b => b.State == ObjectState.OutOfBand);
        // a plan always carries the routine loads too; what is pending in the structure is the rest
        var structureSteps = stepsNow.Where(s => s.Type is not (StepType.Load or StepType.Backfill)).ToList();
        var loadSteps = stepsNow.Count - structureSteps.Count;
        var risky = structureSteps.Count(s => s.Risk == RiskClass.Risky);
        var destructive = structureSteps.Count(s => s.Risk == RiskClass.Destructive);
        var structure = result.Blocks.Count > 0 || drifted > 0 ? "attention" : structureSteps.Count > 0 ? "pending" : "in_sync";
        var structureText = structure switch
        {
            "attention" => $"needs attention: {result.Blocks.Count} blocked model(s), {drifted} object(s) changed outside the tool",
            "pending" => $"pending: a deploy would run {structureSteps.Count} step(s) ({risky} risky, {destructive} destructive) and {loadSteps} load(s)",
            _ => $"in sync: a deploy would change no structure{(loadSteps > 0 ? $" (it would run {loadSteps} routine load(s))" : "")}",
        };
        var (lastDeploy, lastRefresh, noEvents) = LastEvents(session.Context.Config, session.Target, env);
        string EventText(AuditLog.EventRow? e, string none) => e == null ? none : $"{e.Id}  {e.Status.ToUpperInvariant()}  {e.LastUtc:yyyy-MM-dd HH:mm} UTC ({Ago(e.LastUtc)})";
        var policy = session.Context.Config.LifecycleOf(session.Target);
        output.WriteLine($"Structure  {structureText}");
        output.WriteLine($"Deploy     {EventText(lastDeploy, noEvents ?? "none recorded")}");
        output.WriteLine($"Refresh    {EventText(lastRefresh, noEvents ?? "none recorded")}{(lastRefresh is { Status: "failed" or "stopped" or "completed_with_failures" } ? "  (not every load finished: `connection refresh <connection> <plan> --only-failed` repeats the rest)" : "")}");
        output.WriteLine($"Policy     {policy.Describe()}");
        var planLines = RefreshViews.PlanLines(session.Context.Config, session.Target, root, onlyPlan, env, out var planWhy);
        if (planLines is not { Count: 0 }) RefreshViews.PrintPlanLines(output, planLines, planWhy);
        output.WriteLine();
        object? Event(AuditLog.EventRow? e) => e == null ? null : new { id = e.Id, status = e.Status, utc = e.LastUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) };
        output.Payload("summary", new { structure, steps = structureSteps.Count, loads = loadSteps, risky, destructive, blocked = result.Blocks.Count, changed_outside_the_tool = drifted, last_deploy = Event(lastDeploy), last_refresh = Event(lastRefresh), policy = policy.Describe() });

        var width = Math.Max(6, result.Bases.Count == 0 ? 0 : result.Bases.Max(b => b.Object.Length));
        output.WriteLine($"{"object".PadRight(width)}  state");
        foreach (var b in result.Bases.OrderBy(b => b.Object, StringComparer.Ordinal))
            output.WriteLine($"{b.Object.PadRight(width)}  {b.State switch { ObjectState.Missing => "missing (a plan would create it)", ObjectState.Untracked => "exists, not tracked (a plan would ask to adopt it)", ObjectState.InSync => "in sync", _ => "CHANGED OUTSIDE THE TOOL" }}");
        output.WriteLine();
        var steps = result.Steps;
        output.WriteLine($"A plan now would have {steps.Count} step(s) ({steps.Count(s => s.Risk == RiskClass.Risky)} risky, {steps.Count(s => s.Risk == RiskClass.Destructive)} destructive) and ask {result.Questions.Count} question(s) first; {result.Blocks.Count} blocked, {result.Skipped.Count} skipped.");
        var attention = result.Blocks.Count > 0 || result.Bases.Any(b => b.State == ObjectState.OutOfBand) || liveCollation.Any(d => d.Severity == Severity.Error) || trimmedFindings.Count > 0;
        output.Next(attention || structureSteps.Count > 0 ? "connection deploy" : "connection refresh", "connection monitor");
        return attention ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    internal static object QuestionJson(Question q) => new
    {
        id = q.Id, prompt = q.Prompt, context = q.Context,
        options = q.Options.Select(o => new { key = o.Key, description = o.Description, consequence = o.Consequence, takes_value = o.TakesValue, value_hint = o.ValueHint }).ToList(),
        proposal = q.Proposal == null ? null : new { option = q.Proposal.OptionKey, value = q.Proposal.Value, certainty = q.Proposal.Certainty.ToString().ToLowerInvariant(), evidence = q.Proposal.Evidence },
    };

    internal static void WriteAtomic(string path, string content)
    {
        var temp = path + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(content));
        File.Move(temp, path, overwrite: true);
    }
}
