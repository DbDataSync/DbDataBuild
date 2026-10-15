using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;

namespace DbDataBuild.Cli;

/// <summary>Builds the <see cref="DependencyGraph"/> of a project from its queries, and the column-level lineage on request.</summary>
internal static class ProjectGraph
{
    public static DependencyGraph Build(ProjectContext ctx)
    {
        var edges = new List<(string Model, string Reads)>();
        foreach (var m in ctx.Project.BuiltModels)
        {
            // a model that names something by a parameter reads other tables on connections that give other names: the graph has all of them
            foreach (var sql in ctx.QueryVariants(m))
            {
                foreach (var t in ctx.BaseTablesOf(m, sql)) edges.Add((m.Definition.Name, t));
                foreach (var macro in ctx.MacrosCalledBy(sql)) edges.Add((m.Definition.Name, macro));        // the model depends on the macro itself as well as on what it expands to
            }
        }
        foreach (var macro in ctx.Project.Macros.Definitions.Where(d => !d.IsType))
            foreach (var callee in macro.Calls) edges.Add((ProjectContext.MacroNode(macro.ShortName), ProjectContext.MacroNode(callee)));
        foreach (var n in ctx.Project.NativeModels)
            foreach (var read in n.Native?.Reads ?? []) edges.Add((n.Name, read));            // a native text is opaque: `reads:` is how it has ancestors
        return new DependencyGraph(ctx.Project.BuiltModels.Select(s => s.Definition.Name), ctx.Project.DeclaredModelsAndStaging.Select(d => d.Name).Concat(ctx.Project.Macros.Definitions.Where(d => !d.IsType).Select(d => ProjectContext.MacroNode(d.ShortName))), edges);
    }

    /// <summary>Which column of which table each output column of a model comes from, with the kind of transformation (direct, expression, aggregation, ...).</summary>
    public static IReadOnlyList<ColumnEdge> ColumnEdges(ProjectContext ctx, BuiltModel model)
    {
        var sql = model.ReadQuery(ctx.Root, ctx.Config);
        var facts = MetadataBuilder.AnalyzeAgainstUpstream(ctx, model.Definition.Name, ctx.AnalysisSql(model, sql)).Facts;
        var edges = new List<ColumnEdge>();
        foreach (var p in facts?.Projections ?? [])
        {
            if (p.Name == null) continue;
            var column = model.Definition.Columns.FirstOrDefault(c => string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase))?.Name ?? p.Name;
            if (p.Upstream.Count == 0) edges.Add(new ColumnEdge(null, null, model.Definition.Name, column, p.TransformKind));
            foreach (var u in p.Upstream) edges.Add(new ColumnEdge(u.Table, u.Column, model.Definition.Name, column, p.TransformKind));
        }
        return edges;
    }
}

/// <summary>A column of <c>To</c> built from <c>FromColumn</c> of <c>FromTable</c> (both null for a column made of constants).</summary>
internal sealed record ColumnEdge(string? FromTable, string? FromColumn, string ToModel, string ToColumn, string Transform);
