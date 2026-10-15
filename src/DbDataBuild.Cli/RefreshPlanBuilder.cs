using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

/// <summary>What `project compile` writes for the refreshes of one connection: every load plan, and one file per refresh plan.</summary>
internal sealed record CompiledRefresh(LoadsFile Loads, IReadOnlyList<RefreshPlanFile> Plans)
{
    /// <summary>The files, relative to `rendered/`.</summary>
    public IEnumerable<RenderedFile> Files()
    {
        yield return new($"{Loads.Connection}/{LoadsDocument.FileName}", LoadsDocument.Serialize(Loads));
        foreach (var p in Plans) yield return new(RefreshPlanDocument.PathOf(p.Connection, p.Name), RefreshPlanDocument.Serialize(p));
    }
}

/// <summary>
/// Compiles the refreshes of a connection from the project alone (no connection is opened): every load of every model as a load plan (`loads.yml`: the committed script and its hash, the inputs with where each default
/// comes from, the hooks around it, what the structure should be), and each refresh plan as the members that are in it, in dependency order, naming their load plans by hash (`refresh/&lt;name&gt;.yml`). A model with
/// a default load and no `refresh:` anywhere is in the plan `default`. A load that cannot run unattended is a load plan, and may be in a plan only if its membership gives the inputs that have no default.
/// </summary>
internal static class RefreshPlanBuilder
{
    /// <summary>Every model of the project that declares <paramref name="connection"/>, with its query as that connection reads it: what the project hash and the refresh plan are made from.</summary>
    public static List<LoadedModel> ModelsOf(ProjectContext ctx, string connection) =>
        ctx.Project.BuiltModels.Where(s => ctx.TargetsOf(s.Definition).Contains(connection)).Select(s => new LoadedModel(s, s.ReadQuery(ctx.Root, ctx.Config, connection))).ToList();

    /// <summary>The structure the compiled project expects of one connection, as one hash: each table model by the shape its declaration gives it, each view by its definition. A deploy records it.</summary>
    public static string ProjectHash(ProjectContext ctx, string connection, IReadOnlyList<LoadedModel> models)
    {
        var ddl = TargetRegistry.Get(ctx.Config.EngineOf(connection) ?? connection).CreateDdl(ctx.Config.ForConnection(connection));
        var lines = new List<string>();
        foreach (var m in models.OrderBy(m => m.Model.Definition.Name, StringComparer.Ordinal))
        {
            var def = m.Model.Definition;
            lines.Add(def.Kind == ModelKinds.View ? $"{def.Name}=view:{ctx.DefinitionHashOf(m.Sql)}" : $"{def.Name}={ExpectedShape(ddl, def)}");
        }
        return Hashing.Sha256Hex(string.Join("\n", lines));
    }

    private static string ExpectedShape(DdlGenerator ddl, ModelDefinition def)
    {
        try { return Hashing.ShapeHash(DdlGenerator.ExpectedShape(ddl.MapAll(def))); }
        catch (DdlUnsupportedException ex) { return "unsupported:" + ex.Diagnostic.Code; }
    }

    /// <summary>Whether a value written for an input is one of the input's type. A type this does not know a form for takes any text.</summary>
    internal static bool InputFits(string type, string value)
    {
        var t = type.Trim().ToUpperInvariant();
        if (t == "DATE" || t.StartsWith("TIMESTAMP", StringComparison.Ordinal) || t is "BIGINT" or "INTEGER" or "INT" or "SMALLINT") return ColumnTypes.LiteralFits(t == "INT" ? "INTEGER" : type, value);
        if (t.StartsWith("DECIMAL", StringComparison.Ordinal) || t.StartsWith("NUMERIC", StringComparison.Ordinal) || t is "DOUBLE" or "REAL" or "FLOAT") return decimal.TryParse(value.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
        if (t is "BOOLEAN" or "BOOL") return value is "true" or "false";
        return true;
    }

    /// <summary>
    /// The inputs of one rendered load: the ones its script binds (the query's parameters, the watermark, `start` and `end` of a range load), with the declared `inputs:` laid over them. Null (with the reason) when the load
    /// cannot run unattended because a parameter of its query has no value on the connection.
    /// </summary>
    /// <param name="originEngine">For a copy: the engine of the origin a `source` resolver runs at, which picks the file it was lowered into.</param>
    internal static (List<RefreshParameter>? Inputs, string? Skip) InputsOf(ProjectContext ctx, BuiltModel source, string connection, ModelDefinition def, RenderedOperation op, IReadOnlyDictionary<string, ParameterValue>? parameterValues, List<Diagnostic> diags, string? originEngine = null)
    {
        var declaredOp = LoadPlan.For(def, connection).FirstOrDefault(o => o.Name == op.Operation);
        var inputs = new List<RefreshParameter>();
        foreach (var p in op.Parameters)
        {
            if (p.Source == "parameter")
            {
                var key = QueryParameter.KeyOf(p.Name);
                if (key == null || parameterValues?.GetValueOrDefault(key) is not { } pv) return (null, $"the parameter `{key ?? p.Name}` has no value on {connection}");
                inputs.Add(new(p.Name, pv.Type, RefreshParameter.FromParameter, pv.Value));
            }
            else if (p.Source == "resolver") inputs.Add(new(p.Name, p.Type, RefreshParameter.FromResolver, null));
            else inputs.Add(new(p.Name, p.Type, RefreshParameter.Required, null));
        }
        if (declaredOp is { InputList.Count: > 0 } && !DeclareInputs(ctx, source, connection, def, declaredOp, op, parameterValues, inputs, diags, originEngine)) return (null, "its `inputs:` could not be compiled");
        return (inputs, null);
    }

    /// <summary>
    /// Lays the load's declared `inputs:` over the inputs its script binds: a script parameter (`start`, `end`) gets a default or a resolver; a name no script binds is a free input; an entry that provides several makes
    /// them `provided` and is itself the resolver. The text of a `target` resolver is the file the renderer wrote (named with its hash); an `expression` is the text as written, with the parameters it names and their values.
    /// Returns false (with diagnostics) when it cannot be compiled.
    /// </summary>
    private static bool DeclareInputs(ProjectContext ctx, BuiltModel source, string connection, ModelDefinition def, LoadOperation declared, RenderedOperation op, IReadOnlyDictionary<string, ParameterValue>? modelValues, List<RefreshParameter> inputs, List<Diagnostic> diags, string? originEngine)
    {
        var ok = true;
        void Bad(LoadInput i, string found, DiagnosticDescriptor? d = null) { diags.Add(new Diagnostic(d ?? DiagnosticCatalog.InputResolverInvalid, new(source.DefinitionFile, i.Line, 1), $"{def.Name}.{declared.Name}: the input `{i.Name}` {found}.")); ok = false; }
        var providedBy = declared.InputList.Where(g => g.Provides.Count > 0).SelectMany(g => g.Provides.Select(p => (Name: p, Group: g.Name))).ToDictionary(x => x.Name, x => x.Group, StringComparer.Ordinal);
        var values = source.ParametersFor(ctx.Config, connection);

        RefreshInputResolver? ResolverOf(LoadInput i)
        {
            if (i.Resolver == null) return null;
            var problems = new List<string>();
            var refs = InputCompiler.ParametersOf(source, ctx.Config, connection, i.Resolver.Text, problems);
            if (i.Resolver.Kind == InputResolverKinds.Expression) return new(i.Resolver.Kind, i.Resolver.Text, i.Provides, refs);
            // a `target` or `source` resolver is the text lowered for the engine it runs on; the load plan carries it beside the input's name
            var rendered = op.InputList.FirstOrDefault(r => r.Name == i.Name && r.Kind == i.Resolver.Kind && (r.Engine == null || originEngine == null || r.Engine == originEngine));
            if (rendered == null) { Bad(i, "has no rendered resolver (see the errors of the compile)"); return null; }
            return new(i.Resolver.Kind, rendered.Text, i.Provides, refs, rendered.Engine);
        }

        // the value a default gives: a literal, or the parameter it names
        (string? Value, string Source) DefaultOf(LoadInput i)
        {
            if (i.Default == null) return (null, RefreshParameter.Required);
            var refs = InputParameters.Refs(i.Default);
            if (refs.Count == 0) return (i.Default, RefreshParameter.FromDefault);
            if (refs.Count > 1 || i.Default.Trim() != $"${{{refs[0]}}}" || !values.TryGetValue(refs[0], out var pv) || !InputParameters.Scopes.Contains(refs[0][..refs[0].IndexOf('.')])) { Bad(i, $"has the default `{i.Default}`, which is not one `${{project.x}}` or `${{connection.x}}` parameter that has a value on {connection}"); return (null, RefreshParameter.Required); }
            return (pv.Value, RefreshParameter.FromParameter);
        }

        foreach (var li in declared.InputList)
        {
            var existing = inputs.FirstOrDefault(p => p.Name == li.Name);
            if (existing != null && existing.Source is RefreshParameter.FromParameter or RefreshParameter.FromResolver) { Bad(li, "has the name of a parameter the load's query or watermark already binds"); continue; }
            if (li.Provides.Count > 0)
            {
                inputs.Add(new(li.Name, "", RefreshParameter.Computed, null, ResolverOf(li), Free: true));
                continue;
            }
            var type = li.Type ?? existing?.Type ?? "VARCHAR";
            var entry = new RefreshParameter(li.Name, type, RefreshParameter.Required, null, null, Free: existing == null, OnNull: li.OnNull);
            if (providedBy.TryGetValue(li.Name, out var group)) entry = entry with { Source = RefreshParameter.Provided, ProvidedBy = group };
            else if (li.Resolver != null) entry = entry with { Source = RefreshParameter.Computed, Resolver = ResolverOf(li) };
            var (value, valueSource) = DefaultOf(li);
            if (li.Resolver != null || entry.Source == RefreshParameter.Provided) entry = entry with { Initial = li.OnNull == InputOnNull.Initial ? value : null };
            else if (li.Default != null) entry = entry with { Source = valueSource, Value = value };
            if (value != null && !InputValues.Fits(type, value)) Bad(li, $"has the value `{value}`, which is not a {type}");
            if (existing != null) inputs[inputs.IndexOf(existing)] = entry; else inputs.Add(entry);
        }
        // a name a group provides that the load's script binds (`start`, `end`) is provided too, though it is declared nowhere else
        foreach (var (name, group) in providedBy)
            if (inputs.FirstOrDefault(p => p.Name == name) is { } p && p.Source == RefreshParameter.Required)
                inputs[inputs.IndexOf(p)] = p with { Source = RefreshParameter.Provided, ProvidedBy = group };
        return ok;
    }

    /// <summary>The staging table of a copy, its columns, and for each origin the read in that origin's dialect (with and without the watermark bound): what a refresh needs to run the transfers without the project.</summary>
    private static RefreshCopy CopyOf(ProjectContext ctx, string connection, DdlGenerator ddl, ModelDefinition def, IReadOnlyList<CopyOrigin> origins)
    {
        var (stagingSchema, stagingTable) = (CopyModels.StagingSchema(ctx.Config), CopyModels.StagingTable(def.Name));
        var list = new List<RefreshCopyOrigin>();
        string create = "";
        foreach (var origin in origins)
        {
            var (createText, spec) = Planner.CopyTransfer(ctx.Config, def, ddl, origin);
            create = createText;
            string? incremental = null;
            IReadOnlyList<string>? order = null;
            if (def.Watermark != null)
            {
                var bounded = Planner.CopyTransfer(ctx.Config, def, ddl, origin with { WatermarkValue = "0" }).Spec;
                (incremental, order) = (bounded.ReadText, bounded.ParameterOrder);
            }
            list.Add(new(origin.Connection, origin.Engine, origin.Table, spec.ReadText, incremental, spec.Slice?.Column, spec.Slice?.Value, spec.Slice?.Added ?? false, [], order, spec.Dialect, spec.DialectHash));
        }
        var watermark = def.Watermark == null ? null : new RefreshCopyWatermark(def.Watermark.Column, def.Columns.First(x => string.Equals(x.Name, def.Watermark.Column, StringComparison.OrdinalIgnoreCase)).Type, def.Watermark.Lookback);
        return new RefreshCopy($"{stagingSchema}.{stagingTable}", create, ddl.DropTableIfExists(stagingSchema, stagingTable), def.Columns.Select(x => new PlanColumn(x.Name, x.Type)).ToList(), watermark, list);
    }

    /// <param name="dependencies">The dependency rows of the whole project (<see cref="MetadataBuilder.Dependencies"/>, computed once for all connections): the file keeps the ones between the models it loads.</param>
    /// <param name="models">Every model of the project that declares <paramref name="connection"/>, with its query as that connection reads it, and its rendering for the connection.</param>
    public static CompiledRefresh Build(ProjectContext ctx, string connection, IReadOnlyList<(LoadedModel Model, RenderResult Render)> models, IReadOnlyList<DependencyRow> dependencies, List<Diagnostic> diags)
    {
        var root = ctx.Root;
        var ddl = TargetRegistry.Get(ctx.Config.EngineOf(connection) ?? connection).CreateDdl(ctx.Config.ForConnection(connection));
        var planned = models.Select(x =>
        {
            var m = x.Model;
            var bases = m.Model.Definition is { IsCopy: true, LocalCopy: false } ? [] : ctx.WithNativeReads(ctx.BaseTablesOf(m.Model, m.Sql));
            return new PlannedModel(m.Model.Definition, "", m.Model.QueryFile, ctx.DefinitionHashOf(m.Sql), bases, HookLoader.Load(m.Model, ctx.Config, connection, root, diags), [],
                PlanningSession.Merge(m.Model.ParametersFor(ctx.Config, connection), ctx.Lowering.NativeValuesFor(m.Sql, connection)), null);
        }).ToList();
        var renders = models.ToDictionary(x => x.Model.Model.Definition.Name, x => x.Render, StringComparer.Ordinal);
        var sources = models.ToDictionary(x => x.Model.Model.Definition.Name, x => x.Model.Model, StringComparer.Ordinal);

        var loads = new List<LoadPlanEntry>();
        var excluded = new List<RefreshExclusion>();
        var excludedLoads = new Dictionary<(string Model, string Load), string>();           // a load that has no load plan, and why: a plan that names it says so
        var modelOrder = new List<PlannedModel>();
        foreach (var model in ModelOrder.Sort(planned, out _))
        {
            var def = model.Definition;
            var ops = renders[def.Name].Loads.Where(l => l.Target == connection).OrderBy(o => o.Operation, StringComparer.Ordinal).ToList();
            if (ops.Count == 0) continue;                                           // a view, a mapped model, a kind without loads
            modelOrder.Add(model);
            void Skip(RenderedOperation? op, string reason)
            {
                if (op == null) { excluded.Add(new(def.Name, reason)); foreach (var o in ops) excludedLoads[(def.Name, o.Operation)] = reason; }
                else { excluded.Add(new(def.Name, $"the load `{op.Operation}`: {reason}")); excludedLoads[(def.Name, op.Operation)] = reason; }
            }
            RefreshCopy? copy = null;
            if (def.IsCopy && !def.LocalCopy)
            {
                var origins = ctx.OriginsOf(def, connection);
                if (origins.Count == 0) { Skip(null, "the copy has no origin on another connection"); continue; }
                if (origins.Any(o => o.Native != null)) { Skip(null, "its origin is a native model (a deploy plans a copy of one); a refresh copies from tables and views"); continue; }
                copy = CopyOf(ctx, connection, ddl, def, origins);
            }
            var shape = ExpectedShape(ddl, def);
            if (shape.StartsWith("unsupported:", StringComparison.Ordinal)) { Skip(null, $"its declared columns have no type on {connection} ({shape[12..]})"); continue; }
            var hooks = model.HookList.Where(h => h.Hook.Event is "pre_load" or "post_load").ToList();
            var bad = hooks.FirstOrDefault(h => h.Hook.Effect != "data" || !string.Equals(h.Hook.Risk, "safe", StringComparison.OrdinalIgnoreCase));
            if (bad != null) { Skip(null, $"the hook `{bad.Hook.Name}` around its load is not a safe data hook, so the load is not routine"); continue; }
            RefreshHook Hook(PlannedHook h) => new(h.Hook.Name, h.Hook.Event, h.Hook.ScriptPath.Replace('\\', '/'), h.FileHash, h.Hook.Effect, h.Hook.Risk);

            foreach (var op in ops)
            {
                var declaredOp = LoadPlan.For(def, connection).FirstOrDefault(o => o.Name == op.Operation);
                var (inputs, skipReason) = InputsOf(ctx, sources[def.Name], connection, def, op, model.ParameterValues, diags, copy?.Origins[0].Engine);
                if (inputs == null) { Skip(op, skipReason!); continue; }
                // a load that replaces everything of an origin reads it in full: no watermark bound, so the run never works one out
                var opCopy = declaredOp?.ReadsAll == true && copy?.Watermark != null ? copy with { Watermark = null, Origins = copy.Origins.Select(o => o with { ReadWithWatermark = null }).ToList() } : copy;
                if (opCopy != null && inputs.Any(i => i.Resolver?.Kind == InputResolverKinds.Source))
                {
                    // a `source` resolver is lowered for the engine of each origin: each origin carries the inputs as its own engine runs them
                    var first = true;
                    var perOrigin = new List<RefreshCopyOrigin>();
                    foreach (var o in opCopy.Origins)
                    {
                        var (its, why) = InputsOf(ctx, sources[def.Name], connection, def, op, model.ParameterValues, first ? diags : [], o.Engine);
                        first = false;
                        if (its == null) { Skip(op, why!); perOrigin.Clear(); break; }
                        perOrigin.Add(o with { Inputs = its });
                    }
                    if (perOrigin.Count == 0) continue;
                    opCopy = opCopy with { Origins = perOrigin };
                }
                loads.Add(new(def.Name, op.Operation, op.Strategy, op.IsDefault, op.ScriptPath, Hashing.ScriptHash(op.Script), op.ResolverPath, op.Resolver == null ? null : Hashing.ScriptHash(op.Resolver), model.DefinitionHash,
                    shape, inputs, op.Watermark?.OnNull, op.Watermark?.Initial, hooks.Where(h => h.Hook.Event == "pre_load").Select(Hook).ToList(), hooks.Where(h => h.Hook.Event == "post_load").Select(Hook).ToList(), declaredOp?.MaxRuntime?.Text, opCopy));
            }
        }
        // the dependency graph among the models these loads are of, from the rows `publish` writes as metadata_dependency; a refresh orders and skips by it and reads no project file
        var loadedKinds = loads.Select(l => l.Model).Distinct(StringComparer.Ordinal).ToDictionary(n => n, n => sources[n].Definition.Kind, StringComparer.OrdinalIgnoreCase);
        var loadsFile = new LoadsFile(connection, ProjectHash(ctx, connection, models.Select(x => x.Model).ToList()), loads, excluded.OrderBy(x => x.Model, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToList(),
            MetadataBuilder.LoadDependencies(dependencies, loadedKinds),
            ctx.Models.Where(m => loadedKinds.ContainsKey(m.Name)).OrderBy(m => m.Name, StringComparer.Ordinal).Select(ModelFacts.Of).ToList());

        // ---- the refresh plans: who is in which, in what order ----
        var members = new Dictionary<string, List<RefreshMember>>(StringComparer.Ordinal);
        var planExcluded = new Dictionary<string, List<RefreshPlanExclusion>>(StringComparer.Ordinal);
        List<RefreshMember> MembersOf(string plan) => members.TryGetValue(plan, out var l) ? l : members[plan] = [];
        List<RefreshPlanExclusion> ExcludedOf(string plan) => planExcluded.TryGetValue(plan, out var l) ? l : planExcluded[plan] = [];
        foreach (var model in modelOrder)
        {
            var def = model.Definition;
            var ops = renders[def.Name].Loads.Where(l => l.Target == connection).ToList();
            if (def.Refresh.Count == 0)
            {
                var op = ops.FirstOrDefault(o => o.IsDefault) ?? (ops.Count == 1 ? ops[0] : null);
                if (op == null) { ExcludedOf(RefreshPlanSettings.DefaultName).Add(new(def.Name, "-", "it has several load operations and none is the default")); continue; }
                Place(RefreshPlanSettings.DefaultName, new RefreshMembership(RefreshPlanSettings.DefaultName, op.Operation, op.Operation, [], null), def, ops, implied: true);
                continue;
            }
            foreach (var m in def.Refresh) Place(m.Plan, m, def, ops);
        }

        void Place(string plan, RefreshMembership m, ModelDefinition def, List<RenderedOperation> ops, bool implied = false)
        {
            if (!ops.Any(o => o.Operation == m.Load)) return;                      // the load is limited to other connections (`connections:` on it)
            var where = new SourceLocation(m.File ?? def.Name, m.Line, 1);
            if (loadsFile.Find(def.Name, m.Load) is not { } entry)
            {
                ExcludedOf(plan).Add(new(def.Name, m.Name, excludedLoads.GetValueOrDefault((def.Name, m.Load), "it has no load plan")));
                return;
            }
            if (implied && entry.RequiredInputs.Any())
            {
                // nobody wrote this membership: a model whose default load needs a person is left out of `default`, as it always was, and says why
                ExcludedOf(plan).Add(new(def.Name, m.Name, $"the load `{m.Load}` needs a value from a person ({string.Join(", ", entry.RequiredInputs.Select(i => "@" + i.Name))}); name it in a plan with `inputs:`, or run it with `--input`"));
                return;
            }
            var ok = true;
            foreach (var (name, value) in m.Inputs)
            {
                if (entry.Inputs.FirstOrDefault(i => i.Name == name && i.Provides.Count == 0) is not { } input)
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.RefreshMembershipInvalid, where, $"`{def.Name}`: the load `{m.Load}` in the plan `{plan}` is given the input `{name}`, which it does not take.",
                        entry.Inputs.Count(i => i.Provides.Count == 0) == 0 ? "It takes no input." : $"Its inputs: {string.Join(", ", entry.Inputs.Where(i => i.Provides.Count == 0).Select(i => i.Name))}."));
                    ok = false;
                }
                else if (!InputFits(input.Type, value))
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.RefreshMembershipInvalid, where, $"`{def.Name}`: the value `{value}` of the input `{name}` of `{m.Load}` in the plan `{plan}` is not a {input.Type}."));
                    ok = false;
                }
            }
            foreach (var missing in entry.RequiredInputs.Where(i => m.Inputs.All(g => g.Key != i.Name)))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.RefreshInputMissing, where, $"`{def.Name}`: the load `{m.Load}` in the plan `{plan}` needs a value for `{missing.Name}` ({missing.Type}), and the membership gives none."));
                ok = false;
            }
            if (ok) MembersOf(plan).Add(new(m.Name, def.Name, m.Load, entry.Hash, m.OnFailure, m.Inputs.Select(i => new RefreshInputValue(i.Key, i.Value)).ToList()));
        }

        // a plan the project declares has a file even when no model is in it yet; `default` has one when anything is in it
        var names = members.Keys.Concat(planExcluded.Keys).Concat(ctx.Config.RefreshPlans.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var plans = new List<RefreshPlanFile>();
        foreach (var name in names)
        {
            ctx.Config.RefreshPlans.TryGetValue(name, out var declared);
            plans.Add(new RefreshPlanFile(connection, name, declared?.Info ?? ObjectInfo.Empty, RefreshPlanSettingsText.From(declared), members.GetValueOrDefault(name) ?? [],
                (planExcluded.GetValueOrDefault(name) ?? []).OrderBy(x => x.Model, StringComparer.Ordinal).ThenBy(x => x.Member, StringComparer.Ordinal).ToList()));
        }
        return new CompiledRefresh(loadsFile, plans);
    }
}
