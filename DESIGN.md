# DESIGN.md: DbDataBuild (explicit SQL transformation tool)

Status: design baseline for the first build. Written to be read by Claude Code and by humans. Items marked **[VERIFY]** are assumptions that must be checked against current docs or tests before code depends on them. Items marked **[DECIDE]** are open decisions for the owner (none are open at the moment; see section 17).

---

## 1. Purpose

A .NET command-line tool that builds and maintains analytics tables and views on **SQL Server Enterprise** (primary) and **Microsoft Fabric Warehouse** (secondary), from SQL model files written in **DuckDB dialect** and transpiled per target.

Model definitions follow a minimal subset of SQLMesh's style. Execution is **explicit, plan-then-apply, and predictable**. A person must always be able to tell, before anything runs, exactly which scripts will execute against a target and why.

## 2. Non-negotiable principles

These are invariants. Each one must have automated tests (section 15).

1. **Nothing touches a target without an explicit request.** No startup migrations, no auto-heal, no implicit operations, no side effects from read-only commands.
2. **State informs reports and blocking. It never triggers or selects actions.** Tracking tables are read for visibility and for preflight blocks only.
3. **Plan, review, apply.** `plan` is read-only toward the target. It asks open questions, then produces a complete plan. `apply` executes exactly what the plan records, nothing more.
4. **Every statement that will run is shown in full, with the reason it is needed.** Plans are human-readable documents first.
5. **Stateless by default.** What to do is derived from the git repo and the target's current catalog and data. Watermarks come from target data. Tracking tables are append-only records, not control inputs.
6. **Configuration errors explain themselves.** Stable diagnostic codes, file and line, what was found, what is supported, suggested fix. Never a stack trace as primary output.
7. **Supported states are enumerated data.** The support matrix and the planning decision table are tested data, not hidden branching logic.
8. **An agent must be able to operate without access to data.** Everything the tool writes by default (diagnostics, logs, plans, metadata, the default output of every command) is schema, SQL, hashes and counts, so an AI agent, or any automation, can plan, apply, test and review without ever being shown a row. That is a property of the defaults, not a ban: an operator who wants to inspect data can ask for it explicitly (`diff --show-values`, section 9.10), and what is shown goes only to the output of that command, never into a log or a plan. Declared runtime parameter values and watermark resolver results are logged (section 6.6) because the load needs them recorded. The agent guidance says not to ask for values without the person's go-ahead.
9. **No virtual environments, no implicit backfills, no automatic change categorization.**

## 3. Non-goals (first version)

- Python models, SCD Type 2 snapshots, virtual environments, automatic breaking-change categorization, a docs website, a package ecosystem, cross-project meshes, and a built-in scheduler (SQL Agent or CI invokes the tool).
- Schema names and object names as parameters (environment-dependent identifiers). Rendered SQL uses each model's fixed, declared names, and the environment is selected by the target connection. To be revisited in a later design.
- Engines other than SQL Server, Fabric, and PostgreSQL. PostgreSQL is included early, as a deliberately non-T-SQL target, to expose multi-target pitfalls in the shared code (section 8). The design must *allow* further targets.

## 4. Development ground rules (for Claude Code)

- **Schema-only development.** The dev environment has no network path to production. Never request, log, or commit production data. Fixtures are synthetic.
- **Never connect to any host except the configured local or ephemeral test engine.**
- Write tests first for each invariant and diagnostic. Run the full test suite before proposing a change.
- Keep commands, diagnostics, matrix entries, and decision-table rows as **data** with tests enumerating them.
- Any new code path that can execute a non-query statement must go through the mutation gate (section 9.3). A test enforces this.
- Prefer small, reviewable changes. Update this document when a decision changes.
- Do not add dependencies without noting them in section 5 and verifying the license.

## 5. Technology

| Concern | Choice | Notes |
|---|---|---|
| Language/runtime | C#, .NET 10 (LTS; moved from .NET 8, which leaves support in November 2026, so that Terminal.Gui 2.x can be used) | Single-file, self-contained publish for win-x64 and linux-x64 |
| CLI | `System.CommandLine` | Interactive prompts via a small abstraction (Spectre.Console is acceptable) |
| SQL parse/transpile | `polyglot-sql` (Rust, MIT) via its C FFI library (`polyglot-sql-ffi`) | Bound with P/Invoke (`DbDataBuild.Sql`). Native library built from the pinned commit by `scripts/build-polyglot.sh` (verified in the milestone 1 spike, `spike/RESULTS.md`). The FFI crate is not on crates.io, so it is built from the repo. Dialects are `tsql`, `fabric`, `postgresql`, `duckdb`; SQL Server maps to `tsql` |
| Offline DuckDB | `DuckDB.NET.Data` (MIT, verified) | Synthetic data, local execution, result typing |
| T-SQL syntax check | `Microsoft.SqlServer.TransactSql.ScriptDom` (MIT, verified) | Per-version parsers **[VERIFY]** Fabric coverage. Syntax only: the spike showed it accepts `GROUP BY 1` and `[1,2,3]`, which SQL Server then rejects |
| Database access | `Microsoft.Data.SqlClient` (MIT, verified) | Integrated and Entra auth; `SqlBulkCopy` for test loads. Server is given as `host,port` |
| PostgreSQL access | `Npgsql` (PostgreSQL license, verified permissive) | Binary `COPY` for test loads **[VERIFY]** |
| YAML | `YamlDotNet` | Plus published JSON Schemas for editor validation |
| Tests | xUnit, a snapshot library (e.g., Verify) for golden files (plain golden files with `UPDATE_GOLDEN=1` are used so far) | `JsonSchema.Net` **7.0.4** (MIT) for schema conformance tests only. The conformance project (`tests/DbDataBuild.Tests.Conformance`) also uses `Microsoft.Data.SqlClient` (MIT), `Npgsql` (PostgreSQL license), `DuckDB.NET.Data.Full` (MIT) and `Xunit.SkippableFact` (MS-PL), and only there: product code must not reference a database driver until the mutation gate exists (a test checks it). Do not upgrade to 9.x without review: it ships under the Open Source Maintenance Fee EULA, not a plain open-source license |
| Containers | Optional only | Must work with no container runtime (Windows dev has no nested virtualization) |

### Naming

The product is **DbDataBuild**, a sibling of DbDataSync. As with `dbdatasync`, the CLI is the full lowercase product name.

| Item | Name |
|---|---|
| CLI | `dbdatabuild` |
| Config file | `dbdatabuild.yml` |
| Tracking schema name (default) | `dbdatabuild` |
| Diagnostic prefix | `DDB-` (short form, easier to read in error messages) |
| .NET namespaces / projects | `DbDataBuild.*` |

All of these derive from a single `ProductInfo` constant in the code base, so a later rename is mechanical. **[VERIFY]** the name for collisions (NuGet, GitHub, trademark) before the first public release.

### Solution layout

```
src/
  DbDataBuild.Cli/                            # command definitions, effect-class headers, prompts
  DbDataBuild.Core/                           # project model, plan model, questions, diagnostics
  DbDataBuild.Models/                         # model definition (YAML) loader and validation, model graph
  DbDataBuild.Define/                         # `define`: inference from a query, questions, definition writer and splice editor, diff
  DbDataBuild.Sql/                            # polyglot FFI binding, AST helpers, hashing, matrix linter
  DbDataBuild.Targets/                        # ITarget + SqlServer, Fabric and PostgreSQL implementations: strategy loaders, offline validators, renderer
  DbDataBuild.Targets.DuckDb/                 # canonical dialect engine, synthetic data runner
  DbDataBuild.State/                          # tracking tables access, schema/physical/definition hashes
  DbDataBuild.Testing/                        # ephemeral database manager, differential runner
tests/
  DbDataBuild.Tests.Unit/
  DbDataBuild.Tests.Conformance/              # DuckDB vs target differential tests (needs a SQL Server test instance)
  DbDataBuild.Tests.Golden/                   # plan documents, dry-run output, diagnostics
matrix/                 # support matrix data (YAML)

schemas/                # JSON Schemas for config, models, sources, answers, plans
docs/diagnostics/       # generated diagnostic catalog
rendered/               # committed, rendered load operations per target (section 6.6)
```

## 6. Model definitions

SQLMesh-style concepts (kinds, grain, declared columns), with **no custom grammar**. A model is a pair of files with the same base name:

- `<name>.sql`: a single SELECT in **DuckDB dialect**. Plain SQL with no header, no macros, and no templating. It runs as-is in DuckDB.
- `<name>.yml`: the model definition, written in a strict YAML subset (section 6.3).

```yaml
# models/marts/fct_orders.yml
name: marts.fct_orders
kind:
  type: incremental_by_unique_key
  unique_key: [order_id]
grain: [order_id]
connections=: [sqlserver, fabric]
columns:
  - name: order_id
    type: BIGINT
    nullable: false
  - name: customer_id
    type: BIGINT
  - name: order_date
    type: DATE
  - name: amount
    type: DECIMAL(14, 2)
  - name: discount_code
    type: VARCHAR(20)
```

```sql
-- models/marts/fct_orders.sql
SELECT
  o.order_id,
  o.customer_id,
  o.order_date,
  o.amount,
  o.discount_code
FROM staging.orders AS o
```

Upstream references are plain table names, resolved against the model graph and the committed source descriptors. There is no `ref()`.

### 6.1 Supported kinds (first milestone)

| `kind.type` | Meaning | Load behavior |
|---|---|---|
| `view` | View over upstream | DDL only (create/alter via plan). `apply` issues no data statements |
| `full` | Table fully reloaded | Transactional delete+insert of contents (table swap can be added later as a named load operation, section 6.6) |
| `incremental_by_unique_key` | Upsert by key | Watermark-free: rows selected per model query, applied via delete+insert or MERGE per target. Requires `unique_key` |
| `incremental_by_time_range` | Time-sliced incremental | Range derived from `MAX(time_column)` in the target minus `lookback`, never from a state table. Requires `time_column` |

Kinds define table semantics and supply the **default** load operation. Explicit, additional load operations are declared with `loads` (section 6.6).

### 6.2 Definition rules

- `columns` is **required for all models**. It is the declared output schema. It drives the shape hash, the plan's DDL, and offline validation with no database connection.
- `targets` lists which engines the model must be valid for. The project default applies if omitted. The linter evaluates the matrix against these targets.
- `grain` is required for incremental kinds and is checked against `unique_key`.
- `name` is explicit in the file and must match the path convention (`models/marts/fct_orders.yml` is `marts.fct_orders`). A mismatch is a diagnostic.
- Every `.sql` needs a `.yml` and vice versa. An orphan of either kind is a diagnostic (DDB-1xx) that points to `dbdatabuild project model update`.
- Unknown keys are errors (DDB-1xx), not ignored.
- Renames are never inferred. They are declared explicitly in the definition:

  ```yaml
  renames:
    - from: cust_nm
      to: customer_name
  ```

### 6.3 Strict YAML subset

YAML has well-known pitfalls (for example, unquoted `no`, `on`, or `2026-10-12` being coerced to booleans or dates by some parsers). The loader therefore applies a strict subset:

- **All scalars are read as strings** and validated against the JSON Schemas in `schemas/`. Nothing is coerced by YAML type rules. (The reader notes whether a scalar was written in quotes, which only model tests use, to tell a plain `null` from the text `"null"`.)
- **Unknown keys and duplicate keys are errors.** Anchors, aliases, merge keys, and custom tags are not allowed.
- Enumerated values are lowercase snake_case. SQL types are written as plain strings (`DECIMAL(14, 2)`).
- The JSON Schemas (`schemas/model.schema.json`, `schemas/config.schema.json`) are associated with the files by glob (`models/**/*.yml`, `dbdatabuild.yml`; see `.vscode/settings.json`) so editors, and Claude Code, validate definitions as they are written. The C# loaders are authoritative because they produce the diagnostics, and they also check what a schema cannot (name against path, grain against `unique_key`, column references). A conformance corpus runs through both, so the schemas and loaders cannot drift apart: structural errors must fail in both, semantic-only errors must pass the schema and fail the loader. Editors read unquoted `false` or `16` as typed values, so the schemas accept both forms where the loader reads strings. `schemas/answers.schema.json` covers answers files (section 10.1); `schemas/plan.schema.json` covers plan files (section 10.2); it cannot verify the content hash.
- Every error carries file, line, and column, and uses the diagnostic format in section 14.

**Bodies have no macros or templating.** Load strategies (section 6.6) wrap the body, for example `SELECT * FROM (<body>) AS b WHERE b.order_date >= @start`. Predicate pushdown into bodies with aggregates or windows is not rewritten for the author: slicing the finished query is always correct, and where an engine cannot apply the filter early the tool says so (DDB-225, section 6.6.2). If wrapping proves too costly for specific models, revisit with standard bind-parameter placeholders declared in the YAML, still without custom syntax.

### 6.4 Parsing strategy

- The definition is loaded with `YamlDotNet` under the strict rules above, then validated against its JSON Schema. The tool has **no custom grammar parser**.
- The body is parsed with polyglot as DuckDB dialect into an AST. **The AST is used for analysis (dependencies, lineage, lint, hashing) and for transpilation.** For any emitted SQL that is not produced by transpilation, prefer minimal text substitution over regeneration, to preserve comments and hints. **[VERIFY]** round-trip behavior.
- If polyglot fails to parse a file, report a diagnostic. Do not fall back to silent best-effort.

### 6.5 Definition generation and maintenance (`dbdatabuild project model update`)

The definition file can be generated, or updated, from the query body plus a guided walkthrough. The command is **offline and repo-only**: it never connects to a target, and it writes only the definition (`.yml`) files it is pointed at. It never modifies a `.sql` file.

**Usage**

```
dbdatabuild project model update models/marts/fct_orders.sql            # walkthrough, shows diff, asks before writing
dbdatabuild project model update models/marts --answers define-answers.yml --write   # non-interactive
dbdatabuild project model update --check                                # CI: exit non-zero if any definition is out of sync with its body; writes nothing
```

**Inputs:** the `.sql` body, the existing `.yml` if any, the project model graph, source descriptors (committed schema exports), and the project profile (string semantics, default connections).

**What it infers** (offline, no target connection). Every inferred value is a *proposal with evidence*. Accepting it is an explicit answer (a keypress interactively, `accept: inferred` in an answers file, or `--accept-inferred`, which is recorded in the diff output). `--accept-inferred` covers only high-certainty proposals (names from paths, types resolved by the DuckDB describe, nullability from lineage); kind, grain and unique key, targets, time column, renames, and load operations always need an explicit answer. There are no silent defaults.

| Definition field | How it is proposed | Question if unclear |
|---|---|---|
| `name` | Path convention (`models/marts/fct_orders.sql` becomes `marts.fct_orders`) | Always confirmed |
| `columns` (names, types) | The body is resolved by asking DuckDB to describe the query (`LIMIT 0`) against empty tables built from the upstream declared columns and source descriptors. Names and DuckDB types are mapped to logical types | If a type can't be resolved, or a mapping is lossy for a target, ask |
| Nullability | Proposed from lineage (for example, a column passed straight through from a `NOT NULL` upstream column by an inner join) | Otherwise ask per column |
| `grain` / `unique_key` | Candidates from upstream keys, `GROUP BY` columns, and `DISTINCT` usage | Choose one (required for incremental kinds) |
| `kind` | Never inferred | Options listed with one-line consequences |
| `time_column` (time-range kinds) | Candidates among `DATE`/`TIMESTAMP` output columns | Choose one, plus lookback |
| `targets` | Project default | Confirm. The matrix linter runs during the walkthrough and shows which constructs limit which targets |
| Column collations | Project profile default | Ask only for exceptions |
| `renames` | A missing declared column plus a new column of the same type and position | Question: rename or drop-and-add |
| `loads` | The kind supplies a default operation | Ask whether to declare additional operations (section 6.6) |

**Questions** use the same mechanism as planning: stable IDs (`Q-define-<model>-<field>`), context, explicit options, an answers file, and in non-interactive mode all unanswered questions are listed at once with the YAML to add.

**Generate and replace semantics**

- **No definition present:** a new `<name>.yml` is created beside the `.sql` file, in canonical formatting with a fixed key order.
- **Definition present:** the command computes a field-level diff and edits the file by **minimal text splices** located with the YAML parser's source positions, so comments, key order, and the author's own formatting survive. It does not re-serialize the whole file. **[VERIFY]** `YamlDotNet` does not round-trip comments when re-serializing, which is why splices are used. Fields are treated by class:
  - *Human-authored* (`name`, `kind`, `grain`, `unique_key`, `targets`, `renames`, `loads`, comments) are never changed without a question showing old and new values.
  - *Derived* (`columns`) are updated per column through questions (added, removed, type changed, nullability changed). New entries use canonical formatting.
  - *Unknown keys* are errors, never dropped silently.
- **Idempotent:** a second run on an in-sync definition produces no diff.
- **The `.sql` body is never opened for writing.** A hash check confirms it is unchanged after the command.
- **Nothing is written before confirmation.** The unified diff is shown first. Interactive mode asks for confirmation; non-interactive mode requires `--write`.
- **Safe write:** the file's hash is checked against what was read, the write goes to a temp file then an atomic rename, and the command refuses if the file changed in between.

**Relationship to planning:** `dbdatabuild connection deploy` detects a definition that no longer matches its body (declared `columns` versus resolved query output) and refuses to plan that model, pointing to `dbdatabuild project model update`. Planning never proceeds from a stale declared schema. History dispositions (whether a new column's history was backfilled) are **not** definition questions, since they depend on target state. They remain planning questions.

**Effect class:** repo files only. Tests assert zero database connections, `.sql` byte-equality, and idempotency (section 15.5).

### 6.5.1 How `define` works (as built)

The text above is the design. This records what the implementation does, including the decisions the design left open.

**Mapped models (as built).** A table that exists and that the tool does not build is a **mapped model**: `models/<schema name>/<table>.yml` with `kind: {type: mapped}` and no `.sql`, in the same column shape as a model definition (it replaces the old `sources/` folder, which is no longer read and is an error naming where its files go). A folder may be named `sources`, or anything, and `defaults: {kind: {type: mapped}}` in its `_dbdatabuild.yml` makes every file beneath it mapped. `name` must equal the path under `models/` with `/` replaced by `.` (as for models), a `.sql` beside a mapped model is an error, an optional `grain` feeds grain candidates, and `connections` (layered like a model's) says where the table exists (the project's default connections without it; not yet checked against the models that read it). The schema is `schemas/source.schema.json`, and `schemas/model.schema.json` hands any file that says `kind: {type: mapped}` to it; `validate` checks them. `import` exports them from a connection (below); they can still be written by hand.

**Source descriptors**

```yaml
# models/staging/orders.yml
name: staging.orders
kind:
  type: mapped
grain: [order_id]
columns:
  - name: order_id
    type: BIGINT
    nullable: false
  - name: amount
    type: DECIMAL(14, 2)
  - name: customer_id
    type: BIGINT
indexes:                                 # optional: what the table has (import writes them)
  - {name: ix_orders_amount, columns: [amount], include: [order_id]}
foreign_keys:
  - {name: fk_orders_customer, columns: [customer_id], references: {table: staging.customers, columns: [customer_id]}}
```

**Importing sources** (`import`, effect: target read-only; with `--write` it also writes mapped models under `models/`). `import [schema_name.table_name ...]` reads the columns, native types, nullability and primary key of the tables and views the patterns match (`*` and `?` are wildcards, case-insensitive; no pattern refreshes every descriptor the project has) through the read login: the same catalog queries planning uses, plus the schema names and primary keys. It runs no query against the data, needs no tracking tables, and changes nothing in the target. Native types map to logical types through an enumerated table (`SourceTypes`), with a fit for each: *exact* (`int` is `INTEGER`, `numeric(14,2)` is `DECIMAL(14, 2)`, `varchar(n)` and `nvarchar(n)` are `VARCHAR(n)`, and text with no declared length (`varchar(max)`, `nvarchar(max)`, `text`, `ntext`, an unconstrained PostgreSQL `varchar`) is a bare `VARCHAR`), *widened* (`tinyint` is `SMALLINT`, `money` is `DECIMAL(19, 4)`, `char(n)` is `VARCHAR(n)`, `binary(n)` is `BLOB`, `datetime` is `TIMESTAMP`) and *lossy* (`datetime2(7)`, which keeps one digit more than `TIMESTAMP`; `xml`, `json` and `jsonb`, which are read as text). Non-exact fits are printed per column and appear in the JSON. A column with **no representation** (`geography`, `interval`, arrays, user types) is left out of a generated descriptor and reported (DDB-226): a guessed type would flow into every model that selects the column. A committed type that means the same as the live one (`INT` for `INTEGER`, or a length a person put on unlimited text) and a column the catalog cannot type but the descriptor declares are kept as written. **Indexes and foreign keys** are exported too, as `indexes:` (name, columns, `unique`, `include`) and `foreign_keys:` (name, columns, `references: {table, columns}`): they are facts about the table that a rule or an advisor can read, and the tool never creates them. The index that backs the primary key is the grain and is not listed; an index on an expression, and an index or foreign key that names a column with no logical type, are left out with a note. The live table wins for both (they are exports, and any difference is a `changed` descriptor). Collation is not exported: a native collation name has no honest logical name, because the logical names (`default`, `exact`) are the project's own profile. The live table wins for columns, types and nullability (a descriptor is an export); a committed `grain` is kept and a primary key only seeds a new descriptor's grain. Output is a diff per descriptor, `new`, `changed`, `unchanged`, `stale` (a descriptor for a table the target does not show: DDB-228, never deleted) or `invalid` (a file that does not load is never overwritten). Without `--write` nothing is written; `--write` writes through `DefinitionFile` (hash-checked, atomic); `--check` writes nothing and fails with DDB-227 per descriptor that differs, for CI. A table that is a model, the tracking schema name, and names that cannot be a path are skipped and listed. Descriptors are generated with fixed key order and no comments, so a hand-written comment in a refreshed file is lost; use the diff before `--write`.

**Inference** (offline). For each model the query's tables are resolved against models and sources (DDB-218 if one is missing), empty in-memory DuckDB tables are built from their declared columns, and DuckDB *describes* the query (it is never run; external access is disabled first, so a query cannot read files or the network while it is bound). Lineage and nullability come from polyglot's `analyze_query` with the same schema. Type proposals: a column passed straight through keeps its upstream declared type, including a `VARCHAR` length, which DuckDB alone cannot report; a written `CAST(x AS VARCHAR(n))` states its length; otherwise DuckDB's type is mapped through an enumerated table (`LogicalTypes`). Clean, target-neutral types (`BIGINT`, `INTEGER`, `SMALLINT`, `DOUBLE`, `BOOLEAN`, `DATE`, `TIMESTAMP`, `TIME`, `DECIMAL(p, s)`) are high certainty; types lossy for some target (`TINYINT`, `FLOAT`, unsigned integers, `TIMESTAMP WITH TIME ZONE`, `BLOB`, `UUID`) are normal certainty; `HUGEINT`, `INTERVAL`, `JSON` and nested types get no proposal and are asked. **Unlimited text stays unlimited:** a bare `VARCHAR` (no length declared upstream, none written as `CAST(x AS VARCHAR(n))`) is proposed as `VARCHAR`, high certainty, and maps to `nvarchar(max)` on SQL Server, `varchar(max)` on Fabric and `text` on PostgreSQL; the tool never invents a length, and whether a project allows unlimited output columns is a lint for the project (a unit test over the metadata), not a rule of the tool. Nullability is proposed from lineage (`non_null`, `nullable`) and asked when lineage cannot tell. An expression without an alias, or a duplicate column name, is an error (DDB-220).

**Questions** (ids are `Q-define-<model>-<field>`, with `columns.<column>.type|nullable|remove` for columns and `Q-rename-<model>.<column>` for renames; unusual characters in names become `_u<hex>_`). They come in rounds because later questions depend on earlier answers: for a new definition, round 1 is `name`, `kind`, `targets` and every column's `type` and `nullable`, and round 2 (once the kind is answered) is `grain` and `unique_key`, or `time_column` and `lookback`. In non-interactive mode the open questions of the round reached are listed at once, with a note that more follow. Each question's escape hatch is `skip_model`. Grain candidates come from GROUP BY columns that appear in the output, SELECT DISTINCT, and an upstream key that passes through a one-to-one query; time column candidates are the DATE and TIMESTAMP outputs.

**Existing definitions.** Only `columns` (and `renames`) are touched, by minimal text splices located with the YAML parser's offsets (block and flow item styles, CRLF, and files without a final newline are handled; a `columns` list written as a flow list is reported, DDB-422, not rewritten). Columns match by name, case-insensitively, and declared order is not compared. A difference is a declared column the query no longer returns, a returned column that is not declared, or a type that is not equivalent (synonyms match, a bare DuckDB `VARCHAR` matches any `VARCHAR(n)`). A removed declared column and an added one of the same type at the same ordinal position are asked first as a possible rename; a rename updates the column, records `renames:`, and carries its references in `grain`, `unique_key` and `time_column` (the question shows the old and new values). **Decision:** declared nullability on an existing column is human knowledge: a declared NOT NULL over a column that lineage cannot prove non-null (a LEFT JOIN that always matches, for example) is a note, never a difference, or `--check` would fail forever. Lineage proposes nullability only for new columns. A person may answer `keep_declared`; that is recorded in the output and `--check` keeps reporting it.

**Writing.** Nothing is written before the diff is shown, and (interactively) confirmed. Writes are **all or nothing across models**: if any selected model still needs attention, nothing is written. Each file is written through `DefinitionFile`, which accepts only `.yml` paths, checks the file's hash against what was read (twice, the second time immediately before a rename), writes a temp file and renames it; a changed file is refused (DDB-421). After writing, every query file is re-hashed against what was read. After editing, the new text is loaded and compared with the inferred columns: anything still different must be exactly what the person chose to keep, otherwise `define` stops with an internal error and writes nothing.

**Modes.** `--check` asks nothing and writes nothing, and fails (DDB-420 per difference, with the definition line) if a definition is missing or out of sync; it cannot be combined with `--write`, `--answers` or `--accept-inferred`. `--write` is non-interactive and needs `--answers` for any open question. Without either, `define` is interactive and refuses to run without a terminal. `--accept-inferred` accepts only high-certainty proposals and each acceptance is printed with the diff. Models are processed in dependency order, so a model sees the columns an upstream model has just been given; a model whose upstream is not defined is skipped with a note, and a dependency cycle is DDB-221.

### 6.5.2 Copies: rows from one connection to another (as built)

`kind: {type: copy, from: schema_name.table_name}` is a table filled with the rows of another model that lives on **another connection**, which is how data moves between connections (a query always runs on one connection; the tool
never joins across two, and uses no linked server and no DuckDB in the middle). It has no `.sql` and no `columns`: the columns (and the grain, unless it names its own) are the origin's. `from` is a model, a mapped model or
another copy of the project; it is on one connection, or on several that each hold the same table (fan-in, with a `slice`, below), and the copy must be on others. It is always persisted, with the `full_replace` strategy, `indexes`, `hooks` and drift as
for any table. `connections` places it like any model; `validate` and `render` need no database.

How it runs. The copy is an ordinary load over a **staging table** on its own connection (`<tracking schema name>.stg_<schema name>__<table>`, shortened with a hash past 60 bytes, so deterministic): its generated query is
`SELECT <the columns> FROM <staging table>`, and the staging table is declared to the project as a generated mapped table, so lowering, linting, rendering and the matrix treat it like any model. `plan` adds, after the table's own
steps, a **transfer** step (its text drops and creates the staging table from the declared columns; its `transfer:` block names the origin connection, the exact read in the origin's dialect with every column quoted, and the
columns with their declared types), then the load step from the render, then a drop of the staging table. `apply` runs the transfer with the origin's read login (`DBDATABUILD_<ORIGIN>_READ`, checked, and the connection opened,
before the first statement of the plan): a streaming read of the one SELECT, each value converted **by the declared logical type** (a value that does not fit is an error naming the column, never printing the value), written
to the staging table by the engine's bulk route through the gate (`GateStatement.BulkCopy`: SQL Server's bulk copy in batches, PostgreSQL's binary `COPY`), and logged in `run_log` as operation `transfer` with a row count and no
values. The load then replaces the table in one destination transaction. A transfer that failed is run again from the start (the staging table is recreated); the load step never ran, so the table is untouched.
Dates at the engines' limits (0001-01-01, 9999-12-31) are copied as dates, not as infinity (the driver setting is `DriverSettings`).

**Fan-in: one application on several connections.** A mapped model may be on several connections (`connections=: [store_017, store_018]`; a folder's `_dbdatabuild.yml` can set it for everything beneath), and a copy
of it then needs a `slice`, which says which rows are an origin's:

```yaml
# dbdatabuild.yml
connections:
  store_017: { engine: postgres, parameters: { store_id: "017" } }     # a connection's parameters: what differs between the systems
  store_018: { engine: postgres, parameters: { store_id: "018" } }
# models/warehouse/orders.yml
name: warehouse.orders
kind:
  type: copy
  from: pos.orders                       # a mapped model on store_017 and store_018
  slice: { column: store_id, value: "${origin.store_id}", type: "VARCHAR(10)" }
  on_mismatch: fail                      # or skip
```

`${origin.name}` is the parameter of the connection a row came from (`${connection.name}` that of the destination); any other scope is refused when the project loads, and a connection without the parameter is named. If the origin has no
column called as the slice, the copy adds it (the `type` is then required) and writes the value into every row of that origin; if it has one, every row must already hold the value (an origin cannot write into another's slice). The
slice column joins the grain, and the copy's load becomes `delete_insert_by_key` on it, so **a run replaces only the rows of the origin it loads**: rows of other origins, or of no origin, are not touched. The plan has one transfer
and one load per origin, in the order of the `connections` list, and a staging table that is dropped at the end. `plan` compares each origin's live table with the mapped model (a column gone, a type changed, a column that
can now be NULL where the declaration says it cannot; an added column is no difference) and names every origin that differs (DDB-230): `on_mismatch: fail` (the default) stops the plan, `skip` leaves that origin's steps out with a
warning. An origin whose login is not in the environment is reported as not checked, and `apply` refuses to start unless every origin can be opened.

**Incremental copies.** `unique_key: [id]` and `watermark: {column: updated_at, lookback: 3 days}` under `kind` make a copy read only what changed:
the origin read gains `WHERE <column> >= @watermark`, the bound being the newest value of that column the destination holds (for the origin's own rows when there is a slice) less the lookback (days, weeks, months
for a DATE; any unit for a TIMESTAMP), computed at plan time and recorded in the plan's transfer step (`watermark: {column, type, value}`; the value is bound by the driver, never in the text). With nothing in the
destination yet the bound is absent and everything is read. The rows read replace the rows with the same `unique_key` (`delete_insert_by_key`; with a slice the key gains the slice column); **rows deleted at the origin are
not deleted in the destination** (an incremental copy cannot see them: plan a full copy, a copy with no `watermark`, to reconcile). `plan --full-refresh <model>` reads such a copy from the start once (the merge by `unique_key` makes that safe; it still does not see rows deleted at the origin); for a model every load of which rebuilds it in full it is accepted with a note (nothing to refresh), and an incremental model is pointed at `--backfill`. `report` lists each origin's last good run and its latest attempt.

Not built: `columns` selection or a row filter (do it with a model on the origin, then copy that), copying types the logical types do not cover, rows deleted at the origin in an incremental copy.

### 6.5.3 Parameters (as built)

One concept, four scopes in files, a fifth at run time. A reference carries its scope, so nothing shadows anything: `${project.name}`, `${connection.name}`, `${origin.name}` (a copy's slice only), `${model.name}`;
operation parameters (`@name`, section 6.6) are bound at run time and are a different mechanism. A value is a single value (text) or `{type, value}` with a type of VARCHAR, BIGINT, INTEGER, SMALLINT, DATE or TIMESTAMP.

- **Where they are declared.** Project parameters: `parameters:` in `dbdatabuild.yml`; a folder's `_dbdatabuild.yml` has its own `parameters:` that **overrides** them for everything beneath it (nearest wins), and
  `connections.<name>.parameters:` there overrides a connection's (a folder file never declares a connection: the connections exist once, in the root file). Connection parameters: `connections.<name>.parameters:`.
  Model parameters: `parameters:` in the model's own file, and only there (`defaults:` refuses them: they are never inherited).
- **In configuration** (a copy's slice value) a reference is substituted when the project loads; a missing parameter is named, with the file it should be in.
- **In a model's query, as a value** (`WHERE region = ${project.region}`; not in a view, which is DDL and binds nothing). DuckDB must still bind and lower the query offline, so each reference is first a **marker**:
  a literal of the parameter's type (`'__ddb_param_project_region__'`, a large integer, `DATE '1000-01-02'`); the text, its hash and the rendered files therefore never depend on a value. After lowering and transpiling,
  each marker in the target's text becomes a placeholder `@p_<scope>_<name>`, declared in the script's parameters (source `parameter`); the lowered artifact shows the references again. `plan` fills each placeholder
  with the value on the plan's connection (a connection parameter can differ per connection) and `apply` binds it with its type through the driver, so **the value is never in a statement's text** and a value change
  is a new plan, not a new rendered file; the values of a run are in `run_log`. `sample`, `test` and DuckDB runs use the real values as literals. An undefined reference, a reference in a view, an
  `origin` reference in a query, differing types of one `${connection.x}` across the model's connections, and a query that already contains a marker are refused when the project is checked.
- **Not built**: a parameter that changes a schema name or table **name written in a query** (a name reaches a query only as the string a macro receives: type `NAME`, 6.5.5), and parameters of other types (boolean, decimal, double).

### 6.5.7 A query file's head (as built; design note: `docs/research/model-naming.md`)

A query file may start with a **head** that says what it builds, so a reader of the `.sql` file does not have to guess what a "model" is:

```sql
CREATE TABLE marts.fct_orders
WITH (kind = 'incremental_by_unique_key', unique_key = (order_id))
AS
SELECT ...
```

`CREATE VIEW schema_name.object_name AS SELECT ...` is a view; `CREATE TABLE schema_name.object_name AS SELECT ...` is a table, a full replace unless the definition file says another kind. The grammar is `CREATE (TABLE | VIEW) name
[WITH (key = value, ...)] AS query`: a name is `schema_name.object_name` (words or "quoted names"), a key is a word, a value is a word, a 'string', a number or a (list) of those, keywords and keys are not case-sensitive, and comments may come
first. The options are the reload options of a kind: `kind` (`full`, `incremental_by_unique_key`, `incremental_by_time_range`), `unique_key`, `time_column`, `lookback` (`'3 days'`); a view takes none. Everything else about the model
(columns, grain, indexes, hooks, connections, parameters, `loads:`) stays in its definition file. A file whose first word is not `CREATE` has no head and is only a query, as before.

The head is parsed by the tool itself (a small hand-written reader: nothing in DuckDB or the SQL parser knows `WITH (kind = ...)` for a model), so its errors name a line and a column (DDB-238), and the query after `AS` is passed
on exactly as written (its hash, its lowering and its line numbers are those of the query). The head is a settings layer of the model, merged like the others: its name and kind replace what a folder's `defaults:` says (`kind=`),
the definition file may leave out `name:` and `kind:`, and **the kind is said in one place**: `CREATE VIEW` or a `kind` in `WITH` together with a `kind:` in the definition file is an error, as is a `name:` that differs from the head's, or
`CREATE TABLE` over a definition that says `kind: view`. A plain `CREATE TABLE` leaves a kind that the definition file gives alone. `model_layout` applies to the name the head gives. `define` takes the name, the kind and the
options from the head (it does not ask for them) and writes a definition without `name:` and `kind:`. Not built: options other than the four (the multi-operation `loads:` stays in YAML), heads for native models and copies,
and a per-connection schema name (parked).

### 6.5.6 Where a model's name comes from (as built; design note: `docs/research/model-naming.md`)

**Terms.** A *schema name* is the namespace an object lives in (`marts` in `marts.fct_orders`); a *schema* is only the defined shape of a table or view (its columns, types and other metadata). The word is overloaded among DBAs, so this project says
"schema name" for the namespace and keeps "schema" for the shape, in documents and in discussion (swept on 2026-10-06: documents, diagnostics, command descriptions, JSON Schema descriptions and the C# members that hold a schema name are `SchemaName`; still named `schema` are the config key `tracking: { schema: ... }`, the `tracking_schema` key of the `init` and metadata documents, the option `diff --against-schema`, and local variables and parameters in the code; the keys are listed in `OPEN-ITEMS.md` section Q).

**Length.** A name (schema name, object name, column, index) longer than an engine keeps is DDB-241, for each connection the model is built on: 63 bytes on PostgreSQL, which would otherwise cut it without a word and build a table that never matches its declaration, and 128 characters on SQL Server and Fabric (entry 110).

A model's name is the `name:` of its definition file, whatever its path (`schema name` + `.` + `object name`: the schema name is the part before the last dot). `model_layout` in `dbdatabuild.yml` says whether the files must spell the name:
`folder` (the default, as before: `models/<schema name>/<object>.yml`, any depth, the path is the name; mismatch is DDB-107), `dotted` (`<schema name>.<object>.yml` in any folder), `object` (`<object>.yml` in any folder) or
`none` (no check). The query is always the file beside the definition (same stem, `.sql`; `.native.sql` for a native model). Two files that claim one name are an error. `define` and `import` follow the layout for a
file that does not exist yet and the definition's own file for one that does; the graph and the metadata show each mapped model's real file. Declaring the object in the query file is 6.5.7; not built (parked): a per-connection schema name.

### 6.5.5 Macros and types (as built; design: this section)

A project's `macros/` folder holds `.sql` files of DuckDB `CREATE MACRO` (scalar, or `AS TABLE`), `CREATE FUNCTION` (DuckDB's other name for it) and `CREATE TYPE` statements, several to a file, separated by `;`.
Nothing else may be in them (a table, a setting, an extension, a query is refused), so a macro file has no effect of its own. DuckDB expands a macro while it binds a query, so everything downstream of the
lowering (the support matrix, the transpile, the rendered files, the hashes) sees only what the macro expanded to; **a macro is never run by an engine**. Macros need lowering (they are refused with it off).

- **A name given as an argument becomes the query for that table**: `query_table(tbl)` takes a table (or `schema_name.table_name`) as a constant string, `COLUMNS(lambda c: c = col)` takes a column name the same way.
  A name that does not exist is a bind error, so `validate` finds it offline. Both only accept constants (a literal, or an expression of literals); a name read from a column is refused by DuckDB.
- **One macro for several tables of the same shape**: a live table and its snapshot are the same macro given a column name (`snapshot_at('snap.orders', 'snap_date')`) or `NULL` (`snapshot_at('live.orders', NULL)`);
  the macro writes `CASE WHEN col IS NULL THEN current_date ELSE COLUMNS(lambda c: c = coalesce(col, '__none')) END AS as_of_date`. The lowerer **folds a CASE whose conditions are constants** to the branch that is
  taken, and drops a GROUP BY key that no column determines (T-SQL refuses one; if it was the only key, `HAVING count(*) > 0` keeps what GROUP BY did), so each model's SQL is for its table, with no branch
  and no UNION left for the engine to choose. DuckDB cannot overload a macro by the kind of its argument, nor switch the structure of a query on it; the constants in an expression are what can be switched.
- **Loaded on demand, callees first**: DuckDB binds the names in a macro (a table, a type, another macro) when it creates it, so creating every macro up front would fail wherever one names something that is
  not declared. A binding is given only the macros the queries it binds reach, with what they call, types first, then the declared tables, then the macros. A macro that takes its table as a parameter needs no table
  to exist. A recursive macro, or a circle of them, is refused (DuckDB could not create it). `validate` creates each macro on its own against the declared tables and warns about one DuckDB refuses; a model
  that does not reach it is unaffected.
- **Dependencies**: a model that calls a macro depends on the macro (a `name()` node of kind `macro` in `graph` and `metadata`'s `upstream`, with macro to macro edges) and on the tables of the bound plan, which are
  there once DuckDB has expanded it (so a table that is only an argument is a dependency of the model, and is checked for connections like any other). The model's definition hash includes the text of the macros it
  reaches, so changing a macro changes what every model that reaches it is planned and blocked on. `define`, `sample`, `test` and the planner use the same tables and macros.
- **Default parameters and named arguments** are DuckDB's own and pass straight through (the tool reads a macro's head and body only for its name and the macros it calls): `CREATE MACRO snapshot_at(tbl, col := NULL, lag_days := 0)`
  may be called `snapshot_at('src.orders')`, `snapshot_at('src.orders_snap', col := 'snap_date')` or with the arguments named in any order, also inside another macro (`snapshot_at(tbl, col := col)`). The expanded query,
  the graph, the column lineage, `define`, `sample` and the hash (the default is part of the macro's text) all see the call as it expanded. Verified through `render`, `validate`, `graph`, `define` and `sample`.
- **Names as parameters** (type `NAME`): a parameter `{ type: NAME, value: src.orders_snap }` (any scope; `value: ""` is `NULL`) is **not a value to bind**: before DuckDB binds the query,
  `${connection.table}` is replaced by the string `'src.orders_snap'` (`NULL` for an empty one), so `snapshot_at(${connection.table}, ${connection.date_col})` is the call for that connection's table and column.
  A name is dotted identifiers only (validated; nothing that could be more than a name), a parameter is a name on every connection of the model or on none (and has a value on each), and a view may use one
  (a name is not bound). When a model's names differ between its connections, each distinct query is lowered on its own and written to `rendered/lowered/<model>/lowered.<first connection that reads it>.sql`
  (one `lowered.sql` when they agree); the plan for one connection and `render` of all produce the same files; `validate`, the lint and the graph (the union of the tables its names can point at) see every variant,
  and a table is checked against the connections that read it through their own name only (DDB-231). Changing a name changes the model's definition hash on that connection.
- **Column lineage** goes through a macro: for a query that calls one, `graph --column`, the metadata and `define` read lineage and nullability from the lowered query (plain SQL on real tables), so a column is followed to the table column it was given.
- **Types** (`CREATE TYPE`) load in the same place, before the tables. They are DuckDB's, for macros and for readability. An alias (`CREATE TYPE money AS DECIMAL(12, 2)`) is replaced by the type it stands for while the query is bound, so an engine receives `DECIMAL(12, 2)`. The lowerer does not map an enum to an engine type: **a cast to or from an `ENUM` is refused (DDB-324)**, where it used to be left in the lowered query as `CAST(x AS ENUM)` with a DDB-305 warning about a type `custom` (2026-10-12, entry 144). A DATE that DuckDB folded while it expanded a macro (the default of a parameter, `live_as_of := DATE '2024-06-30'`) is written as a date literal; it used to reach the lowered query as `CAST(19904 AS DATE)`, days since 1970, which DuckDB and PostgreSQL refuse and SQL Server reads as 1954.

### 6.5.4 Native models and local copies (as built; design: `docs/research/native-queries.md`)

A **native** model, `kind: {type: native, access: select, query: ...}` (or the text in a `.native.sql` file beside the definition, never both), is a table the **engine computes from its own query**: a
table-valued function, `STRING_SPLIT`, `unnest`, an engine-native select. It declares its columns like a mapped model, so DuckDB binds queries against them offline as for any table, and lives on exactly one
connection (the text is that engine's dialect, never lowered or transpiled). The text must be a single SELECT (the read guard, now in `Core`); a T-SQL text may not start with WITH.

- **Inlined.** A model on the same connection that reads it gets the native text spliced in **after transpiling** as a derived table (`FROM (<text>) AS [nums]`, an existing alias kept), in the load script and in
  a view's DDL. The reading query's own text and hash are unchanged. A table on another connection cannot read it (DDB-231).
- **Parameters** in the text (`${project.x}`, `${connection.x}`, `${model.x}` of the native model itself) become placeholders of their own (`@p_native_<model>__<scope>_<name>`), declared in the reader's script,
  filled by the plan and bound at apply. A **view** cannot read a native model whose text has parameters (DDL binds nothing); the plan says so.
- **As a copy origin** the native text becomes the origin read (`SELECT cols FROM (<text>) AS t`), its parameters carried in the transfer step (`transfer.parameters`) and bound on the origin.
- **Local copies**: a copy whose connections are all the origin's (any model, mapped or native) is an ordinary full-replace load over `SELECT <columns> FROM <origin>`: no staging, no transfer. This is how a
  native select becomes a real table with indexes and drift on its own connection. Mixed (some connections the origin's, some not) is refused; `slice`, `unique_key` and `watermark` are refused on a local copy.
- **Commands** (`access: command`, `query: EXEC dbo.proc @x = ${project.x}` or `CALL proc(...)` or `SELECT * FROM fn(...)`): a call that returns rows, run and never read. Only a connection with
  `connections.<name>.allow_native_commands: true` may have one (the read login's permissions are what keep it read-only; PostgreSQL's read session is also read-only). The text is one call (starts with EXEC,
  EXECUTE, CALL or SELECT, no `;`). A query cannot read it, even on its own connection (DDB-231: copy it). It is only a **copy origin**, and always a **transfer** (staging table, bulk copy), even when the
  origin is the destination's connection, which is its local-copy form: the call's first result set lands in the model's table, matched to the declared columns by name (extra columns ignored). The call
  runs on the origin with the read login **inside a transaction that is rolled back**, its parameters bound. The step is **risky** (`--allow-risky`, also for a dry run, which does not run it); `apply`
  refuses a plan whose origin connection does not allow commands. An incremental copy of a command must pass the bound itself (`@watermark` in the text).
- **`reads:`** (optional list of the models or mapped tables the text reads) is how an opaque native text takes part in the graph: `graph` and its selectors (`+model`), `metadata`'s `metadata_upstream` and
  `metadata_ancestors`, and the build order (a model reading a native select is built after the models in its `reads:`). It is not checked against the text. A native model without it gets a note (DDB-233).
- **Plan-time describe**: `plan` asks the engine for the result shape of each native **select** it reads or copies (the driver's schema-only mode: nothing runs) and compares column names and types with the
  declaration (DDB-230; a copy's `on_mismatch: skip` applies to a native origin too). Nullability is not compared (an engine calls every computed column nullable, so NOT NULL stays an assertion). A command
  is not described; a text the engine cannot describe is a warning that it was not checked.
- **Metadata**: a native model is a `source` document with a `native` block (`access`, `connections`, `text_hash`, `reads`); its `definition_hash` covers the text; an upstream entry of a model that reads one has kind
  `native`; `graph` nodes have kind `native` and show the `reads:` behind it.
- **`track_definition: [dbo.fn_open_orders, public.fn_rates(date)]`** names the routines the text depends on (an explicit list; the text is never parsed for names). Each `apply` that uses the native model records
  a hash of their live definitions (`OBJECT_DEFINITION` on SQL Server, `pg_get_functiondef` on PostgreSQL; line endings normalised) in the tracking tables, as a `native_definition` document per native model and
  connection (`metadata_document`, no layout change); each `plan` compares the live definitions with the last record. A change is **DDB-234, a warning** (`policy.severity.native_definition_changed: error`
  stops the plan). The first plan has nothing to compare with and says nothing. Without tracking, or when the engine returns no definition (no such routine, several of that name, no permission), the routine
  is reported as not checked (DDB-235, a note) and nothing is recorded for it. Read-only: one catalog query per routine on the read login.
- **Not built**: change feeds with deletes (backlog, `OPEN-ITEMS.md` L).

### 6.5.8 Title, description, tags and properties (as built; entry 145)

The project, each connection, each model (built, mapped or native), each column and each index can say what they are in words, with four keys of the same names everywhere. **Nothing here changes what is built**: no rendered file, plan, shape hash or statement depends on it, and nothing is written into a deployed SQL object (no `COMMENT ON`, no extended property; that stays a later decision, `OPEN-ITEMS.md` T).

| Key | Value | Limit | Inherited |
|---|---|---|---|
| `title` | a short display name, one line | 200 characters | no |
| `description` | free text (a quoted or block scalar) | 4000 characters | no |
| `tags` | a list of words: letters, digits, `_`, `-`, `.`, `:`; starting with a letter, digit or `_` | 128 characters each | models and mapped or native models, from `defaults:` above (below) |
| `properties` | a mapping of key to one value | key 128 characters (a tag's form, starting with a letter or `_` and not ending in `-`, so `key-` and `key=` keep their merge meaning), value 4000 | the same |

Where: `dbdatabuild.yml` has them at the top level for the project (`defaults:` is the model settings and a different thing) and in each `connections.<name>` entry; a model's definition has them at the top level, in each column and in each index; a mapped or native model, its columns and its indexes likewise (`foreign_keys` and the tests under `tests/` have none: a test's `description` and `tags` are comments in its own file, DESIGN.md 9.8). A value past a limit, a key or tag of the wrong shape is **DDB-243**; a value of the wrong kind (a list where a word is expected, an empty or `null` property) is DDB-106.

**Property values** are text, a number or a boolean, read from how they are written and never from a declared type: `true` and `false` (lowercase, unquoted) are booleans; unquoted plain digits (`-12`, `0.5`; no exponent, no leading zero, no plus sign) are numbers; everything else, and anything in quotes, is text (`"007"`, `"1"` and `1e3` are text). A property is held as its text with a type tag beside it (`number`, `boolean` or `text`), so the digits written are the digits kept, a value compares as its text whatever its type (`property:tier=1` selects `tier: 1` and `tier: "1"` alike), and a rewrite of a file quotes a text that looks like a number or a boolean so the type survives. In the metadata documents a property is a JSON string, number (the digits as written) or boolean. A list or a mapping as a value is refused: a property is one fact; use `tags` for several words.

**Layering.** `tags` and `properties` of a model are layered keys (9.4.1) with the rules `tags` already had: the project's `defaults:`, then each folder's `_dbdatabuild.yml` from `models/` down, then the model's own file, the nearest winning. A mapping merges by key (the nearer value replaces the farther for the same key), `properties=: {..}` replaces everything inherited, `properties-: [key, ...]` removes keys; for `tags` a list appends, `tags=` replaces and `tags-` removes. `title` and `description` are not layered: they describe the object whose file writes them, and either in a `defaults:` section is an unknown key (DDB-104). A connection's, a column's, an index's and the project's own words are not inherited from anything. A diagnostic about a value that came from a folder file names that file.

**Selector.** `property:key=value` selects the models whose merged `properties` have that key with that value as text (a comma ends a selector term, so a value with a comma cannot be selected); `property:key` selects the models that have the key. `tag:` as before.

**Where it goes.** The metadata documents carry them, leaving out what is empty (so a project that says nothing has the documents it had, byte for byte): `title`, `description`, `tags` (sorted) and `properties` (sorted by key) on the project document and on each connection in `config.connections`, on the model and source documents, and on each of their `columns` and `indexes` (`schemas/metadata.schema.json`). `connection publish` and the deploy's `metadata.store_on_apply` store the documents as before; **tracking layout 6** adds the table `object_info` and `publish` also writes what each stored document says as rows: for one subject (kind `project`, `model` or `source`, and its name) and one element (`''` for the object itself, `column:<name>`, `index:<name>` or `connection:<name>`), an `item` (`title`, `description`, `tag`, `property`), an `item_key` (the tag, or the property key) and for a property its `value_type` and `value` as text. The rows of a subject are replaced when its document changes (the old rows first, the document after, so an interrupted publish is repeated whole by the next one), and a subject that stops saying anything has none left. They can be queried with plain SQL (`SELECT ... FROM dbdatabuild.object_info WHERE item = 'property' AND item_key = 'owner'`) without opening the JSON.

**Maintained by the tools.** `project model update` splices what it changes and leaves other keys and comments as they are, so words written in a model survive it; `project import --refresh` rewrites a mapped model's file from the live table and keeps what the committed file said about the table, its columns and its indexes (including, for a model in a folder with `defaults:` tags or properties, the merged result written into the file: the same meaning, one more place to edit). The definition hash of a source does not include any of it.

**Not built.** Writing them into the database (`COMMENT ON`, extended properties); the same four on foreign keys, tests, hooks, loads and parameters; a `property:` selector on connections; a list as a property value.

### 6.6 Load operations: paired with targets, committed, parameterized

A **load operation** is a named way to load one model on one target. A model can have several operations per target (for example a routine watermark load, a period reload, and a keyed merge). The SQL for every operation is **rendered to disk and committed**. At execution time the only unresolved things are the operation's declared runtime parameters.

**Declaration**

```yaml
# models/marts/fct_orders.yml (excerpt)
kind:
  type: incremental_by_time_range
  time_column: order_date
# ... name, grain, targets as before; columns include order_id, order_date (DATE) and modified_at (TIMESTAMP)
loads:
  daily:
    default: true
    strategy: watermark_append
    watermark:
      column: modified_at
      resolver: target_max        # how the value is determined; rendered and committed
      lookback: 3 days
      on_null: require_param      # an empty target never silently means "load everything"
  reload_period:
    strategy: delete_insert_by_range
    params:
      start: DATE
      end: DATE
    max_span: 400 days
  by_key:
    strategy: merge_by_key
    key: [order_id]              # defaults to kind.unique_key, which this kind does not have
    connections: [sqlserver]
```

**Pairing rules**

- An operation's identity is `(model, target, operation name)`. `connections: [...]` restricts an operation; without it the operation applies to every target the model declares.
- Every declared pair must render, or fail with a diagnostic (for example, the strategy is `unsupported` on that target). There are no silent gaps. `dbdatabuild project show loads` prints the model-by-target-by-operation table with each pair's matrix status.
- At most one operation per `(model, target)` is marked `default`. `dbdatabuild connection refresh` without `--op` uses it. With none marked, `--op` is required.
- If a model declares no `loads`, its `kind` supplies a single operation named `default`. It is still rendered and committed explicitly, and `dbdatabuild project model update` asks about it during the walkthrough.
- Strategies are a closed library: `watermark_append`, `delete_insert_by_range`, `delete_insert_by_key`, `merge_by_key`, `full_replace`. Each strategy has a per-target implementation behind `ILoader`, matrix rows (for example, `merge_by_key` is `emulated` on Fabric **[VERIFY]**), and a **DuckDB reference implementation** used as the test oracle (section 15.5).

**Committed artifacts**

```
rendered/
  sqlserver/
    marts.fct_orders/
      load.daily.sql               # value parameter: @watermark
      load.daily.resolve.sql       # resolver: the committed query that determines @watermark
      load.reload_period.sql       # value parameters: @start, @end
      load.by_key.sql
      manifest.yml                 # operations, parameters, sources, hashes, matrix status
  postgres/
    marts.fct_orders/ ...
```

- Files are generated by `dbdatabuild project compile`. Output is deterministic with **no timestamps**. Each file begins with a comment holding the model, operation, target, strategy and definition hash, the parameters, and the target rules that fired. It holds no tool version and no matrix version: a rendered file is a function of the model, the project configuration and the translation rules, so it changes when its text changes and not when the tool is upgraded (a version in the header made every file differ, with the same SQL, after every release, and failed `render --check` in every project). The matrix status that applies to the model is in the manifest, per operation.
- `dbdatabuild project compile --check` (for CI) fails if the committed files differ from a fresh render, and writes nothing.
- The rendered files cover **load operations and resolvers only**. Create/alter DDL depends on target state, so its text lives in committed plans (section 10.5).
- Each rendered T-SQL file is parsed by ScriptDOM as part of validation. **[VERIFY]** handling of parameterized scripts, and equivalent checks for other targets.

**Parameters and placeholders**

Each operation declares its parameters: name, logical type, source (`runtime` for user-supplied, or `resolver`), and constraints (type, range, `max_span`). Only **value parameters** (`@name`) may appear as placeholders in a rendered file. They are bound through the database driver and never interpolated into the text, so the executed SQL text is **exactly** the committed text. A lint rejects any other placeholder.

**Object names are fixed.** Schema names and object names in rendered files are exactly the model's declared names. Which environment a run targets is determined by the target's connection (server and database), not by the SQL text. A value such as an environment name may still be passed as a bound parameter (for example, for logging), but it cannot change identifiers. Making schema names and object names parameters is deliberately **out of scope** for this design and will be revisited (section 3).

**How a watermark is determined** is written down and committed, never implied:

- The resolver is a committed SQL file: a single `SELECT` returning one row and one column of the parameter's declared type. `target_max` produces a query such as `SELECT MAX(modified_at) FROM <target table>`, less the declared lookback.
- It executes at plan time under the read-only login. The plan shows the result and the resolver's hash, and `apply` binds that value.
- **`apply` re-runs the resolver and compares it with the plan.** If the value moved between plan and apply, the plan is stale and `apply` refuses, consistent with the stale-plan rule.
- A NULL result is handled by the declared `on_null`: `require_param` (a question, or `--param` in an answers file) or a declared, committed initial literal. Nothing defaults to "from the beginning of time".
- A resolver may be `overridable`. An explicit `--param watermark=...` is then recorded in the plan as an override.

**Runtime parameter handling**

- Runtime parameters without a value become questions (`Q-param-<model>-<op>-<param>`) using the same question and answers-file mechanism. Constraints such as `max_span` are enforced at plan time.
- **All parameter values and resolver results are logged**: in the target's `run_log`, in plans shown or written by the tool, and in the tool's logs. This is a deliberate, accepted choice for now. Resolvers are expected to return watermark-like values (timestamps, dates, sequence keys); a resolver that returns a business measure needs review. Redaction can be scoped and added later if a need appears.
- Plans containing load steps (and therefore resolved values) are routine artifacts and are **not committed to the repo**. Only DDL plans for shared targets are committed (section 10.5). This also keeps resolved values away from the schema-only development environment.

**What a plan shows for a load step:** the operation name, the committed file path and hash, the resolver file and hash, the parameter values (on screen), and the full resolved script. `apply` verifies the committed file's hash against the plan before executing.

### 6.6.1 How render and loads work (as built)

**Declaring operations.** The `loads:` block is validated by the loader and by `schemas/model.schema.json`. Keys: `default`, `strategy`, `targets`, and per strategy `key` (the key strategies; defaults to the kind's `unique_key`), `column` and `params` and `max_span` (`delete_insert_by_range`; the column defaults to the kind's `time_column`, `params` to the column's own type), and `watermark` (`watermark_append`: `column`, `resolver: target_max`, `lookback`, `on_null: require_param | initial`, `initial`, `overridable`). Durations are `<n> minute|hour|day|week|month` and must fit the column (a DATE takes days, weeks and months). The watermark column and range column must be DATE, TIMESTAMP or an integer type, and an `initial` literal must be valid for it. A view takes no loads. At most one operation is the default for a target.

**What a kind supplies when `loads:` is absent.** `view`: nothing (DDL only). `full`: `full_replace`. `incremental_by_unique_key`: `delete_insert_by_key` on the unique key (a `merge_by_key` operation can be declared; `delete_insert_by_key` is the default because `MERGE` has concurrency pitfalls on SQL Server and is new in PostgreSQL 15). `incremental_by_time_range`: `watermark_append` on the time column with the kind's `lookback` and `on_null: require_param`. Declared operations replace the implicit one.

**Decision: lookback on `watermark_append`.** The example in 6.6 pairs `watermark_append` with a `lookback`, but appending the rows of a lookback window would duplicate them. So with a `lookback` the window from the watermark is *replaced* (rows at or after the watermark are deleted from the target, and the query's rows at or after it are inserted); without one, only rows strictly newer than the watermark are appended. The `watermark` parameter is the resolver's value: `MAX(column)` in the target, less the lookback, `COALESCE`d with the typed initial literal when `on_null: initial`. With `on_null: require_param` an empty target returns NULL and planning asks.

**Script shape.** Every script stages the query result once into a temporary table (so the body runs once, and its result cannot change between the delete and the insert), then applies it inside one transaction that the script opens and closes itself, so it is atomic wherever it is run. T-SQL: `SET XACT_ABORT ON`, `SELECT ... INTO #ddb_stage`, delete, insert (or `MERGE`), drop, commit. PostgreSQL: `BEGIN`, `CREATE TEMP TABLE ddb_stage AS`, delete, insert (or `MERGE`, version 15 or later), drop, commit. The model body is wrapped as a CTE named `ddb_body`, transpiled by polyglot, and the loader's own statements are assembled around it as text, so nothing depends on guessing polyglot's output; a body's own CTEs are hoisted for T-SQL and an unbounded `ORDER BY` gets `OFFSET 0 ROWS`. Polyglot's `unsupportedLevel: raise` is not used: it misses constructs and also rejects supported ones (`REGEXP_LIKE` on SQL Server 2025), so the support matrix decides what may render. Parameters appear only as `@name`; the placeholder lint rejects anything else (DDB-319). Key columns that are nullable are warned about (DDB-320).

**Validation.** Every rendered script and resolver is parsed offline: ScriptDOM for T-SQL (the grammar follows `targets.sqlserver.version`: 14, 15, 16, or 17 and later, and the newest when no version is configured; Fabric uses the newest), polyglot for PostgreSQL. ScriptDOM accepts parameterized scripts (verified). `validate` renders in memory, so an unrenderable model x target x operation pair is reported by name (DDB-317), and so is a script a parser rejects (DDB-318).

**Files.** `render --write` writes `rendered/<target>/<model>/load.<op>.sql`, `load.<op>.resolve.sql` (when the operation has a resolver) and `manifest.yml`, deterministically and without timestamps. Each file starts with a comment holding the model, operation, target, strategy and definition hash (the hash of the normalized body AST, section 12.1), then the parameters and the target rules that fired; no tool version and no matrix version (they would change every file at every release without changing a line of SQL). The manifest lists the operations with their parameters, sources, matrix status (the worst status among the strategy and the constructs used) and the SHA-256 of each committed file, so `apply` can verify the text it executes. `rendered/` is pinned to LF by `.gitattributes`: the hashes are of the committed text. `--write` is all or nothing: it writes nothing if any pair fails, writes only files whose text changed (atomically), and removes stale generated files (`load.*.sql`, `manifest.yml`) in the directories it owns, including those of models that no longer exist, leaving anything else alone. `--check` writes nothing and reports each missing, differing or no-longer-rendered file as DDB-424. `loads` prints the model x target x operation table with the matrix status.

**Conformance.** `matrix/strategies.yml` has one row per strategy and target. The `translated` entries cite `conformance/<strategy>`, cases in `tests/DbDataBuild.Tests.Conformance`. That suite runs the rendered scripts, exactly as committed with only the parameters bound, on real SQL Server and PostgreSQL (`scripts/test-engines.sh up` starts throwaway containers; the suite refuses non-loopback hosts and is reported as skipped when no engine is configured), and compares the result with a DuckDB reference implementation of each strategy's semantics on synthetic data: a first run, a rerun, late-arriving rows, a failure part-way (the target must be unchanged and no temp table left), bound range parameters, and the resolvers' values for timestamp, date and integer watermarks. Fabric entries are `unverified`: no Fabric engine was available.

**Since built** (sections 6.6.2 and 10.4.1): resolvers run at plan time and again at apply, scripts are bound and run at apply, and the executor ends a failed PostgreSQL transaction block with `ROLLBACK`. **Not built:** `define` does not yet ask about declaring additional operations.

### 6.6.2 Slices and runtime parameters (as built)

**The slice is applied to the finished query.** `watermark_append` and `delete_insert_by_range` select `FROM (<your query>) WHERE slice_column >= @watermark`. That is always correct, including for aggregates and windows (pushing the filter into a windowed query would change the numbers). Whether it is also cheap depends on whether the engine can apply the filter before the expensive part. The tool does not rewrite the query to force that. It **advises** (DDB-225, a warning, never blocking): it follows the slice column down through the query's derived tables, CTEs and set operations and reports what stands in the way: the column is the output of an aggregate or a window function, a window is not partitioned by it, or the query has a LIMIT or DISTINCT ON. A column it cannot trace (a star, an ambiguous name) gives no finding. The message carries the way out: slice by a grouping or partition column, or choose a strategy that does not slice the finished result. The author may do their own slicing inside the query and use `full_replace` or a key-based load; the tool does not generate that, and a first-class custom slicing operation is not built. `lint_ignore: [DDB-225]` (per model) and `lint: { slices: false }` (per project) silence it. **The slice's source column (DDB-239, entry 98).** When the slice column is read from a mapped model's column directly or through a cast (by lineage), and the project declares something about that source (its `indexes:` or its `grain`) and no declared index (and not the first grain column, which the primary key's index leads with) is led by the column, the lint says so: each load scans the source to find its slice. A source with nothing declared is not judged; a computed column is not traced; a built model is not judged (its indexes are the tool's to create). The same two switches silence it (`lint_ignore: [DDB-239]`, `lint: { slices: false }`).

**Runtime parameters** are questions (`Q-param-<model>-<operation>-<parameter>`, option `provide` with a value, or `skip_load`). Besides an answers file, `plan --param model.operation.parameter=value` (repeatable) answers them on the command line; the model name may contain dots because the last two parts before `=` are the operation and the parameter. A parameter named twice, in `--param` and in the answers file or twice in `--param`, is refused. `run` cannot take parameters (it refuses open questions), which is intended: a runtime range is a person's decision. Values are checked against their type and `max_span`, and a range that is **empty or backwards** (the end is exclusive) is refused (DDB-412) whether or not there is a `max_span`. For a range load the plan reads the smallest and largest value of the range column on the target (a read-only `SELECT MIN, MAX` through the read session) and **notes**, without refusing, a range the data does not overlap (the DELETE removes nothing; likely a typo of a year) and a range into an empty table (a first load). All parameter values and resolver results are still logged.

## 7. Support matrix

The matrix is **data**, stored under `matrix/`, and every non-`native` row is backed by a conformance test.

### 7.1 Status tiers

| Status | Meaning |
|---|---|
| `native` | Same syntax and semantics |
| `translated` | Rewritten; conformance test passes |
| `approximated` | Rewritten with a documented semantic difference (warning) |
| `emulated` | Multi-statement or helper rewrite (note atomicity) |
| `unsupported` | Build fails if the model declares this target |
| `unverified` | Rewrite exists, no passing conformance test yet (warning) |

### 7.2 Format

```yaml
- id: fn.date_trunc.month
  duckdb: native
  tsql:
    sqlserver: { status: translated, min_version: 16, test: conf/date_trunc_month }
    fabric:    { status: translated, test: conf/date_trunc_month }
- id: op.int_division
  tsql:
    any: { status: approximated, note: "DuckDB `/` yields decimal; rewritten with cast", test: conf/int_div }
- id: syntax.qualify
  tsql:
    any: { status: emulated, note: "Rewritten to subquery with row_number filter" }
- id: type.list
  tsql:
    any: { status: unsupported }
```

A row also carries `detect:` (how the linter finds the construct: `node:<polyglot Expression tag>`, `fn:<FUNCTION>`, or `detector:<name>` for a coded predicate such as `qualify`), and each non-native, non-unverified entry names a `test:` (currently `spike/<case id>`, replaced by conformance test ids in milestone 3). `matrix/covered.yml` lists the nodes, functions and cast data types verified as plain SQL, each with the cases that show it. The matrix files are embedded in the tool.

Keys include engine version or compatibility level where behavior differs. Per-target keys are `sqlserver`, `fabric`, and `postgres`; a `tsql` group is shorthand for both T-SQL targets. A construct that is `native` or `translated` on the T-SQL targets is **not** assumed to behave the same on Postgres, so each target has its own row and conformance test.

### 7.3 Use

- The linter maps each AST node to construct IDs and reports per declared target. Default severities: `unsupported` is an error (DDB-301), `approximated` and `unverified` are warnings (DDB-302, DDB-304), `emulated` is a note (DDB-303), and a `min_version` is a warning until target versions are configured (DDB-308). Policy config will set severity per status.
- **Nothing is assumed safe.** An AST node, function, cast data type, or select clause that is neither matched by a matrix row nor listed in `covered.yml` is reported as DDB-305. This is deliberate: the spike showed polyglot silently passes unsupported constructs through, and its `unsupportedLevel: raise` option catches only a few.
- A `dbdatabuild help matrix` command prints the matrix, and a per-model portability report lists exactly which constructs limit which targets.
- **Semantic profile rules** (string comparison/collation, trailing-space handling, integer division, cast lengths, Unicode literals, NULL ordering, boolean/bit handling, timestamp and decimal precision) are matrix rows tied to per-target profiles. Seed list in section 15.4.

### 7.4 String comparison semantics

String comparison is the highest-risk semantic divergence between the canonical dialect and the targets. It produces different results with no error on either side, so it gets its own rules.

**Facts to design around** (**[VERIFY]** each in the conformance suite):

| | DuckDB | SQL Server | Fabric Warehouse |
|---|---|---|---|
| Case sensitivity | Case-sensitive by default. Collations `NOCASE`, `NOACCENT`, `NFC` are built in; ICU collations (e.g., `DE`) need the ICU extension. Collation applies per expression (`x COLLATE NOCASE`), per column (`VARCHAR COLLATE NOCASE`), or globally (`SET default_collation = NOCASE`) | Governed by the collation of the column, database, or expression. Common defaults are case-insensitive | Default collation may be case-sensitive, with case-insensitive available as an option **[VERIFY]** |
| Trailing spaces in `=` | **Significant.** No collation or setting makes DuckDB ignore them | Typically ignored in `=`, `IN`, joins, `GROUP BY` (ANSI padding), but significant in `LIKE` | **[VERIFY]** |
| `LENGTH`/`LEN` | `length()` counts trailing spaces | `LEN()` excludes trailing spaces; `DATALENGTH()` counts bytes | **[VERIFY]** |

DuckDB's docs warn that its collation support has known limitations, and collations can't be mixed freely (comparing columns with different collations is an error), so every rule below needs a test, not an assumption.

**Comparison profile.** The default profile is SQL Server-style: **case-insensitive, accent-sensitive, trailing spaces ignored**. It is written into `dbdatabuild.yml` by `dbdatabuild connection init`, and the effective profile is printed in every command header, so it is never hidden. A project that wants different semantics changes the file.

```yaml
# dbdatabuild.yml
string_semantics:
  case: insensitive          # sensitive | insensitive
  accent: sensitive          # sensitive | insensitive
  trailing_space: ignored    # significant | ignored
  collations:                # logical name -> per-engine name
    default:
      duckdb: NOCASE
      sqlserver: Latin1_General_100_CI_AS
      fabric: Latin1_General_100_CI_AS_KS_WS_SC_UTF8   # [VERIFY]
```

In DuckDB terms the default profile is `NOCASE` alone: it ignores case but stays accent-sensitive, so do not chain `NOACCENT`. On SQL Server it corresponds to a `_CI_AS` collation.

A target whose configured collation cannot satisfy the project's declared profile is reported at `validate` time, before any connection is needed, and again in `check` against the live catalog.

**Per connection (as built).** The profile above is the project's. A connection may say its own, over it: `connections.<name>.string_semantics` takes the same keys (`case`, `accent`, `trailing_space`, `collations`),
and each field it leaves out is the project's (a collation entry replaces the project's for that logical name and engine). SQL Server ignores trailing spaces in `=` whatever the collation and PostgreSQL keeps them, so
a project that builds on both cannot satisfy one `trailing_space`; it sets the project's value and overrides it on the connections of the other engine. The collation checks (`validate`, `check`) hold each connection to
its own profile, DDL states each connection's collations, and every command header shows the overrides (`trailing_space=ignored (on postgres: ... trailing_space=significant)`).

**What the profile does and does not guarantee (review, 2026-10-06).** Built: the declared profile, the collation checks (offline and against the live catalog), explicit collations in all generated DDL, the `LEN` rule on
SQL Server, and the matrix rows (`str.eq`, `str.like.*`, `str.len`, ... are `approximated` on the engines). **Not built**, though the list below says "How the profile is applied": the DuckDB-side emulation of the target's
semantics (`default_collation`, the `rtrim()` rewrite) for `sample` and `test`, and any rewrite of a comparison on an engine that cannot satisfy the profile natively. So the profile is a **declaration that is checked, not
a behavior the tool imposes**: a comparison runs with the engine's own rules, and a model that runs on two connections with different profiles can return different rows for string comparisons. Ways to make connections
agree, none built: (1) keep the data free of the difference (a data test that no value has trailing spaces; then `=` agrees), (2) rewrite comparisons on the engine that cannot match the profile (`rtrim()` both sides on
PostgreSQL for `ignored`; a sentinel appended to both sides on SQL Server for `significant`; both lose index use), (3) the DuckDB-side emulation, so a sample run shows what the profile means. Which of these, and whether
the lint should say where a model's result depends on the difference, is open (`OPEN-ITEMS.md` M).

**Where connections disagree (as built, 2026-10-06).** Two things make a disagreement visible and, for one of its causes, harmless:

- **DDB-236 (advice, per model).** A model built on connections whose effective profiles differ on `case`, `accent` or `trailing_space`, and whose lowered query uses a **string column** (by its declared type) in a
  filter, join, `GROUP BY`, `HAVING`, window partition, `DISTINCT` or set operation (these depend on all three) or an `ORDER BY` / window order (case and accent only), is reported with the columns and the clauses, and how
  the connections differ. A column named inside a larger expression counts as used there: this says where to look, not what the engine will do. A model silences it with `lint_ignore: [DDB-236]`.
- **`trimmed: true` on a column** (a model's or a mapped model's) declares that no value ends in a space. When trailing spaces are the only difference between the connections, a model whose compared columns are all
  `trimmed` is not reported, since `=` then agrees everywhere. `check` counts the rows that break the declaration on the connection (DDB-237, an error; `DATALENGTH` on SQL Server, `length` on PostgreSQL; a count, never a
  value; a table that does not exist yet is not counted).

- **The profile in `sample` and `test` (as built).** A model runs in DuckDB the way its connection (its first) compares strings. **Case and accent**: the profile's `duckdb` collation (`collations.default.duckdb`, for
  example `NOCASE`) is set as `default_collation` for the model's query only; the table it makes keeps plain `VARCHAR` columns, so a test compares expected and actual values exactly. **Trailing spaces** (DuckDB cannot
  ignore them with any collation): where the profile says `ignored`, the query is lowered (`PlanLowerer.Lower(..., ignoreTrailingSpaces: true)`) and string operands are wrapped in `rtrim()` in comparisons, `IN`,
  `BETWEEN`, join conditions, window partitions, `GROUP BY` keys and `DISTINCT` (a grouped or distinct value is shown trimmed; SQL Server shows either). **Not covered**: set operations without ALL, `count(DISTINCT x)`
  (DuckDB's `default_collation` does not reach it either), and `LIKE`. The run says what it did (a note with the result of `sample`). A model that cannot be lowered is run as written, with a note. Checked against SQL
  Server and PostgreSQL: the same data and collation give the same group, join and filter counts as the emulation.

Not built: any rewrite of a comparison on an **engine** that cannot match the profile (the profile is still a checked declaration there).

**How the profile is applied:**

1. **Declared column collations.** `columns` entries in the model definition may specify `collation: <logical name>`; the default comes from the profile. Generated DDL always states collations explicitly, so objects never depend on a database default. The collation is part of `shape_hash`.
2. **Offline DuckDB execution emulates the target.** Offline runs set `default_collation` and use per-column collations from the profile. Where the profile says trailing spaces are ignored, a **DuckDB-side emulation rewrite** wraps string operands of `=`, `IN`, join keys, `GROUP BY`, `DISTINCT`, set operations, and window `PARTITION BY` in `rtrim()`. This rewrite applies **only when executing in DuckDB**. The SQL emitted for targets is unchanged where the target already has those semantics, which also avoids hurting index seeks.
3. **Transpiled output follows the target's collation rules.** Emit `COLLATE` clauses only where the matrix row requires one (for example, a comparison between columns with different declared collations).
4. **Collation-sensitive operations are lint targets.** The linter identifies, from the AST, every equality or `IN` predicate, join key, `GROUP BY`, `DISTINCT`, `UNION`, window `PARTITION BY`/`ORDER BY`, `ORDER BY`, `MIN`/`MAX`, and `LIKE` over string expressions, and checks each against the declared profile and the operands' declared collations. Comparing operands with different collations is an error offline (SQL Server would reject it at run time with a collation conflict).
5. **Differential comparison normalizes per profile.** When the profile says trailing spaces are ignored, output string columns are `rtrim`-normalized before comparison, because SQL Server may return either representative of rows grouped together. Record this normalization in the test report so it can't quietly mask real differences.

**Matrix rows** (each has a conformance test; status shown is the expected starting point):

| ID | Expected status (SQL Server) | Notes |
|---|---|---|
| `str.eq.case` | translated | Driven by profile and column collation |
| `str.eq.trailing_space` | native on target; emulated in DuckDB | Emulation rewrite above |
| `str.in_list` / `str.join_key` | as above | Same semantics as `=` |
| `str.group_by` / `str.distinct` / `str.union_dedupe` / `str.window_partition` | as above | Row counts differ if semantics differ |
| `str.like.case` | translated | DuckDB `LIKE` is case-sensitive; `ILIKE` is always case-insensitive. Target `LIKE` follows collation |
| `str.like.trailing_space` | approximated | Significant in SQL Server `LIKE` even when `=` ignores them |
| `str.len` | approximated | `length()` vs `LEN()` differ on trailing spaces |
| `str.order_by` / `str.min_max` / `str.window_order` | approximated | Sort order follows collation rules, so ranks and `ROW_NUMBER` ties can differ. Lint warns on ordering by strings without an explicit tiebreaker |
| `str.concat_null` | translated | DuckDB `||` with NULL yields NULL; SQL Server `+` also yields NULL under default settings, but `CONCAT()` treats NULL as empty. Choose one rewrite deliberately |
| `str.unicode_literal` | translated | Emit `N'...'` and `nvarchar` for non-ASCII content |
| `str.cast_no_length` | translated | `CAST(x AS VARCHAR)` needs an explicit length or `max` on SQL Server |
| `str.case_fold` | approximated | `lower()`/`upper()` Unicode behavior may differ by collation |

**Conformance cases for this section** (run under both profile settings: case-insensitive/trailing-ignored and case-sensitive/trailing-significant, using the ephemeral database collation and DuckDB `default_collation` to match):

- `'abc' = 'ABC'`, `'abc' = 'abc   '`, and `'abc' = ' abc'` in `WHERE`, `CASE`, join keys, and `IN`
- `GROUP BY` and `DISTINCT` over values that differ only by case or trailing spaces (row counts and representative values)
- `UNION` versus `UNION ALL` de-duplication of the same values
- `ROW_NUMBER() OVER (PARTITION BY s ORDER BY s)` over mixed-case values
- `LIKE 'abc%'`, `LIKE 'abc'` against `'abc  '`, and `ILIKE`
- `length(s)` versus `LEN(s)` for values with trailing and leading spaces
- `MIN`/`MAX` over mixed-case strings; `ORDER BY` over mixed-case and accented strings
- Accent handling: `'é' = 'e'` and `'é' = 'É'` under the default profile (accent-sensitive), and under an accent-insensitive profile (`NOACCENT` on DuckDB)
- Column-to-column comparison with different declared collations (expected diagnostic, not a run-time failure)
- Empty string versus NULL; non-ASCII values round-tripped through `nvarchar`
- `CAST(x AS VARCHAR)` on long values (truncation must not occur silently)

The synthetic-data edge-case pack (section 15.2) includes every value these cases need.

**Empty strings (measured on Oracle; the rules below are the plan for when Oracle becomes a target, not built).** Oracle has no empty string: it stores `''` as NULL. DuckDB, SQL Server and PostgreSQL keep them apart, and no rewrite can make Oracle do so: `length('')` is 0 in DuckDB and NULL on Oracle, `s = ''` is never true there, `coalesce(s, 'x')` replaces a `''` that DuckDB keeps, and any string function (`substr`, `replace`, `trim`, `concat` of nothing) can produce one. Measured with the probe suite (docs/research/engine-differences): of the 90 probe cases whose answer differs from DuckDB's on Oracle 23ai, **36 differ only in the row that holds `''`** (the probes report them as `empty-string`, a class apart from the undiagnosed `DIFFERENT`), so a quarter of the early Oracle noise was this one fact. The plan:
- **A refusal, not an approximation.** A target that cannot hold the value never approximates silently (section 7.6). Oracle gets a matrix row `str.empty_string`, status `approximated`, reported whenever the query contains an empty string literal or a function that returns text from text that may be empty; the project accepts it for Oracle by setting `string_semantics.empty_string: null` (default: not accepted, an error, DDB-301), the way the collation profile is accepted for string comparison.
- **Declared columns.** A text column declared `nullable: false` cannot be loaded into Oracle (a `''` would be a NULL in a NOT NULL column): `validate` refuses it for an Oracle target, with the column named.
- **Keys and grain.** `''` and NULL are distinct DuckDB values and one on Oracle: a unique key or grain over a text column can collide there. `validate` reports it for an Oracle target.
- **Tests.** Model tests and `sample` run in DuckDB and cannot see it; a model test for an Oracle target may say `empty_string: null`, which maps `''` in the expected and the given rows to NULL before comparing.

### 7.6 Lowering (as built)

Every model query is bound by DuckDB and **lowered** to one explicit query before the matrix lint and the transpile (research: `docs/research/duckdb-plan-lowering/README.md`).

- **Flow**: author's SQL, then DuckDB's bound, unoptimized plan (against empty tables built from the declared columns of every other model and source), then one readable query in DuckDB's dialect with `*`, `USING`, `NATURAL JOIN`, `GROUP BY ALL` and ordinals, macros and implicit casts expanded, then lint and polyglot. The lint and the loads see the lowered query; findings point at its committed artifact.
- **Committed artifact**: `rendered/lowered/<model>/lowered.sql`, with a header naming the model, the source file and its hash, the DuckDB version (the one input of the lowering that is not in the project, so an upgrade shows up as a diff; there is no tool version), the output columns with their resolved DuckDB types and the type rules that changed the text. It is written by `render --write`, checked by `render --check` and by `plan` (a stale or missing file is DDB-424), so a reviewer sees exactly what runs, a DuckDB upgrade shows up as a diff, and the pinned engine version is visible.
- **Plan format and DuckDB versions.** The serialized plan is DuckDB's internal format and DuckDB 2.0 restructured it. `PlanNormalizer` rewrites a 2.0 plan into the 1.x shape the lowerer reads (names from `qname.path`, comparison and cast functions back to comparisons and casts, NOT over IN back to NOT IN, lead/lag arguments), and is the identity on a 1.x plan, so one lowerer serves both. The whole suite passes on the 2.0 alpha. Correlated subqueries rely on DuckDB's `delim_join_as_cte = false` (set by `QueryDescriber.PreparePlanConnection` when the setting exists), which DuckDB has marked deprecated; without it the plan holds materialized CTEs and joins to grouped tables and 16 forms would need an inverse decorrelation. See `docs/research/duckdb-2.0/README.md`, which also records the plan to move. `scripts/test-duckdb-preview.sh` runs the unit tests against DuckDB's preview library.
- **Subqueries are lowered back to subqueries.** DuckDB's binder has already decorrelated them (an `EXISTS` is a `MARK` delim join, a scalar subquery a `SINGLE` join, `LATERAL` an inner or left one, `IN` a `MARK` join), so the lowerer undoes that: each correlated reference is written as the outer column, always qualified (`o.customer_id`) inside the subquery, the joins back to the outer values and the `GROUP BY` on them are dropped, and the right side is printed as a nested query. Supported: `EXISTS` and `NOT EXISTS`, `IN` and `NOT IN` (with DuckDB's null semantics), scalar subqueries including aggregates and `count(*)`, `LIMIT k` with an order inside a correlated subquery, subqueries in the select list, `WHERE`, `HAVING`, `ON`, `ORDER BY` and `CASE`, nesting several levels deep, `LATERAL` and `LEFT JOIN LATERAL`, and uncorrelated scalar, `EXISTS` and `IN` subqueries. `x op ANY/ALL (subquery)` and row-value `(a, b) IN (subquery)` are lowered **as filter conditions** (below). Refused by name: those three-valued comparisons used as a *value* (in a select list, a `CASE`, under `IS NULL`, in `ORDER BY`), a correlated subquery over `UNION`/`INTERSECT`/`EXCEPT`, a window partitioned by a correlated value, and a correlated `LIMIT` with an offset. Table aliases the author wrote are not in the plan, so each subquery source is named after its table (`orders`, `orders_2`).
- **Non-ASCII text literals on SQL Server (as built, entry 120).** A T-SQL string literal is `varchar` in the database's code page unless it is written `N'...'`, and the tool writes text columns as `nvarchar`, so `'日本語'` reached the column as `???` with no error. Every literal that holds a character outside ASCII is now written with the `N` prefix in the text rendered for SQL Server (loads and views; `TsqlLiterals`, applied in `TargetRules.Finish`); ASCII literals, comments and quoted identifiers are left as they were. Not applied to Fabric (unverified: its text columns are `varchar` with a UTF-8 collation) or to a hook script (the person's own text).
- **An aggregate over a subquery (as built, entry 96).** SQL Server refuses an aggregate or a `GROUP BY` key that contains a subquery, so when one does, the lowerer first computes the subquery as a column of a derived table and the aggregate reads that column.
- **`ANY`, `ALL` and row-value `IN` (as built).** DuckDB's binder turns each into a MARK join whose value is three-valued: TRUE if some row of the subquery matches, FALSE if no row matches or could (every comparison on a NULL operand counts as "could"), NULL otherwise; `x op ALL (S)` is `NOT (x op' ANY (S))`, and `x <> ALL` and `x = ANY` are `NOT IN` and `IN`, which every engine already has and which lower unchanged. The rest cannot be one expression, because SQL Server has no boolean values, so the lowerer keeps the mark symbolic and writes it by what the *filter* needs of it, with predicates only: TRUE as `EXISTS (S WHERE p)`, and FALSE as `NOT EXISTS (S WHERE p OR an operand IS NULL)`. The filter's own AND, OR and NOT are followed (`NOT (x > ANY S)` needs the mark FALSE). A mark used anywhere else (a select-list item, `CASE`, `IS NULL`, `ORDER BY`, `GROUP BY`, inside a larger condition) is a **value** (entry 95): it is written as `(CASE WHEN EXISTS (S WHERE p) THEN TRUE WHEN NOT EXISTS (S WHERE p OR an operand IS NULL) THEN FALSE ELSE NULL END)`, DuckDB's three-valued answer, and the target rules lower that boolean like any other (a bit on SQL Server). A join condition on a mark is still refused. For a row of several comparisons DuckDB lets a NULL in any component make the row unknown even when another component is definitely different (SQL's AND would say FALSE), so `(1, 2) NOT IN ((NULL, 3))` is not true in DuckDB; the lowering follows DuckDB. A subquery with an aggregate, window, DISTINCT or LIMIT of its own becomes a derived table first. DuckDB cannot itself run a *correlated* row-value `IN`. The lowered text is tested against DuckDB's own rows on data with NULLs on both sides, in the unit tests and on SQL Server and PostgreSQL.
- **The author's table aliases come back.** DuckDB's plan carries none, so the lowered query used to name each table after itself (`orders`, `orders_2`). The tables of the author's text are read from DuckDB's parse tree in the order the
  planner reaches them (`FROM`, left to right and into subqueries, then `WHERE`, `GROUP BY`, `HAVING`, `QUALIFY`, then the select list) and the n-th scan of a table takes the alias of the n-th mention, when the alias is a plain
  lowercase name that no table, CTE or earlier alias uses; a mention without an alias keeps the table's name. An alias is only a label, so a plan that does not line up with the text costs readability, never correctness. A
  query with a CTE (planned once per use) or that reaches a macro (the plan has scans the text never names) keeps the table names. Generated names never take the name of a table: derived tables are named `s1`, `s2`, and repeated tables `name_2`; every table and CTE name of the plan is reserved first, so a table called `s1` is not captured by a derived table of the same name.
- **`DISTINCT ON`** lowers to `row_number() OVER (PARTITION BY the ON keys ORDER BY the rest of the ordering) = 1` over a derived table, but only when the ordering **decides** the row: DuckDB (like a window) keeps an arbitrary row among ties, so the result would differ between engines. The ordering must include the declared grain of every table the query reads (a source's or model's `grain:`); otherwise it is refused and the message names the columns to add. No ORDER BY, an ORDER BY of only the ON columns, a derived table or a table without a grain are refused.
- **Integer series**: `generate_series` and `range` over integer constants lower to `generate_series(start, stop[, step]) AS series(value)` (`range`'s exclusive end made inclusive, the column cast to BIGINT). SQL Server 2022 (version 16) has the function but takes no column list, so the renderer drops `(value)` for the T-SQL dialects. Date series are refused.
- **A query that cannot be lowered is an error** (DDB-324), not a fallback: `UNNEST` and other table functions, `USING SAMPLE`, `LIMIT ... PERCENT`, list and struct constructors, and the subquery shapes above. A DuckDB parse error is still DDB-306 and an undeclared upstream table DDB-218.
- **Type rules** (so far): `avg` of a non-DOUBLE argument is written `avg(CAST(x AS DOUBLE))`, and a DATE widened to TIMESTAMP by `date_trunc` or interval arithmetic is written with an explicit cast. Division needs none: the binder already casts both operands to DOUBLE. Null ordering is always written explicitly. Output column names are applied from `DESCRIBE` by position, and a name DuckDB repeats gets a `_2` suffix.
- **Binder rewrites are accepted** in the lowered query: `BETWEEN` becomes two comparisons, `NOT (a > 1)` becomes `a <= 1`, `NULLIF` becomes `CASE`, constants are folded, an unused non-materialized CTE is inlined.
- `lowering: { enabled: false }` in `dbdatabuild.yml` turns the stage off for a project (the author's text goes straight to the lint and transpile, as before). There is no per-model fallback.
- Metadata (`metadata`, `publish-metadata`) records each column's resolved DuckDB type, the lowered artifact's hash and the rules fired.
- Still open: target-specific string-comparison, `LIKE`, `REPLACE` and `LENGTH` rules through identity marker macros, `sum` widening, decimal precision pinning, and lowering for the constructs above. SQL Server rejects an aggregate over a subquery (`sum((SELECT ...))`), which lowering faithfully preserves; that is a matrix finding, not a lowering gap.


#### 7.6.1 Target rules (as built)

The lowered query is in DuckDB's dialect and identical for every target. A few functions behave differently on an engine, and polyglot can only translate the name, so a **target rule** step (`DbDataBuild.Targets/Rules/TargetRules.cs`) sits between the lowered query and the transpile. It parses the DuckDB-dialect query, rewrites the recognised shapes into equivalent DuckDB-dialect expressions built from functions that translate cleanly, and hands the result to the transpile. The rewritten text appears only in the rendered scripts under `rendered/<target>/`, which name the rules that fired in a `-- target rules:` header line; `rendered/lowered/` stays target-neutral. A query no rule applies to is passed through byte for byte. The matrix lint and `validate` run on the rewritten query, per target.

The step does not guess types. The lowerer, which has them, **marks** the expressions that need a rule by writing an explicit cast the engines would not need, and the rule removes the mark:

| Rule | Mark in the lowered query | Targets | What it does |
|---|---|---|---|
| `length-trailing-spaces` | none (`length(x)`) | SQL Server, Fabric | `LEN` ignores trailing spaces, DuckDB counts them: `length(x \|\| 'x') - 1` |
| `round-double` | `round(CAST(x AS DOUBLE), n)` with constant `n` (`\|n\| <= 15`) | SQL Server, PostgreSQL | DuckDB rounds the scaled double half away from zero (`round(2.675, 2)` is 2.68, `round(0.285, 2)` is 0.28); SQL Server rounds the decimal text (2.67) and PostgreSQL has no `round(double precision, integer)`: `sign(x*p) * floor(abs(x*p) + 0.5) / p` |
| `try-cast-parse` | `TRY_CAST(CAST(s AS VARCHAR) AS <integer, decimal, double, date, timestamp>)` | SQL Server, Fabric, PostgreSQL | `''` is NULL in DuckDB, 0 on SQL Server, an error on PostgreSQL. SQL Server: `TRY_CAST(NULLIF(TRIM(s), '') AS ...)`. PostgreSQL: a `CASE` that tests the text with a regular expression and the range before casting (integers, decimals and doubles; dates cannot be tested without raising, so they are left as they are and still reported) |
| `sum-widen` | `sum(CAST(x AS BIGINT))` (or `DECIMAL(38, 0)` for a BIGINT) | all | not a target rule but a lowering rule: DuckDB sums integers into a HUGEINT, SQL Server's `SUM` of an `INT` raises an overflow error past 2^31. Written into the lowered query itself, so it is visible in the committed artifact |

Known differences the rules do not remove: a string DuckDB reads as a number that an engine refuses (`'12.7'` as INTEGER is 13 and `'1e3'` is 1000 in DuckDB, NULL on the engines); a PostgreSQL `TRY_CAST` of a string longer than 38 digits raises; `round` of a double beyond 1e15 digits or scaled past the double range. A column declared HUGEINT is still refused, so a model writes `CAST(sum(n) AS BIGINT)`.

#### 7.6.2 Rewrites, and turning them off (as built)

Every step that makes an engine give DuckDB's answer is a **rewrite** with a name, listed as data in `RewriteCatalog` (`dbdatabuild help matrix --rewrites` prints it: what the rewrite does, the targets it is for, where it is required, and what the engine does without it). They fall in two kinds. A **required** rewrite is what makes a query valid on an engine (`pad-to-length`, `concat-plus`, `date-plus-days`, `weekday-datefirst` on SQL Server; `split-part`, `string-agg-array`, `json-extract-string`, `json-array-length`, `regexp-full-match`, `regexp-extract` and `round-double` on PostgreSQL); it cannot be turned off. An **optional** rewrite only keeps the answer equal to DuckDB's (`length-trailing-spaces`, `double-to-int`, `decimal-to-int`, `double-to-decimal`, `avg-double`, `sum-widen`, `try-cast-parse`, `date-to-timestamp`, `date-diff-boundaries`, `date-diff-weeks`, and `round-double` on SQL Server).

```yaml
rewrites:                   # dbdatabuild.yml, and the same keys in a model definition (applied on top of the project's)
  fidelity: native          # exact (default: every rewrite) or native (the engines' own behavior wherever it is allowed)
  disable: [date-diff-weeks]   # single rewrites off
  enable: [length-trailing-spaces]   # single rewrites back on, under fidelity: native
```

`native` gives shorter queries and simpler plans (no `LEN(s + 'x') - 1`, no scaled rounding, no widened sums), at the price of the engines' own answers where they differ; the matrix linter then reports those constructs as notes again, because the rewrite no longer silences them. Naming a rewrite the model's targets need in `disable` is DDB-229 (the other names in the list still apply). The policy is resolved per model for the targets it is built for, so `round-double` stays on for a model built for SQL Server and PostgreSQL (PostgreSQL has no `round(double precision, integer)`). The rendered files say what is off (`-- rewrites off: ...` in the lowered query and in every load script), the setting is part of what the rendered files hash, and the metadata documents carry it (`config.rewrites`, `rewrites_off` per model). `RewriteOptOutProbes` runs the optional rewrites both ways on a real engine and checks that the engine's own answer differs in the way the catalog says; the template conformance test builds `retail` and `adventureworks` with `fidelity: native` and compares row counts.

## 8. Targets

```csharp
public interface ITarget
{
    string Name { get; }
    TargetProfile Profile { get; }        // collation, default string type, precision rules, version
    string Render(Expression ast, RenderContext ctx);   // transpile + per-target post-rewrites
    IReadOnlyList<Diagnostic> Validate(string sql);     // offline validators (e.g., ScriptDOM)
    ILoader Loader { get; }               // engine-specific load statements per model kind
    IDdlGenerator Ddl { get; }            // create/alter statements from shape diffs
    IHooks Hooks { get; }                 // app locks, statistics, indexes, grants (engine-specific, explicit)
}
```

- **SQL Server**: T-SQL via ScriptDOM-validated output, transactional DDL, `sp_getapplock` for mutual exclusion.
- **Fabric Warehouse**: T-SQL surface is narrower. Per Microsoft's T-SQL surface area page (checked 2026-09-30): `MERGE`, `TRUNCATE TABLE`, `QUALIFY`, `GROUP BY ALL`/`ORDER BY ALL`, and column rename via `sp_rename` are supported; `ALTER TABLE` is limited (add nullable columns, drop column, constraints `NOT ENFORCED`; `ALTER COLUMN` is preview); recursive queries, triggers, synonyms and materialized views are not supported; nested CTEs are preview. Docs say what is supported, not how it behaves, so every Fabric matrix entry needs a test on a Fabric engine. None has been available, so Fabric entries are `unverified`. Operations with no native form are `emulated` with explicit atomicity notes.
- **PostgreSQL** (added early as a non-T-SQL target): it differs from the T-SQL targets in identifier case-folding and quoting, case-sensitive string comparison with `varchar` trailing spaces significant, integer division that truncates, first-class booleans, transactional DDL, advisory locks instead of `sp_getapplock`, and `MERGE` only from newer versions **[VERIFY]**. It is close to DuckDB's dialect, so its value is less about hard transpilation and more about exposing T-SQL assumptions baked into shared code (app locks, `nvarchar`, ScriptDOM-only validation, tracking-table DDL, bracket quoting).
- **State and safety as built** (`DbDataBuild.State`, `DbDataBuild.Execution`; see `docs/progress/state-and-apply.md`): logins come from `DBDATABUILD_<CONNECTION>_<READ|WRITE>` environment variables with no fallback (DDB-501); the mutation gate checks effect class (DDB-502), logs before executing and refuses to execute if it cannot log (DDB-503); read queries pass a guard (DDB-504). Tracking-table log ids are GUIDs, not IDENTITY, and a `tracking_version` table records the layout.
- Engine-specific hooks and load logic are written in native target SQL, are exempt from DuckDB-based offline tests, and must be verified on the test engine.

## 9. Commands and effect classes

### 9.1 Command surface (as built; the model behind it: `docs/research/lifecycle-model.md`, for readers `docs/concepts.md`)

The commands are grouped by what they work on. `project` works on the project's files and never opens a database; `connection` works on a database the project is built on (`--connection <name>`, default the project's only default connection); `ui` starts an interface; `help` is reference. A connection has two lanes: **deploy** changes structure (rare, a person decides, recorded as an event) and **refresh** moves data through what exists (frequent, usually scheduled, never changes structure). `docs/commands.md` is generated from the program and lists every option.

Every command declares an **effect class** and a **reads-and-writes code** (`P` the project, `C` a connection, `T` its tracking tables, `S` its structure, `D` its data; `→` reads, `⇒` writes), printed in `--help` and in the header of each run with the connection, the login in use and the lane.

| Command | Reads and writes | Effect class | Purpose |
|---|---|---|---|
| `project create` | `P⇒P` | Repo files only | List the templates, or create a project from one |
| `project model update` | `P⇒P` | Repo files only | Generate or update definition files from the queries (section 6.5); `--check` writes nothing |
| `project compile` | `P⇒P` | Repo files only | Validate, then write `rendered/`: lowered queries, load scripts per connection and the refresh plan of each connection (section 10.6); `--check` compares and writes nothing; `--content` shows the files |
| `project tests run`, `project tests list` | `P→` | Offline only | Run the project's tests (section 9.8) / list them |
| `project sample`, `project seed` | `P→` / `P⇒P` | Offline only / Repo files only | Run models on sample data (section 15.2) / run the seeds into a DuckDB file |
| `project import` | `C→P` | Target read-only (files with `--write`) | Export tables and views as mapped models (section 6.5.1); `--check` for CI |
| `project show loads\|graph\|metadata\|plan` | `P→` | Offline only | The model x connection x operation table / the dependency graph (section 9.9) / everything the tool knows / a plan file |
| `project agent-kit` | `P⇒P` | Repo files only | Install the skill and schemas (section 9.7) |
| `connection inspect` | `C→` | Target read-only | Can the tool use the connection: logins, what they may do, tracking, schema names |
| `connection init` | `P⇒T` | Tracking tables only | Create the tracking tables (prints the script; `--apply` runs it; `--upgrade` brings an older layout to this one) |
| `connection status` | `C→` | Target read-only | Where it stands: drift, blocks, what a deploy would do |
| `connection deploy` | `P⇒S` (`C→P` with `--write-plan`, `P⇒T` with `--ack`) | Target writes | Plan (asking what a plan must), show, and apply (section 10.1); `--write-plan` stops after the plan; `--apply-plan <file>` applies one; `--ack kind:name --reason` records a decision |
| `connection refresh` | `P⇒D` | Target writes (data only) | Run the routine loads of the compiled refresh plan under a check (section 10.6) |
| `connection monitor` | `C→` | Target read-only | History of events, DDL and loads, recorded shapes, what needs attention |
| `connection compare` | `C→` | Target read-only | Compare the data of two tables (section 9.10) |
| `connection seed`, `connection publish` | `P⇒S` / `P⇒T` | Target writes / Tracking tables only | Create and fill the seeded source tables / store the project's metadata |
| `ui terminal\|web\|mcp` | none | Offline only; each action declares its own | The terminal interface (section 9.6), the web page, the MCP server (section 9.7) |
| `help code <code>`, `help matrix` | none | Offline only | Explain a diagnostic / the support matrix |

No flag changes a command's effect class except where the table says (the three modes of `connection deploy` have their own header). Backfills are requested through `connection deploy` (scope option), not a separate mutating command. There are no aliases for the commands of earlier versions.

### 9.2 Logins

- **Read login**: catalog and tracking-table read only. Used by all read-only commands.
- **Write login**: DDL/DML rights scoped to the managed schema names plus insert on tracking tables. Used only by `apply`, `run`, `ack`, `init`.
- Credentials come from the runtime environment (integrated/Entra auth preferred). Never stored in the repo or config files.

### 9.3 The mutation gate

Only code paths within mutating command handlers can obtain a write-capable connection, by way of a single `MutationGate` type that:

- Accepts only statements that originate from a plan step (or a tracking-table writer).
- Records every executed statement (hash, text, step id) to the statement log before execution.
- Verifies the statement's effect class is permitted for the current command.

A test enumerates call paths to `ExecuteNonQuery` and fails on any path not routed through the gate.

### 9.4 Project configuration (`dbdatabuild.yml`)

Offline settings only. Credentials never live here (section 9.2). Every key is optional; an absent key takes the built-in default, and **every command prints the effective settings in its header**, so a default is never hidden. A missing file yields the defaults and a warning (DDB-109). The schema is `schemas/config.schema.json`.

```yaml
title: Sales analytics            # what the project is, in words (6.5.8): title, description, tags and properties; none changes what is built
description: "Orders and customers of the shop."
tags: [sales]
properties: { owner: analytics, tier: 1 }
defaults:                         # model settings every model inherits (section 9.4.1). Default connections: [sqlserver]
  connections: [sqlserver]
connections:                      # named database endpoints; one named after an engine needs no entry (it may set the version)
  sqlserver: { version: 16, title: Warehouse }      # the T-SQL level to generate for (see below); resolves matrix `min_version` rows. A connection may carry the four words too
  # warehouse: { engine: postgres }   # any other name says its engine; its logins are DBDATABUILD_WAREHOUSE_READ and _WRITE
  postgres:  { version: 17 }
tracking:                         # where the records of what the tool built are kept (section 12). Default: nowhere, with a warning
  connection: sqlserver           # a connection of the project, which may be another one than the data's (central tracking); or `tracking: none`
  schema: dbdatabuild             # default: dbdatabuild
string_semantics:                 # section 7.4. Defaults shown
  case: insensitive               # sensitive | insensitive
  accent: sensitive               # sensitive | insensitive
  trailing_space: ignored         # significant | ignored
  collations:
    default: { duckdb: NOCASE, sqlserver: Latin1_General_100_CI_AS }
policy:
  severity:                       # severity of matrix findings: error | warning | note
    approximated: warning         # DDB-302
    emulated: note                # DDB-303
    unverified: warning           # DDB-304
    not_covered: warning          # DDB-305
    native_definition_changed: warning   # DDB-234
    history_inconsistency: warning       # DDB-240
```

- **What a SQL Server version means.** `targets.sqlserver.version` is the **T-SQL level the tool generates for**, which is the engine's major version unless the database runs at an older compatibility level: **16** is SQL Server 2022, and also a 2025 server whose database is at compatibility level 160; **17** is SQL Server 2025 with the database at its own level, 170. There is one setting, not a second one for the compatibility level, because a lower number is always safe: a 2025 server at level 160 runs everything that 2022 runs (checked: the probe suite and the loads agree on 2022 and on 2025 at 160), and what the newer engine adds is used only from the version that has it. Checked on a 2025 server at both levels: `REGEXP_LIKE` and `REGEXP_SUBSTR` exist at 170 only; `REGEXP_REPLACE` exists at 160 too, and a project at 16 does not use it. From 17 the target rules write the regular expression functions (`regexp-full-match`, `regexp-extract`, `regexp-replace-first`); at 16 (or with no version) a query that needs them is reported by the matrix (DDB-301 below the minimum, DDB-308 when no version is set). Fabric gets none of this: it has never been run.
- **Target versions** settle `min_version` matrix rows: at or above the minimum there is no finding, below it the construct is an error (DDB-301), and with no version configured it stays a warning (DDB-308).
- **Policy** can raise or lower a finding's severity but never hides it. `unsupported` findings are always errors and are not configurable.
- **Collation check (offline, `validate`).** For every engine in use (each model's `targets`, the default connections when a model relies on them or there are no models, and always DuckDB) `string_semantics.collations.default` must have an entry, and its name is read for case, accent and trailing-space behavior and compared with the profile. A collation known to contradict the profile is an error (DDB-310); one whose behavior the name cannot settle is a warning (DDB-311) and is never assumed to match; a missing entry, a `collation:` on a column that is not defined, or a defined collation missing an engine the model targets is an error (DDB-312). Only `default` must satisfy the profile: other logical names are declared exceptions. Name rules: SQL Server and Fabric read `_CI_`/`_CS_`/`_AI_`/`_AS_`/`_BIN2` tokens (SQL Server always ignores trailing spaces in `=`; Fabric's is unverified, so it is reported as DDB-311); DuckDB reads `NOCASE`, `NOACCENT`, `NFC`, locale names and `.` chains, and meets an `ignored` trailing-space profile through the offline `rtrim()` rewrite; PostgreSQL reads libc/C locales and ICU `-u-ks-level1/2` names, and cannot ignore trailing spaces natively. The same check runs against the live catalog in `check`: text columns the model leaves on the default collation must have a live collation that satisfies the profile (DDB-310), a column on the database default is DDB-311 because its behavior cannot be verified, and declared exceptions and columns the model does not declare are not held to the profile.

### 9.4.1 Project files in folders and the merge rules (as built)

`dbdatabuild.yml` is the root **project file**. A folder under `models/` may hold `_dbdatabuild.yml`: not a model (the loader and the orphan check skip it). Both files have a `defaults:` section of **model settings**
(`connections`, `kind`, `hooks`, `rewrites`, `lint_ignore`, `tags`, `properties`; `ModelDefinitionLoader.LayeredKeys`; `tags` are labels that `tag:<name>` selects by, merged like `lint_ignore`: they append, `tags=` replaces, `tags-` removes; entry 101; `properties` is a mapping of key to value that merges by key, with `properties=` and `properties-: [key]`, 6.5.8), and for a model the layers are the root file, then each folder's file from `models/` down to the model's folder, then the
model's own file, **the nearest winning**. A folder file has no other section yet; the project-wide sections (connections, tracking, policy, string semantics) are root-only. What is not layered (`name`, `columns`, `grain`,
`loads`, `indexes`, `renames`) stays in the model's own file.

The merge (`YamlMerge`, one table, `YamlMergeTests`): a mapping merges by key, recursively, the nearer layer winning a conflict; a list appends, the inherited items first, a scalar already inherited kept once (a repeat
inside one layer is left for the loader to report); a list of mappings with a `name` merges by `name`; a scalar replaces. A suffix on a key changes that for the key alone: `key=` replaces what was inherited, `key-: [..]` removes
the listed items (or keys), `key+` says "merge". A suffix on a single value other than `=`, and a key that is a list in one layer and a mapping in another, are errors that name the key and its file. A suffix is understood only on
layered keys (and beneath them); on any other key it is just an unknown key. The model's own file is always the last layer, so `connections=: [pg]` means "exactly these" with or without anything above it; `define` writes
exactly that.

Diagnostics name the file the problem is in (a bad inherited `kind` is reported at the folder file's line; each model that inherits it reports it). Provenance is not hidden: `validate` prints
`Inherited by <model>: <path> = <value> (<file>:<line>), ...` for every model that took anything, and the model's metadata document carries the same as `inherited`.

Built since: parameters in files (6.5.3), the name of a model from its definition with `model_layout` (6.5.6). Not built yet: project-wide sections in a folder file (a folder file holds `defaults:` and `parameters:`).

### 9.5 Machine-readable output and its schemas (as built)

Every command takes `--format json` and then prints exactly one JSON document on standard output (`dbdatabuild.output/1`): `command`, `tool_version`, `exit_code`, `ok`, `data`, `diagnostics` (the structured form of the text-mode diagnostics), and `messages` and `errors` (the human text). Exit codes are the same as in text mode. The shapes are **schemas, checked by tests**:

- `schemas/output.schema.json` holds the envelope and one `data` schema per command (`$defs.data_<command>`, selected by `command`). A test requires every command in the command table to have one. Each `data` object is closed (`additionalProperties: false`), so a renamed, retyped or unannounced key fails the build; keys are present when the command got far enough to produce them (a command refused early has few or none), and null values are omitted rather than written as null (the exceptions, `login` and `statement_log`, are typed as nullable).
- `schemas/metadata.schema.json` (`urn:dbdatabuild:schemas:metadata`) defines the **metadata documents**, one per project (`dbdatabuild.project/1`), one per source descriptor (`dbdatabuild.source/1`: columns, grain, and the models that read it) and one per model (`dbdatabuild.model/1`): declared, DuckDB and native types per target, lineage, lowered artifact and rules, rendered loads with hashes and matrix status, indexes, resolved hooks, expected shape hashes, and the index advice. The same documents are what `validate` and `metadata` print and what `publish-metadata` stores in the tracking tables, so one JSON Schema covers both. A new optional key is not a version change; a removed or retyped key is.
- The unit tests validate every offline command's document; the real-engine suite validates every document it sees from `init`, `check`, `plan`, `apply` (dry run, applied and refused), `run`, `report`, `ack` and `publish-metadata` against the schema, on both engines.
- `define` reports its mode, what it checked or wrote, and each model's status with any open questions (as data, with options and proposals) so a pipeline can answer them and run again.

### 9.6 The terminal interface (as built)

`dbdatabuild ui terminal` (Terminal.Gui 2.5, which needs .NET 10, so the solution moved from .NET 8, whose support ends in November 2026) lets a person choose an operation, plan it, read the plan and run it, without remembering options. **It has no logic of its own.** It is a client of the machine interface of section 9.5: every action runs a command in process with `--format json` and shows that document, and anything a command could not do it cannot do either. The consequences, all tested:

- **Forms come from the command definitions.** The catalog is read from the real command tree (every option and argument, its type, default and description), and a form has one field per option or argument. A new option appears in the TUI without writing anything; a test requires every option of every command to be in the catalog, and that the arguments every form can produce are accepted by the real parser. The form shows the exact command line it stands for.
- **Confirmation is by effect.** An action that changes something (data, the target, tracking tables, files) asks first, unless the form as filled in does not change anything (`render` without `--write`, `apply --dry-run`). A project with several default connections is asked once which to work on, instead of letting the command refuse.
- **Questions become dialogs.** When `plan` or `define` stops with open questions, the TUI shows each with its context, options and the tool's proposal (never taken without an explicit choice), writes the answers as an answers file under `.dbdatabuild/`, and runs the command again with `--answers`, the way a pipeline would.
- **Plans are read before they run.** The plan browser lists every step with its risk, the reasons, parameters, resolver result and the exact script, shows the report, and offers a dry run or apply with the flags the plan needs (`--allow-risky`, `--allow-destructive <objects>`) already filled in. A plan that was edited by hand cannot be opened as valid.
- **Models** are browsed from the metadata document (columns with native types per target, loads, indexes, index advice, the lowered query) with actions on the selected model: sample data, render, plan, check.
- **Progress and stop.** A command runs on its own thread; if it takes longer than 0.4 seconds a progress window shows the elapsed time and what the command reports as it goes (for `apply`, each step as it starts and how long it took). For `apply` and `run` a **Stop after this step** button asks the apply to stop between two steps: a statement that has started is never abandoned, the migration is recorded as failed (DDB-445), and `apply --resume` continues from the next step. The live side is `CommandContext` (`CommandHooks`: a progress callback and a stop question, carried with the command), separate from the command's JSON document, which is still delivered at the end.
- **Sample data**: results of `sample` show as tables, one per model, with warnings (declared columns the query does not return) and errors.

Keys: Enter opens, Tab moves, Esc closes a screen, F1 help, F2 models, F3 plans, F4 target, F5 last result, Ctrl+Q quits. `tui` refuses `--format json` and a redirected terminal. Terminal.Gui has no headless driver in its package, so the screens are checked by `scripts/tui_drive.py`, which runs the real app in a pseudo-terminal and prints the screen; the parts that decide behaviour (forms, results, questions, plans) are unit tested without a terminal.

### 9.7 AI agents (as built)

An AI coding agent works through the CLI with `--format json` (the machine interface of section 9.5); an HTTP service is not wanted (a daemon, authentication and a network surface for no new capability), and an MCP server over the same command layer is built (below) (`docs/agents.md` has the reasoning and a permissions example that follows the effect classes). What an agent cannot guess is shipped with the tool: `dbdatabuild project agent-kit` lists, and `--write` installs, a skill (`SKILL.md`: the rules that are never negotiable, the effect of every command, the loop for a model change, how to write a model, what cannot be written and why, how to read a plan) and the JSON Schemas, embedded in the executable so they match its version. `--check` fails when an installed copy differs. Tests keep the skill true: every command, option, diagnostic code and schema it names must exist, its example model must load, and the schemas it ships must be the repository's. The repository's own `CLAUDE.md` is the equivalent for someone changing the tool.

**`dbdatabuild ui mcp`** is that MCP server: the Model Context Protocol over standard input and output (newline-delimited JSON-RPC; protocol 2025-06-18, older versions negotiated), from the same executable. It is a client of the JSON surface like the terminal interface (it reuses the TUI's command catalog and runs each command in process with `--format json`), with no logic and no database access of its own.
- **Tools** are the commands, one each, with inputs from the arguments and options (names with underscores) and the effect class in the annotations (`readOnlyHint` only for commands that never change anything; `destructiveHint` for the ones that write to a target). A result is the command's document (also as `structuredContent`); exit code 1 (findings) is an answer, not an error of the call; a document over 120,000 characters is cut with a note. Progress is sent as `notifications/progress` when the client gave a token.
- **What a model is not given** (principle 8): commands that change a target or its tracking tables (`apply`, `run`, `load-seeds`, `init`, `ack`, `publish-metadata`) unless the person who configured the server passed `--allow-writes`; `--project` (the server has one root); `diff --show-values` and `sample --data`. A path or model name outside the project (absolute, or with `..`) is refused before anything runs. Credentials stay in the server's environment (`DBDATABUILD_<CONNECTION>_<READ|WRITE>`).
- **Getting it started**: the host starts the server, not the model. `dbdatabuild project agent-kit --write --mcp` adds `dbdatabuild ui mcp --project .` (read-only, the READ logins passed on by name) to the project's `.mcp.json` for Claude Code, keeping what else is in the file and refusing a file it cannot merge; the skill has a section for an agent that has the tools (use them instead of the shell, what is withheld and why, open the interface with `show` when the person should look or decide, wait for the host's confirmation, never describe a plan as applied until the person says so).
- **Prompts**: the skill's workflows (`explore-project`, `add-model`, `change-model`, `fix-findings`, `review-plan`), each one user message that sends the model to the skill and to the tools of this server.
- **Resources**: `dbdatabuild://skill`, `dbdatabuild://schemas/<file>` (the agent kit, embedded) and the template `dbdatabuild://explain/{code}` for every diagnostic.
- **Approval**: with `--allow-writes`, a run of a command that changes a database (`apply` unless `--dry-run`, `init --apply`, `load-seeds --apply`, `run`, `ack`, `publish-metadata`) is put to the person through the host (MCP elicitation) and runs only if they approve and type the command's name back; the question shows the exact command and, for `apply`, the plan's steps and risk. A host without elicitation gets a refusal that says what to run by hand. The model cannot answer: the answer arrives from the client on the same channel as any reply, and a model has no tool that sends one. Dry runs and checks need nothing.
- **Cancellation**: `notifications/cancelled` stops the running command between its steps (a second thread keeps reading input while it runs) and its reply is not sent.
- **`show` without the app (as built, entry 129).** A host that does not advertise the extension still gets `show`: the server starts the web page of 9.7 on the loopback address (once, read-only; `--allow-apply` as for the app) and returns a link that works once, for ten minutes. Opening it makes a session: a cookie (`HttpOnly`, `SameSite=Strict`) and a token the page keeps for its calls; the link is spent and the address is redirected to one without it. The other requests need the session. Whoever opens the link first gets the session, which the instructions to the model say not to do and which nothing can prevent for a model that can run commands. `dbdatabuild ui web` itself keeps its address with a token that lasts for the run.
- **The app** (MCP Apps extension, specification 2026-01-26; built, `Mcp/McpServer.cs`, `Web/WebBackend.cs`): for a host that advertises `io.modelcontextprotocol/ui` in `initialize`, the server offers the resource `ui://dbdatabuild/app` (`text/html;profile=mcp-app`: the same page as `dbdatabuild ui web`, with its transport set to `mcp`), marks `review`, `plan`, `graph`, `diff`, `sample` and a new `show` tool (opens a screen) with `_meta.ui.resourceUri`, and adds tools only the app may call (`_meta.ui.visibility: ["app"]`): `ui_run`, `ui_file`, `ui_capabilities`, `ui_answers`, `ui_apply`, `ui_job`, `ui_stop`. They are the web server's endpoints, carried by tool calls through the host: one `WebBackend` decides every refusal for both carriers, so the app can do what the page can and no more. A host without the extension sees none of this (and a call of a `ui_` tool is an unknown tool). **Approval the model cannot give**: `ui_apply` is hidden from the model and needs `dbdatabuild ui mcp --allow-apply`, the plan's target typed in the app and every allowance named (the same checks as the page); `--allow-writes` (commands offered to the model) and `--allow-apply` (the person's button) are independent. The app learns which screen to open from `ui/notifications/tool-result` (`show`, or the document of `review`, `plan`, `graph`...), follows the host's theme, reports its height with `ui/notifications/size-changed`, and keeps the default content security policy (no network, its own script and style). `scripts/mcp-app-host.mjs` is a stand-in host (the message flow of the specification, a sandboxed iframe, the visibility rule) for looking at the app in a browser.
- **Not built**: outputSchema per tool (the schema of every document is a resource), a check against a real host (Claude Desktop, VS Code), display modes other than inline.


**`dbdatabuild ui web`** is the read-only web interface (as built, version 1): one page served from the executable on the loopback address (`--port`, default a free one), a second client of the JSON surface like the terminal interface and the MCP server, with no logic and no database access of its own (`src/DbDataBuild.Cli/Web/`).
- **Screens**: *Health* (`validate` and `test`: counts, findings by file and severity with the `explain` text, a click opens the file at the line), *Lineage* (`graph --columns`: a layered graph, click a table to follow what it reads and what reads it, with the column lineage), *Models* (`metadata`: grain, targets, upstream, columns with native types and where each comes from, the query, and the lowered query and rendered scripts per target, made now by `render --content`, so they are current and need nothing written), *Tests* (with violating rows), *Plans* (`review`: the plans of the project, one with its steps, risk, exact statements, the report and the allowances applying it needs, an edited plan flagged; the page cannot apply a plan), *Support matrix* (the constructs per target and the rewrites). It is plain HTML, CSS and script in one embedded file, with no build step.
- **Safety**: the commands it can run are `validate`, `graph`, `metadata`, `test`, `loads`, `matrix`, `explain`, `review` and `render` without `--write`; the arguments go through the same checks as the MCP server's (`ToolSurface`: no path outside the project, no project option, no option that shows values). Files are readable only under `models`, `sources`, `seeds`, `tests`, `rendered`, `hooks` and the configuration, as text, never through a link; plans, the state directory and the environment are not. It listens on `127.0.0.1` only; every request needs the random token printed at start (in the address, then in a header), the Host of this server and, when the browser sends one, this server's Origin; there is no CORS header and the page has a content security policy that allows only itself. Commands run one at a time. Everything from the project is put in the page as text, never as markup.
- **Plans, answers, apply** (`src/DbDataBuild.Cli/Web/WebActions.cs`): *Make a plan* runs `plan` (target read login from the server's environment; it writes plan files into the project). When `plan` asks questions the page shows each one with its options, consequences and the tool's proposal, the person chooses (nothing is answered for them: `plan --accept-inferred` is not offered here, and "Choose that" is a click on the proposal), the answers are written to `.dbdatabuild/web-answers.yml` (the terminal interface keeps its own `tui-answers.yml`) and the plan is made again. **Apply** exists only when the server was started with `dbdatabuild ui web --allow-apply` (the operator's decision, and the write login must be in the environment). Per request the person names each allowance (the risky steps, each object with a destructive step) and types the plan's target; the server reads the plan file itself and checks the id, the target and the allowances against it (an allowance the plan does not need is refused, an edited plan is refused), then runs exactly `apply <plan> [--allow-risky] [--allow-destructive o]...` as a background job the page follows (progress lines, a stop request honoured between steps). The confirmation is cleared after each run. A dry run needs no confirmation and changes nothing. The page cannot send an argument line.
- **Sample data and table diff** (`sample`, `diff`): *Sample data* runs models in DuckDB on the project's generated rows and shows the tables (offline, nothing read from a target). *Table diff* compares two tables of one target inside the engine with the read login of the server's environment: counts, columns, types, duplicate keys, rows that differ by column, column statistics; values (minimum and maximum, sample rows of each difference) only when the person ticks "Show values", and then shown to them in the page, never to a model (the MCP server still withholds `--show-values` and `sample --data`: `ToolSurface` offers them only when the client is a person's page).
- **Not built**: `apply --resume` and `--allow-dirty` from the page (a terminal does those), the page as an MCP app, a VS Code webview.

### 9.8 Project tests (`dbdatabuild project tests run`)

Projects can test themselves, with metadata rules and model tests (both built; gating on tests is designed for and not built). The language for a test is DuckDB SQL, and the configuration is YAML; nothing else is introduced until a need for it arises.

**Metadata rules** (`tests/metadata/<name>.sql`, as built). A rule is one DuckDB `SELECT` over the metadata views that returns the *violations*; no rows means it passes. The name is the path under `tests/metadata/` with `/` as `.` (`naming/no_max.sql` is `naming.no_max`). Settings are `-- key: value` comments at the top of the file (the comments before the first line of SQL; a comment that is not `word: value` is prose and is ignored; an unknown key, a repeated key or a bad value is a diagnostic with a line number):

```sql
-- description: Output columns declare a length
-- severity: warning        -- error (the default) | warning
-- tags: naming, schema     -- groups: lowercase letters, digits, - and _
SELECT model, column_name FROM metadata_columns WHERE kind = 'model' AND logical_type = 'VARCHAR'
```

**The views.** The metadata documents (the project, every source, every model: what `metadata --format json` prints and `publish-metadata` stores) are loaded into an in-memory DuckDB with file and network access off, and flattened into views. `metadata_current` (`kind`, `subject`, `document` JSON, `document_hash`, `tool_version`) and `metadata_columns` (`model`, `column_name`, `logical_type`, `nullable`, `collation`, `sqlserver_type`, `postgres_type`, `fabric_type`, `inferred_nullability`, `upstream`, `kind`: one row per model **or source** column) have the names and columns of the views `init` creates in a target, so a rule means the same thing against published metadata. The rest flatten the documents: `metadata_models` (kind, unique key, time column, lookback, grain, connections, files, hash, column count, title, description, tags), `metadata_sources` (grain, file, hash, consumers, title, description, tags), `metadata_info` (the rows of the tracking table `object_info`, 6.5.8: `subject_kind`, `subject`, `element`, `item`, `item_key`, `value_type`, `value`, for the project, connections, models, sources, columns and indexes), `metadata_upstream` (model, upstream, `model` or `source`), `metadata_ancestors` (every table a model depends on, with its distance), `metadata_lineage` (one row per column and upstream column, with the transform), `metadata_native_types` (one row per column and connection, with collation or the mapping error), `metadata_indexes`, `metadata_source_indexes` and `metadata_source_foreign_keys` (what an imported source table has), `metadata_loads` (connection, operation, strategy, matrix status, findings, script hash), `metadata_hooks` and `metadata_index_advice`. Lists are DuckDB lists (`len(grain)`, `list_contains(connections, 'postgres')`). The documents themselves are always there in `metadata_current.document` for anything the views leave out. The views are built from the published documents, so a rule sees exactly what is published; they are not yet created in the target (a later step).

**Running.** `dbdatabuild project tests run [names or files] [--tag t ...] [--limit n] [--strict]`, effect class offline only. A rule is parsed by DuckDB first and must be exactly one `SELECT` (DDB-603); one that DuckDB cannot run is DDB-602 whatever its severity. A rule that returns rows is DDB-601: error severity makes the run exit 1, warning severity is reported (as a warning-severity diagnostic) and exits 0 unless `--strict`. `--tag` runs the tests that have any of the given tags; a damaged file is its own finding and never stops the others. The result is one document (`data.counts`, `data.tests[]` with status `pass`, `fail`, `warn` or `error`, the violating rows up to `--limit`, and the columns).

**Gating (version 1 as built, entry 99).** `tests: { gate: { tags: [critical] } }` in `dbdatabuild.yml` names the groups: `plan`, `check` and `run` run the tests with those tags (offline, in DuckDB, after the project validates and before the target is read) and a test that fails or cannot run refuses the plan ("Nothing was planned: a test tagged critical ...", exit 1, the failing tests printed); a pass is printed ("Test gate (tags critical): 3 test(s): 3 passed"). Tests with other tags only advise, as before; `apply` does not run tests (it runs a plan that was gated when it was made). **Not built, still designed for:** a severity floor in the gate, recording the gate's result in the plan file and in the plan hash (today it is in the command's output only), and gating `apply`.

**Model tests** (`tests/models/<name>.yml`, as built). A file tests one model: `<name>` is the path under `tests/models/` with `/` as `.` and is the model's name unless `model:` says otherwise. Settings (`description`, `severity`, `tags`) are `#` comments at the top of the file, as `--` comments are in a rule; the owner expects most model YAML settings to be allowed that way in a future version. A case has a `name`, `given` rows per table the query reads, and `expect` and/or `assert`:

```yaml
# tags: critical
cases:
  - name: keeps null amounts
    given:
      staging.orders:                      # a source, or another model: its declared columns
        - {order_id: 1, amount: 10.00}
        - {order_id: 2, amount: null}
    expect:                                # a list of rows, or {rows: [...], ordered: true}
      - {order_id: 1, amount: 10.00}
      - {order_id: 2, amount: null}
  - name: amounts are never negative
    given: {staging.orders: [{order_id: 1, amount: 5}]}
    assert: SELECT * FROM result WHERE amount < 0     # violations, like a rule
```

All scalars are read as strings and cast by DuckDB to the declared type of their column (`10` is `10.00` for a `DECIMAL(14, 2)`); a plain `null`, `~` or empty value is NULL and the *quoted* text `"null"` is the string (the YAML reader records whether a scalar was quoted for exactly this). A column left out of a row is NULL; a `given` row that leaves out a NOT NULL column, names a column the table does not declare, or a `given` table the query does not read, is an error (DDB-602), as is a value that does not cast. A table the query reads that is not given is empty. The tool creates the tables in an in-memory DuckDB (external access off) from the declared columns of the sources and models the query reads, fills them with the `given` rows, runs the model's query **as written** into a table named `result`, casts it to the declared output columns and types, and compares with `expect` using `EXCEPT ALL` both ways: duplicates count and order does not matter unless `ordered: true`. A difference is a failure (DDB-601) whose rows carry `_diff` (`missing`: expected and not returned; `unexpected`: returned and not expected), or for `ordered`, the positions that differ. `assert` is a single SELECT (DDB-603 otherwise) over `result`, one violation per row. Each case is one entry of `data.tests[]` (`<name>::<case>`, kind `model`, with `model` and `case`); severity and tags are the file's.

These tests check a model's **logic in DuckDB**: the lowering to each engine is tested against real engines, and running a case on a target would be a separate opt-in effect class. Not covered yet: floating-point tolerance, parameterized loads and the multi-run behaviour of incremental kinds, nested types (arrays, structs) as values.

### 9.9 Dependency graph and model selectors (as built)

**The graph.** Which table each model reads comes from parsing the queries (names only: no DuckDB, no target), so it is cheap and always current; sources are the tables no model builds, and a name that is neither is an unknown table (DDB-218 elsewhere) that the graph keeps and shows. `DependencyGraph` (in `DbDataBuild.Models`) answers ancestors and descendants with their distance and a depth limit, and the level of each model (the longest chain from a source). It is built lazily, only when a selector needs it. A cycle is DDB-221 as before; the graph walks end instead of looping.

**Selectors** are accepted wherever a command takes models (`validate` takes none; `render`, `plan`, `check`, `run`, `sample`, `metadata`, `publish-metadata`, `graph`). A plain name, file or directory still means what it did. New:

| Selector | Selects |
|---|---|
| `+model` | the model and everything it reads, through other models |
| `model+` | the model and everything that depends on it |
| `2+model`, `model+1` | the same, limited to that many steps |
| `@model` | the model, everything that depends on it, and everything those need to be built (dbt's `@`) |
| `kind:full`, `connection:postgres`, `tag:finance`, `property:owner=finance`, `path:models/marts` | by kind, by a connection the model is built for, by tag (`tags:` in the definition or in a `defaults:` section above it), by a property and its value as text (`property:owner` alone: has the property), by directory |
| `changed:<git ref>` | the models whose `.sql` or `.yml` differs from the ref in the working tree (committed or not, and untracked files); a changed source descriptor selects the models that read it; a changed `dbdatabuild.yml` selects every model; a changed folder file (`_dbdatabuild.yml`) selects every model beneath it, a changed native text the models that read it, a changed hook script the models that run it, a changed macro file the models whose queries reach one of its macros, and a changed rendered file the model it was rendered for (entry 97). `changed:origin/main+` is the changed models and what depends on them. Nothing changed is a valid, empty selection |
| `a,b` | the intersection of the terms: `kind:view,stg.orders+` |
| `exclude:<selector>` | takes models out again: `marts.fct_orders+ exclude:marts.big` |

Several arguments are a union. A term that selects nothing is a usage error that names the term (a typo must not run nothing); the exception is `changed:`. `@` is not a response file marker here (the command-line parser's response-file feature is off). A selector that adds models adds them for *every* purpose: `plan +marts.big` plans the model and everything it reads, in dependency order, as the planner already orders them.

**`dbdatabuild project show graph [selectors]`** (offline). It prints the chosen models and the tables they read by level, with each model's kind and what it reads (`--format json` has nodes, edges and unknown tables). `--columns` adds, for each output column, which column of which table it comes from and how (`direct`, `expression`, `aggregation`, ...), from the same lineage the metadata documents carry. `--column model.column` follows one column through the whole project: the source columns it comes from and every column built from it, with distances (the answer to "what does a change to this column reach?"). `--diagram dot|mermaid` prints a Graphviz or Mermaid diagram.

**For rules**: the view `metadata_ancestors(model, ancestor, depth)` (a recursive query over `metadata_upstream`) is every table a model depends on and how far away it is, so a rule can say "no mart reads a source directly" or "nothing in `marts` is more than four levels above a source".

### 9.10 Table diff (`dbdatabuild connection compare`, as built, version 1)

`diff <schema_name.table_name> --against <schema_name.table_name>` (or `--against-schema <schema name>` for the same table name under another schema name, a development copy for example) compares the data of two tables or views **of one target** with read-only queries (effect class: target read-only; the read login; every statement through `ReadSession`).

- **Schema.** The columns on both sides are paired by name. Columns on one side only are listed, a column whose type has no equality (`text`, `xml`, `json`, spatial types) or whose two types are of different kinds (text against a number) is **not compared** and says why, and a column whose type differs within a kind (`decimal(14,2)` against `decimal(18,3)`) is compared and the difference reported. `--columns` and `--exclude-columns` narrow the comparison (the key always stays).
- **Key.** `--key a,b`, else the table's grain or unique key in the project (a model's `grain`, else `unique_key`, else a source descriptor's `grain`); with neither the command asks for `--key`. A key that is not unique on either side stops the row comparison and says how many keys repeat (the counts are still reported).
- **Counts, inside the engine.** The row counts and non-NULL counts of every compared column (one query per side), the number of repeated keys, and then one `FULL OUTER JOIN` on the key that counts the rows on the left only, on the right only, matched, and matched with different values, in total and per column. A value is different if it differs or exactly one side is NULL (NULL equals NULL); the key is compared with `=`, so a row with a NULL key is a row on one side only. Text columns with different collations are compared in the left column's. Rows are marked present with a constant column, so a NULL in a key never makes a row look absent. Nothing is written and no row leaves the engine.
- **Values only on request.** By default no value is read back: the output is counts and column names, so an operator can work and review without real data on screen. `--show-values` (with `--limit`, default 10) also reads the smallest and largest value of each column and sample rows of each kind of difference (only on the left, only on the right, and the differing columns of matched rows, as old and new), ordered by the key. They appear in the output of that command, in JSON under `samples`, and nowhere else: not in the statement log (which records SQL, never results), not in a plan. This is by design (principle 8): the default needs no data, so an agent can run `diff` and act on the counts; a person can ask to see more.
- **Exit code.** 0 when the compared columns are identical (same rows, same values), 1 when they differ or the key is not unique, 2 for a usage problem (a table that is not there, no key).
- **Across connections (as built, entry 92).** `--against-connection <connection>` compares the table with the one on another connection (the same schema name and table name unless `--against` or `--against-schema` says another), and the two connections may be different engines or two servers of one engine. They cannot join, so each engine computes, from the table, a 64-bit digest (the first eight bytes of SHA-256) of every value from a **canonical text form** that is the same on both engines, and only digests are compared. First per bucket (the first two hex digits of the digest of the key: a row count and, per column, the sum of the low 32 bits of the digests), then, for the buckets that differ, per row: the rows on one side only, the matched rows whose digests differ, and which columns. A repeated key stops the comparison as in one engine. No value leaves an engine, in the output or on the wire, unless `--show-values` is given, and then only the sample rows (one more query per side by key digest). The canonical form: **numbers** (integers, `decimal`/`numeric`, `money`) cast to the larger of the two scales and written as text (`1.50`, so `numeric(12,2)` matches `decimal(18,3)`); **text** exact, as UTF-8 (case and trailing spaces count, since this is a comparison of stored data and not of engine comparison semantics; a fixed-length `char` is trimmed because its padding is part of the type; on SQL Server through `nvarchar` and a UTF-8 collation); **date** `yyyy-mm-dd`; **timestamp** `yyyy-mm-ddThh:mm:ss.ffffff` (six digits: `datetime2(7)` is rounded to them); a timestamp with a zone converted to UTC; **time** with six digits; **boolean** `1`/`0`; **uuid** and **binary** as lower-case hex; NULL is a marker, so NULL differs from the empty string. A column with no such form is **not compared** and says why: `float`, `real`, `double precision` (no text form is the same on both engines; compare them with `diff` in one engine or round them in a view), a PostgreSQL `numeric` without a declared precision, `xml`, `json`, spatial types, and two types of different kinds (`date` against a timestamp). The digests can collide with a probability of about 2^-64 per row pair; a difference is never missed otherwise. Verified on SQL Server 2022 and PostgreSQL 17 both ways (`CrossDiffConformanceTests`); Fabric not run (it needs a UTF-8 collation and `HASHBYTES`, which it has, unchecked). Measured on SQL Server 2022 against PostgreSQL 17 in local containers (both sides run at once, with no query time limit): 1 million rows of five columns in about 25 seconds and 5 million in about 110 (`CrossDiffScaleTests`, run with `DDB_SCALE=1`). **When too many rows differ** (more than 1,000,000 rows in the buckets that differ, which would take about 200 MB of digests to compare one by one) the comparison stops at the buckets: the document has `rows.partial` (buckets that differ, rows in them, and the columns whose values differ) and the text says to narrow the columns (`--columns`, `--exclude-columns`) to see the rows that differ in the others; this is the case that matters most when one column differs systematically between two engines. Limits: the digest query reads the whole table in each engine (an index on the key does not help; there is no row filter yet), and when more than 160 buckets differ the digests of every row are fetched (16 bytes per column per row).

## 10. Planning and applying

### 10.1 `dbdatabuild connection deploy` flow

1. **Discover**: read repo (models, intents, answers), target catalog, and tracking tables.
2. **Compute findings**: shape/physical/definition hash comparisons, drift, required operations by applying the decision table (section 11).
3. **Ask**: collect every open decision into a list of **questions** with stable IDs, context, and explicit options. No silent defaults.
4. **Answer**: interactively, or from `--answers answers.yml`. In non-interactive mode, unanswered questions fail the run and **all are listed at once**, each with the exact YAML to add.
5. **Generate**: produce the plan only when nothing is open. Answers are embedded in the plan.

Question IDs are deterministic (same scenario, same ID) so answer files are reusable and testable.

```yaml
# answers.yml
answers:
  - id: Q-history-marts.fct_orders.discount_code
    choice: not_backfilled
    note: "No history exists in source."
  - id: Q-rename-marts.dim_customer.cust_nm
    choice: rename_to
    value: customer_name
  - id: Q-define-marts.fct_orders-type-amount
    accept: inferred
```

**Questions** (`DbDataBuild.Core.Questions`). A question has a stable id `Q-<area>-<subject>`, a prompt, context lines, and at least two explicit options, each with a lowercase snake_case key, a description, an optional consequence, and optionally a value it takes (`rename_to` takes the new column name). No option is a default. Areas and their id shapes: `define` (`Q-define-<model>-<field>`), `history` (`Q-history-<model>.<column>`), `rename` (`Q-rename-<model>.<column>`), `param` (`Q-param-<model>-<operation>-<parameter>`), `adopt` (`Q-adopt-<object>`). Ids are built only by `QuestionIds`, so a scenario always yields the same id.

**Answers.** Each answer is explicit: `choice` (one of the question's option keys, plus `value:` when that option takes one, and an optional `note:` that is kept with the answer in the plan), or `accept: inferred` to accept the question's inferred proposal. The schema is `schemas/answers.schema.json`. A question may carry a **proposal** (an inferred option and value, with evidence and a certainty). Accepting one is always an explicit answer: `accept: inferred`, the accept key at a prompt, or `--accept-inferred`, which accepts only proposals marked high certainty and is recorded as such (`AcceptedProposalByFlag`).

**Resolution** (`QuestionResolver`). Questions are processed in id order, so the outcome does not depend on input order. A file answer is validated against the question: an unknown choice is DDB-411, a value that does not fit the option is DDB-412, `accept: inferred` without a proposal is DDB-413, and an invalid file answer is an error that is never silently replaced by a prompt. An answer for a question that was not asked is only a warning (DDB-410), because answer files are reused across runs. Without a prompter, every unanswered question is reported at once as DDB-414 with the question's text and paste-ready YAML for each option. With a prompter, only questions the file does not answer are asked. Resolved answers record how each was answered (`File`, `Interactive`, `AcceptedProposal`, `AcceptedProposalByFlag`) and serialize back to the same file format (`AnswerSerializer`), which is how a plan embeds them.

### 10.2 The plan document

Two renderings of one object: a readable Markdown report, and a machine-readable YAML that `apply` consumes. Each plan records: target, git commit, git-dirty flag, tool version, base hashes, answers, steps, and "noticed but not done" items.

Each **step** has: ordinal, type (`ddl` | `load` | `backfill` | `hook`), the **full script text**, a **reason chain** (model change, answer, drift, or request), risk class, expected effect (objects and expected result hash), and lock/transaction notes.

```
PLAN 2026-10-12-fct_orders-01     target: sqlserver-prod     commit: 4af31c2
Summary: 1 DDL step, 1 load step. Nothing else will run.

1. [ddl, safe] ALTER TABLE [marts].[fct_orders] ADD [discount_code] varchar(20) NULL;
   Why: model marts.fct_orders (commit 4af31c2) declares discount_code; target lacks it.
   Decided: answers.yml Q-history-marts.fct_orders.discount_code = not_backfilled ("No history exists in source.")
   Lock: brief schema-modification lock.

2. [load] MERGE ...   (full script)
   Why: routine incremental load; watermark 2026-10-11 read from target.

Noticed but NOT done:
  - Rows loaded before 2026-10-12 will have NULL discount_code.
  - Index IX_fct_orders_date differs from model definition. No change planned.
```

### 10.3 `dbdatabuild connection deploy --apply-plan` semantics

- Verifies plan integrity (content hash). A hand-edited plan is refused unless regenerated through the plan command.
- Verifies **base hashes** against the live target. A mismatch is a **stale plan**: refuse, never guess.
- Refuses from a dirty working tree unless explicitly allowed. Records git commit and plan hash.
- Acquires the application lock. Applies steps in order, recording per-step status.
- After DDL steps, recomputes the shape hash and compares to the plan's expected result. Mismatch is logged and blocks dependents.
- **Partial failure**: per-step status is recorded. Applying the same plan again continues it, and only if the live hashes match the recorded intermediate state (the finished steps are skipped); otherwise it stops and requires a new plan. A plan that completed is refused (DDB-438).
- `--dry-run` prints every statement and runs all preflight checks using the same code path as a real apply.

### 10.4 Risk classes

| Class | Examples | Requires |
|---|---|---|
| safe | add nullable column, widen type, add index | plain `apply` |
| risky | add NOT NULL column, collation change, size-of-data operations on large tables | `--allow-risky`; plan shows estimated rows from catalog metadata only |
| destructive | drop column/table, narrow type, truncate | `--allow-destructive <object list>`; objects must be named explicitly |

Renames are never inferred. An undeclared rename plans as a destructive drop plus an add, and raises a question.

### 10.4.1 Planning and applying as built

`connection deploy` (`--write-plan`, `--apply-plan`, `--ack`), `connection status` and `connection monitor` exist (see `docs/progress/state-and-apply.md` entries 5 to 8 for the decisions and the evidence). Points where the build settles or differs from the text above:

- The decision table is `matrix/decision-table.yml`: 32 rows, each with an owner (`planner`, `command` or `apply`), and a test for every planner row. Planner steps name their row id first in their reason chain.
- A plan with blocks is still written for the models that are not blocked; blocks and skips are listed in the report and the command exits non-zero.
- Plan files are YAML with a SHA-256 over their content. `apply` refuses any plan that is not byte-for-byte what `plan` wrote (DDB-435), including cosmetic edits.
- Applying a plan takes `--allow-risky`, `--allow-destructive <object>` (repeatable, naming each object), `--dry-run` and `--allow-dirty` (a tree with changes is refused by default; `plans.deploy.require_clean_tree: false` lifts that). A plan is applied once (DDB-438); a plan that stopped part-way continues, from the exact intermediate state it recorded, when it is applied again.
- `ack drift` and `ack definition` record a person's acknowledgement of one specific hash in `block_log`; they never change user data.
- Views are tracked by the hash of the applied `CREATE OR ALTER VIEW` text (in `ddl_log`); their column types are derived by the engine, so a view step has no predicted shape hash.
- `connection refresh` runs the routine loads of the compiled refresh plan (section 10.6). `connection monitor` lists applied plans, DDL and load history, recorded objects and what needs attention.
- `plan --op model=operation` and `plan --backfill model=operation` choose operations; a backfill is a risky step recorded in `operation_interval`.
- `report` includes the per-column history report (12.3) built from the answers in applied plans; its warning-or-block policy for downstream models is built (12.3, entry 100).
- Indexes are declared in the model (`indexes:`), never implied by `unique_key`; the planner creates missing ones, rebuilds changed ones (risky), and never drops undeclared ones. Hooks (`hooks:` in a model, `hook_groups:` in `dbdatabuild.yml`) are ordered, named, native-SQL scripts attached to events (`pre_`/`post_` create, alter, load, backfill; drop is reserved) per engine, and run as `hook` steps in plans.
- **Index lint and generation** (`IndexAdvisor`): the planner still creates only declared indexes, but `validate` and `plan` now advise. A key-based load (`merge_by_key`, `delete_insert_by_key`, or the implicit one of `incremental_by_unique_key`) whose key no declared index leads with (as a set of columns, on every target that runs the load) is **DDB-223, a warning**: each load scans the table. A key that is indexed but not declared `unique: true`, and the watermark, time and range columns of `watermark_append` and `delete_insert_by_range`, are **DDB-224, a note**. The message carries the exact line to paste under `indexes:` (`ux_<table>_<columns>` for a unique key, `ix_` otherwise, at most 60 characters). Advice never blocks. An operator silences a code for one model with `lint_ignore: [DDB-223]`, or all index advice with `lint: { indexes: false }` in `dbdatabuild.yml`. `define` asks, for a new model, whether to declare the suggested indexes (a normal-certainty proposal, so `--accept-inferred` never takes it); the existing-definition update path does not add indexes.

### 10.5 Plans live in the repo

Plans for shared targets are committed under `plans/<connection>/` with their answers and intents. Dev and ephemeral plans are not committed, and neither are plans containing load steps with resolved parameter values (section 6.6). `migration_log` in the target records plan hash, plan text, git commit, and before/after hashes, so the target's audit trail is self-contained.

### 10.6 Refresh from a compiled plan (as built; model: `docs/research/lifecycle-model.md`, sections 5 and 8)

A refresh is the frequent lane: it moves data through objects that exist and never changes structure. It does not plan. `project compile` writes, for each connection, `rendered/<connection>/refresh.plan.yml` (`RefreshPlanBuilder`, `RefreshPlanDocument`), a deterministic file with a hash of its own content that is committed with the project and documents how the project operates every day:

- `loads`: the routine loads in dependency order. Each is a model's default operation: the committed load script (and resolver) by path and hash, the model's definition hash, the values the project's files give its parameters, the watermark's `on_null`/`initial`, and the safe data hooks (`pre_load`, `post_load`) around it, by path and hash. A value only the connection knows (a watermark) is not in the plan; the resolver finds it when the refresh runs.
- `requires`: each loaded table and the shape hash the declared columns give it on that engine.
- `project_hash`: a hash over the expected structure of the whole connection (each table model's shape, each view's definition). A deploy records the same hash on its event (`migration_log.project_hash`, tracking layout 5).
- `excluded`: models with loads that are not routine, with the reason (a copy; a load that needs a value from a person; a hook that is not a safe data hook). A deploy handles those.

`connection refresh` reads that file and the scripts it names (a script whose hash differs is DDB-448; a missing plan is DDB-446), applies the check, finds the watermarks, and runs the loads through the same apply engine in refresh mode: no verification against the live objects, nothing to continue, and an id of its own per run (`ref-<utc>-<4 hex>`) in `migration_log` with `lane = 'refresh'` and the compiled plan's hash. It writes no plan file; the plan text is kept in the event only when `plans.refresh.audit` is `full`.

The check (`refresh.check`, or `--check`) chooses what to compare before anything runs. **none**: nothing; the engine's own errors are the check, and the connection is asked nothing about its structure (a test proves no catalog query is made). **project**: the plan's `project_hash` against the last completed deploy's; tracking tables only. **objects** (the default): each loaded object's expected shape against the latest recorded one; tracking tables only. **live**: each against the catalog as it is now, which also names a change made outside the tool. A failed check stops the refresh before it runs (DDB-447) unless `refresh.on_fail: warn` or `--on-fail warn` says to report and run. The test gate, native definition recording and metadata storing belong to deploy.

## 11. Planning decision table

Rows are (model kind x target condition). Every cell is an operation, a question, or an explicit refusal with a diagnostic code. A test enumerates the cross-product and fails on any undefined cell.

| Model kind | Target condition | Result |
|---|---|---|
| any | object missing | DDL: create |
| any | object exists, no tool record | **Question**: adopt existing object, or stop |
| any | shape hash differs from last recorded (out-of-band change) | **Block** until `ack drift` or re-plan |
| table kinds | model adds column | DDL add column + **Question** (history disposition) |
| table kinds | model removes column | destructive DDL (requires explicit allow) |
| table kinds | type widening / narrowing | safe / destructive |
| view | definition hash differs | DDL: alter view |
| incremental_* | definition changed since last load | **Block**; report differences; require `ack` or explicit backfill |
| incremental_by_unique_key | unique key missing in the model definition | **Unsupported**: DDB-2xx |
| any | model definition out of sync with its body (declared `columns` differ from resolved query output) | **Block planning** for that model: DDB-4xx, run `dbdatabuild project model update` |
| any | committed load file or resolver differs from a fresh render of the current model | **Block planning** for that model: DDB-4xx, run `dbdatabuild project compile` and commit |
| any | declared model x target x operation pair has no supported rendering | **Unsupported**: DDB-3xx, named pair in the message |
| any | required runtime parameter has no value | **Question** (`Q-param-...`), answered interactively or via answers file |
| any | watermark resolver returns NULL | Per declared `on_null`: question, or the committed initial literal |
| any | resolver returns other than one row and one column, or the wrong type | **Unsupported**: DDB-2xx |
| any | resolver value at `apply` differs from the plan | **Stale plan**: refuse |
| any | upstream blocked or failed | skip downstream, log as skipped |

Populate the full table during implementation and keep it in `matrix/decision-table.yml`.

## 12. Tracking tables

**Where they live (as built).** Tracking is a setting, not a property of the connection it describes: `tracking: { connection: audit, schema: dbdatabuild }` at the project, overridable per connection
(`connections.<name>.tracking`: `none`, or another connection and schema name). **Nothing is tracked unless a project says where**; a connection that is written and has no tracking earns a warning (DDB-232) on `plan` and `apply`,
and `tracking: none` is the explicit choice that does not. The tracking connection can be another connection than the one the data is on (central tracking): it is opened with its own read and write logins, and a connection
that is only read is never tracked. **Every record names the connection it is about**: each table has a `connection` column, part of the key where a key has one (layout version 4; an older layout cannot be upgraded in place and is
refused by name), so one tracking connection holds the records of any number of connections and `metadata_current` over it is one catalogue. `init --connection <tracking connection>` creates the tables once there (once per
connection that keeps records, however many connections write to it); a connection that keeps no records has nothing to initialize. `plan` reads the live shapes from the data connection and the records from the tracking one;
`apply` holds a session and a gate for each (the data gate runs the steps, the tracking gate records them: "started" before and the outcome after, as before), checks both before the first statement, and takes the
application lock on the data connection. `ack`, `report` and `publish-metadata` work on the records through the tracking connection and refuse a connection that has none.

**With no tracking** the tool cannot detect drift (there is no baseline: a change made outside it is indistinguishable from a model change), so plans are made from the declared shape against the live one, an object that exists is
neither adopted nor blocked, and **every DDL change to an existing object is marked risky** (an added index is not); an incremental model is not blocked when its query changes, `ack`, the column history, `report` and
`publish-metadata` have nothing to work with, and an interrupted apply cannot be resumed. Planning and applying otherwise work. A copy's staging table is created under the tracking schema name on the destination whether or not
the connection is tracked.

A dedicated schema name (default `dbdatabuild`). Append-only by convention and permission. **[VERIFY]** each construct on Fabric. The T-SQL below is illustrative: each target generates its tracking-table DDL from one logical definition (column names, logical types, keys), since `nvarchar(max)`, `IDENTITY`, and `datetime2` have different equivalents on PostgreSQL.

```sql
CREATE TABLE dbdatabuild.schema_version (
  object_name      nvarchar(512) NOT NULL,
  shape_hash       char(64)      NOT NULL,
  physical_hash    char(64)      NULL,
  first_seen_utc   datetime2(3)  NOT NULL,
  source           varchar(32)   NOT NULL,   -- tool | out_of_band
  plan_id          varchar(128)  NULL,
  git_commit       varchar(64)   NULL
);
CREATE TABLE dbdatabuild.ddl_log (
  ddl_id bigint IDENTITY PRIMARY KEY, object_name nvarchar(512), statement_hash char(64),
  statement_text nvarchar(max), hash_before char(64), hash_after char(64),
  invoker nvarchar(256), plan_id varchar(128), git_commit varchar(64), executed_utc datetime2(3), status varchar(16)
);
CREATE TABLE dbdatabuild.run_log (
  run_id uniqueidentifier, step_id varchar(64), model nvarchar(512), operation varchar(32),
  rows_affected bigint, status varchar(16), plan_id varchar(128), git_commit varchar(64),
  definition_hash char(64), shape_hash_start char(64), shape_hash_end char(64),
  load_name varchar(128), load_file_hash char(64), resolver_file_hash char(64),
  parameters nvarchar(max),   -- runtime parameter values (all logged, section 6.6)
  watermark_used nvarchar(128), started_utc datetime2(3), ended_utc datetime2(3)
);
CREATE TABLE dbdatabuild.operation_interval (
  model nvarchar(512), run_id uniqueidentifier, range_start nvarchar(128), range_end nvarchar(128),
  shape_hash char(64), operation varchar(32)     -- load | backfill
);
CREATE TABLE dbdatabuild.block_log (
  block_id bigint IDENTITY PRIMARY KEY, model nvarchar(512), code varchar(16), detail nvarchar(max),
  created_utc datetime2(3), ack_by nvarchar(256) NULL, ack_reason nvarchar(max) NULL, ack_utc datetime2(3) NULL
);
CREATE TABLE dbdatabuild.migration_log (
  plan_id varchar(128), plan_hash char(64), plan_text nvarchar(max), git_commit varchar(64),
  applied_by nvarchar(256), applied_utc datetime2(3), hash_before char(64), hash_after char(64), status varchar(16),
  lane varchar(128) NULL, project_hash char(64) NULL   -- layout 5: deploy or refresh; the structure the compiled project expected (a deploy)
);
```

**Layout 5** (`migration_log.lane`, `migration_log.project_hash`): a row of `migration_log` is an event (a deploy or a refresh run, with its own id; a refresh is `ref-<utc>-<4 hex>`). `init` never alters a table, so on a store of layout 4 it does not record version 5 until `init --upgrade --apply` has added the two columns; commands refuse the store (DDB-505) until then.

**Layout 6** (the table `object_info`): the title, description, tags and properties of the project, its connections, its models and sources, their columns and indexes, one row each, written by `connection publish` (and the deploy's `metadata.store_on_apply`) from the documents it stores (6.5.8). Columns: `connection`, `subject_kind` (`project`, `model`, `source`), `subject`, `element` (`''`, `column:x`, `index:x`, `connection:x`), `item` (`title`, `description`, `tag`, `property`), `item_key` (a tag or a property key; `''` for the other two), `value_type` (`text`, `number`, `boolean`, for a property), `value` (as text), `recorded_utc`; the key is the first six. A layout 5 store lacks only this table: `init` creates it (it alters nothing, and records version 6 once the table is there), and `init --upgrade` does the same after its own steps. The deployed objects themselves carry none of it.

### 12.1 Hashes

- **`shape_hash`**: column names, types, lengths, precision, scale, nullability, collation, computed definitions. Sorted by name; ordinals stored separately (the tool always emits explicit column lists).
- **`physical_hash`**: indexes, partitioning, compression. Tracked separately from shape.
- **`definition_hash`**: hash of the normalized DuckDB-dialect AST of the model body, so comments and formatting changes don't register. Also store per-target rendered-SQL hashes.

### 12.2 Capturing out-of-band DDL

- Tool-issued DDL: logged before execution, updated with outcome and resulting hash.
- Out-of-band changes (all targets) are **detected by catalog hashing**. Every `plan` and `apply` takes a catalog snapshot, computes the shape and physical hashes, and compares them with the last recorded `schema_version`. A mismatch is recorded as `out_of_band` and blocks per the decision table. The tool does **not** install DDL triggers or Extended Events sessions.
- Who changed what, with statement text, is a database-audit concern. For SQL Server, SQL Server Audit configured by DBAs is the authoritative record (documented in the operations guide). **[VERIFY]** equivalents on Fabric and PostgreSQL.

### 12.3 History consistency report

Joining `schema_version` to `operation_interval` and recorded history dispositions yields per-column reports such as: "added in schema version 7; intervals before it loaded with NULL; not backfilled; acknowledged by X: <note>". An unacknowledged inconsistency may be configured as a warning or as a block for downstream models, using lineage. **As built (entry 100):** `plan` follows column lineage from each unacknowledged inconsistency (DDB-443) through any number of models to the models it is about to load; such a model gets DDB-240, at the severity of `policy.severity.history_inconsistency` (a warning by default; `error` refuses the plan until the history is backfilled or acknowledged with `ack history`; `note` quiets it). The model that owns the column is not reported against itself.

## 13. Validation layers

Cheapest to strongest:

1. **Config and model validation** (JSON Schema plus semantic checks). Reports all problems in one pass.
2. **Matrix lint** per declared target.
3. **ScriptDOM parse** of rendered T-SQL (syntax only).
4. **Schema-only compile** on the test engine: deploy empty tables from the declared schema, then run `sp_describe_first_result_set` on each rendered SELECT and compare result column names/types with the model's declared `columns` and with DuckDB's result types. Treat "cannot describe" results as *not checkable*, not as errors **[VERIFY]** on Fabric.
5. **Differential data test**: run the same synthetic data through DuckDB and through the target test engine, compare results (section 15.3).

## 14. Diagnostics

### 14.1 Format

```
error DDB-214  marts/fct_orders.yml:3
  Kind incremental_by_unique_key requires a unique_key, but none is set.
  Supported: unique_key: [<column>, ...]
  Fix: add under `kind:`  unique_key: [order_id]
  Docs: dbdatabuild help code DDB-214
```

- Stable codes (`DDB-1xx` config/model definition, `DDB-2xx` model semantics, `DDB-3xx` matrix/portability, `DDB-4xx` planning/decision table, `DDB-5xx` apply/runtime, `DDB-9xx` internal).
- Each diagnostic: code, location, what was found, what is supported, suggested fix, docs pointer.
- `docs/diagnostics/` is generated from the same metadata the validator uses.

### 14.2 Internal errors and data safety

- Unhandled exceptions are caught at the top level and reported as **internal error (tool bug)**: what the command was doing, which statements already ran (from the statement log), and what state the target is in. A stack trace is never the primary output.
- A **database exception** that no command handled is not a tool bug: it is reported as DDB-242 (exit 1), with the exception's type and its error number (SQL Server's `Number`, PostgreSQL's `SqlState`) and never the driver's message, which can quote a server name, a login or a value (entry 105, 113). The library's own default handler, which prints the exception with its message and stack, is switched off so that nothing bypasses this.
- Full detail goes to a local log file **with values scrubbed**.
- **SQL Server error messages contain data values** (conversion failures, duplicate keys). Runtime diagnostics must keep error numbers, states, and object names, and **strip message text that may contain values**. A test feeds known data-bearing errors and asserts the values never appear in output or logs.
- Logs, plans and default output hold no row values, statistics histograms or execution plan literals, so an agent never needs data to work (principle 8). Declared runtime parameter values and resolver results are recorded because the load needs them (section 6.6). A command that can show values (`diff --show-values`, section 9.10) does so only when asked, only in its own output, and the statement log holds the SQL, never a result.

## 15. Testing

### 15.1 Test engines

- The tool creates an **ephemeral database per run** (`CREATE DATABASE tmp_<runid>`, marker written so the tool can identify databases it created, with the target collation; partial containment where it avoids tempdb collation conflicts **[VERIFY]**) and drops it afterwards.
- `ITestEngine` implementations: existing instance via connection string (Developer edition installed natively on Windows or Linux), LocalDB (quick checks; Windows only; Express-engine limits), and optional Testcontainers. **Containers must never be required.** PostgreSQL follows the same pattern: a natively installed server, an ephemeral database per run (`CREATE DATABASE`), accessed via Npgsql.

### 15.2 Synthetic data

- DuckDB SQL scripts generate data: deterministic (fixed seed), committed, diff-friendly.
- A single schema source (the models' declared `columns` and source descriptors) generates both DuckDB DDL and target DDL. The type mapping lives in the matrix.
- **Edge-case packs**: trailing spaces, mixed case, empty string vs NULL, non-ASCII text, max-precision decimals, boundary dates, duplicate keys, late-arriving rows.
- Loading into the test engine: read from DuckDB and `SqlBulkCopy` into ephemeral tables.
- **`dbdatabuild project sample` (as built)** uses the same idea for people: it fills every source a model reads from its declared columns (`SampleGenerator`: deterministic from a seed; NULLs in nullable columns; strings that differ only by case, accent or trailing space, empty strings, quotes; decimals on rounding boundaries; doubles with inexact text; month-end dates; unique grain columns, with a composite grain unique as a combination and its first columns repeating so joins and GROUP BYs find matches), or from `<schema_name.table_name>.csv` files in `--data <dir>`, then runs the selected models and everything they read in dependency order as `CREATE TABLE ... AS <query>` in an in-memory DuckDB with external access off, so a downstream model reads what its upstream produced. A type with no generator is refused with the way out (supply rows). It answers what a query returns on data like this, offline; it is not a differential test and says nothing about a target engine.

### 15.3 Differential comparison rules

Canonical row ordering or set hashing; exact decimal comparison; timestamps truncated to target precision; explicit NULL handling; collation-sensitive tests run under both case-sensitive and case-insensitive settings.

### 15.4 Seed conformance constructs

String comparison semantics (full case list in section 7.4), integer vs decimal division, `CAST` to VARCHAR without length, Unicode literals, NULL ordering, boolean-as-predicate vs `bit`, `DATE`/`TIMESTAMP` precision, `DECIMAL` result precision, `LIMIT`/`OFFSET` vs `TOP`/`OFFSET FETCH`, `QUALIFY`, `DATE_TRUNC`, `LIST`/`STRUCT`/`UNNEST` (expected unsupported), `COALESCE`/`IFNULL`, string functions (`LENGTH`, `SUBSTR`, `||`), `REGEXP`, window functions, `GROUP BY` ordinal, `ILIKE`, date arithmetic, integer overflow behavior. Target about 40 first.

### 15.5 Invariant and behavior tests

- **Zero writes**: every read-only command against a deliberately drifted database performs no writes.
- **Mutation gate**: enumerate call paths to `ExecuteNonQuery`; all must route through the gate.
- **Effect scope**: each command's recorded statements stay within its declared class and object scope.
- **Plan golden files** for every decision-table cell; **dry-run golden files** per command.
- **Stale plan** rejected; **hand-edited plan** rejected; **plan idempotency** (a second plan after apply is empty).
- **Out-of-band drift** blocks; **concurrent run** blocked by the application lock; **partial failure + resume** behaves as specified.
- **Diagnostics**: one fixture per code asserting message, location, and fix; a malformed-config fuzz set asserts no unhandled exception.
- **Questions**: same scenario yields the same question IDs; an answers file fully resolves them.
- **Definition generation** (`dbdatabuild project model update`): golden files for generated definitions; idempotency (second run yields no diff); comments, key order, and hand formatting preserved on update (minimal splices); the `.sql` file byte-identical before and after; unknown keys, duplicate keys, anchors/aliases, and custom tags rejected; orphan `.sql` or `.yml` files reported; stale-file write refused; `--check` writes nothing and fails on out-of-sync definitions; zero database connections during the command.
- **Load operations**: golden files for every rendered operation and resolver; `dbdatabuild project compile --check` fails on any difference and writes nothing; **the executed statement text equals the committed file text** (parameters bound, never interpolated; asserted from the statement log); only declared value parameters appear as placeholders (no identifier substitution of any kind); resolver contract violations (multiple rows, wrong type, NULL) produce the declared outcome; resolver value changing between plan and apply is refused; `max_span` and type constraints enforced; each strategy's result on the test engine is compared with its **DuckDB reference implementation** on synthetic data, including rerun idempotency and late-arriving rows.
- **Data safety**: error-message scrubbing test (section 14.2).

### 15.6 Seeds (as built)

A source table can have a **seed**: `seeds/<schema name>/<table>.sql`, one DuckDB query that returns the table's rows. The query is authored, relational and deterministic: it reads the variables `seed` and `scale` (`getvariable('seed')`, `getvariable('scale')`) and the macros in `seeds/macros.sql` (`rnd(i, salt)`, `pick(i, salt, n)`: a hash of the row number and a salt, so the same seed makes the same rows on every platform). A seed may read other seeds' tables (customers before orders); the order is found from the table names a query mentions, and a cycle is refused.

`dbdatabuild project seed` runs the seeds into `.dbdatabuild/seed.duckdb` (`--seed`, `--scale`); `sample` loads the seeded sources first and falls back to the type-driven generator (15.2) for a source with no seed. The same SQL is the one that loads a real target (a source query is how data enters any database): DuckDB to DuckDB comes first, SQL Server and PostgreSQL destinations are built on it. CSV is not a format of its own: it is a DuckDB `read_csv` in a seed query.

### 15.7 Project templates (as built)

`templates/<name>/` is embedded into the executable and `dbdatabuild project create [template] [dir]` copies one out (no arguments lists them; `.template` holds the one-line description and is not copied). A template is a complete project: config, sources, seeds, models, tests and a README. `starter` (three sources, staging, two marts), `retail` (twelve sources, a star schema with allocation, splitting, mapping and aggregation), `chinook` (a music store: a hierarchy, a text column split into rows, cohorts, rankings) and `adventureworks` (twenty-three sources: slowly changing dimensions, as-of lookups, a recursive parts explosion, quotas, quintiles) exist, and two that show what the others do not: `stores` (four connections: two shop systems on PostgreSQL copied by a fan-in copy with a slice into a SQL Server warehouse and a PostgreSQL reporting database, one report built on both engines, and a refresh plan that lists the copy under `excluded`) and `macros` (a scalar macro, table macros, a type alias, one macro serving a live table and its history, and `NAME` parameters; DESIGN.md 6.5.5). A unit test takes every template through `new`, `validate` with no warning, `seed`, `sample` of every mart, `model update --check`, `test` and `render`; the `templates` conformance group builds `starter`, `retail`, `chinook`, `adventureworks` and `macros` on both engines and compares every mart with DuckDB's rows, and builds `stores` across SQL Server and three PostgreSQL databases (the origins' rows grouped by the test against both engines' report). Each README walks the deploy, refresh, status and monitor cycle with output from a real run; the lifecycle settings (`plans`, `refresh`, `retention`) are a commented block in every template's `dbdatabuild.yml`. Templates are SQL Server first (a PostgreSQL profile is in a comment, because no single string profile fits both engines, DDB-310). What each template found is in `docs/research/template-findings.md`: every problem with a query, a translation or an engine, as fixed, refused or cataloged. The shapes of the data follow the Chinook (MIT) and AdventureWorks (Microsoft Public License) sample databases; the rows are generated and nothing is copied.

`dbdatabuild connection seed [--connection t] [--seed n] [--scale n] [--replace] [--apply]` (effect: target writes) puts the seeded sources on a SQL Server or PostgreSQL sandbox: it runs the seeds in DuckDB, creates each source table from its descriptor (the target DDL type table, so a source type maps the way a model column does) and fills it in batches of `INSERT ... VALUES` with a bound parameter for every value (at most 2,000 parameters and 1,000 rows a statement, the SQL Server limits). Without `--apply` it prints the statements it would send and connects to nothing. It stops at the first table that already exists (`--replace` drops and recreates it), writes only tables the project's sources name, and goes through the mutation gate on the write login; the statement log has the statement text and the number of values, not the values (the rows are a function of the seed and the scale). The conformance suite takes every template through `new`, `load-seeds`, `render`, `init`, `plan`, `apply` on both engines and compares each mart with the same model run on the same seeds in DuckDB.

## 16. Milestones

1. **Spike (time-boxed)**: C# harness binding polyglot via FFI. Run about 40 constructs DuckDB to T-SQL through the conformance runner against a native test instance. Include PostgreSQL as a second render and differential target, since it is cheap at this stage and shows which matrix rows and assumptions are really T-SQL-specific. Outcome: confirm DuckDB-canonical is viable, seed the matrix, record polyglot gaps.
2. **Foundations**: project model, strict YAML loader (duplicate-key detection, comment-preserving edit support), JSON Schemas, diagnostics framework (`validate`, `explain`), matrix loader and linter, `render`, ScriptDOM check, the question/answer framework core, and `dbdatabuild project model update` (which needs DuckDB-based query description, so it may follow milestone 3's DuckDB runner).
3. **Test infrastructure**: ephemeral database manager, synthetic data runner, differential runner, schema-only compile (`sp_describe_first_result_set`).
4. **State and safety core**: tracking tables (`init`), hashes, read/write login split, mutation gate, statement log, invariant tests.
5. **Planning**: plan-specific questions (building on the question/answer framework from milestone 2), decision table, plan document and file format, `plan`, `check`, stale-plan and integrity checks.
6. **Apply**: `apply` (with dry-run, resume, risk classes), VIEW and FULL kinds, `ack`, `report`.
7. **Incremental kinds and load operations** (section 6.6, including `dbdatabuild project compile`, `dbdatabuild project show loads`, resolvers, and parameters): INCREMENTAL_BY_UNIQUE_KEY, then INCREMENTAL_BY_TIME_RANGE, backfill scope in `plan`, `run` shorthand.
8. **PostgreSQL target** (DDL, loads, apply, tracking tables), started right after milestone 6 if capacity allows because it exposes T-SQL assumptions in shared code; then the **Fabric target**, matrix expansion, and operations guidance for out-of-band DDL statement capture with SQL Server Audit (the tool itself detects drift by catalog hash).
9. **Hardening**: fuzzing, error-scrub tests, packaging (single-file publish), docs generation, SQL Agent invocation guide.

Each milestone ends with its tests green and this document updated.

## 17. Open decisions and verifications

**Decisions made** (reflected in the sections noted)

1. `dbdatabuild connection refresh` shorthand is kept: plan + apply, only for routine loads, refusing if the plan contains DDL or open questions (section 9.1).
2. FULL kind loads use transactional delete+insert. Table swap can be added later as a named load operation with its own step type and risk class (sections 6.1 and 6.6).
3. Out-of-band DDL is detected by catalog hashing on every target. The tool does not install triggers or Extended Events sessions. Statement-level capture is SQL Server Audit, configured by DBAs (section 12.2).
4. `columns` is required for all models (section 6.2).
5. Default string-comparison profile: case-insensitive, accent-sensitive, trailing spaces ignored (section 7.4).
6. All runtime parameter values and resolver results are logged. Redaction can be scoped and added later (section 6.6).
7. `dbdatabuild project model update --accept-inferred` exists, limited to high-certainty proposals (section 6.5).
8. The DuckDB-side `rtrim()` emulation rewrite is used for offline runs (section 7.4).
9. Model definitions are YAML files beside plain `.sql` bodies, with no custom grammar and no macros. Load strategies wrap the body (section 6).

**[DECIDE]** None open at the moment.

**[VERIFY]** (do before depending on them)
- polyglot-sql: **spike done for 47 constructs (`spike/RESULTS.md`)**: FFI binding and pinned build work; fidelity gaps found, including silent wrong rewrites, `fabric` dialect gaps, and an incomplete `unsupportedLevel: raise`, so the matrix linter must walk the AST itself. Still open: round-trip stability for comment-preserving substitution, pre-1.0 API churn, Windows build and single-file publish, native distribution plan.
- DuckDB plan lowering: **built** (section 7.6; research in `docs/research/duckdb-plan-lowering/README.md`). Open: target-specific rules and `sum` widening, listed in `docs/progress/OPEN-ITEMS.md`.
- ScriptDOM: available parser versions; Fabric-specific syntax coverage.
- Fabric Warehouse: MERGE/ALTER/TRUNCATE/rename support, trigger support, extended properties, query history retention.
- `sp_describe_first_result_set`: limits (temp tables, dynamic SQL) and Fabric availability.
- Tracking-table constructs on Fabric (identity columns, constraints, `nvarchar(max)`).
- Collation behavior (section 7.4): DuckDB `NOCASE` under `GROUP BY`, `DISTINCT`, joins, and window operations; behavior of chained collations under `GROUP BY`, `DISTINCT`, joins and windows (equality chaining `NOCASE.NOACCENT` is verified on DuckDB 1.5.4 in either order); Fabric Warehouse default and available collations, and its trailing-space and `LEN` semantics.
- PostgreSQL target: polyglot DuckDB-to-Postgres fidelity; offline syntax validation options (a libpg_query binding for .NET, or polyglot validation); schema-only compile via prepare/describe; advisory-lock and tracking-table DDL equivalents; `MERGE` availability by version; identifier case-folding and quoting.
- Load operations: `@name` placeholders are bound by SqlClient and Npgsql, and ScriptDOM parses parameterized scripts (both verified by the conformance suite and unit tests). Still open: Fabric support for the staging, delete+insert and merge templates inside transactions. Predicate pushdown is decided (advice, never a rewrite): section 6.6.2.
- YAML handling: `YamlDotNet` duplicate-key detection and parser source marks for comment-preserving splice edits; editor association of the JSON Schemas by file glob.
- Licenses of all native and managed dependencies.
