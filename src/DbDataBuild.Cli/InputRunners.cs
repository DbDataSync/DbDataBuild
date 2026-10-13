using DbDataBuild.Execution;
using DbDataBuild.Planning;

namespace DbDataBuild.Cli;

/// <summary>
/// Runs the SELECT of a `target` or `source` resolver on a read session (the only way a statement reaches a target: the read login, a single SELECT, a timeout, two rows at most so that "exactly one" can be told).
/// A failure is reported by its type and the driver's error number, never by the driver's message, which can quote data.
/// </summary>
internal sealed class SessionInputRunner(ReadSession session) : IInputQueryRunner
{
    public async Task<InputQueryResult> RunAsync(RefreshInputResolver resolver, string text, string? origin, CancellationToken ct)
    {
        try
        {
            var (names, rows) = await session.QueryFirstRowsAsync(text, 2, TargetSnapshotReader.ScanTimeoutSeconds, ct);
            return new(names, rows, null);
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new([], [], $"failed to run ({ex.GetType().Name}{CliApp.DriverNumber(ex)})"); }
    }
}

/// <summary>Runs `source` resolvers at the origin of a copy: one read session per origin, opened when the first resolver needs it and closed with the run.</summary>
internal sealed class OriginInputRunner(Func<string, CancellationToken, Task<ReadSession>> open, string origin) : IInputQueryRunner, IAsyncDisposable
{
    private ReadSession? session;

    public async Task<InputQueryResult> RunAsync(RefreshInputResolver resolver, string text, string? ignored, CancellationToken ct)
    {
        try { session ??= await open(origin, ct); }
        catch (Exception ex) when (ex is not (OperationCanceledException or GateRefusedException)) { return new([], [], $"could not reach the origin `{origin}` ({ex.GetType().Name})"); }
        return await new SessionInputRunner(session).RunAsync(resolver, text, origin, ct);
    }

    public async ValueTask DisposeAsync() { if (session != null) await session.DisposeAsync(); }
}
