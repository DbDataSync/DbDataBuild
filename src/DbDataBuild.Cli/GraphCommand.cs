using System.Text;
using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project show graph` (effect: offline only; DESIGN.md 9.9). The dependency graph of the project, or the part of it a selector names: which table each model reads, as a leveled list, a Graphviz or Mermaid diagram, or JSON.
/// With --columns it adds which column of which table every output column comes from; with --column it follows one column through the models, up to the columns of mapped models it comes from and down to every
/// column built from it. Nothing is connected to and nothing is written.
/// </summary>
internal static class GraphCommand
{
    public static int Run(CommandSpec spec, string root, string[] models, bool columns, string? column, string? diagram, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (diagram is not (null or "dot" or "mermaid")) { error.WriteLine("--diagram is `dot` or `mermaid`."); return CliApp.ExitUsage; }
        var ctx = ProjectContext.Load(root);
        var selected = ctx.SelectModels(models, error);
        if (selected == null) return CliApp.ExitUsage;
        var invalid = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code).ToList();
        foreach (var d in invalid) error.Diag(d);
        if (invalid.Count > 0)
        {
            output.WriteLine($"No graph: {invalid.Count} error(s) in the project (`{ProductInfo.Cli} project compile` shows them).");
            return CliApp.ExitFindings;
        }

        var graph = ctx.Graph;
        var levels = graph.Levels();
        var chosen = selected.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectModels = ctx.Models.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var natives = ctx.Project.NativeModels.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var macroNodes = ctx.Project.Macros.Definitions.Where(d => !d.IsType).ToDictionary(d => ProjectContext.MacroNode(d.ShortName), StringComparer.OrdinalIgnoreCase);
        var direct = chosen.SelectMany(graph.Reads).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var behindNatives = direct.Where(n => natives.Contains(n) || macroNodes.ContainsKey(n)).SelectMany(graph.Reads).ToList();      // a native model's `reads:` and a macro's callees are shown behind it
        var nodeNames = chosen.Concat(direct).Concat(behindNatives).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => levels.GetValueOrDefault(n)).ThenBy(n => n, StringComparer.Ordinal).ToList();
        string CategoryOf(string n) => projectModels.ContainsKey(n) ? "model" : macroNodes.ContainsKey(n) ? "macro" : "unknown";
        var nodes = nodeNames.Select(n => new
        {
            name = n, category = CategoryOf(n), kind = projectModels.TryGetValue(n, out var pm) ? pm.Kind : null, level = levels.GetValueOrDefault(n),
            file = projectModels.TryGetValue(n, out var fm) ? fm.Path : macroNodes.TryGetValue(n, out var md) ? md.File : null,
            connections = projectModels.TryGetValue(n, out var cm) ? cm.Connections : (IReadOnlyList<string>)[],
        }).ToList();
        var edges = chosen.Concat(direct.Where(n => natives.Contains(n) || macroNodes.ContainsKey(n))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).SelectMany(n => graph.Reads(n).Select(r => (From: r, To: n))).ToList();

        // columns: the edges of the chosen models, or the whole project's when one column is followed
        var allColumnEdges = new List<ColumnEdge>();
        var builtByName = ctx.Project.BuiltModels.ToDictionary(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase);
        var wantedModels = column != null ? ctx.Project.BuiltModels.ToList() : chosen.Where(builtByName.ContainsKey).Select(n => builtByName[n]).OrderBy(s => s.Definition.Name, StringComparer.Ordinal).ToList();
        if (columns || column != null) foreach (var m in wantedModels) allColumnEdges.AddRange(ProjectGraph.ColumnEdges(ctx, m));

        object? lineage = null;
        List<(string Table, string Column, int Distance)>? upstream = null, downstream = null;
        if (column != null)
        {
            var dot = column.LastIndexOf('.');
            if (dot <= 0 || dot == column.Length - 1) { error.WriteLine($"--column is `model.column` (for example `marts.fct_orders.amount`), not `{column}`."); return CliApp.ExitUsage; }
            var (table, col) = (column[..dot], column[(dot + 1)..]);
            var known = projectModels.TryGetValue(table, out var tm) && tm.Columns.Any(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase));
            if (!known) { error.WriteLine($"`{table}` has no column `{col}` (a model of the project, and one of its declared columns)."); return CliApp.ExitUsage; }
            upstream = Follow((table, col), e => allColumnEdges.Where(x => x.ToModel.Equals(e.Table, StringComparison.OrdinalIgnoreCase) && x.ToColumn.Equals(e.Column, StringComparison.OrdinalIgnoreCase) && x.FromTable != null).Select(x => (x.FromTable!, x.FromColumn!)));
            downstream = Follow((table, col), e => allColumnEdges.Where(x => x.FromTable != null && x.FromTable.Equals(e.Table, StringComparison.OrdinalIgnoreCase) && x.FromColumn!.Equals(e.Column, StringComparison.OrdinalIgnoreCase)).Select(x => (x.ToModel, x.ToColumn)));
            lineage = new
            {
                column = $"{table}.{col}",
                upstream = upstream.Select(u => new { table = u.Table, column = u.Column, distance = u.Distance }).ToList(),
                downstream = downstream.Select(u => new { table = u.Table, column = u.Column, distance = u.Distance }).ToList(),
            };
        }

        var text = diagram switch { "dot" => Dot(nodes.Select(n => (n.name, n.kind ?? n.category)).ToList(), edges), "mermaid" => Mermaid(nodes.Select(n => (n.name, n.kind ?? n.category)).ToList(), edges), _ => null };
        output.Payload("nodes", nodes);
        output.Payload("edges", edges.Select(e => new { from = e.From, to = e.To }).ToList());
        output.Payload("unknown_tables", graph.Unknown.Where(u => direct.Concat(behindNatives).Contains(u, StringComparer.OrdinalIgnoreCase)).ToList());
        if (columns || column != null)
        {
            var shown = column != null ? allColumnEdges : allColumnEdges.Where(e => chosen.Contains(e.ToModel)).ToList();
            output.Payload("column_edges", shown.Select(e => new { from_table = e.FromTable, from_column = e.FromColumn, to_model = e.ToModel, to_column = e.ToColumn, transform = e.Transform }).ToList());
        }
        if (lineage != null) output.Payload("column_lineage", lineage);
        output.Payload("diagram", text);

        if (text != null) { output.Write(text); return CliApp.ExitOk; }
        output.WriteLine();
        foreach (var n in nodes)
        {
            var reads = projectModels.ContainsKey(n.name) ? graph.Reads(n.name) : [];
            output.WriteLine($"{n.level,-2} {n.name}  ({n.kind ?? n.category}){(reads.Count > 0 ? "  <- " + string.Join(", ", reads) : "")}");
            if (columns && column == null && n.kind != null && ModelKinds.IsBuilt(n.kind))
                foreach (var g in allColumnEdges.Where(e => e.ToModel.Equals(n.name, StringComparison.OrdinalIgnoreCase)).GroupBy(e => e.ToColumn))
                    output.WriteLine($"      {g.Key} <- {string.Join(", ", g.Select(e => e.FromTable == null ? $"(constant, {e.Transform})" : $"{e.FromTable}.{e.FromColumn} ({e.Transform})"))}");
        }
        if (upstream != null && downstream != null)
        {
            output.WriteLine();
            output.WriteLine($"column {column}");
            output.WriteLine(upstream.Count == 0 ? "  comes from: nothing (a column of a mapped model, or made of constants)" : "  comes from: " + string.Join(", ", upstream.Select(u => $"{u.Table}.{u.Column}" + (u.Distance > 1 ? $" ({u.Distance} steps)" : ""))));
            output.WriteLine(downstream.Count == 0 ? "  feeds: nothing" : "  feeds: " + string.Join(", ", downstream.Select(u => $"{u.Table}.{u.Column}" + (u.Distance > 1 ? $" ({u.Distance} steps)" : ""))));
        }
        output.WriteLine();
        var modelNodes = nodes.Where(n => n.kind != null).ToList();
        var others = nodes.Where(n => n.kind == null).GroupBy(n => n.category).Select(g => $"{g.Count()} {g.Key}(s)");
        output.WriteLine($"{modelNodes.Count} model(s) ({MetadataCommand.KindCounts(modelNodes.Select(n => projectModels[n.name]))}){(others.Any() ? ", " + string.Join(", ", others) : "")}, {edges.Count} edge(s).");
        return CliApp.ExitOk;
    }

    private static List<(string Table, string Column, int Distance)> Follow((string Table, string Column) start, Func<(string Table, string Column), IEnumerable<(string Table, string Column)>> next)
    {
        var seen = new Dictionary<(string, string), int>(new CaseInsensitivePair());
        var frontier = new List<(string Table, string Column)> { start };
        for (var step = 1; frontier.Count > 0; step++)
        {
            var following = new List<(string Table, string Column)>();
            foreach (var e in frontier)
                foreach (var n in next(e).Distinct())
                    if (!n.Equals(start) && seen.TryAdd(n, step)) following.Add(n);
            frontier = following;
        }
        return seen.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).OrderBy(x => x.Item3).ThenBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList();
    }

    private sealed class CaseInsensitivePair : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) k) => HashCode.Combine(k.Item1.ToLowerInvariant(), k.Item2.ToLowerInvariant());
    }

    /// <summary>The shape a node has in a diagram: a box for a model the tool builds, a cylinder for one it declares (mapped, native), a diamond for a macro or an unknown table.</summary>
    private static string Shape(string kind) => ModelKinds.IsBuilt(kind) ? "box" : ModelKinds.Declared.Contains(kind) ? "cylinder" : "diamond";

    private static string Dot(List<(string Name, string Kind)> nodes, List<(string From, string To)> edges)
    {
        var sb = new StringBuilder("digraph dbdatabuild {\n  rankdir=LR;\n");
        string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
        foreach (var (name, kind) in nodes) sb.Append($"  {Q(name)} [shape={Shape(kind)}];\n");
        foreach (var (from, to) in edges) sb.Append($"  {Q(from)} -> {Q(to)};\n");
        return sb.Append("}\n").ToString();
    }

    private static string Mermaid(List<(string Name, string Kind)> nodes, List<(string From, string To)> edges)
    {
        var ids = nodes.Select((n, i) => (n.Name, Id: $"n{i}")).ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder("graph LR\n");
        foreach (var (name, kind) in nodes)
            sb.Append(Shape(kind) == "box" ? $"  {ids[name]}[\"{name}\"]\n" : Shape(kind) == "cylinder" ? $"  {ids[name]}[(\"{name}\")]\n" : $"  {ids[name]}{{\"{name}\"}}\n");
        foreach (var (from, to) in edges) sb.Append($"  {ids[from]} --> {ids[to]}\n");
        return sb.ToString();
    }
}
