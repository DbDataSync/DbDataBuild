# chinook: a digital music store

A project after the shape of the Chinook sample database (artists, albums, tracks, playlists, customers, employees, invoices), with every row
made up by SQL in `seeds/`: the names, tracks and invoices are generated, so nothing here is copied from the original data. Eleven source tables
in the `chinook` schema, a staging layer, and a star schema with a calendar, dimensions, a fact and some reports.

```
dbdatabuild project compile                         # check config, models, see how each query lowers, and write the load scripts (rendered/) to read and commit
dbdatabuild project show graph --columns                # what reads what, and where each column comes from
dbdatabuild project seed --scale 500               # generate the rows of the mapped models (the scale is the number of customers)
dbdatabuild project sample marts.dim_employee      # run a model on that data and look at the rows
dbdatabuild project tests run                           # metadata rules and model tests
```

## The transforms worth reading

| Model | What it shows |
|---|---|
| `marts.dim_employee` | a **hierarchy**: the management chain walked from the general manager down with a recursive query (`WITH RECURSIVE`), a level and the path of names |
| `marts.fct_track_composer` | **splitting** one text column (`Ana Reyes, Ben Okafor` or `A / B / C`) into a row per name with its position, by a recursive query that peels the first name off the rest |
| `marts.dim_track` | joins that keep tracks without an album or a genre, `lpad` to write a length as `mm:ss`, a length band |
| `marts.dim_date` | a calendar counted forward from the first invoice (a series of numbers added to a date) |
| `marts.fct_invoice_lines` | an incremental fact, loaded by the line number |
| `marts.agg_genre_revenue` | **aggregating** by genre and year with the share of the year, a rank, and a running total over the years (a window with a frame) |
| `marts.agg_top_tracks` | the best three tracks of every genre (`row_number`), and units by year as columns (a pivot written with `CASE`) |
| `marts.agg_customer_cohorts` | customers grouped by the month of their first purchase and how many came back, month by month |
| `marts.rpt_genre_year_matrix` | `PIVOT`: the years of the genre revenue become columns |
| `marts.rpt_best_track_per_genre` | `QUALIFY`: the first row of each genre's ranking, without a subquery |
| `marts.rpt_playlists` | a many-to-many table, hours of music, and the most common genres of a playlist as one text (`string_agg`) |
| `marts.rpt_sales_by_manager` | revenue **allocated up the hierarchy**: every manager's figure includes the people below them |

## Tests

`tests/metadata/` holds rules over what the project is (every model has a grain, staging reads only mapped models, dimensions are keyed). `tests/models/` holds cases for the
logic that is easy to get wrong: the split of the composer text, the management chain, the roll-up of revenue, the cohort counts, the `mm:ss` text.

## Running the cycle on a database

`connection seed` puts the tables of the mapped models in an empty database you do not mind filling; the logins are environment variables (`DBDATABUILD_SQLSERVER_READ` and `DBDATABUILD_SQLSERVER_WRITE`, or `..._POSTGRES_...`).
Create a SQL Server database case-sensitive (`CREATE DATABASE chinook COLLATE Latin1_General_100_CS_AS`) to match the project's string profile; with the server default `connection status` reports DDB-310 for a view column made of literals.
The numbers below are from a run against SQL Server 2022 (the same cycle ran on PostgreSQL 17 with the profile in the comment at the top of `dbdatabuild.yml`: same counts, with `postgres` as the connection); ids and times differ in yours.

```
dbdatabuild project compile                                   # offline; writes rendered/, including rendered/sqlserver/loads.yml and refresh/default.yml
dbdatabuild connection inspect sqlserver         # can the tool use it? (before init: "tracking ... not there yet"; after: "Nothing in the way.")
dbdatabuild connection seed sqlserver --apply    # 11 table(s), 4557 row(s)
dbdatabuild connection init sqlserver --apply    # the tracking tables (14 statements)
dbdatabuild connection status sqlserver          # Structure  pending: a deploy would run 27 step(s) (0 risky, 0 destructive) and 13 load(s)
dbdatabuild connection deploy sqlserver --write-plan --accept-inferred    # Plan <id>: 40 step(s) (27 ddl, 13 load, ...); read plans/sqlserver/<id>.plan.md
dbdatabuild connection deploy sqlserver --apply-plan plans/sqlserver/<id>.plan.yml    # Applied plan <id>: 40 step(s) executed.
dbdatabuild connection refresh sqlserver         # Refresh plan rendered/sqlserver/refresh/default.yml: 13 of 13 member(s). ... Refresh ref-<time>-<hash>: 13 step(s) ran.
dbdatabuild connection status sqlserver          # Structure  in sync: a deploy would change no structure (it would run 13 routine load(s))
dbdatabuild connection monitor sqlserver         # events (one deploy, one refresh), DDL, loads, recorded shapes; --event <id>, --lane refresh, --attention
```

What each step is for, and the four `refresh.check` levels (`none`, `project`, `objects`, `live`) with the message each gives when it stops a refresh, are in the `retail` README (`dbdatabuild project create retail`), whose walk-through is the longest.
In short: *deploy* changes structure and is reviewed; *refresh* repeats the loads of the committed refresh plan and never changes structure; `refresh.check` chooses whether it asks the database nothing (`none`), whether
the project was deployed here (`project`), whether each object has the shape last deployed (`objects`, the default) or whether it still has that shape in the catalog (`live`).

The lifecycle block in `dbdatabuild.yml` (commented) shows non-default settings for a small catalog that rarely changes: a deploy plan that is made and applied in one step and keeps only its hash and event
(`plans.deploy: {keep: ephemeral, audit: minimal}`), `refresh.check: project` with `on_fail: warn`, statement logs kept 7 days. Uncommented, they act: after adding a column to `marts.agg_genre_revenue`
(and compiling) `connection refresh` printed ``warning DDB-447  The project expects structure ..., but the last completed deploy (...) recorded ...`` and ran anyway (`warn`), and stopped at the load that needed the new column
(`error DDB-440`, `Refresh ... did not complete. 4 step(s) ran before the stop`), which `connection monitor --event <id>` shows as a `failed` load. `connection deploy --accept-inferred --answers answers.yml --yes`
then planned and applied in one step (`The plan was ephemeral and has been removed`), and the next refresh ran all 13 loads.

## Notes

- On SQL Server a recursive query stops at 100 levels (error 530) where DuckDB goes on; this hierarchy is three levels deep.
- The project is set for SQL Server (see the comment at the top of `dbdatabuild.yml` for PostgreSQL); it asks for SQL Server 2022 (`connections: sqlserver: {version: 16}`) because of `generate_series`.
- The names of the artists, the composers and the customers are invented; the shape of the data follows the Chinook sample database (MIT licence).
