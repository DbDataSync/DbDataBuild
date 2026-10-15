using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Lowering;
using DbDataBuild.Sql;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rules;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The clock functions DuckDB 2.0 folds into constants while it binds (docs/research/duckdb-2.0/README.md, section 2026-10-15) and the planning connections' volatile replacements. The expectations
/// are written once, as the DuckDB 1.5.6 text, and have to hold on every DuckDB: `scripts/test-duckdb-preview.sh` runs this class against 2.0.
/// </summary>
public class ClockOverridesTests
{
    private static DuckDBConnection Plain()
    {
        var c = new DuckDBConnection("DataSource=:memory:");
        c.Open();
        return c;
    }

    private static object? Scalar(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string Lowered(DuckDBConnection c, string sql)
    {
        Scalar(c, "CREATE TABLE IF NOT EXISTS t (d DATE, ts TIMESTAMP, i INTEGER)");
        return PlanLowerer.Lower((string)Scalar(c, QueryDescriber.PlanStatement(sql))!).Sql;
    }

    /// <summary>The lowered query, planned on a planning connection exactly as the tool does it.</summary>
    private static string LoweredByTheTool(string sql, ClockOverrideMode? mode = null)
    {
        using var c = PlanningConnection.Open(lockDown: false, mode);
        return Lowered(c, sql);
    }

    private static string For(string target, string sql) => TargetRules.Finish(Polyglot.TranspileOne(TargetRules.Apply(sql, target).Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!, target);

    private static (string Time, string Zone) SplitZone(string timeWithZone)
    {
        var at = timeWithZone.LastIndexOfAny(['+', '-']);
        return (timeWithZone[..at], timeWithZone[at..]);
    }

    private static List<string> Rows(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
        return rows;
    }

    [Fact]
    public void The_list_names_every_override_once_with_a_kind_the_registration_knows()
    {
        Assert.Equal(ClockOverrides.All.Count, ClockOverrides.All.Select(o => o.Name).Distinct().Count());
        Assert.All(ClockOverrides.All, o => Assert.Contains(o.Value, new[] { "local_date", "utc_timestamp", "local_time_tz", "local_time", "local_timestamp", "captured_text", "captured_ubigint" }));
        Assert.All(ClockOverrides.All.Where(o => o.Value.StartsWith("captured", StringComparison.Ordinal)), o => Assert.NotNull(o.Capture));
        Assert.Contains(ClockOverrides.All, o => o.Name == "current_date" && o.Returns == "DATE");
        Assert.Contains(ClockOverrides.NotCovered, n => n.Name == "current_setting");
    }

    [Fact]
    public void Every_replacement_has_the_built_ins_type_and_the_real_value()
    {
        using var overridden = PlanningConnection.Open(out var status, lockDown: false, ClockOverrideMode.Always);
        Assert.True(status.Registered, status.Problem);
        using var builtIn = Plain();
        foreach (var o in ClockOverrides.All)
            foreach (var call in o.Keywords.Append(o.Name + "()"))
            {
                Assert.Equal(o.Returns, (string)Scalar(overridden, $"SELECT typeof({call})")!);
                Assert.Equal(Scalar(builtIn, $"SELECT typeof({call})"), Scalar(overridden, $"SELECT typeof({call})"));
            }

        // the values: the built-in's own answer, read a moment apart (a day or minute boundary in between is retried)
        void SameWithin(string expression, double seconds)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var a = Convert.ToDouble(Scalar(overridden, expression), CultureInfo.InvariantCulture);
                var b = Convert.ToDouble(Scalar(builtIn, expression), CultureInfo.InvariantCulture);
                if (Math.Abs(a - b) <= seconds || Math.Abs(a - b) >= 86400 - seconds) return;
            }
            Assert.Fail($"{expression} differs between the replacement and the built-in by more than {seconds} s");
        }
        for (var attempt = 0; ; attempt++)
        {
            if (Equals(Scalar(overridden, "SELECT current_date"), Scalar(builtIn, "SELECT current_date")) && Equals(Scalar(overridden, "SELECT today()"), Scalar(builtIn, "SELECT today()"))) break;
            Assert.True(attempt < 3, "current_date differs from the built-in's");
        }
        SameWithin("SELECT epoch(now())", 10);
        SameWithin("SELECT epoch(get_current_timestamp())", 10);
        SameWithin("SELECT epoch(transaction_timestamp())", 10);
        SameWithin("SELECT epoch(localtimestamp)", 10);
        SameWithin("SELECT datediff('microsecond', TIME '00:00:00', localtime) / 1e6", 10);
        // the time with a zone: the same offset as the built-in writes it, and a time of day within seconds of it
        for (var attempt = 0; ; attempt++)
        {
            var mine = (string)Scalar(overridden, "SELECT CAST(get_current_time() AS VARCHAR)")!;
            var theirs = (string)Scalar(builtIn, "SELECT CAST(get_current_time() AS VARCHAR)")!;
            var (mineTime, mineZone) = SplitZone(mine);                                     // DuckDB drops trailing zeros of the fraction, so the text is not a fixed width
            var (theirTime, theirZone) = SplitZone(theirs);
            var seconds = Math.Abs((TimeOnly.Parse(mineTime, CultureInfo.InvariantCulture) - TimeOnly.Parse(theirTime, CultureInfo.InvariantCulture)).TotalSeconds);
            if (mineZone == theirZone && (seconds < 10 || seconds > 86390)) break;
            Assert.True(attempt < 3, $"get_current_time(): {mine} against {theirs}");
        }
        foreach (var captured in new[] { "version()", "current_schema()", "current_database()" })
            Assert.Equal(Scalar(builtIn, $"SELECT {captured}"), Scalar(overridden, $"SELECT {captured}"));
        Assert.True(Convert.ToUInt64(Scalar(overridden, "SELECT txid_current()"), CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public void A_replacement_is_the_only_function_of_its_name_in_the_catalog()
    {
        using var c = PlanningConnection.Open(lockDown: false, ClockOverrideMode.Always);
        foreach (var o in ClockOverrides.All)
            Assert.Equal(1L, Convert.ToInt64(Scalar(c, $"SELECT count(*) FROM duckdb_functions() WHERE function_name = '{o.Name}' AND function_type = 'scalar'"), CultureInfo.InvariantCulture));
    }

    public static TheoryData<string, string, string, string> NestedCalls => new()
    {
        { "SELECT d FROM t WHERE d > current_date - INTERVAL 7 DAY",
          "SELECT d FROM t WHERE (CAST(d AS TIMESTAMP) > CAST((current_date() - INTERVAL 7 DAY) AS TIMESTAMP))",
          "SELECT d FROM t WHERE (CAST(d AS DATETIME2) > CAST((DATEADD(DAY, -7, CAST(GETDATE() AS DATE))) AS DATETIME2))",
          "SELECT d FROM t WHERE (CAST(d AS TIMESTAMP) > CAST((CURRENT_DATE - INTERVAL '7 DAY') AS TIMESTAMP))" },
        { "SELECT date_trunc('month', current_date) AS m FROM t",
          "SELECT CAST(date_trunc('month', current_date()) AS TIMESTAMP) AS m FROM t",
          "SELECT CAST(DATETRUNC(MONTH, CAST(GETDATE() AS DATE)) AS DATETIME2) AS m FROM t",
          "SELECT CAST(DATE_TRUNC('month', CURRENT_DATE) AS TIMESTAMP) AS m FROM t" },
        { "SELECT current_date + i AS x FROM t",
          "SELECT (CAST(current_date() AS DATE) + CAST(i AS INTEGER)) AS x FROM t",
          "SELECT (CAST(DATEADD(DAY, (i), CAST(GETDATE() AS DATE)) AS DATE)) AS x FROM t",
          "SELECT (CAST(CURRENT_DATE AS DATE) + CAST(i AS INT)) AS x FROM t" },
        { "SELECT now() - INTERVAL 1 HOUR AS x FROM t",
          "SELECT (now() - INTERVAL 1 HOUR) AS x FROM t",
          "SELECT (DATEADD(HOUR, -1, SYSDATETIMEOFFSET())) AS x FROM t",
          "SELECT (CURRENT_TIMESTAMP - INTERVAL '1 HOUR') AS x FROM t" },
        { "SELECT year(current_date) - year(d) AS y FROM t",
          "SELECT (date_part('year', current_date()) - date_part('year', d)) AS y FROM t",
          "SELECT (DATEPART(YEAR, CAST(GETDATE() AS DATE)) - DATEPART(YEAR, d)) AS y FROM t",
          "SELECT (EXTRACT(YEAR FROM CURRENT_DATE) - EXTRACT(YEAR FROM d)) AS y FROM t" },
        { "SELECT date_diff('day', d, current_date) AS n FROM t",
          "SELECT date_diff('day', d, current_date()) AS n FROM t",
          "SELECT DATEDIFF(DAY, d, CAST(GETDATE() AS DATE)) AS n FROM t",
          "SELECT CAST(EXTRACT(epoch FROM CAST(CURRENT_DATE AS TIMESTAMP) - CAST(d AS TIMESTAMP)) / 86400 AS BIGINT) AS n FROM t" },
        { "SELECT CAST(current_timestamp AS DATE) AS x FROM t",
          "SELECT CAST(now() AS DATE) AS x FROM t",
          "SELECT CAST(SYSDATETIMEOFFSET() AS DATE) AS x FROM t",
          "SELECT CAST(CURRENT_TIMESTAMP AS DATE) AS x FROM t" },
        { "SELECT transaction_timestamp() AS x FROM t",
          "SELECT now() AS x FROM t",
          "SELECT SYSDATETIMEOFFSET() AS x FROM t",
          "SELECT CURRENT_TIMESTAMP AS x FROM t" },
        { "SELECT localtime AS x FROM t",
          "SELECT CAST(current_localtimestamp() AS TIME) AS x FROM t",
          "SELECT CAST(GETDATE() AS TIME) AS x FROM t",
          "SELECT CAST(LOCALTIMESTAMP AS TIME) AS x FROM t" },
    };

    [Theory, MemberData(nameof(NestedCalls))]
    public void A_clock_call_inside_another_call_lowers_to_the_targets_function_with_the_text_of_DuckDB_1_5(string sql, string lowered, string sqlServer, string postgres)
    {
        var text = LoweredByTheTool(sql);                                                    // the automatic choice: whatever this DuckDB needs
        Assert.Equal(lowered, text.Replace("\n", " "));
        Assert.Equal(sqlServer, For("sqlserver", text).Replace("\n", " "));
        Assert.Equal(postgres, For("postgres", text).Replace("\n", " "));
        Assert.Equal(text, LoweredByTheTool(sql, ClockOverrideMode.Always));                  // and with the replacements forced on, on any DuckDB
    }

    [Fact]
    public void The_automatic_choice_registers_exactly_when_the_library_folds_the_clock()
    {
        using var c = PlanningConnection.Open(out var status, lockDown: false, ClockOverrideMode.Auto);
        Assert.Equal(PlanningConnection.ClockFolds, status.Folds);
        Assert.Equal(status.Folds, status.Registered);
        Assert.False(status.ClockWouldFreeze, status.Problem);
        using var never = PlanningConnection.Open(out var off, lockDown: false, ClockOverrideMode.Never);
        Assert.False(off.Registered);
        Assert.False(off.ClockWouldFreeze);
    }

    [Fact]
    public void Without_the_replacements_a_folding_DuckDB_freezes_the_day_and_with_them_it_does_not()
    {
        if (!PlanningConnection.ClockFolds) return;                                         // DuckDB 1.x keeps the call either way: nothing to show
        var frozen = LoweredByTheTool("SELECT date_trunc('month', current_date) AS m FROM t", ClockOverrideMode.Never);
        Assert.DoesNotContain("current_date", frozen);                                       // a constant of the day of lowering
        Assert.Contains("current_date()", LoweredByTheTool("SELECT date_trunc('month', current_date) AS m FROM t"));
    }

    [Fact]
    public void The_status_says_when_the_clock_could_freeze()
    {
        Assert.True(new PlanningStatus(true, false, null, ClockOverrideMode.Auto).ClockWouldFreeze);
        Assert.True(new PlanningStatus(true, false, "the library has no scalar function API", ClockOverrideMode.Auto).ClockWouldFreeze);
        Assert.False(new PlanningStatus(true, true, null, ClockOverrideMode.Auto).ClockWouldFreeze);
        Assert.False(new PlanningStatus(false, false, null, ClockOverrideMode.Auto).ClockWouldFreeze);
        Assert.False(new PlanningStatus(true, false, null, ClockOverrideMode.Never).ClockWouldFreeze);
    }

    [Theory]
    [InlineData("SELECT version() AS x FROM t", "version()")]
    [InlineData("SELECT txid_current() AS x FROM t", "txid_current()")]
    [InlineData("SELECT current_schema() AS x FROM t", "current_schema()")]
    [InlineData("SELECT current_database() AS x FROM t", "current_database()")]
    [InlineData("SELECT current_catalog AS x FROM t", "current_database()")]
    [InlineData("SELECT current_query() AS x FROM t", "current_query()")]
    [InlineData("SELECT year(current_date) + length(version()) AS x FROM t", "version()")]
    public void A_function_that_answers_about_the_DuckDB_session_is_refused(string sql, string named)
    {
        using var c = PlanningConnection.Open(lockDown: false, ClockOverrideMode.Always);
        var ex = Assert.Throws<LoweringException>(() => Lowered(c, sql));
        Assert.Contains(named, ex.Message);
        Assert.Contains("session function", ex.Message);
    }

    [Fact]
    public void The_localtime_rewrite_and_transaction_timestamp_agree_with_DuckDB_on_a_plain_connection()
    {
        // the oracle: the lowered DuckDB text run on a DuckDB without any replacement, against the function as written
        using var plain = Plain();
        Assert.Equal(true, Scalar(plain, "SELECT abs(datediff('second', localtime, CAST(current_localtimestamp() AS TIME))) < 5 OR abs(datediff('second', localtime, CAST(current_localtimestamp() AS TIME))) > 86390"));
        Assert.Equal(true, Scalar(plain, "SELECT abs(epoch(now()) - epoch(transaction_timestamp())) < 5"));
    }

    [Fact]
    public void Clock_calls_in_the_lowered_text_give_the_rows_the_query_gives_in_DuckDB()
    {
        // the differential run: the lowered text on a plain DuckDB, its rows against the query as the author wrote it
        using var plain = Plain();
        Scalar(plain, "CREATE TABLE t (d DATE, ts TIMESTAMP, i INTEGER)");
        Scalar(plain, "INSERT INTO t SELECT current_date - CAST(k AS INTEGER), current_date - CAST(k AS INTEGER), CAST(k AS INTEGER) FROM range(0, 15) r(k)");
        foreach (var sql in new[]
        {
            "SELECT i FROM t WHERE d > current_date - INTERVAL 7 DAY ORDER BY i",
            "SELECT i, date_diff('day', d, current_date) AS n FROM t ORDER BY i",
            "SELECT i, year(current_date) - year(d) AS y, current_date - d AS gap FROM t ORDER BY i",
            "SELECT i FROM t WHERE d + 3 >= current_date ORDER BY i",
            "SELECT i FROM t WHERE ts < date_trunc('month', current_date) ORDER BY i",
        })
            Assert.Equal(Rows(plain, sql), Rows(plain, LoweredByTheTool(sql)));
    }

    [Fact]
    public void A_macro_default_that_is_a_clock_function_is_reported_with_its_fix_and_other_defaults_are_left_alone()
    {
        var upstream = new List<DuckTable>();
        DuckPrelude Macro(string text) => new([], [], [text]);
        using (PlanningConnection.UseMode(ClockOverrideMode.Always))
        {
            foreach (var call in new[] { "current_date", "now()", "today() - 1", "current_timestamp", "version()", "localtime" })
            {
                var why = QueryDescriber.CheckPrelude(upstream, Macro($"CREATE MACRO m(a := {call}) AS a"));
                Assert.NotNull(why);
                Assert.StartsWith(QueryDescriber.ClockDefaultCode + ": ", why);
                Assert.Contains("parameter `a`", why);
            }
            Assert.Null(QueryDescriber.CheckPrelude(upstream, Macro("CREATE MACRO m(a := 0, b := DATE '2024-01-01') AS a")));
            Assert.Null(QueryDescriber.CheckPrelude(upstream, Macro("CREATE MACRO m(a := NULL) AS coalesce(a, current_date)")));        // the call in the body is the fix
            var random = QueryDescriber.CheckPrelude(upstream, Macro("CREATE MACRO m(a := random()) AS a"));
            Assert.NotNull(random);
            Assert.DoesNotContain("DDB-325", random);                                       // DuckDB's own refusal, not ours to explain
        }
    }
}
