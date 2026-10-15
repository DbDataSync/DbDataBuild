using System.Text.Json.Nodes;

namespace DbDataBuild.Lowering;

/// <summary>
/// DuckDB's serialized plan is not a stable API, and DuckDB 2.0 restructured it: names moved into `qname.path`, comparisons and casts became ordinary functions with
/// `children`, `NOT IN` became NOT over IN, `BETWEEN` became a function, and window functions carry their arguments as children. The lowerer reads the 1.x shape; this rewrites a 2.0 plan into it, node by
/// node, so one lowerer serves both engines and the committed artifacts do not change when only the plan format did. A plan that is already in the 1.x shape is returned
/// unchanged (as the same string). Every rule here was found by comparing the plans of both engines on the lowering corpus.
/// </summary>
public static class PlanNormalizer
{
    private static readonly Dictionary<string, string> Comparisons = new(StringComparer.Ordinal)
    {
        ["COMPARE_EQUAL"] = "COMPARE_EQUAL", ["COMPARE_NOTEQUAL"] = "COMPARE_NOTEQUAL", ["COMPARE_LESSTHAN"] = "COMPARE_LESSTHAN", ["COMPARE_GREATERTHAN"] = "COMPARE_GREATERTHAN",
        ["COMPARE_LESSTHANOREQUALTO"] = "COMPARE_LESSTHANOREQUALTO", ["COMPARE_GREATERTHANOREQUALTO"] = "COMPARE_GREATERTHANOREQUALTO",
        ["COMPARE_DISTINCT_FROM"] = "COMPARE_DISTINCT_FROM", ["COMPARE_NOT_DISTINCT_FROM"] = "COMPARE_NOT_DISTINCT_FROM",
    };

    private static readonly Dictionary<string, string> Opposite = new(StringComparer.Ordinal)
    {
        ["COMPARE_EQUAL"] = "COMPARE_NOTEQUAL", ["COMPARE_NOTEQUAL"] = "COMPARE_EQUAL", ["COMPARE_LESSTHAN"] = "COMPARE_GREATERTHANOREQUALTO", ["COMPARE_GREATERTHANOREQUALTO"] = "COMPARE_LESSTHAN",
        ["COMPARE_GREATERTHAN"] = "COMPARE_LESSTHANOREQUALTO", ["COMPARE_LESSTHANOREQUALTO"] = "COMPARE_GREATERTHAN", ["COMPARE_DISTINCT_FROM"] = "COMPARE_NOT_DISTINCT_FROM", ["COMPARE_NOT_DISTINCT_FROM"] = "COMPARE_DISTINCT_FROM",
    };

    public static string Normalize(string planJson)
    {
        if (!planJson.Contains("\"qname\"", StringComparison.Ordinal)) return planJson;      // the 1.x shape has no qualified names
        var root = JsonNode.Parse(planJson)!;
        Walk(root);
        if (planJson.Contains(DelimPrefix, StringComparison.Ordinal)) root = UndoDelimCtes(root) ?? root;
        return root.ToJsonString();
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    // Dependent joins as materialized CTEs.
    //
    // DuckDB 2.0 flattens a correlated subquery into a DELIM_JOIN as 1.x did and then rewrites that node into CTEs (its setting `delim_join_as_cte`, on by default). The rewrite is mechanical:
    //
    //   DELIM_JOIN(type, conditions, duplicate_eliminated_columns = G) [ L, R(... DELIM_GET ...) ]
    //     becomes
    //   MATERIALIZED_CTE __duckdb_delim_N := L
    //     MATERIALIZED_CTE __duckdb_delim_dedup_M := AGGREGATE GROUP BY G over CTE_REF(N)
    //       JOIN(type, conditions) [ CTE_REF(N), R(... CTE_REF(M) ...) ]
    //
    // with pass-through projections after each CTE. This undoes it, so the lowerer reads the one shape it has always read and the lowered text does not depend on the DuckDB version. It does not
    // set the setting (DuckDB means to remove it), it reads what the default produces.
    // ---------------------------------------------------------------------------------------------------------------------------------------------------------

    private const string DelimPrefix = "__duckdb_delim_";
    private const string DedupPrefix = "__duckdb_delim_dedup_";

    private static string? Name(JsonNode? n) => n is JsonObject o ? Text(o["ctename"]) : null;
    /// <summary>Rewrites the tree bottom-up and returns the node that replaces <paramref name="node"/>.</summary>
    private static JsonNode? UndoDelimCtes(JsonNode? node)
    {
        if (node is not JsonObject o) return node;
        foreach (var list in new[] { o["children"], o["plans"] }.OfType<JsonArray>())
            for (var i = 0; i < list.Count; i++)
            {
                var child = list[i];
                var replacement = UndoDelimCtes(child);
                if (!ReferenceEquals(child, replacement)) { list[i] = null; list[i] = replacement; }
            }
        if (Text(o["type"]) == "LOGICAL_MATERIALIZED_CTE" && Name(o) is { } name && name.StartsWith(DelimPrefix, StringComparison.Ordinal) && !name.StartsWith(DedupPrefix, StringComparison.Ordinal))
            return DelimJoinOf(o) ?? o;
        return o;
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>The number of columns a node outputs, or -1 when this does not know the node (a projection is then left where it is).</summary>
    private static int ColumnCount(JsonNode? n)
    {
        if (n is not JsonObject o) return -1;
        var kids = o["children"] as JsonArray;
        switch (Text(o["type"]))
        {
            case "LOGICAL_GET":
                if (o["projection_ids"] is JsonArray { Count: > 0 } ids) return ids.Count;
                return o["column_indexes"] is JsonArray { Count: > 0 } ci ? ci.Count : o["returned_types"] is JsonArray rt ? rt.Count : -1;
            case "LOGICAL_PROJECTION": return (o["expressions"] as JsonArray)?.Count ?? -1;
            case "LOGICAL_AGGREGATE_AND_GROUP_BY":
                return o["grouping_sets"] is JsonArray { Count: > 1 } ? -1 : ((o["groups"] as JsonArray)?.Count ?? 0) + ((o["expressions"] as JsonArray)?.Count ?? 0);
            case "LOGICAL_FILTER": return o["projection_map"] is JsonArray { Count: > 0 } pm ? pm.Count : kids is { Count: 1 } ? ColumnCount(kids[0]) : -1;
            case "LOGICAL_ORDER_BY" or "LOGICAL_LIMIT" or "LOGICAL_DISTINCT": return o["projections"] is JsonArray { Count: > 0 } pr ? pr.Count : kids is { Count: 1 } ? ColumnCount(kids[0]) : -1;
            case "LOGICAL_CTE_REF": return o["bound_columns"] is JsonArray bc ? bc.Count : -1;
            case "LOGICAL_DELIM_GET": return o["chunk_types"] is JsonArray ct ? ct.Count : -1;
            case "LOGICAL_MATERIALIZED_CTE": return kids is { Count: 2 } ? ColumnCount(kids[1]) : -1;
            case "LOGICAL_CROSS_PRODUCT": return kids is { Count: 2 } && ColumnCount(kids[0]) is var cl and >= 0 && ColumnCount(kids[1]) is var cr and >= 0 ? cl + cr : -1;
            case "LOGICAL_COMPARISON_JOIN":
            {
                if (kids is not { Count: 2 }) return -1;
                var left = o["left_projection_map"] is JsonArray { Count: > 0 } lm ? lm.Count : ColumnCount(kids[0]);
                var right = o["right_projection_map"] is JsonArray { Count: > 0 } rm ? rm.Count : ColumnCount(kids[1]);
                if (left < 0 || right < 0) return -1;
                return Text(o["join_type"]) switch { "MARK" => left + 1, "SEMI" or "ANTI" => left, _ => left + right };
            }
            default: return -1;
        }
    }

    /// <summary>A projection that outputs its child's columns as they are: what the CTE rewrite puts around the nodes it creates.</summary>
    private static JsonNode? WithoutPassThrough(JsonNode? n)
    {
        if (n is JsonObject o && Text(o["type"]) == "LOGICAL_PROJECTION" && o["expressions"] is JsonArray ex && o["children"] is JsonArray { Count: 1 } kids && ColumnCount(kids[0]) == ex.Count)
        {
            for (var i = 0; i < ex.Count; i++)
                if (ex[i] is not JsonObject e || Text(e["type"]) != "BOUND_REF" || Int(e["index"]) != i) return n;
            return kids[0];
        }
        return n;
    }

    private static JsonNode? DelimJoinOf(JsonObject cte)
    {
        if (cte["children"] is not JsonArray { Count: 2 } kids || Int(cte["table_index"]) is not { } delimIndex) return null;
        var dedup = WithoutPassThrough(kids[1]);
        if (dedup is not JsonObject dedupCte || Text(dedupCte["type"]) != "LOGICAL_MATERIALIZED_CTE" || Name(dedupCte) is not { } dedupName || !dedupName.StartsWith(DedupPrefix, StringComparison.Ordinal)) return null;
        if (Int(dedupCte["table_index"]) is not { } dedupIndex || dedupCte["children"] is not JsonArray { Count: 2 } dedupKids) return null;
        if (dedupKids[0] is not JsonObject agg || Text(agg["type"]) != "LOGICAL_AGGREGATE_AND_GROUP_BY" || agg["groups"] is not JsonArray groups || agg["expressions"] is JsonArray { Count: > 0 }) return null;
        if (agg["children"] is not JsonArray { Count: 1 } aggKids || aggKids[0] is not JsonObject aggRef || Text(aggRef["type"]) != "LOGICAL_CTE_REF" || Int(aggRef["cte_index"]) != delimIndex) return null;
        if (WithoutPassThrough(dedupKids[1]) is not JsonObject join || Text(join["type"]) is not ("LOGICAL_COMPARISON_JOIN" or "LOGICAL_CROSS_PRODUCT") || join["children"] is not JsonArray { Count: 2 } joinKids) return null;
        if (joinKids[0] is not JsonObject outer || Text(outer["type"]) != "LOGICAL_CTE_REF" || Int(outer["cte_index"]) != delimIndex) return null;
        if (join["delim_flipped"] is JsonValue flipped && flipped.TryGetValue<bool>(out var isFlipped) && isFlipped) return null;

        var delim = (JsonObject)join.DeepClone();
        delim["type"] = "LOGICAL_DELIM_JOIN";
        if (delim["join_type"] is null) delim["join_type"] = "INNER";
        if (delim["conditions"] is null) delim["conditions"] = new JsonArray();
        delim["duplicate_eliminated_columns"] = groups.DeepClone();
        var left = WithoutPassThrough(kids[0])!.DeepClone();
        var right = ReplaceCteRef(joinKids[1]!.DeepClone(), dedupIndex);
        delim["children"] = new JsonArray(left, right);
        return delim;
    }

    /// <summary>The scans of the deduplicated outer values are DELIM_GET again.</summary>
    private static JsonNode? ReplaceCteRef(JsonNode? node, int cteIndex)
    {
        if (node is JsonArray a)
        {
            for (var i = 0; i < a.Count; i++) { var current = a[i]; var r = ReplaceCteRef(current, cteIndex); if (!ReferenceEquals(r, current)) { a[i] = null; a[i] = r; } }
            return a;
        }
        if (node is not JsonObject o) return node;
        if (Text(o["type"]) == "LOGICAL_CTE_REF" && Int(o["cte_index"]) == cteIndex)
            return new JsonObject { ["type"] = "LOGICAL_DELIM_GET", ["table_index"] = o["table_index"]?.DeepClone(), ["chunk_types"] = o["chunk_types"]?.DeepClone() };
        foreach (var key in o.Select(p => p.Key).ToList())
        {
            var current = o[key];
            var r = ReplaceCteRef(current, cteIndex);
            if (!ReferenceEquals(r, current)) { o[key] = null; o[key] = r; }
        }
        return o;
    }

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var item in a) Walk(item);
                break;
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList()) Walk(o[key]);
                Rewrite(o);
                break;
        }
    }

    /// <summary>A NULL literal, or a cast of one (2.0 types the default of lag and lead).</summary>
    private static bool IsNullConstant(JsonNode? n) =>
        n is JsonObject o && (Text(o["type"]) == "VALUE_CONSTANT" ? o["value"]?["is_null"] is JsonValue v && v.TryGetValue<bool>(out var isNull) && isNull : o["child"] is { } child && IsNullConstant(child));

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static void Rewrite(JsonObject o)
    {
        // names: `qname.path` is [catalog, schema, name], [schema, name] or [name]
        if (o["qname"]?["path"] is JsonArray { Count: > 0 } path && o["name"] is null)
        {
            o["name"] = path[^1]!.DeepClone();
            if (path.Count >= 2) o["schema_name"] = path[^2]!.DeepClone();
            if (path.Count >= 3) o["catalog_name"] = path[0]!.DeepClone();
        }

        var type = Text(o["type"]);           // `type` is an object on a column index, so it is a string only on operators and expressions
        var cls = Text(o["expression_class"]);
        if (cls == "BOUND_FUNCTION" && type != null && o["children"] is JsonArray children)
        {
            if (Comparisons.ContainsKey(type) && children.Count == 2)
            {
                o["expression_class"] = "BOUND_COMPARISON";
                o["left"] = children[0]!.DeepClone();
                o["right"] = children[1]!.DeepClone();
                o.Remove("children");
            }
            else if (type is "COMPARE_BETWEEN" or "COMPARE_NOT_BETWEEN" && children.Count == 3)
            {
                o["expression_class"] = "BOUND_BETWEEN";
                o["input"] = children[0]!.DeepClone();
                o["lower"] = children[1]!.DeepClone();
                o["upper"] = children[2]!.DeepClone();
                o["lower_inclusive"] = o["function_data"]?["lower_inclusive"]?.DeepClone() ?? true;
                o["upper_inclusive"] = o["function_data"]?["upper_inclusive"]?.DeepClone() ?? true;
                o.Remove("children");
            }
            else if (type == "OPERATOR_CAST" && children.Count == 1)
            {
                o["expression_class"] = "BOUND_CAST";
                o["child"] = children[0]!.DeepClone();
                o["try_cast"] = o["function_data"]?["try_cast"]?.DeepClone() ?? false;
                o.Remove("children");
            }
        }

        // aggregates that keep an argument in their bind data (string_agg's separator, the fraction of a quantile): 1.x left it out of `children`, 2.0 lists it there as well
        if (type == "BOUND_AGGREGATE" && o["children"] is JsonArray { Count: 2 } aggregateArgs && o["function_data"] is JsonObject data &&
            ((Text(o["name"]) == "string_agg" && data["separator"] is not null) || (Text(o["name"]) is "quantile_cont" or "quantile_disc" && data["quantiles"] is not null)))
            aggregateArgs.RemoveAt(1);

        // the list of names an UNPIVOT unnests was a constant list in 1.x and is list_value('a', 'b') over constants in 2.0
        if (cls == "BOUND_FUNCTION" && Text(o["alias"]) == "unpivot_names" && Text(o["name"]) == "list_value" && o["children"] is JsonArray names && names.All(n => n is JsonObject c && Text(c["type"]) == "VALUE_CONSTANT"))
        {
            var items = new JsonArray(names.Select(n => n!["value"]!.DeepClone()).ToArray());
            var returnType = o["return_type"]?.DeepClone();
            foreach (var key in o.Select(x => x.Key).ToList()) o.Remove(key);
            o["expression_class"] = "BOUND_CONSTANT";
            o["type"] = "VALUE_CONSTANT";
            o["alias"] = "unpivot_names";
            o["value"] = new JsonObject { ["type"] = returnType, ["is_null"] = false, ["value"] = new JsonObject { ["children"] = items } };
        }

        // NOT (a IN (...)) is what 1.x called COMPARE_NOT_IN
        if (type == "OPERATOR_NOT" && o["children"] is JsonArray { Count: 1 } not && not[0] is JsonObject inner && Text(inner["type"]) == "COMPARE_IN")
        {
            o["type"] = "COMPARE_NOT_IN";
            o["children"] = inner["children"]!.DeepClone();
        }

        // NOT over a comparison is the opposite comparison (1.x folded it while binding; the 3-valued logic is the same, NULL stays NULL)
        if (type == "OPERATOR_NOT" && o["children"] is JsonArray { Count: 1 } negated && negated[0] is JsonObject cmp && Opposite.TryGetValue(Text(cmp["type"]) ?? "", out var opposite) && cmp["left"] is not null)
        {
            o["expression_class"] = "BOUND_COMPARISON";
            o["type"] = opposite;
            o["left"] = cmp["left"]!.DeepClone();
            o["right"] = cmp["right"]!.DeepClone();
            o.Remove("children");
        }

        // lead and lag: 1.x kept the offset and default beside the argument, 2.0 lists them as arguments
        if (type is "WINDOW_LAG" or "WINDOW_LEAD" && o["children"] is JsonArray { Count: > 1 } args)
        {
            // an offset of 1 and a NULL default are what 1.x left out when the author did not write them; the text of the lowered query stays the same
            var plainOffset = args[1] is JsonObject off && Text(off["type"]) == "VALUE_CONSTANT" && off["value"]?["value"]?.ToJsonString() == "1";
            var nullDefault = args.Count > 2 && IsNullConstant(args[2]);
            if (!(plainOffset && (args.Count == 2 || nullDefault)))
            {
                o["offset_expr"] = args[1]!.DeepClone();
                if (args.Count > 2 && !nullDefault) o["default_expr"] = args[2]!.DeepClone();
            }
            o["children"] = new JsonArray(args[0]!.DeepClone());
        }
    }
}
