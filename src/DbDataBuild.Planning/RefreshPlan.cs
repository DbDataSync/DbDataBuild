using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Models.Yaml;
using DbDataBuild.State;

namespace DbDataBuild.Planning;

/// <summary>A data hook around a routine load, by name and file: the script is read from the project when the refresh runs, and must have the hash the plan recorded.</summary>
public sealed record RefreshHook(string Name, string Event, string File, string FileHash, string Effect, string Risk);

/// <summary>A parameter of the project or the connection that a resolver's text names (`${project.days}`), with the value it had when the project was compiled: the load plan carries it, so a run needs no project files.</summary>
public sealed record RefreshParameterRef(string Name, string Type, string Value);

/// <summary>
/// How an input is computed when its load runs (docs/research/refresh-plans.md, section 8). The text is in the load plan beside the input's name, so it is hashed with the plan: an `expression` as written, a `target` or
/// `source` resolver lowered for the engine it runs on. <see cref="Provides"/> are the inputs a resolver that answers several (named columns) provides. <see cref="Hash"/> is what the event records with the value.
/// </summary>
/// <param name="Engine">For a `source` resolver: the engine of the origin the text is lowered for (a copy may have origins of several engines, each with its own file).</param>
public sealed record RefreshInputResolver(string Kind, string Text, IReadOnlyList<string> Provides, IReadOnlyList<RefreshParameterRef> Parameters, string? Engine = null)
{
    public string Hash => Hashing.Sha256Hex(string.Join("\n", Kind, Text, string.Join(",", Parameters.Select(p => $"{p.Name}={p.Value}"))));
}

/// <summary>An input of a load: its name and type, and where its default comes from.</summary>
/// <param name="Source">`parameter` (a value the project's files give, recorded here), `default` (a value the load's `inputs:` give), `resolver` (the watermark, read from the connection when the load runs), `computed` (its <see cref="Resolver"/> finds it when the load runs), `provided` (computed by the resolver of <see cref="ProvidedBy"/>) or `required` (it has no default: a person, or a membership of a plan, gives it).</param>
/// <param name="Free">True for an input no script binds (`days`): resolvers and memberships use it, nothing is bound to the load for it.</param>
/// <param name="OnNull">What to do when the resolver finds NULL: `fail`, `initial` (use <see cref="Initial"/>) or `skip_load`.</param>
public sealed record RefreshParameter(string Name, string Type, string Source, string? Value, RefreshInputResolver? Resolver = null, bool Free = false, string? ProvidedBy = null, string OnNull = "fail", string? Initial = null)
{
    public const string FromParameter = "parameter";
    public const string FromDefault = "default";
    public const string FromResolver = "resolver";
    public const string Computed = "computed";
    public const string Provided = "provided";
    public const string Required = "required";

    /// <summary>The inputs this entry answers when it is a resolver that provides several.</summary>
    public IReadOnlyList<string> Provides => Resolver?.Provides ?? [];

    /// <summary>Whether a resolver (its own, or its group's) has to run to know the value, unless a person or a membership gave it.</summary>
    public bool NeedsResolver => Source is Computed or Provided;
}

/// <summary>
/// One load plan: the compiled form of one load of one model on one connection (docs/research/refresh-plans.md, section 2). The scripts are the committed ones under `rendered/&lt;connection&gt;/`, named here with their
/// hashes; a value that is only known when the load runs (a watermark) is not here, only the resolver that finds it and what to do when it finds nothing. <see cref="Hash"/> is the hash of the load plan's own text, which
/// the members of a refresh plan name it by.
/// </summary>
/// <param name="Load">The load's name (`default` for the one a kind supplies).</param>
/// <param name="ShapeHash">The shape the compiled project expects of the model's table, which a refresh check compares with what it finds.</param>
public sealed record LoadPlanEntry(string Model, string Load, string Strategy, bool IsDefault, string File, string FileHash, string? ResolverFile, string? ResolverHash, string DefinitionHash, string ShapeHash,
    IReadOnlyList<RefreshParameter> Inputs, string? OnNull, string? Initial, IReadOnlyList<RefreshHook> Before, IReadOnlyList<RefreshHook> After, string? MaxRuntime = null, RefreshCopy? Copy = null)
{
    public string Hash => LoadsDocument.HashOf(this);

    /// <summary>`model.load`, the name a person gives to run it on its own.</summary>
    public string FullName => $"{Model}.{Load}";

    /// <summary>The inputs that have no default: a load with any of them cannot run unless someone gives the value.</summary>
    public IEnumerable<RefreshParameter> RequiredInputs => Inputs.Where(i => i.Source == RefreshParameter.Required);
}

/// <summary>
/// One origin of a copy as a refresh runs it: the read in the origin's own dialect (<see cref="ReadWithWatermark"/> is the same read with `WHERE column >= @watermark`, for an incremental copy), how the origin's rows are told
/// apart (a slice), and, when a `source` resolver gives the copy's inputs, the inputs as that origin's engine runs them (each origin has its own lowered text).
/// </summary>
public sealed record RefreshCopyOrigin(string Connection, string Engine, string Table, string ReadText, string? ReadWithWatermark, string? SliceColumn, string? SliceValue, bool SliceAdded, IReadOnlyList<RefreshParameter> Inputs, IReadOnlyList<string>? ParameterOrder = null, string? Dialect = null, string? DialectHash = null);

/// <summary>An incremental copy's watermark: the origin is read from the newest value of <see cref="Column"/> that the destination holds (for the origin's own rows, with a slice), less <see cref="Lookback"/>.</summary>
public sealed record RefreshCopyWatermark(string Column, string Type, string? Lookback);

/// <summary>
/// What makes a load plan a copy (docs/research/refresh-plans.md, section 15): the staging table on the destination (its create text and its drop), the columns, and one entry per origin. A refresh runs, for each origin, the
/// transfer into the staging table and then the load (which reads the staging table), and drops the staging table at the end, as a deploy does.
/// </summary>
public sealed record RefreshCopy(string Staging, string CreateStaging, string DropStaging, IReadOnlyList<PlanColumn> Columns, RefreshCopyWatermark? Watermark, IReadOnlyList<RefreshCopyOrigin> Origins);

/// <summary>A model or load that has no load plan on the connection, and why.</summary>
public sealed record RefreshExclusion(string Model, string Reason);

/// <summary>
/// One edge of the dependency graph a loads file carries: <see cref="Model"/> depends on <see cref="DependsOn"/>, <see cref="Depth"/> steps away (1 is a direct read, more is the shortest path through other things, a view or a
/// model that is not loaded on the connection included). Both sides are models the file loads; the kinds are their model kinds (`full`, `incremental_by_unique_key`, ...). These are the rows of `metadata_dependency` between
/// two loaded models, from the one computation (<c>MetadataBuilder.Dependencies</c>) that `publish` also uses.
/// </summary>
public sealed record LoadDependency(string Model, string ModelKind, string DependsOn, string DependsOnKind, int Depth);

/// <summary>
/// Every load plan of every model on one connection (`rendered/&lt;connection&gt;/loads.yml`), so any load can be run on its own, in a plan or not. <see cref="ProjectHash"/> is a hash over the structure the compiled project
/// expects of the whole connection, which a deploy records, so a refresh can ask whether this project was deployed. <see cref="Dependencies"/> is the dependency graph among the models the file loads, compiled with the
/// loads and hashed with them, so the order a refresh runs in cannot differ from the loads; null for a file compiled before the graph was carried (an empty list is a graph with no edges).
/// </summary>
/// <summary>A property of a model as a selector reads it (`property:key=value` compares the text; the type is kept so the model reads back as it was).</summary>
public sealed record ModelFactProperty(string Key, string Type, string Value);

/// <summary>
/// What the selector grammar reads of one loaded model, kept in `loads.yml` so a refresh can select without the project's files: the model kind, the connections it is on, its tags and properties, and the paths of its
/// files (for the `path:` term, a file and a directory). Only what depends on the working tree or on git (`changed:`) is not here.
/// </summary>
public sealed record ModelFacts(string Name, string Kind, IReadOnlyList<string> Connections, IReadOnlyList<string> Tags, IReadOnlyList<ModelFactProperty> Properties, string DefinitionFile, string? QueryFile)
{
    public static ModelFacts Of(ProjectModel m) => new(m.Name, m.Kind, m.Connections.Order(StringComparer.Ordinal).ToList(), m.Tags.Order(StringComparer.Ordinal).ToList(),
        m.Info.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ModelFactProperty(p.Key, p.Value.Type, p.Value.Text)).ToList(), m.DefinitionFile, m.QueryFile);

    public ProjectModel ToProjectModel() => new(Name, Kind, Connections, new ObjectInfo(null, null, Tags, Properties.ToDictionary(p => p.Key, p => new PropertyValue(p.Type, p.Value), StringComparer.Ordinal)), DefinitionFile, QueryFile);
}

public sealed record LoadsFile(string Connection, string ProjectHash, IReadOnlyList<LoadPlanEntry> Loads, IReadOnlyList<RefreshExclusion> Excluded, IReadOnlyList<LoadDependency>? Dependencies = null, IReadOnlyList<ModelFacts>? Models = null)
{
    public LoadPlanEntry? Find(string model, string load) => Loads.FirstOrDefault(l => l.Model == model && l.Load == load);
}

/// <summary>A value for one input of a load, given by a membership.</summary>
public sealed record RefreshInputValue(string Name, string Value);

/// <summary>One place a load is in a refresh plan, in the plan's order. It names its load plan by <see cref="LoadPlanHash"/>.</summary>
/// <param name="Name">The membership's name inside the plan (the load's name unless `as:` said another).</param>
public sealed record RefreshMember(string Name, string Model, string Load, string LoadPlanHash, string? OnFailure, IReadOnlyList<RefreshInputValue> Inputs)
{
    /// <summary>`model.membership`: what a person calls this member of the plan (an `--input` and the monitor refer to it so).</summary>
    public string FullName => $"{Model}.{Name}";
}

/// <summary>A membership of the plan that was left out of it, and why (a load that needs a person, a copy of a native model, a structure hook).</summary>
public sealed record RefreshPlanExclusion(string Model, string Member, string Reason);

/// <summary>
/// One refresh plan of one connection (`rendered/&lt;connection&gt;/refresh/&lt;name&gt;.yml`): its members in dependency order, each naming a load plan by hash with the inputs it gives, and the plan's own settings. It reads as
/// "what runs hourly".
/// </summary>
public sealed record RefreshPlanFile(string Connection, string Name, ObjectInfo Info, RefreshPlanSettingsText Settings, IReadOnlyList<RefreshMember> Members, IReadOnlyList<RefreshPlanExclusion> Excluded)
{
    /// <summary>The hash the plan's file carries over its own content (what an event records as the plan it ran).</summary>
    public string Hash => RefreshPlanDocument.ContentHash(this);
}

/// <summary>The settings of a plan as the plan file keeps them (text, as declared).</summary>
public sealed record RefreshPlanSettingsText(string? OnFailure, string? OnBusy, string? ExpectedEvery, int? Parallel, string? Check, string? OnFail, string? MaxWait = null, string? MaxRuntime = null)
{
    public static RefreshPlanSettingsText From(RefreshPlanSettings? s) => s == null ? new(null, null, null, null, null, null)
        : new(s.OnFailure, s.OnBusy, s.ExpectedEvery?.Text, s.Parallel, s.Check is { } c ? LifecycleSettings.Name(c) : null, s.OnFail is { } f ? LifecycleSettings.Name(f) : null, s.MaxWait?.Text, s.MaxRuntime?.Text);
}

/// <summary>Reading and writing helpers the compiled files share: quoting, and a reader that turns every problem into a diagnostic at the node.</summary>
internal static class CompiledYaml
{
    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Q(string s) => JsonSerializer.Serialize(s, Quote);

    /// <summary>A reader over one compiled file: a problem is DDB-435 at the node, never an exception.</summary>
    internal sealed class Reader(string file, List<Diagnostic> diags)
    {
        public void Bad(YamlNode at, string found) => diags.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(file, at.Line, at.Column), found));
        public string? S(YamlMapping m, string k) => (m.Get(k) as YamlScalar)?.Value;
        public string Req(YamlMapping m, string k) { var v = S(m, k); if (v == null) Bad(m, $"Required key `{k}` is missing."); return v ?? ""; }
        public void Keys(YamlMapping m, string[] allowed, string where) { foreach (var e in m.Entries.Where(e => !allowed.Contains(e.Key.Value))) Bad(e.Key, $"Unknown key `{e.Key.Value}` in {where}."); }
        public IEnumerable<YamlMapping> Maps(YamlMapping owner, string key, string what)
        {
            foreach (var n in (owner.Get(key) as YamlSequence)?.Items ?? [])
                if (n is YamlMapping m) yield return m;
                else Bad(n, $"Each {what} must be a mapping.");
        }
        public int? Int(YamlMapping m, string k)
        {
            var v = S(m, k);
            if (v == null) return null;
            if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
            Bad(m.Get(k)!, $"`{k}` must be a whole number.");
            return null;
        }
    }
}

/// <summary>One input of a load as a block of YAML: how a load plan (`loads.yml`) and a deploy plan both write it, and read it back.</summary>
internal static class RefreshInputYaml
{
    public static readonly string[] Keys = ["name", "type", "source", "value", "free", "provided_by", "on_null", "initial", "resolver"];
    private static string Q(string s) => CompiledYaml.Q(s);

    /// <param name="first">What starts the first line (a list item's `- `, or the indent of a mapping).</param>
    /// <param name="pad">The indent of every other line.</param>
    public static void Write(StringBuilder sb, string first, string pad, RefreshParameter prm)
    {
        sb.Append(first).Append("name: ").AppendLineLf(Q(prm.Name));
        sb.Append(pad).Append("type: ").AppendLineLf(Q(prm.Type));
        sb.Append(pad).Append("source: ").AppendLineLf(Q(prm.Source));
        if (prm.Value != null) sb.Append(pad).Append("value: ").AppendLineLf(Q(prm.Value));
        if (prm.Free) sb.Append(pad).AppendLineLf("free: true");
        if (prm.ProvidedBy != null) sb.Append(pad).Append("provided_by: ").AppendLineLf(Q(prm.ProvidedBy));
        if (prm.OnNull != "fail") sb.Append(pad).Append("on_null: ").AppendLineLf(Q(prm.OnNull));
        if (prm.Initial != null) sb.Append(pad).Append("initial: ").AppendLineLf(Q(prm.Initial));
        if (prm.Resolver is not { } rv) return;
        sb.Append(pad).AppendLineLf("resolver:");
        sb.Append(pad).Append("  kind: ").AppendLineLf(Q(rv.Kind));
        sb.Append(pad).Append("  text: ").AppendLineLf(Q(rv.Text));
        if (rv.Engine != null) sb.Append(pad).Append("  engine: ").AppendLineLf(Q(rv.Engine));
        if (rv.Provides.Count > 0) { sb.Append(pad).AppendLineLf("  provides:"); foreach (var p in rv.Provides) sb.Append(pad).Append("    - ").AppendLineLf(Q(p)); }
        if (rv.Parameters.Count > 0)
        {
            sb.Append(pad).AppendLineLf("  parameters:");
            foreach (var p in rv.Parameters) { sb.Append(pad).Append("    - name: ").AppendLineLf(Q(p.Name)); sb.Append(pad).Append("      type: ").AppendLineLf(Q(p.Type)); sb.Append(pad).Append("      value: ").AppendLineLf(Q(p.Value)); }
        }
    }

    /// <param name="extra">Keys the owner of the mapping adds to the input's own.</param>
    public static RefreshParameter Read(CompiledYaml.Reader r, YamlMapping pm, params string[] extra)
    {
        r.Keys(pm, [.. Keys, .. extra], "an input");
        RefreshInputResolver? resolver = null;
        if (pm.Get("resolver") is YamlMapping rm)
        {
            r.Keys(rm, ["kind", "text", "engine", "provides", "parameters"], "a resolver");
            var provides = ((rm.Get("provides") as YamlSequence)?.Items ?? []).OfType<YamlScalar>().Select(x => x.Value).ToList();
            var refs = new List<RefreshParameterRef>();
            foreach (var qm in r.Maps(rm, "parameters", "parameter")) { r.Keys(qm, ["name", "type", "value"], "a parameter"); refs.Add(new(r.Req(qm, "name"), r.Req(qm, "type"), r.Req(qm, "value"))); }
            resolver = new(r.Req(rm, "kind"), r.Req(rm, "text"), provides, refs, r.S(rm, "engine"));
        }
        else if (pm.Get("resolver") is { } bad) r.Bad(bad, "A resolver must be a mapping.");
        return new(r.Req(pm, "name"), r.Req(pm, "type"), r.Req(pm, "source"), r.S(pm, "value"), resolver, r.S(pm, "free") == "true", r.S(pm, "provided_by"), r.S(pm, "on_null") ?? "fail", r.S(pm, "initial"));
    }
}

/// <summary>Reads and writes `loads.yml`. Like a deploy plan it carries a hash of its own content and is refused when it was edited.</summary>
public static class LoadsDocument
{
    public const string FileName = "loads.yml";
    private const string HashPrefix = "  hash: ";
    private static string Q(string s) => CompiledYaml.Q(s);

    public static string Serialize(LoadsFile file)
    {
        var body = Body(file);
        return "loads_file:\n" + HashPrefix + Hashing.Sha256Hex(body) + "\n" + body;
    }

    private static void Hooks(StringBuilder sb, string key, IReadOnlyList<RefreshHook> hooks)
    {
        if (hooks.Count == 0) return;
        sb.Append("    ").Append(key).AppendLineLf(":");
        foreach (var h in hooks)
        {
            sb.Append("      - name: ").AppendLineLf(Q(h.Name));
            sb.Append("        event: ").AppendLineLf(Q(h.Event));
            sb.Append("        file: ").AppendLineLf(Q(h.File));
            sb.Append("        file_hash: ").AppendLineLf(Q(h.FileHash));
            sb.Append("        effect: ").AppendLineLf(Q(h.Effect));
            sb.Append("        risk: ").AppendLineLf(Q(h.Risk));
        }
    }

    private static void WriteCopy(StringBuilder sb, RefreshCopy c)
    {
        sb.AppendLineLf("    copy:");
        sb.Append("      staging: ").AppendLineLf(Q(c.Staging));
        sb.Append("      create_staging: ").AppendLineLf(Q(c.CreateStaging));
        sb.Append("      drop_staging: ").AppendLineLf(Q(c.DropStaging));
        sb.AppendLineLf("      columns:");
        foreach (var col in c.Columns) { sb.Append("        - name: ").AppendLineLf(Q(col.Name)); sb.Append("          type: ").AppendLineLf(Q(col.Type)); }
        if (c.Watermark is { } w)
        {
            sb.AppendLineLf("      watermark:");
            sb.Append("        column: ").AppendLineLf(Q(w.Column));
            sb.Append("        type: ").AppendLineLf(Q(w.Type));
            if (w.Lookback != null) sb.Append("        lookback: ").AppendLineLf(Q(w.Lookback));
        }
        sb.AppendLineLf("      origins:");
        foreach (var o in c.Origins)
        {
            sb.Append("        - connection: ").AppendLineLf(Q(o.Connection));
            sb.Append("          engine: ").AppendLineLf(Q(o.Engine));
            sb.Append("          table: ").AppendLineLf(Q(o.Table));
            sb.Append("          read: ").AppendLineLf(Q(o.ReadText));
            if (o.ReadWithWatermark != null) sb.Append("          read_incremental: ").AppendLineLf(Q(o.ReadWithWatermark));
            if (o.ParameterOrder is { Count: > 0 })
            {
                sb.AppendLineLf("          parameter_order:");
                foreach (var name in o.ParameterOrder) sb.Append("            - ").AppendLineLf(Q(name));
            }
            if (o.Dialect != null) sb.Append("          dialect: ").AppendLineLf(Q(o.Dialect));
            if (o.DialectHash != null) sb.Append("          dialect_hash: ").AppendLineLf(Q(o.DialectHash));
            if (o.SliceColumn != null)
            {
                sb.AppendLineLf("          slice:");
                sb.Append("            column: ").AppendLineLf(Q(o.SliceColumn));
                sb.Append("            value: ").AppendLineLf(Q(o.SliceValue ?? ""));
                if (o.SliceAdded) sb.AppendLineLf("            added: true");
            }
            if (o.Inputs.Count > 0)
            {
                sb.AppendLineLf("          inputs:");
                foreach (var prm in o.Inputs) RefreshInputYaml.Write(sb, "            - ", "              ", prm);
            }
        }
    }

    /// <summary>The text of one load plan as it stands in the file, without its own hash line.</summary>
    private static string EntryBody(LoadPlanEntry l)
    {
        var sb = new StringBuilder();
        sb.Append("    model: ").AppendLineLf(Q(l.Model));
        sb.Append("    load: ").AppendLineLf(Q(l.Load));
        sb.Append("    strategy: ").AppendLineLf(Q(l.Strategy));
        if (l.IsDefault) sb.AppendLineLf("    default: true");
        sb.Append("    file: ").AppendLineLf(Q(l.File));
        sb.Append("    file_hash: ").AppendLineLf(Q(l.FileHash));
        if (l.ResolverFile != null) { sb.Append("    resolver_file: ").AppendLineLf(Q(l.ResolverFile)); sb.Append("    resolver_hash: ").AppendLineLf(Q(l.ResolverHash ?? "")); }
        sb.Append("    definition_hash: ").AppendLineLf(Q(l.DefinitionHash));
        sb.Append("    shape_hash: ").AppendLineLf(Q(l.ShapeHash));
        if (l.OnNull != null) sb.Append("    on_null: ").AppendLineLf(Q(l.OnNull));
        if (l.Initial != null) sb.Append("    initial: ").AppendLineLf(Q(l.Initial));
        if (l.MaxRuntime != null) sb.Append("    max_runtime: ").AppendLineLf(Q(l.MaxRuntime));
        if (l.Inputs.Count > 0)
        {
            sb.AppendLineLf("    inputs:");
            foreach (var prm in l.Inputs) RefreshInputYaml.Write(sb, "      - ", "        ", prm);
        }
        Hooks(sb, "before", l.Before);
        Hooks(sb, "after", l.After);
        if (l.Copy is { } c) WriteCopy(sb, c);
        return sb.ToString();
    }

    /// <summary>The hash of one load plan: of its own text, so a change to its script, its inputs or its hooks changes it, and nothing else does.</summary>
    internal static string HashOf(LoadPlanEntry l) => Hashing.Sha256Hex(EntryBody(l));

    private static string Body(LoadsFile f)
    {
        var sb = new StringBuilder();
        sb.Append("  connection: ").AppendLineLf(Q(f.Connection));
        sb.Append("  project_hash: ").AppendLineLf(Q(f.ProjectHash));
        sb.Append("  loads: ").AppendLineLf(f.Loads.Count.ToString(CultureInfo.InvariantCulture));
        if (f.Dependencies != null) sb.Append("  dependencies: ").AppendLineLf(f.Dependencies.Count.ToString(CultureInfo.InvariantCulture));
        if (f.Models != null) sb.Append("  models: ").AppendLineLf(f.Models.Count.ToString(CultureInfo.InvariantCulture));
        if (f.Loads.Count > 0) sb.AppendLineLf("loads:");
        foreach (var l in f.Loads)
        {
            sb.Append("  - hash: ").AppendLineLf(Q(l.Hash));
            sb.Append(EntryBody(l));
        }
        if (f.Dependencies is { Count: > 0 })
        {
            sb.AppendLineLf("dependencies:");
            foreach (var d in f.Dependencies)
            {
                sb.Append("  - model: ").AppendLineLf(Q(d.Model));
                sb.Append("    model_kind: ").AppendLineLf(Q(d.ModelKind));
                sb.Append("    depends_on: ").AppendLineLf(Q(d.DependsOn));
                sb.Append("    depends_on_kind: ").AppendLineLf(Q(d.DependsOnKind));
                sb.Append("    depth: ").AppendLineLf(d.Depth.ToString(CultureInfo.InvariantCulture));
            }
        }
        if (f.Models is { Count: > 0 })
        {
            sb.AppendLineLf("models:");
            foreach (var m in f.Models)
            {
                sb.Append("  - name: ").AppendLineLf(Q(m.Name));
                sb.Append("    kind: ").AppendLineLf(Q(m.Kind));
                sb.Append("    file: ").AppendLineLf(Q(m.DefinitionFile));
                if (m.QueryFile != null) sb.Append("    query_file: ").AppendLineLf(Q(m.QueryFile));
                if (m.Connections.Count > 0) { sb.AppendLineLf("    connections:"); foreach (var c in m.Connections) sb.Append("      - ").AppendLineLf(Q(c)); }
                if (m.Tags.Count > 0) { sb.AppendLineLf("    tags:"); foreach (var tag in m.Tags) sb.Append("      - ").AppendLineLf(Q(tag)); }
                if (m.Properties.Count > 0)
                {
                    sb.AppendLineLf("    properties:");
                    foreach (var p in m.Properties) { sb.Append("      - key: ").AppendLineLf(Q(p.Key)); sb.Append("        type: ").AppendLineLf(Q(p.Type)); sb.Append("        value: ").AppendLineLf(Q(p.Value)); }
                }
            }
        }
        if (f.Excluded.Count > 0) sb.AppendLineLf("excluded:");
        foreach (var x in f.Excluded)
        {
            sb.Append("  - model: ").AppendLineLf(Q(x.Model));
            sb.Append("    reason: ").AppendLineLf(Q(x.Reason));
        }
        return sb.ToString();
    }

    private static readonly string[] TopKeys = ["loads_file", "loads", "dependencies", "models", "excluded"];
    private static readonly string[] HeaderKeys = ["hash", "connection", "project_hash", "loads", "dependencies", "models"];
    private static readonly string[] EntryKeys = ["hash", "model", "load", "strategy", "default", "file", "file_hash", "resolver_file", "resolver_hash", "definition_hash", "shape_hash", "on_null", "initial", "max_runtime", "inputs", "before", "after", "copy"];
    private static readonly string[] HookKeys = ["name", "event", "file", "file_hash", "effect", "risk"];

    private static RefreshCopy ReadCopy(CompiledYaml.Reader r, YamlMapping cm)
    {
        r.Keys(cm, ["staging", "create_staging", "drop_staging", "columns", "watermark", "origins"], "`copy:`");
        var columns = new List<PlanColumn>();
        foreach (var col in r.Maps(cm, "columns", "column")) { r.Keys(col, ["name", "type"], "a column"); columns.Add(new(r.Req(col, "name"), r.Req(col, "type"))); }
        RefreshCopyWatermark? watermark = null;
        if (cm.Get("watermark") is YamlMapping wm) { r.Keys(wm, ["column", "type", "lookback"], "`watermark:`"); watermark = new(r.Req(wm, "column"), r.Req(wm, "type"), r.S(wm, "lookback")); }
        var origins = new List<RefreshCopyOrigin>();
        foreach (var om in r.Maps(cm, "origins", "origin"))
        {
            r.Keys(om, ["connection", "engine", "table", "read", "read_incremental", "parameter_order", "dialect", "dialect_hash", "slice", "inputs"], "an origin");
            string? sliceColumn = null, sliceValue = null; var added = false;
            if (om.Get("slice") is YamlMapping sm) { r.Keys(sm, ["column", "value", "added"], "`slice:`"); sliceColumn = r.Req(sm, "column"); sliceValue = r.Req(sm, "value"); added = r.S(sm, "added") == "true"; }
            var inputs = new List<RefreshParameter>();
            foreach (var pm in r.Maps(om, "inputs", "input")) inputs.Add(RefreshInputYaml.Read(r, pm));
            origins.Add(new(r.Req(om, "connection"), r.Req(om, "engine"), r.Req(om, "table"), r.Req(om, "read"), r.S(om, "read_incremental"), sliceColumn, sliceValue, added, inputs,
                (om.Get("parameter_order") as YamlSequence)?.Items.OfType<YamlScalar>().Select(x => x.Value).ToList() is { Count: > 0 } order ? order : null, r.S(om, "dialect"), r.S(om, "dialect_hash")));
        }
        return new(r.Req(cm, "staging"), r.Req(cm, "create_staging"), r.Req(cm, "drop_staging"), columns, watermark, origins);
    }

    /// <summary>Reads `loads.yml`. Returns null with diagnostics when it is malformed, has unknown keys, or does not match its own hash.</summary>
    public static LoadsFile? Parse(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null && diags.Count == before) diags.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(file, 0, 0), "The loads file is empty."));
        if (root == null || diags.Count > before) return null;
        var r = new CompiledYaml.Reader(file, diags);
        if (root is not YamlMapping top) { r.Bad(root, "A loads file must be a mapping."); return null; }
        r.Keys(top, TopKeys, "the file");
        if (top.Get("loads_file") is not YamlMapping header) { r.Bad(top, "The `loads_file:` section is missing."); return null; }
        r.Keys(header, HeaderKeys, "`loads_file:`");

        List<RefreshHook> HookList(YamlMapping owner, string key)
        {
            var list = new List<RefreshHook>();
            foreach (var hm in r.Maps(owner, key, "hook")) { r.Keys(hm, HookKeys, "a hook"); list.Add(new(r.Req(hm, "name"), r.Req(hm, "event"), r.Req(hm, "file"), r.Req(hm, "file_hash"), r.Req(hm, "effect"), r.Req(hm, "risk"))); }
            return list;
        }

        var loads = new List<LoadPlanEntry>();
        var stored = new List<string>();
        foreach (var m in r.Maps(top, "loads", "load"))
        {
            r.Keys(m, EntryKeys, "a load");
            var inputs = new List<RefreshParameter>();
            foreach (var pm in r.Maps(m, "inputs", "input")) inputs.Add(RefreshInputYaml.Read(r, pm));
            stored.Add(r.Req(m, "hash"));
            RefreshCopy? copy = null;
            if (m.Get("copy") is YamlMapping cm) copy = ReadCopy(r, cm);
            else if (m.Get("copy") is { } badCopy) r.Bad(badCopy, "`copy:` must be a mapping.");
            loads.Add(new(r.Req(m, "model"), r.Req(m, "load"), r.Req(m, "strategy"), r.S(m, "default") == "true", r.Req(m, "file"), r.Req(m, "file_hash"), r.S(m, "resolver_file"), r.S(m, "resolver_hash"),
                r.Req(m, "definition_hash"), r.Req(m, "shape_hash"), inputs, r.S(m, "on_null"), r.S(m, "initial"), HookList(m, "before"), HookList(m, "after"), r.S(m, "max_runtime"), copy));
        }
        var excluded = new List<RefreshExclusion>();
        foreach (var m in r.Maps(top, "excluded", "exclusion")) { r.Keys(m, ["model", "reason"], "an exclusion"); excluded.Add(new(r.Req(m, "model"), r.Req(m, "reason"))); }

        // the graph: the header says whether the file carries one (and how many edges), so a file compiled before there was one reads as having none
        List<LoadDependency>? dependencies = null;
        if (header.Get("dependencies") != null)
        {
            dependencies = [];
            foreach (var m in r.Maps(top, "dependencies", "dependency"))
            {
                r.Keys(m, ["model", "model_kind", "depends_on", "depends_on_kind", "depth"], "a dependency");
                var depth = r.Int(m, "depth");
                if (depth is null or < 1) r.Bad(m, "`depth` must be a whole number from 1.");
                dependencies.Add(new(r.Req(m, "model"), r.Req(m, "model_kind"), r.Req(m, "depends_on"), r.Req(m, "depends_on_kind"), depth ?? 1));
            }
            if (int.TryParse(r.S(header, "dependencies"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count != dependencies.Count : true)
                r.Bad(header, "The count of `dependencies` in the header is not the number of dependencies listed.");
            var loaded = loads.Select(l => l.Model).ToHashSet(StringComparer.Ordinal);
            foreach (var d in dependencies.Where(d => !loaded.Contains(d.Model) || !loaded.Contains(d.DependsOn)))
                r.Bad(top, $"The dependency of `{d.Model}` on `{d.DependsOn}` names a model the file does not load.");
        }
        else if (top.Get("dependencies") is { } stray) r.Bad(stray, "`dependencies:` is listed but the header does not carry them.");

        // the facts selectors read: the header says whether the file carries them (and for how many models), so a file compiled before there were any reads as having none
        List<ModelFacts>? facts = null;
        if (header.Get("models") != null)
        {
            facts = [];
            List<string> Strings(YamlMapping owner, string key) => (owner.Get(key) as YamlSequence)?.Items.OfType<YamlScalar>().Select(x => x.Value).ToList() ?? [];
            foreach (var m in r.Maps(top, "models", "model"))
            {
                r.Keys(m, ["name", "kind", "file", "query_file", "connections", "tags", "properties"], "a model");
                var properties = new List<ModelFactProperty>();
                foreach (var pm in r.Maps(m, "properties", "property")) { r.Keys(pm, ["key", "type", "value"], "a property"); properties.Add(new(r.Req(pm, "key"), r.Req(pm, "type"), r.Req(pm, "value"))); }
                facts.Add(new(r.Req(m, "name"), r.Req(m, "kind"), Strings(m, "connections"), Strings(m, "tags"), properties, r.Req(m, "file"), r.S(m, "query_file")));
            }
            if (int.TryParse(r.S(header, "models"), NumberStyles.None, CultureInfo.InvariantCulture, out var modelCount) ? modelCount != facts.Count : true)
                r.Bad(header, "The count of `models` in the header is not the number of models listed.");
            var loadedNames = loads.Select(l => l.Model).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (!facts.Select(f => f.Name).Order(StringComparer.Ordinal).SequenceEqual(loadedNames))
                r.Bad(top, "The `models:` section must list exactly the models the file loads.");
        }
        else if (top.Get("models") is { } strayModels) r.Bad(strayModels, "`models:` is listed but the header does not carry them.");

        var result = new LoadsFile(r.Req(header, "connection"), r.Req(header, "project_hash"), loads, excluded, dependencies, facts);
        if (diags.Count > before) return null;
        var storedHash = r.S(header, "hash");
        if (storedHash == null) { r.Bad(header, "The loads file has no content hash."); return null; }
        if (storedHash != Hashing.Sha256Hex(Body(result)) || Serialize(result).Replace("\r\n", "\n") != text.Replace("\r\n", "\n") || stored.Where((h, i) => h != loads[i].Hash).Any())
        {
            r.Bad(header, "The loads file's content does not match its hash: the file was edited or damaged. `project compile` writes it again.");
            return null;
        }
        return result;
    }

    /// <summary>The hash a file carries over its own content.</summary>
    public static string ContentHash(LoadsFile f) => Hashing.Sha256Hex(Body(f));
}

/// <summary>Reads and writes a refresh plan's file (`refresh/&lt;name&gt;.yml`). Like a deploy plan it carries a hash of its own content and is refused when it was edited.</summary>
public static class RefreshPlanDocument
{
    public const string FolderName = "refresh";
    private const string HashPrefix = "  hash: ";
    private static string Q(string s) => CompiledYaml.Q(s);

    /// <summary>The file the plan lives in, relative to `rendered/`.</summary>
    public static string PathOf(string connection, string plan) => $"{connection}/{FolderName}/{plan}.yml";

    /// <summary>The name `refresh.plan.yml` had before there were named plans: a project compiled by an older tool has one, and `project compile` removes it.</summary>
    public const string RetiredFileName = "refresh.plan.yml";

    public static string Serialize(RefreshPlanFile plan)
    {
        var body = Body(plan);
        return "refresh_plan:\n" + HashPrefix + Hashing.Sha256Hex(body) + "\n" + body;
    }

    private static string Body(RefreshPlanFile p)
    {
        var sb = new StringBuilder();
        sb.Append("  connection: ").AppendLineLf(Q(p.Connection));
        sb.Append("  plan: ").AppendLineLf(Q(p.Name));
        if (p.Info.Title != null) sb.Append("  title: ").AppendLineLf(Q(p.Info.Title));
        if (p.Info.Description != null) sb.Append("  description: ").AppendLineLf(Q(p.Info.Description));
        if (p.Info.TagsForDocument is { } tags) { sb.AppendLineLf("  tags:"); foreach (var t in tags) sb.Append("    - ").AppendLineLf(Q(t)); }
        if (p.Info.PropertiesForDocument != null)
        {
            sb.AppendLineLf("  properties:");
            foreach (var kv in p.Info.Properties.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                sb.Append("    - key: ").AppendLineLf(Q(kv.Key));
                sb.Append("      type: ").AppendLineLf(Q(kv.Value.Type));
                sb.Append("      value: ").AppendLineLf(Q(kv.Value.Text));
            }
        }
        var s = p.Settings;
        if (s.OnFailure != null) sb.Append("  on_failure: ").AppendLineLf(Q(s.OnFailure));
        if (s.OnBusy != null) sb.Append("  on_busy: ").AppendLineLf(Q(s.OnBusy));
        if (s.MaxWait != null) sb.Append("  max_wait: ").AppendLineLf(Q(s.MaxWait));
        if (s.MaxRuntime != null) sb.Append("  max_runtime: ").AppendLineLf(Q(s.MaxRuntime));
        if (s.ExpectedEvery != null) sb.Append("  expected_every: ").AppendLineLf(Q(s.ExpectedEvery));
        if (s.Parallel != null) sb.Append("  parallel: ").AppendLineLf(s.Parallel.Value.ToString(CultureInfo.InvariantCulture));
        if (s.Check != null) sb.Append("  check: ").AppendLineLf(Q(s.Check));
        if (s.OnFail != null) sb.Append("  on_fail: ").AppendLineLf(Q(s.OnFail));
        sb.Append("  members: ").AppendLineLf(p.Members.Count.ToString(CultureInfo.InvariantCulture));
        if (p.Members.Count > 0) sb.AppendLineLf("members:");
        foreach (var m in p.Members)
        {
            sb.Append("  - member: ").AppendLineLf(Q(m.Name));
            sb.Append("    model: ").AppendLineLf(Q(m.Model));
            sb.Append("    load: ").AppendLineLf(Q(m.Load));
            sb.Append("    load_plan: ").AppendLineLf(Q(m.LoadPlanHash));
            if (m.OnFailure != null) sb.Append("    on_failure: ").AppendLineLf(Q(m.OnFailure));
            if (m.Inputs.Count > 0)
            {
                sb.AppendLineLf("    inputs:");
                foreach (var i in m.Inputs) { sb.Append("      - name: ").AppendLineLf(Q(i.Name)); sb.Append("        value: ").AppendLineLf(Q(i.Value)); }
            }
        }
        if (p.Excluded.Count > 0) sb.AppendLineLf("excluded:");
        foreach (var x in p.Excluded)
        {
            sb.Append("  - model: ").AppendLineLf(Q(x.Model));
            sb.Append("    member: ").AppendLineLf(Q(x.Member));
            sb.Append("    reason: ").AppendLineLf(Q(x.Reason));
        }
        return sb.ToString();
    }

    private static readonly string[] TopKeys = ["refresh_plan", "members", "excluded"];
    private static readonly string[] HeaderKeys = ["hash", "connection", "plan", "title", "description", "tags", "properties", "on_failure", "on_busy", "max_wait", "max_runtime", "expected_every", "parallel", "check", "on_fail", "members"];
    private static readonly string[] MemberKeys = ["member", "model", "load", "load_plan", "on_failure", "inputs"];

    /// <summary>Reads a refresh plan's file. Returns null with diagnostics when it is malformed, has unknown keys, or does not match its own hash.</summary>
    public static RefreshPlanFile? Parse(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null && diags.Count == before) diags.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(file, 0, 0), "The refresh plan file is empty."));
        if (root == null || diags.Count > before) return null;
        var r = new CompiledYaml.Reader(file, diags);
        if (root is not YamlMapping top) { r.Bad(root, "A refresh plan must be a mapping."); return null; }
        r.Keys(top, TopKeys, "the file");
        if (top.Get("refresh_plan") is not YamlMapping header) { r.Bad(top, "The `refresh_plan:` section is missing."); return null; }
        r.Keys(header, HeaderKeys, "`refresh_plan:`");

        var tags = ((header.Get("tags") as YamlSequence)?.Items ?? []).OfType<YamlScalar>().Select(t => t.Value).ToList();
        var properties = new Dictionary<string, PropertyValue>(StringComparer.Ordinal);
        foreach (var pm in r.Maps(header, "properties", "property")) { r.Keys(pm, ["key", "type", "value"], "a property"); properties[r.Req(pm, "key")] = new(r.Req(pm, "type"), r.Req(pm, "value")); }
        var info = new ObjectInfo(r.S(header, "title"), r.S(header, "description"), tags, properties);

        var members = new List<RefreshMember>();
        foreach (var m in r.Maps(top, "members", "member"))
        {
            r.Keys(m, MemberKeys, "a member");
            var inputs = new List<RefreshInputValue>();
            foreach (var im in r.Maps(m, "inputs", "input")) { r.Keys(im, ["name", "value"], "an input"); inputs.Add(new(r.Req(im, "name"), r.Req(im, "value"))); }
            members.Add(new(r.Req(m, "member"), r.Req(m, "model"), r.Req(m, "load"), r.Req(m, "load_plan"), r.S(m, "on_failure"), inputs));
        }
        var excluded = new List<RefreshPlanExclusion>();
        foreach (var m in r.Maps(top, "excluded", "exclusion")) { r.Keys(m, ["model", "member", "reason"], "an exclusion"); excluded.Add(new(r.Req(m, "model"), r.Req(m, "member"), r.Req(m, "reason"))); }

        var settings = new RefreshPlanSettingsText(r.S(header, "on_failure"), r.S(header, "on_busy"), r.S(header, "expected_every"), r.Int(header, "parallel"), r.S(header, "check"), r.S(header, "on_fail"), r.S(header, "max_wait"), r.S(header, "max_runtime"));
        var plan = new RefreshPlanFile(r.Req(header, "connection"), r.Req(header, "plan"), info, settings, members, excluded);
        if (diags.Count > before) return null;
        var stored = r.S(header, "hash");
        if (stored == null) { r.Bad(header, "The refresh plan has no content hash."); return null; }
        if (stored != Hashing.Sha256Hex(Body(plan)) || Serialize(plan).Replace("\r\n", "\n") != text.Replace("\r\n", "\n"))
        {
            r.Bad(header, "The refresh plan's content does not match its hash: the file was edited or damaged. `project compile` writes it again.");
            return null;
        }
        return plan;
    }

    /// <summary>The hash a file carries over its own content (what the event records as the plan it ran).</summary>
    public static string ContentHash(RefreshPlanFile plan) => Hashing.Sha256Hex(Body(plan));
}

/// <summary>One member of the plan as the run saw it: its place in the order, and whether this run's selection included it.</summary>
public sealed record RefreshRunMember(int Position, string Membership, string Model, string Load, string LoadPlanHash, bool Selected);

/// <summary>
/// Everything a refresh run records beyond its loads: the plan's name (null for a load named directly), the selection that was asked for (empty for the whole plan), and the plan's members as they were when it ran.
/// </summary>
public sealed record RefreshRunInfo(string? PlanName, string Selection, IReadOnlyList<RefreshRunMember> Members);
