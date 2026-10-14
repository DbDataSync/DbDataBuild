using DbDataBuild.Core;
using DbDataBuild.Planning;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection deploy`: change a connection's structure to match the models. One command for the whole flow a person goes through. By default it plans (asking the questions a plan needs),
/// writes the plan, shows it and asks whether to apply it. `--write-plan` stops after writing; `--apply-plan` applies a plan that was written; `--ack` records a decision the plan needs
/// (a drift, a changed definition, a history). Applying a plan that stopped part-way continues it, so there is no resume option.
/// </summary>
internal static class DeployCommand
{
    public static int Run(CommandSpec spec, string root, string? target, string[] models, FileInfo? answers, bool acceptInferred, DirectoryInfo? outDir, string[] ops, string[] backfill, string[] parameters,
        bool writePlan, FileInfo? applyPlan, string? ack, string? reason, bool allowRisky, string[] allowDestructive, bool allowDirty, bool dryRun, bool yes, string[] inputArgs,
        TextWriter output, TextWriter error, TextReader input, bool interactive, Func<string, string?> env)
    {
        if (ack != null)
        {
            if (writePlan || applyPlan != null) { error.WriteLine("--ack records a decision and nothing else: it does not go with --write-plan or --apply-plan."); return CliApp.ExitUsage; }
            var colon = ack.IndexOf(':');
            if (colon <= 0 || colon == ack.Length - 1) { error.WriteLine($"--ack takes kind:name (drift:marts.fct, definition:marts.fct, history:marts.fct.column), not `{ack}`."); return CliApp.ExitUsage; }
            return AckCommand.Run(spec with { Effect = EffectClass.TrackingTablesOnly, Disposition = "P⇒T" }, root, ack[..colon], ack[(colon + 1)..], reason, target, output, error, env);
        }
        var pins = new List<InputPin>();
        foreach (var text in inputArgs)
        {
            if (InputPin.Parse(text, out var problem) is not { } pin) { error.WriteLine(problem); return CliApp.ExitUsage; }
            pins.Add(pin);
        }
        // a computed input that moved since the plan: an interactive deployer is asked once for all of them; `--yes` is for scripts and does not say yes to this
        Func<IReadOnlyList<MovedInput>, bool>? approve = interactive && !yes ? moved =>
        {
            ApplyCommand.WriteMoved(moved, output);
            output.Write("Continue with the new values? [y/N] ");
            var reply = input.ReadLine()?.Trim();
            return string.Equals(reply, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(reply, "yes", StringComparison.OrdinalIgnoreCase);
        } : null;
        if (applyPlan != null)
        {
            if (writePlan) { error.WriteLine("--apply-plan applies a plan that was written; --write-plan writes one. Give one of them."); return CliApp.ExitUsage; }
            return ApplyCommand.Run(spec, applyPlan.FullName, root, dryRun, allowRisky, allowDestructive, allowDirty, output, error, env, target, pins: pins, approveMoved: approve);
        }

        string? written = null;
        var exit = PlanCommand.Plan(writePlan ? spec with { Effect = EffectClass.TargetReadOnly, Disposition = "C→P" } : spec, root, target, models, answers, acceptInferred, outDir, ops, backfill, parameters,
            output, error, input, interactive, env, path => written = path, hintApply: writePlan, pins: pins);
        if (writePlan || written == null || exit != CliApp.ExitOk) return exit;

        var relative = File.Exists(written) ? Path.GetRelativePath(root, written).Replace('\\', '/') : Path.GetFileName(written);       // a plan kept in the database is named by its id
        if (!yes)
        {
            if (!interactive)
            {
                output.WriteLine($"Not applied: nobody is here to confirm. Read the plan, then `{ProductInfo.Cli} connection deploy --apply-plan {relative}` (or give --yes to apply it from the start).");
                return CliApp.ExitOk;
            }
            output.Write($"Apply this plan now? [y/N] ");
            var reply = input.ReadLine()?.Trim();
            if (!string.Equals(reply, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(reply, "yes", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine($"Not applied. Apply it later with `{ProductInfo.Cli} connection deploy --apply-plan {relative}`.");
                return CliApp.ExitOk;
            }
        }
        return ApplyCommand.Run(spec, written, root, dryRun, allowRisky, allowDestructive, allowDirty, output, error, env, target, afterPlan: true, approveMoved: approve);
    }
}
