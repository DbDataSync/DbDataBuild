using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Tests.Unit;

internal static class TestSupport
{
    /// <summary>
    /// The rendering half of `project compile`, called directly with the arguments the old `render` took (`--project <dir>`, `--write`, `--check`, `--connection <c>`, model names; nothing at all prints). For tests of what is
    /// rendered, written and compared, with fixtures that are not meant to pass the project's other checks, which `project compile` runs too and fails on.
    /// </summary>
    public static (int Exit, string Out, string Err) RenderFiles(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var dir = ".";
        var models = new List<string>();
        var connections = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--project") dir = args[++i];
            else if (args[i] == "--connection") connections.Add(args[++i]);
            else if (!args[i].StartsWith("--", StringComparison.Ordinal)) models.Add(args[i]);
        var write = args.Contains("--write");
        var check = args.Contains("--check");
        var spec = DbDataBuild.Cli.CommandSpecs.All.First(c => c.Name == "project compile");
        var exit = DbDataBuild.Cli.RenderCommand.Render(spec, dir, [.. models], [.. connections], write, check, content: !write && !check, o, e);
        return (exit, o.ToString(), e.ToString());
    }

    public const string ValidModel = """
        name: marts.fct_orders
        kind:
          incremental_by_unique_key:
            unique_key: [order_id]
        grain: [order_id]
        connections: [sqlserver, fabric]
        columns:
          - name: order_id
            type: BIGINT
            nullable: false
          - name: customer_id
            type: BIGINT
          - name: amount
            type: DECIMAL(14, 2)
        """;

    public static (ModelDefinition? Def, List<Diagnostic> Diags) Load(string yaml, string? expectedName = "marts.fct_orders")
    {
        var diags = new List<Diagnostic>();
        var def = ModelDefinitionLoader.Load(yaml, "models/marts/fct_orders.yml", expectedName, diags);
        return (def, diags);
    }

    public static string NewProjectDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "models", "marts"));
        return dir;
    }

    /// <summary>Hash of every file path and content under a directory, for "nothing was written" assertions.</summary>
    public static string Snapshot(string dir) => string.Join("\n",
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : p));
}
