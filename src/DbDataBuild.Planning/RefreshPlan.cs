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

/// <summary>An input of a load: its name and type, and where its default comes from.</summary>
/// <param name="Source">`parameter` (a value the project's files give, recorded here), `resolver` (read from the connection when the load runs) or `required` (it has no default: a person, or a membership of a plan, gives it).</param>
public sealed record RefreshParameter(string Name, string Type, string Source, string? Value)
{
    public const string FromParameter = "parameter";
    public const string FromResolver = "resolver";
    public const string Required = "required";
}

/// <summary>
/// One load plan: the compiled form of one load of one model on one connection (docs/research/refresh-plans.md, section 2). The scripts are the committed ones under `rendered/&lt;connection&gt;/`, named here with their
/// hashes; a value that is only known when the load runs (a watermark) is not here, only the resolver that finds it and what to do when it finds nothing. <see cref="Hash"/> is the hash of the load plan's own text, which
/// the members of a refresh plan name it by.
/// </summary>
/// <param name="Load">The load's name (`default` for the one a kind supplies).</param>
/// <param name="ShapeHash">The shape the compiled project expects of the model's table, which a refresh check compares with what it finds.</param>
public sealed record LoadPlanEntry(string Model, string Load, string Strategy, bool IsDefault, string File, string FileHash, string? ResolverFile, string? ResolverHash, string DefinitionHash, string ShapeHash,
    IReadOnlyList<RefreshParameter> Inputs, string? OnNull, string? Initial, IReadOnlyList<RefreshHook> Before, IReadOnlyList<RefreshHook> After)
{
    public string Hash => LoadsDocument.HashOf(this);

    /// <summary>`model.load`, the name a person gives to run it on its own.</summary>
    public string FullName => $"{Model}.{Load}";

    /// <summary>The inputs that have no default: a load with any of them cannot run unless someone gives the value.</summary>
    public IEnumerable<RefreshParameter> RequiredInputs => Inputs.Where(i => i.Source == RefreshParameter.Required);
}

/// <summary>A model or load that has no load plan on the connection, and why.</summary>
public sealed record RefreshExclusion(string Model, string Reason);

/// <summary>
/// Every load plan of every model on one connection (`rendered/&lt;connection&gt;/loads.yml`), so any load can be run on its own, in a plan or not. <see cref="ProjectHash"/> is a hash over the structure the compiled project
/// expects of the whole connection, which a deploy records, so a refresh can ask whether this project was deployed.
/// </summary>
public sealed record LoadsFile(string Connection, string ProjectHash, IReadOnlyList<LoadPlanEntry> Loads, IReadOnlyList<RefreshExclusion> Excluded)
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

/// <summary>A membership of the plan that was left out of it, and why (a copy between connections is run by a deploy until copies are loads of a refresh).</summary>
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
public sealed record RefreshPlanSettingsText(string? OnFailure, string? OnBusy, string? ExpectedEvery, int? Parallel, string? Check, string? OnFail)
{
    public static RefreshPlanSettingsText From(RefreshPlanSettings? s) => s == null ? new(null, null, null, null, null, null)
        : new(s.OnFailure, s.OnBusy, s.ExpectedEvery?.Text, s.Parallel, s.Check is { } c ? LifecycleSettings.Name(c) : null, s.OnFail is { } f ? LifecycleSettings.Name(f) : null);
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
        if (l.Inputs.Count > 0)
        {
            sb.AppendLineLf("    inputs:");
            foreach (var prm in l.Inputs)
            {
                sb.Append("      - name: ").AppendLineLf(Q(prm.Name));
                sb.Append("        type: ").AppendLineLf(Q(prm.Type));
                sb.Append("        source: ").AppendLineLf(Q(prm.Source));
                if (prm.Value != null) sb.Append("        value: ").AppendLineLf(Q(prm.Value));
            }
        }
        Hooks(sb, "before", l.Before);
        Hooks(sb, "after", l.After);
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
        if (f.Loads.Count > 0) sb.AppendLineLf("loads:");
        foreach (var l in f.Loads)
        {
            sb.Append("  - hash: ").AppendLineLf(Q(l.Hash));
            sb.Append(EntryBody(l));
        }
        if (f.Excluded.Count > 0) sb.AppendLineLf("excluded:");
        foreach (var x in f.Excluded)
        {
            sb.Append("  - model: ").AppendLineLf(Q(x.Model));
            sb.Append("    reason: ").AppendLineLf(Q(x.Reason));
        }
        return sb.ToString();
    }

    private static readonly string[] TopKeys = ["loads_file", "loads", "excluded"];
    private static readonly string[] HeaderKeys = ["hash", "connection", "project_hash", "loads"];
    private static readonly string[] EntryKeys = ["hash", "model", "load", "strategy", "default", "file", "file_hash", "resolver_file", "resolver_hash", "definition_hash", "shape_hash", "on_null", "initial", "inputs", "before", "after"];
    private static readonly string[] HookKeys = ["name", "event", "file", "file_hash", "effect", "risk"];

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
            foreach (var pm in r.Maps(m, "inputs", "input")) { r.Keys(pm, ["name", "type", "source", "value"], "an input"); inputs.Add(new(r.Req(pm, "name"), r.Req(pm, "type"), r.Req(pm, "source"), r.S(pm, "value"))); }
            stored.Add(r.Req(m, "hash"));
            loads.Add(new(r.Req(m, "model"), r.Req(m, "load"), r.Req(m, "strategy"), r.S(m, "default") == "true", r.Req(m, "file"), r.Req(m, "file_hash"), r.S(m, "resolver_file"), r.S(m, "resolver_hash"),
                r.Req(m, "definition_hash"), r.Req(m, "shape_hash"), inputs, r.S(m, "on_null"), r.S(m, "initial"), HookList(m, "before"), HookList(m, "after")));
        }
        var excluded = new List<RefreshExclusion>();
        foreach (var m in r.Maps(top, "excluded", "exclusion")) { r.Keys(m, ["model", "reason"], "an exclusion"); excluded.Add(new(r.Req(m, "model"), r.Req(m, "reason"))); }

        var result = new LoadsFile(r.Req(header, "connection"), r.Req(header, "project_hash"), loads, excluded);
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
    private static readonly string[] HeaderKeys = ["hash", "connection", "plan", "title", "description", "tags", "properties", "on_failure", "on_busy", "expected_every", "parallel", "check", "on_fail", "members"];
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

        var settings = new RefreshPlanSettingsText(r.S(header, "on_failure"), r.S(header, "on_busy"), r.S(header, "expected_every"), r.Int(header, "parallel"), r.S(header, "check"), r.S(header, "on_fail"));
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

/// <summary>What a refresh step belongs to, for the tracking rows of the run: the membership (the load's name unless the plan says another), the load plan it ran by hash, and the inputs it ran with, each with where its value came from, as JSON.</summary>
public sealed record RefreshStepInfo(string Membership, string LoadPlanHash, string InputsJson);

/// <summary>One member of the plan as the run saw it: its place in the order, and whether this run's selection included it.</summary>
public sealed record RefreshRunMember(int Position, string Membership, string Model, string Load, string LoadPlanHash, bool Selected);

/// <summary>
/// Everything a refresh run records beyond its steps: the plan's name (null for a load named directly), the selection that was asked for (empty for the whole plan), the plan's members as they were when it ran, and the
/// step information by step id.
/// </summary>
public sealed record RefreshRunInfo(string? PlanName, string Selection, IReadOnlyList<RefreshRunMember> Members, IReadOnlyDictionary<string, RefreshStepInfo> Steps);
