using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection init` (DESIGN.md 9.1, 12). Effect class: tracking tables only. By default it prints the idempotent init script for review and connects to
/// nothing; `--apply` runs exactly that script on the write login, through the mutation gate, so every statement is in the statement log.
/// </summary>
internal static class InitCommand
{
    public const string StatementLogDir = ".dbdatabuild/statement-log";

    public static int Run(CommandSpec spec, string projectRoot, string? targetArg, bool apply, bool upgrade, TextWriter output, TextWriter error, Func<string, string?> environment)
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(projectRoot, diags);
        foreach (var d in diags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (diags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;

        var connection = CommandTargets.Resolve(config, targetArg, error);
        if (connection == null) return CliApp.ExitUsage;
        var target = connection.Name; var engine = connection.Engine;

        // the tables go where records are kept: a connection that keeps the records of any connection (itself, or others: central tracking), in the schema name each of them names
        var schemas = config.Connections.Keys.Select(c => config.TrackingOf(c).Target).Where(t => t?.Connection == connection.Name).Select(t => t!.SchemaName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (schemas.Count == 0)
        {
            var elsewhere = config.TrackingOf(target).Target;
            error.WriteLine(elsewhere != null
                ? $"`{target}` keeps no records of its own: they go to `{elsewhere.Connection}`. Initialize that one: `{ProductInfo.Cli} {spec.Name} {elsewhere.Connection}`."
                : $"`{target}` keeps no records: no connection has `tracking: {{ connection: {target} }}` (in {ProductInfo.ConfigFile}, for the project or for a connection), so there is nothing to initialize on it.");
            return CliApp.ExitFindings;
        }

        LoginSettings? write = null;
        if (apply)
        {
            var (settings, missing) = LoginSettings.FromEnvironment(connection, Login.Write, environment);
            if (missing != null)
            {
                output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {target}  |  login: none");
                error.Diag(missing);
                return CliApp.ExitFindings;
            }
            write = settings;
        }

        var ddl = TrackingDdl.For(engine);
        if (upgrade && ddl.Unverified) { error.WriteLine($"The upgrade of an older tracking layout has not been verified on {engine}."); return CliApp.ExitUsage; }
        var script = schemas.SelectMany(s => (upgrade ? ddl.UpgradeScript(s, target) : []).Concat(ddl.InitScript(s, ProductInfo.Version))).ToList();
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {target}  |  login: {(write?.Describe() ?? "none (not applying)")}");
        output.WriteLine($"Tracking schema name: {string.Join(", ", schemas)}. Statements: {script.Count}. {(upgrade ? $"This is an upgrade: tables of an older layout get the `connection` column (set to `{target}` on what they hold) and the views are replaced; nothing else is altered or dropped." : "The script only creates what is missing; it never alters or drops.")}");
        if (ddl.Unverified) output.WriteLine($"note: this script has not been run on {target} (no engine was available to verify it).");

        output.Payload("connection", target);
        output.Payload("tracking_schema", string.Join(", ", schemas));
        output.Payload("layout_version", DbDataBuild.State.TrackingSchema.Version);
        output.Payload("unverified", ddl.Unverified);
        output.Payload("applied", apply);
        output.Payload("statements", script.Select(s => new { id = s.Id, description = s.Description, text = s.Text }).ToList());
        if (!apply)
        {
            output.WriteLine();
            output.Write(TrackingDdl.Render(script));
            output.WriteLine($"Review the script above, then run `{ProductInfo.Cli} {spec.Name} {target} --apply` with the write login configured.");
            output.Next($"connection init {target} --apply");
            return CliApp.ExitOk;
        }

        var runId = Guid.NewGuid();
        try
        {
            using var log = new FileStatementLog(Path.Combine(projectRoot, StatementLogDir), spec.Name, runId);
            output.WriteLine($"Statement log: {Path.GetRelativePath(projectRoot, log.Path)}");
            var gate = Task.Run(() => MutationGate.OpenAsync(write!, spec.Name, StatementKind.Tracking, log, runId)).GetAwaiter().GetResult();
            try { foreach (var schema in schemas) Task.Run(() => upgrade ? TrackingStore.UpgradeAsync(gate, engine, schema, target) : TrackingStore.InitAsync(gate, engine, schema)).GetAwaiter().GetResult(); }
            finally { gate.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        catch (GateRefusedException ex)
        {
            error.Diag(ex.Diagnostic);
            return CliApp.ExitFindings;
        }
        output.WriteLine($"Applied {script.Count} statements. The tracking tables are ready.");
        output.Next("connection status", "connection deploy");
        return CliApp.ExitOk;
    }
}
