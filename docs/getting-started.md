# Getting started

This walks from nothing to a model built on a database. The first part needs only the executable; the second needs an empty SQL Server or PostgreSQL database you do not mind filling. Every command is listed with its options in `docs/commands.md`; running it against real databases, scheduling and recovery are in `docs/operations.md`.

## 1. Look at a project offline

```
dbdatabuild project create starter cafe       # 28 files: a small cafe, mapped models through staging to two marts (also: retail, chinook, adventureworks, fan_in_fan_out, macro_examples)
cd cafe
dbdatabuild project compile                # checks config and models, shows how each query lowers ("OK: 5 model(s) valid"), and writes the load scripts (rendered/) to read and commit
dbdatabuild project show graph --columns   # what reads what, and where each column comes from
dbdatabuild project seed                   # generate the rows of the mapped models into .dbdatabuild/seed.duckdb
dbdatabuild project sample marts.orders    # run a model on that data and see the rows
dbdatabuild project tests run              # the project's tests: metadata rules and model tests
dbdatabuild project show loads             # each model on each connection: the strategy, and how well the engine supports its query
```

Nothing here opens a database (the `project` commands never do). `project compile` prints notes where a construct behaves slightly differently on the engine than in DuckDB (DDB-302); `dbdatabuild help code DDB-302` says what and why.

## 2. What a project is

```
dbdatabuild.yml        connections, defaults, where tracking lives, the string profile
models/**/*.sql        one SELECT per model (DuckDB dialect), optionally with a head: CREATE TABLE schema_name.table_name WITH (...) AS
models/**/*.yml        the definition: name, kind, grain, columns (the contract), indexes, tests
seeds/                 DuckDB queries that make the sample rows of the mapped models
tests/                 metadata rules and model tests
rendered/              generated load scripts: commit them
plans/                 plan files from `connection deploy`: read them, apply them
```

A model is a query and a declaration of the columns it returns. `dbdatabuild project model update models/marts/orders.sql` proposes the declaration from the query and asks before writing; `dbdatabuild project model update --check` fails in CI when they disagree. A table the tool does not build, but your models read, is a **mapped** model (`kind: mapped`); `dbdatabuild project import staging.*` writes them from the database's catalog.

## 3. Build on a database

The tool uses two logins per connection, named by environment variable, and nothing falls back from one to the other: `DBDATABUILD_<CONNECTION>_READ` (planning, compare, import, status, monitor) and `DBDATABUILD_<CONNECTION>_WRITE` (applying a plan, refresh, init, publish). The connection name is the one in `dbdatabuild.yml` (`sqlserver` and `postgres` exist without being declared). A login is a connection string for the engine.

```
export DBDATABUILD_SQLSERVER_READ='Server=localhost,1433;User Id=reader;Password=...;TrustServerCertificate=true'
export DBDATABUILD_SQLSERVER_WRITE='Server=localhost,1433;User Id=builder;Password=...;TrustServerCertificate=true'

dbdatabuild connection seed --apply                      # optional: put the seeded tables (the mapped models) into the database (without --apply it only shows what it would do)
dbdatabuild connection init sqlserver --apply   # create the tracking tables (without --apply it shows the statements)
dbdatabuild connection status sqlserver     # where it stands: nothing deployed yet, no drift
dbdatabuild connection deploy sqlserver     # plan (it asks what it must ask), show the plan, apply it when you say yes
```

`connection deploy` reads the database, writes `plans/sqlserver/<id>.plan.yml` (and a `.plan.md` report) and shows it: every statement, its risk and the reasons, and any question it cannot answer from the project (a renamed column, a load that needs a decision). It asks those questions at the terminal; a script gives the answers in a file (`--answers`) or with `--param`. Applying runs exactly what the plan recorded and refuses a plan that was edited, is out of date, or no longer matches the database. To separate the steps, `--write-plan` stops after the plan and `--apply-plan <file>` applies it later (`--dry-run` checks everything and executes nothing). A plan that stopped part-way continues when the same plan is applied again; a completed plan is refused.

After the first build, `dbdatabuild connection refresh` runs the routine loads (it never changes structure, and says so when a change of structure is due). `dbdatabuild connection monitor` shows what happened, `connection compare` compares a table with another (even on another engine), and `connection deploy --ack drift:<object> --reason <why>` records a decision about drift or a changed definition. `docs/concepts.md` explains how these fit together.

## 4. Next

- Several connections, copies between them, parameters per connection, native queries and macros: `DESIGN.md` section 6.5 and `docs/research/cross-server.md`.
- Working with an AI agent: `docs/agents.md`; `dbdatabuild project agent-kit --write` installs the skill and schemas in the project.
- The terminal and web interfaces: `docs/interfaces.md` (`dbdatabuild ui terminal`, `dbdatabuild ui web`).
