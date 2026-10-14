using System.Text;

namespace DbDataBuild.Models;

/// <summary>
/// Writes the parameters of an origin's read the way the origin's dialect takes them. A read is composed with `@name` placeholders; an origin whose dialect takes `?` or `$1` gets each one rewritten, and the plan records the
/// <b>order</b> in which the values are bound (a name used twice in the text is passed twice, for `?`). `@` inside a string literal, a quoted identifier or a comment is not a placeholder.
/// </summary>
public static class ParameterPlaceholders
{
    /// <param name="names">The placeholders the text may contain (without `@`); any other `@word` is left alone.</param>
    /// <returns>The text, and the names in the order they are bound: one entry per `?` (positional), one per distinct `$n` (numbered), none for `@name` (named parameters bind by name).</returns>
    public static (string Text, IReadOnlyList<string> Order) Rewrite(string text, IReadOnlyCollection<string> names, DialectProfile dialect)
    {
        if (dialect.Parameters == ParameterStyle.NamedAt || names.Count == 0) return (text, []);
        var sb = new StringBuilder(text.Length);
        var order = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\'' or '"' or '`') { var end = EndOfQuoted(text, i, c); sb.Append(text, i, end - i); i = end; continue; }
            if (c == '[' && dialect.QuoteOpen == "[") { var end = EndOfQuoted(text, i, ']'); sb.Append(text, i, end - i); i = end; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') { var end = text.IndexOf('\n', i); end = end < 0 ? text.Length : end; sb.Append(text, i, end - i); i = end; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); end = end < 0 ? text.Length : end + 2; sb.Append(text, i, end - i); i = end; continue; }
            if (c == '@' && i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_'))
            {
                var j = i + 1;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                var name = text[(i + 1)..j];
                if (names.Contains(name))
                {
                    if (dialect.Parameters == ParameterStyle.Positional) order.Add(name);
                    else if (!order.Contains(name)) order.Add(name);
                    sb.Append(dialect.Placeholder(name, order.IndexOf(name) + 1));
                    i = j;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return (sb.ToString(), order);
    }

    // a quote is closed by itself; a doubled quote inside is an escaped one (a `]` is doubled the same way)
    private static int EndOfQuoted(string text, int start, char close)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == close)
            {
                if (i + 1 < text.Length && text[i + 1] == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }
}
