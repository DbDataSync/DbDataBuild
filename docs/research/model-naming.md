# Where a model's schema name and object name come from

Status: option A (`model_layout`, `DESIGN.md` 6.5.6) and then the head of the query file (option B, in the owner's chosen `WITH (...)` form, `DESIGN.md` 6.5.7) are built. This note records the options, the reasoning and what is still open.

## The problem

A model's name is `schema name.object name` (`marts.fct_orders`). It identifies the model everywhere: references in queries, the dependency graph, plans, rendered files, tracking records, metadata. It used to be the **path** under `models/` (`models/marts/fct_orders.sql` is `marts.fct_orders`), and the `name:` in the definition file had to equal it (DDB-107). That dictates a folder structure, which the owner does not want to do: folders should be for the author's organisation, not for the tool.

## The options that were laid out

**A. The definition's `name:` decides.** Drop the requirement that the path is the name. The query file is still the file beside the definition (same folder, same stem). Nothing is declared twice. The name is not visible in the SQL.

**B. A statement in the query file decides.**

```sql
CREATE TABLE marts.fct_orders AS
SELECT ... FROM ...;
```

(`CREATE VIEW schema_name.object_name AS SELECT ...` for a view.) The SQL file says what it builds. The definition file still carries the rest, because `CREATE TABLE` does not say the kind or strategy (full, incremental by key or by time range, copy, native), the columns, the grain, the connections. So the statement would repeat only the name and table-versus-view, and the two must agree (mismatch is an error).

**C. Both allowed.** The statement is read when present and must agree with `name:`.

## Why B is wanted (the owner's reason)

Not DuckDB compatibility (the file does not need to run on its own). It is **clarity for a reader**: someone who opens a `.sql` file and does not know this tool should not have to guess what the file *is*. With `CREATE TABLE marts.fct_orders AS SELECT ...` the file says it builds a table of that name; with a bare `SELECT` it does not. This is a readability and onboarding argument, and it is the reason to come back to it even though A solves the folder problem.

## Decision

**A now**, with a project preference for how strictly the file names must follow the model's name:

```yaml
# dbdatabuild.yml
model_layout: folder      # folder | dotted | object | none
```

| `model_layout` | The file name must be | Example for `marts.fct_orders` |
|---|---|---|
| `folder` (default, as before) | the path is the name: `models/<schema name>/<object>.yml`, any depth (`a/b/c` is `a.b.c`) | `models/marts/fct_orders.yml` |
| `dotted` | `<schema name>.<object>.yml` in any folder | `models/finance/marts.fct_orders.yml` |
| `object` | `<object>.yml` in any folder; the schema name comes only from `name:` | `models/finance/fct_orders.yml` |
| `none` | anything; no check | `models/whatever/x.yml` |

The default is `folder` only because it is what every existing project already does; a project that does not want folders says `dotted`, `object` or `none`. In every layout the name is the definition's `name:` and a name belongs to one model (two files claiming one name is an error). The query file is always the file beside the definition (same stem, `.sql`; `.native.sql` for a native model). Selectors by name, by path and by directory work as before.

What the layout changes elsewhere:

- `define` names a model that has no definition yet from the file name when the layout can (`folder`, `none`: the path; `dotted`: the file name), and asks for a hand-written definition with `name:` when it cannot (`object`, or `dotted` with no schema name in the file name). A model that has a definition uses its `name:`.
- `import` writes a new mapped model where the layout says (`models/<schema name>/<table>.yml`, `models/<schema name>.<table>.yml`, `models/<table>.yml`); a table the project already has a mapped model for is written to that model's own file. (`object` can collide when two schema names have a table of one name: the second is not written until one is moved.)
- Metadata and `graph` show a mapped model's real file (`DeclaredModel.File`), not one computed from its name.
- Seeds and model tests are named by their own folders (`seeds/<schema name>/<table>.sql`, `tests/<schema name>/<model>.yml` or `model:` inside); they are not governed by `model_layout`.

## Still open (to settle when option B is revisited)

1. Is B for readability only (the owner's answer: yes), or should the statement also be able to carry more (for example `CREATE OR REPLACE`, `CREATE TABLE ... (columns)` replacing the `columns:` list)? Each addition removes something from the YAML and adds a second place that says it.
2. What a model built on several connections does about its schema name: the same everywhere, or a per-connection default (a schema-name setting for a connection, so a dev/prod split is a setting and not a name).
3. Copies and mapped models have no query file; they keep the definition's `name:` under every option.
4. If B is built, how an incremental model's `CREATE TABLE ... AS` is read (the strategy is not in the statement), and whether a view must be written `CREATE VIEW`, so the file can no longer be a bare `SELECT`. A bare `SELECT` should stay valid (compatibility, and short examples).

## Option B, built (2026-10-06)

The owner chose the general, commonly understood property list, `WITH (key = value, ...)`, over clause keywords (`INCREMENTAL BY UNIQUE KEY (...)`), and the rule that the head is the file's own layer: it wins over a folder's `defaults:`, and a kind said in the head and in the definition file is an error. A hand-written parser reads the head (polyglot has no dialect that knows these options, the head needs its own line and column errors, and it must load even when the query does not), and the query after `AS` is untouched.

```sql
CREATE TABLE marts.daily_sales
WITH (kind = 'incremental_by_time_range', time_column = sale_date, lookback = '3 days')
AS
SELECT ...
```

What stays open (items 1, 2 and 4 are **parked** by the owner, 2026-10-06, until they pick them up; the head keeps its four options meanwhile):

1. More options in the head (`grain`, `connections`, the operations of `loads:`): each removes a line from the YAML and adds a second place that says it. Only the four reload options are in the head.
2. A head for a native model (`.native.sql`, the engine's own text) and for copies (no query).
3. A per-connection **schema name** (a dev/prod split as a setting, not a name). **Parked** by the owner, 2026-10-06.
4. Whether `define` should be able to write a head into a query file that has none (today it never writes a `.sql`).
