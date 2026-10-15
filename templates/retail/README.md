# retail: an online and in-store shop

A larger project to explore what dbdatabuild does with real-looking transforms. Twelve source tables (`shop.*` for the
operational system, `ref.*` for lookups, `finance.budgets`), a staging layer, and a star schema with facts, dimensions,
aggregates and a report. The data is made up by SQL in `seeds/` (about 300 customers and 2,000 order lines by default), so every command below
runs on your own machine.

```
dbdatabuild project compile                         # check config, models, see how each query lowers, and write the load scripts (rendered/) to read and commit
dbdatabuild project show graph --columns                # what reads what, and where each column comes from
dbdatabuild project seed --scale 1000              # generate the rows of the mapped models into .dbdatabuild/seed.duckdb
dbdatabuild project sample marts.fct_sales_lines   # run a model on that data and look at the rows
dbdatabuild project tests run                           # metadata rules and model tests
```

## The transforms worth reading

| Model | What it shows |
|---|---|
| `staging.customers` | **splitting** one name column into first and last name (`van Dijk` stays whole) and an email into its domain |
| `staging.orders` | **mapping** a two-letter status code to words; a month key cut from a timestamp |
| `marts.dim_store`, `marts.dim_product` | joins to the reference tables, banding (`size_band`, margin band), a default for the unknown |
| `marts.dim_date` | a calendar of every day something happened, with weekday and month names that do not depend on the engine's language |
| `marts.fct_sales_lines` | **allocating** an order's discount and shipping fee to its lines in proportion to their value, in whole cents, the last line taking the remainder so the pieces always add up; incrementally loaded by `order_id, line_no` |
| `marts.fct_shipment_packages` | **splitting** a shipment into one row per package (a join to a series of numbers), weight and freight split evenly with the remainder on the last package |
| `marts.fct_returns` | a lookup with a default (`other`) for an unmapped reason code |
| `marts.agg_daily_sales` | **aggregating** lines to a day, a region and a channel group, with a running total per region |
| `marts.agg_customer_value` | **aggregating** to a customer: orders, spend, first and last order, a value segment |
| `marts.fct_budget_allocation` | **allocating** a regional budget to its shops by floor area, to the cent |
| `marts.rpt_budget_vs_actual` | the allocated budget against actual net sales per shop and month |

## Tests

`tests/metadata/` holds rules over what the project is (every model has a grain, staging reads only mapped models, marts never read sources).
`tests/models/` holds cases for the logic that is easy to get wrong: allocations that must add up exactly, the shipment split, the name split, the status mapping.
Try breaking one: change `ELSE order_discount_cents - ...` in `fct_sales_lines.sql` and run `dbdatabuild project tests run`.

## Things to try

- `dbdatabuild project show graph --column marts.agg_daily_sales.net_amount` follows a number back to the source columns.
- `dbdatabuild project show graph +marts.rpt_budget_vs_actual` and `dbdatabuild connection deploy --write-plan marts.fct_sales_lines+` (a selector) to see what a change touches.
- Add a mart (revenue per product category per month) and a test for it.

## Running the cycle on a database

The source tables are what `seeds/` generates; `connection seed` puts them in an empty SQL Server (or PostgreSQL) database you do not mind filling. What follows is the whole cycle, every output below
is from a run of these commands against SQL Server 2022 (ids, times and hashes differ in yours). Two things first:

- **Create the database case-sensitive**: `CREATE DATABASE retail COLLATE Latin1_General_100_CS_AS`. The project's string profile says text compares case-sensitively. In a database created with the server's
  default collation (`SQL_Latin1_General_CP1_CI_AS`) every command works, but `connection status` reports DDB-310 for `staging.orders.status`: a view column made of string literals takes the database's collation.
- The logins are environment variables, one pair per connection: `DBDATABUILD_SQLSERVER_READ` for what reads and `DBDATABUILD_SQLSERVER_WRITE` for what writes
  (`Server=127.0.0.1,1433;User Id=...;Password=...;Database=retail;TrustServerCertificate=true`; for a trial the two may be the same login).

**1. Compile, offline.** `dbdatabuild project compile` checks the project and writes `rendered/`: the lowered queries, one load script per model and connection, and the **load plans** of every load (`rendered/sqlserver/loads.yml`) and one file per **refresh plan** (`rendered/sqlserver/refresh/daily.yml`, `hourly.yml`: commit them, they document what the nightly and the hourly refresh run). The plans are declared in `dbdatabuild.yml` (`refresh.plans`); every mart is in `daily` because the folder file `models/marts/_dbdatabuild.yml` says so, and `fct_sales_lines` adds itself to `hourly` in its own file. `dbdatabuild project show refresh daily` reads one.

```
OK: 23 model(s) valid. 0 warning(s), 35 note(s).
46 rendered file(s); 46 written, 0 removed.
```

**2. Can the tool use the connection?** `dbdatabuild connection inspect sqlserver` connects with each login and says what is missing. It changes nothing.

```
  read login    DBDATABUILD_SQLSERVER_READ: sa connects; Microsoft SQL Server 2022 (RTM-CU26) ...
  write login   DBDATABUILD_SQLSERVER_WRITE: sa connects; ...; may create tables in the database
  tracking      schema dbdatabuild on sqlserver: not there yet
  schema names  marts (missing: a deploy creates it), staging (missing: a deploy creates it)
1 thing(s) in the way.
Next: dbdatabuild connection init sqlserver --apply
```

**3. Seed and initialize.** `connection seed --apply` creates the twelve tables of the mapped models and fills them (`Loaded 12 table(s), 4632 row(s).`; it stops at a table that exists, `--replace` recreates it).
`connection init --apply` creates the tracking tables (`Applied 14 statements. The tracking tables are ready.`). `inspect` now says `Nothing in the way.`

**4. Status.** `connection status sqlserver` compares the database with the compiled project and says what a deploy would do:

```
Structure  pending: a deploy would run 37 step(s) (0 risky, 0 destructive) and 11 load(s)
Deploy     none recorded
Refresh    none recorded
...
A plan now would have 48 step(s) (0 risky, 0 destructive) and ask 0 question(s) first; 0 blocked, 0 skipped.
```

**5. Deploy** (rare, reviewed, changes structure): write the plan, read `plans/sqlserver/<id>.plan.md`, apply it.

```
dbdatabuild connection deploy sqlserver --write-plan --accept-inferred
  Plan 2026-10-12-63bfc38a: 48 step(s) (37 ddl, 11 load, 0 backfill, 0 hook, 0 track); 0 risky, 0 destructive.
dbdatabuild connection deploy sqlserver --apply-plan plans/sqlserver/2026-10-12-63bfc38a.plan.yml
  Applied plan 2026-10-12-63bfc38a: 48 step(s) executed.
```

**6. Refresh** (frequent, unattended, data only). It reads the committed refresh plan, not the models, checks what `refresh.check` says (the default is `objects`) and repeats the 12 loads of the plan `daily` (the 11 routine loads, and `reconcile` of the sales lines, whose window of days a resolver computes from today's date; `connection refresh sqlserver hourly` runs only the sales lines):

```
dbdatabuild connection refresh sqlserver daily
  Refresh plan rendered/sqlserver/refresh/daily.yml: 12 of 12 member(s).
  Refresh ref-20261012T182419-9aca: 12 step(s) ran.
```

`status` now says `Structure  in sync: a deploy would change no structure (it would run 11 routine load(s))`.

**A window that moves every day.** `fct_sales_lines` has a second load, `reconcile`, which reloads the last days of the history by `date_key` (a line can change after it was loaded: a return, a corrected price). Its `start` and `end` are **inputs** with a resolver, written in the model:

```yaml
    inputs:
      days:  { type: INTEGER, default: 14 }
      start: { resolver: { expression: "CAST(strftime(current_date - days, '%Y%m%d') AS BIGINT)" } }
      end:   { resolver: { expression: "CAST(strftime(current_date + 1, '%Y%m%d') AS BIGINT)" } }
```

An `expression` is evaluated by DuckDB in the tool, on the clock of the day the refresh runs (UTC unless `timezone:` in `dbdatabuild.yml` says another zone), and reads no database. The plan `daily` gives `days: 30`. Without a database you can see what the window is right now, and with a person's value instead:

```
dbdatabuild project show refresh daily --resolve                    # @start ... now: 20260913   @end ... now: 20261014
dbdatabuild connection refresh sqlserver daily --dry-run            # the values each load would use, and where each came from
dbdatabuild connection refresh sqlserver marts.fct_sales_lines.reconcile --input days=4000   # the whole history; the resolver of start is still run, from days
dbdatabuild connection refresh sqlserver marts.fct_sales_lines.reconcile --input start=20240101
```

The event records every input with its source (`default`, `membership`, `flag`, `resolver:expression`), the hash of the resolver and the run's instant, so what a load did at two in the morning can be read the next day.

**7. Monitor.** `connection monitor sqlserver` lists the events (a deploy and a refresh are one event each), the DDL and loads they ran, the shapes recorded for each object and what needs attention:

```
Events (newest first) (2)
  when (UTC)           event                     lane     status     by  commit
  2026-10-12 18:24:19  ref-20261012T182419-9aca  refresh  completed  sa
  2026-10-12 18:24:15  2026-10-12-63bfc38a       deploy   completed  sa
```

- `--event ref-20261012T182419-9aca` shows one event: its states and the 11 loads with their row counts (`marts.fct_sales_lines default ok rows 2095`).
- `--lane refresh` leaves the deploy out, `--last 3` shows the three most recent rows of each history.
- `--attention` leaves only what needs attention (`Needs attention (0)  nothing`); a drift, a blocked object or a backfill that never happened appears there.

**8. The check levels.** `--check` (or `refresh.check` in `dbdatabuild.yml`) chooses what a refresh asks the database before it runs anything. To see them, add a column to `marts.dim_store` (in `models/marts/dim_store.sql`
and `.yml`), run `project compile`, and add a column to the table behind the tool's back (`ALTER TABLE marts.dim_store ADD note_added_outside nvarchar(20) NULL`). Then `connection refresh --dry-run --check <level>`:

| Level | Asks the database | Result here |
|---|---|---|
| `none` | nothing | runs (exit 0): the engine's own errors are the check |
| `project` | the tracking tables | stops: `DDB-447  The project expects structure 5015cc134118, but the last completed deploy (2026-10-12-63bfc38a) recorded 7b6e11d0892e.` |
| `objects` (default) | the tracking tables | stops: ``DDB-447  `marts.dim_store` was last deployed with shape 853abd01c48a, but the project expects 67b32c845ce9.`` |
| `live` | the catalog and the tracking tables | stops: ``DDB-447  `marts.dim_store` was changed outside the tool: its shape is 911ca6ad5431, the last one recorded is 853abd01c48a, and the project expects 67b32c845ce9.`` |

A failed check ends with `Nothing was executed` and exit code 1; `--on-fail warn` reports it and runs anyway (then the engine's own errors are the check: a load that needs the new column stops at that step).
Without the change made outside the tool and with the project compiled after a deploy, the check passes (`live` was run that way on the PostgreSQL walk of `adventureworks` and found nothing). A deploy brings the connection to the project's structure again.

**9. The lifecycle settings.** `dbdatabuild.yml` has a commented block (`plans:`, `refresh:`, `retention:`) with non-default values for a project like this one: a plan kept in the tracking tables and applied by its id, `refresh.check: live`,
statement logs kept 14 days. Uncommented, they act: `connection deploy --write-plan` then says ``Plan 2026-10-12-ca1128bf: 12 step(s) ... kept in the tracking tables of `sqlserver` (`plans.deploy.keep: database`); no file was written.``
and `connection deploy --apply-plan 2026-10-12-ca1128bf` applies it by that id; a plain `connection refresh` runs the `live` check. The settings are explained in `docs/concepts.md`.

## PostgreSQL

The project is set for SQL Server (see the comment at the top of `dbdatabuild.yml` for the PostgreSQL profile). With that profile and an empty PostgreSQL 17 database, all 23 models validate and the cycle above runs as written
with `postgres` as the connection (`Applied plan ...: 48 step(s) executed.`, `Refresh ...: 12 step(s) ran.`), and the conformance suite compares every mart with DuckDB's rows on both engines. `fct_shipment_packages` uses
`generate_series`, which SQL Server has from version 16 (2022; `connections: sqlserver: {version: 16}` says so).
