# Refresh plans, load plans and inputs

Status: design, agreed with the owner 2026-10-12 (the conversation that settled the terms, failure rules, overlap and subsets). **Steps 1 to 4 of section 10 are built (entries 148, 150, 151 to 153, 154); sections 12 to 15 say where the builds differ.** It replaces three items of the lifecycle work: `refresh --backfill`, copies in a refresh, and `monitor --plan`. The semantics of resolvers (section 8) were reviewed and confirmed by the owner on 2026-10-12.

Background: `docs/concepts.md` (deploy and refresh), `docs/research/lifecycle-model.md`, DESIGN.md 6.6 (load operations), 6.5.2 (copies).

## 1. The model in one paragraph

A **load** moves rows into a model's table. An incremental load, a backfill, a reconcile of deleted rows, a filling of a new column and a copy are all loads. They differ in their strategy and in the values they need, not in kind. A backfill is not special and not inherently risky; risk comes from the effect of a load (it deletes and reinserts a range), not from its name. A **refresh plan** is a named, project-wide set of loads for a connection (`hourly`, `daily`), run in dependency order. Models say which refresh plans their loads belong to. A person can always override any input of any load, in a scheduled run too.

## 2. Terms

| Term | Meaning |
|---|---|
| load | any movement of rows into a model's table: a load operation (DESIGN 6.6) or a copy |
| load plan | the compiled form of one load of one model on one connection: script and hash, declared inputs with where each default comes from, hooks, the shape expected. Today this is one entry of `refresh.plan.yml` |
| refresh plan | a named, project-wide set of loads for one connection, in dependency order, with its failure and overlap rules |
| membership | one place where a load appears in a refresh plan, with its own name inside the plan, input values and failure settings |
| input | a named, typed value a load needs (`watermark`, `start`, `end`, `days`) |
| resolver | the thing that computes an input's default (section 8) |
| selection | the subset of a plan a run was asked for (section 7) |

## 3. Declaring

In the model file, next to `loads:` (the key layers like `tags`, so a folder file can put every model beneath it in a plan):

```yaml
loads:
  incremental: { strategy: watermark_append, watermark: { column: updated_at, resolver: target_max } }
  backfill:
    strategy: delete_insert_by_range
    inputs:
      days:  { type: INTEGER, default: 7 }
      start: { type: DATE, resolver: { expression: "current_date - days" } }
      end:   { type: DATE, resolver: { expression: "current_date + 1" } }
refresh:
  hourly: incremental
  daily:  { load: backfill, inputs: { days: 60 } }
  weekly: { load: backfill, inputs: { days: 400 } }
  # the same load twice in one plan needs a name for each membership:
  # monthly: [ { load: backfill, as: recent, inputs: { days: 7 } }, { load: backfill, as: last_year, inputs: { start: "...", end: "..." } } ]
```

- A load names what it does and what it needs and gives defaults. A membership has a body only to change inputs or settings for one cadence.
- A membership's name is the load's name unless `as:` is given; it must be unique within a plan (DDB code to be assigned).
- A model with a default load and no `refresh:` anywhere (itself or in a folder file) is in the plan `default`, as today. Declaring any `refresh:` replaces that implicit membership.
- The project declares its plans in `dbdatabuild.yml`, so a typo in a model's plan name is a diagnostic:

```yaml
refresh:
  check: objects
  on_fail: block
  plans:
    hourly: { title: Orders and shipments, description: "...", on_failure: continue, on_busy: skip, expected_every: 1h, parallel: 1 }
    daily:  { on_failure: continue, expected_every: 1d }
```

- Plans, loads and memberships carry `title`, `description`, `tags` and `properties` like every other object (OPEN-ITEMS section T).
- A **copy** declares its loads the same way: an incremental one (a watermark) in `hourly`, a full one that reconciles deleted rows in `daily`.

## 4. Compiled files

```
rendered/<c>/loads.yml                  every load plan of every model on the connection
rendered/<c>/refresh/<name>.yml         one file per refresh plan: members in order, each naming a load plan by hash,
                                        with input overrides and failure settings
```

- `loads.yml` is complete, so any load can be run on its own, in a plan or not. Both files are deterministic, hash-carrying and committed, and `project compile --check` covers them.
- A refresh plan file is small and reviewable: it reads as "what runs hourly".
- `refresh.plan.yml` goes away (breaking, no alias; its content moves into the two files).
- A load that cannot run without a person (an input with no default and no resolver) is not left out. It is in `loads.yml` and may be named in a plan only if the membership gives the input. A plan that names it without a value is a compile error, since a scheduled run has no one to ask.

## 5. Running

```
connection refresh [<connection>] [<plan>] [<selector> ...]
connection refresh prod hourly
connection refresh prod hourly tag:finance exclude:tag:slow
connection refresh prod orders.backfill --input days=30      # one load, no plan
```

- No plan given means `default`. If the project has no `default` plan the command lists the plans.
- `--input [model.]name=value` overrides any input of any load. `model.name` is needed when the name is ambiguous. The override replaces the resolver, which is then not run at all, so a broken resolver never stands between an administrator and a fix.
- Precedence for an input's value, strongest first: a run-time flag, a value in the membership, a project or connection parameter, the resolver, `initial`/`on_null`. The event records every value and its source.
- Overrides never change the plan or its hash.
- An ad hoc run (a load named directly) is an event with no plan name; checks and failure rules still apply.
- A refresh never changes structure.

## 6. Failure, overlap, parallelism

### Failure

Each load ends `done`, `failed`, `skipped` (with a reason: `upstream failed`, `stopped`, `check failed`, `busy`) or `not run`. A failed refresh check is a load failure of kind `check`, handled the same way. An unreachable origin of a copy is a load failure; there is no separate `on_unavailable`. A load is transactional, so a failed load leaves its table as it was (to be verified for copies, which stage first).

| `on_failure` (plan default, a membership may override) | The failed load | Dependents in the plan | Other loads |
|---|---|---|---|
| `stop` | recorded | `not run` | `not run`; loads in flight finish |
| `continue` (default) | recorded | `skipped: upstream failed` | run |
| `ignore` | recorded as a warning | run on the stale data | run |

- Only dependents inside the plan are skipped. A model that reads the failed table from another plan is untouched; `monitor` and `status` show it reading stale data.
- Outcome of a plan run: `completed`, `completed_with_failures`, `stopped`, `failed`. Exit code 1 for anything but `completed`, with a summary line naming what failed and what was skipped.
- `--only-failed` runs the loads that failed or were skipped in the plan's last run. A full re-run is also safe, since loads are idempotent.
- `retry: { count, wait }` on a plan or membership, with each attempt recorded: later.

### Overlap

The lock moves from the connection to the **load** (connection and model): two runs touching different models run side by side; a deploy still takes the whole connection. A run that finds a load locked, by the earlier run of the same plan or by another plan, applies `on_busy`:

| `on_busy` | |
|---|---|
| `skip` (default) | that load is skipped this cycle (`skipped: busy`). An incremental load loses nothing; the next run's watermark covers the gap |
| `wait` | queue, with an optional `max_wait` |
| `fail` | the load fails; `on_failure` applies |

- **A busy skip is not a failure and dependents still run**, on the last committed data (a load is transactional), marked `upstream skipped: busy`. The upstream/downstream effect system is to be re-evaluated as a whole later; this is good enough for now (owner).
- Locks are session locks (SQL Server application lock, PostgreSQL advisory lock): a crashed run frees them when its session ends.
- Duration and rows of every load are recorded. `status` warns when a plan's last run exceeded its `expected_every`, and `monitor` names the load that took the time. `max_runtime` (plan or load, opt-in) cancels the statement in flight; the transaction rolls back and the load fails.

### Parallelism

- Loads whose in-plan upstream loads are done may run together: `parallel: n` on a plan and `--parallel n` on the command; the default is 1, today's behaviour.
- `max_parallel` on a connection protects an origin system from a fan-in copy opening many sessions.
- Under `stop`, loads in flight finish and none starts; under `continue`, dependents of a failed load are skipped.
- Needs: a session per concurrent load through `MutationGate`, deterministic tracking writes and statement-log order, the event's steps as a partial order, tests on both engines. **Built after the single-threaded version (step 5, section 16).**

## 7. Seeing and selecting

| Where | What |
|---|---|
| `project show refresh [plan]` (offline) | plans, members, order, inputs, resolver text, hashes; `--resolve` evaluates `expression` resolvers now |
| `connection refresh --dry-run` | resolves target and source resolvers read-only and prints the values it would use |
| `connection monitor [<c>] [<plan>]` | the runs of a plan; for each member its last success and failures; partial runs apart from complete ones |
| `connection status [<c>] [<plan>]` | one line per plan: last run, outcome, age, "overdue" past `expected_every`, "slow" past it in duration |
| tracking tables | the event carries `refresh_plan`, the plan hash and the selection; each load row carries `load`, `membership`, `inputs` with their sources, attempt, duration and rows; the plan's members are recorded when it runs (a view answers "what was in `hourly` at that run") |

- The plan is a positional argument everywhere, so there is no `--plan` or `--refresh-plan` option. A stored deploy plan is shown by `--event <id>` or `project show plan`.
- **Selectors** are the model selector grammar the other commands have (`tag:`, `kind:`, `connection:`, `path:`, `property:`, `+model`, `@model`, `changed:`, `exclude:`), plus `load:<name>` and tags on loads. A selection keeps dependency order. Loads not selected are not failures, and neither are their dependents.
- The event records the selection. `monitor` and `status` show "last complete run" and "last partial run" apart: a plan that only ever ran in part is not healthy.

## 8. Inputs and resolvers (confirmed 2026-10-12)

### What exists

One resolver, `target_max` (generated `MAX(column)` on the target, less the lookback), for the single `@watermark` of `watermark_append`. The range strategy's `@start`/`@end` have no resolver. The loader rejects any other resolver name.

### A resolver computes one or more inputs

```yaml
inputs:
  days:  { type: INTEGER, default: 7 }
  start: { type: DATE, resolver: { expression: "current_date - days" } }
  end:   { type: DATE, resolver: { target: "SELECT max(order_date) + 1 FROM sales.orders" } }
  cutoff: { type: TIMESTAMP, resolver: { source: "SELECT max(updated_at) FROM orders" } }   # a copy: at the origin
  # several inputs from one resolver:
  window: { resolver: { expression: "SELECT current_date - 7 AS start, current_date + 1 AS end" }, provides: [start, end] }
```

The kinds, by where they run:

| Kind | Runs | Reads | Login |
|---|---|---|---|
| `expression` | in the tool, evaluated by DuckDB in a `:memory:` database | nothing from any database: calculation only | none |
| `target` | on the load's own connection | tables of the project there | the connection's read login |
| `source` | on the origin of a copy (copies only: a query reads one connection, DDB-231, so data crosses connections only through a copy) | the origin's tables | the origin's read login |
| `target_max` | as today, shorthand for a `target` resolver | the watermark column | read login |

**An expression is not a separate language.** `expression: "current_date - days"` is shorthand for `SELECT current_date - days`, evaluated by DuckDB itself. A text without `SELECT` is wrapped, a text starting with `SELECT` (with no `FROM`, so several columns can feed `provides`) is used as written. One mechanism, one parser, DuckDB's own functions (date arithmetic, `date_trunc`, `last_day`, intervals, `strftime`, case, coalesce, a lookup against a literal table).

### Semantics to settle

1. **Sandbox of `expression`.** A fresh in-memory database per resolution with external access off and configuration locked (no files, no network, no extension loading, no `ATTACH`), so the text can compute and nothing else. A table reference or a subquery over a table is a compile error (there is nothing to read). **Every DuckDB function is allowed for now**; whether to restrict them (an allow or a deny list) is a later discussion. The sandbox, not a function list, is what keeps a resolver from touching anything.
2. **The clock.** `now()`, `current_date`, `today()` are allowed and are the reason `expression` exists. One run has **one frozen instant** (`run_started`), so all loads of a run agree, and it is recorded in the event. `current_date` and `now()` mean **UTC** unless the project (or a connection) sets `timezone:`; the session's own zone is never used.
3. **Other inputs and parameters as variables.** A resolver may name the load's other inputs (`days` above), project and connection parameters (`${project.name}`, `${connection.name}`) and a fixed set of run variables (`run_started`, `plan`, `connection`). Inputs are resolved in dependency order; a cycle is a compile error. A `--input` override of `days` flows into `start`.
4. **Result shape.** One row. One column for one input, or named columns matching `provides`. More than one row, or none, is an error; NULL goes to the input's `on_null` (`fail` default, `initial`, `skip_load`). The value is cast to the declared type; a value that does not fit is an error naming the input and the resolver, never a silent coercion.
5. **Dialect.** `expression` is DuckDB text, the same for every connection, committed once. `target` and `source` resolvers are written in DuckDB dialect and lowered for the engine they run on, like a model's query (so the matrix and DDB-324 apply); a `source` resolver is lowered for the origin's engine. A `target` resolver is written over the project's models, not raw engine names.
6. **Safety.** `target` and `source` run through `ReadSession` only (the gate invariant), as a single `SELECT`, with a timeout and a one-row cap. They never write. A `source` resolver on an origin whose login is not in the environment is a load failure like an unreachable origin.
7. **When it runs, and a plan that moved.** In a refresh: per load, just before that load, with the frozen `run_started`. In a deploy: at plan time, and the values are shown in the plan. All computed values are treated alike (there is no stable or volatile split): `apply` resolves again, and if **any input differs from the plan** it blocks the deployment, shows each input with its planned and its new value, and in an interactive apply lets the deployer approve and continue with the new values. A non-interactive apply stops with exit 1 and, for each moved input, prints the override as it would be typed (`--input orders.backfill.start=2024-03-01`), so the deployer can append those arguments to the same command and run it again: the explicit way to say "yes, with these values". `--yes` does not approve moved inputs. Pinning an input with `--input` when planning has the same effect from the start (a pinned value is not computed, so it cannot move). Both values are recorded in the event. A clock-based input therefore moves at midnight and asks once; that is accepted as the conservative and simple rule.
8. **Overrides.** A `--input` or membership value replaces the resolver, which is not run. The event records the source of each input: `flag`, `membership`, `parameter`, `resolver:memory|target|source`, `initial`.
9. **Committed and hashed.** A `target`/`source` resolver's lowered text is rendered and committed under `rendered/<c>/` and hashed like a load script; an `expression` resolver is committed as its DuckDB text. A change to any resolver changes the load plan hash, and the refresh plan's hash with it. The event stores the resolver hash with the value.
10. **Visibility.** `project show refresh --resolve` evaluates `expression` resolvers offline (what would `start` be now). `refresh --dry-run` also resolves `target` and `source` ones read-only. A resolver that is slow is visible: its duration is recorded.
11. **Values are logged** (the existing accepted choice, DESIGN 6.6): a resolver is expected to return watermark-like values.

## 9. Tracking

New columns on the event and load rows (`refresh_plan`, plan hash, selection, membership, inputs with sources, attempt, duration, rows) and a table for plan members at run time. This goes in **one tracking layout bump (layout 7)**, with `init --upgrade` from 6, as layouts 5 and 6 were done (init never alters tables).

## 10. Build order

1. Load plans and refresh plans (with their tests, docs and the `hourly`/`daily` plans in the `retail` and sample multi-connection templates): `refresh:` in models and folder files, `refresh.plans` in the project, `loads.yml` and `refresh/<name>.yml`, `project show refresh`, the positional plan, memberships with names and input overrides, `--input`, selectors. Layout 7. `monitor`/`status` views.
2. Failure rules (`on_failure`, outcomes, `--only-failed`), the per-load lock and `on_busy`, `expected_every` and duration warnings.
3. Resolvers: `expression`, then user `target`, then `source`; resolvers for range inputs; `provides`; the moved-input rule of a deploy (section 8, item 7: block, show the new values, print the `--input` overrides to append).
4. Copies as loads in a refresh (needs 3 for `source`, the origin check, and `max_parallel`).
5. Parallelism.

## 11. Parked (owner: do address later)

* **User-defined reload strategies** and the general idea of declaring a load's own inputs of any type (a list of regions, a customer id, a status filter) used in a load body or in the strategy's wrapper. Touches the lowering and the guarantee that the executed text is the committed text. List-valued inputs need their own decision.
* `retry`, `max_runtime` details, cross-plan dependencies ("run `daily` after `hourly` completes").
* Re-evaluating the upstream/downstream effect system as a whole.

## 12. As built, step 1 (entry 148)

Built as in sections 1 to 7, 9 and 10 (step 1). Where the build decided something the design left open or went another way:

* **Where `requires`, `project_hash` and `excluded` went.** `rendered/<c>/loads.yml` holds `project_hash` in its header, `shape_hash` on each load plan (what `requires` was: the check compares the shapes of the objects of the loads it is about to run) and `excluded` (models with no load plan, with the reason). A plan file has its own `excluded` (memberships left out of it: a copy between connections, an implicit `default` membership of a load that needs a person). A load plan's `hash` is the hash of its own text; a plan's member names it by that hash, so any change to a script, an input or a hook changes the plan file too.
* **Membership names are unique per model within a plan**, not across the plan: two models both have a load called `incremental`, and a member is addressed as `model.membership`. DDB-245.
* **An implicit `default` membership is never a compile error.** A model with no `refresh:` whose default load needs a person (a range load) is left out of `default` with the reason, as it always was; only a membership somebody wrote (DDB-449) is an error. The plan `default` always exists as a name, even when the project declares plans and does not list it.
* **A plan with no member still has a file** when the project declares it; `default` has one when anything is in it or was left out of it.
* **Settings parsed and stored, acting or not.** `on_failure`, `on_busy`, `expected_every` (`15m`, `1h`, `2d`, `1w`), `parallel` (1 to 64), `check`, `on_fail`, `title`, `description`, `tags`, `properties` are validated and written to the plan file; only `check` and `on_fail` act (flag, then plan, then connection, then project). Memberships take `on_failure` (stored). Titles, descriptions, tags and properties on memberships and loads are **not built** (OPEN-ITEMS U); plans have them. `refresh.plans` is project level only.
* **Selectors** need the project's models only for the model grammar (`tag:`, `+model` ...); `load:`, `exclude:load:`, patterns with `*` and `?` and a plain run work from the committed files. A name that looks like a plan (lower case, no dot) is read as the plan, so a directory selector is written `path:marts`.
* **Inputs.** The inputs the tool has today are a range load's `start` and `end` (no default: `required`), the watermark (a resolver), and the query parameters a load uses (project parameters). `inputs:` with defaults inside `loads:` is step 3. The sources recorded are `flag`, `membership`, `parameter`, `resolver:target` (the existing `target_max`) and `initial`; `run_log.inputs` holds them as JSON, and `run_log.parameters` keeps its older record of the same values.
* **Ad hoc runs** (a load named `model.load`) are events with no plan name; their `plan_hash` is the hash of the load plans' hashes, the selection column holds the names given.
* **Layout 7** is as section 9: `migration_log.refresh_plan` and `selection`, `run_log.membership`, `load_plan_hash`, `inputs`, `attempt` (1), `duration_ms`, and the table `refresh_plan_member` (not a view: the table answers "what was in `hourly` at that run"). `init --upgrade` adds the columns; `init` alone creates the table and does not claim a layout-6 store.
* **Views.** `connection monitor [plan]` shows the runs of a plan (complete and partial apart), and each member's last success and failure with its duration; `connection status` prints a line per plan; `project show refresh [plan]` reads the compiled files. No `expected_every` warning, no overdue or slow marks (step 2).
* **Not built in step 1**: failure rules and outcomes (`--only-failed`), the per-load lock and `on_busy`, retries, parallelism, resolvers other than `target_max`, `--dry-run` resolving read-only, `project show refresh --resolve`, copies in a refresh (a non-local copy is still left out with its reason), titles and tags on memberships, loads and `load:` by tag. The metadata documents were not changed (the plans are in the compiled files).

## 13. As built, step 2 (entry 150)

Failure rules, the lock per load, `--only-failed`, `max_runtime` and the `expected_every` warnings are built as in section 6. Where the build decided something the design left open:

* **No layout 8.** A load's state and reason are the text of `run_log.status` (`ok`, `failed`, `failed: check | max runtime | busy | input`, `skipped: upstream failed | busy | stopped | check failed`, `not run`); every selected load has a row, also one that never ran. The event's `migration_log.status` holds the outcome (`completed`, `completed_with_failures`, `stopped`, `failed`).
* **`failed` outcome** means no load was done and something failed; `stopped` is a `stop` failure or the operator's Ctrl-C; `ignore` and busy skips do not change the outcome (an ignored failure is a warning line and a row).
* **A failed refresh check** on an object is a failure of kind `check` of that object's loads (the rules then apply: with the default `continue` the other loads run). A finding about the connection as a whole still stops a blocking refresh before anything runs and records nothing. This changed an old behaviour: a refresh refused by the check used to leave no event; now it leaves one with the loads `failed: check`.
* **The lock.** Per load: the connection's lock shared plus `<connection lock>:<model>` exclusive, for the length of that load. The deploy's exclusive connection lock therefore conflicts with any running load. A refresh that finds a deploy running applies `on_busy` to each load.
* **`wait` with `max_wait`** that runs out makes the load fail (kind `busy`), not skip.
* **Dependents** are found through the dependency graph in `loads.yml` (section 16, entry 166), so no project file is read. A loads file compiled before the graph was carried has none: the project graph is then read, only when something fails or is skipped, and if the project cannot be read every later load counts as reading it.
* **`max_runtime`** is for one load with its hooks together, on the plan (`max_runtime`) or the load (`loads.<name>.max_runtime`, which wins and is in the load plan's hash); units `s m h d w`. `max_wait` is a plan setting. A membership has no `max_runtime` or `on_busy` of its own yet.
* **Warnings** (`OVERDUE`, `SLOW`) do not change an exit code. A run still going is not slow; it is overdue once it has run longer than `expected_every`. A plan that never ran and declares `expected_every` is overdue.
* **Exit 4** is now returned (it was only documented), for a deploy refused with DDB-439.

## 14. As built, step 3 (entries 151 to 153)

Inputs, resolvers and the moved-input rule are built as in section 8. Where the build decided something the design left open or went another way:

* **Declaration.** `inputs:` is a key of a load (`schemas/model.schema.json`, `$defs/loadInputs`). Names are lower snake case. An entry is a mapping with `type`, `default`, `resolver` (exactly one of `expression`, `target`, `source`), `on_null` and `provides`. The inputs a script binds are the load's parameters; `start` and `end` of a range load take the range column's type when `type` is left out; any other name is a **free** input (recorded in the event, never bound to the script). A `default` beside a resolver is only the value `on_null: initial` falls back to (so it is an error without that). A group (`provides`) has no type and no default; the provided inputs are declared separately (or are `start`/`end`) and have no resolver of their own. The watermark stays in its block; `resolver: target_max` inside `inputs` is refused with a message that says so (it is the shorthand for a `target` resolver, recorded as `resolver:target`).
* **Copies and range loads.** A copy has no `columns`, so a `delete_insert_by_range` load of a copy gives the types in `params: {start: DATE, end: DATE}` beside `column:` (new; without `params` it is DDB-105). A copy is still left out of a refresh plan (step 4), so its `source` resolvers run in a deploy only; the declaration, the compile (one file per engine of the copy's origins, `input.<load>.<input>.<engine>.sql`), the plan (a set of inputs per origin, a load step per origin) and the apply (computed again at the origin on its read login) work and are tested on SQL Server and PostgreSQL in both directions.
* **Where the text lives.** An `expression` is in the input's entry of `loads.yml`, with the `${project.x}`/`${connection.x}` parameters it names and their values at compile time; a `target`/`source` resolver is the rendered file under `rendered/<c>/<model>/` with its hash in `loads.yml` (the design said "under rendered/<c>/"; this keeps the file the engine runs the same file that is hashed). `loads.yml` entries for inputs gained optional keys (`free`, `provided_by`, `on_null`, `initial`, `resolver`); a load without declared inputs is byte-for-byte what step 2 wrote.
* **New sources.** `default` (a value the load's `inputs:` give) joins `flag | membership | parameter | resolver:expression | resolver:target | resolver:source | initial` (section 8 item 8 wrote `resolver:memory` for the expression). The run's instant is a pseudo input `run_started` (source `run`) in `run_log.inputs` of any load whose expression read the clock; no layout 8.
* **Expressions.** Evaluated by DuckDB; the clock functions (`now`, `current_date`, `today`, `current_timestamp`, `localtimestamp`, `current_time` and the rest of that family) are redefined in the sandbox with the run's frozen instant, and `TimeZone` is set to the project's zone for the functions that read a TIMESTAMPTZ. A name DuckDB reserves (`end`) has to be written in double quotes. The compile asks DuckDB for the parse tree: any table, table function, CTE, set operation or qualified name is DDB-451, a name that is not an input, a parameter or a run variable too.
* **Skip.** `on_null: skip_load` records the load as `skipped: no value` (not a failure, the plan's outcome stays `completed`, what reads it runs and says `upstream skipped: no value`). In a deploy it plans no load and says so in the notes.
* **Moved inputs.** The watermark resolver of a deploy is handled as one of the computed inputs (planned value `resolver_result`, name `watermark`); DDB-437 no longer comes from a resolver that moved. `--input` at apply replaces the plan's value (any input of the load, computed or not; the planned value is recorded beside the bound one in `run_log.parameters`). A plan whose input is `skip_load` at apply but was not at plan time is DDB-454 as well. A dry-run apply checks too.
* **Not as the design said.** `--input` takes `[model[.load].]name=value` (the design wrote `model.load.input=value`: both work). Deploy has `--input` for planning and for applying. The interactive question is asked once for all moved inputs; `--yes` leaves it unasked and the apply stops.
* **Open.** Restricting the functions an expression may call; a resolver for the watermark other than `target_max`; list-valued inputs; `max_parallel` (step 5). Copies in a refresh are built in step 4 (section 15).

## 15. As built, step 4 (entry 154)

Copies are loads of a refresh as in sections 1, 2, 3 and 6. Where the build decided something the design left open or went another way:

* **The copy is part of the load plan.** `rendered/<c>/loads.yml` gains an optional `copy:` block on the load plan of a copy that has origins on another connection: `staging` (the table, `<tracking schema>.stg_<schema>__<table>`), the text that creates it and the text that drops it, the declared columns, the watermark (column, type, lookback) of an incremental copy, and `origins:`, one per connection of the origin, each with `connection`, `engine`, `table`, `read` (the SELECT in the origin's dialect, every column quoted, in the declared order), `read_incremental` (the same with `WHERE <column> >= @watermark`), `slice` (column, value, whether the tool adds the column) and, when the load has a `source` resolver, `inputs` (the inputs as that origin's engine runs them). The block is inside the load plan's text, so it is hashed with it: any change to an origin, a column or the staging table changes the plan's hash. A load plan without `copy:` is what step 3 wrote. The design said "origin(s) recorded where the design says": the design does not say; the origin is recorded in the `transfer` row of `run_log` (`load_name` is `from <origin>`, as a deploy has always recorded it), which now also carries the load's membership, plan hash and inputs.
* **The steps are the deploy's.** The same function (`Planner.CopyTransfer`) builds the transfer for a deploy and for the compiled load plan, so the text sent to an origin is the same in both. For each origin: transfer into the staging table, hooks, load, hooks; then one drop of the staging table. The load step of each origin has that origin's inputs.
* **The origin check** is made when the copy's turn comes, under the load lock, before anything is written: each origin must be a connection of the project, its read login must be in the environment (`DBDATABUILD_<ORIGIN>_READ`) and it must answer `SELECT 1` through `ReadSession`. Any failure is `failed: origin` (new `LoadStatus.FailedOrigin`; it needs no new column or code), the reason names the origin and the variable, or the exception type and the driver's error number, never the driver's message. `on_failure` applies as for any failed load. A transfer that fails later (a value that does not fit, a dropped session) is `failed`, as a deploy's. The refresh has no up-front check of origins (a deploy's refuses the whole plan): the transfer steps are kept out of the plan the engine opens, so one unreachable shop fails one load.
* **The check does not compare the origin's table with its declaration** (a deploy's `CopyOriginCheck`, DDB-230, and `on_mismatch`): a refresh asks the connection nothing about structure; the transfer's own check that the read returned the declared columns is the net. `on_mismatch: skip` therefore has no effect in a refresh.
* **Bounds are worked out per run.** An incremental copy's bound for each origin (the newest value of the watermark column in the destination, for that origin's rows when there is a slice, less the lookback) is read when the copy's turn comes, so a load sees what the loads before it wrote. A full re-read of an incremental copy has no form in a refresh (a deploy has `plan --full-refresh`).
* **Not in a refresh:** a copy of a native model (a query or a command on the origin) is left out of the plan, with that reason. The staging table is created in the tracking schema by the refresh's write login, which is DDL on scratch, not on a model's table.
* **`source` resolvers** run in a refresh, once for each origin, on that origin's read login; a copy of several origins records the origin on each input in `run_log.inputs`. The moved-input rule is a deploy's.
* **Order and skipping** need nothing new: a copy's query reads its staging table, so the project graph puts the models that read the copied table after the copy, and a failed copy skips them through the same graph.
* **`max_parallel` is not built.** Section 10 lists it with step 4, but it limits the sessions a parallel run opens to one origin, and a run has one load at a time until step 5. It is left for step 5.

## 16. As built, step 5 (entry 155)

Loads that do not read each other run together, as in sections 5 and 6. Where the build decided something the design left open or went another way:

* **The setting.** `parallel: n` on a plan (1 to 64, already stored) and `--parallel n` on `connection refresh`; the flag beats the plan, the default is 1, and 1 is the engine of steps 1 to 4 exactly (one load after another, in the order of the plan, on the run's own sessions, no project graph read until something fails). The effective number is also the number of loads selected at most, and is printed (`parallel` in the document, one line in the text) when it is above 1. **A dry run goes one load at a time** (it executes nothing, and what it prints stays in the order of the plan); the line says so.
* **What a load waits for.** A load starts when every earlier load of the run that it reads has ended, and an earlier load of the same model has ended (two loads of one model exclude each other through the model's lock, so running them together would only make one skip as busy). "Reads" is the dependency graph in `loads.yml` (`DownstreamOf`, transitive through models that are not in the plan: the rows are the transitive closure). A loads file with no graph (compiled before entry 166) runs one load at a time, which is the same as every earlier load counting as read. Among the loads that are ready the one that comes first in the plan starts first, up to `parallel` at once. The order of the plan is a dependency order already, so only earlier loads are waited for. What the graph cannot see (a hook that writes a scratch table another load reads, a native model's own reads) is not ordered: the person who writes it says it by splitting the plan or running it at 1.
* **Failure rules, unchanged in words.** `continue`: a failed load skips what reads it (`skipped: upstream failed`) and everything else runs. `stop`: **no new load starts and the loads in flight finish** (they are not cancelled: a load is transactional and a cancelled statement would only add a rollback; the design said "loads in flight finish"); the loads that had not started are `not run`, in the order of the plan, and the outcome is `stopped`. `ignore`: a warning, what reads it runs. Which loads were in flight when a `stop` failure ended is whatever was running at that moment, so a `stop` run is not reproducible load for load; its rules are. The operator's first Ctrl-C behaves as a `stop` failure (running loads finish, `skipped: stopped` for the rest). A busy skip lets dependents run and says `upstream skipped: busy`; when several upstream loads were skipped the note names the first in plan order. `on_busy: wait` keeps its place while it waits. `--only-failed`, `max_runtime`, `max_wait` and `expected_every` are per load and need nothing new.
* **Sessions.** The run keeps its first read session and write session for its own event rows (the start, the plan's members, the rows of loads that never ran, the outcome). Each load that runs together gets a **worker**: a read session, a write session (a sibling of the run's `MutationGate`, same command, permissions, statement log and ordinal) and, when the records go to another connection, a tracking write session. Workers are opened when a load first needs one, reused by later loads and closed at the end, so a run holds at most `parallel` workers: about `2 x parallel + 2` sessions at the destination (`3 x parallel + 4` with central tracking, split across the two connections). A session that cannot be opened fails the load that wanted it (`failed`, the exception type and error number). Every statement still goes through `MutationGate` (writes) or `ReadSession` (reads).
* **Shared state.** The statement log is one file per run: a line is written whole, each statement's ordinal is taken and its opening line written as one step, so the ordinals are unique and rise down the file; the lines of different loads interleave, each line names its step. `run_log` rows are written by each load on its own session as it goes (a row's identity is the run and the step number, which are unique); the event rows of `migration_log` are written by the coordinator only, the start before any load and the outcome after the last one. The console is written by the coordinator only.
* **Order of the output.** The result list (text lines `failed:`, `skipped:`, `not run:`, the JSON `results`, `outcomes`, `warnings`) is in the order of the plan, whatever order the loads ended in. The progress line `load n/m: <load>` is printed when the load starts, so progress lines are in start order, which depends on timing; nothing else is.
* **`max_parallel`** on a connection (`connections.<name>.max_parallel`, 1 to 64, `config.schema.json`; no limit by default): the most sessions **one run** may have at that connection at once. At an **origin** it counts the read sessions a refresh opens for a copy: the origin check, a `source` resolver and the transfer's read (each holds its place while it is open; a session waits for a free place in the order asked). At the **destination** it caps `parallel` (the effective number is the smaller; a line says when the cap bit), because there the thing counted is loads, each of which uses two sessions. The tracking connection of central tracking is not counted. The limit holds within one run: another process (a second refresh, a deploy) has its own, and two runs at once can exceed it.
* **Not done:** dividing `parallel` between plans of several runs; a limit on the transfers of one load that has several origins (they run one after another inside the load); an adaptive cap that backs off when an origin answers slowly; `retry`.

## 17. As built, after the review of steps 4 and 5 (entry 156)

The owner reviewed the open decisions of steps 4 and 5 (`docs/research/refresh-decisions-steps-4-5.md`, each has a "Decided" line). Where the build changed or added to sections 14 to 16:

* **An origin is checked when its own transfer's turn comes** (decision A2), in a refresh and in a deploy. The check is the same for both: the origin must be a connection of the project, its read login must be in the environment, and it must answer `SELECT 1` through `ReadSession` (`OriginAccess.CheckAsync`). The engines ask for it through `ApplyOptions.CheckOrigin`, just before each `transfer` step (never in a dry run). In a refresh a failure ends the load `failed: origin` (a `run_log` row under the transfer's step, so `--only-failed` repeats the load), **the origins before it stay loaded** (each origin is its own unit; the rows are replaced by key, so repeating the load is safe) and `on_failure` applies. The check no longer happens for all origins before the first write. Section 15's "origin check ... every origin ... before anything is written" and section 16's `max_parallel` count of "the origin check" (one session per origin, when its turn comes) are read in this light. What is still done before the first origin: the `source` resolvers of the load are resolved (a resolver that cannot run at an origin fails the load as `failed: input`, naming the origin) and the bound of an incremental copy is read from the destination.
* **A deploy asks no origin before its first statement either.** The up-front probe of every origin (`ApplyEngine`) and the refusal for a missing read login (`ApplyCommand`) are gone; an origin that is not a connection of the project, and a native command on a connection that does not allow them, are still refused at once (they are the plan's own mistakes, not the state of a system). **The structure check (DDB-230) moved with it**: `plan` no longer reads any origin; `apply` compares the origin's table with its declaration just before that origin's transfer. `on_mismatch: fail` stops the apply there (the steps before it have run; applying the same plan continues it), `skip` leaves that origin's transfer and load out of this apply (the outcomes say `skipped`; a warning names the origin), and the other origins go on. The plan always has a transfer for every origin, whatever their state.
* **A refresh still does not compare an origin's table with its declaration** (decision A1: keep). A low-priority discussion item in OPEN-ITEMS U records the idea of doing it under `check: live`.
* **A copy declares a second load that re-reads in full** (decision A3). A copy takes `loads:` like any model. For a copy `strategy: full_replace` means everything of the origin being loaded: no watermark bound on the read (`read`, not `read_incremental`, in the load plan of that load; `@watermark` is absent from a deploy plan), and the load replaces all the destination holds for that origin (the whole table without a slice; for a fan-in the origin's rows by the slice column, as the implicit load does, so another origin's rows stay). The `key` of a `delete_insert_by_key` or `merge_by_key` load of a sliced copy that does not name one is the unique key plus the slice column (the implicit load's key). A person or a plan picks it: `refresh: { hourly: default, weekly: { load: full } }`, `connection refresh <connection> <copy>.full`, `connection deploy --op <copy>=full`. `fan_in_fan_out` declares `default` and `full` and a plan `weekly` (a test deletes an order at a shop and the weekly refresh removes it). **`connection deploy --full-refresh` is removed** (the release 0.5.0 has not been cut): it re-read an incremental copy from the start while still replacing by key, which is not what a person expects of "full refresh" (a deleted row stayed). It was removed cleanly: its only effect was to leave out the watermark bound, which the `full` load now does and says.
* **The copy stays in `loads.yml`; the resolver text joins it** (decision A5). `project compile` no longer writes `rendered/<c>/<model>/input.<load>.<input>[.<engine>].sql`. A `target` or `source` resolver is `resolver: {kind, text, engine}` in the input's entry (`name:` beside it), inside the load plan and so hashed with it; a `source` resolver has one entry per origin with the text lowered for that origin's engine. `file` and `file_hash` are gone from the resolver (`resolver_file` from the JSON of `project show refresh`). The run's `resolver_hash` of an input is now over the kind, the text and the parameters it names. Stale `input.*.sql` files of an earlier compile are removed by the next compile. (The watermark resolver of a load, `load.<op>.resolve.sql`, is another thing and is unchanged.)
* **A graph that is not there is said** (decision B3; changed by entry 166: the graph is compiled into `loads.yml`, not read from the project). With `--parallel` above 1 and a loads file that carries no graph, `connection refresh` runs one load at a time, prints `parallel: 1 (the loads file has no dependency graph, ...)`, adds the same text to `warnings`, and the document has `parallel_effective` (`parallel` stays the number asked for, capped by `max_parallel`).
* **Unchanged on purpose:** A4 (the refresh's write login creates and drops the staging table in the tracking schema; a configuration of staging tables is an open item), A6, B1, B2, B4, B5, B6.
