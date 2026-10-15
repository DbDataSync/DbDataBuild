using DuckDB.NET.Data;

namespace DbDataBuild.Targets.DuckDb;

/// <summary>When the planning connections replace the clock functions (<see cref="ClockOverrides"/>).</summary>
public enum ClockOverrideMode
{
    /// <summary>Only when this DuckDB folds a stable function into a constant (found out once per process by planning `year(current_date)`): DuckDB 1.x is left exactly as it is.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>What <see cref="PlanningConnection.Open"/> found.</summary>
/// <param name="Folds">This DuckDB folds `current_date` and its relatives into constants when a plan is built (2.0).</param>
/// <param name="Registered">The replacements are registered on the connection.</param>
/// <param name="Problem">Why the replacements are needed but not in place (the library lacks the C API, or the registration did not change the plan), or null.</param>
public sealed record PlanningStatus(bool Folds, bool Registered, string? Problem, ClockOverrideMode Mode)
{
    /// <summary>The plan of this connection cannot be trusted to keep the clock: it folds, and nothing replaces it.</summary>
    public bool ClockWouldFreeze => Problem != null || Folds && !Registered && Mode != ClockOverrideMode.Never;
}

/// <summary>
/// The one place that opens the DuckDB connections that plan, describe or lower a query: a fresh in-memory instance, locked down (no extension install or load, no external access), and, where this DuckDB folds
/// the clock into constants, with the clock functions replaced by volatile ones of the same name and type (<see cref="ClockOverrides"/>). Connections that run the user's data or expressions (the sample and test
/// runs, the expression resolvers' sandbox, the DuckDB origin, the metadata views) do not come from here and never get the replacements: an override changes the whole DuckDB instance it is registered on, and an
/// in-memory connection is an instance of its own.
/// </summary>
public static class PlanningConnection
{
    /// <summary>Set to <c>always</c> or <c>never</c> to override the automatic choice (for tests and for finding a problem); anything else is automatic.</summary>
    public const string ModeVariable = "DBDATABUILD_DUCKDB_CLOCK_OVERRIDES";

    private static readonly AsyncLocal<ClockOverrideMode?> Scoped = new();

    /// <summary>The mode for the code that runs inside <see cref="UseMode"/> (tests), otherwise the variable's, otherwise automatic.</summary>
    public static ClockOverrideMode DefaultMode => Scoped.Value ?? Environment.GetEnvironmentVariable(ModeVariable)?.Trim().ToLowerInvariant() switch
    {
        "always" => ClockOverrideMode.Always,
        "never" => ClockOverrideMode.Never,
        _ => ClockOverrideMode.Auto,
    };

    /// <summary>Makes every planning connection opened by the calling flow (this thread and what it awaits) use <paramref name="mode"/> until the result is disposed. For tests: a 1.x DuckDB can be made to behave like 2.0.</summary>
    public static IDisposable UseMode(ClockOverrideMode mode)
    {
        var before = Scoped.Value;
        Scoped.Value = mode;
        return new Restore(() => Scoped.Value = before);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private static readonly Lazy<bool> LibraryFolds = new(ProbeFolds);
    private static readonly object VerifyLock = new();
    private static bool verified;
    private static string? verifyProblem;

    /// <summary>The statement whose plan shows whether the clock is folded: a stable function inside another call.</summary>
    internal const string FoldProbe = "SELECT json_serialize_plan('SELECT year(current_date) AS y', optimize := false)";

    private static bool PlanKeepsCurrentDate(DuckDBConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = FoldProbe;
        return (cmd.ExecuteScalar() as string ?? "").Contains("current_date", StringComparison.Ordinal);
    }

    private static bool ProbeFolds()
    {
        try
        {
            using var probe = new DuckDBConnection("DataSource=:memory:");
            probe.Open();
            return !PlanKeepsCurrentDate(probe);
        }
        catch (DuckDBException) { return true; }                                  // cannot tell: treat the clock as folded, the replacement is harmless
    }

    /// <summary>Whether this DuckDB folds the clock into constants in a bound plan (true for 2.0, false for 1.x).</summary>
    public static bool ClockFolds => LibraryFolds.Value;

    /// <summary>Registers on a throwaway connection and plans once, to learn (once per process) whether the replacement does what it is for.</summary>
    private static string? Verify()
    {
        lock (VerifyLock)
        {
            if (verified) return verifyProblem;
            using var probe = new DuckDBConnection("DataSource=:memory:");
            probe.Open();
            var problem = ClockOverrides.Register(probe);
            if (problem == null && !PlanKeepsCurrentDate(probe)) problem = "the replacements were registered but the plan still folds the clock";
            verified = true;
            return verifyProblem = problem == null ? null : "this DuckDB build cannot keep the clock functions out of the plan, so a lowered query would freeze the day of lowering (" + problem + ")";
        }
    }

    /// <summary>An open, locked-down, in-memory connection for planning. Dispose it.</summary>
    public static DuckDBConnection Open(bool lockDown = true, ClockOverrideMode? mode = null) => Open(out _, lockDown, mode);

    public static DuckDBConnection Open(out PlanningStatus status, bool lockDown = true, ClockOverrideMode? mode = null)
    {
        var chosen = mode ?? DefaultMode;
        var folds = ClockFolds;
        var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        try
        {
            var registered = false;
            string? problem = null;
            if (chosen == ClockOverrideMode.Always || chosen == ClockOverrideMode.Auto && folds)
            {
                problem = chosen == ClockOverrideMode.Auto ? Verify() : null;
                if (problem == null)
                {
                    problem = ClockOverrides.Register(connection);
                    registered = problem == null;
                }
            }
            status = new PlanningStatus(folds, registered, problem, chosen);
            if (lockDown)
                foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false" })       // these cannot be changed back
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = setting;
                    cmd.ExecuteNonQuery();
                }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
