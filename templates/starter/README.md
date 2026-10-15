# starter: a small cafe

A tiny project to try dbdatabuild without a database: three source tables (`raw.customers`, `raw.orders`,
`raw.payments`), a staging layer that tidies them, and two marts (`marts.orders`, `marts.customers`). The data is
made up by SQL in `seeds/`, so every command below runs on your own machine with nothing else installed.

```
dbdatabuild project compile                         # check config, models, see how each query lowers, and write the load scripts (rendered/) to read and commit
dbdatabuild project show graph --columns            # what reads what, and where each column comes from
dbdatabuild project seed                       # generate the rows of the mapped models into .dbdatabuild/seed.duckdb (inspect it with any DuckDB tool)
dbdatabuild project sample marts.orders        # run a model on that data and look at the rows
dbdatabuild project tests run                       # the project's tests: metadata rules and model tests
```

## What is where

| Folder | What |
|---|---|
| `models/raw/` | one **mapped** model (`kind: mapped`, no query) per table the project reads but does not build: its columns and types |
| `seeds/` | one DuckDB query per source that generates its rows from a `seed` and a `scale` (`dbdatabuild project seed --seed 7 --scale 500`) |
| `models/staging/` | views: names made consistent, a status mapped to a group, cents turned into dollars |
| `models/marts/` | tables: `orders` pivots payments into one column per method; `customers` aggregates orders |
| `tests/metadata/` | rules over what the project *is* (staging only reads sources, every model has a grain, ...) |
| `tests/models/` | cases for a model's logic: given rows, expected rows |

## Saying what things are

The project, the connection, each mart, a few columns and an index carry a `title`, a `description`, `tags` and `properties` (see `dbdatabuild.yml` and `models/marts/`). They change nothing that is built; they
are in the metadata (`dbdatabuild project show metadata marts.orders`), in the tracking table `object_info` after `dbdatabuild connection publish`, and in the rule `tests/metadata/marts_are_described_and_owned.sql`,
which fails a mart that has no title, description or `owner`. `models/marts/_dbdatabuild.yml` gives every mart the tag `mart` and the property `layer: mart` once; `dbdatabuild project show graph tag:finance` or
`property:owner=finance` selects models by them.

## Things to try

- `dbdatabuild project show graph +marts.customers` and `dbdatabuild project show graph --column raw.payments.amount_cents`.
- Change `seeds/macros.sql` (`scale`) or run `seed --scale 1000`, then `sample marts.customers --limit 10`.
- Break something on purpose: rename a column in `models/marts/orders.sql` and run `dbdatabuild project compile`, then `dbdatabuild project tests run`.
- Add a mart of your own, for example revenue per month, and write a model test for it.

## Trying it on a sandbox database

The source tables are what `seeds/` generates, and `load-seeds` can put them in an empty SQL Server (or PostgreSQL) database you do not mind filling. The logins are environment variables
(`DBDATABUILD_SQLSERVER_WRITE` for what writes, `DBDATABUILD_SQLSERVER_READ` for what reads; see the main documentation):

```
dbdatabuild connection seed                 # shows what it would create and fill; connects to nothing
dbdatabuild connection seed --apply         # creates the tables of the mapped models and loads them (--replace drops and recreates tables that exist)
dbdatabuild connection init --apply               # the tracking tables
dbdatabuild project compile --check     # the rendered files are checked in against the models
dbdatabuild connection deploy --write-plan --accept-inferred   # reads the database, writes a plan you can read
dbdatabuild connection deploy --apply-plan plans/sqlserver/<the plan>.plan.yml
```

Then compare: `dbdatabuild project sample <model> --limit 5` shows what DuckDB computes for the same seeds, and the tables in the database hold the same rows.
