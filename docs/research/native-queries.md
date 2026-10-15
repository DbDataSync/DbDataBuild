# Models over a native query or command

Status: design **agreed** (the owner accepted the five decisions below, and added local copies); steps 0 to 2 built (entry 77: local copies, native selects inline in YAML or a file, inlining in tables and views, native as a copy origin); `access: command` is built too (entry 78); `reads:` in the graph, the plan-time describe and native models in the metadata are built (entry 79); `track_definition` is built (entry 80); change feeds (backlog) are not. It follows the cross-server work (`cross-server.md`, entries 62 to 76 of `docs/progress/state-and-apply.md`), whose terms it uses: a **connection** is a named
endpoint, a **mapped** model declares a table that exists, a **copy** moves rows between connections, and a query runs on one connection.

## What is being asked

A model whose rows come from something that is not a table or view the tool can name: a table-valued function (`dbo.fn_orders(@from)`), a procedure (`EXEC dbo.usp_extract`), SQL Server's
`CHANGETABLE(CHANGES ...)`, a vendor function, a `SELECT` that only the engine's own dialect can write (`OPENJSON`, `STRING_SPLIT`, XML methods, PostgreSQL `jsonb_to_recordset`). Three questions: **where** may
such a model be declared, **how** do queries that depend on it see it, and **what does it cost** in the guarantees the tool makes (offline binding, lineage, drift, no data in statement text, read-only reads).

## What the code gives us, and what stands in the way

- A mapped model is a `DeclaredModel` (it was a `SourceDescriptor` when this was written): name, declared columns, grain, connections. Queries bind against **declared columns** in an empty DuckDB table; the descriptor never needs the object to exist offline.
  So the *shape* side of a native query model is already solved by declaring columns.
- A query on a connection can only read tables on that connection (DDB-231); DuckDB lowers the model's query to DuckDB-dialect SQL and polyglot transpiles it to the engine. Nothing in a model can be native text.
- Reads go through `ReadSession`, whose guard accepts a single `SELECT`/`WITH` and refuses `EXEC`, `EXECUTE`, `CALL`, `INTO`, `SET`, `COPY` and the data-changing keywords. That guard is a second layer; the
  read login's permissions are the real enforcement. A procedure cannot pass it, and should not pass it unexamined: **a procedure can write**.
- A copy already reads one origin SELECT with bound parameters (`OpenStreamAsync`) and writes a staging table. That is exactly the shape of "run a native thing, land the rows".

## The distinction that decides everything: does the engine let a query *contain* it?

| Kind | Example | Can sit inside another query? | Side effects | State |
|---|---|---|---|---|
| **Native select** | `OPENJSON`, `STRING_SPLIT`, a vendor expression, a custom `SELECT` | yes (a derived table) | none | none |
| **Table function** | `dbo.fn_orders(@from)`, `generate_series`, PostgreSQL SRFs | yes (`FROM fn(...)`) | none if it is a real inline/multi-statement TVF | none |
| **Change feed** | `CHANGETABLE(CHANGES t, @last)`, `pg_logical_slot_get_changes` | yes for `CHANGETABLE`; **no** for slot-consuming functions (they consume) | `CHANGETABLE`: none; slot functions: consume | a cursor (last version) |
| **Procedure / command** | `EXEC usp_extract`, `CALL p()` | **no** (cannot be a derived table) | possible, unbounded | possible |

Everything below follows from this table: the first three can be *inlined*; a procedure can only be *run* and its rows *landed*.

## Proposal

### One new kind, two ways to use it

A model of **`kind: native`** (not a flavour of `mapped`, because a mapped model says "this object exists" and a native one says "this is computed by the engine each time it is read"). It lives on one
connection, declares its columns like a mapped model (so DuckDB binds against them, offline, as today) and carries its text per engine in a committed file beside the definition:

```yaml
# models/erp/open_orders.yml
name: erp.open_orders
kind:
  native:
    access: select              # select (inline-able) | command (must be run, never inlined)
connections=: [erp]           # exactly one connection: a native query is written in one engine's dialect
grain: [order_id]
columns:
  - {name: order_id, type: BIGINT, nullable: false}
  - {name: placed, type: DATE}
parameters: { since: { type: DATE, value: "2024-01-01" } }
```
```sql
-- models/erp/open_orders.native.sql   (the engine's own text; ${...} references bind like any value, never as text)
SELECT o.order_id, CAST(o.placed AS date) AS placed FROM dbo.fn_open_orders(${model.since}) AS o
```

The `.native.sql` file is the connection's dialect, not DuckDB's: it is **never lowered or transpiled**, which is the whole point, and so it never gets the tool's rewrites or matrix checks (see Costs).

### Where it may be used

1. **`access: select` native models are inline-able.** A model **on the same connection** that reads `erp.open_orders` gets the native text spliced in as a derived table in its rendered script
   (`... FROM (<native text>) AS open_orders ...`), bound parameters intact. DuckDB still sees only the declared columns, so lowering is unchanged: a native model is a table to every query that reads it.
   The splice happens after transpilation, on the already-lowered text, by name: the query's own text is unchanged and its definition hash is too.
2. **Any native model can be the `from:` of a copy** (this is how a connection's data crosses to another, and the only legal use of `access: command`). The copy's origin read becomes the native text
   instead of `SELECT cols FROM table`; the transfer step already streams one read, converts by declared type and bulk-writes a staging table. A procedure's result set is thereby materialized on the
   destination, where ordinary models read it.
3. **`access: command` models are never inlined and are not readable by a query on their own connection.** A model that reads one is refused (DDB-231 style: "copy it first"). This is what keeps a
   procedure out of the middle of a larger statement, where its effects and its cost would be invisible.
4. **A native model cannot be built on, and cannot be a view's source** unless `select`: a view over a native select is allowed (the splice is DDL text); a view over a command is refused.

### How dependent queries see it

- **Columns and types:** exactly the declared ones. That is also the contract the engine is held to (below).
- **Lineage:** a native query is opaque to the analyzer. Column lineage stops at it, and the `reads:` list (`reads: [dbo.orders, dbo.customers]`, optional) is how it takes part in the graph, in ordering and
  in `--column` impact; without it the graph shows a source with no ancestors, and `validate` says so as a note.
- **Rewrites and the matrix:** the tool's value (answers equal DuckDB's) does not extend into native text, and cannot. A reading model's own query keeps all its checks; the native text carries a standing
  note in `render`'s findings ("engine-native: not checked against DuckDB"), and the native model's declared grain, nullability and types are *assertions*, not facts.
- **Parameters:** values only (`${project.x}`, `${model.x}`, `${connection.x}`), typed and bound, as in 6.5.3. Never names (the agreed exclusion).

### Guarantees we can keep, and how

| Guarantee | How it holds for native text |
|---|---|
| Reads are read-only | The guard stays the guard: `access: select` text must pass the existing single-`SELECT`/`WITH` check (no `EXEC`). `access: command` text is run **only** by a new `ReadSession` call that requires the read login **and** a connection-level `allow_native_commands: true` (default false, in the root file), and runs inside a read-only session (`default_transaction_read_only` on PostgreSQL, a `READONLY` application intent / a rolled-back transaction on SQL Server). The real enforcement stays the login's permissions; the docs say so, and `validate` names every command model so a reviewer sees them in one list. |
| No data in statement text | Parameters are bound, never written, exactly as for query values. The native text is committed, hashed, and shown in the plan (the plan's transfer step already records "the exact read, hashed"). |
| Offline binding | Declared columns, unchanged. |
| Drift is detected | At `plan` the tool asks the engine for the native text's **result shape** without running it where the engine offers that (`sp_describe_first_result_set`, a prepared statement's description on PostgreSQL) and compares it with the declaration, as it already does for mapped origins (DDB-230, `on_mismatch`). For a command the describe is unavailable or unreliable (dynamic SQL, temp tables): the check is skipped with a note, and the transfer's own name-and-count check at apply is the net. |
| One connection per query | A native model is on one connection; using it elsewhere is a copy. |
| Reproducibility | The text and its hash are in the repository and in the plan; an engine-side change to the function is invisible, as it is for any table's contents. A function's *definition* can be hashed on engines that expose it (`sys.sql_modules`, `pg_get_functiondef`) and recorded as drift: **opt-in `track_definition: true`**, read-only, one extra catalog query. |

### Change feeds and any native state

`CHANGETABLE(CHANGES dbo.orders, @last_version)` needs the last version a copy consumed. That is the **incremental copy's watermark** under another name, and the machinery exists: a resolver at plan time
reads the destination's newest value, the plan records it, apply binds it. For a native origin the bound value goes into the native text by name:

```yaml
kind: {copy: {from: erp.order_changes, unique_key: [order_id], watermark: {column: sys_change_version}}}
```
```sql
-- erp.order_changes.native.sql
SELECT ct.sys_change_version, ct.sys_change_operation, ct.order_id, o.* FROM CHANGETABLE(CHANGES dbo.orders, @watermark) AS ct LEFT JOIN dbo.orders o ON o.order_id = ct.order_id
```
A native text that uses `@watermark` declares it (`uses: [watermark]`); the copy's watermark column must be one of the declared columns, and the first run (nothing in the destination) binds `NULL` / the engine's
minimum (`CHANGE_TRACKING_MIN_VALID_VERSION`, declared as `initial:` in the model), because a feed cannot "read everything". **Deletes are visible here** (`sys_change_operation = 'D'`): the copy needs a
`deleted_when:` clause (`column: sys_change_operation, value: D`) to turn them into deletes, which is the one new load behaviour (`delete_insert_by_key` plus a delete of matching keys that are flagged).
Slot-consuming feeds (logical decoding) mutate engine state when read: **refused** in the first version (`access: command` plus an explicit `consumes: true` could allow them later, with a stronger
confirmation and no dry run).

### What a plan shows

A native model adds no new step type: inlined, it is part of the reading model's load text (the plan already shows exact text); landed through a copy, it is the transfer step's `read`. A command-based
transfer step gets `risk: risky` with the reason `native command`, so a person allows it explicitly (`--allow-risky`), and its dry run does **not** execute the command (it prints it).

## Costs, honestly

1. A native model is the one place the tool does not check an engine's answer against DuckDB; the declared types are trusted and verified only by the transfer's per-value conversion and the plan-time describe.
2. Procedure side effects cannot be proven absent by the tool, only forbidden by login and session mode. The design therefore makes commands rare (opt-in per connection, listed by `validate`, risky in the plan).
3. A native text ties a model to one engine: it cannot be built on another connection of another engine, and `connections` of a native model must be a single connection.
4. Inlined native text can make a dependent query slow in ways the declared columns hide (a multi-statement TVF is an optimization barrier). `validate` warns once per inlined multi-statement function when the engine
   reports it (`sys.objects.type = 'TF'`), and the advice is to land it with a copy.

## Alternatives considered

- **A new `native` flavour of `mapped`** (a `native:` key on a mapped model). Rejected: a mapped model's contract is "exists, read by name"; a model that is a computation has different drift, different
  inlining rules and (for commands) different safety rules. One kind with `access` says that in one word.
- **Allow native text inside ordinary model SQL** (`NATIVE('...')`). Rejected: it breaks lowering (DuckDB cannot bind it), hides the engine tie inside a portable query, and invites every query to carry
  unchecked text. Native models keep it in one named, listed place.
- **Run native text through DuckDB's engine scanners** (`postgres_scan`, `sqlserver_scan`). Rejected earlier (no DuckDB in the middle), and it would not run procedures either.
- **Only commands, no inlining** (everything lands through a copy). Simpler and safer, but then a same-connection view over `OPENJSON` needs a copy for no reason; inlining the `select` kind costs one splice.

## Build order

1. `native` kind with `access: select`: definition, `.native.sql` file, guard check, declared columns bound offline, inline splice in rendering, `reads:`, plan-time describe, validation, tests on both engines
   (a table function and an `OPENJSON`/`jsonb_to_recordset` case, a view over it, a dependent model).
2. Native as a copy origin (`select` first): the origin read replaced by the native text, parameters bound, the incremental `@watermark` binding, `initial:`.
3. `access: command`: the `allow_native_commands` connection setting, the read-only session mode, the risky plan step and its dry run, the refusal of inlining.
4. Change feeds: `deleted_when`, `CHANGETABLE` end-to-end test on SQL Server (change tracking enabled on a test table), and the refusal of slot-consuming feeds.
5. `track_definition`, the TVF-type warning, and `report` showing native models.

## Decisions (agreed)

1. **One `native` kind** with `access: select | command`.
2. **Selects and table functions first** (inline, and as a copy origin); commands are step 3, after the first two have been seen.
3. **Inlining on the same connection is accepted**: a derived table spliced into the reading model's rendered text.
4. **Change-feed deletes**: `deleted_when` on the copy (as proposed), to be revisited when feeds are built.
5. **Where the text lives**: either `query:` inline in the YAML (short ones) or a `.native.sql` file beside the definition, never both.

## Local copies (added by the owner): materializing on one connection, without crossing

A copy has so far meant "between connections". It must also work **within** one: `kind: {copy: {from: erp.open_orders}}` on the connection that holds `erp.open_orders`. This is how a native select, a
table function or (later) a command becomes a real table on its own connection, with indexes, drift and the ordinary strategies, and how any model's rows can be snapshotted locally.

- **A local copy is an ordinary load over a generated query.** No staging table and no transfer step: its query is `SELECT <the origin's columns> FROM <origin>`, bound by DuckDB against the declared columns
  like any model, lowered, rendered and, when the origin is native, inlined. Everything else is the existing machinery (`full_replace`, indexes, drift, the plan, tracking).
- **Local or remote is decided by the connections**, not by a setting: a copy whose every connection is the origin's is local; one with none of them is remote (a transfer); a mix is refused with the reason
  (a copy has one meaning per run).
- **Local copies are full replaces**: `slice`, `unique_key` and `watermark` are refused on them for now (an incremental local materialization is an incremental model over the origin, which already exists). The
  refusal says so.
- **A command origin** (`access: command`) cannot be read by a query, so it is, in the end, a transfer even when it is the destination's own connection (built, entry 78); the first release left it out: running a command to fill a table on its own connection is a write-side
  execution (`INSERT ... EXEC`) with the write login, and is designed with step 3, not before.

