# Read-only origins: ODBC, Arrow Flight SQL, DuckDB and configurable dialects

Status: steps 1 to 3 are built (entries 157, 158 and 159; DESIGN.md 9.4.2 is the as-built text, OPEN-ITEMS section V what is left). Steps 4 and 5 are not. Written from the code as of entry 156.

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
dialects:                        # a profile for a system the tool has no built-in for (section 5)
  proxy_sql:
    extends: ansi
    quote: '`'
    ping: SELECT 1               # parameters are positional (?) in every profile but the three engines'

connections:
  store_east:  { engine: readonly postgres }                 # a shop system the tool only reads (today's `postgres`, plus the guarantee)
  legacy_erp:  { engine: readonly odbc ansi }
  analytics:   { engine: readonly duckdb }                   # a .duckdb file; the data in it
  proxy:       { engine: "readonly, flightsql, proxy_sql" }
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

Built-in profiles: `sqlserver`, `postgres`, `fabric` (today's, unchanged), `duckdb`, and **`ansi`**. `extends:` lets a project tweak one (`proxy_sql` above). A profile that is invalid is a diagnostic at load, with a fixture.

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
5. **Catalog on the new origins** (structure check DDB-230 per origin, `import`) where `information_schema` is not offered.

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

1. **The proxy's auth** (bearer token, user and password, mTLS) and **its SQL** (plain ANSI, or what is behind it): decides the `proxy_sql` profile and how the login value is written.
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

## 9. Not covered

Pushing a model's query down to an origin (the origin runs a DuckDB-dialect query lowered for it): out of scope, as "no DuckDB in the middle" and native queries already decided. Writing to any of these. A catalog of every ODBC driver's quirks: a profile per system is the user's.
