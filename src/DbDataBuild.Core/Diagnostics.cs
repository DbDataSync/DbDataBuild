using System.Text;

namespace DbDataBuild.Core;

public enum Severity { Error, Warning, Note }

/// <summary>Static metadata for one diagnostic code. The catalog of these is data; docs are generated from it.</summary>
public sealed record DiagnosticDescriptor(
    string Code,
    Severity DefaultSeverity,
    string Title,
    string Supported,
    string Fix,
    string Explanation);

public readonly record struct SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => Line > 0 ? $"{File}:{Line}" : File;
}

/// <summary>One reported problem. <see cref="Found"/> says what was found; supported/fix come from the descriptor unless overridden.</summary>
public sealed record Diagnostic(
    DiagnosticDescriptor Descriptor,
    SourceLocation Location,
    string Found,
    string? Supported = null,
    string? Fix = null,
    Severity? SeverityOverride = null)
{
    public string Code => Descriptor.Code;

    /// <summary>The descriptor's default, unless project policy (dbdatabuild.yml) raised or lowered it.</summary>
    public Severity Severity => SeverityOverride ?? Descriptor.DefaultSeverity;
}

public static class DiagnosticCatalog
{
    private static DiagnosticDescriptor E(string code, string title, string supported, string fix, string explanation) =>
        new(ProductInfo.DiagnosticPrefix + code, Severity.Error, title, supported, fix, explanation);
    private static DiagnosticDescriptor W(string code, string title, string supported, string fix, string explanation) =>
        new(ProductInfo.DiagnosticPrefix + code, Severity.Warning, title, supported, fix, explanation);
    private static DiagnosticDescriptor N(string code, string title, string supported, string fix, string explanation) =>
        new(ProductInfo.DiagnosticPrefix + code, Severity.Note, title, supported, fix, explanation);

    // 1xx: config / model definition
    public static readonly DiagnosticDescriptor YamlSyntax = E("101", "YAML syntax error",
        "Well-formed YAML in the strict subset (see DESIGN.md section 6.3).",
        "Correct the syntax at the reported position.",
        "The file could not be parsed as YAML, so nothing in it could be validated.");
    public static readonly DiagnosticDescriptor DuplicateKey = E("102", "Duplicate key",
        "Each key at most once per mapping.",
        "Remove or merge the duplicate key.",
        "Duplicate keys are ambiguous (parsers disagree on which wins), so they are always errors.");
    public static readonly DiagnosticDescriptor UnsupportedYamlFeature = E("103", "Unsupported YAML feature",
        "Plain mappings, sequences, and scalars. No anchors, aliases, merge keys, or custom tags.",
        "Write the value out in full.",
        "The loader accepts a strict YAML subset so definitions mean the same thing in every editor and parser.");
    public static readonly DiagnosticDescriptor UnknownKey = E("104", "Unknown key",
        "Only the documented keys for this section.",
        "Remove the key, or correct its spelling.",
        "Unknown keys are errors, never ignored, so a typo cannot silently drop a setting.");
    public static readonly DiagnosticDescriptor MissingKey = E("105", "Missing required key",
        "All required keys for this section.",
        "Add the missing key.",
        "A required key is absent from the definition.");
    public static readonly DiagnosticDescriptor InvalidValue = E("106", "Invalid value",
        "A value of the documented shape and enumeration.",
        "Use one of the supported values.",
        "All scalars are read as strings and validated against the documented shape. Nothing is coerced by YAML type rules.");
    public static readonly DiagnosticDescriptor NameMismatch = E("107", "Model name does not match path",
        "name equals the path under models/ with '/' replaced by '.' (models/marts/fct_orders.yml is marts.fct_orders).",
        "Change `name:` or move the file.",
        "The explicit name and the path convention must agree.");
    public static readonly DiagnosticDescriptor OrphanFile = E("108", "Orphan model file",
        "Every .sql has a .yml with the same base name, and vice versa.",
        "Run `dbdatabuild project model update <path>` to create the missing definition, or remove the orphan.",
        "A model is a pair of files. One without the other cannot be built.");

    public static readonly DiagnosticDescriptor ConfigNotFound = W("109", "Project configuration file not found",
        $"A {ProductInfo.ConfigFile} at the project root.",
        $"Create {ProductInfo.ConfigFile} (see schemas/config.schema.json), or accept the built-in defaults printed in the command header.",
        "Without a configuration file the built-in defaults apply. They are printed in every command header so they are never hidden.");

    // 2xx: model semantics
    public static readonly DiagnosticDescriptor MissingUniqueKey = E("214", "Missing unique_key",
        "unique_key: [<column>, ...]",
        "Add `unique_key:` under the kind (`kind: {incremental_by_unique_key: {unique_key: [order_id]}}`).",
        "Kind incremental_by_unique_key upserts by key and cannot run without one.");
    public static readonly DiagnosticDescriptor MissingTimeColumn = E("215", "Missing time_column",
        "time_column: <column>",
        "Add `time_column:` under the kind (`kind: {incremental_by_time_range: {time_column: ordered_on}}`).",
        "Kind incremental_by_time_range derives its range from MAX(time_column) in the target.");
    public static readonly DiagnosticDescriptor GrainMismatch = E("216", "Grain missing or inconsistent",
        "grain is required for incremental kinds, and must equal unique_key for incremental_by_unique_key.",
        "Set `grain:` to the columns that uniquely identify a row.",
        "Grain documents row identity and is checked against the declared key.");
    public static readonly DiagnosticDescriptor UnknownColumnReference = E("217", "Reference to undeclared column",
        "Columns named in grain, unique_key, time_column, and renames must be declared in `columns`.",
        "Declare the column, or correct the reference.",
        "Declared columns are the model's output schema. Everything else must refer to it.");

    public static readonly DiagnosticDescriptor UpstreamNotFound = E("218", "Upstream table not found",
        "Every table a model queries is another model, built or declared (mapped or native), named schema_name.table_name.",
        "Add a mapped model models/<schema name>/<table>.yml for it, or define the model that produces it.",
        "A model's declared schema is checked against an empty DuckDB schema built from its upstream tables' declared columns, so each one must be declared somewhere.");
    public static readonly DiagnosticDescriptor QueryNotDescribable = E("219", "DuckDB cannot describe the model query",
        "A query DuckDB can bind against the declared upstream columns.",
        "Fix the query, or the declared columns of the upstream tables it uses.",
        "`project model update` asks DuckDB to describe the query (it is never run) to learn its output columns and types.");
    public static readonly DiagnosticDescriptor OutputColumnUnusable = E("220", "Output column cannot be declared",
        "Every output column has a name (an alias for expressions) that no other column shares.",
        "Add an alias to the expression, or rename the duplicate.",
        "Declared columns are matched to the query's output by name, so each name must be present and unique.");

    public static readonly DiagnosticDescriptor ModelCycle = E("221", "Models depend on each other in a cycle",
        "A model graph without cycles.",
        "Break the cycle: one of the models must not query another in the cycle.",
        "Models are defined and built in dependency order, which needs an acyclic graph.");

    public static readonly DiagnosticDescriptor MergeKeyNotIndexed = W("223", "A key-based load has no index on its key",
        "an index whose leading columns are the load's key, declared under `indexes:`",
        "Declare the index shown, or silence the advice with `lint_ignore: [DDB-223]` in the model (or `lint: { indexes: false }` in dbdatabuild.yml).",
        "A merge or delete-and-insert by key looks every incoming row up in the target. Without an index that leads with the key, each load scans the table. The tool never creates an index you did not declare, and does not infer one from `unique_key`; whether the key is also enforced as unique is your choice. This is advice only and never blocks a plan.");
    public static readonly DiagnosticDescriptor LoadColumnNotIndexed = N("224", "A load's column has no leading index",
        "an index whose first column is the load's watermark, time or range column, or (for a key) a unique one",
        "Declare the index shown, or silence the advice with `lint_ignore: [DDB-224]` in the model (or `lint: { indexes: false }` in dbdatabuild.yml).",
        "Loads that read MAX(watermark column) or delete a time range, and key loads whose key is indexed but not declared unique, are served better by an index that leads with that column. Nothing is created for you; this is advice only.");
    public static readonly DiagnosticDescriptor LoadSliceNotPushable = W("225", "A load's slice cannot be applied below part of the query",
        "a slice column that is a plain column of the query, a grouping key of its aggregate, and the partition key of its windows, in a query without LIMIT or DISTINCT ON",
        "Slice by a column that is a grouping or partition key, or choose a strategy that does not slice the finished result (for example filter the source inside the query yourself and reload with full_replace or a key-based load). If the cost is acceptable, silence it with `lint_ignore: [DDB-225]` in the model (or `lint: { slices: false }` in dbdatabuild.yml).",
        "`watermark_append` and `delete_insert_by_range` select from the finished query where the slice column is at or after the start: SELECT ... FROM (<your query>) WHERE slice_column >= @watermark. That is always correct, and it is cheap when the engine can apply the filter before the expensive part of the query. It cannot when the column is the result of an aggregate or a window function, when a window is not partitioned by it, or when the query has a LIMIT or DISTINCT ON (applying it early would change the rows), so every load does the work for the whole history and keeps a few rows. The tool does not rewrite your query to avoid this; it tells you, so you can pick a different column or strategy, or accept the cost.");
    public static readonly DiagnosticDescriptor MappedColumnNoLogicalType = W("226", "A mapped model's column has no logical type",
        "a column whose native type maps to a logical type: integers, DECIMAL(p, s), DOUBLE and FLOAT, BOOLEAN, DATE, TIME, TIMESTAMP, TIMESTAMP WITH TIME ZONE, UUID, BLOB, text (VARCHAR(n), or a bare VARCHAR for unlimited text), and xml and json read as text",
        "Declare the column by hand in the mapped model with the type a model should see (`VARCHAR` for text), and `project import` keeps it. Or leave it out if no model reads it.",
        "A mapped model describes a table to DuckDB so models over it can be bound offline. A column whose native type has no representation as a logical type (geography, intervals, arrays, user types) is left out of the generated mapped model rather than guessed at: a wrong type would flow into every model that selects it. Unlimited text is not one of them: it is a bare VARCHAR. A column you declared yourself in the mapped model is kept as written.");
    public static readonly DiagnosticDescriptor MappedModelOutOfSync = W("227", "A mapped model differs from the table it describes",
        "a committed mapped model `models/<schema name>/<table>.yml` with the columns, types and nullability the table has now",
        $"Run `{ProductInfo.Cli} project import --write` to refresh the mapped models, review the diff, and run `{ProductInfo.Cli} project model update --check` to see which models are affected.",
        "A mapped model is an export of a table the models read. When the table changes (a column added, a type widened, a NOT NULL added) the mapped model is stale until it is refreshed, and models are defined against the stale shape. `project import --check` makes the difference a finding for CI; it needs the read login.");
    public static readonly DiagnosticDescriptor RewriteNotOptional = E("229", "A rewrite cannot be turned off",
        "`rewrites.disable` names only rewrites that reproduce DuckDB's behavior; a rewrite a target cannot do without is not one of them",
        "Remove the name from `disable`, or take the target out of the model's `targets`. `dbdatabuild help matrix --rewrites` lists every rewrite and where each is required.",
        "Some rewrites are what makes a query valid on an engine (SQL Server has no LPAD, PostgreSQL's round takes no double). Turning one off would send the engine a statement it rejects, so the tool refuses before it renders. The rewrites that only keep an engine's answer equal to DuckDB's (a trailing space counted, a week counted) can be turned off.");
    public static readonly DiagnosticDescriptor MappedModelNotImportable = W("228", "A table cannot be imported as a mapped model",
        "a table or view whose schema name and table name can be a path (`models/<schema name>/<table>.yml`), or a mapped model whose table exists",
        "Rename the object, or write the mapped model by hand under a name the project can use. If a mapped model names a table that no longer exists, delete the file or restore the table.",
        "A mapped model's name is its path under `models/` with `/` replaced by `.`, so a dot, slash or backslash in a schema name or table name cannot be represented. A committed mapped model whose table is not found under its schema name is reported and left alone: the tool never deletes a file you may still need.");
    public static readonly DiagnosticDescriptor CopyOriginDiffers = E("230", "A copy's origin differs from its declaration",
        "an origin whose table still has the columns and types the mapped model declares (an added column is fine)",
        "Bring the origin's table back to the declaration, update the mapped model (`dbdatabuild project import`), or set `on_mismatch: skip` on the copy to leave that origin out until it is fixed.",
        "A copy reads each origin with the declared columns. When one system of an application is on another version, its table may have lost a column or changed a type, and reading it would fail or load the wrong thing. The apply of a deploy compares each origin's live table with the declaration just before that origin's own transfer and names the origin that differs; `on_mismatch` says whether that stops the apply there (`fail`; the origins before it stay loaded) or leaves that origin out (`skip`). A refresh does not make this comparison. An origin whose login is not in the environment cannot be compared, and its transfer fails.");
    public static readonly DiagnosticDescriptor ModelReadsAnotherConnection = E("231", "A model reads a table that is not on its connection",
        "every table a query reads exists on the connection the model is built on: a mapped model or a model built there, or a copy of the table to that connection",
        "Copy the table to the model's connection (`kind: {copy: {from: ...}}`) and read the copy, or build the model on the connection where the table is.",
        "A query runs on one connection and never reaches across to another; moving rows between connections is a copy. A model that reads a table declared (or built) only on other connections would fail when it runs, so it is refused when the project is checked.");
    public static readonly DiagnosticDescriptor NativeReadsNotDeclared = N("233", "A native model does not say what it reads",
        "a `reads:` list on the native model naming the tables its text reads (`reads: [dbo.orders, dbo.customers]`)",
        "Add `reads:` to the model's definition, listing the models or mapped tables the native text reads. It is not checked against the text; it only places the model in the graph.",
        "The text of a native model is the engine's own and the tool does not parse it, so without `reads:` the model has no ancestors: `project show graph` and `--column` impact stop at it, a selector such as `+model` does not reach what it is made from, and a model that reads it is not ordered after those tables.");
    public static readonly DiagnosticDescriptor NativeDefinitionChanged = W("234", "A routine a native model uses has changed",
        "the definition of every routine a native model lists under `track_definition` is the one recorded at the last apply",
        "Look at the change (the routine is named, with the recorded and the current hash). If it is intended, apply: the new definition is recorded and the warning goes. `policy.severity.native_definition_changed: error` makes it stop the plan.",
        "A native model's text is the engine's own, often only a call; the logic is in a function or procedure that lives in the database, where a change reaches no file of the project. `track_definition` names those routines; each apply records a hash of their definitions in the tracking tables, and `plan` compares the live definitions with the last record, so a change made in the database is seen before the table is loaded from it. The first plan after the list is added has nothing to compare with and says nothing.");
    public static readonly DiagnosticDescriptor NativeDefinitionNotChecked = N("235", "A routine definition could not be checked",
        "tracking configured for the connection, and a read login that can see the routine's definition (VIEW DEFINITION on SQL Server)",
        "Configure `tracking:` for the connection, grant the read login permission to see the routine, or correct the name in `track_definition` (on PostgreSQL an overloaded function needs its argument types).",
        "`track_definition` compares a routine's live definition with the one recorded at the last apply. Without tracking there is nowhere to keep the record; without permission, or with a name that matches no routine (or several), the engine returns no definition. The routine is then not checked, and nothing is recorded for it.");
    public static readonly DiagnosticDescriptor StringsCompareDifferently = W("236", "A model compares strings on connections that compare them differently",
        "a model whose connections agree on how strings compare (the project's `string_semantics`, or a connection's own), or whose string comparisons are on columns declared `trimmed: true` when only trailing spaces differ",
        "Make the connections' `string_semantics` agree for what this model compares, declare the columns it compares `trimmed: true` when they never end in a space (`connection status` counts the rows that break it), or silence this with `lint_ignore: [DDB-236]` on the model.",
        "SQL Server ignores trailing spaces in a comparison and PostgreSQL keeps them, and a collation can ignore case or accents on one connection and not on another. A model that compares, joins, groups, partitions or deduplicates strings, and is built on connections that disagree, can return different rows on each with no error. Only what the model does to string columns is looked at (the lowered query's column uses and their declared types); a comparison inside an expression is seen as a use of the columns it names.");
    public static readonly DiagnosticDescriptor TrimmedColumnHasTrailingSpaces = E("237", "A column declared trimmed has values that end in a space",
        "every value of a column declared `trimmed: true` ends in a non-space",
        "Trim the values (at the source, or in the model that builds the column), or remove `trimmed: true` and let the connections' string semantics decide.",
        "`trimmed: true` is the declaration that makes trailing spaces, the difference SQL Server and PostgreSQL disagree on, irrelevant for a column. `connection status` counts the rows whose value ends in a space (no value is read or shown); one is enough to make the declaration untrue.");
    public static readonly DiagnosticDescriptor QueryHeadInvalid = E("238", "A model's query file has a head that cannot be read",
        "a head of the form `CREATE TABLE schema_name.object_name [WITH (kind = '...', unique_key = (...), time_column = ..., lookback = '...')] AS` (or `CREATE VIEW schema_name.object_name AS`) followed by the query, naming the same model as the definition file and not repeating its kind",
        "Correct the head at the reported line and column, or remove it and keep `name:` and `kind:` in the definition file.",
        "A query file may start with a head that says what it builds: a table or a view, its name, and the reload options of its kind. The head is read by the tool itself (it is not SQL that DuckDB or an engine runs); the query after `AS` is passed on as written. The name must be the definition file's `name:` when that says one, and the kind must be said in one place: the head or the definition file, not both.");
    public static readonly DiagnosticDescriptor LoadSliceSourceNotIndexed = W("239", "A load's slice column is read from a source column that no index leads",
        "a slice column (a watermark, time or range column) that comes from a source column an index of the source leads with, or from a source the project declares no indexes or grain for",
        "If the source table has an index that leads with the column, add it to the source's declaration (`dbdatabuild project import` exports it); otherwise ask the table's owner for one, or silence the advice with `lint_ignore: [DDB-239]` in the model (or `lint: { slices: false }` in dbdatabuild.yml).",
        "A load that reads only the rows after its watermark, or in its range, is cheap when the engine can seek to them, which needs an index on the source whose first column is the one the slice filters. The advice follows the slice column through the query to the source column it is read from (by lineage) and looks at what the project declares for that source: its `indexes:` and its `grain` (the primary key's index leads with the first grain column). A source with neither declared is not judged, because nothing is known about it. A column that is computed (an expression, an aggregate) is not traced. Advice only; the tool never creates an index on a source.");
    public static readonly DiagnosticDescriptor ReadsInconsistentHistory = W("240", "A model reads a column whose history is inconsistent",
        "a model whose upstream columns have recorded history that agrees with the decisions made when they were added, or whose inconsistency an operator has acknowledged",
        "Run the backfill that was requested (`connection deploy --backfill <model>=<operation>`), or accept the inconsistency with `connection deploy --ack history:<model>.<column> --reason <why>`. `policy.severity.history_inconsistency: error` makes this refuse the plan; `warning` (the default) reports it.",
        "When a column is added to a model, the plan asks what to do about the rows already loaded, and the answer is recorded (DDB-443 when a backfill was chosen for later and none has been recorded since). A model built from such a column, directly or through other models, carries the NULLs of the missing history into its own rows. The plan follows column lineage from each unacknowledged inconsistency to the models it is about to load, so the concern is raised where the numbers are made, not only where the column was added.");
    public static readonly DiagnosticDescriptor NameTooLongForEngine = E("241", "A name is longer than an engine allows",
        "schema names, object names, column names and index names of at most 128 characters on SQL Server and Fabric and 63 bytes on PostgreSQL (the lengths of the connections the model is built on)",
        "Shorten the name, or build the model only on the connections whose engine accepts it (`connections=:`).",
        "PostgreSQL cuts an identifier longer than 63 bytes without saying so, so a column the model declares as `a_very_long_name...` exists in the database under a shorter name, the shape the plan expects is never found, and the step fails after it ran. SQL Server refuses a name over 128 characters. The check is made for every connection the model is built on, with the limit of its engine, before anything is planned.");
    public static readonly DiagnosticDescriptor DatabaseErrorUnhandled = E("242", "The database refused or lost a command and the tool had no more to say",
        "a login the server accepts, a server that stays reachable and statements it can run",
        "Read the error number: 18456 (SQL Server) and 28P01 (PostgreSQL) are a refused login (check the connection string in DBDATABUILD_<CONNECTION>_READ and _WRITE); 53300 and 53200 are too many connections or no memory; a number of 40000 or more on SQL Server is a connection the server closed. The statement log under .dbdatabuild/statement-log/ says what had run.",
        "A database exception that no command handled itself. The tool shows its type and its error number and never the driver's message, because a message can quote a server name, a login or a value from the data (DESIGN.md 14.2). It is not a defect of the tool: the command stopped because the database did not do what was asked, and everything that had been sent is in the statement log.");
    public static readonly DiagnosticDescriptor InfoOutOfBounds = E("243", "A title, description, tag or property is outside its limits",
        "a title of one line and at most 200 characters; a description of at most 4000; tags of at most 128 characters made of letters, digits, `_`, `-`, `.` and `:`; property keys of at most 128 characters of the same kind that start with a letter or `_` and do not end in `-`; property values of at most 4000 characters",
        "Shorten the text, or move the long part into the description. A property key that ends in `-`, `=` or `+` cannot be used because those suffixes change how a layer is merged.",
        "A person writes a title, a description, tags and properties on the project, a connection, a model, a column and an index (DESIGN.md 6.5.8). They are kept in the metadata documents and in the tracking tables, where a column has a length, so the limits are checked when the file is read and not when it is stored. The key shape keeps `properties-: [key]` and `properties=` unambiguous when files above a model give it properties too.");
    public static readonly DiagnosticDescriptor RefreshPlanUndeclared = E("244", "A model is in a refresh plan the project does not declare",
        "a plan name that `refresh.plans` in dbdatabuild.yml declares (or `default`), when the project declares any plans",
        $"Declare the plan under `refresh: {{ plans: ... }}` in {ProductInfo.ConfigFile}, or correct the name in the model's `refresh:`.",
        "The project lists its refresh plans (`hourly`, `daily`) so that a typo in a model's plan name is an error and not a plan nobody runs. A project that declares no plans at all accepts any name; `default` is always valid, because a model with a default load and no `refresh:` is in it.");
    public static readonly DiagnosticDescriptor RefreshMembershipDuplicate = E("245", "A load is in a refresh plan twice under the same name",
        "a membership name (the load's name, or `as:`) that is used once for a model within one plan",
        "Give one of the memberships another name with `as:`, for example `{ load: backfill, as: last_year, inputs: { ... } }`.",
        "A plan names each place a load of a model appears in it. The name is the load's name unless `as:` gives another, and it must be unique for the model within the plan, because the event, the monitor and `--input` refer to a membership by that name. The same load can be in one plan twice, with different inputs, by naming each place.");
    public static readonly DiagnosticDescriptor RefreshMembershipInvalid = E("246", "A refresh plan membership names a load or an input the model does not have",
        "a load of the model (or `default` when the model declares none) and, under `inputs:`, the names and values the load takes",
        "Use a load name from the model's `loads:`, an input the load takes (`start` and `end` of a range load, `watermark` of a watermark load, or a parameter the load's query uses) and a value of that input's type.",
        "A membership says which load runs in a plan and may give values for its inputs for that plan. A load the model does not declare, an input the load does not take, or a value that does not fit the input's type would fail only when the plan ran; the project refuses it when it is compiled.");
    /// <summary>Retired (entry 169): never raised. The old form of `kind` is read no more and is an error under <see cref="KindShapeInvalid"/>; the code stays in the catalog so it is never reused.</summary>
    public static readonly DiagnosticDescriptor KindWrittenOldWay = W("247", "Retired: a model kind written in the old form",
        "(retired) `kind` keyed by the kind's name: `kind: {copy: {from: pos.orders}}`, or `kind: view` for a kind with no settings",
        "Nothing raises this code any more. The old form `kind: {type: copy, from: pos.orders}` is an error, DDB-248, that says what to write.",
        "While the old form still loaded, this warning named the files that wrote it and `project model update` rewrote them. The form was removed in entry 169 (nothing had been released with only the new one), so there is no warning and no rewrite: the old form is DDB-248. The code is kept so that it is never given another meaning.");
    public static readonly DiagnosticDescriptor KindShapeInvalid = E("248", "A model kind is not written as one kind",
        "`kind` is the name of one kind (`kind: full`), or a mapping with exactly one key, the kind's name, whose value is a mapping of that kind's settings (`kind: {incremental_by_unique_key: {unique_key: [order_id]}}`)",
        "Name one kind: view, full, incremental_by_unique_key, incremental_by_time_range, copy, mapped or native, and put its settings beneath its name.",
        "The kind of a model is said by the shape of the file: one key, the kind, with its settings under it (the earlier form, `kind: {type: copy, from: pos.orders}`, is refused with this code and the message says how to write it now), so a setting that belongs to another kind is an unknown key and a model cannot have two kinds. A kind with no settings may be written as a plain value. A key such as `kind.copy:` is an ordinary key with a dot in it, not a nested one.");
    public static readonly DiagnosticDescriptor TrackingNotConfigured = W("232", "Nothing is tracked for a connection",
        "a `tracking:` section naming the connection that keeps the records (`tracking: { connection: audit }`), or `tracking: none` to choose not to track",
        "Add `tracking: { connection: <name> }` to dbdatabuild.yml (a connection of the project; `dbdatabuild connection init <name> --apply` creates the tables there), or `tracking: none` if the connection is not to be tracked. A connection can say its own under `connections.<name>.tracking`.",
        "Without tracking the tool keeps no records: a change made outside it is indistinguishable from a model change (plans are made from the declared shape against the live one, and every change to an existing object is marked risky), an incremental model is not blocked when its query changes, `--ack`, the column history, `connection monitor` and `connection publish` have nothing to work with, and an interrupted apply cannot be resumed. Planning and applying still work. `tracking: none` is the explicit choice and does not warn.");
    public static readonly DiagnosticDescriptor ResolverResultInvalid = E("222", "Resolver returned an unusable result",
        "A resolver that returns exactly one row and one column, of the parameter's type.",
        "Fix the committed resolver query in the model's `loads:` block, then run `project compile`.",
        "A resolver supplies a load parameter from the target (DESIGN.md 6.6). Any other shape of result stops that load: the tool never guesses a value.");

    // 3xx: matrix / portability
    public static readonly DiagnosticDescriptor ConstructUnsupported = E("301", "Construct unsupported on a declared target",
        "Constructs whose matrix status for every declared target is native, translated, emulated, approximated or unverified.",
        "Rewrite the model without the construct, or remove the target from `connections:`.",
        "The support matrix marks this construct `unsupported` for a target the model declares, so the build would fail or give wrong results there.");
    public static readonly DiagnosticDescriptor ConstructApproximated = W("302", "Construct approximated on a declared target",
        "A rewrite with a documented semantic difference (see the note).",
        "Check that the documented difference is acceptable for this model, or avoid the construct.",
        "The construct is rewritten for the target, but the result can differ from DuckDB. The matrix note says how.");
    public static readonly DiagnosticDescriptor ConstructEmulated = N("303", "Construct emulated on a declared target",
        "A multi-statement or helper rewrite (see the note).",
        "No action needed unless the note describes a concern for this model.",
        "The target has no native equivalent; the rewrite emulates it. Check atomicity and performance notes.");
    public static readonly DiagnosticDescriptor ConstructUnverified = W("304", "Construct unverified on a declared target",
        "A construct with a passing conformance test for the target.",
        "Add a conformance case and a matrix test reference, or avoid the construct.",
        "A rewrite exists, but no conformance test has passed for it on this target.");
    public static readonly DiagnosticDescriptor ConstructNotCovered = W("305", "Construct not covered by the support matrix",
        "Nodes, functions and clauses listed in matrix/covered.yml or matrix/constructs.yml.",
        "Add a conformance case for the construct and extend the matrix, or avoid it.",
        "The linter never assumes an unknown construct is safe. It was not exercised by any passing conformance case.");
    public static readonly DiagnosticDescriptor SqlParseFailure = E("306", "Model query could not be parsed",
        "A single SELECT in DuckDB dialect.",
        "Correct the SQL so that it runs in DuckDB.",
        "The query body could not be parsed as DuckDB SQL. There is no best-effort fallback.");
    public static readonly DiagnosticDescriptor NotASingleSelect = E("307", "Model query is not a single SELECT",
        "Exactly one SELECT (or set operation) statement.",
        "Keep one query in the .sql file, with no other statements.",
        "A model body is a single query that load strategies can wrap.");
    public static readonly DiagnosticDescriptor PairUnsupported = E("317", "Load operation has no supported rendering",
        "Every declared model x target x operation pair renders: its strategy and every construct in the query are supported on the target.",
        "Change the strategy or the query, or remove the target from the model or from the operation's `connections:`.",
        "There are no silent gaps: a pair that cannot be rendered is reported by name, never skipped.");
    public static readonly DiagnosticDescriptor RenderedScriptInvalid = E("318", "Rendered script failed offline validation",
        "A rendered script that the target's parser accepts (ScriptDOM for T-SQL, the polyglot parser for PostgreSQL).",
        "Report this: the renderer produced SQL the target's parser rejects, which is a bug in the tool or in a construct the matrix should mark unsupported.",
        "Every rendered file is parsed offline as part of validation.");
    public static readonly DiagnosticDescriptor PlaceholderUndeclared = E("319", "Rendered script has an undeclared placeholder",
        "Only the operation's declared value parameters (@name) appear as placeholders.",
        "Remove the placeholder from the query. Identifiers can never be parameters.",
        "Parameters are bound through the driver and never interpolated, so the executed text is exactly the committed text. Any other placeholder is rejected.");
    public static readonly DiagnosticDescriptor KeyColumnNullable = W("320", "Key column is nullable",
        "Key columns declared `nullable: false`.",
        "Declare the key columns NOT NULL.",
        "A row whose key is NULL never matches in a key comparison, so it would be inserted again on every load.");
    public static readonly DiagnosticDescriptor ConstructNeedsVersion = W("308", "Construct needs a minimum target version",
        "Target versions at or above the matrix min_version.",
        "Confirm the target version, or avoid the construct.",
        "The construct is supported only from a certain engine version. Target versions are not configured yet, so this cannot be checked offline.");

    public static readonly DiagnosticDescriptor CollationCannotSatisfyProfile = E("310", "Collation cannot satisfy the string comparison profile",
        "For each engine in use, a collation whose case, accent and trailing-space behavior match string_semantics.",
        "Choose a collation with the required behavior, or change string_semantics.",
        "The configured collation is known not to behave as the project's profile requires, so string comparisons would silently differ from the canonical DuckDB run.");
    public static readonly DiagnosticDescriptor CollationNotVerifiable = W("311", "Collation behavior cannot be verified offline",
        "A collation name the checker understands, or behavior confirmed against the live catalog.",
        "Verify the collation's behavior manually or against the target, or use a collation the checker recognizes.",
        "The checker could not tell from the name whether the collation matches the profile. It is not assumed to.");
    public static readonly DiagnosticDescriptor CollationNotConfigured = E("312", "Collation not configured",
        "string_semantics.collations.default with an entry for every engine in use, and every `collation:` on a column defined there.",
        "Add the missing entry under string_semantics.collations.",
        "Generated DDL always states collations explicitly, so each engine in use needs a collation name for every logical collation a model uses.");

    public static readonly DiagnosticDescriptor TypeNotMappable = E("321", "Column type has no native mapping on a target",
        "A logical type from the mapping table (BIGINT, INTEGER, SMALLINT, TINYINT, DOUBLE, FLOAT, BOOLEAN, DATE, TIMESTAMP, TIME, TIMESTAMP WITH TIME ZONE, DECIMAL(p, s), VARCHAR(n), UUID, BLOB).",
        "Declare a supported type for the column (a cast in the query, then `project model update`), or remove the target from the model.",
        "DDL needs an exact native type for every declared column. Types without a faithful equivalent (unsigned integers, HUGEINT, structs, lists) are refused rather than guessed. A bare VARCHAR is unlimited text: nvarchar(max) on SQL Server, varchar(max) on Fabric, text on PostgreSQL.");

    public static readonly DiagnosticDescriptor IndexNotSupported = E("322", "Index not supported on a target",
        "Declared indexes only for targets that create them: SQL Server and PostgreSQL.",
        "Remove the index, restrict it with `connections: [...]`, or remove the target from the model.",
        "Fabric Warehouse has no CREATE INDEX (its constraints are metadata only), so a declared index there is refused rather than skipped. Indexed views are out of scope.");

    public static readonly DiagnosticDescriptor HookScriptInvalid = E("323", "Hook script missing, invalid, or attached to an event the model cannot have",
        "For each hook of a model, on each target it applies to: an existing project-relative .sql file whose native SQL parses on that engine, attached to an event the model's kind has.",
        "Create or correct the script named in the message, or change the hook's event or targets.",
        "Hooks are native SQL run exactly as committed, so each script is read and parsed with the target's offline validator before anything is planned. A view has no load or backfill, so it has no hooks for them.");

    public static readonly DiagnosticDescriptor QueryNotLowerable = E("324", "Model query cannot be lowered",
        "A query whose bound plan the lowerer knows: tables and views, joins, filters, aggregates, windows, set operations, CTEs, and the expressions in docs/research/duckdb-plan-lowering.",
        "Rewrite the construct the message names, or turn lowering off with `lowering: { enabled: false }` in dbdatabuild.yml.",
        "Every model query is bound by DuckDB and lowered to one explicit query (star, USING, GROUP BY ALL, macros and implicit casts expanded) before it is checked against the support matrix and transpiled. A construct with no lowering yet (correlated subqueries, UNNEST, sampling, DISTINCT ON, nested types) is an error, not a silent fallback.");

    public static readonly DiagnosticDescriptor MacroDefaultIsClock = E("325", "Macro parameter defaults to a clock or session function",
        "A macro parameter whose default is a constant: a literal, or an expression of literals. The clock (`current_date`, `now()`, `today()`, `current_timestamp`, `localtimestamp`) and the session functions (`version()`, `current_schema()`) are not constants.",
        "Move the call into the macro's body or into the query that calls the macro: `CREATE MACRO m(d := NULL) AS coalesce(d, current_date)`, or pass the value as an argument at the call (`m(d := current_date)`).",
        "A macro's default is bound once, when the macro is created, so a clock function there would be read once and its answer kept: a lowered query would carry the day it was lowered. DuckDB 2.0 does exactly that without telling anyone (it folds the call into a constant, a 1.x release keeps the call). Models are planned with the clock functions kept as calls (docs/research/duckdb-2.0), and then DuckDB itself refuses the default (\"not a constant expression\", the same refusal as for `random()`), which this reports with the fix instead of the raw binder error. A call in the macro's body or in a query is unaffected, and so is a default that is an ordinary constant.");

    // 4xx: planning / questions
    public static readonly DiagnosticDescriptor AnswerForUnknownQuestion = W("410", "Answer for a question that was not asked",
        "Answers whose id matches a question asked in this run.",
        "Remove the answer, or ignore this warning: answer files are reusable across runs and may carry extra answers.",
        "Question ids are deterministic, so an answers file can be reused. An answer with no matching question in this run is ignored.");
    public static readonly DiagnosticDescriptor AnswerChoiceInvalid = E("411", "Answer is not one of the question's options",
        "`choice:` equal to one of the options listed for the question.",
        "Use one of the listed option keys.",
        "An answer must pick an explicit option. Nothing is guessed or defaulted.");
    public static readonly DiagnosticDescriptor AnswerValueMismatch = E("412", "Answer value does not fit the chosen option",
        "`value:` present for options that take one, and absent for options that do not.",
        "Add or remove `value:` to match the option.",
        "Some options carry a value (for example the new column name for a rename). Others must not have one.");
    public static readonly DiagnosticDescriptor NoProposalToAccept = E("413", "Nothing to accept for this question",
        "`accept: inferred` only for questions that carry an inferred proposal.",
        "Answer with an explicit `choice:` instead.",
        "Only questions with an inferred proposal can be answered by accepting it.");
    public static readonly DiagnosticDescriptor QuestionUnanswered = E("414", "Question is unanswered",
        "An answer for every question, interactively or in the answers file.",
        "Add one of the listed answers to the answers file, or run interactively.",
        "In non-interactive mode every open question is listed at once with the YAML to add. No question is answered by default.");

    public static readonly DiagnosticDescriptor DefinitionOutOfSync = E("420", "Definition is out of sync with its query",
        "Declared columns that match the columns the query returns (name, type, nullability).",
        $"Run `{ProductInfo.Cli} project model update <model>` to update the definition.",
        "A model's declared columns are the output schema that drives DDL and hashing. Planning never proceeds from a stale declared schema.");
    public static readonly DiagnosticDescriptor RenderedFileOutOfDate = E("424", "Committed rendered file is out of date",
        "Files under rendered/ identical to a fresh render of the current models.",
        $"Run `{ProductInfo.Cli} project compile` and commit the result.",
        "The rendered load operations are committed and executed exactly as written, so they must match what the current models render to. Planning is blocked for a model whose files differ.");
    public static readonly DiagnosticDescriptor DefinitionFileChanged = E("421", "Definition file changed while define was running",
        "The definition file unchanged between being read and being written.",
        $"Run `{ProductInfo.Cli} project model update` again.",
        "`project model update` checks the file's hash before writing and refuses if anything else touched it, so concurrent edits are never overwritten.");
    public static readonly DiagnosticDescriptor DefinitionNotEditable = E("422", "Definition cannot be edited automatically",
        "A `columns:` list written in block style.",
        "Rewrite the part named in the message in block style, or edit the definition by hand.",
        "`project model update` edits definitions by minimal text splices located with the YAML parser's positions, which needs block style for the lists it changes.");

    public static readonly DiagnosticDescriptor ObjectChangedOutsideTool = E("430", "Object changed outside the tool",
        "A live shape equal to the last shape the tool recorded for the object.",
        $"Review the change, then run `{ProductInfo.Cli} connection deploy --ack drift:<object> --reason <why>` to accept the live shape as the new baseline, or restore the object.",
        "The target's catalog hash for this object differs from the last recorded one (DESIGN.md 12.2). Planning for the model and everything downstream of it is blocked until a person decides.");
    public static readonly DiagnosticDescriptor LoadDefinitionChanged = E("431", "Incremental model changed since its last load",
        "An incremental model whose query is unchanged since the last load, or a change that a person has acknowledged.",
        $"Review the difference, then run `{ProductInfo.Cli} connection deploy --ack definition:<model> --reason <why>`, or request a backfill.",
        "Rows already loaded were produced by the old query. Loading more rows with a changed query would mix two definitions in one table, so planning is blocked until a person decides (DESIGN.md 11).");
    public static readonly DiagnosticDescriptor AdoptionDeclined = E("432", "Existing object was not adopted",
        "An answer of `adopt` for objects that exist on the target but have no tool record.",
        "Answer the adoption question with `adopt`, or rename the model, or remove the existing object.",
        "The tool does not take over an object it did not create without an explicit answer. Choosing `stop` stops planning for the model.");
    public static readonly DiagnosticDescriptor UpstreamBlocked = W("433", "Model skipped because an upstream model is blocked",
        "Every upstream model planned or unchanged.",
        "Resolve the block on the upstream model named in the message.",
        "A model is not planned while something it reads from is blocked or skipped (DESIGN.md 11).");
    public static readonly DiagnosticDescriptor ModelUnplannable = E("434", "Model cannot be planned",
        "A model whose declared columns map to native types and whose query renders for the target.",
        "Fix the problem the message names.",
        "Planning for this model stopped; the message says why.");

    public static readonly DiagnosticDescriptor PlanFileInvalid = E("435", "Plan file is invalid or was edited",
        "A plan file exactly as `connection deploy` wrote it: every key known, its content hash matching.",
        $"Generate a new plan with `{ProductInfo.Cli} connection deploy`. A plan is never edited by hand.",
        "A plan records the exact statements applying it will run, and a content hash over all of it (DESIGN.md 10.3). A hand-edited or damaged plan is refused rather than applied.");

    public static readonly DiagnosticDescriptor StepNeedsAllowance = E("436", "Plan step needs an explicit allowance",
        "Risky steps with `--allow-risky`; destructive steps with `--allow-destructive <object>` naming each object they change.",
        $"Review the plan report, then repeat the command with the allowance the message names. Nothing was executed.",
        "Risk classes are decided at plan time and enforced at apply time (DESIGN.md 10.4). No flag is implied by another, and destructive allowances name objects, never `all`.");
    public static readonly DiagnosticDescriptor PlanIsStale = E("437", "Plan is stale",
        $"A live target whose hashes and resolver results equal the ones the plan recorded.",
        $"Generate a new plan with `{ProductInfo.Cli} connection deploy`. A stale plan is never applied or adjusted.",
        "The plan was made against a different state of the target than the one that exists now (DESIGN.md 10.3). Applying it could do something its reviewer never saw, so it is refused.");
    public static readonly DiagnosticDescriptor PlanAlreadyStarted = E("438", "Plan was already applied or partly applied",
        $"A plan that was never applied, or a partly applied one resumed with `--resume`.",
        $"Use `{ProductInfo.Cli} connection deploy --apply-plan <plan>` for a plan that stopped part-way, or generate a new plan.",
        "Plans are single-use. A plan that completed is never run again. One that stopped part-way continues only when the live objects are exactly in the recorded intermediate state (DESIGN.md 10.3).");
    public static readonly DiagnosticDescriptor ApplyLockHeld = E("439", "Another apply holds the application lock",
        "No other apply running against this target.",
        "Wait for the other apply to finish, then run this one again.",
        "Applies are mutually exclusive per target (`sp_getapplock` on SQL Server, an advisory lock on PostgreSQL). The tool does not wait or queue.");
    public static readonly DiagnosticDescriptor StepFailed = E("440", "A plan step failed",
        "Every step completing.",
        $"Read the statement log named in the output, fix the cause, then `{ProductInfo.Cli} connection deploy --apply-plan <plan>` or generate a new plan.",
        "Steps stopped at the failure; later steps did not run. The step's status and the objects' hashes are recorded.");
    public static readonly DiagnosticDescriptor ApplyStopped = E("445", "Apply was stopped by the operator",
        "an apply that runs every step, or is stopped between two of them",
        $"`{ProductInfo.Cli} connection deploy --apply-plan <plan>` continues from the next step if the objects are still in the state the finished steps left them in.",
        "The operator asked to stop (from the terminal interface). A step that had started finished; the next one never started, so nothing is half done, and the migration is recorded as failed so that --resume can pick it up.");
    public static readonly DiagnosticDescriptor StepResultDiffers = E("441", "A step's result differs from the plan",
        "A shape hash after each DDL step equal to the one the plan promised.",
        $"Inspect the object, then generate a new plan.",
        "After each DDL step the tool recomputes the object's shape hash and compares it with the plan's expected result (DESIGN.md 10.3). A mismatch stops the apply and blocks dependents.");
    public static readonly DiagnosticDescriptor DirtyWorkingTree = E("442", "Working tree has uncommitted changes",
        "A clean working tree, so the commit recorded with the apply identifies what ran.",
        "Commit or stash the changes, or pass `--allow-dirty` to apply anyway (the dirty flag is recorded).",
        "Applies record the git commit; from a dirty tree that commit would not describe the code and plan that were used (DESIGN.md 10.3).");

    public static readonly DiagnosticDescriptor HistoryInconsistent = W("443", "A column's recorded history contradicts what was decided",
        "A recorded backfill after a `backfill_later` decision, or an operator's acknowledgement that none is wanted.",
        $"Run the backfill (`{ProductInfo.Cli} connection deploy --backfill <model>=<operation>`), or accept the situation with `{ProductInfo.Cli} connection deploy --ack history:<model>.<column> --reason <why>`.",
        "The warning is information, not a block: the operator decides what is a continuing concern. An acknowledgement is recorded with who made it and why, changes no data, and is tied to the one plan whose decision it is about.");

    public static readonly DiagnosticDescriptor RefreshPlanMissing = E("446", "There is no compiled refresh plan for the connection",
        "A `rendered/<connection>/loads.yml` and a `rendered/<connection>/refresh/<plan>.yml` that `project compile` wrote, committed with the project.",
        $"Run `{ProductInfo.Cli} project compile` and commit `rendered/`.",
        "A refresh runs the loads of a refresh plan that the project compiled for the connection. It reads the compiled files (every load of the connection in `loads.yml`, and the plan's own file), so it can run on a machine that only has the committed files and a login. They are written by `project compile`; a project that was never compiled, a connection no model declares, or a plan name no model's `refresh:` uses, has none.");

    public static readonly DiagnosticDescriptor RefreshCheckFailed = E("447", "An object a refresh uses is not in the structure the project expects",
        "Every object the refresh uses has the shape the compiled project expects, as the check chosen for the refresh finds it (`refresh.check`).",
        $"Deploy the project (`{ProductInfo.Cli} connection deploy`), or compile again if the plan is out of date; `--check none` skips the check, `--on-fail warn` reports and runs.",
        "A refresh never changes structure. When an object is behind (a column was added to the model and not yet deployed, a table was never created, or something changed it outside the tool), the loads would fail or load the wrong thing, so the refresh stops before it runs anything and says which objects, with the shape expected and the shape found.");

    public static readonly DiagnosticDescriptor RefreshFileChanged = E("448", "A file a refresh plan names is not the one it was compiled from",
        "Load scripts, resolvers and hook scripts with the hashes `loads.yml` recorded, and load plans with the hashes the refresh plan's file names.",
        $"Run `{ProductInfo.Cli} project compile` and commit the result.",
        "The compiled files name each script a refresh runs by path and hash, and each member of a refresh plan names its load plan by hash. A script that is missing or has another content than when the project was compiled means the committed files disagree with each other; the refresh runs nothing rather than guess which one is right.");

    public static readonly DiagnosticDescriptor RefreshInputMissing = E("449", "A refresh plan runs a load that needs a value from a person and gives none",
        "a membership that gives a value (`inputs: { start: 2024-01-01, end: 2024-02-01 }`) for every input of its load that has no default, or a load without such inputs",
        "Add `inputs:` with the missing values to the membership in the model's `refresh:`, or take the load out of the plan and give the values at the command (`--input name=value`).",
        "A scheduled refresh has nobody to ask. A load whose input has no default and no resolver (the `start` and `end` of a range load) can be in `loads.yml` and be run by a person who gives the values, but it can be in a plan only if the plan's membership supplies them; otherwise the plan could not run unattended, and the project refuses it when it is compiled.");
    public static readonly DiagnosticDescriptor InputResolverInvalid = E("451", "An input's resolver cannot be used",
        "an `expression` resolver that DuckDB reads as one expression or one SELECT without FROM, naming only the load's other inputs, `${project.x}` and `${connection.x}` parameters and the run variables `run_started`, `plan` and `connection`; a `target` or `source` resolver that lowers and renders for the engine it runs on",
        "Correct the resolver in the load's `inputs:`; an expression that needs a table is a `target` resolver.",
        "A resolver is checked when the project is compiled, so a typo is not found at two in the morning. An expression is evaluated by DuckDB in an empty sandbox: it can compute (dates, intervals, `date_trunc`, `last_day`, `strftime`, `coalesce`, a lookup in a literal list) and it can read nothing, so a table, a table function or a name that is not an input, a parameter or a run variable is refused. A name DuckDB reserves (an input called `end`) is written in double quotes in an expression: `\"end\" - 7`. A `target` or `source` resolver is written in DuckDB dialect over the project's models and lowered for the engine it runs on, like a model's query; a construct the engine cannot do refuses it, and the message says which.");
    public static readonly DiagnosticDescriptor InputsDependOnEachOther = E("452", "The inputs of a load depend on each other in a circle",
        "inputs whose expressions name inputs that do not (directly or through others) name them back",
        "Take the name of one input out of the expression of the other, or give one of them a value that needs no other input.",
        "An expression may name the load's other inputs (`start: current_date - days`), and the inputs are resolved in dependency order, so `days` is known before `start`. Two inputs that each need the other cannot be resolved in any order. The compile refuses them, naming the circle.");
    public static readonly DiagnosticDescriptor InputNotResolved = E("453", "An input has no usable value",
        "a resolver that returns exactly one row (one column for one input, the named columns for `provides`) whose value fits the input's type, or NULL where `on_null` says what to do",
        "Fix the resolver, or give the value yourself: `--input <model>.<load>.<name>=<value>` replaces the resolver, which is then not run.",
        "The message names the input, the load and the resolver. A resolver that returns no row, more than one row, the wrong columns, or a value that does not fit the type (a time of day for a DATE, a fraction for an INTEGER, a text that is not a date) is an error: the value is never changed to make it fit. NULL is `on_null`: `fail` (the default) stops the load and asks for a value, `initial` uses the input's default, `skip_load` does not run the load (which is not a failure). In a refresh the load fails with `failed: input` and the plan's failure rules apply.");
    public static readonly DiagnosticDescriptor InputsMoved = E("454", "Computed inputs are not what the plan was made with",
        "the same values at apply as at plan time, or the deployer's approval of the new ones",
        "Review the new values and approve them (an interactive apply asks), or run the same command again with the printed `--input` overrides, which pin each value.",
        "A deploy computes its inputs when it plans and shows them in the plan; `apply` computes them again. If any differs (a clock-based input moves at midnight, a maximum moves when data arrives) the deploy stops and shows each moved input with the planned and the new value, rather than load a range nobody looked at. An interactive apply lets the deployer approve and continue with the new values. A non-interactive apply exits 1 and prints each override as it would be typed (`--input orders.backfill.start=2026-03-01`); appending them to the same command is the explicit way to say yes with these values. `--yes` does not approve moved inputs. An input pinned with `--input` when planning is not computed, so it cannot move. Both values are recorded in the event.");
    public static readonly DiagnosticDescriptor RefreshPlanUnknown = E("450", "There is no refresh plan of that name for the connection",
        "one of the plans compiled for the connection (`rendered/<connection>/refresh/<plan>.yml`)",
        $"Run `{ProductInfo.Cli} connection refresh` without a plan to list the plans, or `{ProductInfo.Cli} project compile` if a model's `refresh:` was changed.",
        "A plan exists when some model's `refresh:` names it, or when a model with a default load says nothing (the plan `default`). The name given is none of the plans compiled for this connection.");

    // 455 and 456: read-only origins (docs/research/read-origins.md)
    public static readonly DiagnosticDescriptor ReadOnlyConnectionNotWritable = E("455", "A readonly connection cannot be written, built on or tracked on",
        "a `readonly` connection as the origin of a copy, of a mapped model or of a native query, as a `source` resolver's connection, and as the subject of `connection inspect`, `compare`, `monitor` and `project import`",
        "Use the connection only as an origin, give the model or the tracking a connection that is not `readonly`, or take `readonly` out of the connection's `engine` if the tool should write there (it will then need a `_WRITE` login).",
        "`readonly` in a connection's `engine` is a promise the tool keeps in layers: no `_WRITE` login is ever read for the connection, and it is refused as `tracking.connection`, as a connection of a model (the destination of a build or a copy), and as the connection of `connection deploy`, `refresh`, `seed`, `publish` and `init`. The account's own rights on the system remain the real guarantee.");

    public static readonly DiagnosticDescriptor DialectProfileInvalid = E("456", "A dialect profile is not valid",
        "a `dialects:` entry with a lower-case name that is not a keyword of `engine`, optionally `extends` a built-in (ansi, duckdb, sqlserver, postgres, fabric) or another profile, and the fields `quote`, `parameters` (named_at, positional, numbered), `ping` (one SELECT), `catalog` (information_schema, odbc, none), `identifier_case` (preserve, upper, lower), `limit` (limit, top, fetch) and `types`",
        "Correct the entry the message names. A name that collides with a keyword (readonly, odbc, postgres, ansi...) is renamed; a value outside the choices is one of them.",
        "A dialect profile is data: what an origin needs to know about the SQL a system takes (how to quote a name, how a parameter is written, what to send to see it answers). Every field left out is the base's, `ansi` unless `extends` says otherwise. A profile that cannot be built is refused when the project is loaded, so a typo is not found when a copy runs.");

    // 457 to 461: the dialect a server reports and `project update` (docs/research/read-origins.md section 10)
    public static readonly DiagnosticDescriptor DialectLockInvalid = E("457", "The committed dialects file cannot be used",
        $"`{ProductInfo.DialectLockFile}` as `{ProductInfo.Cli} project update` writes it: a `format: 1` mapping with one entry per connection, each with a built-in or declared `dialect`, a `profile` of valid fields and the `profile_hash` of those fields",
        $"Run `{ProductInfo.Cli} project update` to write the file again from the servers, or restore the committed copy. Do not edit the profile by hand: to choose a dialect yourself, name it in the connection's `engine`.",
        $"The file records which dialect each `readonly flightsql` connection without a dialect keyword was found to have, so that `project compile` and the plans do not depend on a call to the server. A file that is damaged, has an unknown key or a value outside the choices, or whose `profile_hash` is not the hash of its `profile` (it was edited by hand) is refused, and the connection is read as `ansi` until it is repaired: the tool never guesses what a damaged file meant.");
    public static readonly DiagnosticDescriptor DialectLockMissing = W("458", "A flightsql connection has no committed dialect",
        $"a `readonly flightsql` connection whose `engine` names a dialect (`readonly flightsql postgres`), or has an entry in `{ProductInfo.DialectLockFile}`",
        $"Run `{ProductInfo.Cli} project update` to ask the server and commit the answer, or name the dialect in the connection's `engine`. Until then the dialect is `ansi`.",
        $"A copy that reads from a Flight SQL server writes its read in the server's dialect (how a name is quoted). With no keyword in `engine` the tool can ask the server, but a compile must not depend on a connection, so the answer is recorded by `{ProductInfo.Cli} project update` in `{ProductInfo.DialectLockFile}`. Without it the read is written for `ansi`: double-quoted names, which most systems take.");
    public static readonly DiagnosticDescriptor DialectLockStale = W("459", "The committed dialects file has an entry that is not used",
        $"an entry in `{ProductInfo.DialectLockFile}` only for a `readonly flightsql` connection of the project whose `engine` names no dialect",
        $"Run `{ProductInfo.Cli} project update`, which removes the entry, or delete it.",
        "An explicit dialect keyword in `engine` always wins over the one found on the server, and a connection that is not a Flight SQL one, or is no longer in the project, has nothing to discover. The entry is ignored and the keyword (or `ansi`) is used.");
    public static readonly DiagnosticDescriptor OriginDialectChanged = E("460", "The server now reports a different dialect than the committed one",
        $"the dialect and profile recorded in `{ProductInfo.DialectLockFile}`, which the committed plans were written for",
        $"If the change is expected, run `{ProductInfo.Cli} project update`, review the change to `{ProductInfo.DialectLockFile}` and compile again; otherwise find out why the server behind the connection changed.",
        "A copy's read is committed text in the origin's dialect. Asking the server again before the origin's turn and finding another dialect (a different system behind the proxy, or a changed identifier quote) means that text may no longer be right, so the origin fails (`failed: origin`) and the text is never changed silently.");
    public static readonly DiagnosticDescriptor ProjectUpdateDiffers = E("461", "The project is not up to date with its connections",
        $"a project whose committed dialects file and mapped models say what the servers and tables say now",
        $"Run `{ProductInfo.Cli} project update` (add `--offline` to leave the connections alone), review the diff, and commit what it writes.",
        $"`{ProductInfo.Cli} project update --check` writes nothing and fails when running it would change a file: a dialect found on a server, a mapped model whose table has changed, or a definition that is out of step with its query. Each difference is shown with the file and what would change.");

    // 6xx: project tests (`project tests run`)
    public static readonly DiagnosticDescriptor TestFailed = E("601", "A project test failed",
        "a test that returns no violations: a metadata rule (`tests/metadata/<name>.sql`) whose SELECT returns no rows",
        $"Read the violating rows (shown with the finding and in `{ProductInfo.Cli} project tests run --format json`) and fix the models, or change the rule. A rule that should only advise says `-- severity: warning` at the top of the file.",
        "A metadata rule is a DuckDB SELECT over the metadata views (`metadata_columns`, `metadata_models`, `metadata_lineage`, `metadata_indexes`, `metadata_foreign_keys`, `metadata_loads`, `metadata_hooks`, `metadata_native_types`, `metadata_index_advice`, `metadata_upstream`, `metadata_ancestors`, `metadata_dependency`, `metadata_info`, `metadata_current`) that returns the violations. Each returned row is one violation. An `error` rule makes `test` exit non-zero; a `warning` rule is reported with this code at warning severity and does not, unless `--strict` is given.");
    public static readonly DiagnosticDescriptor TestCouldNotRun = E("602", "A project test could not run",
        "a rule whose SQL DuckDB can run over the metadata views",
        "Fix the SQL (the message is DuckDB's). Run `dbdatabuild project tests run --format json` to see it, and query the views by hand with `dbdatabuild project show metadata --format json` for the documents they are built from.",
        "A rule that fails to run is an error whatever its severity: a rule nobody can run protects nothing. The metadata views are built from the same documents `project show metadata` prints, in an in-memory DuckDB with file and network access off.");
    public static readonly DiagnosticDescriptor TestNotASelect = E("603", "A project test is not a single SELECT",
        "one SELECT (or WITH ... SELECT) statement",
        "Make the file one query that returns the violating rows.",
        "A rule only reads: it is one SELECT statement. Several statements, DDL, DML and files with only comments are refused before anything runs.");

    // 5xx: state and safety (logins, the mutation gate, tracking tables)
    public static readonly DiagnosticDescriptor LoginNotConfigured = E("501", "Login not configured",
        "A connection string in the environment variable named in the message, for the login this command needs.",
        "Set the variable in the runtime environment (never in the repo). Read-only commands use the read login, mutating commands the write login.",
        "Credentials come from the runtime environment only (DESIGN.md 9.2). There is no fallback from one login to the other: a read-only command never silently uses the write login.");
    public static readonly DiagnosticDescriptor GateRefused = E("502", "Statement refused by the mutation gate",
        "Only statements from a plan step or a tracking-table writer, of an effect class the running command permits.",
        "This is a bug in the tool if it was not caused by an edited plan. Report it with the command line used.",
        "Every write goes through one gate that checks the statement's origin and effect class against the command that is running (DESIGN.md 9.3). Nothing was executed.");
    public static readonly DiagnosticDescriptor StatementLogUnavailable = E("503", "Statement log could not be written",
        "A writable statement log directory.",
        "Make the log directory writable, or set it elsewhere in the project configuration.",
        "A statement is recorded before it runs. If the record cannot be written the statement is not executed.");
    public static readonly DiagnosticDescriptor ReadStatementRefused = E("504", "Read statement refused",
        "A single SELECT (or WITH ... SELECT) statement with no data-changing keywords.",
        "This is a bug in the tool. Report it with the command line used.",
        "Read-only commands run catalog and tracking-table queries through a guard that refuses anything that could change data. The read login's permissions are the real enforcement; the guard is a second layer. Nothing was executed.");
    public static readonly DiagnosticDescriptor TrackingNotInitialized = E("505", "Tracking tables are missing or have an unknown layout",
        $"The tracking tables created by `{ProductInfo.Cli} connection init` (in the tracking schema name), at a layout version this tool knows.",
        $"Run `{ProductInfo.Cli} connection init` (review its script first), or use a newer tool if the layout is newer. If they exist, the read login cannot see them: it needs SELECT on the tracking schema name, and on SQL Server an explicit DENY VIEW DEFINITION hides them from the catalog.",
        "Planning and applying need the tracking tables to compare hashes and to record what was done. The tool finds them in the engine's catalog with the read login, so a login that is denied the metadata of those tables sees none (checked on SQL Server 2022: with `DENY VIEW DEFINITION` the tables are reported as missing; with SELECT on the schema and no VIEW DEFINITION they are found, and only routine definitions are unreadable, DDB-235).");

    // 9xx: internal
    public static readonly DiagnosticDescriptor InternalError = new(ProductInfo.DiagnosticPrefix + "900",
        Severity.Error, "Internal error (tool bug)",
        "n/a", "Report this with the command line used.",
        "An unhandled exception occurred. This is a bug in the tool, not in your configuration.");

    public static readonly IReadOnlyList<DiagnosticDescriptor> All =
    [
        YamlSyntax, DuplicateKey, UnsupportedYamlFeature, UnknownKey, MissingKey, InvalidValue, NameMismatch, OrphanFile, ConfigNotFound, CopyOriginDiffers, ModelReadsAnotherConnection, NativeReadsNotDeclared, StringsCompareDifferently, TrimmedColumnHasTrailingSpaces, QueryHeadInvalid, LoadSliceSourceNotIndexed, ReadsInconsistentHistory, NameTooLongForEngine, DatabaseErrorUnhandled, InfoOutOfBounds, NativeDefinitionChanged, NativeDefinitionNotChecked, TrackingNotConfigured,
        MissingUniqueKey, MissingTimeColumn, GrainMismatch, UnknownColumnReference, UpstreamNotFound, QueryNotDescribable, OutputColumnUnusable, ModelCycle, ResolverResultInvalid, MergeKeyNotIndexed, LoadColumnNotIndexed, LoadSliceNotPushable, MappedColumnNoLogicalType, MappedModelOutOfSync, MappedModelNotImportable, RewriteNotOptional, ApplyStopped,
        ConstructUnsupported, ConstructApproximated, ConstructEmulated, ConstructUnverified, ConstructNotCovered,
        SqlParseFailure, NotASingleSelect, ConstructNeedsVersion, PairUnsupported, RenderedScriptInvalid, PlaceholderUndeclared, KeyColumnNullable, TypeNotMappable, IndexNotSupported, HookScriptInvalid, QueryNotLowerable, MacroDefaultIsClock,
        CollationCannotSatisfyProfile, CollationNotVerifiable, CollationNotConfigured,
        AnswerForUnknownQuestion, AnswerChoiceInvalid, AnswerValueMismatch, NoProposalToAccept, QuestionUnanswered,
        DefinitionOutOfSync, RenderedFileOutOfDate, ObjectChangedOutsideTool, LoadDefinitionChanged, AdoptionDeclined, UpstreamBlocked, ModelUnplannable, PlanFileInvalid, StepNeedsAllowance, PlanIsStale, PlanAlreadyStarted, ApplyLockHeld, StepFailed, RefreshPlanMissing, RefreshCheckFailed, RefreshFileChanged, RefreshPlanUndeclared, RefreshMembershipDuplicate, RefreshMembershipInvalid, KindWrittenOldWay, KindShapeInvalid, RefreshInputMissing, RefreshPlanUnknown, ReadOnlyConnectionNotWritable, DialectProfileInvalid, DialectLockInvalid, DialectLockMissing, DialectLockStale, OriginDialectChanged, ProjectUpdateDiffers, InputResolverInvalid, InputsDependOnEachOther, InputNotResolved, InputsMoved, StepResultDiffers, DirtyWorkingTree, HistoryInconsistent, DefinitionFileChanged, DefinitionNotEditable,
        TestFailed, TestCouldNotRun, TestNotASelect,
        LoginNotConfigured, GateRefused, StatementLogUnavailable, ReadStatementRefused, TrackingNotInitialized,
        InternalError,
    ];

    public static DiagnosticDescriptor? Find(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase));
}

public static class DiagnosticFormatter
{
    public static string Format(Diagnostic d)
    {
        var sb = new StringBuilder();
        sb.Append(d.Severity switch { Severity.Error => "error", Severity.Warning => "warning", _ => "note" }).Append(' ').Append(d.Code).Append("  ").AppendLineLf(d.Location.ToString());
        sb.Append("  ").AppendLineLf(Indent(d.Found));
        sb.Append("  Supported: ").AppendLineLf(Indent(d.Supported ?? d.Descriptor.Supported));
        sb.Append("  Fix: ").AppendLineLf(Indent(d.Fix ?? d.Descriptor.Fix));
        sb.Append("  Docs: ").Append(ProductInfo.Cli).Append(" explain ").AppendLineLf(d.Code);
        return sb.ToString();
    }

    /// <summary>Continuation lines of multi-line fields are indented under the field (single-line text is unchanged).</summary>
    private static string Indent(string text) => text.Replace("\r\n", "\n").Replace("\n", "\n    ");

    public static string Explain(DiagnosticDescriptor d) =>
        $"{d.Code}  {d.Title}\n\n{d.Explanation}\n\nSupported: {d.Supported}\nFix: {d.Fix}\n";
}
