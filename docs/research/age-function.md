# Scoping `age()` for SQL Server and PostgreSQL

Owner request 2026-10-15, part B (entry 176). The owner saw `age(...)` reach SQL Server as `AGE(...)`, a function that does not exist there. This note says what DuckDB's `age` is, whether PostgreSQL's matches, how SQL Server can express each shape a model uses, and what to build. **Built in entry 177** (rule `age-parts` for SQL Server and Fabric, `normalized-interval` and `age-second` for PostgreSQL, matrix row `fn.age`, test `AgeRuleConformance` on 961 ordered pairs times 12 shapes on both engines); the section 8 recommendation was followed as written.

Everything here is measured by `scripts/age-probe.sh` (test `AgeProbe`, `tests/DbDataBuild.Tests.Conformance/FunctionMatrix/AgeProbe.cs`; output `docs/research/age-function-results.txt`) on DuckDB 1.5.6 (the one the tool ships), **SQL Server 2022 (16.0.4265, version 16)** and **PostgreSQL 17.11**. Fabric was not run.

## 1. DuckDB's overloads

`duckdb_functions()` lists four: `age(TIMESTAMP, TIMESTAMP)`, `age(TIMESTAMP WITH TIME ZONE, TIMESTAMP WITH TIME ZONE)`, `age(TIMESTAMP)` and `age(TIMESTAMP WITH TIME ZONE)`, all returning INTERVAL. A DATE argument is cast to TIMESTAMP by the binder (the plan has `CAST(dt AS TIMESTAMP)`), so there are no separate DATE overloads and a date-only call has no time-of-day part. `age(ts)` with one argument is `age(current_date, ts)` where `current_date` is midnight today (checked on DuckDB: `age(TIMESTAMP '2020-05-17 10:00:00') = age(CAST(current_date AS TIMESTAMP), TIMESTAMP '2020-05-17 10:00:00')` is true), so it depends on the clock and the time zone of whoever evaluates it.

## 2. Exact semantics

`age(a, b)` is the symbolic difference `a - b` as an INTERVAL of **months, days and microseconds** (years are 12 months), with borrowing:

1. Let `L` be the later and `E` the earlier of `a` and `b`; the result is taken from `E` to `L` and negated when `a` is before `b`.
2. Months: `12 * (year(L) - year(E)) + (month(L) - month(E))`. Days: `day(L) - day(E)`. Time: `time(L) - time(E)`.
3. If the time is negative, add 24 hours to it and subtract one day.
4. If the days are then negative, add **the number of days of the month of `E`** (the earlier timestamp, its year decides leap) to them and subtract one month.
5. The interval is `(months, days, micros)`; the sign is applied to all three.

Examples (DuckDB, and PostgreSQL's `age`, which prints `mon`/`mons`, identical in value):

| a | b | DuckDB | PostgreSQL |
|---|---|---|---|
| 2024-03-31 | 2024-02-29 | 1 month 2 days | 1 mon 2 days |
| 2024-03-01 | 2024-01-31 | 1 month 1 day | 1 mon 1 day |
| 2024-02-29 | 2024-01-31 | 29 days | 29 days |
| 2025-02-28 | 2024-02-29 | 11 months 28 days | 11 mons 28 days |
| 2024-01-31 | 2024-03-31 | -2 months | -2 mons |
| 2024-03-01 | 2023-03-01 | 1 year | 1 year |
| 2024-03-01 10:00 | 2024-01-31 12:00 | 1 month 22:00:00 | 1 mon 22:00:00 |
| 2024-03-01 00:00:00.5 | 2024-02-29 23:59:59.75 | 00:00:00.75 | 00:00:00.75 |

Note `2024-02-29` against `2024-01-31` is **29 days**, not one month: January 31 plus a month would be February 29 but the rule borrows (29 - 31 < 0), so a `DATEADD`-based formula (add the months, see what is left) is **not** the same function. Intervals compare by a normalized value, a month counting as 30 days (and a day as 24 hours), in DuckDB and in PostgreSQL alike; the lowered text for a comparison carries DuckDB's own `normalized_interval(...)` wrapper (section 4).

## 3. Does PostgreSQL's native `age` match?

**Yes, exactly.** On 1849 ordered pairs of 43 edge dates (month ends of 2023, 2024, 2025 and the century years 1900, 2000, 2100, every leap day, both directions, equal pairs) and 3600 ordered pairs of 60 timestamps (ten dates times six times of day, 00:00:00, 00:00:00.000001, 08:15:30, 10:30:45.5, 12:00:00, 23:59:59.999999): **all 5449 intervals are equal** as (months, days, microseconds) (`age(a, b) as an interval`). It follows that the tool's present output for PostgreSQL, `AGE(CAST(a AS TIMESTAMP), CAST(b AS TIMESTAMP))`, is right. Not tested: TIMESTAMPTZ arguments (they depend on the session time zone on both sides), years outside 1900 to 2100.

Differences around it on PostgreSQL (all measured):

- `extract(second FROM age(...))`: PostgreSQL returns the fractional second (`-45.5`, `-0.000001`), DuckDB the whole second (`-45`, `0`): 2400 of 5449 rows differ. `trunc(extract(second FROM age(a, b)))` is equal on all 5449. The same rule would be needed for `extract(second FROM timestamp)`; not part of this note.
- the result type of `extract` is `numeric` (DuckDB BIGINT); the values are equal.
- `age(a, b) < INTERVAL '1 year'` and the like are equal on all 5449 rows when written directly. **The tool's lowered text is not directly runnable on PostgreSQL**: it contains `normalized_interval(age(a, b)) > normalized_interval(CAST('1 year' AS INTERVAL))` and PostgreSQL has no `normalized_interval` (`function normalized_interval(interval) does not exist`, measured). Removing the wrapper gives the engine's own comparison, which is the same ordering (1600 of 1600 pairs of ages ordered identically).

## 4. What the tool sends today

The lowered text (DuckDB dialect) has two shapes only:

- `date_part('year' | 'month' | 'day' | 'hour' | 'minute' | 'second', age(a, b))` for `extract(year FROM age(...))`, `date_part(...)`, `year(age(...))` and `month(...)`, `day(...)`;
- `normalized_interval(age(a, b)) <op> normalized_interval(CAST('<literal>' AS INTERVAL))` for a comparison, and `age(a, b)` bare for a value.

| Shape | SQL Server today | PostgreSQL today |
|---|---|---|
| `age(a, b)` as a value | `AGE(a, b)`: `'AGE' is not a recognized built-in function name` | works, 5449 of 5449 equal (but INTERVAL has no DDL type, so it cannot be an output column) |
| `extract`/`date_part`/`year()` of `age` (year, month, day, hour, minute) | `DATEPART(YEAR, AGE(a, b))`: the same error | `EXTRACT(YEAR FROM AGE(a, b))`: works, 5449 of 5449 equal |
| `second` | same error | 2400 of 5449 rows differ (fractional) |
| `age(...) <op> interval`, two ages compared, `ORDER BY age(...)` | the same error (`NORMALIZED_INTERVAL(AGE(...))`) | `function normalized_interval(interval) does not exist` |

Static status for `age` is `same` on both (name unchanged); the matrix lint says DDB-305 only, a warning. So the model builds and fails at apply, on SQL Server for every shape and on PostgreSQL for comparisons.

## 5. The shapes, and what each can be on SQL Server

SQL Server has no interval type, so an `age` result cannot exist as a value. It can exist inside an expression that ends in a number or a boolean. The T-SQL below is the text the translation would write; `A` and `B` are `datetime2(6)` expressions (a DATE operand is cast, as the plan already does):

```
E   = CASE WHEN A < B THEN A ELSE B END        -- the earlier
L   = CASE WHEN A < B THEN B ELSE A END        -- the later
S   = CASE WHEN A < B THEN -1 ELSE 1 END       -- the sign
TB  = CASE WHEN CAST(L AS time(6)) < CAST(E AS time(6)) THEN 1 ELSE 0 END            -- the time borrows a day
BW  = CASE WHEN DAY(L) < DAY(E) OR (DAY(L) = DAY(E) AND CAST(L AS time(6)) < CAST(E AS time(6))) THEN 1 ELSE 0 END   -- the day borrows a month
MO  = DATEDIFF(MONTH, E, L) - BW                                                       -- whole months
DD  = DAY(L) - DAY(E) - TB + BW * DAY(EOMONTH(E))                                     -- days
US  = DATEDIFF_BIG(MICROSECOND, CAST(E AS time(6)), CAST(L AS time(6))) + TB * CAST(86400000000 AS bigint)   -- microseconds
```

`DATEDIFF(MONTH, E, L)` counts month boundaries, which is `12 * (year(L) - year(E)) + month(L) - month(E)`, the symbolic month difference of step 2, not the elapsed months (`DATEADD` is what must not be used, section 2). Each shape then is an expression of S, MO, DD, US; **each was run on SQL Server as written inline (no CTE, nothing precomputed) over every pair and compared with DuckDB's value**:

| Shape (as lowered) | T-SQL | DuckDB vs T-SQL, 5449 pairs | Class |
|---|---|---|---|
| `date_part('year', age(a, b))` (also `extract(year ...)`, `year(age(...))`) | `S * (MO / 12)` | 5449 equal | **portable exactly** |
| `date_part('month', age(a, b))` | `S * (MO % 12)` | 5449 equal | **portable exactly** |
| `date_part('day', age(a, b))` | `S * DD` | 5449 equal | **portable exactly** |
| `date_part('hour', ...)` | `S * (US / CAST(3600000000 AS bigint))` | 5449 equal | **portable exactly** (the divisor must be a bigint: with a plain literal SQL Server divides as decimal and returns -8.25 for -8 in 2330 rows, found by the probe) |
| `date_part('minute', ...)` | `S * ((US / 60000000) % 60)` | 5449 equal | **portable exactly** |
| `date_part('second', ...)` | `S * ((US / 1000000) % 60)` | 5449 equal | **portable exactly** (whole seconds, as DuckDB) |
| `normalized_interval(age(a, b)) <op> normalized_interval(<age or interval literal>)`: compare, `=`, `BETWEEN`, `ORDER BY` | the key `S * ((MO * 30 + DD) * CAST(86400000000 AS bigint) + US)`, compared with the other key (a literal interval becomes its constant: months times 30 plus days, times 86400000000, plus its time) | `> 1 year`, `>= 30 days`, `= 1 month`, `< 0 days`: 5449 equal each; two ages `<`: 1600 of 1600 equal | **portable exactly** (the order and the equality are DuckDB's normalized comparison; `= INTERVAL '1 month'` is also true for 30 days, in DuckDB too) |
| `age(a, b)` selected as a value, a `CAST(age(...) AS VARCHAR)`, `to_json`, any output column | no SQL Server type | not applicable | **must be refused** (the DDL has no INTERVAL type either) |
| `date_part('epoch' | 'millisecond' | 'microsecond', age(...))`, `epoch(age(...))` | would need DuckDB's definition of an interval's epoch (a month as 30 days) | not measured | **refuse** (no demand shown; add by the same key if wanted) |
| `age(...)` as an operand of `+`/`-` with a date or timestamp, in `date_trunc`, in `generate_series` | needs interval arithmetic with month-end rules | not measured | **must be refused** |
| `age(ts)` (one argument) | the same with `A = CAST(GETDATE() AS date)` as a datetime2 | the equivalence `age(ts) = age(current_date, ts)` on DuckDB is true; the T-SQL text is the two-argument one | **portable with a documented difference**: the day is the engine's own clock and zone (matrix row `fn.current_date`), not DuckDB's |
| any `age` over TIMESTAMPTZ arguments | `datetimeoffset` and the session zone | not measured | **refuse** |

The proof covers both directions (a before b: S = -1, 2673 of the 5449 pairs), equal pairs (103, all zero), month ends, the leap days of 2000 and 2024, the non-leap century years 1900 and 2100, and all six times of day including the microsecond before midnight. Not covered: years below 1900 or above 2100, `datetime2` values with 7 digits of fraction (the tool writes `DATETIME2(6)`).

Operand duplication: E, L, S, TB and BW repeat the operands several times, so the rule may only apply when each operand is a column, a literal, a cast of one, or a deterministic expression of them; an operand with a subquery or a volatile call (`random()`) is refused. `current_date` is stable inside a statement on SQL Server, so repeating it is safe.

## 6. PostgreSQL

Keep `age(a, b)` for every shape that matches (values and comparisons), with two changes: strip `normalized_interval(...)` when its argument is an interval (the engine's comparison is the same, measured), and write `trunc(extract(second FROM age(...)))` for the second. An `age` selected as a value stays as it is (it works; the INTERVAL output column is refused by the DDL rule, not by this one).

## 7. Rule-code changes (the list)

- `RewriteCatalog`: one new **required** rewrite, `age-parts`, for SQL Server and Fabric (without it the text cannot run); one **required** rewrite `normalized-interval` for PostgreSQL (the wrapper has no function), and the existing `extract second` question as `age-second` (optional: only keeps DuckDB's whole second).
- `TargetRules` (`DbDataBuild.Targets/Rules/TargetRules.cs`): patterns on the DuckDB-dialect AST: `date_part('<part>', age(x, y))` and the comparison form with `normalized_interval(age(...))` on either side, `age(x)` with one argument; operands tested for the duplication condition; the T-SQL text above, written in DuckDB-dialect functions that polyglot translates (`date_diff('month', ...)`, `day`, `last_day`, `CASE`) so that the existing rules (`date-diff-boundaries`) do not interfere, or as markers like the existing `ddb_` ones (`TargetRules.Markers`) where polyglot would rename. Either way the rendered file names the rule in its header.
- **Matrix** (`matrix/constructs.yml`): one row `fn.age`, detect `fn:AGE`, `sqlserver`/`fabric`: **unsupported** with the note that the shapes of `age-parts` are rewritten before the lint and any other remaining `AGE` is an error; `postgres`: `native` with the notes of section 3. This is where "refuse with a clear message" lives: a leftover `AGE` reaches the lint after the rule has taken the shapes it can, and is reported **DDB-301** (`construct unsupported`) with the row's note. DDB-324 is raised by the *lowerer*, which is target-neutral by design (one lowered artifact for all targets), so it cannot refuse for SQL Server only without breaking that; the matrix row is the existing mechanism for per-target refusal (as for `lpad`). Needs a fixture in `MatrixLinterTests`, a case in `spike/constructs.yml`, and regenerated goldens (the matrix hash is in every rendered header).
- `RewriteOptOutProbes`/rewrite listing: `dbdatabuild help matrix --rewrites` picks the new entries up from the catalog.
- Tests (CLAUDE.md "a rule is tested three ways"): the unit text; a differential run against DuckDB on the pair table (`AgeProbe` is that table and the comparison, to be turned into assertions); a real-engine test on SQL Server and PostgreSQL. Docs: DESIGN 7.6.1 table, an entry in the progress log.

## 8. Decision for the owner, and the cost

**Recommended**: translate on SQL Server and Fabric the shapes that are portable exactly (`year`, `month`, `day`, `hour`, `minute`, `second` of an `age`, and comparisons, ordering and equality of an `age` with an interval or another `age`), refuse the rest (an `age` as a value or output, arithmetic with it, epoch and sub-second parts, TIMESTAMPTZ) with DDB-301 and a message that says which shape is unsupported and what to write instead (`date_diff('month', ...)`, or compute the parts). On PostgreSQL strip `normalized_interval` and truncate the second.

Cost, in the repository's terms: one rule of roughly 150 to 250 lines in `TargetRules` plus the operand test, three catalog entries, one matrix row with its fixture and spike case, golden regeneration (every rendered header changes with the matrix hash), the three-way tests (the probe is the differential and real-engine test, mostly written), DESIGN 7.6.1, an entry. About one working session; the risk is the size of the rendered expression (the SQL Server text for one `extract(year ...)` is about 450 characters for plain column operands and more with casts or qualified names, because the operands repeat), which is readable only as generated text. The alternative of refusing all of `age` on SQL Server costs one matrix row and its fixture and leaves the model author to write `date_diff` or a hand-made expression; it is the cheaper path if the shapes are rare.

What is **not** recommended: translating `age(a, b)` as `DATEDIFF(MONTH, ...)` with an adjustment by `DATEADD` (wrong on month ends, section 2), or mapping it to an `nvarchar` text of the interval (loses comparison and arithmetic and invents a format).
