using DbDataBuild.Models;

namespace DbDataBuild.Planning;

/// <summary>
/// The dependency graph a loads file carries (<see cref="LoadsFile.Dependencies"/>), read for what a refresh needs of it: which loads a load waits for, which loads a failed load makes unreachable, and the model
/// selectors that need only the graph and the kinds. It is built from the rows alone, so a refresh needs no project files. The rows are already the transitive closure with the shortest depth, so a walk is a lookup.
/// </summary>
public sealed class LoadGraph : IModelGraph
{
    private readonly Dictionary<string, Dictionary<string, int>> ancestors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> descendants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> kinds = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="loaded">The models the file loads (a model with no edge is still in the graph).</param>
    public LoadGraph(IEnumerable<string> loaded, IEnumerable<LoadDependency> rows)
    {
        foreach (var m in loaded) kinds.TryAdd(m, "");
        foreach (var d in rows)
        {
            kinds[d.Model] = d.ModelKind;
            kinds[d.DependsOn] = d.DependsOnKind;
            if (!ancestors.TryGetValue(d.Model, out var up)) ancestors[d.Model] = up = new(StringComparer.OrdinalIgnoreCase);
            if (!descendants.TryGetValue(d.DependsOn, out var down)) descendants[d.DependsOn] = down = new(StringComparer.OrdinalIgnoreCase);
            up[d.DependsOn] = d.Depth;
            down[d.Model] = d.Depth;
        }
    }

    /// <summary>The graph of a file, or null when the file carries none (it was compiled before the graph was carried).</summary>
    public static LoadGraph? Of(LoadsFile file) => file.Dependencies == null ? null : new LoadGraph(file.Loads.Select(l => l.Model).Distinct(StringComparer.Ordinal), file.Dependencies);

    public IReadOnlyCollection<string> Models => kinds.Keys;
    public bool Has(string model) => kinds.ContainsKey(model);

    /// <summary>The model kind of a loaded model, as far as the rows say (a model no edge names has none here).</summary>
    public string? KindOf(string model) => kinds.TryGetValue(model, out var k) && k.Length > 0 ? k : null;

    /// <summary>Everything the model depends on among the loaded models, up to <paramref name="depth"/> steps (all when null), with the distance.</summary>
    public IReadOnlyDictionary<string, int> Ancestors(string model, int? depth = null) => Within(ancestors, model, depth);

    /// <summary>Every loaded model that depends on the model, up to <paramref name="depth"/> steps (all when null), with the distance.</summary>
    public IReadOnlyDictionary<string, int> Descendants(string model, int? depth = null) => Within(descendants, model, depth);

    private static IReadOnlyDictionary<string, int> Within(Dictionary<string, Dictionary<string, int>> map, string model, int? depth) =>
        !map.TryGetValue(model, out var all) ? new Dictionary<string, int>() : all.Where(e => depth == null || e.Value <= depth).ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
}
