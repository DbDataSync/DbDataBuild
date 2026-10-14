using System.Security.Cryptography;
using System.Text;
using DbDataBuild.Core;

namespace DbDataBuild.Models;

/// <summary>Why a column set is worth an index: the load reads or merges on it.</summary>
public enum IndexReason { MergeKey, TimeColumn, Watermark, RangeColumn }

/// <summary>
/// One piece of advice: the model's loads use <see cref="Columns"/> as their access path and no declared index leads with them.
/// <see cref="Existing"/> is set when an index leads with the columns but is not unique and the use is a merge key (advice is then a note, not a warning).
/// </summary>
public sealed record IndexAdvice(IndexReason Reason, IReadOnlyList<string> Columns, bool WantUnique, string SuggestedName, string? Existing, IReadOnlyList<string> Targets)
{
    public Severity Severity => Reason == IndexReason.MergeKey && Existing == null ? Severity.Warning : Severity.Note;
    public string Code => Severity == Severity.Warning ? DiagnosticCatalog.MergeKeyNotIndexed.Code : DiagnosticCatalog.LoadColumnNotIndexed.Code;
    public IndexDefinition Suggested => new(SuggestedName, Columns, WantUnique, [], null);
}

/// <summary>
/// Index lint (DESIGN.md 6, indexes): looks at what the model's loads do and says where a declared index is missing. Nothing is inferred from `unique_key`
/// (the operator decides what is enforced), so this only advises, and the advice carries the exact index to declare. A merge on a key that no index leads
/// with scans the table on every load, so that is a warning; a key that is indexed but not unique, and the time, watermark and range columns of the other
/// load strategies, are notes.
/// </summary>
public static class IndexAdvisor
{
    public static IReadOnlyList<IndexAdvice> For(ModelDefinition def, IReadOnlyList<string> targets)
    {
        if (def.Kind == ModelKinds.View || targets.Count == 0) return [];
        var wanted = new List<(IndexReason Reason, IReadOnlyList<string> Columns, string Target)>();
        foreach (var target in targets)
            foreach (var op in LoadPlan.For(def, target))
            {
                // key-based strategies look rows up by key; watermark_append reads the watermark column; delete_insert_by_range deletes by range column
                if (op.Strategy is LoadStrategies.MergeByKey or LoadStrategies.DeleteInsertByKey && op.Key.Count > 0) wanted.Add((IndexReason.MergeKey, op.Key, target));
                if (op.Strategy == LoadStrategies.WatermarkAppend && op.Watermark?.Column is { } w)
                    wanted.Add((string.Equals(w, def.TimeColumn, StringComparison.OrdinalIgnoreCase) ? IndexReason.TimeColumn : IndexReason.Watermark, [w], target));
                if (op.Strategy == LoadStrategies.DeleteInsertByRange && op.Column is { } c)
                    wanted.Add((string.Equals(c, def.TimeColumn, StringComparison.OrdinalIgnoreCase) ? IndexReason.TimeColumn : IndexReason.RangeColumn, [c], target));
            }

        var advice = new List<IndexAdvice>();
        foreach (var group in wanted.GroupBy(w => (w.Reason, Key: string.Join(",", w.Columns.Select(c => c.ToLowerInvariant()).Order(StringComparer.Ordinal)))))
        {
            var columns = group.First().Columns;
            var forTargets = group.Select(g => g.Target).Distinct().ToList();
            // an index that leads with the columns (as a set) and applies to the target; for a key it must also be unique to count as enforcing it
            var wantUnique = group.Key.Reason == IndexReason.MergeKey;
            var want = columns.Select(c => c.ToLowerInvariant()).ToHashSet();
            IEnumerable<IndexDefinition> Leading(string t) => def.Indexes.Where(i => i.AppliesTo(t) && i.Columns.Count >= columns.Count && i.Columns.Take(columns.Count).Select(c => c.ToLowerInvariant()).ToHashSet().SetEquals(want));
            bool Satisfied(string t) => Leading(t).Any(i => !wantUnique || (i.Unique && i.Columns.Count == columns.Count));
            var open = forTargets.Where(t => !Satisfied(t)).ToList();
            if (open.Count == 0) continue;
            advice.Add(new IndexAdvice(group.Key.Reason, columns, wantUnique, NameFor(def.Name, columns, wantUnique), open.SelectMany(Leading).FirstOrDefault()?.Name, open));
        }
        return advice.OrderBy(a => a.Reason).ThenBy(a => a.SuggestedName, StringComparer.Ordinal).ToList();
    }

    /// <summary>`ux_<table>_<columns>` for a unique index, `ix_` otherwise; at most 60 characters (PostgreSQL's limit is 63), with a short hash when it had to be cut.</summary>
    public static string NameFor(string model, IReadOnlyList<string> columns, bool unique)
    {
        var table = model[(model.LastIndexOf('.') + 1)..];
        var raw = $"{(unique ? "ux" : "ix")}_{table}_{string.Join("_", columns)}".ToLowerInvariant();
        var clean = new string(raw.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        if (clean.Length <= 60) return clean;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clean)))[..6].ToLowerInvariant();
        return clean[..53] + "_" + hash;
    }

    /// <summary>The YAML to paste under `indexes:` for this advice.</summary>
    public static string Yaml(IndexAdvice a) =>
        $"- {{name: {a.SuggestedName}, columns: [{string.Join(", ", a.Columns)}]{(a.WantUnique ? ", unique: true" : "")}}}";
}
