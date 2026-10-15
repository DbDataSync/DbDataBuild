# Mapped and native models: why they are `SourceDescriptor` / `dbdatabuild.source`, and what the split costs (review of 2026-10-14)

Status: review (2026-10-14). **Outcome: built in entry 167** (see the end of this note): option (a), stage 2, no merge. Entry 165 fixed the metadata symptoms (a kind on both documents, `model_kind` for all seven).

## How it got this way

1. **2026-09-30 (`a2f7e6f`)**: `SourceDescriptor` is born as the declaration of "a table the project reads but does not build": name, declared columns, grain, connections. Queries bind against the **declared columns** in an empty DuckDB table, so nothing needs the table offline (`docs/research/native-queries.md`, first bullet of "What the code gives us"). It lived in `sources/`.
2. **2026-10-02 (`071fb1a`, entry 36)**: `import-sources` and the **`dbdatabuild.source/1`** document, subject kind `source`, views `metadata_source*`, tracking layout 3.
3. **2026-10-06 (`3352b38`, entry 69)**: "Mapped models replace sources". The *word* became `mapped` and the files moved into `models/`, but the entry says it plainly: "Not renamed yet: ... the metadata document kind `dbdatabuild.source/1` and the `metadata_source*` views; the C# names." They never were.
4. **2026-10-06 (`a8c40a9`, `db66e58`, entries 77 to 79)**: native models were added by giving `SourceDescriptor` an optional `Native` member (`NativeQuery`). The reason is the one in item 1: a native model also has declared columns, is never built, and is bound as an empty table. So the real dividing line in the code is **declared (columns written down, never created, bound as an empty table) versus built (has a query or origin the tool turns into DDL and loads)**, and `mapped` and `native` landed on the declared side. `copy` has no query either but is built, so it is a `ModelDefinition` (it goes through `ModelSource` after `CopyModels`).
5. **2026-10-14 (entries 163, 164)**: "model kind" became the one term and `kind` a plain string in model documents. Mapped and native were left with no kind and `model_kind` null "by decision"; the same entry named a `source/2` as the way out.

So the split is an accident of order (a table-shaped declaration came first, a new kind was hung on it) and not a design that says mapped and native are not models. `docs/models.md` and the CLI help say they are; `DESIGN.md` 6.5 says "a mapped model".

## What each side holds

| | Built: `ModelDefinition` (+ `ModelSource` for files) | Declared: `SourceDescriptor` |
|---|---|---|
| Kinds | view, full, incremental_by_unique_key, incremental_by_time_range, copy | mapped, native (`Native != null`) |
| Loader | `ModelDefinitionLoader` (654 lines), `YamlFieldReader` | `SourceDescriptorLoader` (`SourceDescriptor.cs`, own key list); dispatch is in `ProjectValidator.Validate` by `ModelKindShape.NameOf(kind)` after the layers are merged (about line 210) |
| Project collections | `ProjectValidationResult.Sources` (a list of `ModelSource`!), `.Models` | `.Descriptors` (mapped only), `.NativeModels`, `.FileDescriptors` (both, no generated), `.AllDescriptors` (both and the generated staging tables of copies) |
| Metadata document | `dbdatabuild.model/2` | `dbdatabuild.source/2` (was `/1`, no kind) |
| Subject kind (`metadata_current.kind`, `object_info.subject_kind`) | `model` | `source` |
| Editor schema | `model.schema.json` (1364 lines) | `source.schema.json` (453 lines); `model.schema.json` hands a file to it with if/then/else on an in-file `kind` |
| Tracked shape / drift | `schema_version` and the deploy check | the live check in `project update` / `ConnectionCapture`, `NativeShapeCheck`, `track_definition` for native routines |

## What the split buys

* Type safety where it matters: a built model always has a query, loads and hooks; nothing in the renderer, loader, lowerer or refresh planner has to ask "is this one real?". `ModelDefinition` is used in 25 files, `ModelSource` in 12; none of them can be handed a mapped model by mistake.
* A small descriptor that binds into DuckDB cheaply (`SampleRun`, `ModelLowering`, `ModelGraph` take `AllDescriptors`), and fan-in without invented loads.
* Different lifecycle is explicit: `define`, `render`, `refresh` and `apply` never see a mapped model.

## What it costs: the same concept handled two ways

Each item is a place where a tool or person inspecting the project sees mapped/native as something else than a model. "Fixed in 165" marks what this run changed.

**Metadata documents and stored tables**
1. Two document types with different shapes for the same properties: a model has `files {definition, query}`, `connections`, `inherited`, `loads`, `hooks`, `kind_settings`; a source has `file`, `consumers`, and (before 165) a `native` object instead of `kind_settings`. Columns differ too: model columns carry `native` types per connection, `duckdb_type` and `lineage`; source columns do not. (`MetadataBuilder.Model` / `Source`.)
2. `metadata_current.kind` is the *subject* kind (`model` or `source`) and `metadata_current.model_kind` is the model kind. A mapped model is `kind = 'source'`; this is the one place a tool must still special-case. `object_info.subject_kind` has the same two values and no model kind (join to `metadata_current` for it). `MetadataStore.Key(kind, subject)`, the table key `(connection, kind, subject)` and `MetadataInfoRows.HasRows` all carry the two spellings.
3. **Two columns named `kind` with different meanings.** `metadata_columns.kind` is the subject kind (`model`/`source`; `TrackingDdl` and `MetadataDatabase`), `metadata_models.kind` is the model kind. A rule that joins them confuses the two.
4. Dependency vocabulary is a third one: `depends_on_kind` / `upstream_kind` is `model` (five model kinds lumped), `mapped`, `native`, `macro`, `unknown`; `project show graph` node `kind` is `model`, `source`, `native`, `macro`, `unknown` (`GraphCommand.cs`, `output.schema.json` `data_project_show_graph`) and still says `source` for a mapped model. Fixed in 165: dependency rows and `upstream[].kind` say `mapped`. Not fixed: the graph node `kind`.
5. Views are doubled: `metadata_models` / `metadata_sources`, `metadata_indexes` / `metadata_source_indexes`, `metadata_source_foreign_keys` (a model has no foreign keys), column names `model` vs `source`. A rule over "all models" must union two views.
6. Before 165, `MetadataPublisher.Collect` iterated `ctx.Project.Descriptors` (mapped only, `.NativeModels` was apart), so **a native model had no stored document at all**, yet it was a subject in `metadata_dependency`. A direct consequence of four accessors for one set. Fixed in 165 (`FileDescriptors`).
7. Before 165, `model_kind` was null for both and `source/1` had `native` as an object where `model/2` has `kind_settings`. Fixed in 165.

**Output documents**
8. `project compile` and `project show metadata` return `data.models[]` and `data.sources[]` (two arrays, `output.schema.json` `data_project_compile`, `data_project_show_metadata`); the project document inside has `models[]` (name, kind, connections, hash) and `sources[]` (name, file, hash, `native`) with different keys. `project update` / `project model update` also separate `models` and `sources`.
9. `counts.models` and the line "OK: N model(s) valid" are `result.Sources.Count` (`CliApp.cs`, lines 488 and 498): built models only. A project of 30 models and 12 mapped ones says 30.

**Graph and selectors**
10. `ProjectContext.Select` (run, plan, apply, compile, ...) works on built models only: `kind:mapped` answers "no model has the kind `mapped`", and a `+model` selection that reaches a mapped table drops it (`chosen ... byName.ContainsKey`). `UpdateSelection.MatchDescriptors` re-implements the same grammar (`kind:`, `tag:`, `connection:`, `property:`, `path:`, name patterns) over descriptors for `project update`, where `kind:mapped` and `kind:native` work. Two copies of the grammar will drift.
11. `DependencyGraph(models, sources, edges)` calls mapped and native "sources" and `GraphCommand.KindOf` rebuilds the model/native/macro/source distinction from four dictionaries.

**Interfaces**
12. The TUI "Models" window lists `data.models` only (`ModelsWindow.cs`): mapped and native models are not shown. The web page lists them under a second heading "sources", with a badge that said `source` (now the kind).
13. `docs/models.md` has one table for seven models; the metadata, the counts, the TUI and the selectors have two classes.

**Drift and terms**
14. Drift differs by design (built: `schema_version` shape hash; mapped: live comparison; native: `track_definition`), but it is reported through different commands and documents (`status` objects vs `project update` `sources[]` vs `connection deploy` checks of copy origins). A tool asking "which models differ from their database" has to run three.
15. Terms: `source` still means the retired `sources/` folder, `SourceDescriptor`, `source.schema.json`, `dbdatabuild.source/N`, the subject kind, `metadata_source*`, the JSON key `sources` **and** `ModelSource`, which is a *built* model's files (`ProjectValidationResult.Sources` is the built models, `Descriptors` the "sources"). `kind: table|view` in `project import` output is a fourth use of "kind" (the database object's). The user-facing rule "one term per concept" is broken in the code and the documents, not in the docs.

## Options

**(a) Keep two internal types, present one model surface.** Done in 165 for the documents: `kind` and `kind_settings` on both, `model_kind` in all seven values, dependency kinds `mapped`/`native`. Still to do for a full surface, each independent:
* One subject kind `model` in `metadata_current.kind` and `object_info.subject_kind` (so only `model_kind` tells the sort). Needs layout 9: `UPDATE ... SET kind = 'model' WHERE kind = 'source'` on `metadata_document` and `object_info` (`kind` is part of the table's key, but names are unique across mapped, native and built models, so the update cannot collide), the views, the four templates' rules, `metadata_columns.kind` (becomes `model_kind`, a rename: breaking for user rules), `MetadataStore.Key`. Cost: about a day plus both engines; risk: breaks any user rule or BI query that filters `kind = 'source'`; the document type name `dbdatabuild.source/2` stays, or becomes `dbdatabuild.model/3` with all seven.
* A common read-only projection (`ProjectModel`: name, model kind, columns, grain, connections, info, file) built from both types, used by selectors, graph, counts, TUI and the output documents, so those stop knowing there are two classes. Cost: one new record and about ten call sites (items 8 to 12); risk low (additive).
* One selector grammar over the projection (removes the copy in `UpdateSelection`).

**(b) Merge the types.** `ModelDefinition` gets `Native`, columns without a query, and all the places that assume a query (renderer, lowerer, refresh planner, 25 files) must check "built" first; or `SourceDescriptor` grows to hold indexes, loads, hooks, renames. Counting references: `SourceDescriptor` 56 lines in 19 files, `ModelDefinition` 68 lines in 25 files, `ModelSource` 48 lines in 12, plus the four accessors on `ProjectValidationResult` (about 40 call sites), the two loaders (about 950 lines), the two schemas (about 1800 lines) and the document types. A week or more, with fuzz and golden churn, and a real risk of a mapped model reaching code that assumes loads. It buys nothing the projection in (a) does not, except one loader and one schema.

**(c) Rename inside, keep the split.** Rename `ModelSource` (the built model's files) to something that is not "source" (`BuiltModel`), `SourceDescriptor` to `DeclaredModel`, `Sources`/`Descriptors` to `BuiltModels`/`DeclaredModels`, `source.schema.json` to a name with "declared" or "mapped", and the document `dbdatabuild.source` to `dbdatabuild.declared`... Fixes item 15 only. Mechanical but touches the metadata document name and the `metadata_source*` views (user-visible).

## Recommendation

**(a) in two stages, no merge.** Stage 1 is entry 165 (kind on both documents, `model_kind` and dependency kinds right, native models published). Stage 2, one run, at the next layout bump: the `ProjectModel` projection and one selector grammar (items 8 to 12, additive), and then the single subject kind `model` with layout 9 (items 2, 3, 5), taking the break in user rules at once and saying so in the release notes. Do (c) with it only if the owner wants the word "source" gone from the C# names; it is not needed for consistency of the surface. Revisit (b) only if a third declared kind appears or the two loaders keep drifting; the first sign would be a setting added to one loader and forgotten in the other (`WarnOldKindForm` is already called from both branches).

## Decisions for the owner

1. Go ahead with stage 2 (projection plus selector grammar, then subject kind `model` in layout 9)? The break is `metadata_current.kind = 'source'`, `metadata_columns.kind` and the view names `metadata_source*`.
2. Keep the graph node `kind` value `source` for a mapped model (the released JSON of `project show graph`) or change it to `mapped` like the dependency rows? Changed in 165 only for dependency rows and `upstream[].kind` (which were unreleased in layout 8 / `model/2`).
3. Whether `depends_on_kind` should say the model kind (`view`, `full`, ...) for built models instead of `model`: more precise, one vocabulary with `model_kind`, but it breaks `upstream_kind = 'model'` rules, which are probably the most common ones.
4. Merge: not recommended; if wanted, it needs its own plan.

## Outcome (entry 167, 2026-10-14)

The owner said: consistency, not compatibility; carry stage 2 through. Built, item by item against the list above:

* Items 1 and 5 (two documents, doubled views): one document, `dbdatabuild.model/3`, for all seven kinds; a declared model's document leaves out what it does not have. One set of views: `metadata_models`, `metadata_indexes`, `metadata_foreign_keys`; `metadata_sources`, `metadata_source_indexes` and `metadata_source_foreign_keys` are gone.
* Items 2 and 3 (subject kind, two columns named `kind`): the subject kind is `model` for every model; the column is `subject_kind` (as on `object_info`) in `metadata_document` and `metadata_current`; `metadata_columns.kind` is `model_kind`. Tracking layout 9 with a tested upgrade from layout 8.
* Item 4 (dependency vocabulary): the graph node has `category` (`model`, `macro`, `unknown`) and the model `kind` (a mapped model is `mapped`); the dependency rows get the same two columns in the next entry.
* Item 6 and 7: already fixed in entry 165.
* Items 8 and 9 (two arrays, counts): `data.models[]` for every kind in `project compile` and `project show metadata`; `counts.models` and the text count all seven, with `by_kind`.
* Items 10 and 11 (two selector grammars, `DependencyGraph` calling mapped models sources): `ModelSelection` over `ProjectModel`s, once; `UpdateSelection` calls it; the graph's unbuilt nodes are the declared models and the macros. (The refresh's three-term copy over `loads.yml` goes in the run's subject 5.)
* Item 12 (TUI and web): both list all seven kinds.
* Item 13 (docs): `docs/models.md` and DESIGN.md say one thing.
* Item 15 (terms): `ModelSource` is `BuiltModel`, `SourceDescriptor` is `DeclaredModel`, the project collections are `BuiltModels`, `MappedModels`, `NativeModels`, `DeclaredModels`; `ImportSourcesCommand`, `SourceImport`, `SourceTypes` and the editor schema lost the word; `kind: table|view` of an imported object is `object_kind`. `source` stays for a data source (a resolver's, a parameter's, `SourceCatalogReader`).
* Item 14 (drift reported through three commands) is untouched: it differs by design.

Not merged: the two classes are still two (option (b)); the review's estimate stands. The decisions asked at the end were answered by the owner's direction (go ahead; the node kind is `mapped`; the model kind as the dependency kind is subject 2).

