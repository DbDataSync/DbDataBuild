# Running queries on one server and writing the results to another

Status: investigation, with experiments on real engines (entry 62 of `docs/progress/state-and-apply.md`), and the design the owner and I settled on (entries 63 and 64). **Built since** (entries 66 to 75): connections and the rename, layered project files and the merge rules, mapped models, `import`, copies with a bulk gate statement, fan-in with slices and connection parameters, incremental copies, central and optional tracking. **Not built yet**: parameters that change object names, `schema` as a setting, `copy_to` (records replicated to further connections), a plan-time origin check for an origin that is a built model. The aim is to support these scenarios. Sections: the direction, what the code has, what was measured, the scenarios, then the **terms and design** (from "Terms" down), which is the current
proposal; where the scenario table below speaks of links or DuckDB, the direction above already ruled them out.

## Direction (the owner's decisions)

- **No linked servers or foreign servers.** **No DuckDB in the middle** as a load path. **Same-server cross-database queries are not a feature** (views or synonyms, outside the tool).
- **The tool moves the data**: read on one connection, bulk write on another, through the gate.
- **A clean break**: no compatibility with the current layout; the schemas, keys and directories below replace today's.
- **One root, no grouping above it**: the **project** stays the unit; there is no domain, context or workspace layer.
- **Reusable, general terms** over special ones (a fan-in is not a special feature; see Parameters).

## Words

Today three things are the same place, the **target**: where a model's query *runs* (compute), where its result *lives* (store), and where its inputs live (sources). A target is an engine kind
(`sqlserver`, `postgres`, `fabric`) with one read login and one write login in the environment (`DBDATABUILD_<TARGET>_<READ|WRITE>`), so two servers of one engine cannot even be told apart.
Cross-server work pulls those three apart.

## What the code already has, and what stands in the way

| | State |
|---|---|
| Read path | `ReadSession` over a read login, with `ReadGuard` (SELECT only). Returns rows as objects; it is built for catalog reads and `diff`, not for streaming a table. |
| Write path | `MutationGate` is the only way to send a statement to a target (`GateInvariantTests` scans the source). It takes **text statements**; the one bulk form is `GateStatement.BulkInsert`, a multi-row `INSERT` with a parameter per value (used by `load-seeds`). The log records hashes and counts, never values. |
| Targets and logins | by engine kind; no named connections; a source-only connection (read, never written) has no place. |
| Sources | `sources/<schema name>/<table>.yml` has no location: a source is a table of the target. Descriptors are read from the target by `import-sources`. |
| Names in a model | `schema_name.table_name`. A reference with a catalog (`crm.public.customers`) is refused by the lowering (DDB-324, checked), and a four-part SQL Server name does not parse in DuckDB at all. A logical-to-physical name mapping at render time does not exist. |
| Plan and apply | one target per plan; the plan holds the exact statements and the shapes it assumed of the target's objects (drift is checked against that one target). |
| Offline binding | DuckDB binds every query against declared columns only, so remote tables need only descriptors, not access. |

## What was measured

All on local containers (SQL Server 2022 and 2025, PostgreSQL 17), 1,000,000 rows of four columns unless stated. Local numbers say what the mechanism costs, not what a network does.

| Mechanism | Result |
|---|---|
| **SQL Server, other database on the same instance** (three-part name), inside `BEGIN TRANSACTION` with `XACT_ABORT ON` | works, pull and push |
| **PostgreSQL, other database**, plain `SELECT * FROM otherdb.s.t` | `cross-database references are not implemented`: needs `postgres_fdw` or a schema |
| **SQL Server linked server, pull** (the script runs on the destination and reads `LINK.db.dbo.t`), inside a transaction | works; 1M rows in **8.5 s** |
| **SQL Server linked server, push** (the script runs on the source and writes `LINK.db.dbo.t`) | works outside a transaction; **inside one it fails** (`Msg 7391 ... unable to begin a distributed transaction`): the rendered load scripts are transactional, and a Linux SQL Server has no MSDTC (a Windows one might, configured) |
| **PostgreSQL `postgres_fdw`** (stock image; `CREATE EXTENSION`, `IMPORT FOREIGN SCHEMA`), pull and push inside transactions | both work; a rolled-back push leaves the remote table untouched; there is no two-phase commit, so a failed commit after the remote one cannot be undone |
| **DuckDB in the middle** (`ATTACH` PostgreSQL with the official extension, SQL Server with the community `mssql` extension, `CREATE TABLE ... AS SELECT` into the other server) | works: PostgreSQL to SQL Server, 1M rows in **5.1 s**; SQL Server to SQL Server **7.2 s**; the query is DuckDB's, so no engine rewrites apply; text became `nvarchar(max)`, `numeric(10,2)` became `decimal(10,2)` |
| **Client copy, reading PostgreSQL** with Npgsql | 200k rows into memory in 0.4 s |
| **Client copy, writing SQL Server with `SqlBulkCopy`** | 200k rows in **1.0 s** (about 190k rows/s) |
| **Client copy, writing with parameterised multi-row `INSERT` batches** (what `GateStatement.BulkInsert` is) | 50k rows in **4.8 s** (about 10k rows/s): **twenty times slower**, so a transfer needs a real bulk API, not `BulkInsert` |
| Driver types | Npgsql returns `date` as `DateOnly`, SQL Server's driver wants `DateTime`: the values need converting by declared type, and .NET `decimal` holds 28 digits while SQL Server and PostgreSQL go to 38 |

Reproduce: `scripts/test-engines.sh up mssql mssql2025 pg`; a linked server is `sp_addlinkedserver ... @provider='MSOLEDBSQL', @datasrc='<container ip>'` plus `sp_addlinkedsrvlogin`; DuckDB is
`INSTALL postgres; INSTALL mssql FROM community; ATTACH '...' AS src (TYPE postgres); ATTACH '...;Encrypt=true;TrustServerCertificate=true' AS ms (TYPE mssql);`.

## The scenarios, and what each needs

Scenarios 1 to 3 and 6 are out by the owner's direction (links, same-server cross-database, DuckDB in the middle). 4 and 5 are what the design below builds (5 as an ordinary model on the origin connection, then a copy); 7 stays a documentation note.

| # | Scenario | Mechanism | Fit today | What it needs |
|---|---|---|---|---|
| 1 | Sources in another database or schema name of the same server | the engine's own cross-database name | none (the names are refused) | a source location (`database`), a physical name at render time; SQL Server only (PostgreSQL needs 2) |
| 2 | Sources on another server of the same engine, joined in the destination's script (**pull**) | linked server (SQL Server), `postgres_fdw` (PostgreSQL) | none | as 1, plus a prerequisite the tool checks but does not create (a link is privileged and holds a login) |
| 3 | Compute on the source server, store the result on another (**push**) | a link, written from the source | none | works on PostgreSQL; **not** on SQL Server inside a transaction: do it as a pull from the destination, or move the rows with 4 |
| 4 | Any engine to any engine, or servers that cannot see each other | the tool moves rows: read on the source connection, bulk write on the destination | none | named connections, a `transfer` step, a bulk path in the gate (below) |
| 5 | Compute on the source engine (a big aggregate there, only the result travels) | 4 with the model's query rendered for the **source** engine | none | the model says where it computes (`compute`) and where it is stored; types are mapped for the store |
| 6 | DuckDB computes, reading sources through its extensions | DuckDB attaches the sources, the tool writes the result | partly (`sample` runs DuckDB; `load-seeds` writes DuckDB rows through the gate) | not recommended as the main path (below) |
| 7 | Read replica for sources, primary for the result | two logins | already two logins, but plan, drift and `diff` read the *target's own* state through the read login | not a data-movement feature; a replica as the read login makes drift checks lag; say so in the documentation |

## Terms

| Term | Means |
|---|---|
| **Connection** | A named database endpoint: an engine and a login (`DBDATABUILD_<NAME>_<READ\|WRITE>`). Data lives only on connections. A connection with no write login can only be read. |
| **Engine** | The SQL dialect of a connection (`sqlserver`, `postgres`, `fabric`). The matrix, the rules and the type mapping are per engine. This is what the old word "target" mostly meant. |
| **Model** | One named thing in the project, with declared columns, that lives on a connection (or on several: below). Every kind below is a model. |
| **Kinds that build from SQL** | `view`, `full`, `incremental_by_unique_key`, `incremental_by_time_range`, as today: SQL in DuckDB's dialect, run on the model's connection. |
| **`mapped`** | A model with no body: it **maps** an existing physical table into the project's namespace. The tool never creates or alters it; it declares the columns, optionally the physical name, keys, indexes and tests, and the live table is checked against it. What the old "source" was. A `sources/` folder is only a place to keep them. |
| **`copy`** | A model with no SQL: `from: <model>`, the connection it lives on, and a strategy. Its columns come from the model it copies. The copy is always persisted (a table). |
| **Schema name** | The namespace an object lives in inside a database (`marts` in `marts.fct_orders`): what DBAs usually call a "schema" (SQL Server, PostgreSQL). Always written **schema name** in this project's documents, code comments and discussions. |
| **Schema** | Only the defined shape of a table or view: its columns, types, nullability and other metadata ("the schema of `marts.fct_orders`", "schema drift", a JSON Schema). Never a namespace. |
| **Model name** | `schema name` + `.` + `object name`, as in `marts.fct_orders`; the object name is the part after the last dot. |
| **Model kind** | Which of the seven kinds a model is: `view`, `full`, `incremental_by_unique_key`, `incremental_by_time_range`, `copy`, `mapped`, `native`. The only meaning of `kind` in documents, columns, selectors (`kind:`) and diagnostics; "type" is a column's SQL type and is never used for a model. |
| **Dependency category** | What a model depends on, as a sort of thing: another `model` (whose model kind says which of the seven), a `macro` of the project, or an `unknown` table that is none of the project's. `depends_on_category` in the tracking table and `category` in documents and the graph; the model kind is `depends_on_kind` / `kind`, null for a macro or an unknown table. |
| **Project** | Everything under the root `dbdatabuild.yml`: its models, tests, rendered files and plans. |
| **Project file** | `dbdatabuild.yml` at the root, or `_dbdatabuild.yml` in any folder: the same kind of file with the same sections. The root one is only the outermost; the others refine it for what is beneath them. |
| **Parameter** | A named value. The same word at every level; the scope says whose it is (below). |

Older text in this note, written before the terms above, says "schema" where it means a schema name (`schema:` as a setting is the parked schema-name setting).

There is no separate concept for "a group of connections": what a fan-in needs is a model that exists on several connections, which a model's list of connections already says (below).

**The rule behind all of it: a query runs against one connection.** SQL in a model reads only models that live on the model's own connection; a reference to a model on another connection is refused,
by name, with the fix (copy it first). Moving data between connections is always an explicit `copy`. That removes the question of joins across servers and of "sources times targets": a copy names
its origin and where it lives, and nothing flows that is not written. A transformation happens at the origin (an ordinary model on that connection, then copied) or at the destination (an ordinary
model reading the copy); a copy itself has no SQL.

**Landing raw data and sending on a result are both copies.** Whether the origin is a `mapped` table or a model the project builds is a property of the origin.

**`connections` on a model** replaces `targets` and means the same thing for every kind: *the model is on each of these*. A built model is **built natively on each** (portability: the engine's rules apply,
results can differ by engine). A `mapped` model **exists on each** (the same table on several systems). A `copy` is **copied to each** of its connections, or **from each** of its origin's (below).

**A copy has at most one dimension of expansion.** One origin to several connections is fan-out; several origins to one connection is fan-in; several of both would be a product, and the tool refuses it
("write one copy per destination"). Nothing in the project is ever the product of two lists unless a person wrote both sides.

## Configuration and inheritance

Every project file has the same sections (`connections`, `parameters`, `defaults`, `rewrites`, `policy`, `string_semantics`, `lint`, hook definitions, ...). Beneath a folder, the files from the root down
to that folder are **layered**, then the model's own file on top, and **the nearest wins**:

```
dbdatabuild.yml                       # the root project file: connections, parameters, defaults, policy, string_semantics, rewrites
models/
  crm/
    _dbdatabuild.yml                  # connection: crm_pg ; kind: mapped ; schema: crm
    customers.yml                     # a mapped model: columns, grain, physical name
    orders.yml
  warehouse/
    _dbdatabuild.yml                  # connection: warehouse ; schema: raw ; parameters: { region: eu }
    customers.yml                     # kind: copy ; from: crm.customers ; strategy: full_replace
    marts/
      _dbdatabuild.yml                # kind: full ; rewrites: { fidelity: native } ; parameters: { region: eu-west }
      dim_customer.sql
      dim_customer.yml
```

- **Merge rules**, one table for every layer, tested. By default a layer **merges** into the one above it: a map by key (recursively, the nearer layer wins a conflict), a list by appending (the inherited
  items first, then the layer's own, a repeated scalar kept once; a list of mappings that have a `name` merges by `name`), a scalar by replacing. A suffix on the key changes that for this key only:
  **`key=`** replaces the inherited value whole (a reset), **`key-`** removes the listed items or keys from it, and **`key+`** says "merge" explicitly (the default, for readers). A suffix on a scalar
  (other than `=`) is an error. The schemas accept the suffixes on exactly the keys that inherit, and `validate` shows the result with the file each part came from.
- **Layered**: `connection(s)`, `kind` and its settings (strategy, key, time column), `schema`, `rewrites`, `lint`, `policy`, `string_semantics`, `tags`, `hooks`, and the project and connection `parameters` (below).
- **Not layered, only in the model's own file**: `name`, `columns`, `grain`, `from`, and the **model's parameters**: what a model uses of its own can always be read in its file.
- **Declared once** (a folder file cannot change them, and a conflict is an error naming both files): which connections exist and each one's `engine`, and the `tracking` section.
  What a folder file *can* do for a connection is its `parameters` (below).
- **Provenance is never hidden**: `validate` prints each model's effective settings and the file each came from (the project already prints its effective configuration), and the metadata JSON carries the same.
  Action at a distance is the risk of any inheritance; this is the control.
- **Names**: the `schema` part of a model's name is the `schema:` setting (layered; by default the first folder under `models/`), so folders can be organised by system, layer or anything else.
  Two models cannot have one name; a mapped model's physical name may differ from its project name (`physical: {schema: dbo, table: CUSTOMER_MST}`).
- The underscore file is not a model: the loader and the orphan check skip it.

## Parameters

One concept, four scopes. The key is `parameters:` wherever it is declared; where it is declared decides whose it is, and a reference carries the scope.

| Scope | Declared in | Referenced as | Used for |
|---|---|---|---|
| project | `parameters:` in any project file (root or `_`); layered like any setting, so a model sees the root's merged with every folder file above it | `${project.name}` | values shared by a project or a part of it (a region for a folder) |
| connection | `connections.<name>.parameters:` in any project file, merged by key the same way; the model sees the connection's parameters as layered for its folder | `${connection.name}` | values that differ per connection (a store id): the connection the model lives on |
| origin | (not declared: the connection parameters of the connection a **copy** reads from) | `${origin.name}` | fan-in: the value of the system a row came from |
| model | the model's own file, and only there | `${model.name}` | values that differ per model |
| operation | a load operation (as today) | `@name` in the operation's SQL | runtime values: a watermark, a backfill start |

- **A reference carries its scope, so nothing shadows anything**: `${project.region}` and `${model.region}` are two values.
- **Two ways a value is used, and they are not the same**: a project, connection, origin or model parameter is substituted **into configuration** (a column added by a copy, the value of a slice, a schema name) when the
  project loads, and `validate` shows the result. An operation parameter is **bound** by the driver at run time and never written into statement text. A value that reaches a statement (a slice's value in the
  `DELETE` and the `INSERT`) is bound, not concatenated: the principle that statement text never contains values holds for every scope.
- **Parameters in a model's SQL** are **not in the first version and are required before this work is complete** (decided). Two stages: first as **values** (a literal in a predicate or projection, bound as a driver
  parameter in the rendered script, never written into the text, and typed so DuckDB can still bind the query offline); later as **names** (a parameter that changes a schema or table name, resolved when the project
  loads, so the lowering, the matrix and the rendered files see the final name). Until then model SQL is plain DuckDB. The seeds' `scale` and `seed` are DuckDB variables of the seed queries and stay as they are.

## Fan-in: one application, many deployments

The same table exists on several systems (stores running one application). With the concepts above, nothing is added except the `origin` scope:

```yaml
# dbdatabuild.yml
connections:
  store_017: { engine: postgres, parameters: { store_id: "017" } }
  store_018: { engine: postgres, parameters: { store_id: "018" } }
  warehouse: { engine: sqlserver }

# models/stores/_dbdatabuild.yml        everything under this folder is a mapped table that exists on each store
kind: mapped
connections: [store_017, store_018]
schema: pos

# models/stores/orders.yml              the declaration, once: columns, grain
columns: [ ... ]

# models/warehouse/orders_all.yml       one copy per origin connection, into one table
connection: warehouse
kind: copy
from: pos.orders
strategy: full_replace
slice: { column: store_id, value: "${origin.store_id}" }     # which rows are this origin's; the column is added if the data does not have it
```

- A folder's `connections` list is the whole "group": change the list, and every mapped model under it, and every copy of them, follows. No second place names the members.
- **Parameters are general**: a value per connection that a copy can add as a column (`add: { region: "${origin.region}" }`), use as the slice of a replacement, and, later, any other place a setting differs per
  connection. **No new column is dictated**: if the systems already carry a distinguishing column (a site code in the data), the slice names it and the parameter only says which value is this origin's; if
  they do not, the slice adds one from a parameter. The destination key must include the slice column (`validate` checks).
- **Isolation**: an origin's run replaces only its slice (delete where the slice column equals the value, then insert), so one failing or offline origin leaves the others' data untouched. The check that
  every row of an origin's extract carries that origin's value is made before the swap: an origin cannot write into another's slice.
- **Version skew**: `plan` compares each origin's live table with the declaration and reports every origin that differs, by name; `on_mismatch: fail | skip` (default `fail`) decides, never silently.
  Each origin's last good run is recorded in the tracking tables, and a skipped or failed origin shows in `report`.
- Credentials are per connection (`DBDATABUILD_STORE_017_READ`), fine for tens of systems; hundreds is a later problem.

## Tracking

**Decided.** Tracking data (what `init` creates today in each target) is no longer tied to the connection it describes. **Nothing is tracked unless a project says where**, and a project that writes to a
connection without saying gets a **warning** on every command that would have recorded something. **A configured tracking connection that cannot be reached is an error**, before anything is touched.
Offline tracking and catching up afterwards are a later scenario.

```yaml
# dbdatabuild.yml
tracking: { connection: audit, schema: dbdatabuild }       # the default for every connection that is written
connections:
  warehouse: { engine: sqlserver }                          # tracked on audit (the default above)
  scratch:   { engine: postgres, tracking: none }           # an explicit choice: not tracked, no warning
  vendor:    { engine: sqlserver, tracking: { connection: vendor_audit } }
  audit:     { engine: postgres }
```

- **Where**: `tracking.connection` and `tracking.schema` at the project level (root only: declared once), overridable per connection, never per folder (one plan never reads several tracking stores for one
  connection). A tracking connection needs a read and a write login like any connection that is written. A connection that is only read (a `mapped` origin) is never tracked and never warned about.
- **Every tracking row names the connection it is about** (`connection` is part of each key). That is the only change to the tables. One tracking connection can hold any number of connections' records, and
  `metadata_current` over it is one catalogue of everything the tool built.
- **Many places** (accumulation) is a list of further connections the same rows are copied to, `tracking.copy_to`, idempotent and resumable (the rows are keyed; an update, such as an acknowledgement, replaces by key). The first
  place named is authoritative; the others are replicas. Not needed for the first version.
- **`init`** creates the tracking tables once per tracking connection (effect class: tracking tables only), not once per data connection.
- **A plan's steps name their connection**: data steps on the model's connection, `track` steps on its tracking connection; `apply` holds one gate per connection, checks that every connection it will
  use answers **before** the first statement (the error above), and records "started" before and the outcome after on the tracking connection, exactly as today (the crash window and its reconciliation
  against the live shape are unchanged: a "started" with no outcome is found and checked). The application lock stays a lock on the data connection (a session lock needs no table).

**What the tool cannot do with no tracking** (the warning says so; this is the list): detect drift (there is no recorded baseline, so a change made outside the tool is indistinguishable from a model change: plans
are made from the declared shape against the live one); block an incremental model after a change to its query (DDB-431 compares the definition hash of the last run); keep an acknowledgement (`ack`) or
a block; keep the history, the column-history report, `report` and `publish-metadata`; and verify a plan's recorded base against a recorded shape (it still verifies it against the live shape at apply).
Planning and applying still work, and the risk classes of the decision table still apply. Whether plans without a baseline should treat every change to an existing object as risky is open (below).

## Execution (unchanged from the earlier proposal)

A copy plans as a `transfer` step: the plan records the origin connection, the exact read (hashed like any statement), the staging and destination DDL, and the origin's declared and live shape (a
changed origin is a stale plan). The rows are read through `ReadSession` on the origin connection, converted **by declared logical type** (not the driver's: Npgsql's `DateOnly`, .NET's 28-digit
`decimal`; a value that does not fit is an error naming the column), and written to a staging table on the destination through a new gate statement, `GateStatement.BulkCopy`, implemented with
`SqlBulkCopy` and PostgreSQL's binary `COPY` (190k rows/s measured, against 10k rows/s for the gate's parameterised inserts). The log records the destination, the hash of the read and a row count, never
values. The swap or merge from staging into the destination table is then the existing strategy (`full_replace`, the incremental ones) inside one destination transaction, with indexes, drift and `ack` as
today. The read of the origin is one query on one connection (a snapshot where the engine offers one). Staging has a deterministic name, so an interrupted run is run again. `GateInvariantTests` is
extended to the new statement; there is still no other write path.

## What the clean break changes

| Today | Becomes |
|---|---|
| `targets:`, `default_targets:` | `connection(s):` on a model; `defaults: { connection: ... }` |
| target = engine kind; one read and one write login per engine | named connections, each with its engine; `DBDATABUILD_<CONNECTION>_<READ\|WRITE>` |
| `sources/` directory, source descriptors, `import-sources` | `mapped` models anywhere under `models/`; `import` generates mapped models from a connection's catalog |
| `--target`, `rendered/<engine>/`, `plans/<engine>/` | `--connection`, `rendered/<connection>/`, `plans/<connection>/` |
| the matrix per target | the matrix per engine (unchanged), looked up through the connection's engine |
| name = path under `models/` (`schema_name.table_name`) | `schema` from the setting (default: first folder); name unique in the project |
| `string_semantics`, `rewrites`, `policy` at the project only | defaults that any folder or model can override |
| `tracking_schema`; tracking tables in each target; one plan, one target | `tracking.connection` and `tracking.schema`; tracking rows keyed by `connection`; a plan's steps name their connection |

Everything that reads "target" in the code, the schemas, the documentation and the skill changes; this is a major version (a new release line), not a patch.

## Order

1. **Connections and the rename** (`target` to `connection` and `engine`), model kinds `mapped`, inheritance with `_dbdatabuild.yml` and provenance, `import`. Larger than it sounds: it touches every place that treats a target as an engine. No data movement yet.
2. **`copy` between two connections**: `BulkCopy` in the gate, the transfer step in plan and apply, value conversion, staging and swap, origin shape in the plan; real-engine tests (SQL Server and PostgreSQL both ways: nulls, text, dates, decimals up to 38, large rows).
3. **Origins, connection parameters and slices**: fan-in with per-origin replacement, version-skew reporting.
4. **Incremental extraction** (a watermark on the origin read); **compute at the origin** (a model that lives on the origin connection, then copied, already covers it: this is only convenience).

## Still open

T1 to T3 are **decided** (the owner accepted the defaults): with no tracking, plans are made from declared against live and an `ALTER` or a drop of an existing object is marked risky; `tracking: none` is the explicit
    opt-out; the first version ships no tracking copies to further connections (`copy_to` waits).
0. Which keys of a connection a folder file may not change (my list: the connection's existence, its `engine`, the tracking schema) and whether anything else in a project file should be root-only.
1. Whether a `copy` may select columns or filter rows (my default: no; do it at the origin with a model).
2. ~~Parameters inside model SQL~~ decided: later, in two stages (values, then names); see Parameters.
3. Whether `mapped` models are checked against the live table at every `plan` (my default: yes, as drift is now) or only by `import --check`.
4. Names for the commands (`import`, `copy`) and for `slice`.
