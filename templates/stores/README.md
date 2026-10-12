# stores: two shop systems, one warehouse, two engines

Northstar Outfitters again, with its systems apart. Two regional shop databases (`store_east`, `store_west`, PostgreSQL) each hold a `pos.orders` table; the warehouse (`sqlserver`) and a reporting
database (`postgres`) hold the project's tables. The project shows what one connection cannot:

| Model | What it shows |
|---|---|
| `pos.orders` | a **mapped** model on two connections (`connections=: [store_east, store_west]`): the same table in two systems |
| `warehouse.orders` | a **fan-in copy**: the rows of both shops land in one table. `slice` says which rows are an origin's (`store_id` is added from each connection's `store_id` parameter), the order number repeats between shops so `store_id` joins the key, and a run replaces only the rows of the origin it loads. It is placed on both the warehouse and the reporting database |
| `marts.daily_sales` | a **fan-out**: one query built on SQL Server and on PostgreSQL, each reading its own `warehouse.orders` |

`dbdatabuild.yml` names the connections, gives the shops their `store_id` parameter, keeps the tracking records of all of them on the warehouse (`tracking: { connection: sqlserver }`) and sets the string profile per engine
(the project's profile is SQL Server's; `connections.postgres.string_semantics` overrides `trailing_space`). `marts.daily_sales` groups by two text columns, which would compare differently on the two engines if a value ended
in a space, so the compile reports DDB-236 for it unless the columns are `trimmed: true` (`pos.orders.channel` says so) or the model silences it (`lint_ignore: [DDB-236]` in `models/marts/daily_sales.yml`, with the reason:
`store_id` comes from a parameter, so it never ends in a space). Text comparison across engines is parked as a design question (`docs/progress/OPEN-ITEMS.md`, P); this template shows what exists and adds nothing.

```
dbdatabuild project compile                  # check config, models and sources; write rendered/<connection>/ for sqlserver and postgres (not store_east/store_west: nothing is built there)
dbdatabuild project seed                     # generate the source rows into .dbdatabuild/seed.duckdb
dbdatabuild project sample marts.daily_sales # run the report on generated sample data (a copy is not seeded: the rows of warehouse.orders are made up for the sample)
dbdatabuild project tests run                # the model test gives warehouse.orders rows of two stores with the same order number
```

## What a refresh does and does not do

Compile writes one refresh plan per connection that builds something. In `rendered/sqlserver/refresh.plan.yml` (and the same in `rendered/postgres/refresh.plan.yml`) the report is a routine load and the copy is **excluded**:

```yaml
loads:
  - model: "marts.daily_sales"
    operation: "default"
    file: "sqlserver/marts.daily_sales/load.default.sql"
    ...
excluded:
  - model: "warehouse.orders"
    reason: "a copy moves rows between connections; a deploy plans it"
```

A refresh rebuilds the report from what the warehouse holds; it never reads a shop system. To bring new orders in, **deploy** again: the plan has a transfer and a load per origin (`copy warehouse.orders from store_east (pos.orders)`),
which needs the read login of every origin. `connection refresh` says so while it runs: `1 routine load(s); 1 model(s) are not routine (a deploy handles them): warehouse.orders.`

## Running the cycle on four databases

The numbers below are from a run of these commands against SQL Server 2022 and PostgreSQL 17 (ids and times differ in yours). One database each: `stores_wh` (SQL Server; create it `COLLATE Latin1_General_100_CS_AS` to match the
string profile), `stores_rep`, `stores_east` and `stores_west` (PostgreSQL). Every connection has its own pair of logins:

```
export DBDATABUILD_SQLSERVER_READ='Server=127.0.0.1,1433;User Id=...;Password=...;Database=stores_wh;TrustServerCertificate=true'   # and DBDATABUILD_SQLSERVER_WRITE
export DBDATABUILD_POSTGRES_READ='Host=127.0.0.1;Username=...;Password=...;Database=stores_rep'                                      # and DBDATABUILD_POSTGRES_WRITE
export DBDATABUILD_STORE_EAST_READ='Host=127.0.0.1;Username=...;Password=...;Database=stores_east'                                   # and DBDATABUILD_STORE_EAST_WRITE (seeding only)
export DBDATABUILD_STORE_WEST_READ='Host=127.0.0.1;Username=...;Password=...;Database=stores_west'                                   # and DBDATABUILD_STORE_WEST_WRITE (seeding only)
```

1. **Seed the two shops, each with its own seed** (the seeds generate one table, `pos.orders`; a different `--seed` gives different rows):

   ```
   dbdatabuild connection seed --connection store_east --seed 1 --apply     # pos.orders  60 row(s)
   dbdatabuild connection seed --connection store_west --seed 2 --apply     # pos.orders  60 row(s)
   ```

2. **Inspect, then initialize the tracking tables once** (they live on the warehouse and hold the records of every connection):

   ```
   dbdatabuild connection inspect --connection sqlserver        # schema names  marts (missing: a deploy creates it), warehouse (missing: a deploy creates it) ... 1 thing(s) in the way.
   dbdatabuild connection init --connection sqlserver --apply   # Applied 13 statements. The tracking tables are ready.
   dbdatabuild connection inspect --connection store_east       # tracking  schema dbdatabuild on sqlserver: ready (layout 5) ... Nothing in the way.
   ```

3. **Deploy to each engine that builds something.** The warehouse plan reads the live state of the warehouse and of both origins (`pos.orders` must still match its declaration on each: DDB-230 names an origin whose table has drifted;
   `on_mismatch: skip` in the copy would leave that origin out instead of stopping):

   ```
   dbdatabuild connection status --connection sqlserver          # Structure  pending: a deploy would run 9 step(s) ... and 3 load(s)   (A plan now would have 12 step(s))
   dbdatabuild connection deploy --connection sqlserver --write-plan
   #   Plan 2026-10-12-7a515c7a: 12 step(s) (7 ddl, 3 load, 0 backfill, 0 hook, 0 track); 0 risky, 0 destructive.
   dbdatabuild connection deploy --connection sqlserver --apply-plan plans/sqlserver/2026-10-12-7a515c7a.plan.yml
   #   step 7/12: copy warehouse.orders from store_east (pos.orders)
   #   step 8/12: load warehouse.orders (default)
   #   step 9/12: copy warehouse.orders from store_west (pos.orders)
   #   step 10/12: load warehouse.orders (default)
   #   step 11/12: drop staging table of warehouse.orders
   #   step 12/12: load marts.daily_sales (default)
   dbdatabuild connection deploy --connection postgres --write-plan      # the same project on the other engine, filled from the same two shops
   dbdatabuild connection deploy --connection postgres --apply-plan plans/postgres/<id>.plan.yml
   ```

   `warehouse.orders` then holds 60 rows for `E01` and 60 for `W01` on both engines.

4. **Refresh each engine, and compare them** (a comparison across engines reads only digests of the values):

   ```
   dbdatabuild connection refresh --connection sqlserver      # Refresh plan rendered/sqlserver/refresh.plan.yml: 1 routine load(s); 1 model(s) are not routine (a deploy handles them): warehouse.orders.
   dbdatabuild connection refresh --connection postgres
   dbdatabuild connection compare marts.daily_sales --connection sqlserver --against-connection postgres
   #   matched on the key: 60   only in marts.daily_sales: 0   only in marts.daily_sales: 0
   #   matched rows whose values differ: 0
   #   The tables are identical on the compared columns.
   ```

5. **Monitor.** `dbdatabuild connection monitor --connection sqlserver` has a table the single-connection templates leave empty, the copy origins with their last good run:

   ```
   Copy origins (last good run, and the latest attempt) (2)
     model             origin      last good (UTC)      rows  latest attempt
     warehouse.orders  store_east  2026-10-12 18:28:29  60    2026-10-12 18:28:29 ok
     warehouse.orders  store_west  2026-10-12 18:28:29  60    2026-10-12 18:28:29 ok
   ```

   `--event <id>`, `--lane refresh` and `--attention` work as in the `retail` README, which also walks through the four `refresh.check` levels.

6. **A new order at a shop** reaches the warehouse with a deploy, not a refresh: insert a row into `pos.orders` on `store_east`, refresh (`warehouse.orders` still has 120 rows), deploy again (`E01` has 61 rows, `W01` still 60: only the origin
   with a change is different, and a run replaces only the rows of the origin it loads). The reporting database waits for its own deploy. The conformance test `StoresTemplateConformanceTests` does exactly this on real engines.

`status` may say `Structure  pending: a deploy would run 3 step(s)` after a deploy: those are the transfers and the load of the copy, which every deploy runs again (they move data; no structure changes).

## Notes

- The shop systems are only read by the tool once they are seeded; their logins need `SELECT` on `pos.orders` (and, to seed them, rights to create a schema and a table).
- `connection seed` writes every seeded source of the project to the connection it is given, so seed each shop on its own connection as above (only `pos.orders` has a seed).
- The compile notes DDB-224 for `ux_orders_store_order`: the copy's load key is `store_id` alone (it replaces a shop's rows), which cannot be unique, so no index can make the engine enforce it; the unique index on `(store_id, order_id)` is the table's real key and leads with the load key so each load finds its rows.
- Not in this template: an incremental copy (`unique_key` and `watermark`, DESIGN.md 6.5.2), which reads only what changed at an origin.
