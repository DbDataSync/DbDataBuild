# adventureworks: a bicycle maker

A project after the shape of the AdventureWorks sample database (products and their price history, bills of materials, work orders, sales in several currencies, purchasing,
people), with every row made up by SQL in `seeds/`: nothing is copied from the original data. Twenty-three source tables in five schemas
(`production`, `sales`, `purchasing`, `humanresources`), a staging layer of views, and a warehouse of dimensions, facts, aggregates and reports.

```
dbdatabuild project compile                         # check config, models, see how each query lowers, and write the load scripts (rendered/) to read and commit
dbdatabuild project show graph --columns                # what reads what, and where each column comes from
dbdatabuild project seed --scale 800               # generate the rows of the mapped models (the scale is the number of customers; four orders each)
dbdatabuild project sample marts.fct_bom_explosion # run a model on that data and look at the rows
dbdatabuild project tests run                           # metadata rules and model tests
```

## The transforms worth reading

| Model | What it shows |
|---|---|
| `marts.dim_product_history` | a **slowly changing dimension** built from two histories (cost and list price): the periods start at every change of either, and each ends the day before the next |
| `marts.fct_sales_lines` | **as-of lookups**: the latest exchange rate on or before the order date (a correlated query with `ORDER BY ... LIMIT 1`, a weekend order takes Friday's rate) and the product cost in force on that day (a join on a date range); a fact loaded incrementally; currency conversion worked in DOUBLE and rounded |
| `marts.fct_bom_explosion` | a **parts explosion**: a recursive query from the assemblies nothing else uses, multiplying the quantity per assembly down the levels and keeping the path |
| `marts.agg_bom_rolled_cost` | what a finished product costs from its leaf parts against the cost it carries |
| `marts.agg_sales_territory_quarter` | **aggregating** to a quarter, the running total of the year (a window frame), and the same quarter of the year before with the growth |
| `marts.agg_product_abc` | a Pareto classification: cumulative share of revenue (window over the whole table) and a class A, B or C |
| `marts.rpt_customer_rfm` | recency, frequency and spend cut into five groups (`ntile`) and a segment name written as a **mapping** of the three scores |
| `marts.rpt_sales_person_quota` | quota attainment per quarter, a rank and a percentile (`percent_rank`) |
| `marts.fct_inventory_snapshot` | stock next to sales, the average of the last three months (a rolling frame) and the months of supply |
| `marts.fct_purchase_lines`, `marts.rpt_vendor_scorecard` | lead time, share received and rejected, shipped on time (`count(*) FILTER (...)`), vendors ranked by spend |
| `marts.rpt_revenue_by_color_year` | `PIVOT`: the years become columns |
| `marts.rpt_quota_long` | `UNPIVOT`: revenue and quota columns become one row per measure |
| `marts.rpt_customer_last_order` | `QUALIFY`: the latest order of every customer |
| `marts.rpt_territory_top_products` | `LATERAL`: the three best products of each territory, a subquery that reads the row it is joined to (`CROSS APPLY` on SQL Server) |
| `marts.rpt_product_revenue_spread` | `median`, `quantile_cont` and `quantile_disc`, ranked inside each group because SQL Server has no aggregate for them |
| `marts.dim_product_attributes` | **JSON**: `json_extract_string` reads the frame, the gears and the first tag out of the document each product carries |
| `marts.fct_work_orders` | scrap rate, days late, and a status mapped from dates (an order with no end is in progress) |
| `marts.dim_employee` | the department they are in now (the history row with no end), the pay rate in force (`row_number`), the years with the company |
| `marts.dim_date` | a calendar from a series of numbers with a fiscal year that starts in July |
| `staging.employees` | **splitting** a login (`domain\name`) at the backslash, codes mapped to words |

## Tests

`tests/metadata/` holds rules over what the project is (every model has a grain, staging reads only mapped models, marts are named dim, fct, agg or rpt, incremental models declare their key,
money is a decimal). `tests/models/` holds cases for the logic that is easy to get wrong: the explosion, the cost and price periods, the exchange rate of a weekend, the cohorts of
quintiles, the year-on-year comparison, the on-time flags.

## Running the cycle on a database

`connection seed` puts the tables of the mapped models in an empty database you do not mind filling; the logins are environment variables (`DBDATABUILD_SQLSERVER_READ` and `DBDATABUILD_SQLSERVER_WRITE`, or `..._POSTGRES_...`).
Create a SQL Server database case-sensitive (`CREATE DATABASE adventureworks COLLATE Latin1_General_100_CS_AS`) to match the project's string profile; with the server default `connection status` reports DDB-310 for a view column made of literals.
The numbers below are from a run against SQL Server 2022 (the same cycle ran on PostgreSQL 17 with the profile in the comment at the top of `dbdatabuild.yml`: same counts, with `postgres` as the connection); ids and times differ in yours.

```
dbdatabuild project compile                                   # offline; writes rendered/, including rendered/sqlserver/loads.yml and refresh/default.yml
dbdatabuild connection inspect sqlserver         # can the tool use it? (before init: "tracking ... not there yet"; after: "Nothing in the way.")
dbdatabuild connection seed sqlserver --apply    # 23 table(s), 19531 row(s)
dbdatabuild connection init sqlserver --apply    # the tracking tables (14 statements)
dbdatabuild connection status sqlserver          # Structure  pending: a deploy would run 50 step(s) (0 risky, 0 destructive) and 24 load(s)
dbdatabuild connection deploy sqlserver --write-plan --accept-inferred    # Plan <id>: 74 step(s) (50 ddl, 24 load, ...); read plans/sqlserver/<id>.plan.md
dbdatabuild connection deploy sqlserver --apply-plan plans/sqlserver/<id>.plan.yml    # Applied plan <id>: 74 step(s) executed.
dbdatabuild connection refresh sqlserver         # Refresh plan rendered/sqlserver/refresh/default.yml: 24 of 24 member(s). ... Refresh ref-<time>-<hash>: 24 step(s) ran.
dbdatabuild connection status sqlserver          # Structure  in sync: a deploy would change no structure (it would run 24 routine load(s))
dbdatabuild connection monitor sqlserver         # events (one deploy, one refresh), DDL, loads, recorded shapes; --event <id>, --lane refresh, --attention
```

What each step is for, and the four `refresh.check` levels (`none`, `project`, `objects`, `live`) with the message each gives when it stops a refresh, are in the `retail` README (`dbdatabuild project create retail`), whose walk-through is the longest.
In short: *deploy* changes structure and is reviewed; *refresh* repeats the loads of the committed refresh plan and never changes structure; `refresh.check` chooses whether it asks the database nothing (`none`), whether
the project was deployed here (`project`), whether each object has the shape last deployed (`objects`, the default) or whether it still has that shape in the catalog (`live`).

The lifecycle block in `dbdatabuild.yml` (commented) shows a project that records everything: `plans: {deploy: {keep: committed, audit: full}, refresh: {keep: committed, audit: full}}` (the plan text and the
statement log in each event), `refresh: {check: objects, on_fail: block}` (the default, spelled out) and statement logs kept 90 days. Uncommented, a refresh ran as before (`check: objects`, 24 step(s) ran) and `connection monitor` listed it.

## Notes

- Money here is `DECIMAL(19, 4)`. DuckDB multiplies decimals exactly; SQL Server rounds each product to six decimals when the result would be wider than 38 digits, so a chain of
  products (price, discount, exchange rate) can differ in the last place. `marts.fct_sales_lines` works the discount and the conversion in DOUBLE and rounds to four places, which both engines do alike
  (the support matrix row `type.decimal_product_wide` says more).
- The project is set for SQL Server (see the comment at the top of `dbdatabuild.yml` for PostgreSQL); it asks for SQL Server 2022 (`connections: sqlserver: {version: 16}`) because of `generate_series` and `greatest`.
- The shape of the data follows the AdventureWorks sample database (Microsoft Public License); the rows are invented.
