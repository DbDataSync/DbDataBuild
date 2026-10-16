using System.Text;

namespace DbDataBuild.Lowering;

/// <summary>
/// strftime(ts, 'literal format') and format('literal with {}', ...) written out as the pieces of their text, so that no engine's format function (T-SQL's FORMAT, PostgreSQL's TO_CHAR and format) has to agree with
/// DuckDB's letters: neither does, apart from %Y-%m-%d and %H:%M:%S. A format with anything not listed here is refused with the specifier named (DDB-324): the specifiers whose rule is not exact on both engines
/// (%U %W %V %G week numbers, %Z %z, %c %x %X) and format's width and type specifications ({:05d}).
/// </summary>
internal static class TextFormats
{
    /// <summary>The expression for each strftime specifier; U+0001 stands for the timestamp. Zero padded numbers are the right characters of '0...0' followed by the digits.</summary>
    private static readonly Dictionary<string, string> Specifiers = BuildSpecifiers();

    private static string Pad(string number, int width) => $"right('{new string('0', width - 1)}' || CAST({number} AS VARCHAR), {width})";

    private static Dictionary<string, string> BuildSpecifiers()
    {
        string Part(string p) => $"date_part('{p}', \u0001)";
        static string Names(string selector, int first, params string[] names) => $"CASE {selector} " + string.Join(" ", names.Select((n, i) => $"WHEN {i + first} THEN '{n}'")) + " END";
        var hour12 = "CASE WHEN date_part('hour', \u0001) % 12 = 0 THEN 12 ELSE date_part('hour', \u0001) % 12 END";
        string[] days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
        string[] months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
        return new Dictionary<string, string>
        {
            ["Y"] = Pad(Part("year"), 4), ["y"] = Pad($"({Part("year")} % 100)", 2), ["m"] = Pad(Part("month"), 2), ["d"] = Pad(Part("day"), 2),
            ["H"] = Pad(Part("hour"), 2), ["M"] = Pad(Part("minute"), 2), ["S"] = Pad(Part("second"), 2), ["f"] = Pad($"({Part("microsecond")} % 1000000)", 6),
            ["j"] = Pad(Part("doy"), 3), ["I"] = Pad($"({hour12})", 2), ["p"] = "CASE WHEN date_part('hour', \u0001) < 12 THEN 'AM' ELSE 'PM' END",
            ["-d"] = $"CAST({Part("day")} AS VARCHAR)", ["-m"] = $"CAST({Part("month")} AS VARCHAR)", ["-H"] = $"CAST({Part("hour")} AS VARCHAR)", ["-M"] = $"CAST({Part("minute")} AS VARCHAR)",
            ["-S"] = $"CAST({Part("second")} AS VARCHAR)", ["-j"] = $"CAST({Part("doy")} AS VARCHAR)", ["-I"] = $"CAST({hour12} AS VARCHAR)",
            ["A"] = Names(Part("dow"), 0, days), ["a"] = Names(Part("dow"), 0, days.Select(d => d[..3]).ToArray()),
            ["B"] = Names(Part("month"), 1, months), ["b"] = Names(Part("month"), 1, months.Select(m => m[..3]).ToArray()),
            ["w"] = $"CAST({Part("dow")} AS VARCHAR)", ["u"] = $"CAST({Part("isodow")} AS VARCHAR)", ["%"] = "'%'",
        };
    }

    public static IEnumerable<string> SupportedStrftime => Specifiers.Keys.Select(k => "%" + k);

    /// <summary>The text of `strftime(timestamp, format)` as one expression, or throws <see cref="LoweringException"/> naming the specifier it cannot write exactly.</summary>
    public static string Strftime(string timestamp, string format)
    {
        var pieces = new List<string>();
        var literal = new StringBuilder();
        void Flush() { if (literal.Length > 0) { pieces.Add("'" + literal.ToString().Replace("'", "''") + "'"); literal.Clear(); } }
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%') { literal.Append(format[i]); continue; }
            if (i + 1 >= format.Length) throw new LoweringException("strftime format ends in a lone %");
            var key = format[i + 1] == '-' && i + 2 < format.Length ? "-" + format[i + 2] : format[i + 1].ToString();
            i += key.Length;
            if (key == "%") { literal.Append('%'); continue; }
            if (!Specifiers.TryGetValue(key, out var expression))
                throw new LoweringException($"the strftime specifier %{key} (no exact translation: the supported ones are {string.Join(" ", SupportedStrftime)}; week numbers (%U %W %V %G), time zones (%Z %z) and the locale formats (%c %x %X) differ between engines)");
            Flush();
            pieces.Add(expression.Replace("\u0001", timestamp));
        }
        Flush();
        if (pieces.Count == 0) pieces.Add("''");
        return $"CASE WHEN {timestamp} IS NULL THEN NULL ELSE ({string.Join(" || ", pieces)}) END";
    }

    /// <summary>The text of `format('literal {} ...', a, b)` as a concatenation. Each argument is already text: only the placeholders `{}` (and `{{`, `}}`) are understood.</summary>
    public static string Format(string template, IReadOnlyList<string> arguments)
    {
        var pieces = new List<string>();
        var literal = new StringBuilder();
        var next = 0;
        void Flush() { if (literal.Length > 0) { pieces.Add("'" + literal.ToString().Replace("'", "''") + "'"); literal.Clear(); } }
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { literal.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { literal.Append('}'); i++; continue; }
            if (c == '{')
            {
                var close = template.IndexOf('}', i);
                var spec = close < 0 ? template[i..] : template[i..(close + 1)];
                if (spec != "{}") throw new LoweringException($"the format specification {spec} (only {{}} is translated: the engines' format functions have other placeholders, and a width or type such as {{:05d}} has no exact counterpart)");
                if (next >= arguments.Count) throw new LoweringException("format has more placeholders than arguments");
                Flush();
                pieces.Add(arguments[next++]);
                i++;
                continue;
            }
            if (c == '}') throw new LoweringException("format has a } without a {");
            literal.Append(c);
        }
        Flush();
        if (next != arguments.Count) throw new LoweringException("format has more arguments than placeholders");
        if (pieces.Count == 0) pieces.Add("''");
        return $"({string.Join(" || ", pieces)})";
    }
}
