# Open decisions from refresh plans, steps 4 and 5

For the owner's review. Both steps are merged to `main` (entries 154 and 155; `refresh-plans.md` sections 15 and 16 describe them as built). Each decision below says what was built, shows it, gives the alternatives, and ends with a recommendation. Nothing here blocks anything; the defaults are what runs today. **The owner has decided every item; each ends with a "Decided" line, and entry 156 built the changes (`refresh-plans.md` section 17).**

Samples come from `templates/fan_in_fan_out` (two shop systems, `store_east` and `store_west`, copied into a SQL Server warehouse `sqlserver`, with a PostgreSQL `postgres` for reporting, and central tracking on `sqlserver`).

Contents: step 4 (A1 to A6), step 5 (B1 to B6), then a summary table.

---

## Step 4: copies in a refresh

The copy that the samples use:

```yaml
# models/warehouse/orders.yml
name: warehouse.orders
kind:
  copy:
    from: pos.orders
    slice: {column: store_id, value: "${origin.store_id}", type: "VARCHAR(10)"}
    unique_key: [order_id]
    watermark: {column: ordered_on, lookback: 7 days}
    on_mismatch: fail
connections=: [sqlserver, postgres]
refresh: { hourly: default, daily: default }
```

Run it:

```
dbdatabuild connection refresh sqlserver hourly
```

For each origin (`store_east`, `store_west`) the refresh checks the origin, reads into a staging table, runs the load, then drops the staging table once at the end.

### A1. A refresh does not compare an origin's table with its declaration

**Built.** A deploy runs `CopyOriginCheck` (DDB-230): it asks the origin's catalog whether the table still has the declared columns, and `on_mismatch` says what to do (`fail` refuses, `skip` leaves that origin out and loads the others). A refresh asks no connection about structure, by design ("a refresh that would need a structure change stops and tells you to deploy"). So in a refresh `on_mismatch: skip` does nothing. The safety net is the transfer itself: it checks that the read returned the declared columns, and a mismatch fails the load.

**What it looks like.** East shop adds a column to `pos.orders`; the next refresh reads the declared columns only (every column is named in the read), so the load keeps working. If a declared column is dropped or renamed, the read fails:

```
load 1/3: warehouse.orders
failed: transfer from store_east (OperationalError 42703)
```

The deploy would have said `DDB-230` before touching anything.

**Alternatives.**
1. Keep as is (a mismatch is a failed load).
2. Make the origin's structure check part of `refresh.check: live`. That level already means "ask the catalog"; it would then check origins too and honour `on_mismatch: skip`.
3. Always check origins in a refresh. Costs one catalog query per origin per run, and reintroduces the "a refresh asks about structure" rule for copies only.

**Recommendation.** 1 now, 2 later. It gives `on_mismatch: skip` a meaning in a refresh for people who chose the slow check, without taxing the hourly path.

**Decided (owner):** keep as is. A low-priority open discussion item (an origin structure check in a refresh, perhaps under `check: live`) is in OPEN-ITEMS U.

### A2. One unreachable origin fails one load (not the whole plan)

**Built.** A deploy refuses the whole plan before its first statement when any origin cannot be reached or has no read login. A refresh checks each origin when the copy's turn comes, under the load lock, before anything is written to the destination:

```
connection refresh sqlserver hourly       (DBDATABUILD_STORE_WEST_READ is not set)

load 2/3: warehouse.orders
failed: origin store_west: DBDATABUILD_STORE_WEST_READ is not set
skipped: marts.daily_sales (upstream failed: warehouse.orders)

outcome: completed_with_failures        (exit 1)
```

The unreachable origin fails the **copy as a whole** (all origins are checked before the first write), so no shop's rows are loaded without the other's. `on_failure` applies as for any load: `continue` (the default) runs everything that does not read the copy; the report is skipped.

**One thing to confirm.** After the check, the copy loads origin by origin (transfer, hooks, load, hooks, per origin). If the second origin's transfer fails part-way (a dropped session, a value that does not fit), the first origin's rows are already loaded. The load is `failed` and `--only-failed` repeats it, and the rows are replaced by key, so the repeat is safe; but for a moment the warehouse has east's new rows and not west's. If that matters, the alternative is to stage all origins first and then load once.

**Alternatives.**
1. Keep (partial across origins only on a mid-transfer failure; the repeat repairs it).
2. Stage every origin, then load once (all-or-nothing across origins; more staging space, one big load).
3. Refuse the whole plan up front, as a deploy does (one missing login stops hourly for everything).

**Recommendation.** 1. A refresh runs unattended every hour; an unrelated model should not wait on a shop's login. Take 2 if you ever need cross-shop consistency within a run.

**Decided (owner):** do not check all origins up front. A refresh checks each origin just before that origin's own transfer (reachable, read login, `SELECT 1` through `ReadSession`); a failure fails the load `failed: origin` as before, and the origins already loaded stay loaded (each origin is its own unit). A deploy no longer refuses the plan up front either: each origin is checked when its transfer's turn comes, and the DDB-230 structure check moved to that point (`on_mismatch: skip` still leaves that origin out). Built in entry 156. A configuration to choose one commit per source or one for all sources is a future item in OPEN-ITEMS U.

### A3. An incremental copy cannot be fully re-read inside a refresh

**Built.** A deploy has `connection deploy --full-refresh` (re-read everything). A refresh has no equivalent: an incremental copy always reads from the newest value held for that origin, less the lookback. This is why `fan_in_fan_out` has no separate full copy in `daily`; `daily` runs the same incremental copy as `hourly`.

**What is lost.** Rows deleted at the origin stay in the warehouse. Rows updated at the origin without moving `ordered_on` and older than the lookback are never re-read.

**Today's workarounds.**
* Set the lookback to what your data needs (`lookback: 30 days`).
* Run a deploy with `--full-refresh` when needed.
* A second copy model with no watermark, in the `daily` plan only:

```yaml
name: warehouse.orders_full
kind: {copy: {from: pos.orders, slice: {column: store_id, value: "${origin.store_id}", type: "VARCHAR(10)"}, unique_key: [order_id]}}
refresh: { daily: default }
```

**Alternatives.**
1. Leave it; the full-copy model above does the job.
2. A named second load on the same copy model (`reconcile`, a full re-read), in `daily`, as `retail`'s `reconcile` does for incremental loads:
   `refresh: { hourly: incremental, daily: { load: reconcile } }`.
3. `connection refresh ... --input mode=full` on an ad hoc `warehouse.orders.<load>`.

**Recommendation.** 1 now. 2 is the right shape, and it is the "user-defined reload strategies" item you parked; build it there, not as a special case for copies.

**Decided (owner):** no implicit `--full-refresh`. A copy declares a named second load that re-reads in full (`loads:` with `default` and `full`, a `full_replace`), usable in a plan (`weekly: { load: full }`) or ad hoc (`<copy>.full`); `fan_in_fan_out` shows it. `connection deploy --full-refresh` is removed (0.5.0 is not cut). Built in entry 156.

### A4. The refresh's write login creates and drops a staging table

**Built.** The transfer needs a place to put the origin's rows before the load. A deploy creates it in the tracking schema and drops it; a refresh now does the same:

```yaml
# rendered/sqlserver/loads.yml (copy: block, abridged)
copy:
  staging: ddb.stg_warehouse__orders
  create: CREATE TABLE ddb.stg_warehouse__orders (...)
  drop:   DROP TABLE IF EXISTS ddb.stg_warehouse__orders
  origins:
    - { connection: store_east, engine: postgres, table: pos.orders, read: "SELECT ...", read_incremental: "SELECT ... WHERE ordered_on >= @watermark" }
    - { connection: store_west, ... }
```

This is DDL, but on scratch, not on a model's table: "a refresh never changes structure" still holds for models. It means the refresh's write login needs `CREATE TABLE` in the tracking schema, which a read-and-insert-only login may not have. A failed load leaves the staging table until the copy runs again (the create is `drop and create`); a deploy behaves the same.

**Not verified.** A write login without that right. It should fail as a load (`failed`), not crash; the case was not run.

**Alternatives.**
1. Keep (create and drop per run).
2. Create the staging table once, in a deploy, and have the refresh only truncate and fill it. The refresh login then needs no DDL; the staging table is part of the deployed structure and visible in `status`. Two runs of the same copy cannot overlap anyway (the load lock).
3. Stage in a session-local temporary table (no leftovers). Does not work across the separate sessions a transfer uses.

**Recommendation.** 1 now; plan 2 if a least-privilege refresh login is a requirement, since it removes the leftover-table case and the DDL grant at once. I would test the no-rights case before 0.5.0 either way.

**Decided (owner):** leave as is. An OPEN-ITEMS U item "Staging tables configuration" records the wish to choose native session-scoped temp tables, custom session-scoped temp tables or persistent staging tables.

### A5. The copy's text is inside `loads.yml`

**Built.** The `copy:` block (staging, create/drop text, origin reads in each dialect) is part of the load plan's text, hashed with it. Step 3 went the other way for resolver text: `target` and `source` resolvers are separate rendered files (`input.<load>.<input>[.<engine>].sql`) with only the hash in `loads.yml`. So the project now has two conventions for "SQL the tool generated for a load".

**Why it matters.** A reviewer reads a diff of `loads.yml` for a one-line change in an origin's read; the SQL is long, quoted inside YAML, and for a many-origin fan-in it repeats per origin. Separate files diff like SQL.

**Sample of the alternative.**

```
rendered/sqlserver/warehouse/orders.copy.stage.sql             (create + drop)
rendered/sqlserver/warehouse/orders.copy.store_east.sql        (read and read_incremental)
rendered/sqlserver/warehouse/orders.copy.store_west.sql
```

with `loads.yml` holding the file names and hashes.

**Alternatives.** 1. Keep. 2. Split into files like resolvers.

**Recommendation.** 2, before 0.5.0 (the layout has not been released, so it breaks nothing). It is a mechanical change in the compile step with golden files to regenerate. If you would rather not spend the effort now, 1 is correct and only less pretty.

**Decided (owner):** the copy's text stays in `loads.yml`, and the resolver text moves into it too (the input's entry, beside its name, hashed with the load plan); the separate `input.*.sql` files and the code that wrote and read them are gone. Built in entry 156.

### A6. `fan_in_fan_out` changed behaviour

**Built.** `warehouse.orders` was a full copy left out of the plans. It is now incremental (watermark on `ordered_on`, 7 days of lookback, `unique_key: [order_id]`) and in both `hourly` and `daily`, so the template shows a refresh that reads two shops. Anyone who copied the old template gets a different model.

**Recommendation.** Accept. It is the point of the template; the README explains it. (There was no `stores` model; the earlier "stores copy stays excluded" note was about this one.)

**Decided (owner):** nothing to change.

---

## Step 5: parallelism

Set it on a plan, or on the command (the flag wins, default 1):

```yaml
# dbdatabuild.yml
refresh:
  plans:
    hourly: { expected_every: 1h, parallel: 4 }
connections:
  store_east: { engine: postgres, max_parallel: 2 }   # at most 2 sessions of one run at this origin
  sqlserver:  { max_parallel: 3 }                     # at most 3 loads of one run at the destination
```

```
dbdatabuild connection refresh sqlserver hourly --parallel 3
```

### B1. `on_failure: stop` lets running loads finish

**Built.** With `parallel: 3`, loads A, B, C start; A fails under `on_failure: stop`.

```
t0  start A, B, C
t1  A fails                 -> nothing new starts
t2  B, C finish (not cancelled)
    D, E, F                 -> not run
outcome: stopped            (exit 1)
```

The design said "loads in flight finish". They are not cancelled because a load is transactional and cancelling would only add a rollback. The consequence: which loads were in flight when `A` failed depends on timing, so two `stop` runs of the same project can differ in what completed (`B` done in one, `B` not run in another if it had not started). The rules are reproducible; the exact set is not.

**Alternatives.**
1. Keep: finish what is running.
2. Cancel in-flight loads (`max_runtime`-style: cancel the statement, roll back, `failed: stopped`). Cleaner "stop means stop", at the cost of wasted work and rolled-back loads that were nearly done.
3. A plan setting `on_stop: finish | cancel`.

**Recommendation.** 1. A nearly done load should finish; the operator's Ctrl-C already behaves this way. 3 can come with user-defined strategies if anyone asks.

**Decided (owner):** keep as is.

### B2. Two loads of one model never overlap

**Built.** If a plan has `fct_sales_lines` in both `default` and `reconcile`, the scheduler runs them one after the other even with `parallel: 8`; the second waits for the first to end.

**Why.** They exclude each other through the model's lock, so running them together would make one skip as `busy` (not a failure, dependents still run), and the skip would look like a bug. Waiting is what the person meant.

**Alternatives.** 1. Keep. 2. Let them start together and let the lock decide (`on_busy` applies). 

**Recommendation.** 1.

**Decided (owner):** keep as is.

### B3. If the project graph cannot be read, the run is silently sequential

**Built.** The scheduler orders loads by the project graph (which models read which). If the project cannot be loaded at run time (a refresh does not otherwise load the models), every earlier load counts as "read", which is a sequential run. `--parallel 8` then behaves like `--parallel 1` with no message.

```
connection refresh sqlserver hourly --parallel 8
...                                         (runs one at a time; no line says why)
```

**Why it is a decision.** It is the safe fallback, but a person who asked for 8 and got 1 deserves to be told, and the same fallback already exists elsewhere (`DownstreamOf`).

**Alternatives.**
1. Keep silent.
2. Print one line: `parallel: 1 (the project graph could not be read; loads run in plan order)`, and add `parallel_effective` to the JSON document.
3. Fail with a diagnostic when `--parallel` is above 1 and the graph cannot be read.

**Recommendation.** 2. Cheap, no new failure, and removes the surprise. The reason belongs in `warnings` too.

**Decided (owner):** print the line, add it to `warnings`, and add `parallel_effective` to the refresh JSON document (`schemas/output.schema.json`). Built in entry 156.

### B4. A dry run ignores `--parallel`

**Built.** `connection refresh ... --dry-run --parallel 4` goes one load at a time and prints a line saying so. A dry run executes nothing, so there is nothing to overlap, and its output stays in plan order.

**Recommendation.** Accept. (If you want a dry run to show what would start together, that is a separate preview: "wave 1: A, B; wave 2: C" in `project show refresh`. Not built; say so if wanted.)

**Decided (owner):** keep as is.

### B5. What `max_parallel` limits

**Built.** `connections.<name>.max_parallel` (1 to 64; no limit by default):

| Where | What it counts | Effect |
|---|---|---|
| An origin (a copy's source) | Read sessions **one run** opens there: the origin check, a `source` resolver, the transfer read | A session waits for a free place, in the order asked. Tested on PostgreSQL: 3 sessions uncapped, 1 with `max_parallel: 1` |
| The destination | **Loads** (each uses two sessions: a read and a write) | Caps `parallel` itself; the effective number is the smaller, and a line says when the cap bit |
| The tracking connection (central tracking) | Not counted | |

```
connection refresh sqlserver hourly --parallel 6
parallel: 3 (max_parallel of sqlserver)
```

**Limits to know.**
* It holds within one run. A second refresh, or a deploy, has its own count, so two runs at once can exceed the limit. A true cross-process limit needs a shared counter (a lock table), not built.
* At the destination the unit is a load, at an origin it is a session. The same setting name means two things.
* It is not a limit on the origin database's own sessions from other tools.

**Alternatives.**
1. Keep, documented.
2. Split into two settings: `max_parallel` for origins (sessions), `max_loads` for the destination.
3. Make it a cross-process limit using the tracking tables (a slot table).

**Recommendation.** 1 now. If the dual meaning bothers you, 2 is a rename with no engine work. 3 only when someone runs several refreshes against one origin and sees it hurt.

**Decided (owner):** keep as is.

### B6. "Upstream skipped" names the first skipped load

**Built.** When a load's upstream loads were skipped as busy, its reason names one:

```
load 5/6: marts.daily_sales
upstream skipped: busy (warehouse.orders)
```

If both `warehouse.orders` and `shop.stores` were skipped as busy, only the first in plan order is named.

**Alternatives.** 1. Keep. 2. Name all, comma separated (the reason is stored in `run_log.status` text; limited length).

**Recommendation.** 1. Name all only if someone needs it; the plan's `results` list every skipped load anyway.

**Decided (owner):** keep as is.

---

## Summary

| # | Decision | Recommendation | Cost if changed |
|---|---|---|---|
| A1 | No origin structure check in a refresh | Keep; add to `check: live` later | small |
| A2 | One unreachable origin fails one load | Keep; confirm the partial-origin case | none, or medium for stage-all-then-load |
| A3 | No full re-read of a copy in a refresh | Keep; build as a named load with user-defined strategies | parked |
| A4 | Refresh creates and drops a staging table | Keep; test the no-rights case; deploy-created staging if least privilege is needed | medium |
| A5 | Copy text in `loads.yml` | Split into rendered files, before 0.5.0 | small to medium |
| A6 | `fan_in_fan_out` is incremental | Accept | none |
| B1 | `stop` lets running loads finish | Keep | none |
| B2 | Two loads of one model do not overlap | Keep | none |
| B3 | Unreadable graph means silent sequential | Print a line, add `parallel_effective` | small |
| B4 | Dry run ignores `--parallel` | Accept | none |
| B5 | `max_parallel` is per run; loads at the destination, sessions at an origin | Keep, documented; split the name if it bothers you | small (rename) |
| B6 | "Upstream skipped" names one load | Keep | none |

Not verified for any of these: Fabric; the terminal and web interfaces; a SQL Server origin under `max_parallel`; more than three simultaneous loads on a real engine; a fan-in copy with a SQL Server origin in a refresh; a `source` resolver on a copy with several origins in a refresh.
