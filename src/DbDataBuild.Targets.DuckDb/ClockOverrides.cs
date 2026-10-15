using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace DbDataBuild.Targets.DuckDb;

/// <summary>One built-in function the override replaces (ClockOverrides.json).</summary>
/// <param name="Value">How the replacement computes the answer: <c>local_date</c>, <c>utc_timestamp</c>, <c>local_time_tz</c>, <c>local_time</c>, <c>local_timestamp</c> (the clock), <c>captured_text</c>, <c>captured_ubigint</c> (what <paramref name="Capture"/> answered when the function was registered).</param>
/// <param name="Keywords">The words (`current_timestamp`) DuckDB binds to this function.</param>
public sealed record ClockOverride(string Name, string Returns, string Value, IReadOnlyList<string> Keywords, string? Capture);

/// <summary>A built-in that folds on DuckDB 2.0 and that the override does not cover, and why.</summary>
public sealed record ClockNotCovered(string Name, string Reason);

/// <summary>
/// DuckDB 2.0's binder folds the functions that are stable within a query (`current_date`, `now()` ...) into constants as soon as they stand inside another call, even with the optimizer off and
/// `json_serialize_plan(..., optimize := false)`: `date_trunc('month', current_date)`, `current_date - INTERVAL 7 DAY` and `now() - INTERVAL 1 HOUR` reach the plan as constants of the day of lowering, and the lowered text would
/// freeze that day. DuckDB 1.x keeps the calls. Registering a *volatile* scalar function of the same name through the C API replaces the built-in on that DuckDB instance (the catalog still holds one function of the name; the
/// keyword forms resolve to the replacement), and a volatile function is never folded, so the plan keeps the call.
/// This is a deliberate override of built-in functions. It is safe because it is registered only on the planning connections (<see cref="PlanningConnection"/>: a private in-memory instance each, which never holds user data
/// and answers only DESCRIBE, `json_serialize_plan` and the creation of macros), every replacement has the built-in's name, parameters and return type, and the time kinds return the real time when they run. It is never
/// registered on a connection that executes the user's data or expressions (the sample and test runs, the expression resolvers, the DuckDB origin).
/// The calls are made through the DuckDB.NET bindings, which address the same native library as the rest of the tool (a swapped preview library included).
/// </summary>
public static unsafe class ClockOverrides
{
    private static readonly Lazy<(IReadOnlyList<ClockOverride> Overrides, IReadOnlyList<ClockNotCovered> NotCovered)> Data = new(Load);

    public static IReadOnlyList<ClockOverride> All => Data.Value.Overrides;

    public static IReadOnlyList<ClockNotCovered> NotCovered => Data.Value.NotCovered;

    private static (IReadOnlyList<ClockOverride>, IReadOnlyList<ClockNotCovered>) Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DbDataBuild.Targets.DuckDb.ClockOverrides.json")!;
        using var doc = JsonDocument.Parse(stream);
        var overrides = doc.RootElement.GetProperty("overrides").EnumerateArray().Select(o => new ClockOverride(
            o.GetProperty("name").GetString()!, o.GetProperty("returns").GetString()!, o.GetProperty("value").GetString()!,
            o.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()!).ToList(), o.TryGetProperty("capture", out var c) ? c.GetString() : null)).ToList();
        var notCovered = doc.RootElement.GetProperty("notCovered").EnumerateArray().Select(o => new ClockNotCovered(o.GetProperty("name").GetString()!, o.GetProperty("reason").GetString()!)).ToList();
        return (overrides, notCovered);
    }

    /// <summary>What a registered function needs when it runs: how to compute, the zone, and the captured answer.</summary>
    private sealed class State(string value, TimeZoneInfo zone, string? text, ulong number)
    {
        public readonly string Value = value;
        public readonly TimeZoneInfo Zone = zone;
        public readonly string? Text = text;
        public readonly ulong Number = number;
    }

    private static DuckDBType TypeOf(string returns) => returns switch
    {
        "DATE" => DuckDBType.Date,
        "TIMESTAMP" => DuckDBType.Timestamp,
        "TIMESTAMP WITH TIME ZONE" => DuckDBType.TimestampTz,
        "TIME" => DuckDBType.Time,
        "TIME WITH TIME ZONE" => DuckDBType.TimeTz,
        "VARCHAR" => DuckDBType.Varchar,
        "UBIGINT" => DuckDBType.UnsignedBigInt,
        _ => throw new InvalidOperationException($"the override list names the type {returns}, which the registration does not know"),
    };

    /// <summary>
    /// Registers every function of <see cref="All"/> on the open connection. Returns null when all are registered, otherwise why (an entry point that this DuckDB does not have, a registration it refused): the connection
    /// is then still usable, with whatever was registered before the failure, and the caller decides whether that is enough.
    /// </summary>
    public static string? Register(DuckDBConnection connection)
    {
        try
        {
            var zone = ZoneOf(Scalar(connection, "SELECT current_setting('TimeZone')") as string);
            foreach (var o in All)
            {
                string? text = null;
                ulong number = 0;
                if (o.Capture != null)
                {
                    var captured = Scalar(connection, o.Capture);
                    if (o.Value == "captured_ubigint") number = Convert.ToUInt64(captured, CultureInfo.InvariantCulture);
                    else text = Convert.ToString(captured, CultureInfo.InvariantCulture);
                }
                RegisterOne(connection, o, new State(o.Value, zone, text, number));
            }
            return null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or MissingMethodException or DuckDBException or InvalidOperationException or NotSupportedException)
        {
            return $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }
    }

    private static object? Scalar(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static TimeZoneInfo ZoneOf(string? name)
    {
        if (name != null)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(name); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    private static void RegisterOne(DuckDBConnection connection, ClockOverride o, State state)
    {
        var function = NativeMethods.ScalarFunction.DuckDBCreateScalarFunction();
        try
        {
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetName(function, o.Name);
            using (var type = NativeMethods.LogicalType.DuckDBCreateLogicalType(TypeOf(o.Returns))) NativeMethods.ScalarFunction.DuckDBScalarFunctionSetReturnType(function, type);
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetFunction(function, &Run);
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetVolatile(function);                       // the point: a volatile function is never folded
            var handle = GCHandle.Alloc(state);
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetExtraInfo(function, GCHandle.ToIntPtr(handle), &Destroy);   // DuckDB frees it with the function
            if (!DuckDBStateExtensions.IsSuccess(NativeMethods.ScalarFunction.DuckDBRegisterScalarFunction(connection.NativeConnection, function)))
                throw new InvalidOperationException($"DuckDB refused to register a replacement for {o.Name}");
        }
        finally { NativeMethods.ScalarFunction.DuckDBDestroyScalarFunction(ref function); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroy(nint pointer)
    {
        try { GCHandle.FromIntPtr(pointer).Free(); } catch (InvalidOperationException) { }
    }

    private const long MicrosPerDay = 86_400_000_000L;
    private const int MaxTimeTzOffset = 16 * 60 * 60 - 1;      // dtime_tz_t stores the offset as MAX_OFFSET - seconds east of UTC in the low 24 bits and the time of day above them

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Run(nint info, nint chunk, nint output)
    {
        try
        {
            var state = (State)GCHandle.FromIntPtr(NativeMethods.ScalarFunction.DuckDBScalarFunctionGetExtraInfo(info)).Target!;
            var count = NativeMethods.DataChunks.DuckDBDataChunkGetSize(new DuckDBDataChunk(chunk));
            var now = DateTimeOffset.UtcNow;                                                  // one reading for the whole vector
            var utcMicros = (now.UtcTicks - DateTime.UnixEpoch.Ticks) / 10;
            var offsetSeconds = (long)state.Zone.GetUtcOffset(now).TotalSeconds;
            var localMicros = utcMicros + offsetSeconds * 1_000_000L;
            var timeOfDay = (localMicros % MicrosPerDay + MicrosPerDay) % MicrosPerDay;
            var days = (int)((localMicros - timeOfDay) / MicrosPerDay);
            var data = NativeMethods.Vectors.DuckDBVectorGetData(output);
            switch (state.Value)
            {
                case "local_date": for (ulong i = 0; i < count; i++) ((int*)data)[i] = days; break;
                case "utc_timestamp": for (ulong i = 0; i < count; i++) ((long*)data)[i] = utcMicros; break;
                case "local_timestamp": for (ulong i = 0; i < count; i++) ((long*)data)[i] = localMicros; break;
                case "local_time": for (ulong i = 0; i < count; i++) ((long*)data)[i] = timeOfDay; break;
                case "local_time_tz":
                    var bits = ((ulong)timeOfDay << 24) | (ulong)(MaxTimeTzOffset - offsetSeconds);
                    for (ulong i = 0; i < count; i++) ((ulong*)data)[i] = bits;
                    break;
                case "captured_ubigint": for (ulong i = 0; i < count; i++) ((ulong*)data)[i] = state.Number; break;
                case "captured_text": for (ulong i = 0; i < count; i++) NativeMethods.Vectors.DuckDBVectorAssignStringElement(output, i, state.Text ?? ""); break;
                default: throw new InvalidOperationException($"unknown override kind {state.Value}");
            }
        }
        catch (Exception ex)
        {
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetError(info, ex.Message);
        }
    }
}
