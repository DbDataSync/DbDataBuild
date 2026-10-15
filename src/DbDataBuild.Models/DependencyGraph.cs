namespace DbDataBuild.Models;

/// <summary>
/// Which tables each model reads (DESIGN.md 9.9). Pure data: names only, so selectors and the `graph` command work on it without parsing anything. An edge runs from the table that is read to the model that
/// reads it. Unbuilt nodes are the ones no model builds (declared models, macros); a name that is neither built nor unbuilt is an unknown table (DDB-218 elsewhere) and is kept so the graph still shows it.
/// </summary>
/// <summary>What a selector needs of a graph: the things a name depends on and the things that depend on it, with their distance. The project's graph and the graph a loads file carries both answer it, so one selector grammar serves both.</summary>
public interface IModelGraph
{
    IReadOnlyDictionary<string, int> Ancestors(string name, int? depth = null);
    IReadOnlyDictionary<string, int> Descendants(string name, int? depth = null);
}

public sealed class DependencyGraph : IModelGraph
{
    private readonly Dictionary<string, HashSet<string>> reads = new(StringComparer.OrdinalIgnoreCase);        // model -> tables it reads
    private readonly Dictionary<string, HashSet<string>> readBy = new(StringComparer.OrdinalIgnoreCase);       // table -> models that read it

    public DependencyGraph(IEnumerable<string> models, IEnumerable<string> unbuilt, IEnumerable<(string Model, string Reads)> edges)
    {
        Models = models.OrderBy(m => m, StringComparer.Ordinal).ToList();
        Unbuilt = unbuilt.OrderBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var (model, table) in edges)
        {
            if (string.Equals(model, table, StringComparison.OrdinalIgnoreCase)) continue;
            Add(reads, model, table);
            Add(readBy, table, model);
        }
        var known = Models.Concat(Unbuilt).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Unknown = reads.Values.SelectMany(v => v).Where(t => !known.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(value);
    }

    public IReadOnlyList<string> Models { get; }
    public IReadOnlyList<string> Unbuilt { get; }
    public IReadOnlyList<string> Unknown { get; }

    public bool IsModel(string name) => Models.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The tables a model reads directly.</summary>
    public IReadOnlyList<string> Reads(string model) => reads.TryGetValue(model, out var s) ? s.OrderBy(x => x, StringComparer.Ordinal).ToList() : [];

    /// <summary>The models that read a table directly.</summary>
    public IReadOnlyList<string> ReadBy(string table) => readBy.TryGetValue(table, out var s) ? s.OrderBy(x => x, StringComparer.Ordinal).ToList() : [];

    /// <summary>Everything a name depends on, up to <paramref name="depth"/> steps (all when null), as table names with their distance. The name itself is not included.</summary>
    public IReadOnlyDictionary<string, int> Ancestors(string name, int? depth = null) => Walk(name, depth, Reads);

    /// <summary>Every model that depends on a name, up to <paramref name="depth"/> steps (all when null), with their distance.</summary>
    public IReadOnlyDictionary<string, int> Descendants(string name, int? depth = null) => Walk(name, depth, ReadBy);

    private static Dictionary<string, int> Walk(string start, int? depth, Func<string, IReadOnlyList<string>> next)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var frontier = new List<string> { start };
        for (var step = 1; frontier.Count > 0 && (depth == null || step <= depth); step++)
        {
            var following = new List<string>();
            foreach (var n in frontier)
                foreach (var m in next(n))
                    if (!string.Equals(m, start, StringComparison.OrdinalIgnoreCase) && seen.TryAdd(m, step)) following.Add(m);
            frontier = following;
        }
        return seen;
    }

    /// <summary>The length of the longest chain from an unbuilt node to each model: declared models and macros are level 0, a model that reads only sources is level 1.</summary>
    public IReadOnlyDictionary<string, int> Levels()
    {
        var level = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Of(string name, HashSet<string> path)
        {
            if (level.TryGetValue(name, out var l)) return l;
            if (!IsModel(name)) return 0;
            if (!path.Add(name)) return 0;                                  // a cycle is reported elsewhere (DDB-221); here it just ends
            var result = 1 + Reads(name).Select(r => Of(r, path)).DefaultIfEmpty(0).Max();
            path.Remove(name);
            return level[name] = result;
        }
        foreach (var m in Models) Of(m, []);
        return level;
    }
}

/// <summary>One term of a model selector: `[N+]core[+N]` or `@core`, where core is a name, a path, or `kind:`, `connection:`, `path:`, `changed:`.</summary>
public sealed record SelectorTerm(string Core, bool Upstream, int? UpstreamDepth, bool Downstream, int? DownstreamDepth, bool At);

public static class ModelSelector
{
    /// <summary>
    /// Parses `+model` (it and everything it reads), `model+` (it and everything that depends on it), `2+model` and `model+1` (limited to that many steps), `+model+`, and `@model`
    /// (it, everything that depends on it, and everything those need to be built). A comma joins terms into an intersection: `kind:full,marts.fct_orders+`.
    /// </summary>
    public static SelectorTerm Parse(string token)
    {
        var text = token.Trim();
        var at = text.StartsWith('@');
        if (at) text = text[1..];
        bool up = false, down = false;
        int? upDepth = null, downDepth = null;
        var lead = System.Text.RegularExpressions.Regex.Match(text, @"^(\d*)\+");
        if (lead.Success) { up = true; upDepth = lead.Groups[1].Length > 0 ? int.Parse(lead.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null; text = text[lead.Length..]; }
        var trail = System.Text.RegularExpressions.Regex.Match(text, @"\+(\d*)$");
        if (trail.Success) { down = true; downDepth = trail.Groups[1].Length > 0 ? int.Parse(trail.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null; text = text[..trail.Index]; }
        return new SelectorTerm(text, up, upDepth, down, downDepth, at);
    }

    /// <summary>The set a term selects, given the models its core names. <paramref name="isModel"/> says which names of the graph are models of the project (all seven kinds; macros and unknown tables are not).</summary>
    public static HashSet<string> Expand(SelectorTerm term, IEnumerable<string> core, IModelGraph graph, Func<string, bool> isModel)
    {
        var result = new HashSet<string>(core, StringComparer.OrdinalIgnoreCase);
        var start = result.ToList();
        if (term.At)
        {
            var below = start.SelectMany(m => graph.Descendants(m).Keys).Where(isModel).ToList();
            result.UnionWith(below);
            foreach (var m in result.ToList()) result.UnionWith(graph.Ancestors(m).Keys.Where(isModel));
            return result;
        }
        if (term.Upstream) foreach (var m in start) result.UnionWith(graph.Ancestors(m, term.UpstreamDepth).Keys.Where(isModel));
        if (term.Downstream) foreach (var m in start) result.UnionWith(graph.Descendants(m, term.DownstreamDepth).Keys.Where(isModel));
        return result;
    }
}
