using DbDataBuild.Apply;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

/// <summary>
/// The inputs of the loads a deploy plans (docs/research/refresh-plans.md, section 8): resolved when the plan is made, in dependency order, with one instant for the plan: an expression is computed, a `target` resolver runs
/// on the read login of the connection, a `source` resolver at the origin of a copy (on that origin's read login, for each origin). A value pinned with `--input` replaces the resolver, which is not run. What it finds goes
/// into the plan, so it can be read before it is applied, and so apply can compute it again and say what moved.
/// </summary>
internal static class InputPlanning
{
    public static async Task<(Dictionary<string, PlannedLoadInputs> Result, List<string> Problems)> ResolveAsync(ProjectContext ctx, string target, IReadOnlyList<PlannedModel> planned, IReadOnlyList<RenderedOperation> chosen,
        ReadSession read, Func<string, string?> env, IReadOnlyList<InputPin> pins, InputRunContext run, CancellationToken ct)
    {
        var result = new Dictionary<string, PlannedLoadInputs>(StringComparer.Ordinal);
        var problems = new List<string>();
        var matched = new HashSet<InputPin>();
        var declaredLoads = chosen.Where(op => LoadPlan.For(planned.First(m => m.Definition.Name == op.Model).Definition, target).FirstOrDefault(o => o.Name == op.Operation) is { InputList.Count: > 0 }).ToList();

        // a pin names an input of a load that is being planned; it must name exactly one
        foreach (var pin in pins)
        {
            var fits = chosen.Where(op => pin.Matches(op.Model, op.Operation) && PinnableNames(op, planned.First(m => m.Definition.Name == op.Model).Definition, target).Contains(pin.Name)).ToList();
            if (fits.Count == 0) problems.Add($"--input `{pin.Text}`: none of the loads to plan has an input called `{pin.Name}`{(pin.Target != null ? $" in `{pin.Target}`" : "")}.");
            else if (fits.Count > 1) problems.Add($"--input `{pin.Text}` fits more than one load ({string.Join(", ", fits.Select(f => $"{f.Model}.{f.Operation}"))}): name the load, for example `--input {fits[0].Model}.{fits[0].Operation}.{pin.Name}={pin.Value}`.");
        }
        if (problems.Count > 0) return (result, problems);

        foreach (var op in declaredLoads)
        {
            var model = planned.First(m => m.Definition.Name == op.Model);
            var def = model.Definition;
            var built = ctx.Project.BuiltModels.First(s => s.Definition.Name == def.Name);
            var declared = LoadPlan.For(def, target).First(o => o.Name == op.Operation);
            var declaredNames = declared.InputList.SelectMany(i => i.Names.Append(i.Name)).ToHashSet(StringComparer.Ordinal);
            var given = pins.Where(p => p.Matches(op.Model, op.Operation) && PinnableNames(op, def, target).Contains(p.Name))
                .ToDictionary(p => p.Name, p => (p.Value, "flag"), StringComparer.Ordinal);
            var origins = def.IsCopy && !def.LocalCopy ? model.OriginList.Select(o => (Connection: (string?)o.Connection, o.Engine)).ToList() : [((string?)null, (string?)null)];
            foreach (var (origin, engine) in origins)
            {
                var diags = new List<Diagnostic>();
                var (recipes, skip) = RefreshPlanBuilder.InputsOf(ctx, built, target, def, op, model.ParameterValues, diags, engine);
                if (recipes == null || diags.Any(d => d.Severity == Severity.Error))
                {
                    result[PlanInput.InputsKey(def.Name, op.Operation, origin)] = new([], $"the inputs of {def.Name}.{op.Operation} could not be compiled: {skip ?? string.Join(" ", diags.Select(d => d.Found))}", false);
                    continue;
                }
                // the inputs the model declares, with a value given where a person pinned one; one nobody gave a value is a question, as it always was
                var defs = recipes.Where(r => declaredNames.Contains(r.Name) && (r.Source != RefreshParameter.Required || given.ContainsKey(r.Name))).ToList();
                if (defs.Count == 0) continue;
                await using var originRunner = origin == null ? null : new OriginInputRunner((name, token) => OpenOrigin(ctx, name, env, token), origin);
                var set = await ResolveOneAsync($"{def.Name}.{op.Operation}", defs, given, run, origin == null ? new SessionInputRunner(read) : new SourceOrTargetRunner(new SessionInputRunner(read), originRunner!), origin, ct);
                result[PlanInput.InputsKey(def.Name, op.Operation, origin)] = set;
            }
        }
        return (result, problems);
    }

    /// <summary>The inputs of a load a person can give a value for: what its script binds, and the inputs it declares (not a resolver that provides several).</summary>
    private static IReadOnlyList<string> PinnableNames(RenderedOperation op, ModelDefinition def, string target) =>
        op.Parameters.Select(p => p.Name).Concat(LoadPlan.For(def, target).First(o => o.Name == op.Operation).InputList.Where(i => i.Provides.Count == 0).Select(i => i.Name)).Distinct().ToList();

    private static async Task<PlannedLoadInputs> ResolveOneAsync(string load, IReadOnlyList<RefreshParameter> defs, Dictionary<string, (string, string)> given, InputRunContext run, IInputQueryRunner runner, string? origin, CancellationToken ct)
    {
        var res = await InputResolution.ResolveAsync(load, defs, given.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, kv.Value.Item2), StringComparer.Ordinal), run, runner, r => r.Text, ct);
        string Strip(string m) => m.StartsWith(DiagnosticCatalog.InputNotResolved.Code + ": ", StringComparison.Ordinal) ? m[(DiagnosticCatalog.InputNotResolved.Code.Length + 2)..] : m;
        if (res.Failure != null) return new([], Strip(res.Failure), false);
        if (res.SkipLoad) return new([], null, true);
        var planned = new List<PlannedInput>();
        foreach (var d in defs)
        {
            var def = d;
            if (res.Inputs.FirstOrDefault(i => i.Name == d.Name) is { } found)
            {
                var owner = d.ProvidedBy != null ? defs.FirstOrDefault(g => g.Name == d.ProvidedBy) ?? d : d;
                planned.Add(new(def, found.Source, found.Value, found.ResolverHash, found.IsComputed && owner.Resolver?.Kind == InputResolverKinds.Source ? origin : null));
            }
            else if (d.Provides.Count > 0) planned.Add(new(def, "group", null, d.Resolver!.Hash));
        }
        return new(planned, null, false);
    }

    private static async Task<ReadSession> OpenOrigin(ProjectContext ctx, string connection, Func<string, string?> env, CancellationToken ct)
    {
        var config = ctx.Config.Connections[connection];
        var (login, missing) = LoginSettings.FromEnvironment(config, Login.Read, env);
        if (login == null) throw new GateRefusedException(missing!);
        return await ReadSession.OpenAsync(login, ct);
    }
}
