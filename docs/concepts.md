# Concepts: project and connection, deploy and refresh

> **Status, 2026-10-12.** Built: the commands below (entry 135) except the ones the implementation plan lists as *later* (`connection inspect`, `project model create`, `project tests list`, `connection tests run`); the connection is the first positional argument of a command (`connection deploy <c>`, optional when the project has one default connection; there is no `--connection` option on these, and `project compile` and `project model create` keep `--connection` as a filter that takes several); the compiled refresh plan and its checks (`none`, `project`, `objects`, `live`); tracking layout 5 (each event records its lane, and a deploy the structure it was made from) and layout 6 (the words about objects: title, description, tags, properties); the `plans:`, `refresh:` and `retention:` settings are read and act (`keep: committed|database|ephemeral`, `audit` for both lanes, the statement-log retention). Not yet: the deploy event as an id of its own and the drill-down of `connection monitor` (`docs/research/lifecycle-implementation.md`, phases 5 and 6). `docs/commands.md` is generated from the program and is always what exists now.

## The two things you work on

* A **project** is a directory of models. Working on it is **offline**: it reads and writes files and never opens a database.
* A **connection** is a database the project is built on. Working on it is **online**.

## The four questions you ask of a connection

| You want to know or do | Command | Changes the database? |
|---|---|---|
| Where does it stand? | `dbdatabuild connection status <c>` | no |
| Make its structure match the models | `dbdatabuild connection deploy <c>` | **structure** (tables, views, indexes) and the tracking tables |
| Bring its data up to date | `dbdatabuild connection refresh <c>` | **data**, never structure |
| What happened? | `dbdatabuild connection monitor <c>` | no |

*Deploy* is rare, reviewed, and records what it changed. *Refresh* is frequent, usually unattended, and repeats the same statements. A refresh that would need a structure change stops and tells you to deploy.

```mermaid
flowchart LR
    A["edit models<br/>project model update"] --> B["project compile"]
    B --> T["project tests run"]
    B --> S["connection status"]
    S --> D["connection deploy"]
    D --> R["connection refresh<br/>(scheduled)"]
    R --> M["connection monitor"]
    D --> M
```

## What a command reads and writes

Every command says, in its header and in `--help`, what it touches:

| Code | Reads | Writes | In words |
|---|---|---|---|
| `P→` | project | nothing | safe to run any time |
| `P⇒P` | project | project files | edits or generates files |
| `C→` | connection | nothing | reads the database only |
| `C→P` | connection | project files | reads the database to write files |
| `P⇒T` | project | tracking tables | records metadata or a decision |
| `P⇒S` | project, connection | **structure** | changes what exists |
| `P⇒D` | project, connection | **data** | moves data |
| `—` | nothing | nothing | reference text, or a server that hosts commands |

## Commands

### Working on the project (offline)

| Command | What it does | Disposition |
|---|---|---|
| `project create <dir>` | start a project from a template | `P⇒P` |
| `project model create <schema.object>` | add a model | `P⇒P` |
| `project model update [models]` | write or update the definitions of models from their queries | `P⇒P` |
| `project compile [models]` | check the project and write the compiled files (`rendered/`, refresh plans); `--check` for CI | `P⇒P` |
| `project tests run [tests]` | run the project's tests | `P→` |
| `project sample <models>` | run models on sample data | `P→` |
| `project seed` | build the sample source data | `P⇒P` |
| `project import <c> [tables]` | export a connection's tables as models | `C→P` |
| `project show loads\|graph\|metadata\|plan` | read-only views | `P→` |
| `project agent-kit` | install the skill for AI agents | `P⇒P` |

### Working on a connection (online)

| Command | What it does | Disposition |
|---|---|---|
| `connection inspect <c>` | can the tool use this connection, and if not why | `C→` |
| `connection init <c>` | create the tracking tables | `P⇒T` |
| `connection status <c>` | structure, data and what needs attention | `C→` |
| `connection deploy <c>` | change structure (interactive; or `--write-plan` then `--apply-plan`) | `P⇒S` |
| `connection refresh <c>` | run the routine loads | `P⇒D` |
| `connection monitor <c>` | the timeline of deploys and refreshes | `C→` |
| `connection compare <c> <table>` | compare the data of two tables | `C→` |
| `connection seed <c>` | put sample data into a sandbox database | `P⇒S` |
| `connection publish <c>` | store the project's metadata in the database | `P⇒T` |

### Interfaces and help

| Command | What it does |
|---|---|
| `ui terminal`, `ui web`, `ui mcp` | the terminal interface, the web page, the MCP server for AI agents |
| `help code <code>` | explain a diagnostic such as `DDB-214` |
| `help matrix` | the support matrix |

## Plans and events

A **plan** is a list of steps (statements, their risk and why). An **event** is one run of a plan. Ten refreshes a day are ten events that point at one plan.

* A **deploy plan** is made from the database's live state, so it can go stale. It is shown to you, you give the allowances for risky steps, and it is applied. A completed deploy plan cannot be applied twice. If a deploy stops part-way, running the same plan again continues it.
* A **load** moves rows into a model's table (an incremental load, a backfill, a reload of a range, a copy: they differ in strategy and in the values they need, not in kind). A **refresh plan** is a named, project-wide set of loads for a connection (`hourly`, `daily`), run in dependency order. The project declares its plans (`refresh: { plans: { hourly: { expected_every: 1h } } }` in `dbdatabuild.yml`) and each model says which plans its loads are in (`refresh:` in the model or, layered like `tags`, in a folder's `_dbdatabuild.yml`); a model with a default load and no `refresh:` is in the plan `default`. A **membership** is one place a load is in a plan, with its own name (`as:`) and the values it gives for the load's inputs.
* Compiling (`rendered/<connection>/`) writes every load of the connection as a **load plan** in `loads.yml` and one `refresh/<plan>.yml` per plan whose members name their load plan by hash. They are made from the project alone and committed like code: they document how the project operates every day. Values only known at run time (watermarks) are found when the refresh runs and recorded in the event, not in the plan. `connection refresh [plan] [selectors]` reads these files and the committed scripts they name; it does not load the models (only a selector such as `tag:` does). A person can override any input of any load with `--input`; the event records every value and where it came from (`flag`, `membership`, `parameter`, `resolver:target`, `initial`).

Where plans are kept, and how much is recorded, is the project's choice:

```yaml
plans:
  deploy:  { keep: committed, audit: full }      # plans/ in git, text also in the database
  refresh: { keep: committed, audit: standard }  # compiled plan in git; each event stores the plan hash
connections:
  dev:  { plans: { deploy: { keep: ephemeral, audit: minimal } } }   # made and applied in one step
```

| `keep` | In the project | In the database |
|---|---|---|
| `committed` | a file | yes |
| `database` | no | yes, in `plan_store`: `connection deploy --write-plan` keeps the plan there (it needs the tracking connection's write login) and prints its id; `connection deploy --apply-plan <id>` applies it |
| `ephemeral` | no | the hash only |

| `audit` | Recorded |
|---|---|
| `minimal` | the event and its steps |
| `standard` | adds the plan's hash, who ran it and the commit |
| `full` | adds the plan text and the statement log |

The tool records what happened to match the policy. It does not decide who reviews or approves.

## Before a refresh runs

A refresh can run without asking the database anything, or check first. The project's `refresh.check` (and `--check`) chooses:

| Level | Checks | Asks the database |
|---|---|---|
| `none` | nothing; the engine's own errors are the check | nothing |
| `project` | that the project's structure was deployed to this connection | the tracking tables |
| `objects` (default) | each object the refresh uses against what was last deployed | the tracking tables |
| `live` | each object against the database as it is now (also catches changes made outside the tool) | the catalog and the tracking tables |

When a check fails the refresh stops (`refresh.on_fail: block`) and names the objects that are behind and the deploy that last touched them; with `warn` it reports and runs anyway.

## Terms

| Term | Meaning |
|---|---|
| connection | a named database the project is built on |
| engine | the SQL dialect of a connection (`sqlserver`, `postgres`, `fabric`) |
| lane | deploy or refresh |
| event | one run of a plan, with an id like `dep-20261007T204550-a7f3` or `ref-…` |
| tracking tables | the tables the tool keeps in the database: plans, events, steps, shape history, decisions |
| statement log | a local file of each statement, written before it is sent |
| shape hash | the hash of an object's columns, types and nullability |
| drift | an object changed outside the tool |
| acknowledgement | a decision you record (with a reason) so the tool stops blocking on it |
| allowance | permission to run a risky or destructive step |
| backfill | reloading historical ranges of an incremental model |

The full lookup tables (terms, settings, every command with every parameter) are in `docs/research/lifecycle-model.md`, sections 7, 7a and 11.

## Exit codes

`0` done, `1` findings (a refused plan, a failed step or check, a difference found by `status`), `2` usage, `3` not implemented, `4` busy (another deploy or refresh holds the lock), `70` internal error.
