namespace DbDataBuild.Execution;

/// <summary>
/// The value of an ODBC connection's login: a connection string (`Driver={PostgreSQL Unicode};Server=...;UID=...;PWD=...`), a DSN with its credentials (`DSN=name;UID=...;PWD=...`) or just the name of a DSN. It is passed
/// to the driver manager as written (a bare name becomes `DSN=name`); the tool only reads it to say who the login is, and never prints it.
/// </summary>
public static class OdbcLogin
{
    private static readonly string[] UserKeys = ["uid", "user", "username", "user id", "userid"];

    /// <summary>The `key=value` pairs of a connection string, in order, keys lower case. A value in braces may hold `;`, and `}}` is a `}` inside it. A part with no `=` is skipped.</summary>
    public static IReadOnlyList<(string Key, string Value)> Parse(string text)
    {
        var pairs = new List<(string, string)>();
        var i = 0;
        while (i < text.Length)
        {
            var eq = text.IndexOf('=', i);
            var semicolon = text.IndexOf(';', i);
            if (eq < 0) break;
            if (semicolon >= 0 && semicolon < eq) { i = semicolon + 1; continue; }              // a part without a value
            var key = text[i..eq].Trim().ToLowerInvariant();
            i = eq + 1;
            while (i < text.Length && text[i] == ' ') i++;
            string value;
            if (i < text.Length && text[i] == '{')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '}') { if (i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i += 2; continue; } i++; break; }
                    sb.Append(text[i++]);
                }
                value = sb.ToString();
                var next = text.IndexOf(';', i);
                i = next < 0 ? text.Length : next + 1;
            }
            else
            {
                var end = text.IndexOf(';', i);
                value = (end < 0 ? text[i..] : text[i..end]).Trim();
                i = end < 0 ? text.Length : end + 1;
            }
            if (key.Length > 0) pairs.Add((key, value));
        }
        return pairs;
    }

    /// <summary>What the driver manager is given: the value as written, or `DSN=name` when it is only a name.</summary>
    public static string Normalize(string value)
    {
        var text = value.Trim();
        return text.Contains('=', StringComparison.Ordinal) ? text : "DSN=" + text;
    }

    /// <summary>The user the login names (`UID`, `User`, `Username`), or null when it names none (a trusted connection, or a DSN that holds the credentials).</summary>
    public static string? UserOf(string value)
    {
        foreach (var (key, v) in Parse(Normalize(value)))
            if (UserKeys.Contains(key) && v.Length > 0) return v;
        return null;
    }
}
