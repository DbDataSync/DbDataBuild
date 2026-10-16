using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Lowering;
using DbDataBuild.Sql;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rules;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Conformance.FunctionMatrix;

/// <summary>What the tool does with one query for one target, without running it: <see cref="Status"/> is same, rewritten, refused or error.</summary>
public sealed record StaticTarget(string Status, string Detail, string? Final, string Lint, string Validate, IReadOnlyList<string> Rules);

/// <summary>The static result of one query: DuckDB's own rejection, the lowering's refusal, or the text for each target.</summary>
public sealed record StaticResult(string? DuckRejected, string? Lowered, string? Refused, string? Crash, IReadOnlyList<string> TypeRules, IReadOnlyDictionary<string, StaticTarget> Targets);

/// <summary>
/// The pipeline `project compile` runs on a model's query, as far as it can be run without a project: DuckDB's bound plan, the lowering, the target rules, the matrix lint on the rewritten query, the
/// transpile and the target's own parser. The same calls as <see cref="EngineProbe.Render"/> and the load renderer; nothing is reimplemented here except the classification of the outcome.
/// </summary>
public sealed class StaticPipeline(DuckDBConnection duck)
{
    private readonly MatrixLinter linter = new(MatrixLoader.LoadEmbedded([]));
    private readonly Dictionary<string, StaticResult> cache = new(StringComparer.Ordinal);

    public static readonly string[] Targets = ["sqlserver", "postgres", "fabric"];

    /// <summary>The engine versions the tool is told to generate for: SQL Server 2022 (16). Fabric and PostgreSQL carry none.</summary>
    private static int? VersionOf(string target) => target == "sqlserver" ? 16 : null;

    public static bool IsQueryError(string message) =>
        message.StartsWith("Binder Error", StringComparison.Ordinal) || message.StartsWith("Catalog Error", StringComparison.Ordinal) || message.StartsWith("Parser Error", StringComparison.Ordinal)
        || message.StartsWith("Not implemented Error", StringComparison.Ordinal) || message.StartsWith("Syntax Error", StringComparison.Ordinal);

    public StaticResult Run(string sql)
    {
        if (cache.TryGetValue(sql, out var hit)) return hit;
        return cache[sql] = Compute(sql);
    }

    private StaticResult Compute(string sql)
    {
        string lowered;
        IReadOnlyList<string> typeRules;
        try
        {
            string plan;
            using (var cmd = duck.CreateCommand()) { cmd.CommandText = QueryDescriber.PlanStatement(sql); plan = (string)cmd.ExecuteScalar()!; }
            var names = new List<string>();
            using (var cmd = duck.CreateCommand()) { cmd.CommandText = "DESCRIBE " + sql; using var r = cmd.ExecuteReader(); while (r.Read()) names.Add(r.GetString(0)); }
            var query = PlanLowerer.Lower(plan, names, null, null);
            lowered = query.Sql;
            typeRules = query.Rules;
        }
        catch (LoweringException ex) when (ex.Kind != "unsupported") { return new(FirstLine(ex.Kind + ": " + ex.Message), null, null, null, [], new Dictionary<string, StaticTarget>()); }
        catch (LoweringException ex) { return new(null, null, FirstLine(ex.Message), null, [], new Dictionary<string, StaticTarget>()); }
        catch (DuckDBException ex) { return new(FirstLine(ex.Message), null, null, null, [], new Dictionary<string, StaticTarget>()); }
        catch (Exception ex) { return new(null, null, null, ex.GetType().Name + ": " + FirstLine(ex.Message), [], new Dictionary<string, StaticTarget>()); }

        var targets = new Dictionary<string, StaticTarget>();
        foreach (var t in Targets) targets[t] = ForTarget(lowered, t);
        return new(null, lowered, null, null, typeRules, targets);
    }

    private StaticTarget ForTarget(string lowered, string target)
    {
        TargetRules.Result ruled;
        try { ruled = TargetRules.Apply(lowered, target, null, VersionOf(target)); }
        catch (Exception ex) { return new("error", "CRASH in target rules " + ex.GetType().Name + ": " + FirstLine(ex.Message), null, "", "", []); }

        var lint = linter.Lint(ruled.Sql, "probe.sql", [target]);
        var lintCodes = string.Join(" ", lint.Select(d => d.Code).Distinct().Order(StringComparer.Ordinal));
        var lintErrors = lint.Where(d => d.Severity == Severity.Error).ToList();

        var tgt = TargetRegistry.Get(target);
        var (outcome, text) = TargetRules.Transpile(ruled.Sql.Trim().TrimEnd(';'), Dialects.Canonical, tgt.Dialect);
        if (!outcome.Ok || text == null) return new("error", "transpile: " + FirstLine(outcome.Error ?? "failed"), null, lintCodes, "", ruled.Rules);
        var final = TargetRules.Finish(text, tgt.Name).Trim().TrimEnd(';');
        if (tgt.Dialect is "tsql" or "fabric") final = Regex.Replace(final, @"(GENERATE_SERIES\([^()]*\)) AS (\w+)\(value\)", "$1 AS $2", RegexOptions.IgnoreCase);

        IReadOnlyList<Diagnostic> validation;
        try { validation = tgt.Validate(final, "probe.sql", VersionOf(target)); }
        catch (Exception ex) { return new("error", "CRASH in validate " + ex.GetType().Name + ": " + FirstLine(ex.Message), final, lintCodes, "", ruled.Rules); }
        var validateCodes = string.Join(" ", validation.Select(d => d.Code).Distinct().Order(StringComparer.Ordinal));
        var validateErrors = validation.Where(d => d.Severity == Severity.Error).ToList();

        if (lintErrors.Count > 0) return new("refused", $"{lintErrors[0].Code}: {FirstLine(lintErrors[0].Found)}", final, lintCodes, validateCodes, ruled.Rules);
        if (validateErrors.Count > 0) return new("refused", $"{validateErrors[0].Code}: {FirstLine(validateErrors[0].Found)}", final, lintCodes, validateCodes, ruled.Rules);
        return new(ruled.Rules.Count > 0 ? "rewritten" : "unclassified", string.Join(",", ruled.Rules), final, lintCodes, validateCodes, ruled.Rules);
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    private static readonly Regex CallName = new(@"([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    /// <summary>The names written as calls in a text, upper case.</summary>
    public static HashSet<string> CallNames(string sql) => CallName.Matches(sql).Select(m => m.Groups[1].Value.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Decides `same` or `rewritten` for a target that was not refused: the function is `same` when its name (or its canonical name) still stands as a call in the text sent to the engine and
    /// no target rule fired, `rewritten` otherwise. An operator is `same` when the symbol is still in the text. Heuristic, and said so in the report: it looks at names, not meaning.
    /// </summary>
    public static StaticTarget Classify(StaticTarget t, FunctionOverload f)
    {
        if (t.Status is "refused" or "error" || t.Final == null) return t;
        bool present;
        if (f.Kind == "operator") present = t.Final.Contains(f.Name == "!__postfix" ? "!" : f.Name, StringComparison.Ordinal);
        else
        {
            var calls = CallNames(t.Final);
            present = calls.Contains(f.Name.ToUpperInvariant()) || calls.Contains(f.Canonical.ToUpperInvariant()) || (f.Name == "count_star" && calls.Contains("COUNT"));
            if (f.Kind == "macro") present = false;
        }
        var rewrites = t.Rules.Count > 0;
        return t with { Status = present && !rewrites ? "same" : "rewritten" };
    }
}
