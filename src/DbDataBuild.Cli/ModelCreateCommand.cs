using System.Text;
using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project model create <schema_name.object_name>`: a model to start from, in the project's own layout: a query that runs (it returns the one column the definition declares) and the definition
/// for it, so the project still compiles. The query is the placeholder to replace; `project model update` brings the definition back in line with whatever it becomes. Only files are written.
/// </summary>
internal static partial class ModelCreateCommand
{
    public static readonly IReadOnlyList<string> Kinds = [ModelKinds.View, ModelKinds.Full, ModelKinds.IncrementalByUniqueKey, ModelKinds.IncrementalByTimeRange];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\.[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ModelName();

    public static int Run(CommandSpec spec, string root, string name, string kind, string[] connections, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (!ModelName().IsMatch(name)) { error.WriteLine($"`{name}` is not a model name: it is schema_name.object_name, letters, digits and underscores (for example marts.fct_orders)."); return CliApp.ExitUsage; }
        if (!Kinds.Contains(kind)) { error.WriteLine($"--kind is one of {string.Join(", ", Kinds)}. A copy, a native model and a mapped model are written by hand (`{ProductInfo.Cli} project import` writes the mapped ones)."); return CliApp.ExitUsage; }

        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, diags);
        foreach (var d in diags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (diags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;
        var unknown = connections.FirstOrDefault(c => !config.Connections.ContainsKey(c));
        if (unknown != null) { error.WriteLine($"Unknown connection `{unknown}`. One of: {string.Join(", ", config.Connections.Keys.Order(StringComparer.Ordinal))}."); return CliApp.ExitUsage; }

        var (schema, obj) = (name[..name.IndexOf('.')], name[(name.IndexOf('.') + 1)..]);
        var stem = config.Layout == ModelLayout.Dotted ? $"{ProjectValidator.ModelsDir}/{name}" : $"{ProjectValidator.ModelsDir}/{schema}/{obj}";
        var (yml, sql) = ($"{stem}.yml", $"{stem}.sql");
        foreach (var file in new[] { yml, sql })
            if (File.Exists(Path.Combine(root, file))) { error.WriteLine($"`{file}` already exists: `{name}` is a model of this project (or its file is in the way). Nothing was written."); return CliApp.ExitFindings; }

        var table = kind != ModelKinds.View;
        var timeRange = kind == ModelKinds.IncrementalByTimeRange;
        var kindText = kind switch
        {
            ModelKinds.IncrementalByUniqueKey => "kind: {incremental_by_unique_key: {unique_key: [id]}}",
            ModelKinds.IncrementalByTimeRange => "kind: {incremental_by_time_range: {time_column: event_ts}}",
            _ => $"kind: {kind}",
        };
        var def = new StringBuilder();
        def.AppendLineLf($"name: {name}");
        def.AppendLineLf(kindText);
        if (table) def.AppendLineLf("grain: [id]");
        if (connections.Length > 0) def.AppendLineLf($"connections: [{string.Join(", ", connections)}]");
        def.AppendLineLf("columns:");
        def.AppendLineLf("  - {name: id, type: BIGINT, nullable: false}");
        if (timeRange) def.AppendLineLf("  - {name: event_ts, type: TIMESTAMP, nullable: false}");
        if (kind == ModelKinds.IncrementalByUniqueKey) { def.AppendLineLf("indexes:"); def.AppendLineLf($"  - {{name: ux_{obj}_id, columns: [id], unique: true}}"); }
        var query = new StringBuilder();
        query.AppendLineLf("-- Replace this with the model's query, in DuckDB's dialect. Then `" + $"{ProductInfo.Cli} project model update` brings the definition's columns in line with it.");
        query.AppendLineLf(timeRange ? "SELECT CAST(1 AS BIGINT) AS id, TIMESTAMP '2000-01-01 00:00:00' AS event_ts" : "SELECT CAST(1 AS BIGINT) AS id");

        foreach (var (file, text) in new[] { (yml, def.ToString()), (sql, query.ToString()) })
        {
            var path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
            output.WriteLine($"wrote {file}");
        }
        output.Payload("model", name);
        output.Payload("kind", kind);
        output.Payload("files", new[] { yml, sql });
        output.WriteLine($"Created {name} ({kind}). The query is a placeholder; replace it, then update the definition.");
        output.Next("project model update " + sql, "project compile");
        return CliApp.ExitOk;
    }
}
