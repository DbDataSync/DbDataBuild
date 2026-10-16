using DbDataBuild.Core;
using DbDataBuild.Targets.DuckDb;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Unit;

/// <summary>The interval reader (Core.IntervalParts) against DuckDB's own reading of the same text: DuckDB prints an interval from its three stored numbers, so equal printouts mean equal months, days and microseconds.</summary>
public class IntervalPartsTests
{
    private static string Printed(DuckDBConnection c, string text)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT CAST(CAST('{text}' AS INTERVAL) AS VARCHAR)";
        return (string)cmd.ExecuteScalar()!;
    }

    private static string? PrintedOrNull(DuckDBConnection c, string text)
    {
        try { return Printed(c, text); } catch (DuckDBException) { return null; }
    }

    [Theory]
    [InlineData("1 month 2 days 03:04:05")]
    [InlineData("1 year 2 months")]
    [InlineData("13 months")]
    [InlineData("-1 month -2 days")]
    [InlineData("1 month -2 days")]
    [InlineData("1.5 seconds")]
    [InlineData("0.000001 seconds")]
    [InlineData("1.5 hours")]
    [InlineData("0.5 minutes")]
    [InlineData("1 week")]
    [InlineData("2 weeks 3 days")]
    [InlineData("1 decade")]
    [InlineData("2 centuries")]
    [InlineData("1 millennium")]
    [InlineData("1 quarter")]
    [InlineData("500 milliseconds")]
    [InlineData("1.5 milliseconds")]
    [InlineData("7 microseconds")]
    [InlineData("90 minutes")]
    [InlineData("-03:04:05")]
    [InlineData("03:04")]
    [InlineData("03:04:05.123456")]
    [InlineData("03:04:05.5")]
    [InlineData("1 day -03:04:05")]
    [InlineData("1 DAY")]
    [InlineData("  1 day  ")]
    [InlineData("1 month 1 month")]
    [InlineData("2 days 1 day")]
    [InlineData("0 days")]
    [InlineData("2147483647 days")]
    [InlineData("1000000 hours")]
    [InlineData("-3 microseconds")]
    [InlineData("1 hour 1 minute 1 second")]
    [InlineData("1 year -18 months")]
    [InlineData("1 day 1 week")]
    [InlineData("-1.5 seconds")]
    [InlineData("100:00:00")]
    public void The_reader_gives_the_parts_DuckDB_stores(string text)
    {
        using var c = PlanningConnection.Open(lockDown: false);
        var parts = IntervalParts.TryParse(text);
        Assert.NotNull(parts);
        Assert.Equal(Printed(c, text), Printed(c, parts!.Value.ToText()));
    }

    [Theory]
    [InlineData("1 d")]                    // a short unit
    [InlineData("1d")]
    [InlineData("1 m")]
    [InlineData("1.5 days")]               // a fraction of a day, month or year is spread over smaller units by DuckDB; not reproduced
    [InlineData("1.5 months")]
    [InlineData("1.5 years")]
    [InlineData("1.5 weeks")]
    [InlineData("5")]                      // no unit
    [InlineData("1 day ago")]
    [InlineData("+1 day")]
    [InlineData("1.0000005 seconds")]      // a seventh digit DuckDB cuts off
    [InlineData("1 fortnight")]
    [InlineData("1 day 25:61:61")]
    [InlineData("2147483648 days")]
    [InlineData("")]
    [InlineData("day")]
    [InlineData("1 day,")]
    public void A_text_that_is_not_certain_is_not_read(string text)
    {
        Assert.Null(IntervalParts.TryParse(text));
    }

    [Fact]
    public void Random_texts_are_read_as_DuckDB_reads_them()
    {
        using var c = PlanningConnection.Open(lockDown: false);
        var random = new Random(20261015);
        string[] units = ["microsecond", "microseconds", "millisecond", "milliseconds", "second", "seconds", "minute", "minutes", "hour", "hours", "day", "days", "week", "weeks", "month", "months", "quarter", "year", "years", "decade", "century", "millennium"];
        for (var i = 0; i < 3000; i++)
        {
            var parts = new List<string>();
            for (var k = random.Next(1, 4); k > 0; k--)
            {
                var sign = random.Next(3) == 0 ? "-" : "";
                var unit = units[random.Next(units.Length)];
                var fractional = unit.StartsWith("second") || unit.StartsWith("minute") || unit.StartsWith("hour") || unit.StartsWith("milli");
                var number = random.Next(0, 5000).ToString() + (fractional && random.Next(3) == 0 ? "." + random.Next(0, 1000).ToString("D3") : "");
                parts.Add($"{sign}{number} {unit}");
            }
            if (random.Next(4) == 0) parts.Insert(random.Next(parts.Count + 1), $"{(random.Next(2) == 0 ? "-" : "")}{random.Next(0, 200)}:{random.Next(0, 60):D2}:{random.Next(0, 60):D2}");
            var text = string.Join(" ", parts);
            var duck = PrintedOrNull(c, text);
            var mine = IntervalParts.TryParse(text);
            if (mine == null) continue;                                                               // not read here: nothing to compare (DuckDB may read it or not)
            Assert.True(duck != null, $"DuckDB rejects '{text}' which the reader accepts");
            Assert.True(duck == Printed(c, mine.Value.ToText()), $"'{text}': DuckDB {duck}, reader {mine.Value.ToText()} = {Printed(c, mine.Value.ToText())}");
        }
    }

    [Fact]
    public void Parts_add_and_negate_componentwise()
    {
        var a = new IntervalParts(1, 2, 3);
        Assert.Equal(new IntervalParts(-1, -2, -3), a.Negated);
        Assert.Equal(new IntervalParts(2, 4, 6), a + a);
        Assert.Equal("0 days", default(IntervalParts).ToText());
        Assert.Equal("1 months 2 days 3 microseconds", a.ToText());
    }
}
