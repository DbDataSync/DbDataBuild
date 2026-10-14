using System.Globalization;

namespace DbDataBuild.State;

/// <summary>How faithfully a native type is described by its logical type.</summary>
public enum SourceTypeFit
{
    /// <summary>The logical type holds exactly the values the native type holds.</summary>
    Exact,
    /// <summary>Every native value fits the logical type, which can hold more (tinyint as SMALLINT, money as DECIMAL(19, 4), char(n) as VARCHAR(n) without the padding).</summary>
    Widened,
    /// <summary>The logical type is close but not the same: a value can lose precision (datetime2(7) keeps 7 fractional digits, TIMESTAMP 6).</summary>
    Lossy,
    /// <summary>There is no logical type; the column cannot be described in a source descriptor.</summary>
    None,
}

/// <param name="LogicalType">The logical type written in a descriptor, or null when there is none.</param>
/// <param name="Reason">Why a type is not exact or has no logical type; empty for an exact mapping.</param>
public sealed record SourceType(string? LogicalType, SourceTypeFit Fit, string Reason)
{
    public bool HasType => LogicalType != null;
}

/// <summary>
/// Native column types, as the catalog reports them (<see cref="ColumnShape"/>), to the logical types written in definitions (the reverse of the target DDL
/// mapping). An enumerated table per target: a type is mapped only when its values are known to fit, and a column with no honest logical type is reported, never guessed.
/// Logical types describe values, not storage: a varchar and an nvarchar column are both VARCHAR(n).
/// </summary>
public static class SourceTypes
{
    public static SourceType From(string target, ColumnShape c) => target == "duckdb" ? DuckDb(c) : target == "postgres" ? Postgres(c) : TSql(c);

    private static SourceType Exact(string logical) => new(logical, SourceTypeFit.Exact, "");
    private static SourceType Widened(string logical, string why) => new(logical, SourceTypeFit.Widened, why);
    private static SourceType Lossy(string logical, string why) => new(logical, SourceTypeFit.Lossy, why);
    private static SourceType No(string why) => new(null, SourceTypeFit.None, why);

    private static string Dec(int? p, int? s) => string.Create(CultureInfo.InvariantCulture, $"DECIMAL({p}, {s ?? 0})");

    /// <summary>Text with a declared length is VARCHAR(n); text with none (varchar(max), text) is unlimited, which is a bare VARCHAR: DuckDB's own spelling, and one the target mappings turn back into the engine's unlimited type.</summary>
    private static SourceType Text(ColumnShape c, bool fixedLength, string nativeName)
    {
        if (c.Length is null or < 1) return Exact("VARCHAR");
        var logical = string.Create(CultureInfo.InvariantCulture, $"VARCHAR({c.Length})");
        return fixedLength ? Widened(logical, $"{nativeName}({c.Length}) is fixed length; trailing padding is not part of the logical type") : Exact(logical);
    }

    private static SourceType AsText(string nativeName) => Lossy("VARCHAR", $"{nativeName} is read as text; a model that selects it casts it explicitly if the engine needs that");

    private static SourceType TSql(ColumnShape c) => c.Type switch
    {
        "bigint" => Exact("BIGINT"),
        "int" => Exact("INTEGER"),
        "smallint" => Exact("SMALLINT"),
        "tinyint" => Widened("SMALLINT", "tinyint is unsigned 0..255; there is no unsigned type that every target can hold"),
        "bit" => Exact("BOOLEAN"),
        "decimal" or "numeric" => c.Precision == null ? No("decimal without a precision") : Exact(Dec(c.Precision, c.Scale)),
        "money" => Widened("DECIMAL(19, 4)", "money is a fixed-point type with 4 decimals"),
        "smallmoney" => Widened("DECIMAL(10, 4)", "smallmoney is a fixed-point type with 4 decimals"),
        "float" => Exact("DOUBLE"),
        "real" => Exact("FLOAT"),
        "date" => Exact("DATE"),
        "datetime2" => c.Scale is > 6 ? Lossy("TIMESTAMP", $"datetime2({c.Scale}) keeps more fractional digits than the 6 of TIMESTAMP") : Exact("TIMESTAMP"),
        "datetime" => Widened("TIMESTAMP", "datetime has 3.33 ms resolution"),
        "smalldatetime" => Widened("TIMESTAMP", "smalldatetime has minute resolution"),
        "time" => c.Scale is > 6 ? Lossy("TIME", $"time({c.Scale}) keeps more fractional digits than the 6 of TIME") : Exact("TIME"),
        "datetimeoffset" => c.Scale is > 6 ? Lossy("TIMESTAMP WITH TIME ZONE", $"datetimeoffset({c.Scale}) keeps more fractional digits than the 6 of the logical type") : Exact("TIMESTAMP WITH TIME ZONE"),
        "uniqueidentifier" => Exact("UUID"),
        "varchar" => Text(c, false, "varchar"),
        "nvarchar" => Text(c, false, "nvarchar"),
        "char" => Text(c, true, "char"),
        "nchar" => Text(c, true, "nchar"),
        "binary" or "varbinary" or "image" => Widened("BLOB", $"{c.Type} is bytes of a declared length; BLOB has none"),
        "text" or "ntext" => Exact("VARCHAR"),
        "xml" => AsText("xml"),
        _ => No($"{c.Type} has no logical type"),
    };

    // DuckDB's own type names are the logical types: most map exactly. An unsigned or 128-bit integer, an interval, a list, a struct, a map and an enum have no logical type.
    private static SourceType DuckDb(ColumnShape c) => c.Type switch
    {
        "bigint" => Exact("BIGINT"),
        "integer" => Exact("INTEGER"),
        "smallint" => Exact("SMALLINT"),
        "tinyint" => Widened("SMALLINT", "tinyint is 8 bits; SMALLINT holds every value of it"),
        "boolean" => Exact("BOOLEAN"),
        "decimal" => c.Precision == null ? No("decimal without a precision") : c.Precision > 38 ? No($"decimal({c.Precision}) is wider than the 38 digits of a logical DECIMAL") : Exact(Dec(c.Precision, c.Scale)),
        "double" => Exact("DOUBLE"),
        "float" or "real" => Exact("FLOAT"),
        "date" => Exact("DATE"),
        "time" => Exact("TIME"),
        "timestamp" => Exact("TIMESTAMP"),
        "timestamp_s" or "timestamp_ms" => Widened("TIMESTAMP", $"{c.Type} has fewer fractional digits than TIMESTAMP"),
        "timestamp_ns" => Lossy("TIMESTAMP", "timestamp_ns keeps nanoseconds; TIMESTAMP keeps microseconds"),
        "timestamp with time zone" => Exact("TIMESTAMP WITH TIME ZONE"),
        "uuid" => Exact("UUID"),
        "varchar" => Exact("VARCHAR"),
        "blob" => Exact("BLOB"),
        "json" => AsText("json"),
        _ => No($"{c.Type} has no logical type"),
    };

    private static SourceType Postgres(ColumnShape c) => c.Type switch
    {
        "bigint" => Exact("BIGINT"),
        "integer" => Exact("INTEGER"),
        "smallint" => Exact("SMALLINT"),
        "boolean" => Exact("BOOLEAN"),
        "numeric" => c.Precision == null ? No("numeric without a precision is unconstrained") : c.Precision > 38 ? No($"numeric({c.Precision}) is wider than the 38 digits of a logical DECIMAL") : Exact(Dec(c.Precision, c.Scale)),
        "real" => Exact("FLOAT"),
        "double precision" => Exact("DOUBLE"),
        "date" => Exact("DATE"),
        "timestamp without time zone" => Exact("TIMESTAMP"),
        "timestamp with time zone" => Exact("TIMESTAMP WITH TIME ZONE"),
        "time without time zone" => Exact("TIME"),
        "uuid" => Exact("UUID"),
        "bytea" => Exact("BLOB"),
        "character varying" => Text(c, false, "varchar"),
        "character" => Text(c, true, "char"),
        "text" => Exact("VARCHAR"),
        "json" or "jsonb" or "xml" => AsText(c.Type),
        _ => No($"{c.Type} has no logical type"),
    };
}
