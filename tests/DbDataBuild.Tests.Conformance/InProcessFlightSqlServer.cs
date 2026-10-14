using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using Arrow.Flight.Protocol.Sql;
using DbDataBuild.Execution;
using DuckDB.NET.Data;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// A small Flight SQL server for the tests, in this process and on a loopback port, with an in-memory DuckDB behind it: it needs no container. It does what a proxy in front of a database does for the tool: it answers
/// plain and prepared statements (parameters bound **typed**, as Arrow values, and recorded so a test can say what arrived and in which order), checks a bearer token, trades a user and password for one in the basic
/// handshake, and offers **no** metadata calls (they answer "unimplemented"), so the catalog is read through `information_schema`. It exists beside the real server of the other tests (voltrondata/sqlflite, which binds every
/// parameter as text) because the incremental refresh needs a typed watermark.
/// </summary>
public sealed class InProcessFlightSqlServer : IAsyncDisposable
{
    public const string User = "reader";
    public const string Password = "p;w=d";                  // a value with `;` and `=` in it, so the login's braces are used
    public const string Token = "static-test-token.abc123";

    private readonly WebApplication app;
    private readonly DuckDBConnection db;
    private readonly ServerState state;

    /// <summary>What the server was asked, for a test to read.</summary>
    public sealed class ServerState
    {
        public object Gate { get; } = new();
        public required DuckDBConnection Db { get; init; }
        public ConcurrentDictionary<string, string> Issued { get; } = new();
        public ConcurrentDictionary<string, string> Statements { get; } = new();
        public ConcurrentDictionary<string, RecordBatch?> Parameters { get; } = new();
        public ConcurrentDictionary<string, RecordBatch> Results { get; } = new();
        public List<string> Texts { get; } = [];
        public List<ParameterLog> Bound { get; } = [];
        public List<string> Handshakes { get; } = [];
        public List<string> Actions { get; } = [];
        public string? RejectedToken { get; set; }

        /// <summary>What the server says to GetSqlInfo, by the protocol's id (a string, a bool or a number); null: the call is not offered (the default, like the other metadata calls).</summary>
        public Dictionary<uint, object>? SqlInfo { get; set; }
        public bool RequireAuth { get; set; } = true;
        public int OpenPrepared;
        public int MostConcurrent;
        public int Concurrent;
    }

    /// <summary>The parameters one execution got: the SQL, each parameter's Arrow type name and its value, in column order.</summary>
    public sealed record ParameterLog(string Sql, IReadOnlyList<string> Types, IReadOnlyList<object?> Values);

    private InProcessFlightSqlServer(WebApplication app, DuckDBConnection db, ServerState state, int port) { this.app = app; this.db = db; this.state = state; Port = port; }

    public int Port { get; }
    public IReadOnlyList<string> Texts { get { lock (state.Gate) return state.Texts.ToList(); } }
    public IReadOnlyList<ParameterLog> Bound { get { lock (state.Gate) return state.Bound.ToList(); } }
    public IReadOnlyList<string> Actions { get { lock (state.Gate) return state.Actions.ToList(); } }
    public IReadOnlyList<string> Handshakes { get { lock (state.Gate) return state.Handshakes.ToList(); } }
    /// <summary>Says what the server answers to GetSqlInfo (null: it does not implement the call). Ids: 0 name, 1 version, 3 read-only, 503 identifier case, 504 identifier quote, 505 quoted identifier case.</summary>
    public void SayAboutItself(Dictionary<uint, object>? facts) => state.SqlInfo = facts;
    public int OpenPrepared => state.OpenPrepared;
    public int MostConcurrent => state.MostConcurrent;
    public void ClearLog() { lock (state.Gate) { state.Texts.Clear(); state.Bound.Clear(); state.Handshakes.Clear(); } }

    /// <summary>The login value: bearer token, user and password, or none, over plain gRPC on the server's port.</summary>
    public string Login(string credentials = "token") => $"grpc://127.0.0.1:{Port}" + credentials switch
    {
        "token" => $";token={Token}",
        "password" => $";user={User};password={{{Password}}}",
        "none" => "",
        _ => ";" + credentials,
    };

    public static async Task<InProcessFlightSqlServer> StartAsync()
    {
        var db = new DuckDBConnection("Data Source=:memory:");
        db.Open();
        var state = new ServerState { Db = db };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddSingleton(state);
        builder.Services.AddGrpc().AddFlightServer<DuckFlightSqlServer>();
        var app = builder.Build();
        app.MapFlightEndpoint();
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        return new InProcessFlightSqlServer(app, db, state, port);
    }

    /// <summary>Runs a statement on the DuckDB behind the server (the test's way to put data there).</summary>
    public void Exec(params string[] statements)
    {
        lock (state.Gate)
            foreach (var sql in statements)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        db.Dispose();
    }
}

/// <summary>The Flight SQL protocol over DuckDB, for <see cref="InProcessFlightSqlServer"/>. One instance per call (the framework makes it); everything shared is in <see cref="InProcessFlightSqlServer.ServerState"/>.</summary>
internal sealed class DuckFlightSqlServer(InProcessFlightSqlServer.ServerState state) : FlightSqlServer
{
    // ---- who is asking ----

    private void Authorize(ServerCallContext context)
    {
        if (!state.RequireAuth) return;
        var header = context.RequestHeaders.GetValue("authorization");
        if (header != null && header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var token = header["Bearer ".Length..];
            if (token == InProcessFlightSqlServer.Token || state.Issued.ContainsKey(token)) return;
        }
        throw new RpcException(new Status(StatusCode.Unauthenticated, "the token is not valid (this text must never reach a person)"));
    }

    public override async Task Handshake(IAsyncStreamReader<FlightHandshakeRequest> requestStream, IAsyncStreamWriter<FlightHandshakeResponse> responseStream, ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue("authorization");
        lock (state.Gate) state.Handshakes.Add(header == null ? "none" : header.Split(' ')[0]);
        if (header == null || !header.StartsWith("Basic ", StringComparison.Ordinal)
            || Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])) != $"{InProcessFlightSqlServer.User}:{InProcessFlightSqlServer.Password}")
            throw new RpcException(new Status(StatusCode.Unauthenticated, "wrong user or password (this text must never reach a person)"));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        state.Issued[token] = InProcessFlightSqlServer.User;
        await context.WriteResponseHeadersAsync(new Metadata { { "authorization", "Bearer " + token } });
        await responseStream.WriteAsync(new FlightHandshakeResponse());
    }

    public override Task<FlightInfo> GetFlightInfo(FlightDescriptor flightDescriptor, ServerCallContext context) { Authorize(context); return base.GetFlightInfo(flightDescriptor, context); }
    public override Task DoGet(FlightTicket ticket, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) { Authorize(context); return base.DoGet(ticket, responseStream, context); }
    public override Task DoAction(FlightAction action, IAsyncStreamWriter<FlightResult> responseStream, ServerCallContext context) { Authorize(context); return base.DoAction(action, responseStream, context); }
    public override Task DoPut(FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream, ServerCallContext context) { Authorize(context); return base.DoPut(requestStream, responseStream, context); }

    // ---- statements ----

    private static ByteString Packed(IMessage m) => Any.Pack(m).ToByteString();

    private FlightInfo InfoFor(string handle, RecordBatch result, FlightDescriptor descriptor)
    {
        state.Results[handle] = result;
        var ticket = new FlightTicket(Packed(new CommandPreparedStatementQuery { PreparedStatementHandle = ByteString.CopyFromUtf8(handle) }));
        return new FlightInfo(result.Schema, descriptor, [new FlightEndpoint(ticket, [])], result.Length, -1);
    }

    protected override async Task<FlightInfo> GetStatementQueryFlightInfo(CommandStatementQuery command, FlightDescriptor descriptor, ServerCallContext context)
    {
        var handle = Guid.NewGuid().ToString("N");
        return InfoFor(handle, await RunAsync(command.Query, null, context.CancellationToken), descriptor);
    }

    protected override async Task<FlightInfo> GetPreparedStatementQueryFlightInfo(CommandPreparedStatementQuery command, FlightDescriptor descriptor, ServerCallContext context)
    {
        var handle = command.PreparedStatementHandle.ToStringUtf8();
        if (!state.Statements.TryGetValue(handle, out var sql)) throw new RpcException(new Status(StatusCode.NotFound, "no such prepared statement"));
        state.Parameters.TryGetValue(handle, out var parameters);
        return InfoFor(handle, await RunAsync(sql, parameters, context.CancellationToken), descriptor);
    }

    protected override async Task DoGetPreparedStatementQuery(CommandPreparedStatementQuery command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context)
    {
        var handle = command.PreparedStatementHandle.ToStringUtf8();
        if (!state.Results.TryGetValue(handle, out var result)) throw new RpcException(new Status(StatusCode.NotFound, "no result for it"));
        await responseStream.WriteAsync(result);
    }

    protected override async Task CreatePreparedStatement(ActionCreatePreparedStatementRequest request, FlightAction action, IAsyncStreamWriter<FlightResult> streamWriter, ServerCallContext context)
    {
        var handle = Guid.NewGuid().ToString("N");
        lock (state.Gate) state.Actions.Add("create " + handle[..6]);
        state.Statements[handle] = request.Query;
        Interlocked.Increment(ref state.OpenPrepared);
        // the parameter schema is left empty (the protocol allows a server that does not say, and a proxy may not); the dataset schema is the statement's own, asked with every `?` bound to NULL, and is left
        // empty when the server cannot tell, which the client must accept as well
        var result = new ActionCreatePreparedStatementResult { PreparedStatementHandle = ByteString.CopyFromUtf8(handle) };
        if (!request.Query.Contains("/*noschema*/", StringComparison.Ordinal))
        {
            try
            {
                var nulls = new RecordBatch(new Schema(Enumerable.Range(0, request.Query.Count(c => c == '?')).Select(i => new Apache.Arrow.Field("p" + i, StringType.Default, true)).ToList(), null),
                    Enumerable.Range(0, request.Query.Count(c => c == '?')).Select(_ => (IArrowArray)new StringArray.Builder().AppendNull().Build()).ToList(), 1);
                var empty = await RunAsync($"SELECT * FROM ({request.Query.TrimEnd().TrimEnd(';')}) AS described LIMIT 0", nulls, context.CancellationToken, record: false);
                result.DatasetSchema = ByteString.CopyFrom(SchemaExtensions.SerializeSchema(empty.Schema));
            }
            catch (RpcException) { /* no schema to give */ }
        }
        await streamWriter.WriteAsync(new FlightResult(Packed(result)));
    }

    protected override Task ClosePreparedStatement(ActionClosePreparedStatementRequest request, FlightAction action, IAsyncStreamWriter<FlightResult> streamWriter, ServerCallContext context)
    {
        var handle = request.PreparedStatementHandle.ToStringUtf8();
        lock (state.Gate) state.Actions.Add("close " + handle[..6]);
        if (state.Statements.TryRemove(handle, out _)) Interlocked.Decrement(ref state.OpenPrepared);
        state.Parameters.TryRemove(handle, out _);
        state.Results.TryRemove(handle, out _);
        return Task.CompletedTask;
    }

    protected override async Task PutPreparedStatementQuery(CommandPreparedStatementQuery command, FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream, ServerCallContext context)
    {
        var handle = command.PreparedStatementHandle.ToStringUtf8();
        if (await requestStream.MoveNext(context.CancellationToken)) state.Parameters[handle] = requestStream.Current;
        await responseStream.WriteAsync(FlightPutResult.Empty);
    }

    private async Task<RecordBatch> RunAsync(string sql, RecordBatch? parameters, CancellationToken ct, bool record = true)
    {
        var values = parameters == null ? [] : Enumerable.Range(0, parameters.ColumnCount).Select(i => ArrowConversions.Value(parameters.Column(i), 0)).ToArray();
        if (record)
            lock (state.Gate)
            {
                state.Texts.Add(sql);
                if (parameters != null) state.Bound.Add(new(sql, parameters.Schema.FieldsList.Select(f => f.DataType.Name + (f.DataType is TimestampType { Timezone: { Length: > 0 } tz } ? $"({tz})" : "")).ToList(), values));
        }
        var now = Interlocked.Increment(ref state.Concurrent);
        try
        {
            lock (state.Gate) state.MostConcurrent = Math.Max(state.MostConcurrent, now);
            // a statement that is meant to be slow (a test of the call's deadline, or of how many run together)
            if (record && sql.Contains("/*slow*/", StringComparison.Ordinal)) await Task.Delay(TimeSpan.FromSeconds(20), ct);
            if (record && sql.Contains("\"slow", StringComparison.Ordinal)) await Task.Delay(TimeSpan.FromSeconds(1.5), ct);       // a table named slow*: it takes a second and a half to answer
            lock (state.Gate)
            {
                using var cmd = state.Db.CreateCommand();
                cmd.CommandText = sql;
                foreach (var v in values) cmd.Parameters.Add(new DuckDBParameter { Value = v ?? DBNull.Value });
                using var reader = cmd.ExecuteReader();
                return ToBatch(reader);
            }
        }
        catch (DuckDBException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DuckDB: " + ex.Message));
        }
        finally { Interlocked.Decrement(ref state.Concurrent); }
    }

    // ---- DuckDB's rows as Arrow ----

    private static RecordBatch ToBatch(DbDataReader reader)
    {
        var types = new List<IArrowType>();
        for (var i = 0; i < reader.FieldCount; i++) types.Add(ArrowTypeOf((reader.GetDataTypeName(i) ?? "VARCHAR").ToUpperInvariant()));
        var columns = types.Select(_ => new List<object?>()).ToList();
        while (reader.Read())
            for (var i = 0; i < types.Count; i++)
            {
                var v = reader.GetValue(i);
                columns[i].Add(v is DBNull ? null : v is Stream s ? ReadAll(s) : v);
            }
        var fields = types.Select((t, i) => new Apache.Arrow.Field(reader.GetName(i), t, true)).ToList();
        return new RecordBatch(new Schema(fields, null), types.Select((t, i) => ArrayOf(t, columns[i])).ToList(), columns.Count == 0 ? 0 : columns[0].Count);
    }

    private static byte[] ReadAll(Stream s) { using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }

    private static IArrowType ArrowTypeOf(string duckType)
    {
        if (duckType.StartsWith("DECIMAL(", StringComparison.Ordinal))
        {
            var parts = duckType[8..^1].Split(',');
            return new Decimal128Type(int.Parse(parts[0]), int.Parse(parts[1]));
        }
        return duckType switch
        {
            "BIGINT" => Int64Type.Default,
            "INTEGER" => Int32Type.Default,
            "SMALLINT" => Int16Type.Default,
            "TINYINT" => Int8Type.Default,
            "DOUBLE" => DoubleType.Default,
            "FLOAT" => FloatType.Default,
            "BOOLEAN" => BooleanType.Default,
            "DATE" => Date32Type.Default,
            "TIMESTAMP" => new TimestampType(TimeUnit.Microsecond, (string?)null),
            "TIMESTAMP WITH TIME ZONE" => new TimestampType(TimeUnit.Microsecond, "UTC"),
            "BLOB" => BinaryType.Default,
            _ => StringType.Default,
        };
    }

    private static IArrowArray ArrayOf(IArrowType type, List<object?> values)
    {
        switch (type)
        {
            case Int64Type: { var b = new Int64Array.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToInt64(v)); } return b.Build(); }
            case Int32Type: { var b = new Int32Array.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToInt32(v)); } return b.Build(); }
            case Int16Type: { var b = new Int16Array.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToInt16(v)); } return b.Build(); }
            case Int8Type: { var b = new Int8Array.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToSByte(v)); } return b.Build(); }
            case DoubleType: { var b = new DoubleArray.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToDouble(v)); } return b.Build(); }
            case FloatType: { var b = new FloatArray.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToSingle(v)); } return b.Build(); }
            case BooleanType: { var b = new BooleanArray.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToBoolean(v)); } return b.Build(); }
            case Decimal128Type d: { var b = new Decimal128Array.Builder(d); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToDecimal(v)); } return b.Build(); }
            case Date32Type: { var b = new Date32Array.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(v is DateOnly o ? o.ToDateTime(TimeOnly.MinValue) : Convert.ToDateTime(v)); } return b.Build(); }
            case TimestampType t:
            {
                var b = new TimestampArray.Builder(t);
                foreach (var v in values)
                {
                    if (v == null) b.AppendNull();
                    else b.Append(v is DateTimeOffset o ? o : new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(v), DateTimeKind.Utc)));
                }
                return b.Build();
            }
            case BinaryType: { var b = new BinaryArray.Builder(); foreach (var v in values) { if (v is byte[] bytes) b.Append(bytes.AsSpan()); else b.AppendNull(); } return b.Build(); }
            default: { var b = new StringArray.Builder(); foreach (var v in values) { if (v == null) b.AppendNull(); else b.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!); } return b.Build(); }
        }
    }

    // ---- what this server does not offer ----

    private static RpcException Unimplemented(string what) => new(new Status(StatusCode.Unimplemented, what + " is not offered by this server"));

    protected override Task<FlightInfo> GetCatalogFlightInfo(CommandGetCatalogs command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetCatalogs");
    protected override Task<FlightInfo> GetDbSchemaFlightInfo(CommandGetDbSchemas command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetDbSchemas");
    protected override Task<FlightInfo> GetTablesFlightInfo(CommandGetTables command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetTables");
    protected override Task<FlightInfo> GetTableTypesFlightInfo(CommandGetTableTypes command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetTableTypes");
    // GetSqlInfo is answered when a test gave the server something to say (InProcessFlightSqlServer.SqlInfo); the base class has the protocol's schema
    protected override Task<FlightInfo> GetSqlFlightInfo(CommandGetSqlInfo command, FlightDescriptor flightDescriptor, ServerCallContext context)
    {
        Authorize(context);
        if (state.SqlInfo == null) throw Unimplemented("GetSqlInfo");
        return Task.FromResult(new FlightInfo(SqlInfoReply, flightDescriptor, [new FlightEndpoint(new FlightTicket(Packed(command)), [])], -1, -1));
    }
    protected override Task<FlightInfo> GetPrimaryKeysFlightInfo(CommandGetPrimaryKeys command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetPrimaryKeys");
    protected override Task<FlightInfo> GetExportedKeysFlightInfo(CommandGetExportedKeys command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetExportedKeys");
    protected override Task<FlightInfo> GetImportedKeysFlightInfo(CommandGetImportedKeys command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetImportedKeys");
    protected override Task<FlightInfo> GetCrossReferenceFlightInfo(CommandGetCrossReference command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetCrossReference");
    protected override Task<FlightInfo> GetXdbcTypeFlightInfo(CommandGetXdbcTypeInfo command, FlightDescriptor flightDescriptor, ServerCallContext context) => throw Unimplemented("GetXdbcTypeInfo");
    protected override async Task DoGetSqlInfo(CommandGetSqlInfo command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context)
    {
        await DoGetSqlInfo2(command, responseStream, context);
    }

    // the protocol's reply: a uint32 id and a dense union of the value types
    private static readonly Schema SqlInfoReply = new([
        new Apache.Arrow.Field("info_name", UInt32Type.Default, false),
        new Apache.Arrow.Field("value", new UnionType([
            new Apache.Arrow.Field("string_value", StringType.Default, false), new Apache.Arrow.Field("bool_value", BooleanType.Default, false), new Apache.Arrow.Field("bigint_value", Int64Type.Default, false),
            new Apache.Arrow.Field("int32_bitmask", Int32Type.Default, false), new Apache.Arrow.Field("string_list", new ListType(new Apache.Arrow.Field("item", StringType.Default, true)), false),
            new Apache.Arrow.Field("int32_to_int32_list_map", new MapType(Int32Type.Default, new ListType(new Apache.Arrow.Field("item", Int32Type.Default, true)), false), false)], [0, 1, 2, 3, 4, 5], UnionMode.Dense), false),
    ], null);

    private async Task DoGetSqlInfo2(CommandGetSqlInfo command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context)
    {
        if (state.SqlInfo is not { } facts) throw Unimplemented("GetSqlInfo");
        lock (state.Gate) state.Actions.Add("sqlinfo");
        var asked = command.Info.Count == 0 ? facts.Keys.Order().ToList() : command.Info.Where(facts.ContainsKey).ToList();
        var names = new UInt32Array.Builder();
        var typeIds = new ArrowBuffer.Builder<byte>();
        var offsets = new ArrowBuffer.Builder<int>();
        var strings = new StringArray.Builder(); var bools = new BooleanArray.Builder(); var longs = new Int64Array.Builder();
        foreach (var id in asked)
        {
            names.Append(id);
            switch (facts[id])
            {
                case string s: typeIds.Append(0); offsets.Append(strings.Length); strings.Append(s); break;
                case bool b: typeIds.Append(1); offsets.Append(bools.Length); bools.Append(b); break;
                default: typeIds.Append(2); offsets.Append(longs.Length); longs.Append(Convert.ToInt64(facts[id])); break;
            }
        }
        var union = (UnionType)SqlInfoReply.GetFieldByName("value").DataType;
        IArrowArray[] children = [strings.Build(), bools.Build(), longs.Build(), new Int32Array.Builder().Build(), new ListArray.Builder(StringType.Default).Build(), new MapArray.Builder((MapType)union.Fields[5].DataType).Build()];
        await responseStream.SetupStream(SqlInfoReply);
        await responseStream.WriteAsync(new RecordBatch(SqlInfoReply, [names.Build(), new DenseUnionArray(union, asked.Count, children, typeIds.Build(), offsets.Build(), 0, 0)], asked.Count));
    }
    protected override Task DoGetCatalog(CommandGetCatalogs command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetCatalogs");
    protected override Task DoGetTableType(CommandGetTableTypes command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetTableTypes");
    protected override Task DoGetTables(CommandGetTables command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetTables");
    protected override Task DoGetPrimaryKeys(CommandGetPrimaryKeys command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetPrimaryKeys");
    protected override Task DoGetDbSchema(CommandGetDbSchemas command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetDbSchemas");
    protected override Task DoGetExportedKeys(CommandGetExportedKeys command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetExportedKeys");
    protected override Task DoGetImportedKeys(CommandGetImportedKeys command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetImportedKeys");
    protected override Task DoGetCrossReference(CommandGetCrossReference command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetCrossReference");
    protected override Task DoGetXbdcTypeInfo(CommandGetXdbcTypeInfo command, FlightServerRecordBatchStreamWriter responseStream, ServerCallContext context) => throw Unimplemented("GetXdbcTypeInfo");
    protected override Task PutPreparedStatementUpdate(CommandPreparedStatementUpdate command, FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream, ServerCallContext context) => throw Unimplemented("an update");
    protected override Task PutStatementUpdate(CommandStatementUpdate command, FlightServerRecordBatchStreamReader requestStream, IAsyncStreamWriter<FlightPutResult> responseStream, ServerCallContext context) => throw Unimplemented("an update");
}
