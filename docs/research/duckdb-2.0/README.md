# DuckDB 2.0 (Cyanoptera): what it changes for dbdatabuild

Investigated 2026-10-02 against **2.0.0-alpha44073** (the `v2.0-cyanoptera` branch, built the same day). DuckDB says it releases 2.0 "in the second half of October"; the branch is feature-frozen and now takes only fixes. We run **DuckDB 1.5.4 (CLI) and 1.5.6 (through DuckDB.NET 1.5.6)** today.

## Bottom line

- **Update 2026-10-15 (read the last section first): the tool no longer sets `delim_join_as_cte`; the lowerer reads 2.0's default plans** (CTE delim joins, keyed joins, SEMI joins, count-bug guards). The statements below about the setting are history. Still open on 2.0: ANY/ALL (refused), `%`/`//` by zero, decimal quantiles. (The folded `current_date()` of macro defaults, a real hazard when this was written, is solved: see "Update 2026-10-15 (later)" at the end.) DuckDB.NET still has no 2.0 build (1.5.6), so 1.5.6 stays the default.
- **Update 2026-10-02 (later the same day): the whole suite passes on the 2.0 alpha.** Unit tests 1,084 of 1,084 and the real-engine suite 83 of 83 (SQL Server 2022 and PostgreSQL 17, with the alpha `libduckdb` swapped in), same as on 1.5.6. Across 207 lowering queries the lowered text differs on only three cosmetic points (below), so committed artifacts hardly change on upgrade.
- **Nothing in dbdatabuild's SQL semantics breaks.** On the 94-construct corpus (`spike/constructs.yml` on the seeded data) 1.5 and 2.0 return the same rows, errors and types. The script reports 4 differences: one is a column name, and three are noise (the CSV header printed on an error path, and a random `USING SAMPLE`). The query-level breaking changes in DuckDB 2.0 (integer `//` by zero raises, `~` matches like PostgreSQL, the lambda arrow errors) do not touch anything dbdatabuild generates or accepts.
- **One real incompatibility, now handled: the serialized plan.** DuckDB 2.0 restructured `json_serialize_plan`, which the lowerer reads. `PlanNormalizer` rewrites a 2.0 plan into the 1.x shape (identity on 1.x). Correlated subqueries needed one more thing: DuckDB 2.0 decorrelates them into materialized CTEs and joins to grouped derived tables, which cannot be turned back into the author's subquery without pattern-matching every rewrite. DuckDB's setting **`delim_join_as_cte = false`** brings the old delim-join form back, and `QueryDescriber.PreparePlanConnection` sets it whenever it exists. **That setting is marked deprecated** ("will be removed in a future release"). Without it 16 of the 1,084 tests fail (correlated subqueries again: see "If the setting goes away").
- **DuckDB.NET has no 2.0 build yet** (latest 1.5.6; its `develop` branch is active and a `nightly-builds` branch exists). The *old* C API still works against the 2.0 library, which is why the experiment could run with only the native library swapped.
- **Recommendation (revised):** 2.0 can now be adopted as soon as 2.0.0 and a matching DuckDB.NET exist: no lowering work stands in the way. Until then 1.5.6 stays the default. Run `scripts/test-duckdb-preview.sh` (and with `TEST_PROJECT=tests/DbDataBuild.Tests.Conformance` against the engines) on each alpha or release candidate; if the deprecated setting disappears before or at 2.0.0, do the inverse decorrelation described below.

## How it was tested

```
scripts/test-duckdb-preview.sh        # downloads the preview libduckdb, copies it over the one in the test output, runs the unit tests
python3 docs/research/duckdb-2.0/corpus_diff.py        # 94 constructs on two CLIs; DUCKDB_OLD / DUCKDB_NEW name the two binaries (the preview CLI is duckdb-cli-linux-amd64.tar.gz from https://artifacts.duckdb.org/v2.0-cyanoptera/)
python3 docs/research/duckdb-2.0/plan_kinds.py         # which plan node kinds and keys differ across 134 queries
duckdb -batch < docs/research/duckdb-2.0/breaking_changes_probe.sql    # the breaking-change probes, run on each CLI and diffed
```

## The serialized plan

DuckDB's plan JSON is internal, so a major version moving it is expected. The differences found by `plan_kinds.py` (134 queries, all of which plan on both):

| 1.5 | 2.0 | Handled by |
|---|---|---|
| `"name": "sum", "schema_name": "main", "catalog_name": "system"` on functions, aggregates, windows and scans | `"qname": {"path": ["system", "main", "sum"]}` | normalizer: last element is the name |
| comparisons: `BOUND_COMPARISON` with `left` and `right` | `BOUND_FUNCTION` of type `COMPARE_*` with `children: [l, r]` | normalizer |
| casts: `BOUND_CAST` with `child` and `try_cast` | `BOUND_FUNCTION` of type `OPERATOR_CAST`, `children: [x]`, `function_data.try_cast` | normalizer |
| `NOT IN`: `COMPARE_NOT_IN` | `OPERATOR_NOT` over `COMPARE_IN` | normalizer (so the lowered text is unchanged) |
| `lead`/`lag`: `offset_expr`, `default_expr` beside the argument | all in `children` | normalizer |
| `LOGICAL_GET` carries `name`, `catalog_name`, `schema_name` | `qname`, plus `projection_ids`, `table_filters`, `source_ordinality` and more (ignored) | normalizer |
| **correlated EXISTS / IN**: `LOGICAL_DELIM_JOIN` of `MARK` type, with `DELIM_GET` frames and the correlated filter inside the subquery | a plain `LOGICAL_COMPARISON_JOIN` of `MARK` type; the correlation is **join conditions** (`t.a IS NOT DISTINCT FROM u.a`) and the subquery side is just `u` with `a IS NOT NULL` | `MarkJoin` rewritten: EQUAL is an IN, other conditions go back into the subquery's WHERE; the `IS NOT NULL` guard is removed again so the text reads `u.a = t.a` |
| **correlated scalar subquery with an aggregate** | decorrelated into a **LEFT join to a grouped derived table** (`t LEFT JOIN (SELECT max(b), a FROM u WHERE a IS NOT NULL GROUP BY a) ON t.a = a`); `count(*)` additionally gets a CASE and an empty-result branch for the COUNT bug | **not done** |

```jsonc
// 1.5                                        // 2.0
{"type":"COMPARE_GREATERTHAN",                {"type":"COMPARE_GREATERTHAN",
 "expression_class":"BOUND_COMPARISON",        "expression_class":"BOUND_FUNCTION",
 "left":{...}, "right":{...}}                   "qname":{"path":[">"]}, "children":[{...},{...}]}
```

### If the setting goes away: correlated scalar subqueries

Without `delim_join_as_cte = false` the alpha plans correlated scalar subqueries as a `SINGLE` or `LEFT` join to a grouped derived table, a `count(*)` additionally gets a CASE and an empty-result branch (the COUNT-bug guard) wrapped in materialized CTEs (`LOGICAL_MATERIALIZED_CTE` / `LOGICAL_CTE_REF`), and a `SEMI` join appears. 16 test forms fail in that configuration. Turning it back into the author's subquery is a pattern match:

```sql
-- the author wrote                                   -- the 2.0 plan (setting off the old shape) describes
SELECT id, (SELECT max(b) FROM u WHERE u.a = t.a)     SELECT t.id, g.m FROM t LEFT JOIN
FROM t                                                  (SELECT a, max(b) AS m FROM u WHERE a IS NOT NULL GROUP BY a) g ON t.a = g.a
```

Both are correct; the first is what we promised ("reasonably similar to the source"). The pieces already written help: `TryCountPatch` recognizes the COUNT-bug guard, and `MarkJoin` already reads the 2.0 MARK join for EXISTS and IN.

### What the adaptation changed in the lowerer

| Where | Change |
|---|---|
| `PlanNormalizer` | names from `qname`; comparison, cast and BETWEEN functions back to their node kinds; `NOT` over `IN` is `NOT IN`; `NOT` over a comparison is the opposite comparison (2.0 no longer folds it); `lead`/`lag` arguments, with an offset of 1 and a NULL default dropped as 1.x did |
| `MarkJoin` | EXISTS and IN arrive as MARK joins with the correlation in the join conditions; EQUAL is an IN, the rest go back into the subquery's WHERE (its own column first: `u.a = t.a`), and the `IS NOT NULL` guard that makes null-safe equality an equality is removed again |
| `TryCountPatch` | the COUNT-bug guard shape is just the grouped count |
| right projection map | 2.0 lists the right-side columns a join outputs (`right_projection_map`); absent means all, as in 1.x |
| single-row cross product | an ungrouped aggregate next to another relation is a scalar subquery again: `WHERE a > (SELECT min(a) FROM u)` instead of a CROSS JOIN |
| `QueryDescriber.PreparePlanConnection` | sets `delim_join_as_cte = false` when the setting exists |

The three remaining text differences are cosmetic and equivalent: `SELECT NULL AS x` is no longer cast to INTEGER (the column is untyped NULL), `date_part('year', d)` is `year(d)`, and an unaliased `trim(s)` column is named `"trim"(s)` instead of `main."trim"(s)`.

## Behavior changes found

| Change | Affects us? | Action |
|---|---|---|
| `SELECT NULL AS x` is typed `"NULL"` (SQLNULL), was INTEGER | **Yes: `define --check` reported a false type drift** (declared INTEGER, "resolves NULL"), and `define` would have proposed nothing useful | **Fixed** (version-agnostic): an untyped NULL fits any declared type and is never proposed; the question says to `CAST(NULL AS <type>)` |
| Output name of an unaliased function: `main.trim(s)` becomes `trim(s)` | Lowered artifacts that name such a column change on upgrade (the header already records the DuckDB version, so `render --check` shows it) | none: expected churn |
| `//` (integer division) by zero raises (was NULL); setting `error_on_division_by_zero` | No: `/` is float division and unchanged; the tool never writes `//` | note in the matrix if a model uses `//` |
| Regex operator `~` matches anywhere (PostgreSQL behavior), was a full match | No: the tool uses `regexp_matches`, whose behavior is unchanged | none |
| Lambda arrow `x -> x + 1` is an error by default | No: list functions are refused in lowering | none |
| Negating an unsigned integer errors | No | none |
| A scalar subquery that returns more than one row errors by default (`scalar_subquery_error_on_multiple_rows`) | Positive: now matches SQL Server and PostgreSQL, so the "first() with error guard" recognizer matches the engines | none |
| Transactions are invalidated by any error | No: DuckDB is used offline and in memory | none |
| ICU removed (timezone, calendar, collation code moved into the engine) | Collations used by the string profile (`NOCASE`, `NOACCENT`, `NFC`, chained) gave identical results, including under `GROUP BY` | none |
| PEG parser replaces the PostgreSQL-derived one | No difference on the corpus; plan JSON `query_location` values moved by a couple of characters | none |
| New C API (versioned, stable ABI); the old one remains | DuckDB.NET 1.5.6 runs unchanged against the 2.0 library | wait for a DuckDB.NET release built for 2.0 |
| CLI prints an "agent mode" notice on stderr when `AI_AGENT` is set (and stdout is not a terminal) | Only for people (or agents) running the DuckDB CLI by hand | none |

The matrix needed no change: it describes engines other than DuckDB, and DuckDB's side of every row (the oracle) behaved the same.

## When to move

- **Now:** nothing blocks the move except the two packages: run `scripts/test-duckdb-preview.sh` (unit) and `TEST_PROJECT=tests/DbDataBuild.Tests.Conformance scripts/test-duckdb-preview.sh` (real engines) on each alpha or release candidate, watching for the deprecated setting.
- **When 2.0.0 and a DuckDB.NET release for it exist:** bump `DuckDB.NET.Data.Full` and `Bindings.Full`, regenerate the committed lowered artifacts (their headers carry the DuckDB version), run both suites, and make 2.0 the default. Keep the 1.x path for one release: DuckDB 1.4 is an LTS line and 1.5 stays supported for a while, and the normalizer costs nothing.
- **If `delim_join_as_cte` is removed first:** write the inverse decorrelation described above (a day or two, with the 16 forms as the test).

## Update 2026-10-03: ANY / ALL / row-value IN

The lowering of `x op ANY/ALL (subquery)` and row-value `IN` (progress entry 39) was written and tested against 1.5.x plans. On the 2.0 alpha these plans are different (a count-based `CASE` over a scalar subquery, and an `EMPTY_RESULT` operator for an empty subquery), 9 of the new unit tests fail there (6 value-use refusals that are not refused, and 3 filter cases: two refused with "the plan operator EMPTY_RESULT" and one with **wrong rows**). The default engine is unaffected. Rework them when 2.0 is adopted; the differential tests will show every gap.

## Update 2026-10-15: the lowerer reads 2.0's own plans; `delim_join_as_cte` is no longer set

**What "latest 2.0" is** (checked 2026-10-15): the `v2.0-cyanoptera` branch of duckdb/duckdb, head `bde1b27b4610a81739ed6117ecbb85d5b3224ed4` (2026-10-15 04:57 UTC); the artifacts on https://artifacts.duckdb.org/v2.0-cyanoptera/ of 2026-10-14 report **`v2.0.0-alpha48141`**, source id `c457670e67` (`SELECT version(), source_id FROM pragma_version()`). sha256: `duckdb-shared-libs-linux-amd64.tar.gz` d2ac5ec5bba23375e0fe840b510c7409a476cd01185b0db80079e58f0c411a93, its `libduckdb.so` 28d58fa78efd82d305aed467f4a2d5e5b511e5b5c1dff0f75cc269d8cc6b62ff, `duckdb-cli-linux-amd64.tar.gz`'s `duckdb` e1be137ba304e738bc171782d1cdbcfb5cf86dfa101753d54daeba9e57fbf024 (preview builds move daily; these are the ones used on this date). The latest GitHub release and tag of duckdb/duckdb is still **v1.5.6** (2026-09-28); there is no 2.0 tag or release. **DuckDB.NET has no 2.0 build**: the latest `DuckDB.NET.Data.Full` on NuGet is 1.5.6 (tag 1.5.6; branches `main`, `develop`, `nightly-builds`). So the package pin is unchanged (1.5.6 is the default and passes everything); 2.0 is reached only through the native-library swap of `scripts/test-duckdb-preview.sh`.

**The setting.** The 2026-10-02 notes said `delim_join_as_cte` was marked deprecated; in alpha48141 its description no longer says so (`Rewrite delim joins to materialized CTEs during dependent join flattening.`, default `true`), but the owner's direction stands: nothing in the tool sets it (nor any other setting to make 2.0 plan like 1.x). `QueryDescriber.PreparePlanConnection` is deleted. The lowerer works on the plan 2.0 hands over by default. With the setting off in the old way, 29 unit tests failed on this build (nothing had changed in the setting: the suite had grown from 1,084 to 2,338 tests, and the alpha had moved on); without it, 41 `PlanLowererTests` rows failed before the work below. After it: see the numbers at the end.

### The 2.0 shapes and what undoes them

| 2.0 plan | What it is | Undone by |
|---|---|---|
| `LOGICAL_MATERIALIZED_CTE __duckdb_delim_N` over `LOGICAL_MATERIALIZED_CTE __duckdb_delim_dedup_M` over a join of `CTE_REF(N)` and a subquery that reads `CTE_REF(M)` | the mechanical rewrite of a `DELIM_JOIN` into CTEs (the setting's job) | `PlanNormalizer.UndoDelimCtes`: back to `LOGICAL_DELIM_JOIN` with `duplicate_eliminated_columns` = the groups of the dedup aggregate, `CTE_REF(M)` = `DELIM_GET`, the pass-through projections the rewrite wraps around its nodes removed (a projection of `#0..#k-1` over a child of exactly k columns). An exact inverse: the count-bug guard (`LOGICAL_EMPTY_RESULT`, the CASE) is then the shape `TryCountPatch` always read |
| a `SINGLE`, `LEFT` or `INNER` join of the outer block to a subquery keyed by the correlated column: `t.a IS NOT DISTINCT FROM g.a`, `g` grouped by `a` or filtered by `a IS NOT NULL` (what 2.0 does instead of a delim join when it can) | the correlated `=` moved out of the subquery into a join condition | `PushCorrelation` (Rel level): the `IS NOT NULL` becomes `key = outer value` **at the place it stood among the other conditions** (so the author's order is kept), the key leaves the GROUP BY, the column that carried it carries the outer value. Then the 1.x code for a scalar subquery, a LATERAL join or `LIMIT k` (`TryKeyedLimit`: `row_number() <= k` per key) runs unchanged. Done only when the result is exact: an aggregate must be NULL over no rows (min, max, sum, avg...; not count) because the join gives NULL for an outer value with no match, and an `INNER` join only when the filter above it rejects NULL (`b > (SELECT ...)`); otherwise the join to a derived table stays, which has the same rows |
| `MARK` join with `NOT DISTINCT FROM` conditions | EXISTS / IN | `MarkJoin`, now through `PushCorrelation` too (a subquery with GROUP BY ... HAVING, DISTINCT) |
| `SEMI` join of a subquery to `DELIM_GET` on equal keys, before a grouped count | 2.0 keeps only the keys that occur among the outer values | `JoinWithDelim`: an `IS NOT NULL`, which the keyed join then turns into the equality |
| `LEFT` delim join to an aggregate without groups, an expression (`coalesce`) over it above | the scalar subquery `coalesce((SELECT sum(...)), 0)` | `FinishDelimJoin`: an ungrouped aggregate returns one row, so LEFT is SINGLE |
| `left_projection_map` | the columns of the left side a join outputs (may repeat one) | `LeftColumns` / `MapLeft` |
| a join condition with `comparison: INVALID` and only a `left` | a predicate over both sides (the `OR` of `ON a.k = b.k AND (b.to IS NULL OR a.d <= b.to)`) | a WHERE for an inner join, part of ON otherwise (`PlanLowerer`, the join case) |
| `string_agg` and `quantile_*` with their separator / fraction also in `children` | bind data listed twice | normalizer |
| `list_value('a', 'b')` over constants as the names of an UNPIVOT | was a constant list | normalizer |
| a folded `INTERVAL` constant `{months, days, micros}` | 1.x kept `to_days(3)` | `Literal`: one unit back (`INTERVAL 3 DAY`); 12 MONTH reads as 1 YEAR, 120 SECOND as 2 MINUTE (the author's unit is lost) |

### Text identical to 1.5, and where it is not

Differential run (unit, same seeded data, 1.5.6 against alpha48141): the 126 queries of `spike/constructs.yml` lower to **123 identical texts**; 52 subquery forms (`PlanLowererTests.Subqueries`) to **48 identical**; every text equals what 2.0 gave with the old setting on. The differences, each a loss of information in the 2.0 plan, not a choice:

- a subquery nested two levels deep that correlates to the outermost query: 2.0 refers to the equal column of the middle query (`u_2.a = u.a`, 1.5 `u_2.a = t.a`) because it substituted the columns the middle query's own condition makes equal. Same rows;
- `sum((SELECT count(*) ...))` has no extra duplicated `a AS a_2` column in the derived table (2.0 does not carry it);
- the cosmetic differences of 2026-10-02 (untyped `NULL`, `year(d)`, an unaliased function column's name), the interval unit above.

### What 2.0 changed in meaning, and is not (yet) matched

- **ANY / ALL**: `x op ANY (S)` is planned as a cross product with `min`/`max`, `count(*)` and `count(x)` of S and a CASE chain (uncorrelated), or with a count-bug guard (correlated). That CASE is a boolean value SQL Server cannot use as a condition. It is **refused** (DDB-324), never written; the six row-value and ANY/ALL cases of the unit tests accept exactly that refusal on a 2.0 plan. Not translated.
- **Row-value `IN` with NULLs**: 2.0 follows the standard (a component that is definitely different makes the row definitely not a match); 1.x made the row unknown when any component was NULL. The lowering writes the standard's form on a 2.0 plan (one comparison per row is the same in both), so the *text* of a multi-column `NOT IN` differs by version, on purpose.
- **`%` and `//` by zero raise** in 2.0; the lowering still writes the 1.x answer (NULL, `NULLIF`). The unit tests set `error_on_division_by_zero = false` on the DuckDB that answers, so the rows compare. Decision for the owner.
- **Decimal quantiles** (`quantile_cont`/`median` over a DECIMAL) differ in the last digit from the lowering's (`838.155` against `838.1551`): 2.0 changed the interpolation. One unit test fails on the preview (`Quantiles_of_decimals_cut_to_a_whole_number_of_the_scale...`).
- **(Solved the same day, entry 174: see the last section.) `current_date()` in a macro default** is folded to a DATE constant when the macro is bound (a literal of the day of the lowering), where 1.x kept the call. The plan cannot tell the two apart, so the lowered text would freeze the day: one unit test fails on the preview (`MacroTests.A_macro_with_default_parameters...`). This is the one finding that makes a 2.0 adoption unsafe as it stands: it needs a decision (refuse a DATE/TIMESTAMP literal equal to the day of lowering, or bind macro defaults differently).

## Update 2026-10-15 (later): the clock is folded into constants, and the planning connections replace it (entry 174)

**The problem.** On 2.0 the binder folds the functions that are stable within a query into constants as soon as they stand inside another call, **even with the optimizer fully off and `json_serialize_plan(..., optimize := false)`**: `date_trunc('month', current_date)`, `age(current_date)`, `current_date + i`, `current_date - INTERVAL 7 DAY`, `now() - INTERVAL 1 HOUR`, `year(current_date)`, `date_diff('day', d, current_date)`, `timezone('UTC', now())` and `epoch_ms(now())` all reach the plan as constants (`current_date` as the day number 20741, 2026-10-15, the day of lowering). A bare call at the top of a select item stays a call, which hides the problem; a macro whose body uses the clock folds the same way when it is bound with constant arguments. DuckDB 1.x keeps the calls. The plan cannot tell a folded call from a literal the author wrote, so the lowered text would freeze the day: `... WHERE d > CAST('2026-10-15' AS DATE) - INTERVAL '7 DAY'` on PostgreSQL (shown by `DBDATABUILD_DUCKDB_CLOCK_OVERRIDES=never` below).

**What folds** (probed on `v2.0.0-alpha48256` by planning each function inside `length(CAST(f AS VARCHAR))`; a function folds when the call is replaced by a constant in the plan; the keyword forms behave as the function they bind to):

| Folds (stable within a query) | Result type | Covered by the override |
|---|---|---|
| `current_date`, `today()` | DATE | yes |
| `now()`, `current_timestamp` (binds `get_current_timestamp()`), `transaction_timestamp()` | TIMESTAMP WITH TIME ZONE | yes |
| `current_time` (binds `get_current_time()`) | TIME WITH TIME ZONE | yes |
| `localtime` (binds `current_localtime()`) | TIME | yes |
| `localtimestamp` (binds `current_localtimestamp()`) | TIMESTAMP | yes |
| `current_schema()`, `current_database()` (`current_catalog` binds it), `version()`, `txid_current()` | VARCHAR, VARCHAR, VARCHAR, UBIGINT | yes (values captured when the connection is opened) |
| `current_schemas(bool)` | VARCHAR[] | no (a list result is not written by the override; the folded constant list is refused by the lowerer) |
| `current_setting(name)`, `getvariable(name)` | ANY | no (the result type is the setting's or variable's; a registered function cannot have it) |
| `getenv(name)` | VARCHAR | no (nothing to lower it to) |

Not folded on 2.0 (still calls): `random()`, `uuid()`, `gen_random_uuid()`, `current_connection_id()`, `current_query()`, `current_query_id()`, `current_transaction_id()`. `current_user`, `session_user`, `user`, `current_role` are macros over the constant `'duckdb'` on every version. `pi()` and `pg_typeof()` fold, but to what they always were. `CAST('now' AS TIMESTAMP)` and `TIMESTAMP 'now'` keep the cast of the string. The list is data: `src/DbDataBuild.Targets.DuckDb/ClockOverrides.json` (`overrides` and `notCovered`).

**The override.** `duckdb_create_scalar_function` + `duckdb_scalar_function_set_name(<built-in's name>)` + the return type + a callback + `duckdb_scalar_function_set_volatile` + `duckdb_register_scalar_function(connection, function)` **replaces the built-in of that name on that DuckDB instance** (`duckdb_functions()` still lists one entry; the keyword forms resolve to the replacement), and a volatile function is never folded, so the 2.0 plan keeps the call with the built-in's own name. This is a deliberate override of built-in functions. Why it is safe:

- it is registered only by `PlanningConnection.Open`, whose connections are fresh in-memory instances (one instance per `DuckDBConnection("DataSource=:memory:")`: DuckDB.NET does not share them) that answer DESCRIBE, `json_serialize_plan` and the creation of macros and never hold or return user data;
- every replacement has the built-in's name, parameters (none; `current_schemas`, `current_setting`, `getvariable` are not overridden) and return type, checked in the unit tests against a plain connection (`typeof(f())`, and the values within seconds);
- the time kinds read the clock when they run (UTC for `now()`, in the TimeZone setting the connection had when it was opened for the others; TIMETZ is encoded as DuckDB's `dtime_tz_t`), so a differential test that executes a query on that connection still gets the real time; `version()`, `current_schema()`, `current_database()` and `txid_current()` return what the same connection answered just before the registration (a planning connection never changes any of them);
- **never** on a connection that runs user data or expressions: the `sample` and `test` runs (`DuckDBConnection` in `SampleRun`, `ModelTestRunner`, `SeededData`), the expression resolvers' sandbox (`InputExpressions`, which has its own frozen-clock macros), the DuckDB origin (`Logins`), the metadata views (`MetadataDatabase`) and the parse-only `DuckParseTree` (`json_serialize_sql` parses, it never binds, so nothing folds);
- **only where it is needed**: `PlanningConnection` plans `year(current_date)` once per process on a plain connection; where the call is kept (1.x) nothing is registered and the behavior of every golden and test is the one it was. Where it is folded (2.0) the replacements are registered, verified once (plan again on a registered connection), and if the library lacks the C API or the plan still folds, `QueryDescriber.SerializePlan` returns an error saying the build cannot keep the clock out of the plan instead of a plan that freezes the day. `DBDATABUILD_DUCKDB_CLOCK_OVERRIDES=always|never` forces the choice (for tests and for finding a problem); `PlanningConnection.UseMode` does it for the calling flow.

The calls are made through DuckDB.NET.Bindings (`NativeMethods.ScalarFunction`, public in 1.5.6), which resolve `duckdb` through the same native-library lookup as the rest of the tool, so the swapped preview library of `scripts/test-duckdb-preview.sh` is the one addressed.

**What the override does not cover.** A time function the list does not name (none found); `current_schemas`, `current_setting`, `getvariable` and `getenv` (above); an instance on which the clock is read some other way (a table function, an extension); a connection whose `TimeZone` is changed after it is opened (the planning connections never do, but the replacements would keep the old zone); the value of `txid_current()` and the others `captured` (planning only). On an instance where the built-in is replaced, anything that relied on the clock being a constant inside a macro default now fails (next paragraph).

**Macro defaults.** With the replacement a macro default of `current_date` (or `now()`, `today() - 1`, `version()` ...) is refused by DuckDB itself: `Binder Error: Default value 'current_date' for parameter 'a' is not a constant expression` (the same refusal as for `random()`), where 1.x accepted it and 2.0 without the override froze it. The tool reports it as **DDB-325** with the fix (move the call into the macro's body or into the query; a default cannot be a time function), as a warning where the macro is defined and as an error where a model reaches it, instead of the raw binder error (and, on a 1.x DuckDB, only when the override is forced on: 1.x still accepts the default and plans the call).

**Lowering of the calls** (all with the text of 1.5.6, asserted by `ClockOverridesTests` on both builds): `current_date` and `today()` are `CURRENT_DATE` on PostgreSQL and `CAST(GETDATE() AS DATE)` on SQL Server; `now()`, `current_timestamp` and `transaction_timestamp()` (new: it is `now()` in DuckDB, and SQL Server has no function of that name) are `CURRENT_TIMESTAMP` / `SYSDATETIMEOFFSET()`; `localtimestamp` is `LOCALTIMESTAMP` / `GETDATE()`; `localtime` (new: polyglot wrote `CURRENT_LOCALTIME()`, which neither engine has) is `CAST(<localtimestamp> AS TIME)`; `current_time` is `CURRENT_TIME` on PostgreSQL and `CAST(GETDATE() AS TIME)` on SQL Server (a time without the zone; pre-existing, not changed). `version()`, `txid_current()`, `current_schema()`, `current_schemas()`, `current_database()`, `current_catalog`, `current_connection_id()`, `current_query()`, `current_query_id()`, `current_transaction_id()`, `current_setting()` and `getenv()` are refused (DDB-324: "the session function f()": they answer about the DuckDB that planned the query, which no engine can reproduce; before, `version()` and `txid_current()` reached SQL Server as functions it does not have). `age()` reaches SQL Server as `AGE(...)`, which SQL Server does not have (pre-existing, not changed; an INTERVAL result has no SQL Server type either).

**How to see it.** `scripts/test-duckdb-preview.sh`, then `dotnet test tests/DbDataBuild.Tests.Unit --filter ClockOverridesTests`: `Without_the_replacements_a_folding_DuckDB_freezes_the_day_and_with_them_it_does_not` shows the constant. Against the engines: `DBDATABUILD_DUCKDB_CLOCK_OVERRIDES=never` on the preview makes `ClockConformanceTests` fail with `CAST('2026-10-15' AS DATE)` in the rendered text; with the override it passes on SQL Server 2022 and PostgreSQL 17.

**Verified** (see entry 174 for the numbers): unit and conformance on 1.5.6 (nothing changed there but the new tests); unit, quick and apply on `v2.0.0-alpha48256`. **Not verified**: Windows and macOS library names (the bindings resolve them, untested), Fabric (never run), DuckDB.NET on 2.0 (no build).
