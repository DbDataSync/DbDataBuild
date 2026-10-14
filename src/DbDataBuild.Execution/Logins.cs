using System.Data.Common;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataBuild.Execution;

public enum Login { Read, Write }

/// <summary>A connection string found in the environment. Never printed whole: <see cref="Describe"/> shows the variable and the user only.</summary>
public sealed class LoginSettings
{
    static LoginSettings() => DriverSettings.Apply();

    /// <summary>The connection this login is for (its name: `warehouse`, or `sqlserver` for a connection named after its engine).</summary>
    public string Connection { get; }

    /// <summary>The driver of that connection (sqlserver, fabric, postgres, duckdb): what decides how it is opened and, for the three engines, the SQL.</summary>
    public string Engine { get; }

    /// <summary>The connection is `readonly`: there is no write login for it (the overload that takes the connection refuses to make one).</summary>
    public bool ReadOnly { get; private init; }

    /// <summary>The SQL dialect of the connection: how a parameter is written and a name quoted when this login is read from as an origin.</summary>
    public DialectProfile Dialect { get; private init; } = DialectProfiles.Ansi;
    public Login Login { get; }
    public string Variable { get; }
    internal string ConnectionString { get; }

    private LoginSettings(string connection, string engine, Login login, string variable, string connectionString)
    {
        Connection = connection; Engine = engine; Login = login; Variable = variable; ConnectionString = connectionString;
        Dialect = DialectProfiles.OfEngine(engine) ?? DialectProfiles.Ansi;
    }

    /// <summary>`DBDATABUILD_SQLSERVER_READ`, `DBDATABUILD_WAREHOUSE_WRITE` and so on: the connection's name, in capitals.</summary>
    public static string VariableName(string connection, Login login) =>
        $"{ProductInfo.Cli.ToUpperInvariant()}_{connection.ToUpperInvariant()}_{login.ToString().ToUpperInvariant()}";

    /// <summary>Reads the login from the environment. There is deliberately no fallback from one login to the other.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(string connection, string engine, Login login, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var variable = VariableName(connection, login);
        var value = env(variable);
        if (string.IsNullOrWhiteSpace(value))
            return (null, new Diagnostic(DiagnosticCatalog.LoginNotConfigured, new($"env:{variable}", 0, 0),
                $"The {login.ToString().ToLowerInvariant()} login for connection `{connection}` is not configured: environment variable `{variable}` is not set."));
        return (new LoginSettings(connection, engine, login, variable, value), null);
    }

    /// <summary>
    /// Reads the login of a connection of the project. A `readonly` connection has no write login: asking for one is DDB-455 and the environment is not looked at. <paramref name="dialect"/> is the profile of the
    /// connection when it is a project's own (the three engines, DuckDB and `ansi` are known without it).
    /// </summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(ConnectionConfig connection, Login login, Func<string, string?>? env = null, DialectProfile? dialect = null)
    {
        if (connection.ReadOnly && login == Login.Write)
            return (null, new Diagnostic(DiagnosticCatalog.ReadOnlyConnectionNotWritable, new($"connection:{connection.Name}", 0, 0),
                $"The connection `{connection.Name}` is `readonly` ({connection.Kind.Describe()}): it has no write login and the tool never writes there."));
        var (settings, error) = FromEnvironment(connection.Name, connection.Engine, login, env);
        if (settings == null) return (null, error);
        var kind = connection.Kind;
        return (settings.With(kind.ReadOnly, dialect ?? connection.Profile ?? DialectProfiles.BuiltIn.GetValueOrDefault(kind.Dialect) ?? DialectProfiles.Ansi), null);
    }

    private LoginSettings With(bool readOnly, DialectProfile dialect) => new(Connection, Engine, Login, Variable, ConnectionString) { ReadOnly = readOnly, Dialect = dialect };

    /// <summary>Reads the read or write login of a connection by name: the project's own connection (with its dialect) when it has one, else the connection named after an engine.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(ProjectConfig project, string connection, Login login, Func<string, string?>? env = null) =>
        project.Connections.TryGetValue(connection, out var declared) ? FromEnvironment(declared, login, env, project.Dialects.GetValueOrDefault(declared.Kind.Dialect))
            : FromEnvironment(connection, project.EngineOf(connection) ?? connection, login, env);

    /// <summary>For a connection named after its engine (`sqlserver`, `postgres`, `fabric`), which needs no declaration.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(string connection, Login login, Func<string, string?>? env = null) =>
        FromEnvironment(connection, connection, login, env);

    /// <summary>For the command header (DESIGN.md 9.1): which variable and which user, never the password or the rest of the string.</summary>
    public string Describe() => $"{(Engine == "duckdb" ? "DuckDB file, read-only" : Engine == "odbc" ? $"ODBC, {User ?? "the driver's own credentials"}" : Engine == "flightsql" ? FlightSqlDescription() : User ?? "integrated/default")} ({Variable})";

    /// <summary>Where the login goes, when it is a network address that is safe to show (`host:port` of a Flight SQL login); null otherwise, and when the value cannot be read.</summary>
    public string? Target
    {
        get
        {
            if (Engine != "flightsql") return null;
            try { return FlightSqlLogin.Parse(ConnectionString).HostAndPort; }
            catch (FlightSqlLoginException) { return null; }
        }
    }

    // the host and port and how the login signs in (a user's name, or the kind of token); the value itself is never shown
    private string FlightSqlDescription()
    {
        try { var login = FlightSqlLogin.Parse(ConnectionString); return $"Flight SQL {login.HostAndPort}, {login.AuthenticationDescription}"; }
        catch (FlightSqlLoginException) { return "Flight SQL (the login value is not a valid Flight SQL URI)"; }
    }

    public string? User
    {
        get
        {
            try
            {
                if (Engine == "duckdb") return null;                              // a file has no user
                if (Engine == "odbc") return OdbcLogin.UserOf(ConnectionString);
                if (Engine == "flightsql") return FlightSqlLogin.UserOf(ConnectionString);
                if (Engine == "postgres") return new NpgsqlConnectionStringBuilder(ConnectionString).Username;
                var b = new SqlConnectionStringBuilder(ConnectionString);
                return string.IsNullOrEmpty(b.UserID) ? null : b.UserID;
            }
            catch (ArgumentException) { return null; }
        }
    }

    /// <summary>Opens the read side of this login as a row source. Internal on purpose: the way to it is <see cref="ReadSession"/>.</summary>
    internal async Task<IRowSource> OpenRowSourceAsync(CancellationToken ct)
    {
        if (Engine == "flightsql")
        {
            if (Login == Login.Write) throw new InvalidOperationException("A Flight SQL connection is only read.");
            var flight = await FlightSqlRowSource.ConnectAsync(ConnectionString, ct);
            flight.Dialect = Dialect;
            return flight;
        }
        var connection = await OpenAsync(ct);
        return Engine switch
        {
            "duckdb" => new DuckDbRowSource(connection, Dialect.Parameters),
            "odbc" => new OdbcRowSource((System.Data.Odbc.OdbcConnection)connection, Dialect.Parameters, ConnectionString, Dialect),
            _ => new AdoRowSource(connection, Dialect.Parameters),
        };
    }

    /// <summary>Opens a connection of the right provider. Internal on purpose: the write side is reached only through <see cref="MutationGate"/>, the read side through <see cref="ReadSession"/>.</summary>
    internal async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        if (Engine == "duckdb") return await OpenDuckDbAsync(ct);
        if (Engine == "odbc") return await OpenOdbcAsync(ct);
        DbConnection c = Engine == "postgres"
            ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                // a read login's session is also read-only at the session level (a second layer; the login's permissions are the real one)
                Options = Login == Login.Read ? "-c default_transaction_read_only=on" : null,
            }.ConnectionString)
            : new SqlConnection(new SqlConnectionStringBuilder(ConnectionString) { ApplicationName = Login == Login.Read ? $"{ProductInfo.Cli}-read" : $"{ProductInfo.Cli}-write" }.ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    /// <summary>
    /// An ODBC origin: the login's value goes to the driver manager as written (unixODBC on Linux and macOS, the system's on Windows) and the vendor's driver it names; the tool ships neither. There is no write side to an ODBC
    /// connection. A missing driver manager or driver surfaces here as an exception that <see cref="DriverFailure"/> turns into a sentence that says which is missing.
    /// </summary>
    private async Task<DbConnection> OpenOdbcAsync(CancellationToken ct)
    {
        if (Login == Login.Write) throw new InvalidOperationException("An ODBC connection is only read.");
        var c = new System.Data.Odbc.OdbcConnection(OdbcLogin.Normalize(ConnectionString)) { ConnectionTimeout = 30 };
        try { await c.OpenAsync(ct); }
        catch { await c.DisposeAsync(); throw; }
        return c;
    }

    /// <summary>
    /// A DuckDB origin: the login's value is the **path of a .duckdb file**, opened read-only (`access_mode=READ_ONLY`: a file that is not there is an error, never created, and nothing can be written to it), or `:memory:`, a
    /// database with no file, for a native query that reads a Parquet or CSV file. There is no write side to a DuckDB connection.
    /// </summary>
    private async Task<DbConnection> OpenDuckDbAsync(CancellationToken ct)
    {
        if (Login == Login.Write) throw new InvalidOperationException("A DuckDB connection is only read.");
        var path = ConnectionString.Trim();
        var builder = new DuckDBConnectionStringBuilder { DataSource = path };
        if (!string.Equals(path, ":memory:", StringComparison.Ordinal)) builder["access_mode"] = "READ_ONLY";
        var c = new DuckDBConnection(builder.ConnectionString);
        try { await c.OpenAsync(ct); }
        catch { await c.DisposeAsync(); throw; }
        return c;
    }
}
