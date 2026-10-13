using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;

namespace DbDataBuild.Planning;

/// <summary>The readable rendering of a plan (DESIGN.md 10.2): what will run, why, what was decided, what is blocked, and what was noticed but not done.</summary>
public static class PlanReport
{
    private static string Label(StepType t) => t.ToString().ToLowerInvariant();

    public static string Markdown(Plan plan, IReadOnlyList<Diagnostic>? blocks = null, IReadOnlyList<Diagnostic>? skipped = null)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        L($"# PLAN {plan.Id}");
        L();
        L($"Target: `{plan.Connection}` | commit: `{plan.GitCommit ?? "none"}`{(plan.GitDirty ? " (working tree dirty)" : "")} | tool: {plan.ToolVersion} | content hash: `{PlanDocument.ContentHash(plan)[..16]}`");
        L();
        var counts = plan.Steps.GroupBy(s => s.Type).OrderBy(g => g.Key).Select(g => $"{g.Count()} {Label(g.Key)} step{(g.Count() == 1 ? "" : "s")}").ToList();
        L(plan.Steps.Count == 0 ? "Summary: nothing to do. No statements will run." : $"Summary: {string.Join(", ", counts)}. Nothing else will run.");
        var risky = plan.Steps.Count(s => s.Risk == RiskClass.Risky);
        var destructive = plan.Steps.Count(s => s.Risk == RiskClass.Destructive);
        if (risky + destructive > 0)
            L($"**Needs allowance at apply:** {(risky > 0 ? $"{risky} risky step(s) (`--allow-risky`)" : "")}{(risky > 0 && destructive > 0 ? "; " : "")}{(destructive > 0 ? $"{destructive} destructive step(s) (`--allow-destructive <object>` for each object named)" : "")}.");
        L();

        var answersById = plan.Answers.ToDictionary(a => a.QuestionId);
        foreach (var s in plan.Steps)
        {
            L($"## {s.Id}. [{Label(s.Type)}, {s.Risk.ToString().ToLowerInvariant()}] {s.Description}");
            L();
            L("```sql");
            L(s.Text.TrimEnd());
            L("```");
            L();
            L($"- Object: `{s.Object}`");
            L($"- Why: {string.Join("; ", s.Reasons.Where(r => !r.StartsWith("answer ", StringComparison.Ordinal)))}");
            foreach (var r in s.Reasons.Where(r => r.StartsWith("answer ", StringComparison.Ordinal)))
            {
                var id = r["answer ".Length..].Split(' ')[0];
                var note = answersById.TryGetValue(id, out var a) && a.Note != null ? $" (\"{a.Note}\")" : "";
                L($"- Decided: {id}{(answersById.TryGetValue(id, out var ans) ? $" = {ans.Choice}{(ans.Value != null ? " " + ans.Value : "")} [{ans.Source}]" : "")}{note}");
            }
            if (s.HashAfter != null) L($"- Expected shape hash afterwards: `{s.HashAfter[..16]}`");
            if (s.Parameters.Count > 0) L($"- Parameters: {string.Join(", ", s.Parameters.Select(p => $"@{p.Name} ({p.Type}, {p.Source}) = {(p.Value ?? "NULL")}"))}");
            if (s.Inputs is { Count: > 0 })
            {
                L("- Inputs, as computed when the plan was made (apply computes them again; if any differs it stops and shows what moved):");
                foreach (var i in s.Inputs.Where(i => i.Source != "group"))
                    L($"  - `{i.Name}` ({i.Type}) = {i.Value ?? "NULL"}  [{i.Source}{(i.Origin != null ? " at " + i.Origin : "")}]");
            }
            if (s.HasResolver) L($"- Resolver result at plan time: {(s.ResolverResult ?? "NULL")}. Apply runs the resolver again and refuses if it differs.");
            if (s.Risk != RiskClass.Safe) L($"- Risk: {s.Risk.ToString().ToLowerInvariant()}.");
            L();
        }

        if (blocks is { Count: > 0 })
        {
            L("## Blocked (not planned)");
            L();
            foreach (var b in blocks) L($"- `{b.Code}` {b.Found}");
            L();
        }
        if (skipped is { Count: > 0 })
        {
            L("## Skipped");
            L();
            foreach (var b in skipped) L($"- `{b.Code}` {b.Found}");
            L();
        }
        if (plan.Noticed.Count > 0)
        {
            L("## Noticed but NOT done");
            L();
            foreach (var n in plan.Noticed) L($"- {n}");
            L();
        }
        return sb.ToString();
    }
}
