# Model kinds: what each is and how it operates

A **model** is a table or view in a connection's database, described by files in `models/`. Its **model kind** (`kind:`) says what the tool does with it. There are seven. The reference for every key is `DESIGN.md` section 6 (6.1 kinds, 6.2 rules, 6.5.2 copies, 6.5.4 native models, 6.6 loads); this page is the summary.

## At a glance

| Model kind | What it is | Files | Built by the tool? | Persisted as | Default load |
|---|---|---|---|---|---|
| `view` | A view over other models | `.sql` + `.yml` | yes (DDL) | a view | none: no rows move |
| `full` | A table rebuilt from its query on every load | `.sql` + `.yml` | yes | a table | replace the table's contents (`full_replace`) |
| `incremental_by_unique_key` | A table kept up to date by key | `.sql` + `.yml` | yes | a table | rows of the query replace the rows with the same key (`delete_insert_by_key`, or `merge_by_key` where the engine has `MERGE`) |
| `incremental_by_time_range` | A table kept up to date by time | `.sql` + `.yml` | yes | a table | reload from the newest `time_column` value held, less `lookback` |
| `copy` | A table filled with the rows of a model on **another connection** | `.yml` only | yes | a table | full replace, or replace by key when it has a `watermark` |
| `mapped` | A table that already exists and that the tool does not build | `.yml` only | **no** | (exists already) | none |
| `native` | A table the **engine computes** from its own query | `.yml` (text in it, or in a `.native.sql`) | **no** | (computed on read) | none |

## How each operates

| | `view` | `full` | `incremental_by_unique_key` | `incremental_by_time_range` | `copy` | `mapped` | `native` |
|---|---|---|---|---|---|---|---|
| **Has a query** | DuckDB-dialect `SELECT`, lowered for each engine | same | same | same | none: the origin's columns | none | the engine's own dialect, never lowered |
| **Columns** | declared (`project model update` proposes them from the query) | declared | declared | declared | the origin's | declared (`project import` writes them) | declared |
| **Required keys** | | | `unique_key`, `grain` | `time_column`, `grain` | `from` | | `access`, `query` |
| **Deploy (structure)** | creates or alters the view | creates or alters the table, its indexes | same | same | same, plus a staging table | never creates anything | never creates anything |
| **Refresh (data)** | nothing to load | runs the load | runs the load | runs the load | runs the load: read each origin, stage, load | not in a refresh | not in a refresh (only as the origin of a copy) |
| **Lives on** | one or several connections (`connections:`) | same | same | same | the destination connections; the origin must be on others | one or several connections (fan-in) | exactly one connection |
| **Can be read by a query** | yes | yes | yes | yes | yes | yes | yes, on the same connection (inlined as a derived table); an `access: command` cannot be read |
| **Can be a copy origin** | yes | yes | yes | yes | yes | yes | yes |
| **Loads (`loads:`)** | | yes | yes | yes | yes (`default`, and often a `full`) | | |
| **In refresh plans** | no (no load) | yes | yes | yes | yes, except a copy of a native model, which a deploy handles | no | no |
| **Drift** | detected | detected | detected | detected | detected | checked against the database (`project import`, the structure check) | the routines it names (`track_definition`) |
| **Typical use** | a mart over other tables | a small dimension | a fact table with a key | an event table by day | bring a shop system's orders into the warehouse | a source table you read but do not own | `STRING_SPLIT`, a table-valued function, a stored procedure result |

## How a kind is written, and its settings

`kind` is the name of the model kind. A kind with settings is written as a mapping with **one key, the kind's name**, and that kind's settings beneath it; a kind with no settings may be written as a plain value:

```yaml
kind: view                                                  # no settings: a plain value
kind: {full: {}}                                            # the same, as a mapping
kind:
  incremental_by_unique_key:
    unique_key: [order_id]
kind: {copy: {from: pos.orders, unique_key: [order_id]}}    # flow style
```

Exactly one key is allowed under `kind`, and a setting that does not belong to the kind is an error (unknown keys are refused), because each kind's settings are a closed set in `schemas/model.schema.json`. A key such as `kind.copy:` is an ordinary key with a dot in its name; it does not nest and is not supported. A folder's `_dbdatabuild.yml` (or the project file) may give a default `kind` under `defaults:`; a model's own `kind` replaces it whole, never key by key. The form of the first releases, `kind: {type: copy, from: pos.orders}`, is not read: it is an error (DDB-248) that says how to write it now.

The kind names are closed: `view`, `full`, `incremental_by_unique_key`, `incremental_by_time_range`, `copy`, `mapped`, `native`. Closed sets of values are marked.

| Setting | Of the kind | Required | Values | Meaning |
|---|---|---|---|---|
| `unique_key` | `incremental_by_unique_key`; `copy` (an incremental copy) | yes for `incremental_by_unique_key`; for a copy, needed with `watermark` | a list of column names | the rows of a load replace the rows with the same key; checked against `grain` |
| `time_column` | `incremental_by_time_range` | yes | a `DATE` or `TIMESTAMP` column | the column the range is measured on |
| `lookback` | `incremental_by_time_range` | no | a duration such as `3 days` (minutes, hours, days, weeks, months; a `DATE` column takes days, weeks or months) | how far before the newest value a load starts again, for late or changed rows |
| `from` | `copy` | yes | `schema_name.table_name` of a model, mapped model or another copy, on exactly one connection other than the copy's | the origin |
| `slice` | `copy` | no | `column`, `value` (may use `${origin.name}` or `${connection.name}`), `type` (the column's SQL type, when the origin has no such column) | which rows belong to an origin, for a copy from several connections; a load replaces only that origin's rows |
| `watermark` | `copy` | no | `column`, `lookback` | makes a copy incremental: read the rows at or after the newest value the destination holds, less `lookback` (needs `unique_key`) |
| `on_mismatch` | `copy` | no | **closed:** `fail` (default), `skip` | what a deploy does about an origin whose table differs from its declaration: stop, or leave that origin out (never silently). A refresh does not make this check |
| `access` | `native` | no (default `select`) | **closed:** `select`, `command` | `select`: a query can contain it (it is inlined as a derived table). `command`: it can only be run, as the origin of a copy |
| `query` | `native` | yes, here or in a `.native.sql` file beside the definition (not both) | the engine's own text | a single `SELECT` for `select`; one call (`EXEC`, `EXECUTE`, `CALL` or `SELECT`) for `command` |

`view`, `full` and `mapped` take no settings. Settings that belong to the model rather than to its kind are outside `kind`: `columns`, `grain`, `indexes`, `connections`, `loads`, `refresh`, `hooks`, `renames`, `title`, `description`, `tags`, `properties`. A native model also has `reads`, `parameters` and `track_definition` outside `kind`.

**In the metadata** all seven are the same sort of thing: each has a document whose `kind` is the model kind as a plain string and whose settings (`kind_settings`) sit beside it, and the stored table `metadata_current` has the model kind in `model_kind` for every one of them (`view`, `full`, `incremental_by_unique_key`, `incremental_by_time_range`, `copy`, `mapped`, `native`). All seven are `dbdatabuild.model/3` documents (a mapped or native model's has no query, loads, hooks or lineage), their subject kind in `metadata_current.subject_kind` is `model`, and one array, `data.models[]`, lists them in `project compile` and `project show metadata`; the counts (`OK: 12 model(s) valid (3 view, 2 full, 7 mapped)`) include all seven. In the dependency rows (`metadata_dependency`, the views `metadata_upstream` and `metadata_ancestors`, a model document's `upstream[]`) a thing a query reads has a **category** (`depends_on_category`: `model`, `macro` or `unknown`) and, when it is a model, its **model kind** (`depends_on_kind`: any of the seven, so a view is `view` and a mapped model `mapped`; null for a macro or an unknown table). A rule over "all models" reads `metadata_models` (one row per model of any kind); one about built models only says `model_kind NOT IN ('mapped', 'native')`; one about what a query reads says `upstream_kind = 'mapped'` (a macro or an unknown table has a null kind, so write `IS DISTINCT FROM` where that matters).

## The seven in more detail

### `view`

A view over other models. The tool writes the `CREATE`/`ALTER` through a deploy plan and never moves rows. It follows upstream changes: a column added upstream does not appear in the view until the model is updated and deployed. Because it has no load, a refresh does nothing for it.

```yaml
# models/marts/active_customers.yml (beside active_customers.sql)
name: marts.active_customers
kind: view
columns: [...]
```

### `full`

A table whose whole contents are replaced on each load, in one transaction. The simplest table. Good for small models and for anything you can afford to recompute.

### `incremental_by_unique_key`

A table updated by key: each load runs the query, and its rows replace the rows with the same `unique_key` (`unique_key` is checked against `grain`). Rows that stop appearing in the query are **not** removed. Needs `unique_key` and `grain`.

### `incremental_by_time_range`

A table updated by time: a load reads from the newest value of `time_column` already in the table, less a `lookback` (for rows that arrive late or change), and replaces that range. The range comes from the table itself, never from a state table. Needs `time_column`.

Both incremental kinds can declare more loads (`loads:`): a `backfill` over a date range, a `reconcile`, a `full`. A load is any movement of rows into the table; plans (`hourly`, `daily`) choose which loads run when. See `docs/concepts.md` and `docs/research/refresh-plans.md`.

### `copy`

A table filled with the rows of another model that lives on **another connection** (a query always runs on one connection, and the tool never joins across two). No query and no `columns`: both come from the origin. The origin (`from`) may be a model, a mapped model, a native model, or another copy.

```yaml
name: warehouse.orders
kind:
  copy:
    from: pos.orders
    slice: {column: store_id, value: "${origin.store_id}", type: "VARCHAR(10)"}   # fan-in: which rows are an origin's
    unique_key: [order_id]
    watermark: {column: ordered_on, lookback: 7 days}                           # incremental; leave out for a full copy
connections=: [sqlserver, postgres]
```

How a copy runs: for each origin connection, read the rows (`SELECT` of quoted columns, `WHERE watermark >= ?` when incremental) with the origin's read login, write them into a staging table on the destination, load them into the table, then drop the staging table. The origin can be any connection kind that can be read: SQL Server, PostgreSQL, a DuckDB file, an ODBC database or an Arrow Flight SQL server (`docs/operations.md`). A **local copy** (the origin is on the destination's own connection) skips the staging and is an ordinary full-replace load. A copy can also declare a second load that re-reads everything (`full`).

### `mapped`

A table that exists and that the tool does not build or write: a source system's table, a table loaded by another tool. It is declared (`columns`, `grain`, indexes, foreign keys) so that queries can be checked offline and so that drift is found, and it is written from the database's catalog by `dbdatabuild project import` (the planned `project update` will refresh it: OPEN-ITEMS V). It may be on several connections that each hold the same table (a fan-in). It has no loads and is not in a refresh; it is read by queries and copied from by copies.

### `native`

A table the engine computes from its own query: a table-valued function, `STRING_SPLIT`, an engine-native `SELECT`. The text is in the engine's own dialect and is never lowered or transpiled, so it lives on exactly one connection. A query on that connection reads it as a derived table. With `access: command` (a stored procedure or a function call that returns rows, allowed only where the connection sets `allow_native_commands: true`) it can only be the origin of a copy, and the copy is a risky step.

## Which one do I use?

| You want | Use |
|---|---|
| A query's result, always current, no storage | `view` |
| A small table recomputed every time | `full` |
| A large table that gains and changes rows by key | `incremental_by_unique_key` |
| A large table of events by time | `incremental_by_time_range` |
| Rows from a different database in this one | `copy` |
| To read a table you do not own | `mapped` |
| To read the result of a function or query only the engine can run | `native` |

## Related

* Where the kinds are defined: `DESIGN.md` 6.1, the copy 6.5.2, native 6.5.4, loads 6.6, mapped models 6.5.
* What runs when, and in what order: `docs/concepts.md` (deploy, refresh, refresh plans).
* Origins other than SQL Server and PostgreSQL: `docs/operations.md` (DuckDB, ODBC, Flight SQL) and `docs/research/read-origins.md`.
