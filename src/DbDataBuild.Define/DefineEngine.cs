using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Define;

/// <summary>One model `define` was asked to look at: its query, and its definition if it has one.</summary>
/// <param name="ModelName">The name the path implies (models/marts/fct_orders.sql is marts.fct_orders).</param>
/// <param name="ExistingText">The definition file's text, or null when there is no file.</param>
/// <param name="Existing">The loaded definition; null when there is no file, or the file does not load (then <paramref name="ExistingProblems"/> says why).</param>
public sealed record DefineTarget(
    string ModelName, string DefinitionFile, string QueryFile, string Sql,
    string? ExistingText, ModelDefinition? Existing, IReadOnlyList<Diagnostic> ExistingProblems, MacroSupport? Macros = null, QueryHead? Head = null)
{
    public bool HasDefinitionFile => ExistingText != null;
}

public enum DefineStatus
{
    /// <summary>The definition already matches the query. Nothing to do.</summary>
    InSync,
    /// <summary>Nothing was changed because every difference was deliberately kept (`keep_declared`); `define --check` will still report them.</summary>
    Unchanged,
    Created,
    Updated,
    /// <summary>Questions are open. Nothing is written for this model.</summary>
    Incomplete,
    /// <summary>An error stopped this model (see its diagnostics).</summary>
    Failed,
    /// <summary>Not defined: by answer (`skip_model`), or because an upstream model it needs was not.</summary>
    Skipped,
}

public sealed record DefineOutcome(
    DefineTarget Target, DefineStatus Status, string? NewText, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ResolvedAnswer> Answers, IReadOnlyList<string> Notes, IReadOnlyList<Question> Unanswered)
{
    public bool Changed => Status is DefineStatus.Created or DefineStatus.Updated;
    public bool Done => Status is DefineStatus.InSync or DefineStatus.Unchanged or DefineStatus.Created or DefineStatus.Updated;
}

public sealed record DefineRun(IReadOnlyList<DefineOutcome> Outcomes, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Complete => Outcomes.All(o => o.Done) && Diagnostics.All(d => d.Severity != Severity.Error);
}

/// <summary>
/// Generates and maintains model definitions from the query body plus a guided walkthrough (DESIGN.md 6.5). Offline and repo-only: it
/// reads files and asks DuckDB to describe queries, never connects to a target, and produces text for definition files only.
/// </summary>
public sealed class DefineEngine(ModelGraph graph, ProjectConfig config, MatrixLinter? linter = null)
{
    // ---- check: is each definition in sync with its query? (writes nothing, asks nothing) ----

    public IReadOnlyList<Diagnostic> Check(IReadOnlyList<DefineTarget> targets)
    {
        var diags = new List<Diagnostic>();
        foreach (var t in targets.OrderBy(t => t.DefinitionFile, StringComparer.Ordinal))
        {
            if (!t.HasDefinitionFile)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.DefinitionOutOfSync, new(t.QueryFile, 0, 0), $"`{t.QueryFile}` has no definition file `{t.DefinitionFile}`."));
                continue;
            }
            if (t.Existing == null) { diags.AddRange(t.ExistingProblems); continue; }

            var (inference, problems) = ModelInference.Infer(t.QueryFile, t.Sql, graph, t.Macros);
            if (inference == null) { diags.AddRange(problems); continue; }

            var diff = ColumnDiff.Compute(t.Existing.Columns, inference);
            foreach (var d in diff.Removed)
                diags.Add(new Diagnostic(DiagnosticCatalog.DefinitionOutOfSync, new(t.DefinitionFile, d.Line, 1), $"Column `{d.Name}` is declared, but the query no longer returns it."));
            foreach (var c in diff.Added)
                diags.Add(new Diagnostic(DiagnosticCatalog.DefinitionOutOfSync, new(t.QueryFile, 0, 0), $"The query returns column `{c.Name}` ({c.Type.LogicalType ?? c.DuckDbType}), which is not declared."));
            foreach (var ch in diff.TypeChanged)
                diags.Add(new Diagnostic(DiagnosticCatalog.DefinitionOutOfSync, new(t.DefinitionFile, ch.Declared.Line, 1), $"Column `{ch.Declared.Name}` is declared {ch.Declared.Type}, but the query resolves {ch.ResolvedType}."));
        }
        return diags;
    }

    // ---- run: ask, then produce the new or edited definition text ----

    public DefineRun Run(IReadOnlyList<DefineTarget> targets, AnswerFile? answers, string answersPath, IPrompter? prompter, bool acceptHighCertainty)
    {
        var session = new Session(answers, answersPath, prompter, acceptHighCertainty);
        var runDiags = new List<Diagnostic>();
        var (order, upstreamOf) = Order(targets, runDiags);

        var outcomes = new List<DefineOutcome>();
        var notDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in order)
        {
            var waitingOn = upstreamOf[t.ModelName].Where(notDone.Contains).ToList();
            var outcome = waitingOn.Count > 0
                ? new DefineOutcome(t, DefineStatus.Skipped, null, [], [], [$"Skipped: it queries {string.Join(", ", waitingOn)}, which is not defined yet. Define that first."], [])
                : RunOne(t, session);
            outcomes.Add(outcome);
            if (!outcome.Done) notDone.Add(t.ModelName);
        }

        foreach (var unused in answers?.Answers.Where(a => !session.Asked.Contains(a.QuestionId)) ?? [])
            runDiags.Add(new Diagnostic(DiagnosticCatalog.AnswerForUnknownQuestion, unused.Location, $"The answers file has an answer for `{unused.QuestionId}`, which was not asked in this run."));
        return new DefineRun(outcomes.OrderBy(o => o.Target.DefinitionFile, StringComparer.Ordinal).ToList(), runDiags);
    }

    private sealed class Session(AnswerFile? answers, string answersPath, IPrompter? prompter, bool accept)
    {
        public HashSet<string> Asked { get; } = [];

        public Resolution Ask(IReadOnlyList<Question> questions)
        {
            foreach (var q in questions) Asked.Add(q.Id);
            return QuestionResolver.Resolve(questions, answers, answersPath, prompter, new ResolveOptions(accept, WarnOnUnknownAnswers: false));
        }
    }

    private DefineOutcome RunOne(DefineTarget t, Session session)
    {
        if (t.HasDefinitionFile && t.Existing == null)
            return new DefineOutcome(t, DefineStatus.Failed, null, t.ExistingProblems, [], ["The existing definition does not load, so define cannot edit it. Fix the errors above first."], []);

        var (inference, problems) = ModelInference.Infer(t.QueryFile, t.Sql, graph, t.Macros);
        if (inference == null) return new DefineOutcome(t, DefineStatus.Failed, null, problems, [], [], []);

        var outcome = t.Existing == null ? RunNew(t, inference, session) : RunExisting(t, inference, session);
        if (outcome.Done && DefinitionOf(outcome) is { } def) graph.Provide(t.ModelName, def.Columns, def.Grain);
        return outcome;
    }

    private ModelDefinition? DefinitionOf(DefineOutcome o)
    {
        var text = o.NewText ?? o.Target.ExistingText;
        if (text == null) return null;
        // a file whose query has a head says its name and kind there, not in the definition: they are put back to read the definition as the project does
        if (o.Target.Head != null) text = QueryHeadParser.YamlFields(o.Target.Head) + text;
        return ModelDefinitionLoader.Load(text, o.Target.DefinitionFile, null, [], config.Connections.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private static DefineOutcome Failed(DefineTarget t, IReadOnlyList<ResolvedAnswer> answers, IReadOnlyList<string> notes, params Diagnostic[] diags) =>
        new(t, DefineStatus.Failed, null, diags, answers, notes, []);

    private static Diagnostic BadAnswer(string message) =>
        new(DiagnosticCatalog.InvalidValue, new("<answers>", 0, 0), message);

    // ---- a model with no definition yet ----

    private DefineOutcome RunNew(DefineTarget t, Inference inf, Session session)
    {
        var notes = new List<string>();
        var answers = new List<ResolvedAnswer>();

        // the head of the query file, when there is one, has already said the name and the kind (and the options of the kind): they are not asked
        var head = t.Head;
        var round1 = new List<Question> { DefineQuestions.Targets(t.ModelName, config.DefaultConnections, Portability(t)) };
        if (head == null) { round1.Insert(0, DefineQuestions.Kind(t.ModelName)); round1.Insert(0, DefineQuestions.Name(t.ModelName, t.QueryFile)); }
        foreach (var c in inf.Columns)
        {
            round1.Add(DefineQuestions.ColumnType(t.ModelName, c));
            round1.Add(DefineQuestions.ColumnNullable(t.ModelName, c));
        }
        var r1 = session.Ask(round1);
        answers.AddRange(r1.Answers);
        if (r1.Answers.Any(a => a.Choice == DefineQuestions.SkipModel)) return Skipped(t, answers);
        if (!r1.Complete)
        {
            var kindOpen = r1.Unanswered.Any(q => q.Id == QuestionIds.Define(t.ModelName, "kind"));
            if (kindOpen) notes.Add("More questions (grain, unique key, time column) follow once the kind is answered.");
            return new DefineOutcome(t, DefineStatus.Incomplete, null, r1.Diagnostics, answers, notes, r1.Unanswered);
        }

        var a1 = answers.ToDictionary(a => a.QuestionId);
        var name = head?.Name ?? a1[QuestionIds.Define(t.ModelName, "name")].Value!;
        var kind = head?.Kind ?? a1[QuestionIds.Define(t.ModelName, "kind")].Choice;
        var targets = ParseTargets(a1[QuestionIds.Define(t.ModelName, "connections")], out var targetProblem);
        if (targetProblem != null) return Failed(t, answers, notes, targetProblem);

        var columns = new List<ColumnDefinition>();
        foreach (var c in inf.Columns)
        {
            var type = a1[QuestionIds.Define(t.ModelName, $"columns.{c.Name}.type")].Value!;
            if (!QueryDescriber.IsPlainType(type)) return Failed(t, answers, notes, BadAnswer($"`{type}` is not a plain SQL type name (column `{c.Name}`)."));
            columns.Add(new ColumnDefinition(c.Name, LogicalTypes.Normalize(type), a1[QuestionIds.Define(t.ModelName, $"columns.{c.Name}.nullable")].Choice == "nullable"));
        }

        // round 2: what depends on the kind
        var grain = new List<string>();
        var uniqueKey = new List<string>();
        string? timeColumn = null, lookback = null;
        if (kind is ModelKinds.IncrementalByUniqueKey or ModelKinds.IncrementalByTimeRange)
        {
            var outputs = inf.Columns.Select(c => c.Name).ToList();
            var round2 = new List<Question> { DefineQuestions.Grain(t.ModelName, inf.GrainCandidates, outputs) };
            if (kind == ModelKinds.IncrementalByUniqueKey) { if (head?.Property("unique_key") == null) round2.Add(DefineQuestions.UniqueKey(t.ModelName, outputs)); }
            else
            {
                if (head?.Property("time_column") == null) round2.Add(DefineQuestions.TimeColumn(t.ModelName, inf.TimeColumnCandidates));
                if (head?.Property("lookback") == null && head?.Property("time_column") == null) round2.Add(DefineQuestions.Lookback(t.ModelName));
            }

            var r2 = session.Ask(round2);
            answers.AddRange(r2.Answers);
            if (r2.Answers.Any(a => a.Choice == DefineQuestions.SkipModel)) return Skipped(t, answers);
            if (!r2.Complete) return new DefineOutcome(t, DefineStatus.Incomplete, null, r2.Diagnostics, answers, notes, r2.Unanswered);

            var a2 = r2.Answers.ToDictionary(a => a.QuestionId);
            var g = a2[QuestionIds.Define(t.ModelName, "grain")];
            if (g.Choice.StartsWith("candidate_", StringComparison.Ordinal))
                grain = inf.GrainCandidates[int.Parse(g.Choice["candidate_".Length..]) - 1].Columns.ToList();
            else
            {
                var parsedGrain = ParseColumns(g.Value!, outputs, "grain", out var grainProblem, t);
                if (parsedGrain == null) return Failed(t, answers, notes, grainProblem!);
                grain = parsedGrain;
            }

            if (kind == ModelKinds.IncrementalByUniqueKey && head?.Property("unique_key") is { } headKey)
                uniqueKey = headKey.Value.Items?.ToList() ?? [headKey.Value.Text!];
            else if (kind == ModelKinds.IncrementalByUniqueKey)
            {
                var u = a2[QuestionIds.Define(t.ModelName, "unique_key")];
                if (u.Choice == "same_as_grain") uniqueKey = grain.ToList();
                else
                {
                    var parsed = ParseColumns(u.Value!, outputs, "unique_key", out var keyProblem, t);
                    if (parsed == null) return Failed(t, answers, notes, keyProblem!);
                    uniqueKey = parsed;
                }
            }
            else if (head?.Property("time_column") is { } headTime)
            {
                var match = outputs.FirstOrDefault(o => string.Equals(o, headTime.Value.Text!.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match == null) return Failed(t, answers, notes, BadAnswer($"time_column `{headTime.Value.Text}` of the head is not an output column of the query."));
                timeColumn = match;
                lookback = head.Property("lookback")?.Value.Text;
            }
            else
            {
                var tc = a2[QuestionIds.Define(t.ModelName, "time_column")].Value!;
                var match = outputs.FirstOrDefault(o => string.Equals(o, tc.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match == null) return Failed(t, answers, notes, BadAnswer($"time_column `{tc}` is not an output column of the query."));
                timeColumn = match;
                var lb = a2[QuestionIds.Define(t.ModelName, "lookback")];
                lookback = lb.Choice == "use_lookback" ? lb.Value : null;
            }
        }

        var def = new ModelDefinition(name, kind, uniqueKey, timeColumn, lookback, grain, targets, columns, []);

        // round 3: indexes the loads would use (advice from IndexAdvisor; never added without an answer, and never by --accept-inferred, which takes only high-certainty proposals)
        var advice = IndexAdvisor.For(def, targets ?? config.DefaultConnections);
        if (advice.Count > 0)
        {
            var r3 = session.Ask([DefineQuestions.Indexes(t.ModelName, advice)]);
            answers.AddRange(r3.Answers);
            if (!r3.Complete) return new DefineOutcome(t, DefineStatus.Incomplete, null, r3.Diagnostics, answers, notes, r3.Unanswered);
            if (r3.Answers.Single().Choice == "add_suggested")
            {
                def = def with { DeclaredIndexes = advice.Select(a => a.Suggested).ToList() };
                notes.Add($"Declared {advice.Count} suggested index(es): {string.Join(", ", advice.Select(a => a.SuggestedName))}.");
            }
        }
        var text = DefinitionWriter.Create(def, withNameAndKind: head == null);
        var verify = new List<Diagnostic>();
        if (ModelDefinitionLoader.Load(head == null ? text : QueryHeadParser.YamlFields(head) + text, t.DefinitionFile, head == null ? t.ModelName : null, verify, config.Connections.Keys.ToHashSet(StringComparer.Ordinal)) == null)
            return Failed(t, answers, notes, [.. verify]);
        return new DefineOutcome(t, DefineStatus.Created, text, [], answers, notes, []);
    }

    private static DefineOutcome Skipped(DefineTarget t, IReadOnlyList<ResolvedAnswer> answers) =>
        new(t, DefineStatus.Skipped, null, [], answers, ["Skipped by answer: nothing is written for this model."], []);

    private IReadOnlyList<string>? ParseTargets(ResolvedAnswer answer, out Diagnostic? problem)
    {
        problem = null;
        if (answer.Choice == "use_project_default") return null;
        var known = config.Connections.Keys.ToList();
        // a connection name is matched without regard to case and written as declared
        var typed = answer.Value!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var names = typed.Select(n => known.FirstOrDefault(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase)) ?? n).Distinct().ToList();
        var unknown = names.FirstOrDefault(n => !known.Contains(n));
        if (names.Count == 0 || unknown != null)
        {
            problem = BadAnswer(unknown != null ? $"Unknown connection `{unknown}`. One of: {string.Join(", ", known.Order(StringComparer.Ordinal))}." : "Name at least one connection.");
            return null;
        }
        return names;
    }

    private static List<string>? ParseColumns(string value, IReadOnlyList<string> outputs, string what, out Diagnostic? problem, DefineTarget t)
    {
        problem = null;
        var result = new List<string>();
        foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = outputs.FirstOrDefault(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase));
            if (match == null) { problem = BadAnswer($"{what} names `{raw}`, which is not an output column of {t.QueryFile}."); return null; }
            if (!result.Contains(match)) result.Add(match);
        }
        if (result.Count == 0) problem = BadAnswer($"{what} needs at least one column.");
        return result.Count == 0 ? null : result;
    }

    private IReadOnlyList<string> Portability(DefineTarget t)
    {
        if (linter == null) return [];
        var lines = new List<string>();
        foreach (var target in TargetNames.All)
        {
            var found = linter.Lint(t.Sql, t.QueryFile, [target], config);
            lines.Add($"Portability on {target}: {found.Count(d => d.Severity == Severity.Error)} error(s), {found.Count(d => d.Severity == Severity.Warning)} warning(s), {found.Count(d => d.Severity == Severity.Note)} note(s).");
        }
        return lines;
    }

    // ---- a model that already has a definition ----

    private DefineOutcome RunExisting(DefineTarget t, Inference inf, Session session)
    {
        var existing = t.Existing!;
        var notes = new List<string>();
        var answers = new List<ResolvedAnswer>();
        var diff = ColumnDiff.Compute(existing.Columns, inf);
        notes.AddRange(diff.Notes);
        if (diff.InSync) return new DefineOutcome(t, DefineStatus.InSync, null, [], answers, notes, []);

        var edits = new List<ColumnEdit>();
        var keptRemovals = 0;
        var keptTypes = 0;

        // round 1: possible renames (they decide which columns count as added or removed)
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (diff.RenameCandidates.Count > 0)
        {
            var rq = diff.RenameCandidates.Select(c => DefineQuestions.Rename(t.ModelName, c, ReferencesTo(existing, c.Removed.Name, c.Added.Name))).ToList();
            var r = session.Ask(rq);
            answers.AddRange(r.Answers);
            if (!r.Complete) return new DefineOutcome(t, DefineStatus.Incomplete, null, r.Diagnostics, answers, notes, r.Unanswered);
            foreach (var a in r.Answers.Where(a => a.Choice == "rename_to"))
            {
                var removed = diff.RenameCandidates.First(c => QuestionIds.Rename(t.ModelName, c.Removed.Name) == a.QuestionId).Removed;
                var target = diff.Added.FirstOrDefault(c => string.Equals(c.Name, a.Value!.Trim(), StringComparison.OrdinalIgnoreCase));
                if (target == null) return Failed(t, answers, notes, BadAnswer($"`{a.Value}` is not a new output column, so `{removed.Name}` cannot be renamed to it."));
                if (renamed.Values.Contains(target.Name, StringComparer.OrdinalIgnoreCase)) return Failed(t, answers, notes, BadAnswer($"`{target.Name}` is already the target of another rename."));
                renamed[removed.Name] = target.Name;
                edits.Add(new RenameColumn(removed.Name, target.Name));
            }
        }

        // round 2: what is left after renames
        var effective = existing.Columns.Select(d => renamed.TryGetValue(d.Name, out var to) ? d with { Name = to } : d).ToList();
        var remaining = ColumnDiff.Compute(effective, inf);

        var questions = new List<Question>();
        questions.AddRange(remaining.Removed.Select(d => DefineQuestions.Remove(t.ModelName, d)));
        foreach (var c in remaining.Added)
        {
            questions.Add(DefineQuestions.ColumnType(t.ModelName, c));
            questions.Add(DefineQuestions.ColumnNullable(t.ModelName, c));
        }
        questions.AddRange(remaining.TypeChanged.Select(ch => DefineQuestions.ColumnType(t.ModelName, ch.Inferred, ch.Declared.Type)));

        if (questions.Count > 0)
        {
            var r = session.Ask(questions);
            answers.AddRange(r.Answers);
            if (r.Answers.Any(a => a.Choice == DefineQuestions.SkipModel)) return Skipped(t, answers);
            if (!r.Complete) return new DefineOutcome(t, DefineStatus.Incomplete, null, r.Diagnostics, answers, notes, r.Unanswered);
            var byId = r.Answers.ToDictionary(a => a.QuestionId);

            foreach (var d in remaining.Removed)
            {
                if (byId[QuestionIds.Define(t.ModelName, $"columns.{d.Name}.remove")].Choice == "remove_column") edits.Add(new RemoveColumn(d.Name));
                else { keptRemovals++; notes.Add($"Kept the declaration of `{d.Name}`, which the query no longer returns: `project model update --check` will still report it."); }
            }
            foreach (var c in remaining.Added)
            {
                var type = byId[QuestionIds.Define(t.ModelName, $"columns.{c.Name}.type")].Value!;
                if (!QueryDescriber.IsPlainType(type)) return Failed(t, answers, notes, BadAnswer($"`{type}` is not a plain SQL type name (column `{c.Name}`)."));
                edits.Add(new AddColumn(c.Name, LogicalTypes.Normalize(type), byId[QuestionIds.Define(t.ModelName, $"columns.{c.Name}.nullable")].Choice == "nullable"));
            }
            foreach (var ch in remaining.TypeChanged)
            {
                var a = byId[QuestionIds.Define(t.ModelName, $"columns.{ch.Declared.Name}.type")];
                if (a.Choice == DefineQuestions.KeepDeclared) { keptTypes++; notes.Add($"Kept the declared type of `{ch.Declared.Name}` ({ch.Declared.Type}); the query resolves {ch.ResolvedType}."); continue; }
                if (!QueryDescriber.IsPlainType(a.Value!)) return Failed(t, answers, notes, BadAnswer($"`{a.Value}` is not a plain SQL type name (column `{ch.Declared.Name}`)."));
                edits.Add(new SetColumnType(ch.Declared.Name, LogicalTypes.Normalize(a.Value!)));
            }
        }

        var (newText, problem) = DefinitionEditor.Apply(t.ExistingText!, t.DefinitionFile, edits);
        if (problem != null) return Failed(t, answers, notes, problem);
        if (newText == t.ExistingText) return new DefineOutcome(t, DefineStatus.Unchanged, null, [], answers, notes, []);

        var verify = new List<Diagnostic>();
        var updated = ModelDefinitionLoader.Load(newText!, t.DefinitionFile, t.ModelName, verify, config.Connections.Keys.ToHashSet(StringComparer.Ordinal));
        if (updated == null) return Failed(t, answers, notes, [.. verify]);

        // what is still different must be exactly what the person chose to keep; anything else is a bug in this tool
        var left = ColumnDiff.Compute(updated.Columns, inf);
        if (left.Removed.Count != keptRemovals || left.TypeChanged.Count != keptTypes || left.Added.Count != 0)
            throw new InvalidOperationException($"define left `{t.DefinitionFile}` out of sync in a way nobody chose; nothing was written. This is a tool bug.");
        return new DefineOutcome(t, DefineStatus.Updated, newText, [], answers, notes, []);
    }

    private static IReadOnlyList<string> ReferencesTo(ModelDefinition d, string from, string to)
    {
        var lines = new List<string>();
        bool Has(IEnumerable<string> l) => l.Contains(from, StringComparer.OrdinalIgnoreCase);
        if (Has(d.Grain)) lines.Add($"grain {Fmt(d.Grain)} becomes {Fmt(d.Grain.Select(g => string.Equals(g, from, StringComparison.OrdinalIgnoreCase) ? to : g))}.");
        if (Has(d.UniqueKey)) lines.Add($"unique_key {Fmt(d.UniqueKey)} becomes {Fmt(d.UniqueKey.Select(g => string.Equals(g, from, StringComparison.OrdinalIgnoreCase) ? to : g))}.");
        if (d.TimeColumn != null && string.Equals(d.TimeColumn, from, StringComparison.OrdinalIgnoreCase)) lines.Add($"time_column `{from}` becomes `{to}`.");
        return lines;
    }

    private static string Fmt(IEnumerable<string> l) => "[" + string.Join(", ", l) + "]";

    // ---- order: a model after the models it queries ----

    private (List<DefineTarget> Order, Dictionary<string, List<string>> UpstreamOf) Order(IReadOnlyList<DefineTarget> targets, List<Diagnostic> diags)
    {
        var byName = targets.ToDictionary(t => t.ModelName, StringComparer.OrdinalIgnoreCase);
        var upstreamOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            var (facts, _) = QueryAnalyzer.Analyze(t.Sql);
            upstreamOf[t.ModelName] = (facts?.BaseTables.Select(b => b.QualifiedName) ?? []).Where(n => t.Macros == null || !t.Macros.MacroNames.Contains(n, StringComparer.OrdinalIgnoreCase)).Concat(t.Macros?.Tables ?? []).Where(n => byName.ContainsKey(n) && !string.Equals(n, t.ModelName, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        }

        var order = new List<DefineTarget>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = targets.OrderBy(t => t.ModelName, StringComparer.Ordinal).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(t => upstreamOf[t.ModelName].All(done.Contains)).ToList();
            if (ready.Count == 0)
            {
                foreach (var t in pending)
                    diags.Add(new Diagnostic(DiagnosticCatalog.ModelCycle, new(t.QueryFile, 0, 0), $"`{t.ModelName}` is part of a dependency cycle through {string.Join(", ", upstreamOf[t.ModelName].Where(n => pending.Any(p => string.Equals(p.ModelName, n, StringComparison.OrdinalIgnoreCase))))}."));
                order.AddRange(pending);   // still processed, so each reports its own problem
                break;
            }
            foreach (var t in ready) { order.Add(t); done.Add(t.ModelName); pending.Remove(t); }
        }
        return (order, upstreamOf);
    }
}
