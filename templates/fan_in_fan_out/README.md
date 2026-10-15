# fan_in_fan_out: two shop systems, one warehouse, two engines

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
dbdatabuild project compile                  # check config, models; write rendered/<connection>/ for sqlserver and postgres (not store_east/store_west: nothing is built there)
dbdatabuild project seed                     # generate the rows of the mapped models into .dbdatabuild/seed.duckdb
dbdatabuild project sample marts.daily_sales # run the report on generated sample data (a copy is not seeded: the rows of warehouse.orders are made up for the sample)
dbdatabuild project tests run                # the model test gives warehouse.orders rows of two stores with the same order number
```

## What a refresh does

The project declares three refresh plans, `hourly`, `daily` and `weekly` (`refresh.plans` in `dbdatabuild.yml`), and each model says which it is in (`refresh:` in its file): the copy and the report are in all three (in `weekly` the copy's load is `full`, below). A copy is a load like any other, so `loads.yml` carries the copy's staging table and a read for each shop in the shop's own dialect (`copy:`, `origins:`), and in `rendered/sqlserver/refresh/hourly.yml` (and the same on `postgres`) the copy is a member **before** the report that reads it:

```yaml
members:
  - member: "default"
    model: "warehouse.orders"
  - member: "default"
    model: "marts.daily_sales"
```

`warehouse.orders` is incremental (`unique_key` and `watermark` in the copy's `kind`): a run reads each shop from the newest `ordered_on` the warehouse holds for that shop, less a week, and replaces the rows with the same `(store_id, order_id)`. A refresh stages the rows of each shop on the warehouse, loads them, drops the staging table and then builds the report; it needs the read login of both shops (`DBDATABUILD_STORE_EAST_READ`, `DBDATABUILD_STORE_WEST_READ`) as well as the warehouse's. If a shop cannot be reached the copy ends `failed: origin`, the report is skipped (`upstream failed`, under the plan's `on_failure: continue`), and nothing is half-written. Rows deleted at a shop are not deleted in the warehouse by an incremental copy (a copy with no `watermark` replaces a shop's rows in full and reconciles them).

**A full re-read.** The copy declares two loads (`loads:` in `models/warehouse/orders.yml`): `default`, which replaces the rows it read again, by `(store_id, order_id)`, and `full`, a `full_replace`. For a copy `full_replace` means everything of the origin being loaded: the copy reads that shop in full (no watermark bound) and replaces all the warehouse holds for it (the other shop's rows stay), so a row deleted at a shop goes too. The plan `weekly` runs it (`refresh: { weekly: { load: full } }`); once, by hand, `dbdatabuild connection refresh sqlserver warehouse.orders.full`. There is no flag that re-reads "everything": a person picks the plan or the load.

## Why the copy declares no resolver

An input of a load can be computed by a `source` resolver, which runs at the origin of a copy in the origin's own dialect (`docs/concepts.md`). This project does not use one on `warehouse.orders`, on purpose: the copy is a **fan-in** (two origins, one table, a `slice` that keeps each origin's rows apart), and the loads that take inputs, a range of days or a window, replace everything in the range, which would remove the other shop's rows of the same days (the copy's own load replaces only the rows of the origin it is loading). A copy from one origin has no such problem: `tests/DbDataBuild.Tests.Conformance/RefreshInputsCopyConformanceTests.cs` copies `src.events` with `delete_insert_by_range`, and its `start` is `source: "SELECT min(day) FROM src.events WHERE day >= DATE '2026-03-01'"`, run at the origin on either engine. A `source` resolver of a copy runs in a refresh at the origin, as in a deploy.

## Running the cycle on four databases

The numbers below are from a run of these commands against SQL Server 2022 and PostgreSQL 17 (ids and times differ in yours). One database each: `stores_wh` (SQL Server; create it `COLLATE Latin1_General_100_CS_AS` to match the
string profile), `stores_rep`, `stores_east` and `stores_west` (PostgreSQL). Every connection has its own pair of logins:

```
export DBDATABUILD_SQLSERVER_READ='Server=127.0.0.1,1433;User Id=...;Password=...;Database=stores_wh;TrustServerCertificate=true'   # and DBDATABUILD_SQLSERVER_WRITE
export DBDATABUILD_POSTGRES_READ='Host=127.0.0.1;Username=...;Password=...;Database=stores_rep'                                      # and DBDATABUILD_POSTGRES_WRITE
export DBDATABUILD_STORE_EAST_READ='Host=127.0.0.1;Username=...;Password=...;Database=stores_east'                                   # a `readonly` connection has no _WRITE login
export DBDATABUILD_STORE_WEST_READ='Host=127.0.0.1;Username=...;Password=...;Database=stores_west'                                   # a `readonly` connection has no _WRITE login
```

1. **Seed the two shops, each with its own seed** (the seeds generate one table, `pos.orders`; a different `--seed` gives different rows). The shops are `readonly postgres` in `dbdatabuild.yml`: the tool never writes to a connection that says so, and `connection seed` refuses one (DDB-455), because real shop systems exist already. To make demo shops, say `engine: postgres` for the two lines while you seed, then put `readonly` back:

   ```
   sed -i 's/engine: readonly postgres/engine: postgres/' dbdatabuild.yml
   dbdatabuild connection seed store_east --seed 1 --apply     # pos.orders  60 row(s)   (needs DBDATABUILD_STORE_EAST_WRITE)
   dbdatabuild connection seed store_west --seed 2 --apply     # pos.orders  60 row(s)   (needs DBDATABUILD_STORE_WEST_WRITE)
   sed -i 's/engine: postgres, parameters/engine: readonly postgres, parameters/' dbdatabuild.yml
   ```

2. **Inspect, then initialize the tracking tables once** (they live on the warehouse and hold the records of every connection):

   ```
   dbdatabuild connection inspect sqlserver        # schema names  marts (missing: a deploy creates it), warehouse (missing: a deploy creates it) ... 1 thing(s) in the way.
   dbdatabuild connection init sqlserver --apply   # Applied 14 statements. The tracking tables are ready.
   dbdatabuild connection inspect store_east       # tracking  schema dbdatabuild on sqlserver: ready (layout 6) ... Nothing in the way.
   ```

3. **Deploy to each engine that builds something.** The warehouse plan reads the live state of the warehouse and of both origins (`pos.orders` must still match its declaration on each: DDB-230 names an origin whose table has drifted;
   `on_mismatch: skip` in the copy would leave that origin out instead of stopping):

   ```
   dbdatabuild connection status sqlserver          # Structure  pending: a deploy would run 9 step(s) ... and 3 load(s)   (A plan now would have 12 step(s))
   dbdatabuild connection deploy sqlserver --write-plan
   #   Plan 2026-10-12-7a515c7a: 12 step(s) (7 ddl, 3 load, 0 backfill, 0 hook, 0 track); 0 risky, 0 destructive.
   dbdatabuild connection deploy sqlserver --apply-plan plans/sqlserver/2026-10-12-7a515c7a.plan.yml
   #   step 7/12: copy warehouse.orders from store_east (pos.orders)
   #   step 8/12: load warehouse.orders (default)
   #   step 9/12: copy warehouse.orders from store_west (pos.orders)
   #   step 10/12: load warehouse.orders (default)
   #   step 11/12: drop staging table of warehouse.orders
   #   step 12/12: load marts.daily_sales (default)
   dbdatabuild connection deploy postgres --write-plan      # the same project on the other engine, filled from the same two shops
   dbdatabuild connection deploy postgres --apply-plan plans/postgres/<id>.plan.yml
   ```

   `warehouse.orders` then holds 60 rows for `E01` and 60 for `W01` on both engines.

4. **Refresh each engine, and compare them** (a comparison across engines reads only digests of the values):

   ```
   dbdatabuild connection refresh sqlserver daily # Refresh plan rendered/sqlserver/refresh/daily.yml: 2 of 2 member(s).
   dbdatabuild connection refresh postgres daily
   dbdatabuild connection compare sqlserver marts.daily_sales --against-connection postgres
   #   matched on the key: 60   only in marts.daily_sales: 0   only in marts.daily_sales: 0
   #   matched rows whose values differ: 0
   #   The tables are identical on the compared columns.
   ```

5. **Monitor.** `dbdatabuild connection monitor sqlserver` has a table the single-connection templates leave empty, the copy origins with their last good run:

   ```
   Copy origins (last good run, and the latest attempt) (2)
     model             origin      last good (UTC)      rows  latest attempt
     warehouse.orders  store_east  2026-10-12 18:28:29  60    2026-10-12 18:28:29 ok
     warehouse.orders  store_west  2026-10-12 18:28:29  60    2026-10-12 18:28:29 ok
   ```

   `--event <id>`, `--lane refresh` and `--attention` work as in the `retail` README, which also walks through the four `refresh.check` levels.

6. **A new order at a shop** reaches the warehouse with a refresh: insert a row into `pos.orders` on `store_east`, then `dbdatabuild connection refresh sqlserver hourly` (`E01` has 61 rows, `W01` still 60: only the origin with a change is different, and a run replaces only the rows of the origin it loads) and the report follows in the same run. The reporting database waits for its own `connection refresh postgres hourly`. The conformance test `FanInFanOutTemplateConformanceTests` does exactly this on real engines, and then takes a shop's read login away to show the copy ending `failed: origin`.

`status` may say `Structure  pending: a deploy would run 3 step(s)` after a deploy: those are the transfers and the load of the copy, which every deploy runs again (they move data; no structure changes).

## Words about the connections

Each of the four connections has a `title`, a `description`, `tags` and `properties` in `dbdatabuild.yml` (`role: origin` on the shop systems, `role: build` on the warehouse). They are in the project's metadata
document and, after `dbdatabuild connection publish sqlserver`, in the tracking table `object_info` under `connection:<name>`, so the records say which connection is for what. Nothing is written into the databases.

## Notes

- The shop systems are `readonly`: the tool reads them (a copy, `connection inspect`, `compare`) and never writes. Their read logins need `SELECT` on `pos.orders`; seeding demo shops needs a write login for a moment, with `readonly` taken out (step 1).
- `connection seed` writes every seeded mapped model of the project to the connection it is given, so seed each shop on its own connection as above (only `pos.orders` has a seed).
- The compile notes DDB-224 for `ux_orders_store_order`: the copy's load key is `store_id` alone (it replaces a shop's rows), which cannot be unique, so no index can make the engine enforce it; the unique index on `(store_id, order_id)` is the table's real key and leads with the load key so each load finds its rows.
- The copy is incremental, which a deploy runs too (it reads from the newest value as well); rows deleted at a shop stay in the warehouse; a copy with no `watermark` would replace a shop's rows in full each run (DESIGN.md 6.5.2).
