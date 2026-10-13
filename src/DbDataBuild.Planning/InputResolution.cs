using System.Diagnostics;
using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Planning;

/// <summary>What a run knows about itself that a resolver may use: one instant for the whole run (frozen when it starts), the zone the clock means, and the names `plan` and `connection`.</summary>
public sealed record InputRunContext(DateTimeOffset RunStarted, string Timezone, string? PlanName, string Connection)
{
    /// <summary>The instant as the event records it.</summary>
    public string RunStartedText => RunStarted.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>The rows a `target` or `source` resolver's SELECT returned, or why it did not run (the type of the failure only: a driver message can quote data).</summary>
public sealed record InputQueryResult(IReadOnlyList<string> Names, IReadOnlyList<IReadOnlyList<object?>> Rows, string? Error);

/// <summary>Runs the SELECT of a `target` resolver on the load's connection or of a `source` resolver at the origin, on the read login. The command layer supplies it (every read goes through a read session).</summary>
public interface IInputQueryRunner
{
    /// <param name="origin">For a `source` resolver: the connection it runs at.</param>
    Task<InputQueryResult> RunAsync(RefreshInputResolver resolver, string text, string? origin, CancellationToken ct);
}

/// <summary>One input with the value it will run with and where the value came from: `flag`, `membership`, `parameter`, `default`, `resolver:expression`, `resolver:target`, `resolver:source` or `initial`.</summary>
/// <param name="Planned">For an input of a deploy that was computed again at apply: the value the plan was made with.</param>
public sealed record ResolvedInput(string Name, string Type, string Source, string? Value, string? ResolverHash = null, long DurationMs = 0, string? Planned = null)
{
    public bool IsComputed => Source.StartsWith("resolver:", StringComparison.Ordinal);
}

/// <param name="Failure">The message (with its DDB code) when an input could not be resolved; the load does not run.</param>
/// <param name="SkipLoad">An input found NULL and says `on_null: skip_load`: the load does not run, which is not a failure.</param>
/// <param name="NotResolved">Inputs left alone because the resolver they need was not run (an offline resolution of a `target` or `source` resolver).</param>
public sealed record InputResolutionResult(IReadOnlyList<ResolvedInput> Inputs, string? Failure, bool SkipLoad, IReadOnlyList<string> NotResolved, bool UsedClock)
{
    public bool Ok => Failure == null && !SkipLoad;
}

/// <summary>
/// Resolves the inputs of one load (docs/research/refresh-plans.md, section 8): in dependency order, each input from what a person gave (`--input`, a membership), the project's parameter or the load's default, or
/// from its resolver. A value a person gave replaces the resolver, which is then not run. A resolver must return exactly one row (one column for one input, the named columns for `provides`); NULL goes to `on_null`; the
/// value is cast to the declared type with an error that names the input and the resolver, never a silent coercion.
/// </summary>
public static class InputResolution
{
    private static string Code => DiagnosticCatalog.InputNotResolved.Code;

    /// <param name="given">What a person or a membership gave, by input name, with its source (`flag` or `membership`).</param>
    /// <param name="runner">Runs `target` and `source` resolvers; null resolves offline: those resolvers are not run and the inputs that need them are named in <see cref="InputResolutionResult.NotResolved"/>.</param>
    /// <param name="textOf">The SELECT of a `target` or `source` resolver (read from its committed file, whose hash was checked); null when it cannot be read.</param>
    /// <param name="load">`model.load` for messages.</param>
    public static async Task<InputResolutionResult> ResolveAsync(string load, IReadOnlyList<RefreshParameter> defs, IReadOnlyDictionary<string, (string Value, string Source)> given, InputRunContext context,
        IInputQueryRunner? runner, Func<RefreshInputResolver, string?> textOf, CancellationToken ct)
    {
        var byName = defs.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var resolved = new Dictionary<string, ResolvedInput>(StringComparer.Ordinal);
        var notResolved = new List<string>();
        var groupRows = new Dictionary<string, (IReadOnlyList<string> Names, IReadOnlyList<object?> Row, string Hash, long Ms)?>(StringComparer.Ordinal);
        var usedClock = false;
        string? failure = null; var skip = false;
        string Fail(string message) => failure ??= $"{Code}: {message}";

        // dependency order: an expression's inputs before it, the group before what it provides
        var order = Order(defs, context, out var cycle);
        if (cycle != null) return new([], Fail($"the inputs of {load} depend on each other ({cycle}); the compile refuses this, so the compiled files are out of date."), false, [], false);

        foreach (var def in order)
        {
            if (failure != null || skip) break;
            if (def.Source == RefreshParameter.FromResolver) continue;                     // the watermark: the command resolves it
            if (def.Resolver is { Provides.Count: > 0 }) continue;                           // a group is run for the inputs it provides
            if (given.TryGetValue(def.Name, out var g))
            {
                if (!InputValues.Fits(def.Type, g.Value)) { Fail($"the value `{g.Value}` of the input `{def.Name}` of {load} is not a {def.Type}."); break; }
                resolved[def.Name] = new(def.Name, def.Type, g.Source, g.Value);
                continue;
            }
            switch (def.Source)
            {
                case RefreshParameter.FromParameter or RefreshParameter.FromDefault:
                    resolved[def.Name] = new(def.Name, def.Type, def.Source, def.Value);
                    continue;
                case RefreshParameter.Required:
                    Fail($"{load} needs a value for the input `{def.Name}` ({def.Type}), which has no default: give it with `--input {load}.{def.Name}=<value>`.");
                    continue;
            }

            // computed by its own resolver, or by the resolver of its group
            var owner = def.Source == RefreshParameter.Provided ? byName.GetValueOrDefault(def.ProvidedBy ?? "") : def;
            if (owner?.Resolver is not { } resolver) { Fail($"the input `{def.Name}` of {load} has no resolver to compute it."); continue; }
            var key = owner.Name;
            if (!groupRows.TryGetValue(key, out var row))
            {
                if (resolver.Kind != InputResolverKinds.Expression && runner == null)
                {
                    notResolved.Add(def.Name);
                    continue;
                }
                var watch = Stopwatch.StartNew();
                (IReadOnlyList<string> Names, IReadOnlyList<object?> Row)? got;
                if (resolver.Kind == InputResolverKinds.Expression)
                {
                    usedClock = true;
                    got = Expression(load, owner, resolver, resolved, context, Fail);
                }
                else got = await Query(load, owner, resolver, runner!, textOf, context, Fail, ct);
                groupRows[key] = row = got == null ? null : (got.Value.Names, got.Value.Row, resolver.Hash, watch.ElapsedMilliseconds);
            }
            if (row == null)
            {
                if (failure == null && notResolved.Contains(def.Name) == false && resolver.Kind != InputResolverKinds.Expression && runner == null) notResolved.Add(def.Name);
                continue;
            }
            // which column answers this input: the only one for a resolver of one input, the one named for it in a group
            object? value;
            if (owner.Resolver.Provides.Count == 0)
            {
                if (row.Value.Names.Count != 1) { Fail($"the resolver ({resolver.Kind}) of the input `{def.Name}` of {load} returned {row.Value.Names.Count} columns, not exactly one."); continue; }
                value = row.Value.Row[0];
            }
            else
            {
                var at = row.Value.Names.ToList().FindIndex(n => string.Equals(n, def.Name, StringComparison.OrdinalIgnoreCase));
                if (at < 0) { Fail($"the resolver ({resolver.Kind}) `{owner.Name}` of {load} provides `{def.Name}` but returned the columns {string.Join(", ", row.Value.Names.Select(n => $"`{n}`"))}: name a column `{def.Name}`."); continue; }
                value = row.Value.Row[at];
            }
            var kind = $"resolver:{resolver.Kind}";
            if (value == null)
            {
                switch (def.OnNull)
                {
                    case InputOnNull.Initial when def.Initial != null:
                        resolved[def.Name] = new(def.Name, def.Type, "initial", def.Initial, resolver.Hash, row.Value.Ms);
                        continue;
                    case InputOnNull.SkipLoad:
                        skip = true;
                        continue;
                    default:
                        Fail($"the resolver ({resolver.Kind}) of the input `{def.Name}` of {load} found NULL and `on_null` is {def.OnNull}: give the value with `--input {load}.{def.Name}=<value>`.");
                        continue;
                }
            }
            var (text, error) = InputExpressions.ToText(def.Type, new ExpressionColumn(def.Name, resolver.Kind == InputResolverKinds.Expression ? DuckTypeOf(value) : "DB", value), context.Timezone);
            if (error != null) { Fail($"the resolver ({resolver.Kind}) of the input `{def.Name}` of {load} gave a value that does not fit: {error}."); continue; }
            if (text == null || !InputValues.Fits(def.Type, text)) { Fail($"the resolver ({resolver.Kind}) of the input `{def.Name}` of {load} gave `{text}`, which is not a {def.Type}."); continue; }
            resolved[def.Name] = new(def.Name, def.Type, kind, text, resolver.Hash, row.Value.Ms);
        }

        // in the order the load declares them, groups left out (they are resolvers, not values)
        var inputs = defs.Where(d => resolved.ContainsKey(d.Name)).Select(d => resolved[d.Name]).ToList();
        return new(inputs, failure, skip && failure == null, notResolved, usedClock);
    }

    private static string DuckTypeOf(object value) => value switch { DateTime => "Timestamp", _ => "DB" };

    private static (IReadOnlyList<string> Names, IReadOnlyList<object?> Row)? Expression(string load, RefreshParameter owner, RefreshInputResolver resolver, Dictionary<string, ResolvedInput> resolved, InputRunContext context, Func<string, string> fail)
    {
        var text = InputParameters.Substitute(resolver.Text ?? "", k => resolver.Parameters.Where(p => p.Name == k).Select(p => new ParameterValue(p.Value, p.Type)).FirstOrDefault());
        var variables = resolved.Values.Select(r => new ExpressionVariable(r.Name, r.Type, r.Value)).ToList();
        variables.Add(new("plan", "VARCHAR", context.PlanName));
        variables.Add(new("connection", "VARCHAR", context.Connection));
        var result = InputExpressions.Evaluate(text, variables, context.RunStarted, context.Timezone);
        if (result.Error != null) { fail($"the expression of the input `{owner.Name}` of {load} could not be evaluated: {result.Error}."); return null; }
        return (result.Columns.Select(c => c.Name).ToList(), result.Columns.Select(c => c.Value).ToList());
    }

    private static async Task<(IReadOnlyList<string> Names, IReadOnlyList<object?> Row)?> Query(string load, RefreshParameter owner, RefreshInputResolver resolver, IInputQueryRunner runner, Func<RefreshInputResolver, string?> textOf, InputRunContext context, Func<string, string> fail, CancellationToken ct)
    {
        var text = textOf(resolver);
        if (text == null) { fail($"the {resolver.Kind} resolver of the input `{owner.Name}` of {load} has no text (its committed file could not be read)."); return null; }
        var rows = await runner.RunAsync(resolver, text, null, ct);
        if (rows.Error != null) { fail($"the {resolver.Kind} resolver of the input `{owner.Name}` of {load} {rows.Error}. A table this plan creates cannot be read yet: give the value with `--input {load}.{owner.Name}=<value>`."); return null; }
        if (rows.Rows.Count != 1) { fail($"the {resolver.Kind} resolver of the input `{owner.Name}` of {load} returned {rows.Rows.Count} rows, not exactly one."); return null; }
        return (rows.Names, rows.Rows[0]);
    }

    /// <summary>The inputs in the order they can be resolved: what an expression names comes before it, and a group before what it provides. A circle is reported by its names.</summary>
    internal static List<RefreshParameter> Order(IReadOnlyList<RefreshParameter> defs, InputRunContext context, out string? cycle)
    {
        var byName = defs.ToDictionary(d => d.Name, StringComparer.Ordinal);
        List<string> DependenciesOf(RefreshParameter d)
        {
            var owner = d.Source == RefreshParameter.Provided && d.ProvidedBy != null ? byName.GetValueOrDefault(d.ProvidedBy) ?? d : d;
            if (owner.Resolver is not { Kind: InputResolverKinds.Expression } r) return [];
            var text = InputParameters.Substitute(r.Text ?? "", k => r.Parameters.Where(p => p.Name == k).Select(p => new ParameterValue(p.Value, p.Type)).FirstOrDefault());
            return InputExpressions.Check(text).Names.Where(n => n != d.Name && byName.ContainsKey(n)).ToList();
        }
        var result = new List<RefreshParameter>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        string? found = null;
        bool Visit(RefreshParameter d)
        {
            if (state.GetValueOrDefault(d.Name) == 2) return true;
            if (state.GetValueOrDefault(d.Name) == 1) { found = string.Join(" -> ", stack.Skip(stack.IndexOf(d.Name)).Append(d.Name)); return false; }
            state[d.Name] = 1; stack.Add(d.Name);
            foreach (var dep in DependenciesOf(d))
                if (!Visit(byName[dep])) return false;
            stack.RemoveAt(stack.Count - 1); state[d.Name] = 2;
            result.Add(d);
            return true;
        }
        foreach (var d in defs) if (!Visit(d)) break;
        cycle = found;
        return result;
    }
}
