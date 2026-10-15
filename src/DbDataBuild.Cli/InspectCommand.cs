using System.Data;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection inspect`: can the tool use this connection, and if not, why. For each login (read and write) whether its variable is set, whether it connects and as whom, what the server is, and
/// what the login may do; whether the tracking tables are there and of this layout; which schema names the models use and whether they exist. Everything is asked with SELECTs, the write login's too, so
/// nothing is created to find out; a failure is reported as the driver's type and error number, never its message.
/// </summary>
internal static class InspectCommand
{
    private sealed record Probe(string Kind, string Variable, bool Set, string? User, bool? Connects, string? Server, string? Problem, bool? CanCreateInDatabase);

    public static int Run(CommandSpec spec, string root, string? targetArg, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, diags);
        foreach (var d in diags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (diags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;
        var connection = CommandTargets.Resolve(config, targetArg, error, readOnlyOk: true);
        if (connection == null) return CliApp.ExitUsage;
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {connection.Name}");
        output.WriteLine($"Connection {connection.Name}: engine {connection.Kind.Describe()}, version {(connection.Version?.ToString() ?? "not set")}.");

        var probes = new List<Probe>();
        var problems = 0;
        Probe? readProbe = null;
        foreach (var kind in new[] { Login.Read, Login.Write })
        {
            var (login, missing) = LoginSettings.FromEnvironment(connection, kind, env);
            var variable = LoginSettings.VariableName(connection.Name, kind);
            if (login == null) { probes.Add(new(kind.ToString().ToLowerInvariant(), variable, false, null, null, null, null, null)); continue; }
            Probe probe;
            try { probe = Task.Run(() => ProbeAsync(login, connection.Engine)).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                probe = new(kind.ToString().ToLowerInvariant(), variable, true, login.User, false, null, (login.Target is { } where ? where + ": " : "") + DriverFailure.Describe(ex), null);
            }
            probes.Add(probe);
            if (kind == Login.Read) readProbe = probe;
        }
        foreach (var p in probes)
        {
            if (!p.Set && p.Kind == "write" && connection.ReadOnly) output.WriteLine($"  {p.Kind + " login",-13} none: the connection is readonly, so no write login is read");
            else if (!p.Set) output.WriteLine($"  {p.Kind + " login",-13} {p.Variable}: not set{(p.Kind == "write" ? " (only a deploy, a refresh and the commands that record need it)" : "")}");
            else if (p.Connects == true)
                output.WriteLine($"  {p.Kind + " login",-13} {p.Variable}: {p.User ?? "default user"} connects; {p.Server}{(p.CanCreateInDatabase is { } c ? (c ? "; may create tables in the database" : "; may NOT create tables in the database") : "")}");
            else output.WriteLine($"  {p.Kind + " login",-13} {p.Variable}: {p.User ?? "default user"} does not connect ({p.Problem})");
        }
        if (probes[0].Set == false) problems++;                                               // the read login is what everything else needs
        problems += probes.Count(p => p.Set && p.Connects != true);

        // the tracking tables, and the schema names the models use
        var tracking = config.TrackingOf(connection.Name);
        string trackingText; string trackingState;
        var trackingConnection = connection.Name;                                             // the records may be kept on another connection (central tracking): that is where `init` has to run
        if (tracking.Target is not { } target) { trackingText = tracking.Explicit ? "none: not tracked, by choice (`tracking: none`)" : "not configured: nothing is tracked (DDB-232)"; trackingState = tracking.Explicit ? "none" : "not_configured"; }
        else
        {
            trackingConnection = target.Connection;
            var (trackLogin, _) = LoginSettings.FromEnvironment(target.Connection, target.Engine, Login.Read, env);
            if (trackLogin == null) { trackingText = $"schema {target.SchemaName} on {target.Connection}: its read login is not set"; trackingState = "unreachable"; problems++; }
            else
            {
                try
                {
                    var status = Task.Run(async () => { await using var read = await ReadSession.OpenAsync(trackLogin); return await TrackingStore.StatusAsync(read, target.Engine, target.SchemaName); }).GetAwaiter().GetResult();
                    trackingState = status.State switch { TrackingState.Ready => "ready", TrackingState.Missing => "missing", _ => "other_layout" };
                    trackingText = $"schema {target.SchemaName} on {target.Connection}: " + status.State switch
                    {
                        TrackingState.Ready => $"ready (layout {status.Version})",
                        TrackingState.Missing => "not there yet",
                        _ => $"layout {(status.Version?.ToString() ?? "unreadable")}, this tool needs {TrackingSchema.Version}",
                    };
                    if (status.State != TrackingState.Ready) problems++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    trackingState = "unreachable"; trackingText = $"schema {target.SchemaName} on {target.Connection}: cannot be read ({ex.GetType().Name}{CliApp.DriverNumber(ex)})"; problems++;
                }
            }
        }
        output.WriteLine($"  {"tracking",-13} {trackingText}");

        var schemas = new List<(string Name, bool? Exists)>();
        if (readProbe is { Connects: true })
        {
            try
            {
                var names = ProjectContext.Load(root).Project.BuiltModels.Where(s => s.Definition.Targets?.Contains(connection.Name) ?? config.DefaultConnections.Contains(connection.Name))
                    .Select(s => DbDataBuild.Targets.Ddl.DdlGenerator.Split(s.Definition.Name).SchemaName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                var (readLogin, _) = LoginSettings.FromEnvironment(connection, Login.Read, env);
                if (readLogin!.Dialect.Catalog == CatalogStyle.None) throw new CatalogUnavailableException(readLogin.Dialect.Name);
                var existing = Task.Run(async () =>
                {
                    await using var read = await ReadSession.OpenAsync(readLogin!);
                    // a Flight SQL server answers with its metadata calls when it has them (then `information_schema`); an ODBC source whose dialect says `catalog: odbc`, with the driver's own catalog functions
                    if (connection.Engine == "flightsql" || (connection.Engine == "odbc" && readLogin.Dialect.Catalog == CatalogStyle.Odbc)) return (await SourceCatalogReader.SchemasAsync(read, connection.Engine)).ToHashSet(StringComparer.Ordinal);
                    var rows = await read.QueryAsync(connection.Engine is "postgres" or "duckdb" or "odbc" ? "SELECT schema_name FROM information_schema.schemata" : "SELECT name FROM sys.schemas");
                    return rows.Select(r => (string)r[0]!).ToHashSet(StringComparer.Ordinal);
                }).GetAwaiter().GetResult();
                schemas.AddRange(names.Select(n => (n, (bool?)existing.Contains(n))));
                output.WriteLine($"  {"schema names",-13} {(schemas.Count == 0 ? "no model declares this connection" : string.Join(", ", schemas.Select(s => $"{s.Name} ({(s.Exists == true ? "exists" : "missing: a deploy creates it")})")))}");
            }
            catch (CatalogUnavailableException ex) { output.WriteLine($"  {"schema names",-13} not read: {ex.Brief}"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { output.WriteLine($"  {"schema names",-13} not read ({DriverFailure.Describe(ex)})"); }
        }

        output.Payload("connection", connection.Name);
        output.Payload("engine", connection.Engine);
        if (connection.Version != null) output.Payload("version", connection.Version);
        output.Payload("logins", probes.Select(p => new { kind = p.Kind, variable = p.Variable, set = p.Set, user = p.User, connects = p.Connects, server = p.Server, problem = p.Problem, can_create_tables = p.CanCreateInDatabase }).ToList());
        output.Payload("tracking", new { state = trackingState, detail = trackingText });
        output.Payload("schema_names", schemas.Select(s => new { name = s.Name, exists = s.Exists }).ToList());
        output.WriteLine(problems == 0 ? "Nothing in the way." : $"{problems} thing(s) in the way.");
        if (problems == 0) output.Next("connection status");
        else if (trackingState == "missing" || trackingState == "other_layout") output.Next(trackingState == "missing" ? $"connection init {trackingConnection} --apply" : $"connection init {trackingConnection} --upgrade --apply");
        return problems == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
    }

    private static async Task<Probe> ProbeAsync(LoginSettings login, string engine)
    {
        await using var session = await ReadSession.OpenForInspectionAsync(login);
        // the dialect's own ping first: it is what says the connection answers (a DuckDB file that is not there, an ODBC driver that is missing, fail here)
        await session.QueryAsync(login.Dialect.Ping);
        // an origin known only by its driver has no version query of its own (the dialect says nothing about one): the driver says what it can
        var server = engine == "odbc" ? session.ServerDescription ?? "ODBC source"
            : engine == "flightsql" ? session.ServerDescription ?? "Flight SQL server"
            : (await session.QueryAsync(engine == "postgres" ? "SELECT version()" : engine == "duckdb" ? "SELECT 'DuckDB ' || version()" : "SELECT @@VERSION"))[0][0]?.ToString()?.Split('\n')[0].Trim();
        if (server != null && server.Length > 90) server = server[..90] + "…";
        bool? create = null;
        if (login.Login == Login.Write)
        {
            var rows = await session.QueryAsync(engine == "postgres"
                ? "SELECT CASE WHEN has_database_privilege(current_user, current_database(), 'CREATE') THEN 1 ELSE 0 END"
                : "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1 THEN 1 ELSE 0 END");
            create = Convert.ToInt32(rows[0][0], System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        return new(login.Login.ToString().ToLowerInvariant(), login.Variable, true, login.User, true, server, null, create);
    }
}
