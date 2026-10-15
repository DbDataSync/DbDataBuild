using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Targets;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

/// <summary>
/// Compiles the resolvers of a load's `inputs:` (docs/research/refresh-plans.md, section 8): an `expression` is checked by DuckDB (one expression or one SELECT with no FROM, naming only inputs, parameters and run variables,
/// with no circle between inputs), and a `target` or `source` resolver is lowered like a model's query and rendered for the engine it runs on. What it finds is attached to the rendered operation, and the load plan
/// (`loads.yml`) and the deploy plan carry the text beside the name of the input, so there is no file of its own.
/// </summary>
internal static class InputCompiler
{
    /// <summary>The words an expression may use besides the load's inputs: the variables of a run.</summary>
    public static readonly IReadOnlyList<string> RunVariables = ["run_started", "plan", "connection"];

    /// <summary>The `${scope.name}` references of a resolver text with the value each has on <paramref name="connection"/>; the ones the project does not have, or a scope a resolver may not use, are in <paramref name="problems"/>.</summary>
    public static IReadOnlyList<RefreshParameterRef> ParametersOf(BuiltModel built, ProjectConfig config, string connection, string text, List<string> problems)
    {
        var values = built.ParametersFor(config, connection);
        var found = new List<RefreshParameterRef>();
        foreach (var r in InputParameters.Refs(text))
        {
            if (!InputParameters.Scopes.Contains(r[..r.IndexOf('.')])) { problems.Add($"`${{{r}}}` is not a scope a resolver can use ({string.Join(", ", InputParameters.Scopes)})"); continue; }
            if (!values.TryGetValue(r, out var v)) { problems.Add($"`${{{r}}}` has no value on {connection}"); continue; }
            found.Add(new(r, v.Type, v.Value));
        }
        return found;
    }

    /// <summary>Checks the inputs of every load of the result and adds the rendered resolvers (and their files) to it. A problem is a diagnostic at the input, never an exception.</summary>
    public static RenderResult Attach(ProjectContext ctx, BuiltModel built, RenderResult result)
    {
        if (result.Loads.Count == 0) return result;
        var def = built.Definition;
        var diags = new List<Diagnostic>(result.Diagnostics);
        var operations = new List<RenderedOperation>();
        foreach (var op in result.Loads)
        {
            var declared = LoadPlan.For(def, op.Target).FirstOrDefault(o => o.Name == op.Operation);
            if (declared == null || declared.InputList.Count == 0) { operations.Add(op); continue; }
            var rendered = new List<RenderedInput>();
            void Bad(LoadInput input, string why, DiagnosticDescriptor? descriptor = null) =>
                diags.Add(new Diagnostic(descriptor ?? DiagnosticCatalog.InputResolverInvalid, new(built.DefinitionFile, input.Line, 1), $"{def.Name}.{op.Operation}: the resolver of the input `{input.Name}` {why}."));

            // the names an expression may use: every input of the load, what its script binds, and the run variables
            var known = declared.InputList.SelectMany(i => i.Names.Append(i.Name)).Concat(op.Parameters.Select(p => p.Name)).Concat(RunVariables).ToHashSet(StringComparer.Ordinal);
            var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var producer = declared.InputList.Where(i => i.Provides.Count > 0).SelectMany(g => g.Provides.Select(p => (Name: p, Group: g.Name))).ToDictionary(x => x.Name, x => x.Group, StringComparer.Ordinal);
            foreach (var input in declared.InputList.Where(i => i.Resolver != null))
            {
                var resolver = input.Resolver!;
                var problems = new List<string>();
                var parameters = ParametersOf(built, ctx.Config, op.Target, resolver.Text, problems);
                if (problems.Count > 0) { Bad(input, "names " + string.Join("; ", problems), DiagnosticCatalog.InputResolverInvalid); continue; }
                var text = InputParameters.Substitute(resolver.Text, k => parameters.Where(p => p.Name == k).Select(p => new ParameterValue(p.Value, p.Type)).FirstOrDefault());
                switch (resolver.Kind)
                {
                    case InputResolverKinds.Expression:
                    {
                        var check = InputExpressions.Check(text);
                        if (check.Error != null) { Bad(input, $"is not an expression DuckDB can use: {check.Error}"); break; }
                        var unknown = check.Names.Where(n => !known.Contains(n)).ToList();
                        if (unknown.Count > 0) { Bad(input, $"names {string.Join(", ", unknown.Select(n => $"`{n}`"))}, which {(unknown.Count == 1 ? "is" : "are")} not an input of the load or a run variable ({string.Join(", ", RunVariables)})"); break; }
                        edges[input.Name] = check.Names.Where(n => n != input.Name).Select(n => producer.GetValueOrDefault(n, n)).Distinct(StringComparer.Ordinal).ToList();
                        if (check.Names.Contains(input.Name)) { Bad(input, "names the input it computes", DiagnosticCatalog.InputsDependOnEachOther); break; }
                        rendered.Add(new(input.Name, InputResolverKinds.Expression, resolver.Text));
                        break;
                    }
                    case InputResolverKinds.Target:
                    {
                        var (lowered, error) = ctx.Lowering.LowerResolver(def, built.QueryFile, $"{def.Name}.{op.Operation}.{input.Name}", text);
                        if (lowered == null) { Bad(input, $"cannot be lowered: {error?.Found}"); break; }
                        var (rendering, rdiags) = ctx.Renderer.RenderInputQuery(def, op.Operation, input.Name, lowered, op.Target, op.Target, $"rendered/{op.Target}/{LoadsDocument.FileName}");
                        diags.AddRange(rdiags);
                        if (rendering == null) break;
                        rendered.Add(new(input.Name, InputResolverKinds.Target, rendering));
                        break;
                    }
                    case InputResolverKinds.Source:
                    {
                        var origins = ctx.OriginsOf(def, op.Target);
                        if (origins.Count == 0) { Bad(input, "has no origin to run at: only a copy from another connection has a `source` resolver"); break; }
                        var (lowered, error) = ctx.Lowering.LowerResolver(def, built.QueryFile, $"{def.Name}.{op.Operation}.{input.Name}", text);
                        if (lowered == null) { Bad(input, $"cannot be lowered: {error?.Found}"); break; }
                        foreach (var byEngine in origins.GroupBy(o => o.Engine).OrderBy(g => g.Key, StringComparer.Ordinal))
                        {
                            var (rendering, rdiags) = ctx.Renderer.RenderInputQuery(def, op.Operation, input.Name, lowered, op.Target, byEngine.First().Connection, $"rendered/{op.Target}/{LoadsDocument.FileName}");
                            diags.AddRange(rdiags);
                            if (rendering == null) continue;
                            rendered.Add(new(input.Name, InputResolverKinds.Source, rendering, byEngine.Key));
                        }
                        break;
                    }
                }
            }
            if (Cycle(edges) is { } circle)
            {
                var at = declared.InputList.First(i => i.Name == circle[0]);
                diags.Add(new Diagnostic(DiagnosticCatalog.InputsDependOnEachOther, new(built.DefinitionFile, at.Line, 1), $"{def.Name}.{op.Operation}: the inputs {string.Join(" -> ", circle.Append(circle[0]).Select(n => $"`{n}`"))} depend on each other."));
            }
            operations.Add(op with { Inputs = rendered });
        }
        return result with { Diagnostics = diags, RenderedOperations = operations };
    }

    /// <summary>The names of a circle in the dependencies (each input to the inputs its expression names), or null when there is none.</summary>
    private static List<string>? Cycle(Dictionary<string, List<string>> edges)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);       // 1 visiting, 2 done
        var stack = new List<string>();
        List<string>? Visit(string n)
        {
            if (state.GetValueOrDefault(n) == 2) return null;
            if (state.GetValueOrDefault(n) == 1) return stack.Skip(stack.IndexOf(n)).ToList();
            state[n] = 1; stack.Add(n);
            foreach (var next in edges.GetValueOrDefault(n) ?? []) if (Visit(next) is { } c) return c;
            stack.RemoveAt(stack.Count - 1); state[n] = 2;
            return null;
        }
        foreach (var n in edges.Keys.Order(StringComparer.Ordinal)) if (Visit(n) is { } c) return c;
        return null;
    }
}
