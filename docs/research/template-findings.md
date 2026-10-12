# What the sample projects found

Every template under `templates/` is built on SQL Server 2022 and PostgreSQL 17 by `TemplateConformanceTests` and its marts are compared, row by row, with the same
models run on the same seeds in DuckDB. Writing the templates is how a construct meets an engine, so each problem they hit is written down here with what was done:

- **fixed**: the tool now produces the right query (a lowering change or a target rule, with a unit test and, where an engine is involved, a probe in `EngineDifferenceProbes` or the template test).
- **refused**: the tool says so at `validate` or `render` (a matrix row with `unsupported`, or a lowering refusal DDB-324), and the template writes the transform another way.
- **cataloged**: a difference that is real and understood, with its row in `matrix/constructs.yml` or `docs/research/engine-differences/README.md`; the engine's answer or error is known.

The older findings (from the probes, before the templates) are in `docs/research/engine-differences/README.md`.

## chinook

| # | Construct | What happened | Disposition |
|---|---|---|---|
| C1 | a project with `sources/` but no `models/` | the sources were not loaded, so `seed` said the seed was for a source that did not exist | **fixed**: `ProjectValidator` loads the sources and reports the missing `models/` directory as the error |
| C2 | `a // b` in an authored query | the parser that reads authored queries (lineage, nullability, the hash) cannot read `//` (DDB-306), although DuckDB can; found in `retail` and again in `chinook` | **fixed**: `SqlParseHints.ForParser` gives the parser `a / b` (same length, strings and comments untouched). Only lineage and the hash come from that parse, and they are the same for both operators; the meaning comes from DuckDB's plan, which lowers `//` itself |
| C3 | `lpad(s, n, p)`, `rpad` | SQL Server has neither (before 2025); the probes had them as `unsupported` | **fixed** for a literal length up to 4000 and a literal non-empty pad: target rule `pad-to-length` (`LEFT(REPLICATE(p, n), n - LEN(s)) + s`, `LEFT(s, n)` when too long); any other use is still refused. Remaining difference: a character outside the BMP counts as two on SQL Server |
| C4 | `split_part(s, sep, n)` | DuckDB expands the macro into `array_extract(string_split(s, sep), n)`, which neither engine has: both rendered invalid SQL and the linter only warned (DDB-305) | **fixed** on PostgreSQL (target rule `split-part`, its own `split_part`); **refused** on SQL Server (matrix row `fn.split_part`: STRING_SPLIT is a table function). The template splits with a recursive query |
| C5 | `WITH RECURSIVE` | lowering refused it (DDB-324) | **fixed**: `LOGICAL_RECURSIVE_CTE` is lowered (anchor and recursive part, text and decimal columns cast to one type in both parts, as SQL Server requires). **cataloged** in `syntax.recursive_cte`: SQL Server stops at 100 levels (error 530), allows only `UNION ALL`, and cannot take `OPTION (MAXRECURSION)` in a view |
| C6 | `generate_series(0, n)` where `n` comes from another table | lowering said "0 argument(s)" | **refused** with a message that says why (a series per row is a `CROSS APPLY` on SQL Server); the template joins a series of constants and filters it |
| C7 | `date + 3`, `date - n`, `n + date` | SQL Server: "Operand type clash: date is incompatible with int"; PostgreSQL accepts it | **fixed**: lowering marks it (`CAST(d AS DATE) + CAST(n AS INTEGER)`) and the target rule `date-plus-days` writes an interval of days on SQL Server |
| C8 | `string_agg(x, sep ORDER BY ...)` on PostgreSQL | polyglot writes `LISTAGG`, which PostgreSQL does not have; `render` refused it | **fixed**: target rule `string-agg-array` (`array_to_string(array_agg(x ORDER BY ...), sep)`) |
| C9 | `strpos(a \|\| b, 'x')`, `position(... IN a \|\| b)` | the transpile writes `+` for `\|\|` everywhere except inside the arguments of functions it rewrites into another shape (`CHARINDEX` with swapped arguments, `starts_with`): `CHARINDEX('x', a \|\| b)` is a syntax error on SQL Server | **fixed**: target rule `concat-plus` writes every `\|\|` as `+` before the transpile (the lowered query casts non-text operands itself). The upstream bug is in polyglot |
| C10 | `CAST(x AS VARCHAR)`, `LEFT`, `REPEAT` | not in `matrix/covered.yml`, so `validate` warned (DDB-305) | **fixed**: covered with the probe cases that already compare them on both engines |
| C11 | constants in a recursive anchor (`1 AS n`, `'a' AS s`) | the lowerer had no type for a constant, so the cast that keeps both parts the same type was missing | **fixed**: a constant's type is read from its value |

## adventureworks

| # | Construct | What happened | Disposition |
|---|---|---|---|
| A1 | `lead`, `ntile`, `percent_rank`, `cume_dist`, `first_value`, `last_value` | not in `matrix/covered.yml`, so `validate` warned (DDB-305), and no probe compared them | **fixed**: 36 window cases (frames, ties, NULL ordering, defaults) are now probes in `EngineDifferenceProbes`; every one agrees with DuckDB on both engines, and the nodes are covered |
| A2 | `count(*) OVER (...)` | the plan has a `count` with no argument and the lowerer wrote `count() OVER ()`: SQL Server "COUNT_BIG requires 1 argument", PostgreSQL "count(*) must be used" | **fixed** (the window count is written `count(*)`) |
| A3 | `nth_value(x, n) OVER (...)` | the lowerer does not know the window operator; SQL Server has no NTH_VALUE either | **refused** (DDB-324, "the window function WINDOW_NTH_VALUE"), listed here |
| A4 | `median(x)`, `quantile_cont(x, q)` | the fraction lives outside the aggregate's parameters in the plan; SQL Server has only an analytic PERCENTILE_CONT with an OVER clause | **refused** (DDB-324), listed here |
| A5 | `greatest`, `least` | not covered (DDB-305), and a SQL Server 2022 function | **fixed**: matrix row `fn.greatest_least` (SQL Server version 16; both engines skip NULLs like DuckDB, probed) |
| A6 | `date_diff('year' \| 'month' \| 'quarter', a, b)` on PostgreSQL | polyglot writes `AGE()`, which counts whole elapsed years and months; DuckDB counts boundaries crossed (tenure of someone hired 2016-06-30, on 2025-01-01: 9 vs 8) | **fixed**: target rule `date-diff-boundaries` (the difference of the calendar fields) |
| A7 | `date_diff('week', a, b)` | SQL Server counts week boundaries from `@@DATEFIRST`; PostgreSQL's cast to an integer rounds (8.86 weeks is 9) where DuckDB counts whole periods of seven days toward zero | **fixed** on both: target rule `date-diff-weeks` (the days between over seven, toward zero) |
| A8 | a product of several decimals | DuckDB casts the operands to DECIMAL(38, s) when the result would be wider than 38 digits and multiplies exactly; SQL Server cuts the scale of every product to 6 and rounds there. Price times discount times exchange rate differed in the fourth decimal in about one line in a hundred | **cataloged**: matrix row `type.decimal_product_wide` (the linter notes any product with a DECIMAL(38, s) operand), a probe with a case that shows it, and the template works the chain in DOUBLE and rounds |
| A9 | `x + 1`, `d - n` where `d` is a DATE (the previous period ends the day before the next one starts) | see C7 | **fixed** (C7) |
| A10 | as-of lookups: a correlated `ORDER BY ... LIMIT 1` subquery, and a join on a date range | lowered and agree with DuckDB on both engines | verified, nothing to do |
| A11 | a recursive query with decimal quantities multiplied down the levels and a path | lowered with the casts of C5; agrees on both engines | verified |
| A12 | a backslash in a string literal (`strpos(login, '\')`) | read the same by the parser, DuckDB and both engines | verified |

## The language areas (PIVOT, UNPIVOT, QUALIFY, LATERAL, quantiles, JSON, regular expressions)

Models added to `adventureworks` (`rpt_revenue_by_color_year`, `rpt_quota_long`, `rpt_customer_last_order`, `rpt_territory_top_products`, `rpt_product_revenue_spread`, `dim_product_attributes`) and `chinook` (`rpt_genre_year_matrix`, `rpt_best_track_per_genre`); regular expressions, `nth_value` and the JSON functions that have no counterpart are probes (`EngineDifferenceProbes`), because a model that needs them runs on one engine only.

| # | Construct | What happened | Disposition |
|---|---|---|---|
| L1 | `PIVOT` (statement or in a FROM) | the offline parser neither analyzes a PIVOT statement nor reads the query a PIVOT pulls from, so `define` could not find the tables it reads (DDB-219) and the dependency graph would have missed them | **fixed**: DuckDB's own parser (`json_serialize_sql`) says which tables a query reads when the offline parser does not know the syntax (`DuckParseTree`); lineage is unknown for such a query, the columns still come from DuckDB's describe. The lowering of a PIVOT was already an aggregate with `IS NOT DISTINCT FROM`, now covered |
| L2 | `UNPIVOT` | the plan is an UNNEST of two lists built per row, which the lowerer refused | **fixed**: lowered to one scan of the source joined to a lateral `VALUES` list with a row per unpivoted column (`CROSS APPLY (VALUES ...)` on SQL Server, `CROSS JOIN LATERAL (VALUES ...)` on PostgreSQL; the rows DuckDB drops for a NULL come out of the filter that follows). Any other `UNNEST` (a list column) is still **refused** |
| L3 | `QUALIFY`, `LATERAL` | lowered to a window in a subquery and to a correlated subquery (`CROSS APPLY` on SQL Server) | verified on both engines, nothing to do |
| L4 | `median`, `quantile_cont`, `quantile_disc` | refused (the fraction lives outside the aggregate's arguments); SQL Server has only an analytic PERCENTILE_CONT | **fixed**: the rows are ranked inside each group and the quantile read off the ranks with conditional aggregates, on any engine. A DECIMAL result follows DuckDB exactly: it interpolates as `lo * (1 - d) + hi * d` on the stored whole numbers and **cuts** the result (1228.40888 is 1228.4088). A list of fractions, DISTINCT and FILTER are **refused** |
| L5 | `nth_value` | not lowered | **fixed** for PostgreSQL (lowered; native there); **cataloged** unsupported on SQL Server (matrix `fn.nth_value`) |
| L6 | `json_extract_string` and the arrows | SQL Server: `JSON_VALUE` (same answers, NULL for an object); PostgreSQL: polyglot writes `x ->> '$.a.b'`, which takes a key not a path and no operator takes text; the arrows `->>` and `->` could not be transpiled; with several keys polyglot kept only the first, and under a CAST it wrote the last key as a path | **fixed**: the arrows are read as the functions; on PostgreSQL a simple literal path becomes a chain of `->` and a last `->>` (a number for an array position), in parentheses. Computed paths and wildcards **refused**. `json_extract`, `json_valid`, `json_type`, `json_keys` **cataloged** unsupported (matrix `fn.json_other`); `json_array_length` fixed on PostgreSQL, unsupported on SQL Server |
| L7 | `regexp_full_match`, `regexp_extract`, `regexp_replace`, `regexp_matches` | PostgreSQL: the first wrote the unanchored `~` (a partial match counted), the second does not exist; SQL Server 2022 has none | **fixed** on PostgreSQL (anchored pattern; `regexp_match` with the group number plus one, `''` for no match); **cataloged** unsupported on SQL Server (2025 has REGEXP_*, not verified here). RE2 and PostgreSQL's ARE agree on common patterns, not all |
| L8 | `generate_series` between dates, or with bounds from another table | refused | **cataloged** (a lateral series is a `CROSS APPLY` of GENERATE_SERIES, integers only on SQL Server); the templates count days forward from a constant series |
| L9 | `IS NOT DISTINCT FROM` as a pivot's null-safe comparison | not covered (DDB-305) | **fixed**: covered, with the probes that already compare it |

## fan_in_fan_out and macro_examples, and the walk of every template through the new cycle (2026-10-12)

`fan_in_fan_out` (a fan-in copy, a fan-out model, central tracking, a refresh plan with an excluded copy) and `macro_examples` (every kind of macro) were added, and `retail`, `chinook` and `adventureworks` were walked through `inspect`, `seed`, `init`, `status`, `deploy`, `refresh` and `monitor` on SQL Server 2022 and PostgreSQL 17 (entry 144).

| # | Construct | What happened | Disposition |
|---|---|---|---|
| M1 | a macro that casts to an `ENUM` type | the lowered query said `CAST(CAST(status AS ENUM) AS VARCHAR)`, which no engine reads, and `compile` reported only a DDB-305 warning about a type `custom` | **fixed**: the lowerer refuses a cast to or from an enum (DDB-324, with the alias as the way out); an alias type (`CREATE TYPE money AS DECIMAL(12, 2)`) lowers to the type it stands for |
| M2 | a DATE default of a macro parameter (`live_as_of := DATE '2024-06-30'`) | DuckDB folds it to a DATE constant that the plan carries as days since 1970; the lowerer wrote `CAST(19904 AS DATE)`, an error in DuckDB and PostgreSQL and 1954 on SQL Server | **fixed**: written as `DATE '2024-06-30'` |
| M3 | `project model update --check` in a project with a copy | DDB-108, "has no query file", for the copy's definition | **fixed**: a copy is left out like a mapped model |
| M4 | `project model update` for a query given a table by a project `NAME` parameter | DDB-219, the marker `__ddb_param_project_history_table__` is not a table (`compile` and `sample` were right) | **fixed**: names are resolved before the query is bound, on the model's first connection |
| M5 | a SQL Server database created with the server's default collation | every command works, but `connection status` reports DDB-310 for a view column made of string literals (it takes the database collation) | **documented** in the READMEs (create the database `COLLATE Latin1_General_100_CS_AS`); the string profile is parked |
| M6 | `retail` on PostgreSQL | the README said that `fct_shipment_packages` and the calendar do not render for PostgreSQL; all 23 models validate, deploy and refresh, and the marts match DuckDB | README corrected |
| M7 | a column declared wider than its query returns (`macro_examples`: `DECIMAL(21, 2)` against a `CAST(... AS money)`) | `compile` and the unit test passed; a deploy refused the model (DDB-420) | the template's declaration fixed; the unit test over the templates now runs `model update --check` |

## Not covered by a template yet

`UNNEST` and list columns (L2), a lateral or date series (L8), `split_part` on SQL Server (C4), `json_extract` and `json_valid` (L6), and every regular expression on SQL Server (L7): the matrix says which are refused today; a template that needs one is how it gets built.
