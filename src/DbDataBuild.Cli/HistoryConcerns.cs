using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;

namespace DbDataBuild.Cli;

/// <summary>
/// Where an unacknowledged history inconsistency (DDB-443: a backfill was requested for a new column and none is recorded) reaches the models a plan is about to load (DDB-240). Column lineage is followed from each
/// such column to the columns built from it, through any number of models; a planned model with such a column downstream gets a finding whose severity is `policy.severity.history_inconsistency`. The model that
/// owns the column is the subject of the acknowledgement and is not reported against itself.
/// </summary>
internal static class HistoryConcerns
{
    public static IReadOnlyList<Diagnostic> Check(ProjectContext ctx, IReadOnlyList<string> plannedModels, IReadOnlyList<ColumnHistoryEntry> history)
    {
        var open = history.Where(h => h.NeedsAttention).ToList();
        if (open.Count == 0 || plannedModels.Count == 0) return [];
        var edges = ctx.Project.BuiltModels.SelectMany(s => ProjectGraph.ColumnEdges(ctx, s)).Where(e => e.FromTable != null && e.FromColumn != null).ToList();
        var bySource = edges.ToLookup(e => (e.FromTable!.ToLowerInvariant(), e.FromColumn!.ToLowerInvariant()));

        // column -> the entries it comes from (the first found, for the message)
        var reached = new Dictionary<(string Model, string Column), (ColumnHistoryEntry Entry, string Via)>();
        foreach (var entry in open)
        {
            var queue = new Queue<(string Model, string Column, string Via)>();
            queue.Enqueue((entry.Model.ToLowerInvariant(), entry.Column.ToLowerInvariant(), $"{entry.Model}.{entry.Column}"));
            var seen = new HashSet<(string, string)> { (entry.Model.ToLowerInvariant(), entry.Column.ToLowerInvariant()) };
            while (queue.Count > 0)
            {
                var (model, column, via) = queue.Dequeue();
                foreach (var e in bySource[(model, column)])
                {
                    var key = (e.ToModel.ToLowerInvariant(), e.ToColumn.ToLowerInvariant());
                    if (!seen.Add(key)) continue;
                    reached.TryAdd(key, (entry, via));
                    queue.Enqueue((key.Item1, key.Item2, $"{via} > {e.ToModel}.{e.ToColumn}"));
                }
            }
        }

        var severity = ctx.Config.Policy.GetValueOrDefault(PolicyKeys.HistoryInconsistency, Severity.Warning);
        var findings = new List<Diagnostic>();
        foreach (var model in plannedModels.Order(StringComparer.Ordinal))
        {
            var hits = reached.Where(r => string.Equals(r.Key.Model, model, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Key.Column, StringComparer.Ordinal).ToList();
            if (hits.Count == 0) continue;
            var shown = string.Join("; ", hits.Take(3).Select(h => $"`{h.Key.Column}` comes from {h.Value.Via}"));
            var more = hits.Count > 3 ? $" and {hits.Count - 3} more column(s)" : "";
            var origins = string.Join(", ", hits.Select(h => $"{h.Value.Entry.Model}.{h.Value.Entry.Column}").Distinct(StringComparer.Ordinal));
            findings.Add(new Diagnostic(DiagnosticCatalog.ReadsInconsistentHistory, new(ctx.Project.BuiltModels.FirstOrDefault(s => s.Definition.Name == model)?.DefinitionFile ?? model, 0, 0),
                $"{model} is built from a column whose history is inconsistent ({shown}{more}): a backfill was requested for {origins} and none is recorded, so rows loaded before it hold NULL there.",
                Fix: $"Backfill it (`{ProductInfo.Cli} connection deploy --backfill <model>=<operation>`), or accept it: `{ProductInfo.Cli} connection deploy --ack history:{hits[0].Value.Entry.Model}.{hits[0].Value.Entry.Column} --reason <why>`.") with { SeverityOverride = severity });
        }
        return findings;
    }
}
