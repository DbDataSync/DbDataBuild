using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Tests.Unit;

/// <summary>The `expression` resolver of an input (docs/research/refresh-plans.md, section 8): evaluated by DuckDB in a sandbox, on a frozen clock, in the zone the project says.</summary>
public class InputExpressionTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 23, 30, 15, TimeSpan.Zero);

    private static ExpressionResult Eval(string text, string tz = "UTC", params ExpressionVariable[] vars) => InputExpressions.Evaluate(text, vars, Instant, tz);

    private static string? Text(ExpressionResult r, string type, string tz = "UTC")
    {
        Assert.Null(r.Error);
        var (text, error) = InputExpressions.ToText(type, r.Columns[0], tz);
        Assert.Null(error);
        return text;
    }

    [Fact]
    public void A_bare_expression_is_a_select_and_a_select_without_from_is_used_as_written()
    {
        Assert.Equal("SELECT 1 + 2", InputExpressions.Statement("1 + 2"));
        Assert.Equal("SELECT 1 AS a, 2 AS b", InputExpressions.Statement("  SELECT 1 AS a, 2 AS b ; "));
        Assert.Equal("3", Text(Eval("1 + 2"), "INTEGER"));
        var two = Eval("SELECT current_date - 7 AS start, current_date + 1 AS \"end\"");
        Assert.Null(two.Error);
        Assert.Equal(["start", "end"], two.Columns.Select(c => c.Name));
        Assert.Equal("2026-02-22", InputExpressions.ToText("DATE", two.Columns[0], "UTC").Text);
        Assert.Equal("2026-03-02", InputExpressions.ToText("DATE", two.Columns[1], "UTC").Text);
    }

    [Fact]
    public void The_clock_is_the_runs_instant_in_the_zone_of_the_project_never_the_machines()
    {
        Assert.Equal("2026-03-01", Text(Eval("current_date"), "DATE"));
        Assert.Equal("2026-03-01", Text(Eval("today()"), "DATE"));
        Assert.Equal("2026-03-01 23:30:15", Text(Eval("now()"), "TIMESTAMP"));
        Assert.Equal("2026-03-01 23:30:15", Text(Eval("current_timestamp"), "TIMESTAMP"));
        Assert.Equal("2026-03-01 23:30:15", Text(Eval("localtimestamp"), "TIMESTAMP"));
        // 23:30 UTC is already the next day in Berlin (+1 in March before the change) and still the same day in New York
        Assert.Equal("2026-03-02", Text(Eval("current_date", "Europe/Berlin"), "DATE", "Europe/Berlin"));
        Assert.Equal("2026-03-02 00:30:15", Text(Eval("now()", "Europe/Berlin"), "TIMESTAMP", "Europe/Berlin"));
        Assert.Equal("2026-03-01", Text(Eval("current_date", "America/New_York"), "DATE", "America/New_York"));
        Assert.Equal("2026-03-01 18:30:15", Text(Eval("now()", "America/New_York"), "TIMESTAMP", "America/New_York"));
        Assert.Contains("not known", Eval("current_date", "Mars/Olympus").Error);
        // one frozen instant: two evaluations agree whatever the real time is
        Assert.Equal(Text(Eval("now()"), "TIMESTAMP"), Text(Eval("now()"), "TIMESTAMP"));
    }

    [Fact]
    public void Inputs_and_run_variables_are_the_names_an_expression_may_use()
    {
        var days = new ExpressionVariable("days", "INTEGER", "60");
        Assert.Equal("2025-12-31", Text(Eval("current_date - days", "UTC", days), "DATE"));
        Assert.Equal("2026-03-01 23:30:15", Text(Eval("run_started", "UTC"), "TIMESTAMP"));
        Assert.Equal("hourly", Text(Eval("plan", "UTC", new ExpressionVariable("plan", "VARCHAR", "hourly")), "VARCHAR"));
        Assert.Null(Text(Eval("days", "UTC", new ExpressionVariable("days", "INTEGER", null)), "INTEGER"));
        Assert.Contains("days", InputExpressions.Check("current_date - days").Names);
        Assert.Equal([], InputExpressions.Check("current_date - 7").Names);
        Assert.Equal([], InputExpressions.Check("list_sum(list_transform([1, 2, 3], x -> x * 2))").Names);
        Assert.Contains("nope", Eval("nope + 1").Error is { } e ? e : "");
    }

    [Fact]
    public void An_expression_reads_no_table_and_is_one_select()
    {
        foreach (var bad in new[] { "SELECT * FROM range(3)", "SELECT (SELECT max(a) FROM t)", "SELECT 1 FROM x", "WITH c AS (SELECT 1) SELECT * FROM c", "SELECT 1 UNION SELECT 2", "SELECT * FROM read_csv('/etc/passwd')" })
        {
            var check = InputExpressions.Check(bad);
            Assert.True(check.Error != null, "check: " + bad);
            Assert.True(Eval(bad).Error != null, "eval: " + bad);
        }
        foreach (var bad in new[] { "COPY (SELECT 1) TO 'x.csv'", "CREATE TABLE t(a INT)", "1 +", "SELECT 1; SELECT 2", "a.b + 1" })
            Assert.True(InputExpressions.Check(bad).Error != null, bad);
        Assert.Null(InputExpressions.Check("(SELECT 1) + 1").Error);                              // a subquery that reads nothing is just a calculation
    }

    [Fact]
    public void The_sandbox_cannot_reach_files_or_the_network_or_change_its_configuration()
    {
        Assert.NotNull(Eval("SELECT * FROM read_text('/etc/hostname')").Error);
        Assert.NotNull(Eval("length(read_blob('/etc/hostname'))").Error);
        // a statement that is not a SELECT never runs (SET, PRAGMA, ATTACH)
        Assert.NotNull(InputExpressions.Check("SET enable_external_access = true").Error);
        Assert.NotNull(Eval("SET enable_external_access = true").Error);
        // a command is wrapped in a SELECT like any text, so it is read as a calculation and never executed
        var file = Path.Combine(Path.GetTempPath(), "ddb-sandbox-" + Guid.NewGuid().ToString("N") + ".db");
        _ = Eval($"ATTACH '{file}'");
        _ = Eval($"COPY (SELECT 1) TO '{file}.csv'");
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(file + ".csv"));
    }

    [Fact]
    public void More_than_one_row_or_a_value_that_does_not_fit_is_an_error_that_says_so_never_a_silent_coercion()
    {
        Assert.Contains("more than one row", Eval("SELECT unnest([1, 2]) AS a").Error ?? "");
        (string? Text, string? Error) To(string type, string expr, string tz = "UTC") => InputExpressions.ToText(type, Eval(expr).Columns[0], tz);
        Assert.Contains("fraction", To("INTEGER", "7 / 2.0 + 0.5 + 0.25").Error);
        Assert.Equal("4", To("INTEGER", "8 / 2.0").Text);                                            // a whole number is a whole number whatever DuckDB called it
        Assert.Contains("time of day", To("DATE", "now()").Error);
        Assert.Equal("2026-03-01", To("DATE", "date_trunc('day', now())").Text);
        Assert.Equal("2026-03-01 00:00:00", To("TIMESTAMP", "current_date").Text);                    // a date is a midnight, losing nothing
        Assert.Contains("does not fit a SMALLINT", To("SMALLINT", "70000").Error);
        Assert.Contains("not text", To("VARCHAR", "42").Error);
        Assert.Contains("not a date", To("DATE", "'tomorrow'").Error);
        Assert.Equal("2026-01-31", To("DATE", "'2026-01-31'").Text);
        Assert.Contains("not a whole number", To("INTEGER", "true").Error);
        Assert.Null(To("DATE", "NULL::DATE").Text);
    }
}
