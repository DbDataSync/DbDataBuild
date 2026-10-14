using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Flight.Sql.Client;
using DbDataBuild.Models;
using DbDataBuild.State;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace DbDataBuild.Execution;

/// <summary>A table (or view) a Flight SQL server's metadata calls described: the schema name it is in, its name, whether it is a view, and its columns from the Arrow schema the server sent.</summary>
internal sealed record MetadataTable(string SchemaName, string Name, bool IsView, IReadOnlyList<ColumnShape> Columns);

/// <summary>A foreign key as a Flight SQL server's imported-keys call describes it: the key's name, its columns and what they reference.</summary>
internal sealed record MetadataForeignKey(string Table, string Name, IReadOnlyList<string> Columns, string ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns);

/// <summary>An index as a catalog describes it: its table, name, whether it is unique, its key columns in order (`name desc` for a descending one) and its included columns.</summary>
internal sealed record MetadataIndex(string Table, string Name, bool Unique, IReadOnlyList<string> Columns, IReadOnlyList<string> Include);

/// <summary>
/// What a source that has a catalog of its own, apart from SQL, can say about its tables: Flight SQL's metadata calls (`CommandGetDbSchemas`, `CommandGetTables`, `CommandGetPrimaryKeys`, `CommandGetImportedKeys`) and
/// ODBC's catalog functions (`catalog: odbc`). Every answer is null when the source does not offer that call; the caller then falls back to the dialect's `information_schema` text, or says plainly that there is no catalog to read.
/// </summary>
internal interface IMetadataCatalog
{
    /// <summary>The indexes of the tables of a schema, per table; null where the source has no way to say (Flight SQL has no metadata call for indexes).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataIndex>>?> IndexesAsync(string schema, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<MetadataIndex>>?>(null);

    Task<IReadOnlyList<string>?> SchemasAsync(CancellationToken ct);
    Task<IReadOnlyList<MetadataTable>?> TablesAsync(string schema, string? table, CancellationToken ct);
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> PrimaryKeysAsync(string schema, CancellationToken ct);
    Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?> ForeignKeysAsync(string schema, CancellationToken ct);
}

/// <summary>What a source can say about the system it reaches without a query of ours; today only a Flight SQL server's `GetSqlInfo`. Null when the source does not offer the call.</summary>
internal interface IServerFacts
{
    Task<ServerFacts?> ServerFactsAsync(CancellationToken ct);
}

/// <summary>
/// An Arrow Flight SQL server as a row source (Apache.Arrow.Flight.Sql over Grpc.Net.Client). The login is a URI with options (<see cref="FlightSqlLogin"/>). A statement without parameters is sent as a plain query; one with
/// parameters is **prepared** and its parameters are bound as a one-row Arrow batch in the order the plan gives (`?`, positional). The server answers with endpoints; the rows are read from each in turn on this same
/// connection as record batches and handed over value by value, by <see cref="ArrowConversions.Value"/>. A call's time limit is the gRPC deadline. Like ADO.NET's drivers it is only ever reached through <see cref="ReadSession"/>.
/// </summary>
internal sealed class FlightSqlRowSource : IRowSource, IMetadataCatalog, IServerFacts
{
    private readonly FlightSqlLogin login;
    private readonly GrpcChannel channel;
    private readonly FlightSqlClient client;
    private readonly Metadata headers;

    internal FlightSqlClient Client => client;

    private readonly DeadlineInterceptor deadlines;

    private FlightSqlRowSource(FlightSqlLogin login, GrpcChannel channel, FlightSqlClient client, Metadata headers, DeadlineInterceptor deadlines)
    {
        this.login = login; this.channel = channel; this.client = client; this.headers = headers; this.deadlines = deadlines;
    }

    /// <summary>
    /// The time limit of a call is a gRPC **deadline**: every call made while a limit is set carries one (the server gets it too, as `grpc-timeout`, and can stop). The library's own `FlightCallOptions.Timeout` is not
    /// used by its client, so the limit is put on the calls here.
    /// </summary>
    private sealed class DeadlineInterceptor : Interceptor
    {
        private long ticks;
        public TimeSpan? Timeout { set => Interlocked.Exchange(ref ticks, value?.Ticks ?? 0); }

        private ClientInterceptorContext<TRequest, TResponse> With<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
            where TRequest : class where TResponse : class =>
            Interlocked.Read(ref ticks) is var t and > 0 && context.Options.Deadline is null
                ? new(context.Method, context.Host, context.Options.WithDeadline(DateTime.UtcNow + TimeSpan.FromTicks(t))) : context;

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation) =>
            continuation(request, With(context));
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
            continuation(request, With(context));
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) =>
            continuation(request, With(context));
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation) =>
            continuation(With(context));
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation) =>
            continuation(With(context));
    }

    /// <summary>`Flight SQL server proxy.example.com:31337 (TLS)`: where the source goes, never how it signs in.</summary>
    public string? ServerDescription => $"Flight SQL server {login.HostAndPort}{(login.Tls ? " (TLS)" : " (no TLS)")}";

    /// <summary>The seconds a call may take when the caller names none (the other drivers' default).</summary>
    internal const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// Builds the channel and signs in: a bearer token is sent with each call; a user and password are traded for one by the server's handshake. Nothing is sent for a token (the first call is the first contact), so
    /// "the server cannot be reached" and "the token is refused" show at the first statement, which for a command is its check of the origin.
    /// </summary>
    public static async Task<FlightSqlRowSource> ConnectAsync(string loginValue, CancellationToken ct)
    {
        var login = FlightSqlLogin.Parse(loginValue);
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds), EnableMultipleHttp2Connections = true };
        if (login.Tls) handler.SslOptions = TlsOptions(login);
        var channel = GrpcChannel.ForAddress(login.Address, new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true, MaxReceiveMessageSize = null, MaxSendMessageSize = null });
        try
        {
            var headers = new Metadata();
            foreach (var (name, value) in login.Headers) headers.Add(name, value);
            var deadlines = new DeadlineInterceptor();
            var flight = new FlightClient(channel.Intercept(deadlines));
            if (await login.ResolveTokenAsync(ct) is { } token) headers.Add("authorization", "Bearer " + token);
            else if (login.User != null) headers.Add("authorization", await HandshakeAsync(flight, login, headers, ct));
            return new FlightSqlRowSource(login, channel, new FlightSqlClient(flight), headers, deadlines);
        }
        catch { channel.Dispose(); throw; }
    }

    /// <summary>
    /// The server's basic-authentication handshake: the user and password go in an `authorization: Basic` header of the Handshake call, and the server answers with the header to use from then on (`Bearer ...`), in the
    /// response headers or the trailers. A server that answers with none is sent the basic header with every call.
    /// </summary>
    private static async Task<string> HandshakeAsync(FlightClient flight, FlightSqlLogin login, Metadata custom, CancellationToken ct)
    {
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login.User}:{login.Password}"));
        var metadata = new Metadata();
        foreach (var h in custom) metadata.Add(h);
        metadata.Add("authorization", basic);
        using var call = flight.Handshake(metadata, DateTime.UtcNow.AddSeconds(DefaultTimeoutSeconds), ct);
        var headersTask = call.ResponseHeadersAsync;
        try
        {
            await call.RequestStream.WriteAsync(new FlightHandshakeRequest(ByteString.Empty, 0), ct);
            await call.RequestStream.CompleteAsync();
        }
        // a server that decides on the header alone answers and ends the call before the empty request is written; what it answered is what counts (a refusal is the status the next read throws)
        catch (Exception ex) when (ex is RpcException or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested) { }
        var responseHeaders = await headersTask;
        while (await call.ResponseStream.MoveNext(ct)) { /* the payload is not used */ }
        var issued = responseHeaders.GetValue("authorization") ?? call.GetTrailers().GetValue("authorization");
        return string.IsNullOrWhiteSpace(issued) ? basic : issued;
    }

    /// <summary>The TLS the login asks for: the machine's trust by default, the certificate authority of `ca_file` instead of it, or no check at all (`tls_skip_verify`).</summary>
    internal static SslClientAuthenticationOptions TlsOptions(FlightSqlLogin login)
    {
        var options = new SslClientAuthenticationOptions();
        if (login.TlsSkipVerify) options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        else if (login.CaFile != null)
        {
            X509Certificate2Collection roots;
            try { roots = []; roots.ImportFromPemFile(login.CaFile); }
            catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
            { throw new FlightSqlLoginException("The ca_file of the Flight SQL login could not be read as a PEM file of certificates."); }
            options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate == null) return false;
                if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate));
            };
        }
        return options;
    }

    private FlightCallOptions Options(int? timeoutSeconds)
    {
        var seconds = timeoutSeconds ?? DefaultTimeoutSeconds;
        deadlines.Timeout = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        return new FlightCallOptions { Headers = headers };
    }

    public async Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct)
    {
        var options = Options(timeoutSeconds);
        PreparedHandle? prepared = null;
        try
        {
            FlightInfo info;
            if (parameters.Count == 0) info = await client.ExecuteAsync(sql, Transaction.NoTransaction, options, ct);
            else
            {
                prepared = await PrepareAsync(sql, options, ct);
                info = await BindAndExecuteAsync(prepared, ArrowConversions.ParameterBatch(parameters), options, ct);
            }
            return await FlightSqlRowReader.StartAsync(this, info, options, prepared, ct);
        }
        catch
        {
            await CloseQuietlyAsync(prepared, options);
            throw;
        }
    }

    // The prepared-statement exchange is made here from the protocol's own messages (CreatePreparedStatement and ClosePreparedStatement actions, a DoPut of the parameters, a FlightInfo for the handle) instead of with the
    // library's PreparedStatement, which refuses a server that leaves the dataset or the parameter schema empty (the protocol allows it, and a proxy may well do it) and holds the handle as UTF-8 text.

    /// <summary>A server-side prepared statement: its opaque handle, and the schema of its rows when the server said it.</summary>
    internal sealed record PreparedHandle(ByteString Handle, Schema? DatasetSchema);

    private async Task<PreparedHandle> PrepareAsync(string sql, FlightCallOptions options, CancellationToken ct)
    {
        var action = new FlightAction("CreatePreparedStatement", Google.Protobuf.WellKnownTypes.Any.Pack(new Arrow.Flight.Protocol.Sql.ActionCreatePreparedStatementRequest { Query = sql }).ToByteString());
        Arrow.Flight.Protocol.Sql.ActionCreatePreparedStatementResult? created = null;
        await foreach (var result in client.DoActionAsync(action, options, ct)) created ??= Google.Protobuf.WellKnownTypes.Any.Parser.ParseFrom(result.Body).Unpack<Arrow.Flight.Protocol.Sql.ActionCreatePreparedStatementResult>();
        if (created == null || created.PreparedStatementHandle.IsEmpty) throw new InvalidOperationException("The Flight SQL server returned no prepared statement.");
        var schema = created.DatasetSchema.IsEmpty ? null : SchemaExtensions.DeserializeSchema(new ReadOnlyMemory<byte>(created.DatasetSchema.ToByteArray()));
        return new PreparedHandle(created.PreparedStatementHandle, schema);
    }

    private async Task<FlightInfo> BindAndExecuteAsync(PreparedHandle prepared, RecordBatch parameters, FlightCallOptions options, CancellationToken ct)
    {
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Google.Protobuf.WellKnownTypes.Any.Pack(new Arrow.Flight.Protocol.Sql.CommandPreparedStatementQuery { PreparedStatementHandle = prepared.Handle }).ToByteArray());
        await client.DoPutAsync(descriptor, parameters, options, ct);
        return await client.GetFlightInfoAsync(descriptor, options, ct);
    }

    internal static async Task CloseQuietlyAsync(PreparedHandle? prepared, FlightCallOptions options, FlightSqlRowSource source)
    {
        if (prepared == null) return;
        try
        {
            var action = new FlightAction("ClosePreparedStatement", Google.Protobuf.WellKnownTypes.Any.Pack(new Arrow.Flight.Protocol.Sql.ActionClosePreparedStatementRequest { PreparedStatementHandle = prepared.Handle }).ToByteString());
            await foreach (var _ in source.client.DoActionAsync(action, options, CancellationToken.None)) { /* no result */ }
        }
        catch (Exception) { /* the statement is gone with the connection */ }
    }

    private Task CloseQuietlyAsync(PreparedHandle? prepared, FlightCallOptions options) => CloseQuietlyAsync(prepared, options, this);

    public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) =>
        throw new NotSupportedException("A Flight SQL connection runs queries only: a native command is for SQL Server and PostgreSQL connections.");

    /// <summary>The dialect of the connection, for the one bounded read this source makes (<see cref="DescribeAsync"/>); the login sets it.</summary>
    internal DialectProfile Dialect { get; set; } = DialectProfiles.Ansi;

    public async Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        if (await DescribeByProtocolAsync(sql, ct) is { } described) return described;
        // a server that will not say the schema of a statement without running it: a read bounded to no rows, in the dialect's `limit` style, gives the schema of its (empty) result
        try
        {
            await using var reader = (FlightSqlRowReader)await ExecuteAsync(Dialect.Bounded(sql, 0), parameters, null, false, ct);
            return reader.ResultSchema.FieldsList.Count == 0 ? null : ArrowConversions.Columns(reader.ResultSchema);
        }
        catch (Exception ex) when (DriverFailure.Rpc(ex) != null) { return null; }
    }

    private async Task<IReadOnlyList<ColumnShape>?> DescribeByProtocolAsync(string sql, CancellationToken ct)
    {
        var options = Options(null);
        PreparedHandle? prepared = null;
        try
        {
            Schema? schema = null;
            try
            {
                prepared = await PrepareAsync(sql, options, ct);
                schema = prepared.DatasetSchema;
            }
            catch (Exception ex) when (DriverFailure.Rpc(ex) != null) { /* a server without prepared statements may still say the schema of a query */ }
            if (schema == null || schema.FieldsList.Count == 0)
            {
                // the library's GetExecuteSchemaAsync prepares a statement it then leaves open when the server gives no schema: the GetSchema call is made here with the statement as a command
                try { schema = await client.GetSchemaAsync(FlightDescriptor.CreateCommandDescriptor(Google.Protobuf.WellKnownTypes.Any.Pack(new Arrow.Flight.Protocol.Sql.CommandStatementQuery { Query = sql }).ToByteArray()), options, ct); }
                catch (Exception ex) when (DriverFailure.Rpc(ex) != null) { return null; }
            }
            return schema == null || schema.FieldsList.Count == 0 ? null : ArrowConversions.Columns(schema);
        }
        finally { await CloseQuietlyAsync(prepared, options); }
    }

    // ---- the metadata calls ----

    // the client library wraps a call's RpcException in an InvalidOperationException ("Failed to prepare statement"): the status is looked for down the chain
    private static bool NotOffered(Exception ex) => DriverFailure.Rpc(ex)?.StatusCode is StatusCode.Unimplemented or StatusCode.InvalidArgument;

    // The library's own metadata calls send an empty string for a filter left out (`catalog = ""`), which servers read as "the catalog with no name" and answer with nothing. These send only what is asked.
    private async Task<FlightInfo> MetadataInfoAsync(Google.Protobuf.IMessage command, FlightCallOptions options, CancellationToken ct) =>
        await client.GetFlightInfoAsync(FlightDescriptor.CreateCommandDescriptor(Google.Protobuf.WellKnownTypes.Any.Pack(command).ToByteArray()), options, ct);

    private async Task<List<RecordBatch>> ReadAsync(FlightInfo info, FlightCallOptions options, CancellationToken ct)
    {
        var batches = new List<RecordBatch>();
        foreach (var endpoint in info.Endpoints)
            await foreach (var batch in client.DoGetAsync(endpoint.Ticket, options, ct)) batches.Add(batch);
        return batches;
    }

    private static int IndexOf(RecordBatch batch, string column) => batch.Schema.FieldsList.ToList().FindIndex(f => f.Name == column);

    private static string? Text(RecordBatch batch, string column, int row) =>
        IndexOf(batch, column) is var i and >= 0 ? ArrowConversions.Value(batch.Column(i), row)?.ToString() : null;

    public async Task<IReadOnlyList<string>?> SchemasAsync(CancellationToken ct)
    {
        var options = Options(null);
        try
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var batch in await ReadAsync(await MetadataInfoAsync(new Arrow.Flight.Protocol.Sql.CommandGetDbSchemas(), options, ct), options, ct))
                for (var r = 0; r < batch.Length; r++) if (Text(batch, "db_schema_name", r) is { Length: > 0 } name) names.Add(name);
            return names.ToList();
        }
        catch (Exception ex) when (NotOffered(ex)) { return null; }
    }

    public async Task<IReadOnlyList<MetadataTable>?> TablesAsync(string schema, string? table, CancellationToken ct)
    {
        var options = Options(null);
        try
        {
            var found = new List<MetadataTable>();
            var command = new Arrow.Flight.Protocol.Sql.CommandGetTables { DbSchemaFilterPattern = schema, IncludeSchema = true };
            if (table != null) command.TableNameFilterPattern = table;
            {
                foreach (var batch in await ReadAsync(await MetadataInfoAsync(command, options, ct), options, ct))
                    for (var r = 0; r < batch.Length; r++)
                    {
                        // the pattern is a LIKE pattern (`_` matches any character): the name is compared exactly here
                        if (Text(batch, "db_schema_name", r) != schema || Text(batch, "table_name", r) is not { } name || (table != null && name != table)) continue;
                        var type = Text(batch, "table_type", r) ?? "";
                        if (type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) || type.Contains("TEMPORARY", StringComparison.OrdinalIgnoreCase)) continue;
                        var schemaIndex = IndexOf(batch, "table_schema");
                        if (schemaIndex < 0 || ArrowConversions.Value(batch.Column(schemaIndex), r) is not byte[] bytes || bytes.Length == 0) continue;     // no columns told: it cannot be described
                        found.Add(new(schema, name, type.Contains("VIEW", StringComparison.OrdinalIgnoreCase), ArrowConversions.Columns(SchemaExtensions.DeserializeSchema(new ReadOnlyMemory<byte>(bytes)))));
                    }
            }
            return found;
        }
        catch (Exception ex) when (NotOffered(ex)) { return null; }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> PrimaryKeysAsync(string schema, CancellationToken ct)
    {
        var options = Options(null);
        try
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (await TablesAsync(schema, null, ct) is not { } tables) return null;                                  // no metadata calls at all
            foreach (var table in tables)
            {
                if (table.IsView) continue;
                var columns = new List<(int Sequence, string Column)>();
                foreach (var batch in await ReadAsync(await MetadataInfoAsync(new Arrow.Flight.Protocol.Sql.CommandGetPrimaryKeys { DbSchema = schema, Table = table.Name }, options, ct), options, ct))
                    for (var r = 0; r < batch.Length; r++)
                        if (Text(batch, "column_name", r) is { } column) columns.Add((int.TryParse(Text(batch, "key_sequence", r), out var s) ? s : columns.Count + 1, column));
                if (columns.Count > 0) result[table.Name] = columns.OrderBy(c => c.Sequence).Select(c => c.Column).ToList();
            }
            return result;
        }
        catch (Exception ex) when (NotOffered(ex)) { return null; }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?> ForeignKeysAsync(string schema, CancellationToken ct)
    {
        var options = Options(null);
        try
        {
            var result = new Dictionary<string, IReadOnlyList<MetadataForeignKey>>(StringComparer.Ordinal);
            if (await TablesAsync(schema, null, ct) is not { } tables) return null;                                  // no metadata calls at all
            foreach (var table in tables)
            {
                if (table.IsView) continue;
                var rows = new List<(string Key, int Sequence, string Column, string RefSchema, string RefTable, string RefColumn)>();
                foreach (var batch in await ReadAsync(await MetadataInfoAsync(new Arrow.Flight.Protocol.Sql.CommandGetImportedKeys { DbSchema = schema, Table = table.Name }, options, ct), options, ct))
                    for (var r = 0; r < batch.Length; r++)
                        if (Text(batch, "fk_column_name", r) is { } column && Text(batch, "pk_table_name", r) is { } refTable && Text(batch, "pk_column_name", r) is { } refColumn)
                            rows.Add((Text(batch, "fk_key_name", r) ?? $"fk_{table.Name}_{refTable}", int.TryParse(Text(batch, "key_sequence", r), out var s) ? s : rows.Count + 1, column,
                                Text(batch, "pk_db_schema_name", r) ?? schema, refTable, refColumn));
                if (rows.Count == 0) continue;
                result[table.Name] = rows.GroupBy(x => x.Key, StringComparer.Ordinal).Select(g =>
                {
                    var ordered = g.OrderBy(x => x.Sequence).ToList();
                    return new MetadataForeignKey(table.Name, g.Key, ordered.Select(x => x.Column).ToList(), ordered[0].RefSchema, ordered[0].RefTable, ordered.Select(x => x.RefColumn).ToList());
                }).ToList();
            }
            return result;
        }
        catch (Exception ex) when (NotOffered(ex)) { return null; }
    }

    // The ids of the standard SqlInfo the tool reads (the Flight SQL protocol): the server's name, version and read-only flag, and how identifiers are cased and quoted.
    private const uint NameId = 0, VersionId = 1, ReadOnlyId = 3, IdentifierCaseId = 503, IdentifierQuoteId = 504, QuotedIdentifierCaseId = 505;

    /// <summary>
    /// `GetSqlInfo` for the ids above: what the server says it is. A proxy answers per data source (the login's `x-datasource` header). Null when the server does not implement the call; a value of a type other than the
    /// protocol's for an id is left out rather than guessed at.
    /// </summary>
    public async Task<ServerFacts?> ServerFactsAsync(CancellationToken ct)
    {
        var options = Options(null);
        try
        {
            var command = new Arrow.Flight.Protocol.Sql.CommandGetSqlInfo();
            foreach (var id in new[] { NameId, VersionId, ReadOnlyId, IdentifierCaseId, IdentifierQuoteId, QuotedIdentifierCaseId }) command.Info.Add(id);
            string? name = null, version = null, quote = null, idCase = null, quotedCase = null; bool? readOnly = null;
            foreach (var batch in await ReadAsync(await MetadataInfoAsync(command, options, ct), options, ct))
            {
                if (batch.ColumnCount < 2 || batch.Column(0) is not UInt32Array ids || batch.Column(1) is not DenseUnionArray values) continue;
                var typeIds = ((Apache.Arrow.Types.UnionType)values.Data.DataType).TypeIds.ToArray();
                for (var row = 0; row < batch.Length; row++)
                {
                    if (ids.GetValue(row) is not { } id) continue;
                    var child = System.Array.IndexOf(typeIds, (int)values.TypeIds[row]);
                    if (child < 0) continue;
                    var offset = values.ValueOffsets[row];
                    var text = values.Fields[child] is StringArray strings ? strings.GetString(offset) : null;
                    var number = values.Fields[child] switch { Int64Array longs => longs.GetValue(offset), Int32Array ints => ints.GetValue(offset), _ => null };
                    switch (id)
                    {
                        case NameId: name = text; break;
                        case VersionId: version = text; break;
                        case ReadOnlyId: readOnly = values.Fields[child] is BooleanArray flags ? flags.GetValue(offset) : null; break;
                        case IdentifierQuoteId: quote = text; break;
                        case IdentifierCaseId: idCase = number is { } c1 ? ServerFacts.CaseName(c1) : null; break;
                        case QuotedIdentifierCaseId: quotedCase = number is { } c2 ? ServerFacts.CaseName(c2) : null; break;
                    }
                }
            }
            return new ServerFacts(name, version, quote, idCase, quotedCase, readOnly);
        }
        catch (Exception ex) when (NotOffered(ex)) { return null; }
    }

    public ValueTask DisposeAsync()
    {
        channel.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The rows of one Flight SQL statement: the record batches of each endpoint in turn, a row at a time. The first batch is read when the statement starts, so a failure shows at once and the names are known.</summary>
internal sealed class FlightSqlRowReader : IRowReader
{
    private readonly FlightSqlRowSource source;
    private readonly FlightCallOptions options;
    private readonly FlightSqlRowSource.PreparedHandle? prepared;
    private readonly CancellationTokenSource cancellation;
    private readonly IReadOnlyList<FlightEndpoint> endpoints;
    private int nextEndpoint;
    private IAsyncEnumerator<RecordBatch>? batches;
    private RecordBatch? current;
    private int row = -1;
    private Schema schema;

    private FlightSqlRowReader(FlightSqlRowSource source, FlightCallOptions options, FlightSqlRowSource.PreparedHandle? prepared, CancellationTokenSource cancellation, IReadOnlyList<FlightEndpoint> endpoints, Schema schema)
    {
        this.source = source; this.options = options; this.prepared = prepared; this.cancellation = cancellation; this.endpoints = endpoints; this.schema = schema;
    }

    public static async Task<FlightSqlRowReader> StartAsync(FlightSqlRowSource source, FlightInfo info, FlightCallOptions options, FlightSqlRowSource.PreparedHandle? prepared, CancellationToken ct)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = new FlightSqlRowReader(source, options, prepared, cancellation, info.Endpoints, info.Schema ?? new Schema(new List<Field>(), null));
        try { await reader.AdvanceBatchAsync(); }
        catch { await reader.DisposeAsync(); throw; }
        return reader;
    }

    public IReadOnlyList<string> Names => schema.FieldsList.Select(f => f.Name).ToList();

    /// <summary>The Arrow schema of the result as the server (or the first batch) said it.</summary>
    internal Schema ResultSchema => schema;

    // the next batch with rows, across endpoints; false at the end of the last
    private async Task<bool> AdvanceBatchAsync()
    {
        while (true)
        {
            if (batches == null)
            {
                if (nextEndpoint >= endpoints.Count) return false;
                batches = source.Client.DoGetAsync(endpoints[nextEndpoint++].Ticket, options, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            }
            if (!await batches.MoveNextAsync())
            {
                await batches.DisposeAsync();
                batches = null;
                continue;
            }
            current?.Dispose();
            current = batches.Current;
            if (current.Schema.FieldsList.Count > 0) schema = current.Schema;
            row = -1;
            if (current.Length > 0) return true;
        }
    }

    public async Task<bool> ReadAsync(CancellationToken ct)
    {
        if (current == null) return false;
        if (++row < current.Length) return true;
        if (await AdvanceBatchAsync()) { row = 0; return true; }
        current = null;
        return false;
    }

    public ValueTask<object?> ValueAsync(int column, CancellationToken ct)
    {
        try { return ValueTask.FromResult(ArrowConversions.Value(current!.Column(column), row)); }
        catch (ArrowTypeNotCarriedException ex) { throw new TransferException($"column `{schema.FieldsList[column].Name}`: {ex.Message}."); }
    }

    public void Cancel() { try { cancellation.Cancel(); } catch (Exception) { /* it ended */ } }

    public async ValueTask DisposeAsync()
    {
        // a statement that still has rows is cancelled instead of drained
        if (batches != null) Cancel();
        try { if (batches != null) await batches.DisposeAsync(); }
        catch (Exception ex) when (ex is OperationCanceledException or RpcException) { /* the cancelled rest */ }
        finally
        {
            current?.Dispose();
            cancellation.Dispose();
            await FlightSqlRowSource.CloseQuietlyAsync(prepared, options, source);
        }
    }
}
