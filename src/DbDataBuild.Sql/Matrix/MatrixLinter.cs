using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Ast;

namespace DbDataBuild.Sql.Matrix;

/// <summary>
/// Maps the AST of a model body (DuckDB dialect) onto the support matrix and reports, per declared target, what each
/// construct costs. Offline: parses only, never connects. Constructs the matrix does not cover are reported, not assumed safe.
/// </summary>
public sealed class MatrixLinter(SupportMatrix matrix)
{
    private static readonly string[] QueryRoots = ["select", "union", "intersect", "except", "subquery"];

    // Select fields that may be non-null without the matrix saying anything: the clauses the passing cases exercised.
    private static readonly HashSet<string> CoveredSelectFields =
    [
        "expressions", "from", "joins", "where_clause", "group_by", "having", "qualify", "order_by", "limit", "offset",
        "distinct", "distinct_on", "sample", "with", "leading_comments",
    ];

    private static readonly HashSet<string> CoveredJoinKinds = ["Inner", "Left", "Right", "Full", "Cross", "Natural"];

    /// <param name="config">Supplies target versions (to resolve min_version rows) and severity policy. Defaults apply if null.</param>
    public IReadOnlyList<Diagnostic> Lint(string sql, string file, IReadOnlyList<string> targets, ProjectConfig? config = null)
    {
        config ??= ProjectConfig.Default;
        var diags = new List<Diagnostic>();
        var parsed = Polyglot.Parse(sql, Dialects.Canonical);
        if (!parsed.Ok)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.SqlParseFailure, new(file, 0, 0), $"The DuckDB parser reported: {parsed.Error}", Fix: SqlParseHints.Fix(sql, parsed.Error)));
            return diags;
        }

        var root = AstNode.Parse(parsed.Data!);
        if (root.Children.Count != 1 || !QueryRoots.Contains(root.Children[0].Type))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.NotASingleSelect, new(file, 0, 0),
                root.Children.Count != 1 ? $"The file contains {root.Children.Count} statements." : $"The statement is a {root.Children[0].Type}, not a query."));
            return diags;
        }

        var reportedUncovered = new HashSet<string>();
        foreach (var node in root.Descendants())
        {
            var (line, col) = node.Location();
            var loc = new SourceLocation(file, line, col);
            var matched = matrix.Rows.Where(r => r.Detect.Any(d => Matches(d, node))).ToList();

            foreach (var row in matched)
            {
                var rowLoc = LocateRow(row, node, file) ?? loc;
                foreach (var target in targets)
                    diags.AddRange(Report(row, target, rowLoc, config, Describe(node)));
            }

            if (!matched.Any(r => r.Detect.Any(d => d.Kind is DetectKind.Node or DetectKind.Function)))
                CheckCovered(node, loc, reportedUncovered, diags, config);
            CheckClauses(node, loc, reportedUncovered, diags, config);
        }
        return diags;
    }

    /// <summary>Names the call or expression that matched a row, so a message about a row that covers many functions says which one it was: `ACOSH(...)`, `DATE_PART('week', ...)`.</summary>
    private static string? Describe(AstNode node)
    {
        if (node.Type is not ("function" or "aggregate_function")) return null;
        var name = node.GetString("name")?.ToUpperInvariant();
        if (name == null) return null;
        if (node.TryGet("args", out var args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && LiteralText(args[0]) is { } first) return $"`{name}('{first}', ...)`";
        return $"`{name}(...)`";
    }

    private static string? LiteralText(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("literal", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty("literal_type", out var t) && t.GetString() == "string" &&
        l.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static SourceLocation? LocateRow(ConstructRow row, AstNode node, string file)
    {
        foreach (var d in row.Detect.Where(d => d.Kind == DetectKind.Detector && Matches(d, node)))
            if (Detectors.LocationField.TryGetValue(d.Name, out var field) && node.LocationOfField(field) is var (line, col))
                return new SourceLocation(file, line, col);
        return null;
    }

    private static bool Matches(DetectRule rule, AstNode node) => rule.Kind switch
    {
        DetectKind.Node => node.Type == rule.Name,
        DetectKind.Function => node.Type is "function" or "aggregate_function" && string.Equals(node.GetString("name"), rule.Name, StringComparison.OrdinalIgnoreCase),
        DetectKind.Detector => Detectors.All[rule.Name](node),
        _ => false,
    };

    private IEnumerable<Diagnostic> Report(ConstructRow row, string target, SourceLocation loc, ProjectConfig config, string? what = null)
    {
        if (!row.Targets.TryGetValue(config.EngineOf(target) ?? target, out var entry)) yield break;       // the matrix is per engine; `target` is a connection (or an engine name)
        var note = string.IsNullOrEmpty(entry.Note) ? "" : " " + entry.Note;
        string Found(string verb) => $"`{row.Id}` is {verb} on {target}{(what == null ? "" : $" ({what})")}.{note}";
        Severity? Policy(string key) => config.Policy.TryGetValue(key, out var sv) ? sv : null;

        // A configured engine version settles min_version rows. Below the minimum the construct does not work there.
        var versionKnown = config.TargetVersions.TryGetValue(target, out var version);
        if (entry.MinVersion is { } min && versionKnown && version < min && entry.Status is not SupportStatus.Unsupported)
        {
            yield return new Diagnostic(DiagnosticCatalog.ConstructUnsupported, loc,
                $"`{row.Id}` needs {target} version {min} or later, but the project configures version {version}.{note}",
                Fix: $"Raise `connections.{target}.version` if the engine is newer, avoid `{row.Id}`, or remove `{target}` from `connections:`.");
            yield break;
        }

        switch (entry.Status)
        {
            case SupportStatus.Unsupported:
                yield return new Diagnostic(DiagnosticCatalog.ConstructUnsupported, loc, Found("unsupported"),
                    Fix: $"Rewrite the model without `{row.Id}`, or remove `{target}` from `connections:`.");
                break;
            case SupportStatus.Approximated:
                yield return new Diagnostic(DiagnosticCatalog.ConstructApproximated, loc, Found("approximated"), SeverityOverride: Policy(PolicyKeys.Approximated));
                break;
            case SupportStatus.Emulated:
                yield return new Diagnostic(DiagnosticCatalog.ConstructEmulated, loc, Found("emulated"), SeverityOverride: Policy(PolicyKeys.Emulated));
                break;
            case SupportStatus.Unverified:
                yield return new Diagnostic(DiagnosticCatalog.ConstructUnverified, loc, Found("unverified"), SeverityOverride: Policy(PolicyKeys.Unverified));
                break;
        }
        if (entry.MinVersion is { } v && !versionKnown && entry.Status is not SupportStatus.Unsupported)
            yield return new Diagnostic(DiagnosticCatalog.ConstructNeedsVersion, loc,
                $"`{row.Id}` on {target} needs engine version {v} or later, and no version is configured.",
                Fix: $"Set `connections.{target}.version` in {ProductInfo.ConfigFile}, or avoid `{row.Id}`.");
    }

    private void CheckCovered(AstNode node, SourceLocation loc, HashSet<string> seen, List<Diagnostic> diags, ProjectConfig config)
    {
        if (node.Type == "function")
        {
            var name = node.GetString("name") ?? "<unnamed>";
            if (name.StartsWith("ddb_", StringComparison.OrdinalIgnoreCase)) return;           // a marker a target rule wrote (ddb_idiv, ddb_xor): the tool's own, turned into an operator after the transpile
            if (!matrix.IsCoveredFunction(name) && seen.Add($"fn:{name}:{loc.Line}:{loc.Column}"))
                diags.Add(NotCovered(config, loc, $"function `{name.ToUpperInvariant()}`"));
            return;
        }
        if (!matrix.IsCoveredNode(node.Type) && seen.Add($"node:{node.Type}:{loc.Line}:{loc.Column}"))
            diags.Add(NotCovered(config, loc, $"expression `{node.Type}`"));

        if (node.Type is "cast" or "try_cast" && node.TryGet("to", out var to) && to.ValueKind == JsonValueKind.Object &&
            to.TryGetProperty("data_type", out var dt) && dt.GetString() is { } type &&
            !matrix.Covered.Any(c => c.Kind == DetectKind.DataType && c.Name == type) && seen.Add($"type:{type}:{loc.Line}:{loc.Column}"))
            diags.Add(NotCovered(config, loc, $"data type `{type}` in a cast"));
    }

    private static void CheckClauses(AstNode node, SourceLocation loc, HashSet<string> seen, List<Diagnostic> diags, ProjectConfig config)
    {
        if (node.Type != "select") return;
        foreach (var p in node.Body.EnumerateObject())
            if (!CoveredSelectFields.Contains(p.Name) && Detectors.NonNull(node, p.Name) && seen.Add($"clause:{p.Name}:{loc.Line}:{loc.Column}"))
                diags.Add(NotCovered(config, loc, $"clause `{p.Name}`"));

        foreach (var j in Detectors.Joins(node))
            if (j.TryGetProperty("kind", out var k) && k.GetString() is { } kind && !CoveredJoinKinds.Contains(kind) && seen.Add($"join:{kind}:{loc.Line}:{loc.Column}"))
                diags.Add(NotCovered(config, loc, $"join kind `{kind}`"));
    }

    private static Diagnostic NotCovered(ProjectConfig config, SourceLocation loc, string what) =>
        new(DiagnosticCatalog.ConstructNotCovered, loc, $"The {what} is not covered by the support matrix, so its behavior on the targets is unknown.",
            SeverityOverride: config.Policy.TryGetValue(PolicyKeys.NotCovered, out var sv) ? sv : null);
}
