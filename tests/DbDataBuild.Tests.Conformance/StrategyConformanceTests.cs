using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.Rendering;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// DESIGN.md 15.5, load operations: each strategy's rendered script is executed, exactly as committed (only parameters bound), on a real
/// engine, and the result is compared with its DuckDB reference implementation on synthetic data, including a rerun and late-arriving
/// rows. A failure part-way must leave the target unchanged. The strategy ids below are what matrix/strategies.yml cites.
/// </summary>
public class StrategyConformanceTests
{
    public sealed record Case(string Id, string Variant, string Yaml, string Operation, OracleSpec Spec, bool UsesResolver, IReadOnlyList<long> LateIdsPickedUp, DateTime? PoisonTs = null)
    {
        public override string ToString() => $"{Id}/{Variant}";
    }

    private static string Model(string kind, string? loads = null) =>
        $"name: marts.fct_events\nkind: {kind}\n{(kind.Contains("incremental") ? "grain: [event_id]\n" : "")}columns:\n{Dataset.Columns}{loads ?? ""}";

    private static readonly DateTime In = DateTime.Parse("2024-01-04 12:00:00", System.Globalization.CultureInfo.InvariantCulture);

    public static readonly Case[] Cases =
    [
        new("full_replace", "default", Model("full"), "default", new(OracleKind.FullReplace), false, [11, 12]),
        new("delete_insert_by_key", "default", Model("{incremental_by_unique_key: {unique_key: [event_id]}}"), "default", new(OracleKind.DeleteInsertByKey), false, [11, 12]),
        new("merge_by_key", "explicit", Model("full", "loads:\n  m:\n    default: true\n    strategy: merge_by_key\n    key: [event_id]\n"), "m", new(OracleKind.MergeByKey), false, [11, 12]),
        new("delete_insert_by_range", "explicit",
            Model("{incremental_by_time_range: {time_column: event_ts}}", "loads:\n  r:\n    default: true\n    strategy: delete_insert_by_range\n    params: {start: TIMESTAMP, end: TIMESTAMP}\n"),
            "r", new(OracleKind.DeleteInsertByRange, Start: Dataset.RangeStart, End: Dataset.RangeEnd), false, [], In),
        new("watermark_append", "append",
            Model("full", "loads:\n  w:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max}\n"),
            "w", new(OracleKind.WatermarkAppend), true, []),
        new("watermark_append", "lookback",
            Model("full", "loads:\n  w:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, lookback: 2 days}\n"),
            "w", new(OracleKind.WatermarkAppend, Lookback: TimeSpan.FromDays(2)), true, [11]),
    ];

    public static TheoryData<string, string, string> Matrix
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var engine in new[] { "sqlserver", "postgres" })
                foreach (var c in Cases) data.Add(engine, c.Id, c.Variant);
            return data;
        }
    }

    private static Case Find(string id, string variant) => Cases.Single(c => c.Id == id && c.Variant == variant);

    private static readonly SupportMatrix SupportMatrix = MatrixLoader.LoadEmbedded([]);

    private static (string Script, string? Resolver) Render(Engine engine, string yaml, string operation)
    {
        var diags = new List<Diagnostic>();
        var def = ModelDefinitionLoader.Load(yaml, "models/marts/fct_events.yml", null, diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        var result = new LoadRenderer(SupportMatrix, new MatrixLinter(SupportMatrix), ProjectConfig.Default).Render(def!, Dataset.Body, "models/marts/fct_events.sql", [engine.Name]);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Severity.Error).Select(DiagnosticFormatter.Format));
        var script = result.Files.Single(f => f.Path.EndsWith($"/load.{operation}.sql", StringComparison.Ordinal)).Content;
        var resolver = result.Files.SingleOrDefault(f => f.Path.EndsWith($"/load.{operation}.resolve.sql", StringComparison.Ordinal))?.Content;
        return (script, resolver);
    }

    private static async Task<Dictionary<string, object?>> ParamsAsync(Engine engine, Case c, string? resolver, Oracle oracle)
    {
        if (c.Spec.Kind == OracleKind.DeleteInsertByRange) return new() { ["start"] = c.Spec.Start, ["end"] = c.Spec.End };
        if (!c.UsesResolver) return [];
        var value = await engine.ScalarAsync(resolver!);                                           // the committed resolver, run on the engine
        var expected = oracle.Watermark(c.Spec.Lookback);
        Assert.Equal(expected, value is DateTime dt ? dt : null);                                  // and it must agree with the reference
        return new() { ["watermark"] = value };
    }

    /// <summary>A DATE comes back as DateTime from SQL Server and DateOnly from Npgsql.</summary>
    private static DateTime AsDate(object? value) => value switch { DateOnly d => d.ToDateTime(TimeOnly.MinValue), DateTime dt => dt.Date, _ => throw new InvalidCastException($"{value?.GetType()} is not a date") };

    private static async Task SetUp(Engine engine, bool withCheck, IEnumerable<Row> source)
    {
        await engine.StartAsync();
        await Dataset.CreateAsync(engine, withCheck);
        await Dataset.InsertAsync(engine, "staging", "events", source);
        await Dataset.InsertAsync(engine, "marts", "fct_events", Dataset.Target0);
    }

    // ------------------------------------------------------------------------------------------------------------------

    [SkippableTheory, MemberData(nameof(Matrix))]
    public async Task The_rendered_script_matches_the_reference_on_a_first_run_a_rerun_and_late_rows(string engineName, string id, string variant)
    {
        var c = Find(id, variant);
        await using var engine = EngineEnv.Require(engineName);
        await SetUp(engine, withCheck: false, Dataset.Source);
        using var oracle = new Oracle(Dataset.Source, Dataset.Target0);
        var (script, resolver) = Render(engine, c.Yaml, c.Operation);

        // first run
        var parameters = await ParamsAsync(engine, c, resolver, oracle);
        await engine.RunScriptAsync(script, parameters);
        oracle.Apply(c.Spec);
        var first = await Dataset.TargetRowsAsync(engine);
        Assert.Equal(oracle.Target(), first);

        // a rerun changes nothing (and still matches the reference applied again)
        parameters = await ParamsAsync(engine, c, resolver, oracle);
        await engine.RunScriptAsync(script, parameters);
        oracle.Apply(c.Spec);
        var second = await Dataset.TargetRowsAsync(engine);
        Assert.Equal(first, second);
        Assert.Equal(oracle.Target(), second);

        // late-arriving rows: picked up exactly when the strategy says they are
        await Dataset.InsertAsync(engine, "staging", "events", Dataset.Late);
        oracle.AddSource(Dataset.Late);
        parameters = await ParamsAsync(engine, c, resolver, oracle);
        await engine.RunScriptAsync(script, parameters);
        oracle.Apply(c.Spec);
        var third = await Dataset.TargetRowsAsync(engine);
        Assert.Equal(oracle.Target(), third);
        foreach (var late in Dataset.Late)
            Assert.Equal(c.LateIdsPickedUp.Contains(late.Id), third.Any(r => r.StartsWith(late.Id + "|", StringComparison.Ordinal)));
    }

    [SkippableTheory, MemberData(nameof(Matrix))]
    public async Task A_failure_part_way_leaves_the_target_unchanged_and_no_temporary_table_behind(string engineName, string id, string variant)
    {
        var c = Find(id, variant);
        await using var engine = EngineEnv.Require(engineName);
        var poison = Dataset.Poison with { Ts = c.PoisonTs ?? Dataset.Poison.Ts };
        await SetUp(engine, withCheck: true, Dataset.Source.Append(poison));
        using var oracle = new Oracle(Dataset.Source, Dataset.Target0);
        var (script, resolver) = Render(engine, c.Yaml, c.Operation);
        var parameters = await ParamsAsync(engine, c, resolver, oracle);

        await Assert.ThrowsAnyAsync<Exception>(() => engine.RunScriptAsync(script, parameters));
        if (engine.Name == "postgres") await engine.ExecAsync("ROLLBACK");                      // a failed PostgreSQL transaction block stays open until the caller ends it

        using var untouched = new Oracle([], Dataset.Target0);
        Assert.Equal(untouched.Target(), await Dataset.TargetRowsAsync(engine));
        Assert.False(await engine.TableExistsAsync("ddb_stage") || await engine.TableExistsAsync("#ddb_stage"));
    }

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task Range_reload_honors_whatever_range_is_bound_and_the_text_never_changes(string engineName)
    {
        var c = Find("delete_insert_by_range", "explicit");
        await using var engine = EngineEnv.Require(engineName);
        await SetUp(engine, withCheck: false, Dataset.Source);
        using var oracle = new Oracle(Dataset.Source, Dataset.Target0);
        var (script, _) = Render(engine, c.Yaml, c.Operation);

        foreach (var (start, end) in new[]
                 {
                     (Dataset.RangeStart, Dataset.RangeEnd),
                     (DateTime.Parse("2024-01-06 00:00:00"), DateTime.Parse("2024-01-08 00:00:00")),   // a different range through the same committed text
                     (DateTime.Parse("2030-01-01 00:00:00"), DateTime.Parse("2030-01-02 00:00:00")),   // an empty range changes nothing
                 })
        {
            await engine.RunScriptAsync(script, new Dictionary<string, object?> { ["start"] = start, ["end"] = end });
            oracle.Apply(new OracleSpec(OracleKind.DeleteInsertByRange, Start: start, End: end));
            Assert.Equal(oracle.Target(), await Dataset.TargetRowsAsync(engine));
        }
        Assert.DoesNotContain("2024-01-03", script.Split('\n').Where(l => !l.StartsWith("--")).Aggregate("", (a, b) => a + b));   // values are bound, never interpolated
    }

    // ---- resolvers ----

    private static string WatermarkModel(string watermark, string extraColumns = "", string table = "fct_events") =>
        $"name: marts.{table}\nkind: full\ncolumns:\n{Dataset.Columns}{extraColumns}loads:\n  w:\n    default: true\n    strategy: watermark_append\n    watermark: {watermark}\n";

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task Resolvers_return_the_documented_value_for_every_shape(string engineName)
    {
        await using var engine = EngineEnv.Require(engineName);
        await SetUp(engine, withCheck: false, []);

        // timestamp watermark with a lookback, from a non-empty target
        var (_, withLookback) = Render(engine, WatermarkModel("{column: event_ts, resolver: target_max, lookback: 6 hours}"), "w");
        Assert.Equal(DateTime.Parse("2024-01-05 00:00:00") - TimeSpan.FromHours(6), await engine.ScalarAsync(withLookback!));

        // no lookback: plain MAX
        var (_, plain) = Render(engine, WatermarkModel("{column: event_ts, resolver: target_max}"), "w");
        Assert.Equal(DateTime.Parse("2024-01-05 00:00:00"), await engine.ScalarAsync(plain!));

        // an empty target gives NULL (on_null: require_param asks), or the committed literal (on_null: initial)
        await engine.ExecAsync($"DELETE FROM {engine.QuoteIdent("marts")}.{engine.QuoteIdent("fct_events")}");
        Assert.Null(await engine.ScalarAsync(withLookback!));
        var (_, initial) = Render(engine, WatermarkModel("{column: event_ts, resolver: target_max, lookback: 6 hours, on_null: initial, initial: \"2020-01-01 00:00:00\"}"), "w");
        Assert.Equal(DateTime.Parse("2020-01-01 00:00:00"), await engine.ScalarAsync(initial!));
    }

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task Date_and_integer_watermarks_keep_their_types(string engineName)
    {
        await using var engine = EngineEnv.Require(engineName);
        await engine.StartAsync();
        await engine.ExecAsync($"CREATE SCHEMA {engine.QuoteIdent("marts")}");
        await engine.ExecAsync($"CREATE TABLE {engine.QuoteIdent("marts")}.{engine.QuoteIdent("daily")} (d DATE NOT NULL, seq BIGINT NOT NULL)");
        await engine.ExecAsync($"INSERT INTO {engine.QuoteIdent("marts")}.{engine.QuoteIdent("daily")} VALUES ('2024-03-31', 7), ('2024-01-15', 41)");

        string Yaml(string wm) => $"name: marts.daily\nkind: full\ncolumns:\n  - {{name: d, type: DATE, nullable: false}}\n  - {{name: seq, type: BIGINT, nullable: false}}\nloads:\n  w:\n    strategy: watermark_append\n    watermark: {wm}\n";
        string Body(string sql) => sql;
        (string Script, string? Resolver) RenderDaily(string wm)
        {
            var def = ModelDefinitionLoader.Load(Yaml(wm), "models/marts/daily.yml", null, [])!;
            var r = new LoadRenderer(SupportMatrix, new MatrixLinter(SupportMatrix), ProjectConfig.Default).Render(def, Body("SELECT d.d, d.seq FROM staging.daily d"), "models/marts/daily.sql", [engine.Name]);
            Assert.Empty(r.Diagnostics.Where(x => x.Severity == Severity.Error).Select(DiagnosticFormatter.Format));
            return (r.Files.Single(f => f.Path.EndsWith("load.w.sql", StringComparison.Ordinal)).Content, r.Files.Single(f => f.Path.EndsWith("load.w.resolve.sql", StringComparison.Ordinal)).Content);
        }

        // a DATE minus a calendar month or days stays a DATE (PostgreSQL would otherwise widen it to a timestamp)
        var days = await engine.ScalarAsync(RenderDaily("{column: d, resolver: target_max, lookback: 3 days}").Resolver!);
        Assert.Equal(DateTime.Parse("2024-03-28"), AsDate(days));
        var month = await engine.ScalarAsync(RenderDaily("{column: d, resolver: target_max, lookback: 1 month}").Resolver!);
        Assert.Equal(DateTime.Parse("2024-02-29"), AsDate(month));          // 2024-03-31 minus one month: the calendar clamps to Feb 29
        var week = await engine.ScalarAsync(RenderDaily("{column: d, resolver: target_max, lookback: 1 week}").Resolver!);
        Assert.Equal(DateTime.Parse("2024-03-24"), AsDate(week));
        var seq = await engine.ScalarAsync(RenderDaily("{column: seq, resolver: target_max}").Resolver!);
        Assert.Equal(41L, Convert.ToInt64(seq));
        var seqInitialAfterDelete = RenderDaily("{column: seq, resolver: target_max, on_null: initial, initial: \"100\"}").Resolver!;
        await engine.ExecAsync($"DELETE FROM {engine.QuoteIdent("marts")}.{engine.QuoteIdent("daily")}");
        Assert.Equal(100L, Convert.ToInt64(await engine.ScalarAsync(seqInitialAfterDelete)));
    }
}
