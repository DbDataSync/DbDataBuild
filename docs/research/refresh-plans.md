# Refresh plans, load plans and inputs

Status: design, agreed with the owner 2026-10-12 (the conversation that settled the terms, failure rules, overlap and subsets). **Step 1 of section 10 is built (entry 148); section 12 says where it differs.** It replaces three items of the lifecycle work: `refresh --backfill`, copies in a refresh, and `monitor --plan`. The semantics of resolvers (section 8) were reviewed and confirmed by the owner on 2026-10-12.

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
      start: { type: DATE, resolver: { memory: "current_date - days" } }
      end:   { type: DATE, resolver: { memory: "current_date + 1" } }
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
- Needs: a session per concurrent load through `MutationGate`, deterministic tracking writes and statement-log order, the event's steps as a partial order, tests on both engines. **Built after the single-threaded version.**

## 7. Seeing and selecting

| Where | What |
|---|---|
| `project show refresh [plan]` (offline) | plans, members, order, inputs, resolver text, hashes; `--resolve` evaluates `memory` resolvers now |
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
  start: { type: DATE, resolver: { memory: "current_date - days" } }
  end:   { type: DATE, resolver: { target: "SELECT max(order_date) + 1 FROM sales.orders" } }
  cutoff: { type: TIMESTAMP, resolver: { source: "SELECT max(updated_at) FROM orders" } }   # a copy: at the origin
  # several inputs from one resolver:
  window: { resolver: { memory: "SELECT current_date - 7 AS start, current_date + 1 AS end" }, provides: [start, end] }
```

The kinds, by where they run:

| Kind | Runs | Reads | Login |
|---|---|---|---|
| `memory` | in the tool, in a DuckDB `:memory:` database | nothing from any database: calculation only | none |
| `target` | on the load's own connection | tables of the project there | the connection's read login |
| `source` | on the origin of a copy (copies only: a query reads one connection, DDB-231, so data crosses connections only through a copy) | the origin's tables | the origin's read login |
| `target_max` | as today, shorthand for a `target` resolver | the watermark column | read login |

**The expression variant is not a separate language.** `memory: "current_date - days"` is shorthand for `SELECT current_date - days`, evaluated by DuckDB itself. A text without `SELECT` is wrapped, a text starting with `SELECT` is used as written. One mechanism, one parser, DuckDB's own functions (date arithmetic, `date_trunc`, `last_day`, intervals, `strftime`, case, coalesce, a lookup against a literal table).

### Semantics to settle

1. **Sandbox of `memory`.** A fresh in-memory database per resolution with external access off and configuration locked (no files, no network, no extension loading, no `ATTACH`), so the text can compute and nothing else. A table reference or a subquery over a table is a compile error (there is nothing to read). **Every DuckDB function is allowed for now**; whether to restrict them (an allow or a deny list) is a later discussion. The sandbox, not a function list, is what keeps a resolver from touching anything.
2. **The clock.** `now()`, `current_date`, `today()` are allowed and are the reason `memory` exists. One run has **one frozen instant** (`run_started`), so all loads of a run agree, and it is recorded in the event. `current_date` and `now()` mean **UTC** unless the project (or a connection) sets `timezone:`; the session's own zone is never used.
3. **Other inputs and parameters as variables.** A resolver may name the load's other inputs (`days` above), project and connection parameters (`${project.name}`, `${connection.name}`) and a fixed set of run variables (`run_started`, `plan`, `connection`). Inputs are resolved in dependency order; a cycle is a compile error. A `--input` override of `days` flows into `start`.
4. **Result shape.** One row. One column for one input, or named columns matching `provides`. More than one row, or none, is an error; NULL goes to the input's `on_null` (`fail` default, `initial`, `skip_load`). The value is cast to the declared type; a value that does not fit is an error naming the input and the resolver, never a silent coercion.
5. **Dialect.** `memory` is DuckDB text, the same for every connection, committed once. `target` and `source` resolvers are written in DuckDB dialect and lowered for the engine they run on, like a model's query (so the matrix and DDB-324 apply); a `source` resolver is lowered for the origin's engine. A `target` resolver is written over the project's models, not raw engine names.
6. **Safety.** `target` and `source` run through `ReadSession` only (the gate invariant), as a single `SELECT`, with a timeout and a one-row cap. They never write. A `source` resolver on an origin whose login is not in the environment is a load failure like an unreachable origin.
7. **When it runs, and a plan that moved.** In a refresh: per load, just before that load, with the frozen `run_started`. In a deploy: at plan time, and the values are shown in the plan. All computed values are treated alike (there is no stable or volatile split): `apply` resolves again, and if **any input differs from the plan** it blocks the deployment, shows each input with its planned and its new value, and in an interactive apply lets the deployer approve and continue with the new values. A non-interactive apply stops with exit 1 and, for each moved input, prints the override as it would be typed (`--input orders.backfill.start=2024-03-01`), so the deployer can append those arguments to the same command and run it again: the explicit way to say "yes, with these values". `--yes` does not approve moved inputs. Pinning an input with `--input` when planning has the same effect from the start (a pinned value is not computed, so it cannot move). Both values are recorded in the event. A clock-based input therefore moves at midnight and asks once; that is accepted as the conservative and simple rule.
8. **Overrides.** A `--input` or membership value replaces the resolver, which is not run. The event records the source of each input: `flag`, `membership`, `parameter`, `resolver:memory|target|source`, `initial`.
9. **Committed and hashed.** A `target`/`source` resolver's lowered text is rendered and committed under `rendered/<c>/` and hashed like a load script; a `memory` resolver is committed as its DuckDB text. A change to any resolver changes the load plan hash, and the refresh plan's hash with it. The event stores the resolver hash with the value.
10. **Visibility.** `project show refresh --resolve` evaluates `memory` resolvers offline (what would `start` be now). `refresh --dry-run` also resolves `target` and `source` ones read-only. A resolver that is slow is visible: its duration is recorded.
11. **Values are logged** (the existing accepted choice, DESIGN 6.6): a resolver is expected to return watermark-like values.

## 9. Tracking

New columns on the event and load rows (`refresh_plan`, plan hash, selection, membership, inputs with sources, attempt, duration, rows) and a table for plan members at run time. This goes in **one tracking layout bump (layout 7)**, with `init --upgrade` from 6, as layouts 5 and 6 were done (init never alters tables).

## 10. Build order

1. Load plans and refresh plans: `refresh:` in models and folder files, `refresh.plans` in the project, `loads.yml` and `refresh/<name>.yml`, `project show refresh`, the positional plan, memberships with names and input overrides, `--input`, selectors. Layout 7. `monitor`/`status` views.
2. Failure rules (`on_failure`, outcomes, `--only-failed`), the per-load lock and `on_busy`, `expected_every` and duration warnings.
3. Resolvers: `memory`, then user `target`, then `source`; resolvers for range inputs; `provides`; stable/volatile.
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
