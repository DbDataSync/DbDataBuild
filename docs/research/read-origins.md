# Read-only origins: ODBC, Arrow Flight SQL, DuckDB and configurable dialects

Status: steps 1 to 6 are built (entries 157 to 162; DESIGN.md 9.4.2 and 9.4.3 are the as-built text, OPEN-ITEMS section V what is left). Written from the code as of entry 156.

## 1. The goal

A **copy** can read from a connection that is not one of the three engines the tool builds on (SQL Server, PostgreSQL, Fabric). The new connections are **read-only origins**: they are read, never written, never built on, never tracked on.

* **DuckDB**: a database file, read in process.
* **ODBC**: any database with an ODBC driver.
* **Arrow Flight SQL**: including the owner's query compression proxy.
* **Configurable dialects**, because ODBC and a proxy say nothing about the SQL they accept, and a built-in **ANSI** dialect as the default for them.

## 2. What the code does today (why this is smaller than it looks)

* An origin is a project connection with an `engine` of `sqlserver`, `postgres` or `fabric`. `engine` means two things at once: the **dialect** (quoting, how the catalog is read, which type table) and the **driver** (`Microsoft.Data.SqlClient` or `Npgsql`, in `DbDataBuild.Execution`).
* `ReadSession` wraps an ADO.NET `DbConnection`. `Planner.CopyTransfer` writes the origin's read as text in the origin's dialect (`TargetRegistry.Get(origin.Engine).CreateDdl(...)` supplies the quoting) and the committed load plan carries that text. The refresh sends it; it is never lowered or transpiled.
* The read is a simple shape: `SELECT "a", "b" FROM "schema"."table" [WHERE "wm" >= @watermark]`, or the user's own text for a native origin or a `source` resolver (sent as written).
* Values are converted by the **declared logical type** of the column (`TransferValues.Convert`), not by what the driver returns. So a new driver does not need a type-mapping table to copy. It does to *discover* a table (`project import`, the structure check).
* Every statement goes through `ReadSession`, whose `ReadGuard` refuses anything but a SELECT.

So the dialect an origin needs is **small**: how to quote an identifier, how a parameter is written, a ping, and (optionally) how to read the catalog. It is not the lowering of model queries, because nothing is built on an origin.

## 3. The model: one `engine` setting made of keywords

All of a connection's kind stays in `engine`, as a space- or comma-separated list of keywords (or a YAML list). Each keyword belongs to one closed class, so the order does not matter and nothing is positional:

| Class | Keywords | Meaning |
|---|---|---|
| access | `readonly` | the connection is only read (default: read and write) |
| driver | `sqlserver`, `postgres`, `fabric`, `duckdb`, `odbc`, `flightsql` | how the tool connects |
| dialect | `ansi`, `duckdb`, `sqlserver`, `postgres`, `fabric`, or a name from `dialects:` | the SQL the origin takes |

```yaml
dialects:                        # a profile for a system the tool has no built-in for (section 5), named after the system
  clickhouse_sql:
    extends: ansi
    quote: '`'
    ping: SELECT 1               # parameters are positional (?) in every profile but the three engines'

connections:
  store_east:  { engine: readonly postgres }                 # a shop system the tool only reads (today's `postgres`, plus the guarantee)
  legacy_erp:  { engine: readonly odbc ansi }
  analytics:   { engine: readonly duckdb }                   # a .duckdb file; the data in it
  proxy_erp:   { engine: "readonly, flightsql, postgres" }   # a proxy is several connections: the dialect is the one of the system behind it
  proxy_clicks:{ engine: "readonly, flightsql, clickhouse_sql" }
  warehouse:   { engine: sqlserver }                         # unchanged
```

Rules:

* A driver keyword that is also an engine the tool builds on (`sqlserver`, `postgres`, `fabric`) or DuckDB **brings its own dialect**; a dialect keyword is then not allowed with it (`readonly postgres ansi` is DDB-new: "postgres already has a dialect"). `odbc` and `flightsql` take a dialect and default to `ansi`.
* `odbc`, `flightsql` and `duckdb` need `readonly`. They are origins only; without it the connection is refused with a message that says to add it. (If a later release lets one be written, the rule is lifted in one place.)
* Exactly one driver; at most one access and one dialect. A repeated or unknown keyword is a diagnostic naming the choices.
* A name in `dialects:` may not be a reserved keyword, so the classes cannot collide.
* The three connections named after an engine (`sqlserver`, `postgres`, `fabric`) still exist undeclared.
* `engine` is parsed into three values (`access`, `driver`, `dialect`) that the rest of the code reads; no code sees the string.

`readonly` means: no `_WRITE` login is ever read; the connection cannot be `tracking.connection`, a destination of a model or a copy, or a deploy or refresh target (DDB code, new); `connection inspect`, `status` of structure and `project import` work on it. A copy, a mapped model, a `source` resolver and a native query may name it as an origin. `readonly` also works on the engines the tool builds on, so the fan-in template's shop systems become `readonly postgres`.

The login is `DBDATABUILD_<NAME>_READ`, as now. Its value is whatever the driver takes: an ODBC connection string (or DSN), the path of a DuckDB file (opened `READ_ONLY`), a Flight SQL URI such as `grpc+tls://host:31337` with credentials as parts of the value.

## 4. Drivers

| Driver | Library | Notes |
|---|---|---|
| `duckdb` | `DuckDB.NET.Data.Full` (already a dependency) | In process; the file is opened read-only (`access_mode=READ_ONLY`); dialect is the model dialect itself. Also the easiest way to test the abstraction: no docker. Could also read a Parquet or CSV file through a view (later). |
| `odbc` | `System.Data.Odbc` (an ADO.NET provider) | Needs the platform's driver manager (Windows has one; Linux needs unixODBC) and the vendor's driver on the machine; the tool ships neither. Parameters are positional `?`. The driver decides transactions, snapshots and timeouts. |
| `flight_sql` | an Arrow Flight SQL client for .NET (`Apache.Arrow.Flight` with the Flight SQL protocol, or the ADBC Flight SQL driver; choose when building) | Not ADO.NET: results are Arrow record batches. Needs a small reader abstraction (below). Prepared statements for parameters. Auth: bearer token or user/password handshake; TLS options. |

**Reader abstraction.** `ReadSession` is built on `DbConnection`/`DbDataReader`. DuckDB and ODBC fit. Flight SQL does not, so introduce one interface under `ReadSession`, with an ADO.NET implementation and a Flight SQL implementation:

```csharp
interface IRowSource : IAsyncDisposable        // one open read connection
{
    Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, CancellationToken ct);
}
interface IRowReader { IReadOnlyList<string> Names { get; } Task<bool> ReadAsync(CancellationToken ct); object? Value(int i); }
```

The transfer reads row by row today (`Transfer.cs` converts each value by logical type). A Flight SQL source can later hand over whole Arrow batches and write them with a bulk path (a separate optimisation, not needed first).

`ReadGuard` and `GateInvariantTests` stay as they are: the only way to execute against an origin is `ReadSession`. The read-only property is kept in layers: no write login exists for the connection, `ReadGuard` refuses non-SELECT text, DuckDB opens read-only, and the docs say what the other drivers cannot enforce (the account's own rights are the real guarantee).

## 5. Dialects

A dialect profile is **data**, not code (the repo prefers a table to a switch). What an origin needs from it:

| Field | Meaning | `ansi` default |
|---|---|---|
| `quote` | identifier quote (`"`, `` ` ``, `[ ]`) | `"` |
| `parameters` | `named_at` (`@name`), `positional` (`?`), `numbered` (`$1`) | `positional` |
| `ping` | the statement of `connection inspect` | `SELECT 1` |
| `catalog` | `information_schema` queries for tables and columns, or `none` | `information_schema` |
| `identifier_case` | `preserve`, `upper`, `lower` (what an unquoted name becomes) | `preserve` |
| `limit` | `limit` (`LIMIT n`), `top`, `fetch` (`FETCH FIRST n ROWS ONLY`), for the sample and a bounded read | `fetch` |
| `types` | how the catalog's type names map to logical types (used by `project import` and the structure check only) | a standard table for ANSI names |

Built-in profiles: `sqlserver`, `postgres`, `fabric` (today's, unchanged), `duckdb`, and **`ansi`**. `extends:` lets a project tweak one (`clickhouse_sql` above). The dialect belongs to the system a connection reaches, not to the server in front of it: one proxy is several connections, each naming its system's dialect. A profile that is invalid is a diagnostic at load, with a fixture.

`ansi` is the lowest common denominator: it must work with a `SELECT` of quoted columns and a `WHERE col >= ?`. Anything past that (a function, a hint) belongs in a user's native text, which is sent as written to the origin.

Positional parameters change one thing: the committed `read_incremental` text and a native text with several placeholders bind **by order**, so the plan records the order of the parameters, and a placeholder used twice is passed twice. (Today the text uses `@watermark` once.)

## 6. What changes where

| Area | Change |
|---|---|
| `DbDataBuild.Models` / config | `engine` keywords parsed into access, driver and dialect; `dialects:` at the project; layering as for other connection settings; `schemas/config.schema.json`; DDB codes for: a write login or tracking or a destination on a `readonly` connection; an unknown driver or dialect; an invalid profile |
| `DbDataBuild.Targets` | `TargetRegistry` resolves a dialect profile to a quoting/DDL helper for an origin; the three engines remain `ITarget`s; an origin dialect is **not** an `ITarget` (nothing is rendered for it) |
| `DbDataBuild.Execution` | `IRowSource`; `ReadSession` takes one; `Logins` builds one per driver; `SessionLimits` (`max_parallel`) applies unchanged |
| `Planner.CopyTransfer` | uses the dialect's quoting and parameter style; records parameter order |
| catalog / import / inspect | `SourceCatalogReader` and `project import` read an origin through the profile's `catalog`; DuckDB and `information_schema` first, ODBC `SQLColumns`-style and Flight SQL `GetTables` later if `information_schema` is not offered |
| docs | DESIGN.md, concepts, operations, SKILL.md, a new `help` page on dialects; a diagnostic list |

## 7. Build order

1. **Split and profile.** The `engine` keywords (`readonly`, driver, dialect), the `dialects:` mechanism with `ansi` and `duckdb`, the config checks, `IRowSource` around the existing ADO.NET path. No new driver; the three engines behave exactly as before (the whole suite is the test).
2. **DuckDB origin.** A file as an origin: copy, mapped model, `source` resolver, `project import`, `inspect`. Tests with no docker.
3. **ODBC origin.** Positional parameters through the plan. Test against PostgreSQL through its ODBC driver in the engine container setup (unixODBC and `psqlodbc` in the test image) and, if cheap, a second driver.
4. **Flight SQL origin.** The `IRowSource` over a Flight SQL client; auth and TLS settings; tests against an open-source Flight SQL server (a DuckDB or SQLite one in docker) and against the owner's proxy.
5. **Catalog on the new origins** (structure check DDB-230 per origin, `import`) where `information_schema` is not offered. Built (section 8c).
6. **The dialect from the server** (planned, section 10).

Each is a separate agent run on a branch, as for the refresh plans, with the unit suite and the conformance groups after each.

## 8. Decided, and still open

Decided by the owner:

* The kind of a connection stays in `engine`, as keywords (section 3). Properties remain possible beside it.
* The proxy supports prepared statements, so positional parameters work for Flight SQL.
* The proxy authenticates with a bearer token, user and password, or (the owner believes) Entra ID with OIDC. The login value carries the method; token and password forms first, Entra ID/OIDC after the Flight SQL driver works (needs a token source: a command or the environment).
* ODBC: documented as needing the platform's driver manager (unixODBC on Linux and macOS) and the vendor's driver; `connection inspect` says clearly which is missing.
* Snapshot and parameter order: the owner has no view; the build documents a copy's snapshot as the origin's behaviour, and records the order of positional parameters in the plan (a placeholder used twice is passed twice).
* DuckDB: the data in a `.duckdb` file only. Parquet and CSV are reached by a user-written source query of a copy (native query) against the origin, not by a feature of the driver.

Still open:

1. **The proxy's auth** (bearer token, user and password, mTLS) and **its SQL**: settled by section 8c, the dialect is the one of the system behind each connection, and the login selects it with `header.x-datasource=<name>`.
2. **Parquet through DuckDB.** A native copy needs an origin connection. For parquet alone, does a `readonly duckdb` connection whose login is `:memory:` do (a database with no file; the query reads the parquet)? It should; it is checked when DuckDB is built.
3. **ODBC packaging.** The tool ships no driver manager or drivers. OK to document "install unixODBC and your vendor's driver", and have `connection inspect` say clearly when either is missing?
4. **Snapshot.** A copy's read is "a snapshot where the engine offers one". For ODBC and Flight SQL the tool cannot know. OK to document it as the origin's behaviour?
5. **Parameter order.** Positional `?` records the order of parameters in the plan; a placeholder used twice is passed twice. Confirm.
6. **A destination later?** `readonly` is a keyword, so a later release can allow writing DuckDB or ODBC by dropping the rule in section 3.

## 8a. As built, step 3 (the ODBC origin, entry 159)

What was built is what section 4 says, with these facts the build settled:

* **The login** is an ODBC connection string or a DSN with its credentials; a bare word is read as `DSN=word`. It reaches the driver manager as written. Only the user (`UID`, `User`, `Username`, `User Id`) is read from it, for the command header.
* **The source** is `OdbcRowSource` over `System.Data.Odbc` 10.0.13. Positional `?` values come in the order of the plan's `parameter_order`; the ADO types are mapped to ODBC's (`DateTime2` to `DateTime`; text as `NVarChar`). A native text is described with the driver's schema-only mode and the ODBC types are named as `information_schema` names, so one type table (the profile's `types:`) serves both ways of finding a column's type.
* **The catalog** is `information_schema` (columns, primary keys, foreign keys; no indexes) when the profile says so; `catalog: none` makes `project import` say it plainly, `connection inspect` say the schema names were not read, and the structure check of a copy skipped with a note. `ConnectionConfig.Profile` carries a project's own dialect to every login, which step 1 had not needed.
* **Missing prerequisites**: `DriverFailure` (Execution) names a missing driver manager (`DllNotFoundException`) and a missing or unloadable driver (SQLSTATE `IM002`, `IM003`, unixODBC's `01000` "Can't open lib", `IM004`, `IM010`, `IM012`, `IM014`) in a sentence of ours; any other failure is the exception type and the SQLSTATE. It is used by `connection inspect`, the check of the origin before a copy's transfer and a refresh (so the copy ends `failed: origin`), a session that cannot be opened and the resolvers.
* **psqlodbc hands a boolean over as text** unless `BoolsAsChar=0` is in the connection string; the copy reads `t`/`f` (and `true`/`false`, `1`/`0`, `y`/`n`) as well, so the default driver setting works.
* **Tested against** PostgreSQL 17 through psqlodbc 16 and unixODBC 2.3.12 into SQL Server and PostgreSQL. No second driver and no Windows or macOS driver manager was tried.

## 8b. As built, step 4 (the Flight SQL origin, entry 160)

* **Client**: `Apache.Arrow.Flight.Sql` 23.0.0. Evaluated: the ADBC Flight SQL driver (`Apache.Arrow.Adbc.Drivers.FlightSql` 0.21.0) is a managed wrapper over `Apache.Arrow.Flight` (22.1) with the ADBC layer on top; it adds nothing the tool needs (the tool has its own row reader) and trails the Flight packages. Both are managed, so the single-file self-contained publish is unaffected. Its prepared-statement class and its metadata and timeout members have faults (see DESIGN.md 9.4.2), so the protocol messages are used directly where it matters.
* **Login**: a URI with options (`grpc+tls://host:port;token=...`, `;user=..;password=..`, `token_env`, `token_command`, `ca_file`, `tls_skip_verify`, `header.<name>`), documented in operations.md ("Flight SQL setup").
* **Entra ID / OIDC**: no flow is built. A token obtained outside the tool is used through `token`, `token_env` or `token_command`; the rest is in OPEN-ITEMS V.
* **Tested against** voltrondata/sqlflite (C++ Flight SQL server, DuckDB, plain and TLS, JWT after the basic handshake) and an in-process server (DuckDB, typed parameters, no metadata calls). sqlflite binds every parameter as text.

## 8c. As built, step 5 (the catalog on the new origins, entry 161)

* **`catalog: odbc`** is a third choice of the profile (`information_schema`, `odbc`, `none`; `ansi` stays `information_schema`), for an `odbc` connection only (DDB-456 on any other). The driver's own catalog functions are called through the driver manager directly (`OdbcCatalogClient.cs`: `SQLTables`, `SQLColumns`, `SQLPrimaryKeys`, `SQLForeignKeys`, `SQLStatistics`), because `System.Data.Odbc`'s `GetSchema` has no keys; a connection of its own, the same login, no statement executed (a test scans the file). Used by `project import`, the DDB-230 check and `connection inspect`.
* **Indexes** are written by `project import` from `duckdb_indexes()` (DuckDB) and `SQLStatistics` (any ODBC source, whichever catalog its dialect names); Flight SQL has no metadata call for them and `information_schema` has no view, so the import says so in words (`catalog_notes`).
* **DuckDB foreign keys**: `duckdb_constraints()` has no referenced-schema column, its constraint text has the qualifier and is read; DuckDB 1.5 refuses foreign keys across schemas, so the answer is always the key's own schema (pinned by a test).
* **`limit`** and **`identifier_case`** act where they are meaningful: `limit` writes the one bounded read the tool makes (the zero-row read that describes a native query when the driver has no schema-only mode); `identifier_case` finds a declared table that is stored in the other case in the structure check and says how to spell it. Neither is applied to a copy's read (that would change `readonly odbc postgres`). No sample, preview or row count of an origin exists, so there is no other bounded read.
* **A built-in engine dialect with `odbc` or `flightsql`** (`readonly flightsql postgres`) was accepted by the keyword rules but did not work: it bound `@name` and had no type names. Fixed: those drivers read it with positional parameters and the engine's type names on top of the standard ones.
* **What the proxy offers** (github.com/DbDataSync/DbDataProxy, read with `gh` on 2026-10-14; no release yet, parameters in progress): Flight SQL over SQL Server, DuckDB and ODBC data sources; the data source is chosen per call by the `x-datasource` header (so `header.x-datasource=<name>` in the login URI; one proxy is several connections); parameters are `?` bound by position through prepared statements (`@name` also on SQL Server); metadata calls answered: `GetCatalogs`, `GetDbSchemas`, `GetTables` (with each table's Arrow schema), `GetTableTypes`, `GetPrimaryKeys`, `GetSqlInfo` (server name, server version, Arrow version, read-only `false`, SQL `true`, Substrait `false`; no identifier quote character, no dialect name); not answered: `GetImportedKeys`, `GetExportedKeys`, `GetCrossReference`, `GetXdbcTypeInfo`. Tables and schemas come from `information_schema` for SQL Server and DuckDB sources and from `SQLTables` for ODBC sources; an ODBC source reports no primary keys (an empty answer). So the tool's foreign keys through the proxy come from `information_schema` over the query path (SQL Server and DuckDB sources only).

## 9. Not covered

Pushing a model's query down to an origin (the origin runs a DuckDB-dialect query lowered for it): out of scope, as "no DuckDB in the middle" and native queries already decided. Writing to any of these. A catalog of every ODBC driver's quirks: a profile per system is the user's.

## 10a. As built, step 6 (the dialect from the server, entry 162)

Built as section 10 proposed, with the owner's decisions of 2026-10-14: the command is `project update` (not `connection inspect --adopt-dialect`), the file is `dialects.lock.yml`, and the proxy was read for the ids it sends. The facts the tool reads are the standard ids 0, 1, 3, 503, 504 and 505; the proxy sends them per data source but no dialect name, so the **product name** picks the dialect (a table of fragments in `DialectDiscovery`, or a name from `dialects:` that the product spells), and a product the tool does not know is `ansi` with the reported quote character (a dialect name the tool does not know is `ansi` with the facts, not an error: the person adds a profile named after the product, or names the dialect in `engine`). Plans and `loads.yml` carry `dialect` and `dialect_hash`; the refresh's and the deploy's origin check compare the hash and fail the origin (DDB-460). DESIGN.md 9.4.3 has the details.

## 10. Step 6 as it was planned (kept for the reasoning; built, see 10a)

Today `engine: readonly flightsql` with no dialect keyword is `ansi`, and a person names the dialect of the system behind a proxy by hand. The proxy can report enough for the tool to choose.

* **When**: `readonly flightsql` with no dialect keyword asks the server (Flight SQL `GetSqlInfo`) for: the identifier quote character (`SQL_IDENTIFIER_QUOTE_CHAR`), the identifier case (`SQL_IDENTIFIER_CASE`, `SQL_QUOTED_IDENTIFIER_CASE`), the server name and version, and a key the proxy defines for the name of the dialect behind the data source (its id is the proxy's to publish; a Flight SQL server may use ids of its own). An explicit dialect keyword always wins and nothing is asked. If the server says nothing usable the dialect is `ansi`, as now.
* **Mapping**: a dialect name the server sends that is a built-in or a name in the project's `dialects:` is that dialect; otherwise the facts it did send build a profile on `ansi` (quote character, case; `limit` stays `fetch` unless the dialect name says otherwise). `connection inspect` prints what the server reported and which dialect was chosen and why.
* **Recorded, not rediscovered**: the committed read text of a copy depends on the dialect (quoting, placeholders), and `project compile` is offline, so discovery is an explicit step by a command that opens the connection (proposal: `connection inspect <c> --adopt-dialect` writes `dialects.lock.yml`: the dialect chosen, the facts reported, a hash of the profile). Compile, plans and `loads.yml` use the lock exactly as they use a keyword, and the plan and `loads.yml` carry the dialect name and profile hash beside `parameter_order`. A refresh asks the server again as part of its ping before an origin's turn; if the answer differs from the lock, the origin fails (`failed: origin`) naming what differs, and the text is never changed silently. Alternative for the owner: record it in the plan only (`connection deploy --write-plan`), which then cannot serve a refresh without a deploy.
* **What the proxy would need to send** (a contract for its repo; it sends today the server name, version, Arrow version, read-only, SQL and Substrait flags): answers to `GetSqlInfo` that are per data source (the same `x-datasource` header as every call): `SQL_IDENTIFIER_QUOTE_CHAR`, `SQL_IDENTIFIER_CASE`, `SQL_QUOTED_IDENTIFIER_CASE`, and a custom id carrying the dialect name of the system behind the data source (`postgres`, `sqlserver`, `duckdb`, or the ODBC driver's product name), optionally the parameter style and the limit style. Also useful, not required: `GetImportedKeys` for SQL Server and DuckDB sources and primary keys for ODBC sources (its README says they report none).
* **Open for the owner**: lock file or plan-only; the id of the custom key; whether a dialect name the tool does not know is an error or `ansi` with the facts.
