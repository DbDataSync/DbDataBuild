# Command reference

Generated from the command tree of `dbdatabuild` (`UPDATE_GOLDEN=1 dotnet test tests/DbDataBuild.Tests.Unit` rewrites it; a test fails when it is out of date). Every command takes `--format json` to give one JSON document on standard output (see `schemas/output.schema.json`). The effect class says what a command may touch: offline only, repository files only, a target read-only, the tracking tables only, or a target's data or definitions (DESIGN.md section 9.1).

| Command | Reads and writes | Effect | Purpose |
|---|---|---|---|
| [`project create`](#project-create) | `P⇒P` | Repo files only (no target connection) | List the project templates built in, or create a ready-to-run project from one |
| [`project model create`](#project-model-create) | `P⇒P` | Repo files only (no target connection) | Create a model to start from: a definition and a placeholder query that runs, in the project's layout (a view, a full table, or an incremental one) |
| [`project model update`](#project-model-update) | `C→P` | Target read-only | Generate or update model definition files from the models' queries, and the mapped models (and the tables copies read) from their tables on the connections they are on; --offline reads no connection, --check writes nothing |
| [`project compile`](#project-compile) | `P⇒P` | Repo files only (no target connection) | Validate the project and write what is compiled from it (rendered/): the lowered queries and the load scripts per connection |
| [`project tests run`](#project-tests-run) | `P→` | Offline only | Run the project's tests: metadata rules (DuckDB SQL over the metadata views) in tests/metadata/ and model tests (given rows, expected rows) in tests/models/ |
| [`project tests list`](#project-tests-list) | `P→` | Offline only | List the project's tests with their kind, severity and tags (nothing is run) |
| [`project sample`](#project-sample) | `P→` | Offline only | Run models on generated or supplied sample data, offline |
| [`project seed`](#project-seed) | `P⇒P` | Repo files only (no target connection) | Run the seeds (DuckDB queries that generate the rows of the mapped models) into a DuckDB file |
| [`project import`](#project-import) | `C→P` | Target read-only | Export tables and views from a connection as mapped models (models/), so models over them bind offline (writes files only with --write) |
| [`project update`](#project-update) | `C→P` | Target read-only | Bring in everything that comes from connections: the definitions of the models, the metadata of the mapped models and of the tables copies read (columns, types, keys, indexes), and the dialect each Flight SQL server reports, committed in dialects.lock.yml; --offline reads no connection, --check writes nothing |
| [`project show loads`](#project-show-loads) | `P→` | Offline only | Print the model x connection x operation pairing table |
| [`project show graph`](#project-show-graph) | `P→` | Offline only | Show the dependency graph (which table each model reads), column lineage, or a diagram of it; selectors pick the part to show |
| [`project show metadata`](#project-show-metadata) | `P→` | Offline only | Print everything the tool knows about the project and its models (use --format json) |
| [`project show refresh`](#project-show-refresh) | `P→` | Offline only | Read the compiled refresh plans (offline): the plans of each connection, or one plan with its members in the order they run, the inputs each load takes and where each default comes from, what each load waits for (the dependency graph the loads file carries, with model kinds and depth), what was left out and the hashes |
| [`project show plan`](#project-show-plan) | `P→` | Offline only | Read plan files (offline, nothing applied): list a project's plans, or show one with its steps, risk, exact statements, report and what applying it would need to be allowed |
| [`project agent-kit`](#project-agent-kit) | `P⇒P` | Repo files only (no target connection) | Install the skill and JSON Schemas an AI coding agent needs to work in a project (lists them unless --write) |
| [`connection inspect`](#connection-inspect) | `C→` | Target read-only | Can the tool use this connection, and if not, why: whether each login is set, connects and as whom, what it may do, whether the tracking tables are ready, which schema names the models use exist |
| [`connection init`](#connection-init) | `P⇒T` | Tracking tables only | Create the tracking tables and their schema name (prints the script; --apply runs it) |
| [`connection status`](#connection-status) | `C→` | Target read-only | Where a connection stands: drift, blocks, what a deploy would do |
| [`connection deploy`](#connection-deploy) | `P⇒S` | Target writes (DDL and/or data, as the plan states) | Change a connection's structure to match the models: plan with questions, show the plan, apply it (--write-plan stops after writing the plan; --apply-plan applies a written one; --ack records a decision) |
| [`connection refresh`](#connection-refresh) | `P⇒D` | Target writes (data only) | Run the loads of a refresh plan that project compile wrote (the plan default unless one is named, all of it or the members the selectors choose), or one load named model.load, as an event of its own: it checks what refresh.check says first, never changes structure, and leaves what a refresh cannot run to a deploy |
| [`connection monitor`](#connection-monitor) | `C→` | Target read-only | The events (deploys and refreshes), DDL and load history, recorded shapes, and what needs attention; with a refresh plan named, that plan's runs and each member's last success and failure |
| [`connection compare`](#connection-compare) | `C→` | Target read-only | Compare the data of two tables, of one connection or across connections and engines (by digests): schemas, row counts and a key-based row diff (values are read only with --show-values) |
| [`connection seed`](#connection-seed) | `P⇒S` | Target writes (DDL and/or data, as the plan states) | Create the seeded tables (the mapped models) on a connection and fill them from the seeds (prints what it would do unless --apply) |
| [`connection publish`](#connection-publish) | `P⇒T` | Tracking tables only | Store the project and model metadata as JSON in the connection for introspection |
| [`ui terminal`](#ui-terminal) | `—` | Offline only | Interactive terminal interface: choose, plan and run operations (each action it runs declares its own effect) |
| [`ui web`](#ui-web) | `—` | Offline only | Web interface on this machine's loopback address: project health, lineage, models and their rendered scripts, tests, the support matrix (it runs only commands that read, unless started with --allow-apply) |
| [`ui mcp`](#ui-mcp) | `—` | Offline only | Model Context Protocol server on standard input and output: the commands as tools for an AI agent, the agent kit as resources (each tool it offers declares its own effect; commands that change a connection are offered only with --allow-writes) |
| [`help code`](#help-code) | `—` | Offline only | Long-form explanation of a diagnostic code |
| [`help matrix`](#help-matrix) | `—` | Offline only | Print the support matrix and portability report |
| [`help dialects`](#help-dialects) | `—` | Offline only | What a dialect profile is: its fields, the built-in dialects, how to add one for a system behind odbc or flightsql (with an example) |

## project create

List the project templates built in, or create a ready-to-run project from one.

Effect: Repo files only (no target connection). Reads and writes: `P⇒P`.

Usage: `dbdatabuild project create [template] [directory]`

| Argument | Meaning |
|---|---|
| `template` | The template to create (omit to list them) |
| `directory` | Where to write it (default: a directory named after the template) |

## project model create

Create a model to start from: a definition and a placeholder query that runs, in the project's layout (a view, a full table, or an incremental one).

Effect: Repo files only (no target connection). Reads and writes: `P⇒P`.

Usage: `dbdatabuild project model create <model> [options]`

| Argument | Meaning |
|---|---|
| `model` | The model's name, schema_name.object_name (marts.fct_orders) |

| Option | Meaning |
|---|---|
| `--connection` `<text ...>` | The connections it is built on (default: the project's default connections); repeat for several |
| `--kind` `<text>` | view (the default), full, incremental_by_unique_key or incremental_by_time_range |
| `--project` `<path>` | Project root (contains models/) |

## project model update

Generate or update model definition files from the models' queries, and the mapped models (and the tables copies read) from their tables on the connections they are on; --offline reads no connection, --check writes nothing.

Effect: Target read-only. Reads and writes: `C→P`.

Usage: `dbdatabuild project model update [paths]... [options]`

| Argument | Meaning |
|---|---|
| `paths` (several) | Model .sql or .yml files, or directories under models/ (default: every model) |

| Option | Meaning |
|---|---|
| `--accept-inferred` | Accept inferred proposals marked high certainty (names from paths, types from DuckDB, nullability from lineage) |
| `--answers` `<file>` | Answers file for the questions (see schemas/answers.schema.json) |
| `--check` | CI: fail if any definition is out of sync with its query; asks nothing, writes nothing |
| `--offline` | Read no connection: update only the definitions of models from their queries (no mapped model is compared with its table) |
| `--project` `<path>` | Project root (contains models/) |
| `--write` | Non-interactive: write the definitions without asking (needs --answers for any open questions) |

## project compile

Validate the project and write what is compiled from it (rendered/): the lowered queries and the load scripts per connection.

Effect: Repo files only (no target connection). Reads and writes: `P⇒P`.

Usage: `dbdatabuild project compile [models]... [options]`

| Argument | Meaning |
|---|---|
| `models` (several) | Model names (marts.fct_orders), model files, or directories (default: every model) |

| Option | Meaning |
|---|---|
| `--check` | CI: validate, and fail if the committed rendered/ files differ from a fresh compile; writes nothing |
| `--connection` `<text ...>` | Only these connections |
| `--content` | With --format json: put each rendered file's text in the document (files[].content), so a client can show the lowered and rendered scripts; validates and writes nothing |
| `--project` `<path>` | Project root (contains models/) |

## project tests run

Run the project's tests: metadata rules (DuckDB SQL over the metadata views) in tests/metadata/ and model tests (given rows, expected rows) in tests/models/.

Effect: Offline only. Reads and writes: `P→`.

Usage: `dbdatabuild project tests run [tests]... [options]`

| Argument | Meaning |
|---|---|
| `tests` (several) | Test names (tests/metadata/naming/x.sql is naming.x) or files (default: every test) |

| Option | Meaning |
|---|---|
| `--kind` `<text>` | Run only tests of this kind: metadata (tests/metadata) or model (tests/models) |
| `--limit` `<number>` | How many violating rows to show per test |
| `--project` `<path>` | Project root |
| `--strict` | Fail on warning-severity tests too |
| `--tag` `<text ...>` | Run only tests with this tag (repeat for several: any of them) |

## project tests list

List the project's tests with their kind, severity and tags (nothing is run).

Effect: Offline only. Reads and writes: `P→`.

Usage: `dbdatabuild project tests list [options]`

| Option | Meaning |
|---|---|
| `--kind` `<text>` | List only tests of this kind: metadata (tests/metadata) or model (tests/models) |
| `--project` `<path>` | Project root |
| `--tag` `<text ...>` | List only tests with this tag (repeat for several: any of them) |

## project sample

Run models on generated or supplied sample data, offline.

Effect: Offline only. Reads and writes: `P→`.

Usage: `dbdatabuild project sample [models]... [options]`

| Argument | Meaning |
|---|---|
| `models` (several) | Model names (marts.fct_orders), model files, or directories (default: every model) |

| Option | Meaning |
|---|---|
| `--data` `<path>` | A directory of CSV files named after declared models (staging.orders.csv) to use instead of generated rows |
| `--declared` | Also show the declared (mapped and native) models the models read |
| `--limit` `<number>` | Rows of each result to show (the row count is always complete) |
| `--project` `<path>` | Project root (contains models/) |
| `--rows` `<number>` | Rows generated for each declared model |
| `--scale` `<number>` | For declared models that have a seed (seeds/): the scale the seed reads with getvariable('scale'), usually how many of the main entity (default: the project's own) |
| `--seed` `<number>` | Seed of the generator: the same seed gives the same rows |

## project seed

Run the seeds (DuckDB queries that generate the rows of the mapped models) into a DuckDB file.

Effect: Repo files only (no target connection). Reads and writes: `P⇒P`.

Usage: `dbdatabuild project seed [options]`

| Option | Meaning |
|---|---|
| `--out` `<file>` | The DuckDB file to write (default: .dbdatabuild/seed.duckdb) |
| `--project` `<path>` | Project root |
| `--scale` `<number>` | The variable `scale` the seeds read, usually how many of the main entity (default: the project's own) |
| `--seed` `<number>` | The variable `seed` the seeds read: the same seed and scale give the same rows |

## project import

Export tables and views from a connection as mapped models (models/), so models over them bind offline (writes files only with --write).

Effect: Target read-only. Reads and writes: `C→P`.

Usage: `dbdatabuild project import [connection] [tables]... [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection to read (default: the project's only default connection) |
| `tables` (several) | Tables or views as schema_name.table_name, with * and ? as wildcards (default: refresh the mapped models the project already has) |

| Option | Meaning |
|---|---|
| `--check` | CI: fail if a descriptor differs from the table it describes; writes nothing |
| `--project` `<path>` | Project root |
| `--write` | Write the new and changed mapped models under models/ (without it the command only shows the diff) |

## project update

Bring in everything that comes from connections: the definitions of the models, the metadata of the mapped models and of the tables copies read (columns, types, keys, indexes), and the dialect each Flight SQL server reports, committed in dialects.lock.yml; --offline reads no connection, --check writes nothing.

Effect: Target read-only. Reads and writes: `C→P`.

Usage: `dbdatabuild project update [selectors]... [options]`

| Argument | Meaning |
|---|---|
| `selectors` (several) | What to update (default: every model, mapped model and connection): names, `schema.*` patterns, paths, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:key=value`, `connection:`, `path:`, `changed:<git ref>`, `exclude:<selector>` |

| Option | Meaning |
|---|---|
| `--accept-inferred` | Accept inferred proposals marked high certainty when updating the definitions of models |
| `--answers` `<file>` | Answers file for the questions of the model definitions (see schemas/answers.schema.json) |
| `--check` | CI: write nothing, show what would change (per model, mapped model and connection) and fail if anything would |
| `--offline` | Read no connection: update only the definitions of the models from their queries (no dialect is asked and no mapped model is compared with its table) |
| `--project` `<path>` | Project root |

## project show loads

Print the model x connection x operation pairing table.

Effect: Offline only. Reads and writes: `P→`.

Usage: `dbdatabuild project show loads [options]`

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root (contains models/) |

## project show graph

Show the dependency graph (which table each model reads), column lineage, or a diagram of it; selectors pick the part to show.

Effect: Offline only. Reads and writes: `P→`.

Usage: `dbdatabuild project show graph [models]... [options]`

| Argument | Meaning |
|---|---|
| `models` (several) | Selectors (default: every model): names, paths, `+model`, `model+`, `2+model`, `@model`, `kind:`, `tag:`, `property:key=value`, `connection:`, `path:`, `changed:<git ref>`, `exclude:...` |

| Option | Meaning |
|---|---|
| `--column` `<text>` | Follow one column (model.column) up to the columns it comes from and down to the columns built from it |
| `--columns` | Also show which column of which table each output column comes from |
| `--diagram` `<text>` | Print a diagram instead of the list: dot (Graphviz) or mermaid |
| `--project` `<path>` | Project root |

## project show metadata

Print everything the tool knows about the project and its models (use --format json).

Effect: Offline only. Reads and writes: `P→`.

Usage: `dbdatabuild project show metadata [models]... [options]`

| Argument | Meaning |
|---|---|
| `models` (several) | Model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:key=value`, `changed:<git ref>`, `exclude:...` (default: every model) |

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root |

## project show refresh

Read the compiled refresh plans (offline): the plans of each connection, or one plan with its members in the order they run, the inputs each load takes and where each default comes from, what each load waits for (the dependency graph the loads file carries, with model kinds and depth), what was left out and the hashes.

Effect: Offline only. Reads and writes: `P→`.

Usage: `dbdatabuild project show refresh [plan] [options]`

| Argument | Meaning |
|---|---|
| `plan` | A refresh plan by name; omit to list the plans of every connection |

| Option | Meaning |
|---|---|
| `--connection` `<text>` | Only the plans of this connection |
| `--project` `<path>` | Project root |
| `--resolve` | Evaluate the `expression` resolvers of the plan's loads now, offline (what `start` would be at this moment, in the project's time zone); a `target` or `source` resolver reads a database and is not run here, `connection refresh --dry-run` runs it. Needs a plan |

## project show plan

Read plan files (offline, nothing applied): list a project's plans, or show one with its steps, risk, exact statements, report and what applying it would need to be allowed.

Effect: Offline only. Reads and writes: `P→`.

Usage: `dbdatabuild project show plan [plan] [options]`

| Argument | Meaning |
|---|---|
| `plan` | A plan file (plans/<connection>/<id>.plan.yml); omit to list the project's plans |

| Option | Meaning |
|---|---|
| `--connection` `<text>` | When listing, only the plans of this connection |
| `--project` `<path>` | Project root |

## project agent-kit

Install the skill and JSON Schemas an AI coding agent needs to work in a project (lists them unless --write).

Effect: Repo files only (no target connection). Reads and writes: `P⇒P`.

Usage: `dbdatabuild project agent-kit [options]`

| Option | Meaning |
|---|---|
| `--check` | CI: fail if the installed kit differs from this version's; writes nothing |
| `--dir` `<text>` | Where to install the kit, relative to the project (default: .claude/skills/dbdatabuild) |
| `--mcp` | Also add the dbdatabuild MCP server to the project's .mcp.json (Claude Code starts it: `dbdatabuild ui mcp --project .`, read-only; other servers in the file are kept). With --write it writes the file, with --check it checks it, otherwise it only says what it would do. |
| `--project` `<path>` | Project root |
| `--write` | Install the files (default: list them and write nothing) |

## connection inspect

Can the tool use this connection, and if not, why: whether each login is set, connects and as whom, what it may do, whether the tracking tables are ready, which schema names the models use exist.

Effect: Target read-only. Reads and writes: `C→`. Lane: inspect.

Usage: `dbdatabuild connection inspect [connection] [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection to inspect (default: the project's only default connection) |

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root |

## connection init

Create the tracking tables and their schema name (prints the script; --apply runs it).

Effect: Tracking tables only. Reads and writes: `P⇒T`.

Usage: `dbdatabuild connection init [connection] [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection to initialize (default: the project's only default connection) |

| Option | Meaning |
|---|---|
| `--apply` | Run the script on the write login (default: print it for review and connect to nothing) |
| `--project` `<path>` | Project root (contains dbdatabuild.yml) |
| `--upgrade` | Bring tracking tables of an older layout (before 4) to this one first: adds the `connection` column to each record table (every existing row gets the connection being initialized), puts it in the primary keys and drops the old views; the script is printed for review like the rest |

## connection status

Where a connection stands: drift, blocks, what a deploy would do.

Effect: Target read-only. Reads and writes: `C→`. Lane: inspect.

Usage: `dbdatabuild connection status [connection] [models]... [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection to check (default: the project's only default connection) |
| `models` (several) | A refresh plan first, to see only that plan's line (named as `connection refresh` names it), then model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:key=value`, `changed:<git ref>`, `exclude:...` (default: every model that declares the connection) |

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root |

## connection deploy

Change a connection's structure to match the models: plan with questions, show the plan, apply it (--write-plan stops after writing the plan; --apply-plan applies a written one; --ack records a decision).

Effect: Target writes (DDL and/or data, as the plan states). Reads and writes: `P⇒S`. Lane: deploy.

Usage: `dbdatabuild connection deploy [connection] [models]... [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection to deploy to (default: the project's only default connection) |
| `models` (several) | Model names, files or directories to plan (default: every model that declares the connection) |

| Option | Meaning |
|---|---|
| `--accept-inferred` | Accept inferred proposals marked high certainty |
| `--ack` `<text>` | Record a decision instead of deploying, as kind:name: drift:<object> (it changed outside the tool), definition:<model> (an incremental model's query changed), history:<model.column> (a recorded backfill that never happened). Needs --reason |
| `--allow-destructive` `<text ...>` | Object (marts.fct) whose destructive steps are allowed; repeat for several objects |
| `--allow-dirty` | Apply from a working tree with uncommitted changes (recorded) |
| `--allow-risky` | Allow the plan's risky steps |
| `--answers` `<file>` | Answers file for the questions (see schemas/answers.schema.json) |
| `--apply-plan` `<file>` | Apply a plan file written by --write-plan (plans/<connection>/<id>.plan.yml). A plan that stopped part-way continues; a completed plan is refused |
| `--backfill` `<text ...>` | model=operation: plan that operation as a backfill (risky; needs --allow-risky to apply); the model has no routine load in this plan (repeatable) |
| `--dry-run` | Apply: run every check and print every statement; execute nothing |
| `--input` `<text ...>` | `[model.load.]name=value`: the value of an input of a load, instead of the one a resolver computes (repeatable). With the plan it pins the input: it is not computed, so it cannot move. With --apply-plan it is how a deployer says "yes, with this value" to an input that moved (the stop prints each override as it would be typed) |
| `--op` `<text ...>` | model=operation: load this model with a non-default operation (repeatable) |
| `--output` `<path>` | Where to write the plan files (default: plans/<connection>/) |
| `--param` `<text ...>` | model.operation.parameter=value: the value of a runtime parameter of a load operation, instead of an answers file (repeatable) |
| `--project` `<path>` | Project root |
| `--reason` `<text>` | With --ack: why the change is accepted (required; recorded with your login) |
| `--write-plan` | Plan and write the plan files, then stop: nothing is applied |
| `--yes` | Apply the plan without asking (for a script; risky and destructive steps still need their allowances, and it does not approve computed inputs that moved since the plan: `--input` does) |

## connection refresh

Run the loads of a refresh plan that project compile wrote (the plan default unless one is named, all of it or the members the selectors choose), or one load named model.load, as an event of its own: it checks what refresh.check says first, never changes structure, and leaves what a refresh cannot run to a deploy.

Effect: Target writes (data only). Reads and writes: `P⇒D`. Lane: refresh.

Usage: `dbdatabuild connection refresh [connection] [plan] [selectors]... [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection (default: the project's only default connection) |
| `plan` | The refresh plan to run (default: `default`); a load named directly as `model.load` runs on its own, in no plan |
| `selectors` (several) | Which members of the plan to run: model names, `tag:`, `kind:`, `connection:`, `path:`, `property:`, `+model`, `model+`, `@model`, `changed:<ref>`, `exclude:<selector>`, `load:<name>`, or patterns with * and ? (default: the whole plan) |

| Option | Meaning |
|---|---|
| `--allow-dirty` | Run from a working tree with uncommitted changes (recorded) |
| `--check` `<text>` | The safety check before the loads, instead of the project's `refresh.check`: none (nothing; the engine's errors are the check), project (this project was deployed here), objects (each object against what was last deployed), live (each object against the catalog now) |
| `--dry-run` | Run every check and print every statement; execute nothing |
| `--input` `<text ...>` | `[model.]name=value`: the value of an input of a load to run, instead of its default or the plan's (repeatable); it replaces a resolver, which is then not run, and is recorded as coming from the flag. `model.name` is needed when more than one load has the input |
| `--on-fail` `<text>` | What a failed check does, instead of the project's `refresh.on_fail`: block (stop before anything runs) or warn (report and run) |
| `--only-failed` | Run only the loads of the plan that did not end done in its last run: failed, skipped or not run (a full run again is also safe, loads are idempotent). Needs a plan and its recorded runs |
| `--parallel` `<number>` | How many loads may run at once, instead of the plan's `parallel` (1 to 64; default 1, one after another). A load starts when the loads of the plan that it reads are done; `max_parallel` on the connection caps it |
| `--project` `<path>` | Project root |

## connection monitor

The events (deploys and refreshes), DDL and load history, recorded shapes, and what needs attention; with a refresh plan named, that plan's runs and each member's last success and failure.

Effect: Target read-only. Reads and writes: `C→`. Lane: inspect.

Usage: `dbdatabuild connection monitor [connection] [plan] [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection (default: the project's only default connection) |
| `plan` | A refresh plan: show its runs (complete and partial apart) and each member's last success and failure, instead of the tables |

| Option | Meaning |
|---|---|
| `--attention` | Only what needs attention (the other tables are left out of the text; the document still has them) |
| `--event` `<text>` | One event in detail, by its id (as the first table shows it): its states, its DDL and its loads |
| `--lane` `<text>` | Only the events of this lane (and their DDL and loads): deploy or refresh |
| `--last` `<number>` | How many recent rows of each history to show |
| `--project` `<path>` | Project root |

## connection compare

Compare the data of two tables, of one connection or across connections and engines (by digests): schemas, row counts and a key-based row diff (values are read only with --show-values).

Effect: Target read-only. Reads and writes: `C→`. Lane: inspect.

Usage: `dbdatabuild connection compare [connection] [table] [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection (default: the project's only default connection) |
| `table` | The table or view to compare, as schema_name.table_name (a model's table, for example). Required; the connection comes before it |

| Option | Meaning |
|---|---|
| `--against` `<text>` | The table or view to compare it with, as schema_name.table_name |
| `--against-connection` `<text>` | Compare with the table on this connection (the same table name unless --against or --against-schema says another); the connections may be on different engines, and only digests of the values are compared |
| `--against-schema` `<text>` | Compare with the table of the same name under this schema name (a development copy, for example) |
| `--columns` `<text ...>` | Compare only these columns (and the key) |
| `--exclude-columns` `<text ...>` | Leave these columns out of the comparison |
| `--key` `<text ...>` | Columns that identify a row (default: the model's grain or unique key, or a mapped model's grain) |
| `--limit` `<number>` | With --show-values, how many sample rows of each kind of difference |
| `--project` `<path>` | Project root |
| `--show-values` | Read and show values: the smallest and largest of each column and sample rows of each difference (without it only counts are read) |

## connection seed

Create the seeded tables (the mapped models) on a connection and fill them from the seeds (prints what it would do unless --apply).

Effect: Target writes (DDL and/or data, as the plan states). Reads and writes: `P⇒S`.

Usage: `dbdatabuild connection seed [connection] [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection to load (default: the project's only default connection) |

| Option | Meaning |
|---|---|
| `--apply` | Create and fill the tables on the write login (default: print what would happen and connect to nothing) |
| `--project` `<path>` | Project root |
| `--replace` | Drop and recreate a seeded table that already exists (default: stop at the first table that exists) |
| `--scale` `<number>` | The variable `scale` the seeds read, usually how many of the main entity (default: the project's own) |
| `--seed` `<number>` | The variable `seed` the seeds read: the same seed and scale give the same rows |

## connection publish

Store the project and model metadata as JSON in the connection for introspection.

Effect: Tracking tables only. Reads and writes: `P⇒T`.

Usage: `dbdatabuild connection publish [connection] [models]... [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection (default: the project's only default connection) |
| `models` (several) | Model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:key=value`, `changed:<git ref>`, `exclude:...` (default: every model) |

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root |

## ui terminal

Interactive terminal interface: choose, plan and run operations (each action it runs declares its own effect).

Effect: Offline only. Reads and writes: `—`.

Usage: `dbdatabuild ui terminal [connection] [options]`

| Argument | Meaning |
|---|---|
| `connection` | Connection to work on (default: the project's only default connection) |

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root |

## ui web

Web interface on this machine's loopback address: project health, lineage, models and their rendered scripts, tests, the support matrix (it runs only commands that read, unless started with --allow-apply).

Effect: Offline only. Reads and writes: `—`.

Usage: `dbdatabuild ui web [options]`

| Option | Meaning |
|---|---|
| `--allow-apply` | Let the page apply plans (a person confirms each one by typing the plan's id and connection; needs the write login in this environment). Off by default: the page reads and plans only. |
| `--port` `<number>` | Port on the loopback address (default: a free one) |
| `--project` `<path>` | Project root to show |

## ui mcp

Model Context Protocol server on standard input and output: the commands as tools for an AI agent, the agent kit as resources (each tool it offers declares its own effect; commands that change a connection are offered only with --allow-writes).

Effect: Offline only. Reads and writes: `—`.

Usage: `dbdatabuild ui mcp [options]`

| Option | Meaning |
|---|---|
| `--allow-apply` | Let an MCP app (a page the host shows the person) apply plans: the person confirms in the app by typing the plan's target; the tools it calls are hidden from the model. Needs the write login in this environment. Independent of --allow-writes, which offers the commands to the model (each run then needs the person's approval). |
| `--allow-writes` | Also offer the commands that change a target or its tracking tables (apply, run, load-seeds, init, ack, publish-metadata). Off by default: a person runs those. |
| `--no-show` | Do not offer the `show` tool (the link to the page in a browser, or the app screen). The other tools, and the app for the tools that carry it, are unchanged. |
| `--project` `<path>` | Project root the tools work on |

## help code

Long-form explanation of a diagnostic code.

Effect: Offline only. Reads and writes: `—`.

Usage: `dbdatabuild help code <code>`

| Argument | Meaning |
|---|---|
| `code` | Diagnostic code, e.g. DDB-214 |

## help matrix

Print the support matrix and portability report.

Effect: Offline only. Reads and writes: `—`.

Usage: `dbdatabuild help matrix [options]`

| Option | Meaning |
|---|---|
| `--rewrites` | List the rewrites that make the engines give DuckDB's answers (what each does, where it is required, and what the engine does without it); `rewrites:` in dbdatabuild.yml or a model turns the optional ones off |

## help dialects

What a dialect profile is: its fields, the built-in dialects, how to add one for a system behind odbc or flightsql (with an example).

Effect: Offline only. Reads and writes: `—`.

Usage: `dbdatabuild help dialects`
