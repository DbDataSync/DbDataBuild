using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sample;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project tests run` (effect: offline only; DESIGN.md 9.8). Runs the project's tests: metadata rules (`tests/metadata/*.sql`, DuckDB SELECTs over the metadata views that return the violations) and
/// model tests (`tests/models/*.yml`, given rows and what the model's query must return). Everything runs in an in-memory DuckDB; no target is contacted and nothing is written. Tests are selected
/// by name or path, by kind and by tag; an `error` test that fails (or cannot run) makes the run fail, a `warning` test only reports (unless `--strict`).
/// </summary>
internal static class TestCommand
{
    private sealed record Outcome(string Kind, string Name, string File, string? Model, string? Case, string? Description, string Severity, IReadOnlyList<string> Tags,
        string Status, int Violations, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, string? Error);

    public static int Run(CommandSpec spec, string root, string[] names, string[] tags, string? kind, int limit, bool strict, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (limit < 0) { error.WriteLine("--limit must not be negative."); return CliApp.ExitUsage; }
        if (kind is not (null or "metadata" or "model")) { error.WriteLine("--kind is `metadata` or `model`."); return CliApp.ExitUsage; }

        var ruleFiles = kind == "model" ? [] : TestRuleLoader.Discover(root);
        var modelFiles = kind == "metadata" ? [] : ModelTestLoader.Discover(root);
        if (Select(ruleFiles, modelFiles, names, error) is not var (selectedRules, selectedModels)) return CliApp.ExitUsage;

        var ctx = ProjectContext.Load(root);
        var invalid = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code).ToList();
        foreach (var d in invalid) error.Diag(d);
        if (invalid.Count > 0)
        {
            output.WriteLine($"No tests were run: {invalid.Count} error(s) in the project. Fix them (`{ProductInfo.Cli} project compile` shows them).");
            return CliApp.ExitFindings;
        }

        var outcomes = new List<Outcome>();
        bool Wanted(IReadOnlyList<string> t) => tags.Length == 0 || t.Any(x => tags.Contains(x, StringComparer.Ordinal));

        // a damaged file is a finding of its own and never stops the others
        var rules = new List<TestRule>();
        foreach (var file in selectedRules)
        {
            var problems = new List<Diagnostic>();
            var rule = TestRuleLoader.Load(File.ReadAllText(Path.Combine(root, file)), file, problems);
            if (rule == null) { Damaged(outcomes, "metadata", TestRuleLoader.NameOf(file), file, problems, error); continue; }
            if (Wanted(rule.Tags)) rules.Add(rule);
        }
        if (rules.Count > 0)
        {
            using var db = MetadataDatabase.Open(MetadataPublisher.Collect(ctx, null, plan: null).Select(d => new MetadataDocument(d.SubjectKind, d.Subject, d.Json, d.Hash)), ProductInfo.Version, MetadataBuilder.Dependencies(ctx));
            foreach (var rule in rules) outcomes.Add(EvaluateRule(db, rule, limit, error));
        }

        foreach (var file in selectedModels)
        {
            var problems = new List<Diagnostic>();
            var test = ModelTestLoader.Load(File.ReadAllText(Path.Combine(root, file)), file, problems);
            if (test == null) { Damaged(outcomes, "model", ModelTestLoader.NameOf(file), file, problems, error); continue; }
            if (Wanted(test.Tags)) EvaluateModelTest(ctx, test, limit, outcomes, error);
        }
        outcomes = outcomes.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();

        var counts = new
        {
            total = outcomes.Count,
            passed = outcomes.Count(o => o.Status == "pass"),
            failed = outcomes.Count(o => o.Status == "fail"),
            warned = outcomes.Count(o => o.Status == "warn"),
            errored = outcomes.Count(o => o.Status == "error"),
        };
        output.Payload("counts", counts);
        output.Payload("tests", outcomes.Select(o => new
        {
            name = o.Name, kind = o.Kind, file = o.File, model = o.Model, @case = o.Case, description = o.Description, severity = o.Severity, tags = o.Tags,
            status = o.Status, violations = o.Violations, columns = o.Columns, rows = o.Rows, error = o.Error,
        }).ToList());

        if (outcomes.Count == 0)
        {
            output.WriteLine(ruleFiles.Count + modelFiles.Count == 0 ? $"No tests: {TestRuleLoader.Directory}/ and {ModelTestLoader.Directory}/ have no test files." : "No tests matched.");
            return CliApp.ExitOk;
        }
        output.WriteLine();
        foreach (var o in outcomes)
        {
            var label = o.Status switch { "pass" => "pass", "fail" => "FAIL", "warn" => "warn", _ => "ERROR" };
            output.WriteLine($"{label,-5} {o.Name}{(o.Violations > 0 ? $"  ({o.Violations} violation(s))" : "")}{(o.Description is { } d ? "  " + d : "")}");
            if (o.Error != null) output.WriteLine($"      {o.Error}");
            foreach (var row in o.Rows) output.WriteLine("      " + string.Join(" | ", row.Select(kv => $"{kv.Key}={Show(kv.Value)}")));
            if (o.Violations > o.Rows.Count) output.WriteLine($"      ... and {o.Violations - o.Rows.Count} more (--limit)");
        }
        output.WriteLine();
        output.WriteLine($"{counts.total} test(s): {counts.passed} passed, {counts.failed} failed, {counts.warned} warned, {counts.errored} could not run.");
        return counts.failed + counts.errored > 0 || (strict && counts.warned > 0) ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    private static void Damaged(List<Outcome> outcomes, string kind, string name, string file, List<Diagnostic> problems, TextWriter error)
    {
        foreach (var p in problems) error.Diag(p);
        outcomes.Add(new Outcome(kind, name, file, null, null, null, "error", [], "error", 0, [], [], string.Join(" ", problems.Select(p => p.Found))));
    }

    /// <summary>Names and files select tests of either kind: a name or path matches a rule or a model test file; none given selects everything.</summary>
    private static (List<string> Rules, List<string> Models)? Select(IReadOnlyList<string> rules, IReadOnlyList<string> models, string[] names, TextWriter error)
    {
        if (names.Length == 0) return (rules.ToList(), models.ToList());
        var chosenRules = new List<string>();
        var chosenModels = new List<string>();
        foreach (var n in names)
        {
            var path = n.Replace('\\', '/');
            var r = rules.Where(f => f == path || TestRuleLoader.NameOf(f) == n).ToList();
            var m = models.Where(f => f == path || ModelTestLoader.NameOf(f) == n).ToList();
            if (r.Count + m.Count == 0)
            {
                var known = rules.Select(TestRuleLoader.NameOf).Concat(models.Select(ModelTestLoader.NameOf)).Distinct().ToList();
                error.WriteLine($"No test named `{n}`. Tests: {(known.Count == 0 ? "none" : string.Join(", ", known))}.");
                return null;
            }
            foreach (var f in r) if (!chosenRules.Contains(f)) chosenRules.Add(f);
            foreach (var f in m) if (!chosenModels.Contains(f)) chosenModels.Add(f);
        }
        return (chosenRules, chosenModels);
    }

    private static Outcome EvaluateRule(MetadataDatabase db, TestRule rule, int limit, TextWriter error)
    {
        var run = db.Run(rule.Sql, limit);
        return Judge("metadata", rule.Name, rule.File, null, null, rule.Description, rule.Severity, rule.Tags, run.Outcome, run.Message, 1, run.Columns, run.Rows, run.Count, error);
    }

    private static void EvaluateModelTest(ProjectContext ctx, ModelTestFile test, int limit, List<Outcome> outcomes, TextWriter error)
    {
        var built = ctx.Project.BuiltModels.FirstOrDefault(s => s.Definition.Name == test.Model);
        if (built == null)
        {
            var d = new Diagnostic(DiagnosticCatalog.InvalidValue, new(test.File, 1, 1), $"The test `{test.Name}` is for the model `{test.Model}`, which is not a model of this project.",
                Fix: $"Name an existing model in `model:`, or name the file after the model (`{ModelTestLoader.Directory}/<schema name>/<model>.yml`).");
            error.Diag(d);
            outcomes.Add(new Outcome("model", test.Name, test.File, test.Model, null, test.Description, test.Severity, test.Tags, "error", 0, [], [], d.Found));
            return;
        }

        var valuesSql = built.ReadQueryWithValues(ctx.Root, ctx.Config, ctx.TargetsOf(built.Definition)[0]);
        var (sql, collation, _) = ctx.StringEmulation(built, valuesSql);
        var declared = ctx.Project.Models.Select(m => (m.Name, m.Columns)).Concat(ctx.Project.DeclaredModelsAndStaging.Select(s => (s.Name, s.Columns))).ToDictionary(t => t.Name, t => t.Columns, StringComparer.OrdinalIgnoreCase);
        var upstream = ctx.BaseTablesOf(built, valuesSql)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(declared.ContainsKey).Select(n => new UpstreamTable(n, declared[n])).ToList();

        foreach (var c in test.Cases)
        {
            var result = ModelTestRunner.Run(built.Definition.Name, built.Definition.Columns, sql, upstream, c, limit, ctx.Project.Macros.PreludeFor([valuesSql]), collation);
            var outcome = result.Outcome switch { CaseOutcome.Pass => RuleOutcome.Ran, CaseOutcome.Fail => RuleOutcome.Ran, CaseOutcome.NotASelect => RuleOutcome.NotASelect, _ => RuleOutcome.CouldNotRun };
            var o = Judge("model", $"{test.Name}::{c.Name}", test.File, test.Model, c.Name, test.Description, test.Severity, test.Tags, outcome, result.Message, result.Line, result.Columns, result.Rows,
                result.Outcome == CaseOutcome.Fail ? Math.Max(1, result.Count) : 0, error);
            outcomes.Add(o);
        }
    }

    /// <summary>The status, the diagnostic and the outcome record for one finished test or case.</summary>
    private static Outcome Judge(string kind, string name, string file, string? model, string? caseName, string? description, string severity, IReadOnlyList<string> tags,
        RuleOutcome ran, string? message, int line, IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, object?>> rows, int count, TextWriter error)
    {
        Outcome Make(string status, string? err) => new(kind, name, file, model, caseName, description, severity, tags, status, status is "pass" or "error" ? 0 : count, columns, status == "error" ? [] : rows, err);
        if (ran == RuleOutcome.NotASelect)
        {
            error.Diag(new Diagnostic(DiagnosticCatalog.TestNotASelect, new(file, Math.Max(1, line), 1), $"`{name}`: {message}"));
            return Make("error", message);
        }
        if (ran == RuleOutcome.CouldNotRun)
        {
            error.Diag(new Diagnostic(DiagnosticCatalog.TestCouldNotRun, new(file, Math.Max(1, line), 1), $"`{name}` could not run: {message}"));
            return Make("error", message);
        }
        if (count == 0) return Make("pass", null);

        var warning = severity == "warning";
        var first = rows.Count > 0 ? string.Join(", ", rows[0].Select(kv => $"{kv.Key}={Show(kv.Value)}")) : "";
        error.Diag(new Diagnostic(DiagnosticCatalog.TestFailed, new(file, Math.Max(1, line), 1),
            $"`{name}`{(message != null ? ": " + message : $" returned {count} violation(s)")}{(first.Length > 0 ? $", the first: {first}" : "")}.", SeverityOverride: warning ? Severity.Warning : null));
        return Make(warning ? "warn" : "fail", message);
    }

    private static string Show(object? v) => v switch
    {
        null => "NULL",
        System.Collections.IEnumerable list and not string => "[" + string.Join(", ", list.Cast<object?>().Select(Show)) + "]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
