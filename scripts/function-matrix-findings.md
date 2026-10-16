## Findings

This section is written by hand from the data below (`scripts/function-matrix-findings.md`; the numbers are the ones in the tables). Everything in it was measured on SQL Server 2022 and PostgreSQL 17 unless it says "static".

### 1. Silent wrong answers (the worst class: the engine runs the text and gives other values)

Found on a common-type overload, so a model can reach them. One example each (the data file has the rows).

| Function | Engine | What happens |
|---|---|---|
| `log(x)` | SQL Server | DuckDB's `log(x)` is base 10; the text is `LOG(x)`, which is the natural logarithm there. `log(2.5)`: DuckDB 0.3979, SQL Server 0.9163. PostgreSQL's `log(x)` is base 10 and matches. A rule would write `LOG10(x)` (decision below). |
| `date + INTERVAL '1 month 2 days 03:04:05'` (and `-`) | SQL Server | rendered as `DATEADD(MONTH, 1, dt)`: the days and the time are dropped. DuckDB 2024-02-17 03:04:05, SQL Server 2024-02-15. An interval of one unit is right (matrix row `op.interval_add`); an interval of several units is not, and nothing says so. PostgreSQL matches. |
| `md5(s)`, `sha256(s)` | SQL Server | `HASHBYTES` over an `nvarchar` hashes the UTF-16 bytes (`md5('abc')`: DuckDB 900150..., SQL Server ce1473...); `sha256` also returns binary, not hex text. |
| `bit_length(s)`, `octet_length`, `CAST(text AS BLOB)` | SQL Server | count or hold UTF-16 bytes (24 vs 48 bits for `abc`). |
| `date_part(part, x)`, `datepart` | SQL Server | `week` is the US week, not ISO (2024-03-31: DuckDB 13, SQL Server 14); on a DATE the time parts raise where DuckDB gives 0; on a TIMESTAMP `millisecond`, `microsecond`, `epoch`, `decade`, `century` and others differ (14 of 21 parts on DATE, 11 of 21 on TIMESTAMP). PostgreSQL: `epoch`, `yearweek`, `era`, `julian` and `week` do not translate (loud: `function epoch(timestamp) does not exist`); `hour` and the other time parts raise on a DATE. |
| `date_trunc('week', date)` | SQL Server | the week starts on Sunday (2024-01-14) instead of Monday (2024-01-15); also wrong for `millennium`, `century`, `decade`. |
| `date_trunc('millennium'/'century', ...)` | PostgreSQL | DuckDB 2000-01-01, PostgreSQL 2001-01-01 (PostgreSQL's millennium and century start in year 1). |
| `date_diff('decade'/'century'/'millennium', a, b)` | PostgreSQL | rendered as `AGE(b, a)`: an interval where DuckDB returns a number. On SQL Server `millisecond`/`microsecond` over dates overflow `DATEDIFF` (loud), and `week`, `decade`, `century`, `millennium` differ. |
| `/` over FLOAT, `round(FLOAT, n)`, `trunc(FLOAT, n)` | both | DuckDB's FLOAT is 32-bit; the engines compute in 64-bit, so the digits differ (`1.399999976158142` against `1.4`). |
| `strftime(ts, fmt)` | both | the format letters are not translated (`%A %B %j` come out as `A B DDD` on SQL Server, `%A %B %2460325` on PostgreSQL). Only `%Y-%m-%d` and `%H:%M:%S` agree. |
| `format('{}|{}', a, b)` | PostgreSQL | DuckDB's `format` takes `{}`; PostgreSQL's `format` is printf-like and returns the text unchanged. |
| `ucase`/`upper`/`lower` | PostgreSQL | `upper('héllo')`: the `C` collation changes ASCII only (known, matrix row `str.case_mapping`). |
| `reverse`, `substr`, `left`, `right`, `length`/`char_length` | SQL Server | a character outside the BMP is two UTF-16 units (known, `docs/research/engine-differences`). `left`/`right`/`substr` with a negative count raise on SQL Server where DuckDB returns text. |
| `ascii('')` | SQL Server | DuckDB 0, SQL Server NULL. |
| `unicode('')` | both | DuckDB -1, SQL Server NULL, PostgreSQL 0. |
| `hex(bigint)` | PostgreSQL | lower case where DuckDB gives upper case. |
| `sign(x)` | both | DuckDB returns TINYINT; the engines return the argument's type (a float for a DOUBLE). |
| `json_value`, `json_extract_path`, `json_array`, `json_object` | both | quoting and spacing of the result (`"v"` against `v`; `[null,2]` against `[2]` on SQL Server; `{"abc" : "abc"}` on PostgreSQL). |
| `fdiv`, `fmod`, `/`, `//`, `%` by zero | both | DuckDB gives Infinity or NaN; the engines give NULL or raise (known: `op.div`). |
| `covar_pop`, `covar_samp`, `regr_sxy` | PostgreSQL | a numerically unstable algorithm: on the probe's 1e10 outlier PostgreSQL returns -334 where DuckDB returns exactly 0. |

Also found: **TINYINT** maps to SQL Server's unsigned `tinyint`, so a negative TINYINT value raises (loudly) on SQL Server (`CAST(-2 AS TINYINT)`); the overloads over TINYINT are counted as `arg type unsupported` for that reason, and so are HUGEINT, the unsigned types, `BIT`/`BIGNUM` (no such type on the engine).

### 2. The tool's own failures (`lowerer error`, a bug candidate each)

`&`, `|`, `<<`, `>>`, `^`, `**` and postfix `!` (bitwise operators, power, factorial) and `^@` (starts_with) reach polyglot's DuckDB parser in the lowered text and fail with `Parse error ... Unexpected token Amp` (and `Pipe`, `LtLt`, `GtGt`, `Caret`, `CaretAt`, `Exclamation`): 43 overloads, 7 functions, the same on both engines. The failure is loud (a parse failure at compile), not silent, and the message names a token, not the construct. `power(x, y)` and `pow` are not affected.

### 3. What the tool does not catch before the engine does

On SQL Server 183 functions have an overload over common types that the engine rejects; on PostgreSQL 166. For 178 (SQL Server) and 157 (PostgreSQL) of them the static outcome is `same`: the text goes out under DuckDB's name and the only message at compile is **DDB-305** ("not covered by the matrix"), a warning, so the model builds and fails at apply time (`'ACOSH' is not a recognized built-in function name`, `function normalized_interval(interval) does not exist`). Examples: SQL Server has no `acosh`, `asinh`, `atanh`, `atan2` (named `ATN2`), `cbrt`, `cosh`, `bin`, `dayname`, `epoch`, `age`, `entropy`, `bar`, `encode`; PostgreSQL lacks `bit_count` for small integers, `date_part` on `date` with an hour, `days_in_month`, `bar`, `era`, `epoch_ms`, `datesub`. Comparing an interval (`age(a, b) > INTERVAL '1 year'`) leaves DuckDB's own `normalized_interval(...)` call in the lowered text, which neither engine has. These are the functions to give a matrix row (`unsupported` makes DDB-301 an error) or a translation.

### 4. Functions the tool refuses

423 overloads on SQL Server and 359 on PostgreSQL are refused (DDB-324 for list, struct, map and lambda arguments, `UNNEST`-like shapes and the session functions; DDB-301 for matrix rows marked unsupported, for example `lpad` with a non-constant length on SQL Server, `contains`, `mode`, `corr` on SQL Server). Refusal is the intended behavior for these.

### 5. Reading the numbers

An overload is one signature of `duckdb_functions()`; most functions have many (`+` has 40: every integer width, DECIMAL, DATE, TIME, INTERVAL ...). The outcome of a function is its worst overload, so the first summary table is harsh; the second table judges only the overloads over common types. A `not probed` function is one for which no query could be built or DuckDB rejected the arguments; the reason is in the CSV (130 are `icu_collate_*` collations and 52 `__internal_*` helpers, which are not callable by name).

### 6. What is not verified

- **Fabric**: nothing was run. Its column only says whether the text equals SQL Server's.
- The probe table has 8 rows; a function that is wrong only for other inputs shows `works`.
- Time, randomness, session and order-dependent functions were only run (the `values_compared` column says `run only`).
- Functions that need an argument the generator cannot build (UNION, GEOMETRY, VARIANT, AGGREGATE_STATE, a sequence, a variable) are listed as `not probed` with that reason.
- SQL Server 2025 (version 17, `REGEXP_*`) was not part of this run; the matrix rows with `min_version: 17` therefore show as `refused` here (version 16).
- Table functions and pragmas are listed only.

### 7. Decisions for the owner

1. **`log(x)` on SQL Server** is silently wrong. A target rule `log10-for-log` (SQL Server and Fabric: `LOG(x)` with one argument becomes `LOG10(x)`) is about the size of the existing `round-double`: a rule, a catalog entry, three tests (text, differential against DuckDB, real engine). Recommended; not done here because it changes rendered output.
2. **`date + INTERVAL` of several units on SQL Server**: either refuse an interval constant with more than one non-zero unit (DDB-324, a small check in the rule that handles `DATEADD`) or expand it into nested `DATEADD` calls (months, then days, then seconds; DuckDB adds months first, then days, then the time). Recommended: expand, and test month ends.
3. **Bitwise operators, `**`, `^`, `!`** do not parse (section 2): either teach the transpile (polyglot) or refuse them by name with DDB-324 so the message names the construct.
4. **DDB-305 for a function that fails on every engine run** could become an error for the functions the matrix now knows to be missing (SQL Server: `acosh`, `asinh`, `atanh`, `cosh`, `cbrt`, `bin`, `dayname`, `epoch`...): add matrix rows (`unsupported`) from `function-matrix.csv`; there are 183 and 166 of them and each needs a fixture in `MatrixLinterTests` and a case in `spike/constructs.yml` (CLAUDE.md), so this is a batch job, not a quick edit.
5. **FLOAT**: DuckDB's 32-bit FLOAT is written as 64-bit `FLOAT`/`DOUBLE PRECISION`. Either map to `REAL` (SQL Server) and `real` (PostgreSQL), or refuse FLOAT and tell the author to use DOUBLE. Recommended: map (both engines have a 32-bit type and the arithmetic then matches).
6. **`strftime`/`date_part`/`date_trunc` parts** need a table of the parts and formats that translate, and DDB-324 for the rest: today they pass through and give other values. A rule table would be data (`matrix`-style).

### 8. After the rewrites (entry 177)

The matrix was rerun after the work of entry 177 (items 1 to 7 of the owner's list). Counts are those of the tables above, before and after; a function that is now refused with a message at compile (DDB-301 or DDB-324) counts as **refused**, which is the intended outcome for what has no exact translation.

**Functions by worst outcome of any overload**

| Outcome | SQL Server before | SQL Server after | PostgreSQL before | PostgreSQL after |
|---|---|---|---|---|
| works | 115 | 137 | 154 | 184 |
| differs | 38 | 42 | 30 | 25 |
| engine error | 191 | 28 | 168 | 38 |
| refused | 138 | 282 | 130 | 242 |
| lowerer error | 7 | 0 | 7 | 0 |

**Functions judged on the overloads over common types**

| Outcome | SQL Server before | SQL Server after | PostgreSQL before | PostgreSQL after |
|---|---|---|---|---|
| works | 120 | 159 | 153 | 196 |
| differs | 31 | 31 | 25 | 19 |
| engine error | 183 | 15 | 166 | 31 |
| refused | 98 | 234 | 88 | 193 |
| lowerer error | 7 | 0 | 7 | 0 |

**Overloads**

| Outcome | SQL Server before | SQL Server after | PostgreSQL before | PostgreSQL after |
|---|---|---|---|---|
| works | 299 | 424 | 404 | 545 |
| differs | 89 | 93 | 67 | 63 |
| engine error | 1458 | 94 | 1259 | 117 |
| refused | 423 | 1668 | 346 | 1507 |
| lowerer error | 43 | 0 | 43 | 0 |

What changed: the silent wrong answers of section 1 for `log`, multi-unit intervals, `date_part` (week, isoyear, the time parts of a DATE, millisecond, microsecond, epoch, decade, century), `date_trunc('week')`, `date_diff` of the small units and of decade, century and millennium, `strftime` and `format`, FLOAT division and rounding, and the covariance are gone or refused with a name; the lowerer failures of section 2 are zero; the functions of section 3 that failed at the engine are rewritten (a table, `FunctionTemplates`) or refused at compile by the rows `fn.missing.*`. What is left in `engine error` is overloads over INTERVAL, TIME and BLOB arguments and a few functions whose arguments the probe builds in a way the engine rejects (`min(a, b)`, `left(text, bigint)` on PostgreSQL, `round(double, column)` on PostgreSQL); `differs` is mostly the documented domain differences (`power`, the hyperbolics, the string and UTF-16 findings) and the tolerance of the rewrites.
