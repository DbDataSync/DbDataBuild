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
        return (settings.With(kind.ReadOnly, dialect ?? DialectProfiles.BuiltIn.GetValueOrDefault(kind.Dialect) ?? DialectProfiles.Ansi), null);
    }

    private LoginSettings With(bool readOnly, DialectProfile dialect) => new(Connection, Engine, Login, Variable, ConnectionString) { ReadOnly = readOnly, Dialect = dialect };

    /// <summary>For a connection named after its engine (`sqlserver`, `postgres`, `fabric`), which needs no declaration.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(string connection, Login login, Func<string, string?>? env = null) =>
        FromEnvironment(connection, connection, login, env);

    /// <summary>For the command header (DESIGN.md 9.1): which variable and which user, never the password or the rest of the string.</summary>
    public string Describe() => $"{(Engine == "duckdb" ? "DuckDB file, read-only" : User ?? "integrated/default")} ({Variable})";

    public string? User
    {
        get
        {
            try
            {
                if (Engine == "duckdb") return null;                              // a file has no user
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
        var connection = await OpenAsync(ct);
        return Engine == "duckdb" ? new DuckDbRowSource(connection, Dialect.Parameters) : new AdoRowSource(connection, Dialect.Parameters);
    }

    /// <summary>Opens a connection of the right provider. Internal on purpose: the write side is reached only through <see cref="MutationGate"/>, the read side through <see cref="ReadSession"/>.</summary>
    internal async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        if (Engine == "duckdb") return await OpenDuckDbAsync(ct);
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
