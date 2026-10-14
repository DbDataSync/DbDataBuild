using System.Text.RegularExpressions;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>
/// What `project update` was asked to look at: every model, mapped model and copy of the project (no selector), or the ones the selectors name. The selectors are the model grammar of the other commands
/// (`ProjectContext.Select`: names, paths, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:`, `connection:`, `path:`, `changed:`, `exclude:`); a mapped model has no query and is not a model of that grammar, so
/// it is also matched by name (with `*` and `?`), by those filters, and as what a selected model reads (`+model` includes the mapped models under it).
/// </summary>
internal sealed record UpdateSelection(bool Everything, IReadOnlySet<string> Names)
{
    public static readonly UpdateSelection All = new(true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool Includes(string name) => Everything || Names.Contains(name);

    /// <summary>The selection the arguments make, or null (with the problem written) when a term selects nothing.</summary>
    public static UpdateSelection? Resolve(ProjectContext ctx, IReadOnlyList<string> arguments, TextWriter error)
    {
        if (arguments.Count == 0) return All;
        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            var excluded = argument.StartsWith("exclude:", StringComparison.Ordinal);
            var term = excluded ? argument["exclude:".Length..] : argument;
            var found = Matching(ctx, term);
            if (found.Count == 0) { error.WriteLine($"`{term}` selects no model, mapped model or copy."); return null; }
            (excluded ? exclude : include).UnionWith(found);
        }
        if (arguments.All(a => a.StartsWith("exclude:", StringComparison.Ordinal))) include.UnionWith(AllNames(ctx));
        include.ExceptWith(exclude);
        return new UpdateSelection(false, include);
    }

    private static IEnumerable<string> AllNames(ProjectContext ctx) =>
        ctx.Project.Sources.Select(s => s.Definition.Name).Concat(ctx.Project.FileDescriptors.Select(d => d.Name));

    private static HashSet<string> Matching(ProjectContext ctx, string term)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = ctx.Select([term], TextWriter.Null);
        if (models != null) result.UnionWith(models.Select(m => m.Source.Definition.Name));
        var parts = term.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // a term of several parts is an intersection; the models were intersected by the model grammar, the descriptors are intersected here
        HashSet<string>? descriptors = null;
        foreach (var part in parts)
        {
            var parsed = ModelSelector.Parse(part);
            var matched = MatchDescriptors(ctx, parsed.Core);
            if (parsed.Upstream || parsed.At)
                foreach (var m in models ?? [])
                    foreach (var a in ctx.Graph.Ancestors(m.Source.Definition.Name, parsed.UpstreamDepth).Keys)
                        if (ctx.Project.FileDescriptors.Any(d => string.Equals(d.Name, a, StringComparison.OrdinalIgnoreCase))) matched.Add(a);
            if (descriptors == null) descriptors = matched; else descriptors.IntersectWith(matched);
        }
        if (descriptors != null) result.UnionWith(descriptors);
        return result;
    }

    /// <summary>The mapped and native models a core of the grammar names: a name or pattern, `kind:`, `connection:`, `tag:`, `property:`, `path:`; empty for `changed:` and for anything else.</summary>
    private static HashSet<string> MatchDescriptors(ProjectContext ctx, string core)
    {
        var all = ctx.Project.FileDescriptors;
        IEnumerable<SourceDescriptor> chosen;
        if (core is "all" or "*") chosen = all;
        else if (core.StartsWith("kind:", StringComparison.Ordinal)) { var k = core[5..]; chosen = all.Where(d => (d.IsNative ? "native" : "mapped") == k); }
        else if (core.StartsWith("tag:", StringComparison.Ordinal)) { var t = core[4..]; chosen = all.Where(d => d.Info.Tags.Contains(t, StringComparer.Ordinal)); }
        else if (core.StartsWith("connection:", StringComparison.Ordinal)) { var c = core[11..]; chosen = all.Where(d => (d.Connections ?? ctx.Config.DefaultConnections).Contains(c, StringComparer.OrdinalIgnoreCase)); }
        else if (core.StartsWith("property:", StringComparison.Ordinal))
        {
            var spec = core[9..]; var eq = spec.IndexOf('=');
            var (key, wanted) = eq < 0 ? (spec, null) : (spec[..eq], spec[(eq + 1)..]);
            chosen = all.Where(d => d.Info.Properties.TryGetValue(key, out var v) && (wanted == null || v.Text == wanted));
        }
        else if (core.StartsWith("path:", StringComparison.Ordinal)) { var dir = core[5..].Replace('\\', '/').Trim('/'); chosen = all.Where(d => d.File.StartsWith(dir + "/", StringComparison.Ordinal)); }
        else if (core.Contains(':', StringComparison.Ordinal)) chosen = [];
        else
        {
            var normalized = core.Replace('\\', '/');
            var pattern = new Regex("^" + Regex.Escape(core).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            chosen = all.Where(d => pattern.IsMatch(d.Name) || (d.File.Length > 0 && (d.File == normalized || normalized.EndsWith("/" + d.File, StringComparison.Ordinal) || d.File.StartsWith(normalized.TrimEnd('/') + "/", StringComparison.Ordinal))));
        }
        return chosen.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The connections a selected object reaches: a model's own (and, for a copy, the connections it reads from), a mapped model's.</summary>
    public IReadOnlyList<string> ConnectionsOf(ProjectContext ctx)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in ctx.Project.Sources.Where(s => Includes(s.Definition.Name)))
            foreach (var target in ctx.TargetsOf(source.Definition))
            {
                names.Add(target);
                foreach (var origin in ctx.OriginsOf(source.Definition, target)) names.Add(origin.Connection);
            }
        foreach (var d in ctx.Project.FileDescriptors.Where(d => Includes(d.Name)))
            foreach (var c in d.Connections ?? ctx.Config.DefaultConnections) names.Add(c);
        return names.Order(StringComparer.Ordinal).ToList();
    }
}
