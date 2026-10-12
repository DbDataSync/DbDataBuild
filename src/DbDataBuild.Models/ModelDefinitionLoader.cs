using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>Loads and validates one model definition (.yml). All problems are reported in one pass.</summary>
public static class ModelDefinitionLoader
{
    private static readonly string[] TopKeys = ["name", "kind", "grain", "connections", "columns", "renames", "loads", "indexes", "hooks", "lint_ignore", "rewrites", "parameters", .. YamlFieldReader.InfoKeys];
    private static readonly string[] RenameKeys = ["from", "to"];

    /// <param name="file">Path shown in diagnostics.</param>
    /// <param name="expectedName">Name implied by the path convention, or null to skip the check.</param>
    /// <param name="above">The project files above the model, root down (`defaults:` of each): the layers its own file is merged onto, the nearest winning (<see cref="YamlMerge"/>).</param>
    /// <param name="merged">Receives the merged mapping and where each part of it came from, when the file is a mapping that merged.</param>
    public static ModelDefinition? Load(string text, string file, string? expectedName, List<Diagnostic> diags, IReadOnlySet<string>? connections = null,
        IReadOnlyList<YamlLayer>? above = null, Action<MergedYaml>? merged = null)
    {
        var errorsBefore = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null)
        {
            if (diags.Count == errorsBefore)
                diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(file, 1, 1), "The file is empty. Required keys: name, kind, columns."));
            return null;
        }
        var known = connections ?? TargetNames.All.ToHashSet(StringComparer.Ordinal);
        if (root is not YamlMapping own)
        {
            new Validator(file, diags, known).Validate(root, expectedName);   // says the file must be a mapping
            return null;
        }
        // the file is always the last layer, whether or not anything is above it, so a suffix (`connections=`) means the same everywhere
        var result = YamlMerge.Merge([.. above ?? [], new YamlLayer(file, own)], LayeredKeys, diags);
        if (diags.Count > errorsBefore) return null;
        merged?.Invoke(result);
        return LoadMerged(result, file, expectedName, diags, known);
    }

    /// <summary>Validates a definition that was already merged from the project files above it and its own file. <paramref name="file"/> is the model's own file, for a problem that belongs to the whole definition.</summary>
    public static ModelDefinition? LoadMerged(MergedYaml merged, string file, string? expectedName, List<Diagnostic> diags, IReadOnlySet<string>? connections = null)
    {
        var errorsBefore = diags.Count;
        var def = new Validator(file, diags, connections ?? TargetNames.All.ToHashSet(StringComparer.Ordinal)) { NodeFiles = merged.FileOf }.Validate(merged.Root, expectedName);
        return diags.Count > errorsBefore ? null : def;
    }

    /// <summary>The top-level keys a model inherits from the project files above it (`defaults:` in each); a model's own file may use the same names, with a suffix, to change what it inherits.</summary>
    public static readonly IReadOnlySet<string> LayeredKeys = new HashSet<string>(["connections", "kind", "hooks", "rewrites", "lint_ignore", "tags", "properties"], StringComparer.Ordinal);

    private sealed class Validator(string file, List<Diagnostic> diags, IReadOnlySet<string> connections) : YamlFieldReader(file, diags)
    {
        private readonly List<YamlScalar> renameTargets = [];

        public ModelDefinition? Validate(YamlNode root, string? expectedName)
        {
            if (root is not YamlMapping top)
            {
                Add(DiagnosticCatalog.InvalidValue, root, "The definition must be a mapping of keys to values.");
                return null;
            }
            CheckKeys(top, TopKeys, "the definition");

            var name = Scalar(top, "name", required: true, at: top);
            if (name != null && expectedName != null && name.Value != expectedName)
                Add(DiagnosticCatalog.NameMismatch, name, $"name is `{name.Value}`, but the path implies `{expectedName}`.",
                    fix: $"Change `name:` to `{expectedName}`, or move the file.");

            var (kindType, uniqueKey, timeColumn, lookback, kindNode) = ReadKind(top);
            var isCopy = kindType?.Value == ModelKinds.Copy;
            YamlScalar? from = null;
            CopySlice? slice = null;
            CopyWatermark? watermark = null;
            var onMismatch = CopySlice.Fail;
            if (isCopy)
            {
                from = (kindNode as YamlMapping)?.Get("from") as YamlScalar;
                if (from == null || !System.Text.RegularExpressions.Regex.IsMatch(from.Value, @"^[^.\s/\\]+\.[^.\s/\\]+$"))
                    Add(DiagnosticCatalog.InvalidValue, (YamlNode?)from ?? kindNode ?? top, "A copy names the model it copies, `kind: {type: copy, from: schema_name.table_name}`.");
                if (top.Get("columns") is { } own) Add(DiagnosticCatalog.InvalidValue, own, "A copy has no `columns`: it has the columns of the model it copies.", "Remove `columns:`.");
                (slice, onMismatch) = ReadCopySettings(kindNode as YamlMapping);
                watermark = ReadCopyWatermark(kindNode as YamlMapping, uniqueKey);
            }
            var grain = StringList(top, "grain", required: false);
            var targets = StringList(top, "connections", required: false, allowEmpty: false, unique: true);
            if (targets != null)
                foreach (var t in targets.Where(t => !connections.Contains(t.Value)))
                    Add(DiagnosticCatalog.InvalidValue, t, $"Unknown connection `{t.Value}`.", $"One of: {string.Join(", ", connections.Order(StringComparer.Ordinal))}.");

            var columns = isCopy ? [] : ReadColumns(top);
            var loads = ReadLoads(top, kindType?.Value, uniqueKey, timeColumn, columns);
            var renames = ReadRenames(top);
            var indexes = ReadIndexes(top, columns, targets?.Select(t => t.Value).ToList());
            if (kindType?.Value == ModelKinds.View && indexes.Count > 0) Add(DiagnosticCatalog.InvalidValue, top.Get("indexes")!, "A view cannot have indexes (indexed views are out of scope).");
            var hooks = top.Get("hooks") is { } hn ? HookReader.ReadList(hn, allowUse: true, "`hooks`", (d, n, f) => Add(d, n, f), connections) : [];

            var lintIgnore = StringList(top, "lint_ignore", required: false, allowEmpty: false, unique: true);
            foreach (var code in (lintIgnore ?? []).Where(c => !IndexAdvisorCodes.Contains(c.Value)))
                Add(DiagnosticCatalog.InvalidValue, code, $"`{code.Value}` is not an advisory lint code.", $"One of: {string.Join(", ", IndexAdvisorCodes)}.");

            var info = ReadInfo(top);

            var rewrites = ReadRewrites(top);
            var modelParameters = top.Get("parameters") is { } pn ? ParameterReferences.Read(pn, "`parameters`", (d, n, f) => Add(d, n, f)) : null;

            // semantic checks
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            void Ref(IEnumerable<YamlScalar>? refs, string what)
            {
                foreach (var r in refs ?? [])
                    if (columns.Count > 0 && !declared.Contains(r.Value))
                        Add(DiagnosticCatalog.UnknownColumnReference, r, $"{what} refers to `{r.Value}`, which is not declared in `columns`.");
            }
            Ref(grain, "grain");
            Ref(uniqueKey, "unique_key");
            Ref(renameTargets, "rename target");
            if (timeColumn != null) Ref([timeColumn], "time_column");
            if (kindType?.Value == ModelKinds.IncrementalByTimeRange && lookback != null && LoadDuration.TryParse(lookback.Value) is { } lb &&
                timeColumn != null && columns.FirstOrDefault(c => string.Equals(c.Name, timeColumn.Value, StringComparison.OrdinalIgnoreCase)) is { } timeDef &&
                !lb.FitsColumnType(timeDef.Type))
                Add(DiagnosticCatalog.InvalidValue, lookback, $"A lookback of `{lookback.Value}` does not fit the {timeDef.Type} time column `{timeDef.Name}`.", "Days, weeks and months fit a DATE; any unit fits a TIMESTAMP.");

            var incremental = kindType?.Value is ModelKinds.IncrementalByUniqueKey or ModelKinds.IncrementalByTimeRange;
            if (incremental && (top.Get("grain") == null || grain is { Count: 0 }))
                Add(DiagnosticCatalog.GrainMismatch, kindNode ?? top, $"Kind {kindType!.Value} requires `grain`, but none is set.");
            if (kindType?.Value == ModelKinds.IncrementalByUniqueKey && grain != null && uniqueKey != null &&
                !grain.Select(g => g.Value).ToHashSet().SetEquals(uniqueKey.Select(k => k.Value)))
                Add(DiagnosticCatalog.GrainMismatch, grain.FirstOrDefault() ?? (YamlNode)top,
                    $"grain [{string.Join(", ", grain.Select(g => g.Value))}] differs from unique_key [{string.Join(", ", uniqueKey.Select(k => k.Value))}].");

            if (name == null || kindType == null) return null;
            return new ModelDefinition(name.Value, kindType.Value,
                uniqueKey?.Select(k => k.Value).ToList() ?? [], timeColumn?.Value, lookback?.Value,
                grain?.Select(g => g.Value).ToList() ?? [], targets?.Select(t => t.Value).ToList(),
                columns, renames, loads, indexes, hooks, lintIgnore?.Select(c => c.Value).ToList(), rewrites, from?.Value, from?.Line ?? 0, slice, onMismatch, false, watermark, modelParameters, false, info);
        }

        /// <summary>`watermark: {column, lookback}` of an incremental copy. It needs a `unique_key` (the rows it reads again replace the ones with the same key).</summary>
        private CopyWatermark? ReadCopyWatermark(YamlMapping? kind, List<YamlScalar>? uniqueKey)
        {
            if (kind?.Get("watermark") is not { } node) return null;
            if (node is not YamlMapping map) { Add(DiagnosticCatalog.InvalidValue, node, "`watermark` is a mapping: `{column: updated_at, lookback: 3 days}`."); return null; }
            CheckKeys(map, ["column", "lookback"], "`watermark`");
            var column = Scalar(map, "column", required: true, at: map);
            var lookback = map.Get("lookback") as YamlScalar;
            if (lookback != null && LoadDuration.TryParse(lookback.Value) == null)
                Add(DiagnosticCatalog.InvalidValue, lookback, $"lookback is `{lookback.Value}`.", "A number and a unit, for example `3 days` (minutes, hours, days, weeks or months).");
            if (kind.Get("unique_key") == null || uniqueKey is { Count: 0 })
                Add(DiagnosticCatalog.MissingUniqueKey, map, "An incremental copy (`watermark`) needs a `unique_key`: the rows it reads again replace the rows with the same key.", fix: "add under `kind:`  unique_key: [order_id]");
            return column == null ? null : new CopyWatermark(column.Value, lookback?.Value, map.Line);
        }

        private static readonly System.Text.RegularExpressions.Regex Reference = new(@"\$\{([a-z]+)\.([a-z][a-z0-9_]*)\}");

        /// <summary>`slice` and `on_mismatch` of a copy. A reference in the slice's value is `${origin.name}` or `${connection.name}`; any other `${` is a mistake to say so about now, not at plan.</summary>
        private (CopySlice? Slice, string OnMismatch) ReadCopySettings(YamlMapping? kind)
        {
            var onMismatch = CopySlice.Fail;
            if (kind?.Get("on_mismatch") is { } om)
            {
                if (om is YamlScalar { Value: CopySlice.Fail or CopySlice.Skip } s) onMismatch = s.Value;
                else Add(DiagnosticCatalog.InvalidValue, om, "`on_mismatch` is `fail` or `skip`: what to do about an origin whose table differs from the declaration.");
            }
            if (kind?.Get("slice") is not { } node) return (null, onMismatch);
            if (node is not YamlMapping map) { Add(DiagnosticCatalog.InvalidValue, node, "`slice` is a mapping: `{column: store_id, value: \"${origin.store_id}\"}`."); return (null, onMismatch); }
            CheckKeys(map, ["column", "value", "type"], "`slice`");
            var column = Scalar(map, "column", required: true, at: map);
            var value = Scalar(map, "value", required: true, at: map);
            var type = map.Get("type") as YamlScalar;
            if (value != null)
            {
                var stripped = Reference.Replace(value.Value, "");
                foreach (System.Text.RegularExpressions.Match m in Reference.Matches(value.Value).Where(m => !ParameterReferences.Scopes.Contains(m.Groups[1].Value)))
                    Add(DiagnosticCatalog.InvalidValue, value, $"`${{{m.Groups[1].Value}.{m.Groups[2].Value}}}` is not a parameter scope.", "`${origin.name}` (the connection a copy reads from), `${connection.name}` (the one it writes to), `${project.name}` or `${model.name}`.");
                if (stripped.Contains("${", StringComparison.Ordinal)) Add(DiagnosticCatalog.InvalidValue, value, "A reference in a slice's value is written `${origin.name}`.");
            }
            if (column == null || value == null) return (null, onMismatch);
            return (new CopySlice(column.Value, value.Value, type?.Value, map.Line), onMismatch);
        }

        private static readonly string[] IndexAdvisorCodes = [DiagnosticCatalog.MergeKeyNotIndexed.Code, DiagnosticCatalog.LoadColumnNotIndexed.Code, DiagnosticCatalog.LoadSliceNotPushable.Code, DiagnosticCatalog.LoadSliceSourceNotIndexed.Code, DiagnosticCatalog.StringsCompareDifferently.Code];

        private (YamlScalar? Type, List<YamlScalar>? UniqueKey, YamlScalar? TimeColumn, YamlScalar? Lookback, YamlNode? Node) ReadKind(YamlMapping top)
        {
            var node = top.Get("kind");
            if (node == null) { Add(DiagnosticCatalog.MissingKey, top, "Required key `kind` is missing.", fix: "Add `kind:` with a `type:`."); return default; }
            if (node is not YamlMapping kind) { Add(DiagnosticCatalog.InvalidValue, node, "`kind` must be a mapping with a `type`."); return default; }

            var type = Scalar(kind, "type", required: true, at: kind);
            var valid = type != null && ModelKinds.All.Contains(type.Value);
            if (type != null && !valid)
                Add(DiagnosticCatalog.InvalidValue, type, $"Unknown kind `{type.Value}`.", $"One of: {string.Join(", ", ModelKinds.All)}.");

            var allowed = new List<string> { "type" };
            if (type?.Value == ModelKinds.IncrementalByUniqueKey) allowed.Add("unique_key");
            if (type?.Value == ModelKinds.IncrementalByTimeRange) { allowed.Add("time_column"); allowed.Add("lookback"); }
            if (type?.Value == ModelKinds.Copy) { allowed.Add("from"); allowed.Add("slice"); allowed.Add("on_mismatch"); allowed.Add("unique_key"); allowed.Add("watermark"); }
            if (valid) CheckKeys(kind, allowed, $"kind `{type!.Value}`");

            var uniqueKey = StringList(kind, "unique_key", required: false);
            var timeColumn = kind.Get("time_column") as YamlScalar;
            if (type?.Value == ModelKinds.IncrementalByUniqueKey && (kind.Get("unique_key") == null || uniqueKey is { Count: 0 }))
                Add(DiagnosticCatalog.MissingUniqueKey, kind, "Kind incremental_by_unique_key requires a unique_key, but none is set.",
                    fix: "add under `kind:`  unique_key: [order_id]");
            if (type?.Value == ModelKinds.IncrementalByTimeRange && timeColumn == null)
                Add(DiagnosticCatalog.MissingTimeColumn, kind, "Kind incremental_by_time_range requires a time_column, but none is set.");
            var lookback = kind.Get("lookback") as YamlScalar;
            if (lookback != null && LoadDuration.TryParse(lookback.Value) == null)
                Add(DiagnosticCatalog.InvalidValue, lookback, $"lookback is `{lookback.Value}`.", "A number and a unit, for example `3 days` (minutes, hours, days, weeks or months).");
            return (type, uniqueKey, timeColumn, lookback, kind);
        }

        private static readonly string[] LoadKeys = ["default", "strategy", "key", "column", "watermark", "params", "max_span", "connections"];
        private static readonly string[] WatermarkKeys = ["column", "resolver", "lookback", "on_null", "initial", "overridable"];
        private static readonly System.Text.RegularExpressions.Regex OpName = new("^[a-z][a-z0-9_]*$");

        private List<LoadOperation>? ReadLoads(YamlMapping top, string? kind, List<YamlScalar>? uniqueKey, YamlScalar? timeColumn, IReadOnlyList<ColumnDefinition> columns)
        {
            if (top.Get("loads") is not { } node) return null;
            if (node is not YamlMapping loads) { Add(DiagnosticCatalog.InvalidValue, node, "`loads` must be a mapping of operation names to definitions."); return null; }
            if (kind == ModelKinds.View) { Add(DiagnosticCatalog.InvalidValue, node, "A view has no data load (DDL only), so it takes no `loads:`."); return null; }

            ColumnDefinition? Column(string name) => columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            var result = new List<LoadOperation>();
            foreach (var entry in loads.Entries)
            {
                if (!OpName.IsMatch(entry.Key.Value)) { Add(DiagnosticCatalog.InvalidValue, entry.Key, $"`{entry.Key.Value}` is not a valid operation name.", "Lowercase snake_case, such as `daily` or `reload_period`."); continue; }
                if (entry.Value is not YamlMapping op) { Add(DiagnosticCatalog.InvalidValue, entry.Value, $"Operation `{entry.Key.Value}` must be a mapping with a `strategy`."); continue; }
                CheckKeys(op, LoadKeys, $"load operation `{entry.Key.Value}`");

                var strategy = Scalar(op, "strategy", required: true, at: op);
                if (strategy == null) continue;
                if (!LoadStrategies.All.Contains(strategy.Value)) { Add(DiagnosticCatalog.InvalidValue, strategy, $"Unknown strategy `{strategy.Value}`.", $"One of: {string.Join(", ", LoadStrategies.All)}."); continue; }

                bool isDefault = false;
                if (Scalar(op, "default", required: false, at: op) is { } d)
                {
                    if (d.Value is "true" or "false") isDefault = d.Value == "true";
                    else Add(DiagnosticCatalog.InvalidValue, d, $"default is `{d.Value}`.", "true or false (lowercase).");
                }
                var targets = StringList(op, "connections", required: false, allowEmpty: false, unique: true);
                foreach (var t in targets ?? [])
                    if (!connections.Contains(t.Value)) Add(DiagnosticCatalog.InvalidValue, t, $"Unknown connection `{t.Value}`.", $"One of: {string.Join(", ", connections.Order(StringComparer.Ordinal))}.");

                void NotUsed(params string[] keys)
                {
                    foreach (var k in keys.Where(k => op.Get(k) != null))
                        Add(DiagnosticCatalog.InvalidValue, op.Entries.First(e => e.Key.Value == k).Key, $"`{k}` is not used by strategy `{strategy.Value}`.");
                }

                IReadOnlyList<string> key = [];
                string? column = null;
                WatermarkSpec? watermark = null;
                IReadOnlyList<LoadParameter> parameters = [];
                LoadDuration? maxSpan = null;

                switch (strategy.Value)
                {
                    case LoadStrategies.FullReplace:
                        NotUsed("key", "column", "watermark", "params", "max_span");
                        break;

                    case LoadStrategies.DeleteInsertByKey or LoadStrategies.MergeByKey:
                    {
                        NotUsed("column", "watermark", "params", "max_span");
                        var own = StringList(op, "key", required: false, allowEmpty: false, unique: true);
                        var names = own?.Select(k => (Node: k, k.Value)).ToList() ?? uniqueKey?.Select(k => (Node: k, k.Value)).ToList() ?? [];
                        if (own == null && op.Get("key") == null && (uniqueKey == null || uniqueKey.Count == 0))
                            Add(DiagnosticCatalog.MissingKey, op, $"Strategy `{strategy.Value}` needs `key:`, and the model's kind has no unique_key to default to.", fix: "Add `key: [<column>, ...]`.");
                        foreach (var (node2, name) in names.Where(n => own != null))
                            if (columns.Count > 0 && Column(name) == null) Add(DiagnosticCatalog.UnknownColumnReference, node2, $"key refers to `{name}`, which is not declared in `columns`.");
                        key = names.Select(n => n.Value).ToList();
                        break;
                    }

                    case LoadStrategies.DeleteInsertByRange:
                    {
                        NotUsed("key", "watermark");
                        var own = Scalar(op, "column", required: false, at: op);
                        var colName = own?.Value ?? timeColumn?.Value;
                        if (colName == null) { Add(DiagnosticCatalog.MissingKey, op, $"Strategy `{strategy.Value}` needs `column:`, and the model's kind has no time_column to default to.", fix: "Add `column: <column>`."); break; }
                        var def = Column(colName);
                        if (def == null) { if (columns.Count > 0) Add(DiagnosticCatalog.UnknownColumnReference, (YamlNode?)own ?? op, $"The range column `{colName}` is not declared in `columns`."); break; }
                        if (!IsRangeType(def.Type)) { Add(DiagnosticCatalog.InvalidValue, (YamlNode?)own ?? op, $"The range column `{def.Name}` is {def.Type}.", "DATE, TIMESTAMP or an integer type."); break; }
                        column = def.Name;
                        parameters = ReadRangeParams(op, def);
                        if (Scalar(op, "max_span", required: false, at: op) is { } ms)
                        {
                            maxSpan = LoadDuration.TryParse(ms.Value);
                            if (maxSpan == null) Add(DiagnosticCatalog.InvalidValue, ms, $"max_span is `{ms.Value}`.", "A number and a unit, for example `400 days`.");
                            else if (!maxSpan.FitsColumnType(def.Type) && IsTemporal(def.Type)) Add(DiagnosticCatalog.InvalidValue, ms, $"The unit of max_span `{ms.Value}` does not fit a {def.Type} column.");
                        }
                        break;
                    }

                    case LoadStrategies.WatermarkAppend:
                        NotUsed("key", "column", "params", "max_span");
                        watermark = ReadWatermark(op, columns);
                        break;
                }

                if (strategy.Value != LoadStrategies.DeleteInsertByRange) NotUsed();
                result.Add(new LoadOperation(entry.Key.Value, isDefault, strategy.Value, key, column, watermark, parameters, maxSpan, targets?.Select(t => t.Value).ToList(), Declared: true, entry.Key.Line));
            }

            foreach (var target in connections.Order(StringComparer.Ordinal))
            {
                var defaults = result.Where(o => o.IsDefault && o.AppliesTo(target)).ToList();
                if (defaults.Count > 1)
                    Add(DiagnosticCatalog.InvalidValue, loads, $"More than one operation is the default for {target}: {string.Join(", ", defaults.Select(o => o.Name))}.", "At most one default per connection.");
            }
            return result;
        }

        private static bool IsTemporal(string type) => type.Trim().ToUpperInvariant() is { } t && (t == "DATE" || t.StartsWith("TIMESTAMP", StringComparison.Ordinal));

        private static bool IsInteger(string type) => type.Trim().ToUpperInvariant() is "BIGINT" or "INTEGER" or "INT" or "SMALLINT";

        private static bool IsRangeType(string type) => IsTemporal(type) || IsInteger(type);

        private IReadOnlyList<LoadParameter> ReadRangeParams(YamlMapping op, ColumnDefinition column)
        {
            var type = column.Type;
            if (op.Get("params") is not { } node) return [new("start", type), new("end", type)];   // the range column's own type
            if (node is not YamlMapping p) { Add(DiagnosticCatalog.InvalidValue, node, "`params` must map `start` and `end` to types."); return []; }
            CheckKeys(p, ["start", "end"], "`params`");
            var result = new List<LoadParameter>();
            foreach (var name in new[] { "start", "end" })
            {
                if (p.Get(name) is not YamlScalar t || t.Value.Length == 0) { Add(DiagnosticCatalog.MissingKey, p, $"`params` needs `{name}:` with a type."); continue; }
                if (!ColumnTypes.Equivalent(t.Value, column.Type))
                    Add(DiagnosticCatalog.InvalidValue, t, $"Parameter `{name}` is {t.Value}, but the range column `{column.Name}` is {column.Type}.", "The parameter types must match the range column.");
                result.Add(new LoadParameter(name, t.Value));
            }
            return result;
        }

        private WatermarkSpec? ReadWatermark(YamlMapping op, IReadOnlyList<ColumnDefinition> columns)
        {
            if (op.Get("watermark") is not { } node) { Add(DiagnosticCatalog.MissingKey, op, "Strategy `watermark_append` needs a `watermark:` block.", fix: "Add `watermark:` with `column:` and `resolver: target_max`."); return null; }
            if (node is not YamlMapping w) { Add(DiagnosticCatalog.InvalidValue, node, "`watermark` must be a mapping."); return null; }
            CheckKeys(w, WatermarkKeys, "`watermark`");

            var col = Scalar(w, "column", required: true, at: w);
            var resolver = Scalar(w, "resolver", required: true, at: w);
            if (resolver != null && resolver.Value != "target_max")
                Add(DiagnosticCatalog.InvalidValue, resolver, $"Unknown resolver `{resolver.Value}`.", "`target_max` (MAX of the column in the target, less the lookback).");
            ColumnDefinition? def = null;
            if (col != null)
            {
                def = columns.FirstOrDefault(c => string.Equals(c.Name, col.Value, StringComparison.OrdinalIgnoreCase));
                if (def == null) { if (columns.Count > 0) Add(DiagnosticCatalog.UnknownColumnReference, col, $"The watermark column `{col.Value}` is not declared in `columns`."); }
                else if (!IsRangeType(def.Type)) Add(DiagnosticCatalog.InvalidValue, col, $"The watermark column `{def.Name}` is {def.Type}.", "DATE, TIMESTAMP or an integer type.");
            }

            LoadDuration? lookback = null;
            if (Scalar(w, "lookback", required: false, at: w) is { } lb)
            {
                lookback = LoadDuration.TryParse(lb.Value);
                if (lookback == null) Add(DiagnosticCatalog.InvalidValue, lb, $"lookback is `{lb.Value}`.", "A number and a unit, for example `3 days`.");
                else if (def != null && IsRangeType(def.Type) && !lookback.FitsColumnType(def.Type)) Add(DiagnosticCatalog.InvalidValue, lb, $"A lookback of `{lb.Value}` does not fit a {def.Type} watermark column.", "Use a DATE or TIMESTAMP column (days, weeks and months fit a DATE).");
            }

            var onNull = WatermarkSpec.RequireParam;
            if (Scalar(w, "on_null", required: false, at: w) is { } on)
            {
                if (on.Value is WatermarkSpec.RequireParam or WatermarkSpec.InitialLiteral) onNull = on.Value;
                else Add(DiagnosticCatalog.InvalidValue, on, $"on_null is `{on.Value}`.", "`require_param` (a question, or `--param` in an answers file) or `initial` (with a committed `initial:` literal).");
            }
            var initial = Scalar(w, "initial", required: false, at: w);
            if (onNull == WatermarkSpec.InitialLiteral && initial == null && w.Get("initial") == null)
                Add(DiagnosticCatalog.MissingKey, w, "`on_null: initial` needs `initial:`, the committed literal used when the target is empty.", fix: "Add `initial: 2020-01-01` (a date, timestamp or integer matching the column).");
            if (onNull != WatermarkSpec.InitialLiteral && initial != null)
                Add(DiagnosticCatalog.InvalidValue, initial, "`initial:` is only used with `on_null: initial`.");
            if (initial != null && def != null && IsRangeType(def.Type) && !ColumnTypes.LiteralFits(def.Type, initial.Value))
                Add(DiagnosticCatalog.InvalidValue, initial, $"`{initial.Value}` is not a valid {def.Type} literal.");

            var overridable = false;
            if (Scalar(w, "overridable", required: false, at: w) is { } ov)
            {
                if (ov.Value is "true" or "false") overridable = ov.Value == "true";
                else Add(DiagnosticCatalog.InvalidValue, ov, $"overridable is `{ov.Value}`.", "true or false (lowercase).");
            }
            return col == null ? null : new WatermarkSpec(def?.Name ?? col.Value, lookback, onNull, initial?.Value, overridable);
        }

        private List<IndexDefinition> ReadIndexes(YamlMapping top, List<ColumnDefinition> columns, List<string>? modelTargets)
        {
            var result = new List<IndexDefinition>();
            if (top.Get("indexes") is not { } node) return result;
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, "`indexes` must be a list."); return result; }
            string[] keys = ["name", "columns", "unique", "include", "connections", .. InfoKeys];
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, item, "Each index must be a mapping with `name` and `columns`."); continue; }
                CheckKeys(m, keys, "an index");
                var name = Scalar(m, "name", required: true, at: m);
                var cols = StringList(m, "columns", required: true, allowEmpty: false, unique: true);
                var include = StringList(m, "include", required: false, allowEmpty: false, unique: true);
                var idxTargets = StringList(m, "connections", required: false, allowEmpty: false, unique: true);
                var unique = false;
                if (Scalar(m, "unique", required: false, at: m) is { } u)
                {
                    if (u.Value is "true" or "false") unique = u.Value == "true";
                    else Add(DiagnosticCatalog.InvalidValue, u, $"unique is `{u.Value}`.", "true or false (lowercase).");
                }
                if (name != null)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name.Value, "^[A-Za-z_][A-Za-z0-9_]*$")) Add(DiagnosticCatalog.InvalidValue, name, $"`{name.Value}` is not a valid index name.", "Letters, digits and underscores, not starting with a digit.");
                    else if (!names.Add(name.Value)) Add(DiagnosticCatalog.DuplicateKey, name, $"The index name `{name.Value}` is used more than once.");
                }
                foreach (var c in (cols ?? []).Concat(include ?? []).Where(c => columns.Count > 0 && !declared.Contains(c.Value)))
                    Add(DiagnosticCatalog.UnknownColumnReference, c, $"The index refers to `{c.Value}`, which is not declared in `columns`.");
                foreach (var c in (include ?? []).Where(i => cols?.Any(k => string.Equals(k.Value, i.Value, StringComparison.OrdinalIgnoreCase)) == true))
                    Add(DiagnosticCatalog.InvalidValue, c, $"`{c.Value}` is both a key and an included column of the index.");
                foreach (var t in (idxTargets ?? []).Where(t => !connections.Contains(t.Value)))
                    Add(DiagnosticCatalog.InvalidValue, t, $"Unknown connection `{t.Value}`.", $"One of: {string.Join(", ", connections.Order(StringComparer.Ordinal))}.");
                if (name == null || cols == null) continue;
                result.Add(new IndexDefinition(name.Value, cols.Select(c => c.Value).ToList(), unique, include?.Select(c => c.Value).ToList() ?? [], idxTargets?.Select(t => t.Value).ToList(), m.Line, ReadInfo(m)));
            }
            return result;
        }

        private List<RenameDefinition> ReadRenames(YamlMapping top)
        {
            var result = new List<RenameDefinition>();
            renameTargets.Clear();
            if (top.Get("renames") is not { } node) return result;
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, "`renames` must be a list of from/to pairs."); return result; }
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, item, "Each rename must be a mapping with `from` and `to`."); continue; }
                CheckKeys(m, RenameKeys, "a rename");
                var from = Scalar(m, "from", required: true, at: m);
                var to = Scalar(m, "to", required: true, at: m);
                if (from == null || to == null) continue;
                result.Add(new RenameDefinition(from.Value, to.Value));
                renameTargets.Add(to);
            }
            return result;
        }
    }
}
