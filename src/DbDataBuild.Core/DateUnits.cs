namespace DbDataBuild.Core;

/// <summary>The units of date_part, date_trunc and date_diff that the tool translates exactly on SQL Server and PostgreSQL (the table FunctionTemplates in DbDataBuild.Targets holds the translations, tested against DuckDB and both engines). The matrix refuses any other unit with its name.</summary>
public static class DateUnits
{
    public static readonly IReadOnlySet<string> DatePart = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "year", "month", "day", "quarter", "doy", "dow", "isodow", "week", "isoyear", "yearweek", "decade", "century", "millennium", "era", "julian", "epoch", "hour", "minute", "second", "millisecond", "microsecond",
    };

    public static readonly IReadOnlySet<string> Truncation = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "year", "quarter", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond", "decade", "century", "millennium",
    };

    public static readonly IReadOnlySet<string> Difference = Truncation;
}
