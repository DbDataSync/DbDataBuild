using System.Data;
using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Types;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>An Arrow type with no value the tool can carry (a list, a struct, a map, an interval). The message names the Arrow type, never a value.</summary>
public sealed class ArrowTypeNotCarriedException(string arrowType) : Exception($"the Arrow type {arrowType} has no value a copy can carry")
{
    public string ArrowType { get; } = arrowType;
}

/// <summary>
/// The values and the types of Arrow, in the terms the rest of the tool already has. A value is made into the plain .NET object a driver would have given (a number of its own width, a string, a decimal, a
/// <see cref="DateOnly"/>, a <see cref="DateTime"/>, a <see cref="DateTimeOffset"/>, a <see cref="TimeOnly"/>, bytes), and the copy then converts it by the column's declared logical type like every value from every origin
/// (<see cref="TransferValues"/>). A type is named as `information_schema` names it, so one table of types (the dialect's `types:`) serves a source found through a catalog and one found through Arrow.
/// </summary>
internal static class ArrowConversions
{
    /// <summary>The value at <paramref name="row"/> of <paramref name="array"/>; null for SQL NULL. Throws <see cref="ArrowTypeNotCarriedException"/> for a type that has no scalar value.</summary>
    public static object? Value(IArrowArray array, int row)
    {
        if (array.IsNull(row)) return null;
        return array switch
        {
            Int64Array a => a.GetValue(row),
            Int32Array a => a.GetValue(row),
            Int16Array a => a.GetValue(row),
            Int8Array a => a.GetValue(row),
            UInt64Array a => a.GetValue(row),
            UInt32Array a => a.GetValue(row),
            UInt16Array a => a.GetValue(row),
            UInt8Array a => a.GetValue(row),
            DoubleArray a => a.GetValue(row),
            FloatArray a => a.GetValue(row),
            HalfFloatArray a => (double?)a.GetValue(row),
            BooleanArray a => a.GetValue(row),
            StringArray a => a.GetString(row),
            LargeStringArray a => a.GetString(row),
            StringViewArray a => a.GetString(row),
            BinaryArray a => a.GetBytes(row).ToArray(),
            LargeBinaryArray a => a.GetBytes(row).ToArray(),
            BinaryViewArray a => a.GetBytes(row).ToArray(),
            Decimal128Array a => a.GetValue(row),
            Decimal256Array a => a.GetValue(row),
            Date32Array a => a.GetDateOnly(row),
            Date64Array a => a.GetDateOnly(row),
            TimestampArray a => Timestamp(a, row),
            Time32Array a => a.GetTime(row),
            Time64Array a => a.GetTime(row),
            DurationArray a => a.GetTimeSpan(row),
            FixedSizeBinaryArray a => a.GetBytes(row).ToArray(),
            DictionaryArray a => Value(a.Dictionary, (int)(Index(a.Indices, row) ?? throw new ArrowTypeNotCarriedException("dictionary"))),
            _ => throw new ArrowTypeNotCarriedException(TypeName(array.Data.DataType)),
        };
    }

    private static long? Index(IArrowArray indices, int row) => indices switch
    {
        Int8Array a => a.GetValue(row), Int16Array a => a.GetValue(row), Int32Array a => a.GetValue(row), Int64Array a => a.GetValue(row),
        UInt8Array a => a.GetValue(row), UInt16Array a => a.GetValue(row), UInt32Array a => a.GetValue(row), UInt64Array a => (long?)a.GetValue(row),
        _ => null,
    };

    // a timestamp with a zone is an instant (a DateTimeOffset at UTC); one without is a clock reading, kept as it is. A nanosecond timestamp keeps the 100 ns of a .NET tick.
    private static object? Timestamp(TimestampArray array, int row)
    {
        var instant = array.GetTimestamp(row);
        if (instant == null) return null;
        if (string.IsNullOrEmpty(((TimestampType)array.Data.DataType).Timezone)) return DateTime.SpecifyKind(instant.Value.UtcDateTime, DateTimeKind.Unspecified);
        return instant.Value;                                                                          // (a conditional expression of the two would convert the DateTime to a DateTimeOffset)
    }

    /// <summary>The short name of an Arrow type for a sentence (`list`, `struct`, `interval_month_day_nano`).</summary>
    public static string TypeName(IArrowType type) => type.Name;

    /// <summary>
    /// The column a field of an Arrow schema is, typed as `information_schema` would name it (`bigint`, `character varying`, `decimal` with its precision and scale, `timestamp with time zone`...). A type with no standard
    /// name (a list, a struct) is named by its Arrow name (`list`), which the dialect's `types:` can map and which is otherwise said not to have a logical type.
    /// </summary>
    public static ColumnShape Column(Field field)
    {
        var type = field.DataType is DictionaryType dictionary ? dictionary.ValueType : field.DataType;
        int? precision = null, scale = null;
        string name;
        switch (type)
        {
            case Int64Type or UInt32Type: name = "bigint"; break;
            case Int32Type or UInt16Type: name = "integer"; break;
            case Int16Type or Int8Type or UInt8Type: name = "smallint"; break;
            case BooleanType: name = "boolean"; break;
            case FloatType or HalfFloatType: name = "real"; break;
            case DoubleType: name = "double precision"; break;
            case Decimal128Type d: name = "decimal"; precision = d.Precision; scale = d.Scale; break;
            case Decimal256Type d: name = "decimal"; precision = d.Precision; scale = d.Scale; break;
            case Date32Type or Date64Type: name = "date"; break;
            case TimestampType t: name = string.IsNullOrEmpty(t.Timezone) ? "timestamp" : "timestamp with time zone"; break;
            case Time32Type or Time64Type: name = "time"; break;
            case StringType or LargeStringType or StringViewType: name = "character varying"; break;
            case BinaryType or LargeBinaryType or BinaryViewType or FixedSizeBinaryType: name = "blob"; break;
            default: name = type.Name; break;
        }
        return new(field.Name, name, null, precision, scale, field.IsNullable, null, null);
    }

    public static IReadOnlyList<ColumnShape> Columns(Schema schema) => schema.FieldsList.Select(Column).ToList();

    /// <summary>
    /// The parameters of a prepared statement as the one-row record batch Flight SQL binds, a column per `?` in the order the plan gives them (a placeholder used twice is two columns). A column's Arrow type follows the
    /// parameter's <see cref="DbType"/>; a NULL is a null of that type.
    /// </summary>
    public static RecordBatch ParameterBatch(IReadOnlyList<GateParameter> parameters)
    {
        var fields = new List<Field>();
        var arrays = new List<IArrowArray>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var (array, type) = ParameterArray(parameters[i]);
            fields.Add(new Field($"p{i}", type, true));
            arrays.Add(array);
        }
        return new RecordBatch(new Schema(fields, null), arrays, 1);
    }

    private static System.Globalization.CultureInfo Inv => System.Globalization.CultureInfo.InvariantCulture;

    private static (IArrowArray, IArrowType) ParameterArray(GateParameter p)
    {
        var v = p.Value;
        switch (p.Type)
        {
            case DbType.Int64 or DbType.UInt32 or DbType.UInt64:
            {
                var b = new Int64Array.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToInt64(v, Inv));
                return (b.Build(), Int64Type.Default);
            }
            case DbType.Int32 or DbType.UInt16:
            {
                var b = new Int32Array.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToInt32(v, Inv));
                return (b.Build(), Int32Type.Default);
            }
            case DbType.Int16 or DbType.Byte or DbType.SByte:
            {
                var b = new Int16Array.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToInt16(v, Inv));
                return (b.Build(), Int16Type.Default);
            }
            case DbType.Boolean:
            {
                var b = new BooleanArray.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToBoolean(v, Inv));
                return (b.Build(), BooleanType.Default);
            }
            case DbType.Double:
            {
                var b = new DoubleArray.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToDouble(v, Inv));
                return (b.Build(), DoubleType.Default);
            }
            case DbType.Single:
            {
                var b = new FloatArray.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToSingle(v, Inv));
                return (b.Build(), FloatType.Default);
            }
            case DbType.Decimal or DbType.Currency or DbType.VarNumeric:
            {
                var d = v == null ? 0m : Convert.ToDecimal(v, Inv);
                var scale = (decimal.GetBits(d)[3] >> 16) & 0x7F;
                var type = new Decimal128Type(Math.Max(28, scale + 1), scale);
                var b = new Decimal128Array.Builder(type);
                if (v == null) b.AppendNull(); else b.Append(d);
                return (b.Build(), type);
            }
            case DbType.Date:
            {
                var b = new Date32Array.Builder();
                if (v == null) b.AppendNull(); else b.Append(ToDateTime(v));
                return (b.Build(), Date32Type.Default);
            }
            case DbType.DateTime or DbType.DateTime2 or DbType.DateTimeOffset:
            {
                // a clock reading with no zone: the plan's watermark is compared with a TIMESTAMP column, which has none either
                var type = new TimestampType(TimeUnit.Microsecond, (string?)null);
                var b = new TimestampArray.Builder(type);
                if (v == null) b.AppendNull();
                else b.Append(v is DateTimeOffset o ? o : new DateTimeOffset(DateTime.SpecifyKind(ToDateTime(v), DateTimeKind.Utc)));
                return (b.Build(), type);
            }
            case DbType.Binary:
            {
                var b = new BinaryArray.Builder();
                if (v is byte[] bytes) b.Append(bytes.AsSpan()); else b.AppendNull();
                return (b.Build(), BinaryType.Default);
            }
            default:
            {
                var b = new StringArray.Builder();
                if (v == null) b.AppendNull(); else b.Append(Convert.ToString(v, Inv)!);
                return (b.Build(), StringType.Default);
            }
        }
    }

    private static DateTime ToDateTime(object v) => v switch
    {
        DateTime d => d,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => Convert.ToDateTime(v, System.Globalization.CultureInfo.InvariantCulture),
    };
}
