using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>An index a model declares (DESIGN.md 6, indexes). The operator decides what is enforced or indexed; nothing is inferred from `unique_key`.</summary>
/// <param name="Include">Non-key (covering) columns.</param>
/// <param name="Targets">Restricts the index to these targets; null means every target the model declares.</param>
public sealed record IndexDefinition(string Name, IReadOnlyList<string> Columns, bool Unique, IReadOnlyList<string> Include, IReadOnlyList<string>? Targets, int Line = 0, ObjectInfo? Info = null)
{
    public bool AppliesTo(string target) => Targets == null || Targets.Contains(target);
}

/// <summary>
/// One event a hook can attach to: when it happens in a plan, as `pre_<action>` or `post_<action>`. The registry is data: a new kind of hook is one row here,
/// plus the place in the planner that fires it. An event the planner does not fire yet is <see cref="Fires"/> = false and is refused when a model uses it,
/// so a hook never silently does nothing.
/// </summary>
public sealed record HookEvent(string Name, string Phase, string Action, bool Fires, string Description);

public static class HookEvents
{
    public static readonly IReadOnlyList<string> Phases = ["pre", "post"];

    public static readonly IReadOnlyList<HookEvent> All =
    [
        .. new (string Action, bool Fires, string What)[]
        {
            ("create", true, "the object's CREATE TABLE or CREATE VIEW step"),
            ("alter", true, "the group of ALTER, ADD, DROP, RENAME and index steps that change an existing object"),
            ("drop", false, "dropping the object (the tool has no step that drops a whole object yet)"),
            ("load", true, "the model's routine load step"),
            ("backfill", true, "the model's backfill step"),
        }.SelectMany(a => Phases.Select(p => new HookEvent($"{p}_{a.Action}", p, a.Action, a.Fires, $"{(p == "pre" ? "Before" : "After")} {a.What}."))),
    ];

    public static HookEvent? Find(string name) => All.FirstOrDefault(e => e.Name == name);
    public static IReadOnlyList<string> Names => All.Select(e => e.Name).ToList();
}

/// <summary>
/// A hook entry as written: either a reference to a project-level group (<see cref="Use"/>), or a named script attached to an event. A script is one path, or a path
/// per target; the file holds native SQL for that engine and is executed exactly as committed.
/// </summary>
/// <param name="Effect">`ddl` or `data`: what the script is allowed to do, which decides which command may run it (`run` runs only data hooks).</param>
/// <param name="Risk">The author's declaration of how risky the script is (safe, risky, destructive); it sets the step's risk class in the plan.</param>
public sealed record HookDefinition(
    string? Name, string? Event, string? Use, string? Script, IReadOnlyDictionary<string, string>? ScriptByTarget, IReadOnlyList<string>? Targets,
    string Effect, string Risk, int Line = 0)
{
    public bool IsReference => Use != null;
}

/// <summary>A hook after groups are expanded and the target is chosen: the script file to run, in plan order.</summary>
public sealed record ResolvedHook(string Name, string Event, string ScriptPath, string Effect, string Risk, string? Group);

public static partial class HookReader
{
    private static readonly string[] Keys = ["name", "event", "script", "connections", "effect", "risk", "use"];
    public static readonly IReadOnlyList<string> Effects = ["ddl", "data"];
    public static readonly IReadOnlyList<string> Risks = ["safe", "risky", "destructive"];

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*$")]
    private static partial Regex NamePattern();

    /// <summary>A project-relative path with forward slashes, inside the project, to a .sql file.</summary>
    public static bool IsSafePath(string path) =>
        path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    /// <summary>Reads a list of hook entries. <paramref name="allowUse"/> is false inside a group (groups do not nest).</summary>
    internal static List<HookDefinition> ReadList(YamlNode node, bool allowUse, string where, Action<DiagnosticDescriptor, YamlNode, string> add, IReadOnlySet<string>? connections = null)
    {
        var result = new List<HookDefinition>();
        if (node is not YamlSequence seq) { add(DiagnosticCatalog.InvalidValue, node, $"{where} must be a list of hooks (the order is the order they run in)."); return result; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in seq.Items)
        {
            if (item is not YamlMapping m) { add(DiagnosticCatalog.InvalidValue, item, "Each hook must be a mapping."); continue; }
            foreach (var e in m.Entries.Where(e => !Keys.Contains(e.Key.Value)))
                add(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in a hook. Keys: {string.Join(", ", Keys)}.");

            string? S(string k) => (m.Get(k) as YamlScalar)?.Value;
            if (m.Get("use") != null)
            {
                if (!allowUse) { add(DiagnosticCatalog.InvalidValue, m, "A hook group cannot use another group."); continue; }
                if (m.Entries.Any(e => e.Key.Value != "use")) add(DiagnosticCatalog.InvalidValue, m, "A hook that says `use:` takes no other keys: the group's hooks are inserted here as written.");
                if (S("use") is { Length: > 0 } use && NamePattern().IsMatch(use)) result.Add(new HookDefinition(null, null, use, null, null, null, "ddl", "safe", m.Line));
                else add(DiagnosticCatalog.InvalidValue, m.Get("use")!, "`use` must name a hook group (letters, digits, `_` and `-`).");
                continue;
            }

            var name = S("name");
            if (name == null || !NamePattern().IsMatch(name)) { add(DiagnosticCatalog.InvalidValue, (YamlNode?)m.Get("name") ?? m, "A hook needs a `name` (letters, digits, `_` and `-`)."); continue; }
            if (!names.Add(name)) add(DiagnosticCatalog.DuplicateKey, m, $"The hook name `{name}` is used more than once in {where}.");

            var ev = S("event");
            var known = ev == null ? null : HookEvents.Find(ev);
            if (ev == null) add(DiagnosticCatalog.MissingKey, m, $"Hook `{name}` needs an `event`.");
            else if (known == null) add(DiagnosticCatalog.InvalidValue, m.Get("event")!, $"Hook `{name}`: `{ev}` is not an event. One of: {string.Join(", ", HookEvents.Names)}.");
            else if (!known.Fires) add(DiagnosticCatalog.InvalidValue, m.Get("event")!, $"Hook `{name}`: `{ev}` is reserved. {known.Description} is not something the planner does yet, so the hook would never run.");

            string? script = null;
            Dictionary<string, string>? byTarget = null;
            switch (m.Get("script"))
            {
                case YamlScalar s when s.Value.Length > 0:
                    script = s.Value;
                    if (!IsSafePath(script)) add(DiagnosticCatalog.InvalidValue, s, $"Hook `{name}`: `{script}` is not a project-relative .sql path (forward slashes, no `..`, no drive or absolute path).");
                    break;
                case YamlMapping sm:
                    byTarget = [];
                    foreach (var e in sm.Entries)
                    {
                        if (!TargetNames.All.Contains(e.Key.Value)) { add(DiagnosticCatalog.InvalidValue, e.Key, $"Hook `{name}`: unknown engine `{e.Key.Value}` (a `script` maps an engine to a path). One of: {string.Join(", ", TargetNames.All)}."); continue; }
                        if (e.Value is YamlScalar ps && IsSafePath(ps.Value)) byTarget[e.Key.Value] = ps.Value;
                        else add(DiagnosticCatalog.InvalidValue, e.Value, $"Hook `{name}`: the script for `{e.Key.Value}` must be a project-relative .sql path.");
                    }
                    if (byTarget.Count == 0) add(DiagnosticCatalog.InvalidValue, sm, $"Hook `{name}`: `script` maps no target to a path.");
                    break;
                default:
                    add(DiagnosticCatalog.MissingKey, m, $"Hook `{name}` needs a `script`: a path, or a path per target.");
                    break;
            }

            List<string>? targets = null;
            if (m.Get("connections") is { } tn)
            {
                if (tn is YamlSequence ts && ts.Items.Count > 0 && ts.Items.All(i => i is YamlScalar))
                {
                    targets = ts.Items.Cast<YamlScalar>().Select(i => i.Value).ToList();
                    foreach (var bad in targets.Where(t => !(connections ?? TargetNames.All.ToHashSet()).Contains(t))) add(DiagnosticCatalog.InvalidValue, tn, $"Hook `{name}`: unknown connection `{bad}`.");
                    if (byTarget != null) add(DiagnosticCatalog.InvalidValue, tn, $"Hook `{name}`: `targets` and a per-target `script` both say where it runs. Use one.");
                }
                else add(DiagnosticCatalog.InvalidValue, tn, $"Hook `{name}`: `targets` must be a non-empty list.");
            }

            var effect = S("effect") ?? "ddl";
            if (!Effects.Contains(effect)) { add(DiagnosticCatalog.InvalidValue, m.Get("effect")!, $"Hook `{name}`: effect `{effect}` is not one of {string.Join(", ", Effects)}."); effect = "ddl"; }
            var risk = S("risk") ?? "safe";
            if (!Risks.Contains(risk)) { add(DiagnosticCatalog.InvalidValue, m.Get("risk")!, $"Hook `{name}`: risk `{risk}` is not one of {string.Join(", ", Risks)}."); risk = "safe"; }

            result.Add(new HookDefinition(name, ev, null, script, byTarget, targets, effect, risk, m.Line));
        }
        return result;
    }

    /// <summary>
    /// The hooks that run for a model on a target, groups expanded in place and entries for other targets dropped. Problems (an unknown group, a clash of names after
    /// expansion) are reported once, here; the project config supplies the groups.
    /// </summary>
    /// <param name="target">The connection the hooks are resolved for. A `script` that maps engines to paths is looked up by that connection's engine; a `targets` list names connections.</param>
    public static IReadOnlyList<ResolvedHook> Resolve(ModelDefinition model, ProjectConfig config, string target, string file, List<Diagnostic> diags)
    {
        var engine = config.EngineOf(target) ?? target;
        var result = new List<ResolvedHook>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Emit(HookDefinition h, string? group)
        {
            var path = h.Script ?? (h.ScriptByTarget != null && h.ScriptByTarget.TryGetValue(engine, out var p) ? p : null);
            var applies = h.ScriptByTarget != null ? path != null : h.Targets == null || h.Targets.Contains(target);
            if (!applies || path == null) return;
            var name = group == null ? h.Name! : $"{group}.{h.Name}";
            if (!names.Add(name)) { diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, new(file, h.Line, 1), $"The hook name `{name}` appears twice for {model.Name} after groups are expanded.")); return; }
            result.Add(new ResolvedHook(name, h.Event!, path, h.Effect, h.Risk, group));
        }
        foreach (var h in model.Hooks)
        {
            if (!h.IsReference) { Emit(h, null); continue; }
            if (!config.HookGroups.TryGetValue(h.Use!, out var group))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, h.Line, 1), $"{model.Name} uses the hook group `{h.Use}`, which is not defined under `hook_groups` in {ProductInfo.ConfigFile}.",
                    Fix: $"Define `{h.Use}` under `hook_groups:`, or remove the reference."));
                continue;
            }
            foreach (var g in group) Emit(g, h.Use);
        }
        return result;
    }
}
