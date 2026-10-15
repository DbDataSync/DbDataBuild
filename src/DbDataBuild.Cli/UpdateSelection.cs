using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>
/// What `project update` was asked to look at: every model of the project (no selector), or the ones the selectors name. The selectors are the model grammar of every other command (`ProjectContext.SelectModels`,
/// <see cref="ModelSelection"/>): names and patterns, paths, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:`, `connection:`, `path:`, `changed:`, `exclude:`. A mapped or native model is a model of that grammar like
/// the others, so `kind:mapped` and `+model` (which includes the mapped models under it) work here as they do everywhere.
/// </summary>
internal sealed record UpdateSelection(bool Everything, IReadOnlySet<string> Names)
{
    public static readonly UpdateSelection All = new(true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool Includes(string name) => Everything || Names.Contains(name);

    /// <summary>The selection the arguments make, or null (with the problem written) when a term selects nothing.</summary>
    public static UpdateSelection? Resolve(ProjectContext ctx, IReadOnlyList<string> arguments, TextWriter error)
    {
        if (arguments.Count == 0) return All;
        var chosen = ctx.SelectModels(arguments, error);
        return chosen == null ? null : new UpdateSelection(false, chosen.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The connections a selected object reaches: a model's own (and, for a copy, the connections it reads from), a mapped model's.</summary>
    public IReadOnlyList<string> ConnectionsOf(ProjectContext ctx)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in ctx.Project.BuiltModels.Where(s => Includes(s.Definition.Name)))
            foreach (var target in ctx.TargetsOf(source.Definition))
            {
                names.Add(target);
                foreach (var origin in ctx.OriginsOf(source.Definition, target)) names.Add(origin.Connection);
            }
        foreach (var d in ctx.Project.DeclaredModels.Where(d => Includes(d.Name)))
            foreach (var c in d.Connections ?? ctx.Config.DefaultConnections) names.Add(c);
        return names.Order(StringComparer.Ordinal).ToList();
    }
}
