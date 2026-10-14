using System.Data.Odbc;

namespace DbDataBuild.Execution;

/// <summary>
/// What went wrong with a driver, described without the driver's message (which can quote data, a path or a user name): the exception's type and an error number, and for the one failure a person can
/// fix on the machine, a missing ODBC driver manager or a missing ODBC driver, a sentence that says which is missing.
/// </summary>
public static class DriverFailure
{
    /// <summary>` (error 1234)` for a database exception (SQL Server's `Number`, PostgreSQL's `SqlState`, the SQLSTATE of an ODBC driver), else nothing.</summary>
    public static string Number(Exception ex)
    {
        if (ex is OdbcCatalogException catalogFunction) return State(catalogFunction.SqlState) is { } fromFunction ? $" (error {fromFunction})" : "";
        if (ex is OdbcException odbc) return odbc.Errors.Count > 0 && State(odbc.Errors[0].SQLState) is { } state ? $" (error {state})" : "";
        if (ex is not System.Data.Common.DbException) return "";
        foreach (var name in new[] { "Number", "SqlState" })
            if (ex.GetType().GetProperty(name)?.GetValue(ex) is { } value && value.ToString() is { Length: > 0 and <= 12 } text && text != "0") return $" (error {text})";
        return "";
    }

    private static string? State(string? sqlState) => sqlState is { Length: 5 } && sqlState.All(char.IsAsciiLetterOrDigit) ? sqlState : null;

    /// <summary>
    /// The sentence for an ODBC prerequisite that is missing, or null when the exception is something else: the driver manager (unixODBC on Linux and macOS; Windows has one) or the driver or data source the login names.
    /// Only the SQLSTATE and the type are looked at; nothing the driver said is repeated.
    /// </summary>
    public static string? Prerequisite(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DllNotFoundException or EntryPointNotFoundException)
                return "the ODBC driver manager is not installed on this machine (install unixODBC on Linux or macOS; Windows has one built in)";
            if (e is not OdbcException odbc) continue;
            var states = odbc.Errors.Cast<OdbcError>().Select(x => x.SQLState).ToList();
            if (states.Contains("IM002"))
                return "the ODBC driver or data source named in the login was not found (no such driver in odbcinst.ini and no such DSN; the vendor's driver must be installed)";
            // unixODBC reports a driver it cannot open as a warning (01000), and its text is the only thing that tells it from any other: the text is read, never repeated
            if (states.Contains("IM003") || odbc.Errors.Cast<OdbcError>().Any(x => x.SQLState == "01000" && x.Message.Contains("Can't open lib", StringComparison.Ordinal)))
                return "the ODBC driver named in the login is not installed or could not be loaded (it is not listed in odbcinst.ini, or its file is missing, or a library it needs is)";
            if (states.Contains("IM004") || states.Contains("IM014"))
                return "the ODBC driver manager could not use the driver named in the login (the driver does not match the manager, or the driver is not an ODBC driver)";
            if (states.Contains("IM012") || states.Contains("IM010"))
                return "the ODBC login is not a valid connection string or DSN";
        }
        return null;
    }

    /// <summary>The gRPC failure inside an exception, or null: the Flight SQL client wraps a call's RpcException in an InvalidOperationException.</summary>
    public static Grpc.Core.RpcException? Rpc(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException) if (e is Grpc.Core.RpcException rpc) return rpc;
        return null;
    }

    /// <summary>
    /// The sentence for a Flight SQL failure, or null when the exception is something else: a login value that cannot be used or a token that cannot be had (our own sentence), or a gRPC status named by its kind
    /// (unreachable, the TLS handshake, credentials refused, a deadline, a call the server does not offer). The server's message and the transport's text are never repeated: they can quote a user, a host or a value.
    /// </summary>
    public static string? FlightSql(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is FlightSqlLoginException login) return $"{login.Message} (FlightSqlLoginException)";
            if (e is not Grpc.Core.RpcException rpc) continue;
            var status = rpc.StatusCode;
            var tls = false;
            // the transport's failure to set up TLS comes as Unavailable from a call and as Internal from a handshake: it is an AuthenticationException somewhere down the chain either way
            foreach (var start in new[] { rpc.Status.DebugException, rpc.InnerException })
                for (var inner = start; inner != null; inner = inner.InnerException)
                    if (inner is System.Security.Authentication.AuthenticationException) tls = true;
            var sentence = status switch
            {
                _ when tls && status != Grpc.Core.StatusCode.Unauthenticated => "the TLS handshake with the Flight SQL server failed (the certificate is not trusted or does not match the host, or the server does not speak TLS: see ca_file and tls_skip_verify)",
                Grpc.Core.StatusCode.Unavailable => "the Flight SQL server could not be reached (nothing answered at that host and port, or it does not speak TLS where TLS was asked for, or the other way round)",
                Grpc.Core.StatusCode.Unauthenticated => "the Flight SQL server refused the credentials (the token or the user and password)",
                Grpc.Core.StatusCode.PermissionDenied => "the Flight SQL server refused the account permission for that",
                Grpc.Core.StatusCode.DeadlineExceeded => "the Flight SQL server did not answer in time",
                Grpc.Core.StatusCode.Unimplemented => "the Flight SQL server does not offer that call",
                Grpc.Core.StatusCode.Cancelled => "the call to the Flight SQL server was cancelled",
                _ => "the Flight SQL server reported an error",
            };
            return $"{sentence} (RpcException, gRPC {status})";
        }
        return null;
    }

    /// <summary>The failure in words that quote nothing: the missing prerequisite when that is it, a Flight SQL failure named by its kind, else the type and the error number.</summary>
    public static string Describe(Exception ex) => Prerequisite(ex) ?? FlightSql(ex) ?? $"{ex.GetType().Name}{Number(ex)}";
}
