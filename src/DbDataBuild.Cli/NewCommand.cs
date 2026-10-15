using DbDataBuild.Core;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project create` (effect: repo files only; DESIGN.md 15.7). Without a template name it lists the project templates built into the executable. With one it writes that project into a directory (default: a
/// directory named after the template): models, mapped models, seeds that generate their rows, tests and a README, ready to `validate`, `seed`, `sample` and `test` with nothing else installed. It never overwrites a file.
/// </summary>
internal static class NewCommand
{
    public static int Run(CommandSpec spec, string? template, string? directory, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        output.Payload("templates", TemplateStore.All.Select(t => new { name = t.Name, description = t.Description, files = t.Files.Count }).ToList());
        if (template == null)
        {
            output.Payload("directory", null);
            output.Payload("written", new List<string>());
            output.WriteLine();
            output.WriteLine("Project templates:");
            foreach (var t in TemplateStore.All) output.WriteLine($"  {t.Name,-10} {t.Description}  ({t.Files.Count} files)");
            output.WriteLine("\nCreate one with the command below.");
            output.Next("project create <template> [directory]");
            return CliApp.ExitOk;
        }
        if (TemplateStore.Find(template) is not { } found)
        {
            error.WriteLine($"No template named `{template}`. Templates: {string.Join(", ", TemplateStore.All.Select(t => t.Name))}.");
            return CliApp.ExitUsage;
        }
        var target = Path.GetFullPath(directory ?? found.Name);
        var clashes = found.Files.Where(f => File.Exists(Path.Combine(target, f.Path))).Select(f => f.Path).ToList();
        if (clashes.Count > 0)
        {
            error.WriteLine($"{target} already has {string.Join(", ", clashes.Take(5))}{(clashes.Count > 5 ? $" and {clashes.Count - 5} more" : "")} of this template; nothing was written. Use an empty or new directory.");
            return CliApp.ExitUsage;
        }
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var f in found.Files)
        {
            var path = Path.Combine(target, f.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, f.Text, utf8);
        }
        output.Payload("directory", target);
        output.Payload("written", found.Files.Select(f => f.Path).ToList());
        output.WriteLine();
        output.WriteLine($"Wrote {found.Files.Count} file(s) of the `{found.Name}` project to {target}.");
        output.WriteLine("Read README.md in it first. The commands below take --project (or run them from inside the directory).");
        output.Next($"project compile --project {target}", $"project seed --project {target}", $"project tests run --project {target}");
        return CliApp.ExitOk;
    }
}
