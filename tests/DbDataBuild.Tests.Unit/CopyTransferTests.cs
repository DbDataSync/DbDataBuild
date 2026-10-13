using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Tests.Unit;

/// <summary>The parts of a copy that need no database: planning the transfer, the conversion of values by declared type, and the gate's bulk statement.</summary>
public class CopyTransferTests
{
    private static readonly ProjectConfig Config = ProjectConfig.Default;

    private static ModelDefinition Copy(string name = "dst.items") =>
        new(name, ModelKinds.Copy, [], null, null, ["id"], null, [new ColumnDefinition("id", "BIGINT", false), new ColumnDefinition("label", "VARCHAR(20)")], [], From: "src.items");

    private static readonly ProjectConfig PostgresConfig = ProjectConfigLoader.Load("string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n", "dbdatabuild.yml", [])!;

    private static PlanInput Input(PlannedModel model, string engine = "sqlserver") => new(
        engine, engine == "postgres" ? PostgresConfig : Config, [model], new Dictionary<string, ObjectShape>(), new HashSet<string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, string>(), new HashSet<string>(),
        new Dictionary<string, IReadOnlyList<RenderedLoad>> { [model.Definition.Name] = [new RenderedLoad("default", true, "-- load script", new string('a', 64), null, [], null)] },
        new Dictionary<string, ResolverOutcome>());

    [Fact]
    public void A_copy_plans_its_table_then_the_transfer_then_the_load_then_the_drop_of_the_staging_table()
    {
        var model = new PlannedModel(Copy(), "SELECT 1", "models/dst/items.yml", "h", [], Origins: [new CopyOrigin("crm", "postgres", "src.items")]);
        var plan = Planner.Plan(Input(model), []);
        Assert.True(plan.Complete);
        Assert.Empty(plan.Blocks);
        Assert.Equal(["create schema dst", "create table dst.items", "copy dst.items from crm (src.items)", "load dst.items (default)", "drop staging table of dst.items"], plan.Steps.Select(s => s.Description));

        var transfer = plan.Steps[2];
        Assert.Equal(StepType.Transfer, transfer.Type);
        Assert.Equal("crm", transfer.Transfer!.Origin);
        Assert.Equal("dbdatabuild.stg_dst__items", transfer.Transfer.Staging);
        Assert.Equal("SELECT \"id\", \"label\" FROM \"src\".\"items\"", transfer.Transfer.ReadText);                  // the origin's own quoting, every column named, in the declared order
        Assert.Equal(["id BIGINT", "label VARCHAR(20)"], transfer.Transfer.Columns.Select(c => $"{c.Name} {c.Type}"));
        Assert.Contains("DROP TABLE IF EXISTS [dbdatabuild].[stg_dst__items];", transfer.Text);                       // the staging table is made from the declared columns, in the destination's dialect
        Assert.Contains("CREATE TABLE [dbdatabuild].[stg_dst__items]", transfer.Text);
        Assert.Contains("[id] bigint NOT NULL", transfer.Text);
        Assert.Equal(StepType.Ddl, plan.Steps[4].Type);
        Assert.Contains("DROP TABLE IF EXISTS [dbdatabuild].[stg_dst__items]", plan.Steps[4].Text);
    }

    [Fact]
    public void A_copy_to_postgres_reads_from_sql_server_in_its_own_quoting()
    {
        var model = new PlannedModel(Copy(), "SELECT 1", "models/dst/items.yml", "h", [], Origins: [new CopyOrigin("erp", "sqlserver", "src.items")]);
        var plan = Planner.Plan(Input(model, "postgres"), []);
        var transfer = plan.Steps.Single(s => s.Type == StepType.Transfer);
        Assert.Equal("SELECT [id], [label] FROM [src].[items]", transfer.Transfer!.ReadText);
        Assert.Contains("CREATE TABLE \"dbdatabuild\".\"stg_dst__items\"", transfer.Text);
    }

    [Fact]
    public void A_copy_from_several_origins_stages_and_loads_each_in_turn_with_its_own_slice_value()
    {
        var def = Copy("dst.orders") with
        {
            Columns = [new ColumnDefinition("id", "BIGINT", false), new ColumnDefinition("store_id", "VARCHAR(10)", false)], Grain = ["id", "store_id"],
            Slice = new CopySlice("store_id", "${origin.store_id}", "VARCHAR(10)"), SliceColumnAdded = true, From = "pos.orders",
        };
        var model = new PlannedModel(def, "SELECT 1", "models/dst/orders.yml", "h", [], Origins: [new CopyOrigin("store_17", "postgres", "pos.orders", "017"), new CopyOrigin("store_18", "postgres", "pos.orders", "018")]);
        var input = Input(model);
        var plan = Planner.Plan(input, []);
        Assert.Empty(plan.Blocks);
        Assert.Equal(["create schema dst", "create table dst.orders", "copy dst.orders from store_17 (pos.orders)", "load dst.orders (default)",
                      "copy dst.orders from store_18 (pos.orders)", "load dst.orders (default)", "drop staging table of dst.orders"], plan.Steps.Select(s => s.Description));
        var transfers = plan.Steps.Where(s => s.Type == StepType.Transfer).ToList();
        Assert.Equal(["store_17", "store_18"], transfers.Select(t => t.Transfer!.Origin));
        Assert.Equal(["017", "018"], transfers.Select(t => t.Transfer!.Slice!.Value));
        Assert.All(transfers, t =>
        {
            Assert.Equal("store_id", t.Transfer!.Slice!.Column);
            Assert.True(t.Transfer.Slice.Added);
            Assert.Equal("SELECT \"id\" FROM \"pos\".\"orders\"", t.Transfer.ReadText);               // the added column is written, not read
            Assert.Equal(["id", "store_id"], t.Transfer.Columns.Select(c => c.Name));                     // but the staging table has it
        });
        Assert.Equal(7, plan.Steps.Select(s => s.Id).Distinct().Count());

        // and the plan file keeps the slice
        var text = PlanDocument.Serialize(new Plan("2026-10-12-00000000", "sqlserver", null, false, "0.1.0", [], [], plan.Steps, []));
        var parsed = PlanDocument.Parse(text, "p.yml", [])!;
        Assert.Equal(new PlanSlice("store_id", "018", true), parsed.Steps.Single(s => s.Transfer?.Origin == "store_18").Transfer!.Slice);
    }

    [Fact]
    public void An_incremental_copy_reads_each_origin_from_its_bound_and_binds_it()
    {
        var def = Copy("dst.orders") with { UniqueKey = ["id"], Watermark = new CopyWatermark("id", null) };
        var model = new PlannedModel(def, "SELECT 1", "models/dst/orders.yml", "h", [], Origins: [new CopyOrigin("crm", "postgres", "src.items", WatermarkValue: "41")]);
        var transfer = Planner.Plan(Input(model), []).Steps.Single(s => s.Type == StepType.Transfer);
        Assert.Equal("SELECT \"id\", \"label\" FROM \"src\".\"items\" WHERE \"id\" >= @watermark", transfer.Transfer!.ReadText);       // the value is bound by the driver, not in the text
        Assert.Equal(new PlanWatermark("id", "BIGINT", "41"), transfer.Transfer.Watermark);
        Assert.DoesNotContain("41", transfer.Transfer.ReadText);

        var first = new PlannedModel(def, "SELECT 1", "models/dst/orders.yml", "h", [], Origins: [new CopyOrigin("crm", "postgres", "src.items")]);      // nothing in the destination yet: everything is read
        var all = Planner.Plan(Input(first), []).Steps.Single(s => s.Type == StepType.Transfer);
        Assert.DoesNotContain("WHERE", all.Transfer!.ReadText);
        Assert.Null(all.Transfer.Watermark);

        var text = PlanDocument.Serialize(new Plan("2026-10-12-00000000", "sqlserver", null, false, "0.1.0", [], [], [transfer], []));
        Assert.Equal(new PlanWatermark("id", "BIGINT", "41"), PlanDocument.Parse(text, "p.yml", [])!.Steps.Single().Transfer!.Watermark);
    }

    [Theory]
    [InlineData("BIGINT", 5, typeof(long))]
    [InlineData("INTEGER", 5L, typeof(int))]
    [InlineData("SMALLINT", 5, typeof(short))]
    [InlineData("TINYINT", (byte)5, typeof(short))]
    [InlineData("DOUBLE", 1.5f, typeof(double))]
    [InlineData("FLOAT", 1.5, typeof(float))]
    [InlineData("BOOLEAN", true, typeof(bool))]
    [InlineData("DECIMAL(18, 3)", 5L, typeof(decimal))]
    [InlineData("VARCHAR(40)", "x", typeof(string))]
    [InlineData("VARCHAR", "x", typeof(string))]
    [InlineData("TIMESTAMP", "2024-02-29T13:14:15", typeof(DateTime))]
    public void Values_are_made_the_type_the_column_declares(string type, object value, Type expected)
    {
        var raw = type == "TIMESTAMP" ? DateTime.Parse((string)value, System.Globalization.CultureInfo.InvariantCulture) : value;
        Assert.IsType(expected, TransferValues.Convert(raw, new TransferColumn("c", type))!);
    }

    [Fact]
    public void Dates_times_and_identifiers_come_out_in_one_form_whichever_driver_they_came_from()
    {
        Assert.Equal(new DateTime(2024, 2, 29), TransferValues.Convert(new DateOnly(2024, 2, 29), new("d", "DATE")));              // Npgsql hands over a DateOnly
        Assert.Equal(new DateTime(2024, 2, 29), TransferValues.Convert(new DateTime(2024, 2, 29, 13, 0, 0), new("d", "DATE")));     // SQL Server a DateTime
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)TransferValues.Convert(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new("t", "TIMESTAMP"))!).Kind);
        Assert.Equal(new TimeSpan(1, 2, 3), TransferValues.Convert(new TimeOnly(1, 2, 3), new("t", "TIME")));
        var id = Guid.NewGuid();
        Assert.Equal(id, TransferValues.Convert(id.ToString(), new("u", "UUID")));
        Assert.Equal(new byte[] { 1, 2 }, TransferValues.Convert(new byte[] { 1, 2 }, new("b", "BLOB")));
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), TransferValues.Convert(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new("z", "TIMESTAMP WITH TIME ZONE")));
        Assert.Equal(12.5m, TransferValues.Convert(12.5, new("p", "DECIMAL(10, 2)")));
        Assert.Null(TransferValues.Convert(null, new("n", "BIGINT")));
        Assert.Null(TransferValues.Convert(DBNull.Value, new("n", "BIGINT")));
    }

    [Theory]
    [InlineData("INTEGER", 3_000_000_000L)]                // does not fit
    [InlineData("SMALLINT", 70_000)]
    [InlineData("BIGINT", "not a number")]
    [InlineData("UUID", "not a guid")]
    [InlineData("BLOB", "text")]
    [InlineData("GEOGRAPHY", "x")]                          // a type a copy cannot carry
    public void A_value_that_does_not_fit_is_an_error_that_names_the_column_and_never_the_value(string type, object value)
    {
        var ex = Assert.Throws<TransferException>(() => TransferValues.Convert(value, new TransferColumn("the_column", type)));
        Assert.Contains("the_column", ex.Message);
        Assert.DoesNotContain(value.ToString()!, ex.Message);
    }

    private sealed class Probe : IWriteExecutor, IStatementLog
    {
        public List<string> Events { get; } = [];
        public long Rows { get; set; }
        public Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) { Events.Add("exec:" + text); return Task.FromResult(0L); }
        public async Task<long> BulkCopyAsync(string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct)
        {
            await foreach (var _ in rows) Rows++;
            Events.Add($"bulk:{schema}.{table}({string.Join(",", columns.Select(c => c.Name))})={Rows}");
            return Rows;
        }
        public void Append(StatementLogEntry e) => Events.Add($"log:{e.Phase}:{e.StepId}:{e.Text}:{e.Outcome}:{string.Join(",", (e.Parameters ?? []).Select(p => p.Name))}");
        public Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct) => Task.FromResult(true);
        public Task UnlockAsync(string resource, bool shared, CancellationToken ct) => Task.CompletedTask;
        public Task RecoverAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<object?[]> Rows(int n) { for (var i = 0; i < n; i++) { await Task.Yield(); yield return [i, "secret value"]; } }

    [Fact]
    public async Task The_gate_logs_a_bulk_copy_before_and_after_with_the_count_and_never_a_value()
    {
        var p = new Probe();
        var gate = MutationGate.ForTesting(p, "apply", StatementKind.Data, p);
        TransferColumn[] columns = [new("id", "BIGINT"), new("note", "VARCHAR")];
        var written = await gate.BulkCopyAsync(GateStatement.BulkCopy("3", "dbdatabuild.stg_x", columns), "dbdatabuild", "stg_x", columns, Rows(3));
        Assert.Equal(3, written);
        Assert.Equal(["log:begin:3:BULK COPY INTO dbdatabuild.stg_x (id, note)::(bulk values)", "bulk:dbdatabuild.stg_x(id,note)=3", "log:end:3::ok rows=3:"], p.Events);
        Assert.DoesNotContain(p.Events, e => e.Contains("secret"));
    }

    [Fact]
    public async Task A_dry_run_logs_the_bulk_copy_and_reads_no_rows_and_a_command_that_may_not_write_data_is_refused()
    {
        var p = new Probe();
        TransferColumn[] columns = [new("id", "BIGINT")];
        var dry = MutationGate.ForTesting(p, "apply", StatementKind.Data, p, dryRun: true);
        Assert.Equal(0, await dry.BulkCopyAsync(GateStatement.BulkCopy("3", "t", columns), "s", "t", columns, Rows(3)));
        Assert.Equal(["log:dry-run:3:BULK COPY INTO t (id)::(bulk values)"], p.Events);

        var tracking = MutationGate.ForTesting(new Probe(), "init", StatementKind.Tracking, p);
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => tracking.BulkCopyAsync(GateStatement.BulkCopy("3", "t", columns), "s", "t", columns, Rows(1)));
        Assert.Equal("DDB-502", ex.Diagnostic.Code);
    }
}
