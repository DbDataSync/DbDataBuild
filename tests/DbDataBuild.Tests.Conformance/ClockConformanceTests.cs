using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The clock functions the planning connections keep as calls (docs/research/duckdb-2.0/README.md, section 2026-10-15), on the real engines. A query that uses `current_date`, `now()` and their relatives inside another
/// call is lowered, rendered and run on SQL Server and PostgreSQL; the rows are DuckDB's. The answers cannot depend on which day the test runs, so each query compares a clock call with a date or time derived
/// from the same call or with the probe table's fixed dates, and the rendered text must carry no date literal (a folded clock would be one). On DuckDB 2.0 the lowering only keeps the calls because of the replacements:
/// `scripts/test-duckdb-preview.sh` with TEST_PROJECT=tests/DbDataBuild.Tests.Conformance runs this against it.
/// </summary>
public class ClockConformanceTests
{
    private static readonly string[] Queries =
    [
        "SELECT id, dt < current_date AS v FROM probe",
        "SELECT id, dt + 7 < current_date AS v FROM probe",
        "SELECT id, dt > current_date - INTERVAL 7 DAY AS v FROM probe",
        "SELECT id, (dt + INTERVAL 7 DAY) < (current_date - INTERVAL 1 DAY) AS v FROM probe",
        "SELECT id, date_trunc('month', current_date) <= current_date AS v FROM probe",
        "SELECT id, date_diff('day', dt, current_date) >= 0 AS v FROM probe",
        "SELECT id, CAST(now() AS DATE) - dt >= 0 AS v FROM probe",
        "SELECT id, now() - INTERVAL 1 HOUR < now() AS v FROM probe",
        "SELECT id, transaction_timestamp() <= now() AS v FROM probe",
        "SELECT id, CAST(localtime AS VARCHAR) <> '' AS v FROM probe",
        "SELECT id, year(current_date) >= 2026 AS v FROM probe",
        "SELECT count(*) AS v FROM probe WHERE dt > current_date - INTERVAL 7 DAY",
        "SELECT count(*) AS v FROM probe WHERE dt <= current_date AND ts < now() + INTERVAL 1 DAY",
    ];

    // a date, or the day number of one (20741 is 2026-10-15)
    private static readonly Regex DateLiteral = new(@"\d{4}-\d{2}-\d{2}|\b(19|20|21)\d{3}\b", RegexOptions.CultureInvariant);

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task A_clock_call_inside_another_call_gives_DuckDBs_answer_and_is_not_a_literal_of_the_day_of_lowering(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        await probe.CreateTableAsync();

        var problems = new List<string>();
        foreach (var sql in Queries)
        {
            var expected = probe.OnDuckDb(sql);
            var rendered = probe.Render(sql);
            if (rendered.Sql == null) { problems.Add($"{sql}: not rendered ({rendered.Refused})"); continue; }
            if (DateLiteral.IsMatch(rendered.Sql)) problems.Add($"{sql}: the rendered text carries a literal date or day number: {rendered.Sql}");
            var actual = await probe.OnEngineAsync(rendered.Sql);
            if (actual.Error != null || actual.Rows != expected.Rows) problems.Add($"{sql}: DuckDB {expected.Rows} / {name} {actual.Error ?? actual.Rows}\n  {rendered.Sql}");
        }
        Assert.True(problems.Count == 0, name + ":\n" + string.Join("\n", problems));
    }

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task The_engines_clock_is_read_when_the_query_runs(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        await probe.CreateTableAsync();

        // the date the engine answers is within a day of the host's (the engine runs in UTC, the host in its own zone)
        var date = probe.Render("SELECT current_date AS v FROM probe WHERE id = 1");
        Assert.NotNull(date.Sql);
        Assert.DoesNotMatch(DateLiteral, date.Sql);
        var answer = await probe.OnEngineAsync(date.Sql);
        Assert.Null(answer.Error);
        var day = DateOnly.ParseExact(answer.Rows, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.InRange(Math.Abs(day.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber), 0, 1);

        // the instant the engine answers is the present, to the minute
        var now = probe.Render("SELECT CAST(now() AS TIMESTAMP) AS v FROM probe WHERE id = 1");
        Assert.NotNull(now.Sql);
        Assert.DoesNotMatch(DateLiteral, now.Sql);
        var instant = await probe.OnEngineAsync(now.Sql);
        Assert.Null(instant.Error);
        var parsed = DateTime.ParseExact(instant.Rows, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None);
        Assert.InRange(Math.Abs((parsed - DateTime.UtcNow).TotalMinutes), 0, 90);              // an engine's CAST of a time with a zone keeps its local or its UTC clock: within the offsets of any zone
    }
}
