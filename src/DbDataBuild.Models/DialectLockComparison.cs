namespace DbDataBuild.Models;

public static class DialectLockComparison
{
    /// <summary>
    /// What differs between a committed entry and the dialect chosen from what the server reports now, in words; empty when the profile hash is the same (a changed server version alone is no difference: the committed
    /// text does not depend on it). The server's name is named when it changed, because it is usually why the rest did.
    /// </summary>
    public static IReadOnlyList<string> Differences(DialectLockEntry committed, DiscoveredDialect now, ServerFacts nowFacts)
    {
        if (DialectDiscovery.Hash(now.Profile) == committed.Hash) return [];
        var found = new List<string>();
        var current = DialectLockEntry.From(committed.Connection, nowFacts, now);
        if (committed.Dialect != current.Dialect) found.Add($"the dialect was `{committed.Dialect}` and is now `{current.Dialect}`");
        if (committed.Quote != current.Quote) found.Add($"the identifier quote was `{committed.Quote}` and is now `{current.Quote}`");
        if (committed.IdentifierCase != current.IdentifierCase) found.Add($"the identifier case was {committed.IdentifierCase} and is now {current.IdentifierCase}");
        if (committed.Parameters != current.Parameters) found.Add($"parameters were {committed.Parameters} and are now {current.Parameters}");
        if (committed.Limit != current.Limit) found.Add($"the limit style was {committed.Limit} and is now {current.Limit}");
        if (committed.Ping != current.Ping) found.Add("the ping statement changed");
        if (committed.Catalog != current.Catalog) found.Add($"the catalog was {committed.Catalog} and is now {current.Catalog}");
        if (found.Count == 0) found.Add("the profile changed");
        if (committed.Reported.ServerName != nowFacts.ServerName) found.Add($"the server was {Name(committed.Reported.ServerName)} and now says {Name(nowFacts.ServerName)}");
        return found;
    }

    private static string Name(string? server) => server == null ? "nothing" : $"`{server}`";
}
