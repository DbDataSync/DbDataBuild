using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;

namespace DbDataBuild.Cli;

/// <summary>
/// Reads and checks the hook scripts of a model for one target (DESIGN.md 8, hooks). Every problem is DDB-323 (or a group problem from the resolver), reported before
/// anything is planned: a missing file, a script the target's offline parser rejects, an event the model's kind cannot have.
/// </summary>
internal static class HookLoader
{
    public static IReadOnlyList<PlannedHook> Load(BuiltModel built, ProjectConfig config, string target, string root, List<Diagnostic> diags)
    {
        var model = built.Definition;
        var resolved = HookReader.Resolve(model, config, target, built.DefinitionFile, diags);
        var result = new List<PlannedHook>();
        var engine = TargetRegistry.Get(config.EngineOf(target) ?? target);
        int? version = config.TargetVersions.TryGetValue(target, out var v) ? v : null;
        foreach (var hook in resolved)
        {
            var where = new SourceLocation(built.DefinitionFile, model.Hooks.FirstOrDefault(h => h.Name == hook.Name || h.Use == hook.Group)?.Line ?? 0, 1);
            var action = HookEvents.Find(hook.Event)!.Action;
            if (model.Kind == ModelKinds.View && action is "load" or "backfill")
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.HookScriptInvalid, where, $"Hook `{hook.Name}` of {model.Name} is on `{hook.Event}`, but a view has no {action}."));
                continue;
            }
            var path = Path.GetFullPath(Path.Combine(root, hook.ScriptPath));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.HookScriptInvalid, where, $"Hook `{hook.Name}`: `{hook.ScriptPath}` is outside the project."));
                continue;
            }
            if (!File.Exists(path))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.HookScriptInvalid, where, $"Hook `{hook.Name}` of {model.Name} names `{hook.ScriptPath}` for {target}, which does not exist.", Fix: $"Create `{hook.ScriptPath}`, or change the hook."));
                continue;
            }
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.HookScriptInvalid, where, $"Hook `{hook.Name}`: `{hook.ScriptPath}` is empty."));
                continue;
            }
            var problems = engine.Validate(text, hook.ScriptPath, version);
            if (problems.Count > 0)
            {
                diags.AddRange(problems.Select(p => new Diagnostic(DiagnosticCatalog.HookScriptInvalid, p.Location, $"Hook `{hook.Name}` of {model.Name} for {target}: {p.Found}")));
                continue;
            }
            result.Add(new PlannedHook(hook, text, Hashing.ScriptHash(text)));
        }
        return result;
    }
}
