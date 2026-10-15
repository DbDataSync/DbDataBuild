using DbDataBuild.Execution;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project model update [paths]`: the definitions of models from their queries (<see cref="DefineCommand"/>, unchanged) and then the metadata of the mapped models in scope from the connections they are
/// on (<see cref="ConnectionCapture"/>, the code `project update` runs too). `--offline` leaves the connections alone; whether it is the default is <see cref="UpdateDefaults"/>. A mapped model is never ignored: if its
/// connection cannot be read the command says so. `project update` is this for every model plus the dialects of the connections.
/// </summary>
internal static class ModelUpdateCommand
{
    public static int Run(CommandSpec spec, string root, string[] paths, FileInfo? answers, bool write, bool check, bool acceptInferred, bool offline, TextWriter output, TextWriter error, TextReader input, bool interactive,
        Func<string, string?> env, SessionOpener? open = null)
    {
        // a path that names a mapped model or a copy has no query to define from: it is the connections' part, so it is not handed to the definitions
        var rooted = Path.GetFullPath(root);
        string Relative(string p) { var full = Path.GetFullPath(p, rooted); return full.StartsWith(rooted, StringComparison.Ordinal) ? Path.GetRelativePath(rooted, full).Replace('\\', '/') : p; }
        var online = UpdateDefaults.Online(offline);
        var tablePaths = new HashSet<string>(StringComparer.Ordinal);
        if (paths.Length > 0 && online)
        {
            var known = ProjectContext.Load(root);
            foreach (var d in known.Project.DeclaredModels.Where(d => d.File.Length > 0)) tablePaths.Add(d.File);
            foreach (var c in known.Project.BuiltModels.Where(s => s.Definition.IsCopy)) tablePaths.Add(c.DefinitionFile);
        }
        var definitionPaths = paths.Where(p => !tablePaths.Contains(Relative(p))).ToArray();
        int exit;
        if (paths.Length > 0 && definitionPaths.Length == 0)
        {
            output.WriteLine($"{DbDataBuild.Core.ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}");
            exit = CliApp.ExitOk;
        }
        else
        {
            exit = DefineCommand.Run(spec, root, definitionPaths, answers, write, check, acceptInferred, output, error, input, interactive);
            if (exit == CliApp.ExitUsage) return exit;
        }
        output.Payload("online", online);
        if (!online)
        {
            output.WriteLine("Offline: no connection was read, so no mapped model was compared with its table.");
            return exit;
        }
        var ctx = ProjectContext.Load(root);
        var selectors = paths.Select(Relative).ToArray();
        var selection = UpdateSelection.Resolve(ctx, selectors, TextWriter.Null) ?? new UpdateSelection(false, new HashSet<string>());
        var outcome = ConnectionCapture.GatherAsync(ctx, selection, env, open ?? ProjectUpdateCommand.TestOpener.Value ?? ReadSession.OpenAsync).GetAwaiter().GetResult();
        output.Payload("connections", outcome.Connections.Select(c => new { connection = c.Connection, driver = c.Driver, status = c.Status, reason = c.Reason }).ToList());
        output.Payload("mapped_models", ConnectionCapture.MappedModelsPayload(outcome));
        output.Payload("catalog_notes", outcome.Notes);
        Func<int, bool>? confirm = write || check || !interactive ? null : n =>
        {
            output.Write($"Write {n} mapped model file(s)? [y/N] ");
            return input.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
        };
        var (captureExit, _, files) = ConnectionCapture.Finish(root, outcome, check, confirm, output, error);
        output.PayloadAppend("written", files);
        if (files.Count > 0 && exit == CliApp.ExitOk) output.Next("project compile");
        return captureExit != CliApp.ExitOk ? CliApp.ExitFindings : exit;
    }
}
