using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>
/// A model of the project as every consumer that is not building it sees it: the same facts for all seven model kinds, whether the tool builds it (view, full, the two incrementals, copy) or only declares it (mapped,
/// native). Selectors, the dependency graph, the counts, the metadata documents and the interfaces read this and do not ask which of the two classes a model is. It is read-only and holds no query: the facts a
/// selector reads are in it (and are what `loads.yml` stores for a loaded model, so a refresh can select without the project).
/// </summary>
/// <param name="Connections">The connections the model lives on: its own, else the project's default.</param>
/// <param name="DefinitionFile">The project-relative path of the file that defines it.</param>
/// <param name="QueryFile">The project-relative path of its query file; null for a model with none (a copy, a mapped or native model).</param>
public sealed record ProjectModel(string Name, string Kind, IReadOnlyList<string> Connections, ObjectInfo Info, string DefinitionFile, string? QueryFile)
{
    /// <summary>True for a model the tool builds (it has a query or an origin to load from); false for mapped and native models, which it only declares.</summary>
    public bool IsBuilt => ModelKinds.IsBuilt(Kind);

    /// <summary>Where the model is written, for the `path:` selector: its query file, else its definition file.</summary>
    public string Path => QueryFile ?? DefinitionFile;

    public IReadOnlyList<string> Tags => Info.Tags;

    /// <summary>The declared columns (empty for a model read from a compiled plan, which keeps only what selectors need).</summary>
    public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

    public IReadOnlyList<string> Grain { get; init; } = [];
}

public static class ProjectModels
{
    /// <summary>Every model of the project, built and declared, ordered by name. Generated staging tables of copies are not models of the project.</summary>
    public static IReadOnlyList<ProjectModel> Of(ProjectValidationResult project, IReadOnlyList<string> defaultConnections) =>
        project.BuiltModels.Select(b => Of(b, defaultConnections)).Concat(project.DeclaredModels.Select(d => Of(d, defaultConnections)))
            .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();

    public static ProjectModel Of(BuiltModel model, IReadOnlyList<string> defaultConnections) =>
        new(model.Definition.Name, model.Definition.Kind, model.Definition.Targets ?? defaultConnections, model.Definition.Info, model.DefinitionFile, model.QueryFile.Length == 0 || model.QueryFile == model.DefinitionFile ? null : model.QueryFile) { Columns = model.Definition.Columns, Grain = model.Definition.Grain };

    public static ProjectModel Of(DeclaredModel model, IReadOnlyList<string> defaultConnections) =>
        new(model.Name, model.Kind, model.Connections ?? defaultConnections, model.Info, model.File, null) { Columns = model.Columns, Grain = model.Grain };
}

/// <summary>What the selector grammar needs from outside the model list: the connections (for the hint on a term that names none) and the one term that reads the working tree and git.</summary>
/// <param name="Changed">The models a `changed:&lt;git ref&gt;` term chooses, or null with the problem written; null when the caller has no project to ask (a refresh of a compiled plan), which is said where the term is used.</param>
public sealed record SelectionHost(IReadOnlyCollection<string> Connections, Func<string, TextWriter, IReadOnlySet<string>?>? Changed = null)
{
    /// <summary>Said in the error for a name that is none of the models, when the models are not all the project's ("among the models this loads file loads").</summary>
    public string? Scope { get; init; }
}

/// <summary>
/// The model selector grammar (DESIGN.md 9.9), once, over <see cref="ProjectModel"/>s and a <see cref="DependencyGraph"/>: a model name or pattern (`*` and `?`), a file or a directory, the graph operators `+model`,
/// `model+`, `2+model`, `model+1`, `@model`, the filters `kind:`, `tag:`, `property:key=value`, `connection:`, `path:`, `changed:&lt;git ref&gt;`, a comma for an intersection, and `exclude:&lt;selector&gt;`. It is the grammar of every command that
/// selects models, project selectors and refresh selectors alike, and it works the same on a mapped or native model as on a built one.
/// </summary>
public sealed class ModelSelection(IReadOnlyList<ProjectModel> models, IModelGraph graph, SelectionHost host)
{
    /// <summary>The names the arguments select (every model when there are none), in name order. Null, with the problem written, when a term selects nothing.</summary>
    public List<string>? Select(IReadOnlyList<string> args, TextWriter error)
    {
        var chosen = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includes = args.Where(a => !a.StartsWith("exclude:", StringComparison.Ordinal)).ToList();
        if (includes.Count == 0) foreach (var m in models) if (seen.Add(m.Name)) chosen.Add(m.Name);
        foreach (var arg in includes)
        {
            var set = Term(arg, error);
            if (set == null) return null;
            foreach (var name in set) if (seen.Add(name)) chosen.Add(name);
        }
        foreach (var arg in args.Where(a => a.StartsWith("exclude:", StringComparison.Ordinal)))
        {
            var set = Term(arg["exclude:".Length..], error);
            if (set == null) return null;
            excluded.UnionWith(set);
        }
        var known = models.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        return chosen.Where(n => !excluded.Contains(n) && known.ContainsKey(n)).Select(n => known[n].Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>One argument: terms joined by commas are intersected, each term is an optional operator around a core.</summary>
    private HashSet<string>? Term(string arg, TextWriter error)
    {
        HashSet<string>? result = null;
        var isModel = models.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var part in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var term = ModelSelector.Parse(part);
            var core = Core(term.Core, error);
            if (core == null) return null;
            var set = term is { Upstream: false, Downstream: false, At: false } ? new HashSet<string>(core, StringComparer.OrdinalIgnoreCase) : ModelSelector.Expand(term, core, graph, isModel.Contains);
            if (result == null) result = set; else result.IntersectWith(set);
        }
        return result ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private List<string>? Core(string core, TextWriter error)
    {
        List<string>? Found(IEnumerable<ProjectModel> found, string what)
        {
            var names = found.Select(m => m.Name).ToList();
            if (names.Count == 0) { error.WriteLine($"`{core}` selects no model ({what})."); return null; }
            return names;
        }
        if (core is "all" or "*") return models.Select(m => m.Name).ToList();
        if (core.StartsWith("kind:", StringComparison.Ordinal)) { var k = core[5..]; return Found(models.Where(m => string.Equals(m.Kind, k, StringComparison.OrdinalIgnoreCase)), $"no model has the kind `{k}`"); }
        if (core.StartsWith("tag:", StringComparison.Ordinal)) { var t = core[4..]; return Found(models.Where(m => m.Tags.Contains(t, StringComparer.Ordinal)), $"no model has the tag `{t}`"); }
        if (core.StartsWith("property:", StringComparison.Ordinal))
        {
            // `property:key=value` (the value as text, whatever its type; a comma cannot be in it) or `property:key` (has the property)
            var spec = core[9..]; var eq = spec.IndexOf('=');
            var (key, wanted) = eq < 0 ? (spec, null) : (spec[..eq], spec[(eq + 1)..]);
            return Found(models.Where(m => m.Info.Properties.TryGetValue(key, out var v) && (wanted == null || v.Text == wanted)), wanted == null ? $"no model has the property `{key}`" : $"no model has the property `{key}` with the value `{wanted}`");
        }
        if (core.StartsWith("connection:", StringComparison.Ordinal)) { var t = core[11..]; return Found(models.Where(m => m.Connections.Contains(t, StringComparer.OrdinalIgnoreCase)), $"no model is on `{t}`"); }
        if (core.StartsWith("path:", StringComparison.Ordinal)) { var d = core[5..].Replace('\\', '/').Trim('/'); return Found(models.Where(m => m.Path.StartsWith(d + "/", StringComparison.Ordinal)), $"no model is under `{d}/`"); }
        if (core.StartsWith("changed:", StringComparison.Ordinal))
        {
            if (host.Changed == null) { error.WriteLine("`changed:` reads the working tree and git, which a compiled plan does not carry: it needs the project."); return null; }
            var changed = host.Changed(core[8..], error);
            return changed == null ? null : changed.OrderBy(n => n, StringComparer.Ordinal).ToList();                       // nothing changed is a valid, empty answer
        }

        var normalized = core.Replace('\\', '/');
        var byName = models.Where(m => string.Equals(m.Name, core, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 0 && core.IndexOfAny(['*', '?']) >= 0)
        {
            var pattern = new Regex("^" + Regex.Escape(core).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            byName = models.Where(m => pattern.IsMatch(m.Name)).ToList();
        }
        var byPath = models.Where(m => Files(m).Any(f => normalized == f || normalized.EndsWith("/" + f, StringComparison.Ordinal))).ToList();
        var byDir = models.Where(m => normalized.TrimEnd('/') is var d && m.Path.StartsWith(d + "/", StringComparison.Ordinal)).ToList();
        var found = byName.Count > 0 ? byName : byPath.Count > 0 ? byPath : byDir;
        if (found.Count == 0)
        {
            error.WriteLine($"`{core}` does not name a valid model, a model file, or a directory containing models{(host.Scope == null ? "" : " among " + host.Scope)}.{(Regex.IsMatch(core, "^[A-Za-z][A-Za-z0-9_]*$") ? $" (If it was meant to be a connection: the project's are {string.Join(", ", host.Connections.Order(StringComparer.Ordinal))}.)" : "")}");
            return null;
        }
        return found.Select(m => m.Name).ToList();
    }

    private static IEnumerable<string> Files(ProjectModel m) => m.QueryFile == null ? [m.DefinitionFile] : [m.QueryFile, m.DefinitionFile];
}
