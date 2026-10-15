# macro_examples: a small order ledger

A project that shows **macros**: names for expressions and for whole queries, kept in `macros/*.sql`. Two mapped models hold orders (`ledger.orders`, the live state, and `ledger.orders_history`, the same
orders as they stood at the end of each month) and three marts are built from them.

**A macro is never run by an engine.** DuckDB expands a macro while the tool binds a query offline; everything after that (the support matrix, the transpile to T-SQL or PostgreSQL, the files in `rendered/`,
the hashes) sees only what the macro expanded to. Open `rendered/sqlserver/marts.orders/load.default.sql` and you will not find `dollars(`, `size_band(` or `status_group(`, only the expressions they stand for.
Macros need the lowerer (they are refused with `lowering: { enabled: false }`), and `dbdatabuild project compile` finds a macro that is wrong, or given a table that does not exist, without any database.

```
dbdatabuild project compile                          # expand the macros; rendered/lowered/<model>/lowered.sql is the query after DuckDB expanded them
dbdatabuild project show graph +marts.status_totals_history   # the macros are nodes of the graph: dollars(), snapshot_at(), status_totals()
dbdatabuild project seed                             # the rows of the mapped models (ledger.orders 80 rows, ledger.orders_history 305)
dbdatabuild project sample marts.orders --limit 5    # run a model on them
dbdatabuild project tests run                        # a model test per mart; the macros are tested through them
```

## What is where

| File | What it shows |
|---|---|
| `macros/money.sql` | **scalar macros**: `dollars(cents)`, `size_band(amount)`, `status_group(status)`. A call is replaced by its expression, so changing a limit in `size_band` is one edit and every model that bands an amount agrees |
| `macros/snapshots.sql` | **table macros** (`AS TABLE`): `snapshot_at(tbl, col)` and `status_totals(tbl, col)`, a macro that calls a table macro and a scalar one. The table and the column arrive as names (`query_table(tbl)`, `COLUMNS(lambda c: c = col)`), only constants can be names, and a name that does not exist is a bind error found by `compile`. `live_as_of := DATE '2024-06-30'` is a default parameter, which a call may name or leave out |
| `macros/types.sql` | **a type**: `CREATE TYPE money AS DECIMAL(12, 2)`. The name is replaced by the type while the query is bound, so `rendered/` says `NUMERIC(12, 2)`. An `ENUM` type is DuckDB's alone: a cast to it is refused (DDB-324) rather than sent to an engine that has no such type |
| `models/marts/status_totals_live.sql` and `status_totals_history.sql` | **one macro for tables of the same shape**: `status_totals('ledger.orders', NULL)` and `status_totals(${project.history_table}, ${project.snapshot_column})`. `NULL` for the day column says the table is live; the macro's `CASE WHEN col IS NULL` is decided while DuckDB expands the call, so the SQL of each model has no `CASE` and no `UNION`, only its own table |
| `dbdatabuild.yml` `parameters:` | **names as parameters** (type `NAME`): `history_table: { type: NAME, value: ledger.orders_history }`. A name is not a value to bind: before DuckDB binds the query, `${project.history_table}` becomes the text `'ledger.orders_history'`. Only dotted names are accepted. A connection may give its own value (`connections.<name>.parameters`) |

What the same macro expands to for the two tables (`rendered/lowered/marts.status_totals_live/lowered.sql` and `..._history/...`):

```
SELECT DATE '2024-06-30' AS as_of_date, status, count(*) AS orders, sum(CAST((CAST(amount_cents AS DECIMAL(22, 0)) * CAST(0.01 AS DECIMAL(22, 2))) AS DECIMAL(12, 2))) AS total
FROM ledger.orders
GROUP BY status

SELECT snap_date AS as_of_date, status, count(*) AS orders, sum(CAST((CAST(amount_cents AS DECIMAL(22, 0)) * CAST(0.01 AS DECIMAL(22, 2))) AS DECIMAL(12, 2))) AS total
FROM ledger.orders_history
GROUP BY snap_date, status
```

A model that calls a macro depends on it (`dollars()  (macro)` in `project show graph`), and its definition hash includes the macro's text, so editing `macros/money.sql` changes what every model that reaches it is planned and blocked on.

## Things to try

- Change `size_band` in `macros/money.sql` (`< 25` to `< 30`), run `dbdatabuild project compile` and `dbdatabuild project tests run`: the model test for `marts.orders` fails at the band, and `compile --check` says the rendered files are out of date.
- Change the value of `snapshot_column` in `dbdatabuild.yml` to something that is not a column of the history table and run `compile`: DDB-324, DuckDB could not bind the query, with the expression and the name you gave in the message (a star over an empty set of columns); nothing was asked of a database.
- Add a second history table of the same shape and a model that calls `status_totals` on it with a new parameter.
- Try `CAST(status AS my_enum)` in a macro after `CREATE TYPE my_enum AS ENUM (...)`: `compile` refuses the model (DDB-324) and says why.

## Running it on a database

`connection seed --apply` creates both source tables in an empty database (SQL Server created `COLLATE Latin1_General_100_CS_AS` to match the profile) and `connection init --apply`, `connection deploy --write-plan --accept-inferred` and
`--apply-plan` build the three marts, as in the `retail` README (which also walks through `refresh`, `status`, `monitor` and the `refresh.check` levels). The conformance suite builds the project on SQL Server 2022 and on PostgreSQL 17
and compares every mart, row by row, with what DuckDB gives on the same seeds, so the expansion is checked against DuckDB on both engines. For PostgreSQL replace the lines at the top of `dbdatabuild.yml` as its comment says and keep the `parameters:` block.
