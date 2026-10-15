using DbDataBuild.Models;

namespace DbDataBuild.Define;

/// <summary>An upstream table as `define` sees it: its declared columns, optional grain, and whether it is a model or a source.</summary>
public sealed record UpstreamTable(string Name, IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> Grain, bool IsModel);

/// <summary>
/// Resolves the plain table names a query uses (DESIGN.md 6: there is no ref()) against the project's models and committed source
/// descriptors. Names match case-insensitively. A model defined earlier in the same run can be supplied with <see cref="Provide"/>.
/// </summary>
public sealed class ModelGraph
{
    private readonly Dictionary<string, UpstreamTable> tables = new(StringComparer.OrdinalIgnoreCase);

    public ModelGraph(IEnumerable<ModelDefinition> models, IEnumerable<DeclaredModel> sources)
    {
        foreach (var m in models) tables[m.Name] = new UpstreamTable(m.Name, m.Columns, m.Grain, IsModel: true);
        foreach (var s in sources) tables.TryAdd(s.Name, new UpstreamTable(s.Name, s.Columns, s.Grain, IsModel: false));
    }

    public UpstreamTable? Find(string qualifiedName) => tables.GetValueOrDefault(qualifiedName);

    /// <summary>Makes a model's (re)defined columns visible to the models that depend on it.</summary>
    public void Provide(string name, IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<string> grain) =>
        tables[name] = new UpstreamTable(name, columns, grain, IsModel: true);

    public IReadOnlyCollection<string> Names => tables.Keys;
}
