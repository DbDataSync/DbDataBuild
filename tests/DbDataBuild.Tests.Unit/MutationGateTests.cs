using System.Data;
using DbDataBuild.Core;
using DbDataBuild.Execution;

namespace DbDataBuild.Tests.Unit;

public class MutationGateTests
{
    /// <summary>Records what happened and in which order, shared with the log so ordering can be asserted.</summary>
    private sealed class Probe : IWriteExecutor, IStatementLog
    {
        public List<string> Events { get; } = [];
        public Exception? Throw { get; set; }
        public bool LogFails { get; set; }
        public Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
        {
            Events.Add("exec:" + text);
            if (Throw != null) throw Throw;
            return Task.FromResult(7L);
        }
        public void Append(StatementLogEntry entry)
        {
            if (LogFails) throw new IOException("disk full");
            Events.Add($"log:{entry.Phase}:{entry.StepId}");
        }
        public bool LockGranted { get; set; } = true;
        public Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct) { Events.Add("lock:" + resource); return Task.FromResult(LockGranted); }
        public Task UnlockAsync(string resource, bool shared, CancellationToken ct) { Events.Add("unlock:" + resource); return Task.CompletedTask; }
        public Task RecoverAsync(CancellationToken ct) { Events.Add("recover"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MutationGate Gate(Probe p, StatementKind permitted = StatementKind.Data | StatementKind.Tracking, bool dry = false) =>
        MutationGate.ForTesting(p, "run", permitted, p, dry);

    [Fact]
    public async Task The_statement_is_logged_before_it_runs_and_the_outcome_after()
    {
        var p = new Probe();
        var rows = await Gate(p).ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1"));
        Assert.Equal(7, rows);
        Assert.Equal(["log:begin:s1", "exec:INSERT 1", "log:end:s1"], p.Events);
    }

    [Fact]
    public async Task A_statement_of_a_kind_the_command_does_not_permit_is_refused_and_not_logged_as_run()
    {
        var p = new Probe();
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => Gate(p).ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Ddl, "DROP TABLE x")));
        Assert.Equal("DDB-502", ex.Diagnostic.Code);
        Assert.Empty(p.Events);
    }

    [Fact]
    public async Task If_the_log_cannot_be_written_the_statement_does_not_run()
    {
        var p = new Probe { LogFails = true };
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => Gate(p).ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1")));
        Assert.Equal("DDB-503", ex.Diagnostic.Code);
        Assert.DoesNotContain(p.Events, e => e.StartsWith("exec:"));
    }

    [Fact]
    public async Task A_failing_statement_is_logged_with_the_error_type_and_never_its_message()
    {
        var log = new MemoryStatementLog();
        var p = new Probe { Throw = new InvalidOperationException("Violation of key for value 'secret-customer-42'") };
        var gate = MutationGate.ForTesting(p, "run", StatementKind.Data, log);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1")));
        var end = log.Entries.Single(e => e.Phase == "end");
        Assert.Equal("failed InvalidOperationException", end.Outcome);
        Assert.DoesNotContain("secret", string.Join(" ", log.Entries.Select(e => e.Outcome + e.Text)));
    }

    [Fact]
    public async Task A_failed_statement_is_followed_by_a_recovery_so_later_tracking_writes_can_run()
    {
        var p = new Probe { Throw = new InvalidOperationException("boom") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Gate(p).ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1")));
        Assert.Equal(["log:begin:s1", "exec:INSERT 1", "log:end:s1", "log:recover:s1:rollback", "recover"], p.Events);
    }

    [Fact]
    public async Task The_application_lock_is_logged_and_a_lock_held_elsewhere_is_reported_not_waited_for()
    {
        var p = new Probe();
        var gate = Gate(p);
        Assert.True(await gate.TryAcquireApplicationLockAsync("dbdatabuild:x"));
        await gate.ReleaseApplicationLockAsync("dbdatabuild:x");
        Assert.Equal(["log:begin:lock", "lock:dbdatabuild:x", "log:end:lock", "log:begin:unlock", "unlock:dbdatabuild:x", "log:end:unlock"], p.Events);

        var busy = new Probe { LockGranted = false };
        Assert.False(await Gate(busy).TryAcquireApplicationLockAsync("dbdatabuild:x"));
    }

    [Fact]
    public async Task Dry_run_takes_no_lock_and_runs_no_recovery()
    {
        var p = new Probe();
        var gate = Gate(p, dry: true);
        Assert.True(await gate.TryAcquireApplicationLockAsync("r"));
        await gate.ReleaseApplicationLockAsync("r");
        Assert.DoesNotContain(p.Events, e => e.StartsWith("lock:") || e.StartsWith("unlock:"));
    }

    [Fact]
    public async Task Dry_run_runs_every_check_and_log_entry_but_never_the_executor()
    {
        var p = new Probe();
        var gate = Gate(p, dry: true);
        Assert.Equal(0, await gate.ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1")));
        Assert.Equal(["log:dry-run:s1"], p.Events);
        await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(GateStatement.FromPlanStep("s2", StatementKind.Ddl, "DROP TABLE x")));
    }

    [Fact]
    public void A_plan_step_can_never_claim_to_be_a_tracking_statement()
    {
        Assert.Throws<ArgumentException>(() => GateStatement.FromPlanStep("s1", StatementKind.Tracking, "INSERT 1"));
        Assert.Throws<ArgumentException>(() => GateStatement.FromPlanStep("s1", (StatementKind)64, "INSERT 1"));
    }

    [Fact]
    public async Task The_log_records_hash_text_parameters_and_ordinal_in_order()
    {
        var log = new MemoryStatementLog();
        var gate = MutationGate.ForTesting(new Probe(), "run", StatementKind.Data, log);
        await gate.ExecuteAsync(GateStatement.FromPlanStep("a", StatementKind.Data, "UPDATE t SET x = @x", [new("x", DbType.Int32, 5)]));
        await gate.ExecuteAsync(GateStatement.FromPlanStep("b", StatementKind.Data, "UPDATE t SET y = 1"));
        Assert.Equal([1, 1, 2, 2], log.Entries.Select(e => e.Ordinal));
        var begin = log.Entries[0];
        Assert.Equal(DbDataBuild.State.Hashing.ScriptHash("UPDATE t SET x = @x"), begin.StatementHash);
        Assert.Equal("UPDATE t SET x = @x", begin.Text);
        Assert.Equal([("x", "5")], begin.Parameters);
        Assert.Null(log.Entries[1].Text);
    }

    [Fact]
    public void File_log_is_durable_per_line()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = Guid.NewGuid();
            using var log = new FileStatementLog(dir, "apply", run);
            log.Append(new StatementLogEntry(run, 1, DateTime.UtcNow, "apply", "begin", "s1", "Data", "h", "SELECT 'é'", [("p", "v")], null));
            var line = ReadWhileOpen(log.Path).Single(); // readable while the log is still open: it was flushed
            Assert.Contains("\"step\":\"s1\"", line);
            Assert.Contains("é", line);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Logins_are_read_from_the_named_variable_with_no_fallback_between_them()
    {
        var env = new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_WRITE"] = "Server=x;User Id=writer;Password=hunter2" };
        var (write, _) = LoginSettings.FromEnvironment("sqlserver", Login.Write, env.GetValueOrDefault);
        Assert.NotNull(write);
        var (read, error) = LoginSettings.FromEnvironment("sqlserver", Login.Read, env.GetValueOrDefault);
        Assert.Null(read);
        Assert.Equal("DDB-501", error!.Code);
        Assert.Contains("DBDATABUILD_SQLSERVER_READ", error.Found);
        Assert.DoesNotContain("hunter2", error.Found);
    }

    [Fact]
    public void The_login_description_names_the_variable_and_user_and_never_the_password()
    {
        var env = new Dictionary<string, string?> { ["DBDATABUILD_POSTGRES_READ"] = "Host=h;Username=reader;Password=hunter2" };
        var (settings, _) = LoginSettings.FromEnvironment("postgres", Login.Read, env.GetValueOrDefault);
        Assert.Equal("reader (DBDATABUILD_POSTGRES_READ)", settings!.Describe());
        var (integrated, _) = LoginSettings.FromEnvironment("sqlserver", Login.Read, new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_READ"] = "Server=x;Integrated Security=true" }.GetValueOrDefault);
        Assert.Equal("integrated/default (DBDATABUILD_SQLSERVER_READ)", integrated!.Describe());
    }

    /// <summary>A reader must allow the writer's open handle (FileShare.ReadWrite); File.ReadAllLines does not, and on Windows that is a sharing violation.</summary>
    private static string[] ReadWhileOpen(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}

