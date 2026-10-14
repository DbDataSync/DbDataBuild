using System.CommandLine;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Tui;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli;

/// <summary>`dbdatabuild ui terminal`: the terminal user interface. It connects to nothing itself; each action it runs is a command with its own effect class.</summary>
internal static class TuiCommand
{
    public static int Run(CommandSpec spec, string projectRoot, string? target, bool json, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (json) { error.WriteLine("`tui` is interactive and has no JSON form. Use the other commands with --format json."); return CliApp.ExitUsage; }
        if (Console.IsOutputRedirected || Console.IsInputRedirected) { error.WriteLine("`tui` needs a terminal: standard input and output must not be redirected."); return CliApp.ExitUsage; }
        if (!Directory.Exists(projectRoot)) { error.WriteLine($"The project directory `{projectRoot}` does not exist."); return CliApp.ExitUsage; }
        if (target != null && CommandTargets.NamesOf(projectRoot) is var names && !names.Contains(target)) { error.WriteLine($"Unknown connection `{target}`. One of: {string.Join(", ", names)}."); return CliApp.ExitUsage; }
        return TuiApp.Run(new CliHost(env), new TuiOptions(Path.GetFullPath(projectRoot), target));
    }

    /// <summary>Commands run in this process through the same entry point as the real CLI, always with `--format json`, never interactively.</summary>
    internal sealed class CliHost(Func<string, string?> env) : ICommandHost
    {
        private static readonly string[] Hidden = ["--help", "--version", "--format"];
        private IReadOnlyList<CommandInfo>? commands;

        public IReadOnlyList<CommandInfo> Commands => commands ??= Catalog();

        public CommandResult Run(IReadOnlyList<string> args, RunHooks? hooks = null)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = CommandContext.With(new CommandHooks(hooks?.Progress, hooks?.StopRequested),
                () => CliApp.Run([.. args, "--format", "json"], o, e, input: TextReader.Null, interactive: false, environment: env));
            return new CommandResult(exit, o.ToString(), e.ToString());
        }

        internal static IReadOnlyList<CommandInfo> Catalog()
        {
            var root = CliApp.Build(new StringWriter(), new StringWriter(), TextReader.Null, interactive: false);
            var result = new List<CommandInfo>();
            foreach (var spec in CommandSpecs.All.Where(s => s.Name is not ("ui terminal" or "ui mcp" or "ui web")))
            {
                var cmd = CliApp.Find(root, spec.Name)!;
                var args = cmd.Arguments.Select(a => new ArgumentInfo(a.Name, a.Description ?? "", a.Arity.MaximumNumberOfValues > 1, a.Arity.MinimumNumberOfValues > 0, ArgumentChoices(spec.Name, a.Name))).ToList();
                var options = cmd.Options.Where(o => !Hidden.Contains(o.Name)).Select(o => Describe(o)).ToList();
                result.Add(new CommandInfo(spec.Name, spec.Purpose, spec.Effect.Describe(), ImpactOf(spec), args, options, WriteFlagOf(spec.Name), spec.Lane, spec.Disposition));
            }
            return result;
        }

        private static OptionInfo Describe(Option o)
        {
            var type = o.ValueType;
            var kind = type == typeof(bool) ? OptionKind.Flag : type == typeof(int) || type == typeof(int?) ? OptionKind.Integer
                : type == typeof(DirectoryInfo) || type == typeof(FileInfo) ? OptionKind.Path : type == typeof(string[]) ? OptionKind.List : OptionKind.Text;
            string? def = o.HasDefaultValue ? o.GetDefaultValue() switch { null => null, string[] a => string.Join(",", a), var v => v.ToString() } : null;
            // the connections are the project's own, so a form takes the name as text (the target chooser lists them); the options with a closed set of values say them
            IReadOnlyList<string> choices = o.GetCompletions(System.CommandLine.Completions.CompletionContext.Empty).Select(c => c.Label).ToList();      // what `AcceptOnlyFromAmong` says
            if (choices.Count > 0 && kind == OptionKind.Text) kind = OptionKind.Choice;
            return new OptionInfo(o.Name, o.Description ?? "", kind, def, choices);
        }

        private static IReadOnlyList<string> ArgumentChoices(string command, string name) => (command, name) switch
        {
            _ => [],
        };

        /// <summary>Commands that only change something when a flag says so (`--write`), unless a flag says not to (`!--check`), or with any of several (`--apply-plan|--yes|--ack`).</summary>
        private static string? WriteFlagOf(string command) => command switch
        {
            "project compile" or "project update" => "!--check",
            "project model update" => "--write",
            "connection init" or "connection seed" => "--apply",
            "connection deploy" => "--apply-plan|--yes|--ack",
            "connection refresh" => "!--dry-run",
            _ => null,
        };

        // the two update commands read connections (a read login) and write the project's files: a person is asked before the second, an agent is not (project files are its work)
        private static Impact ImpactOf(CommandSpec spec) => spec.Name is "project update" or "project model update" ? Impact.RepoFiles : ImpactOf(spec.Effect);

        private static Impact ImpactOf(EffectClass e) => e switch
        {
            EffectClass.RepoFilesOnly => Impact.RepoFiles,
            EffectClass.TrackingTablesOnly => Impact.TrackingTables,
            EffectClass.TargetDataWrites => Impact.TargetData,
            EffectClass.TargetWrites => Impact.Target,
            _ => Impact.None,
        };
    }
}
