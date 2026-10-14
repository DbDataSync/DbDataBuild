using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild help dialects` (effect: none): what a dialect profile is, its fields with their choices, the built-in dialects as the tool has them, which dialect a connection gets, how to add one, and an example of a
/// proxy in front of several systems. Offline and generated from <see cref="DialectProfiles"/>, so the page cannot disagree with the tool.
/// </summary>
internal static class HelpDialects
{
    private sealed record Field(string Name, IReadOnlyList<string> Choices, string Default, string Meaning, string Used);

    private static readonly Field[] Fields =
    [
        new("extends", [.. DialectProfiles.BuiltIn.Keys.Order(StringComparer.Ordinal), "another profile of the project"], "ansi", "The dialect this one starts from. Each field a profile does not name is the base's.",
            "every field below."),
        new("quote", ["\"", "`", "[ ]"], "\"", "How an identifier is quoted in the SQL the tool writes for this origin (a copy's read).", "the read of a copy; a native query is sent as written."),
        new("parameters", DialectProfiles.ParameterStyles, "positional", "How a parameter is written: `@name`, `?` bound by position, or `$1`. ODBC and Flight SQL always bind by position, so `named_at` is read as `positional` there.",
            "the read of a copy (the watermark) and a native query; the plan records the order the values are bound in."),
        new("ping", ["one SELECT"], "SELECT 1", "What `connection inspect` sends to see that the connection answers.", "`connection inspect`, and the check before an origin's turn in a refresh."),
        new("catalog", DialectProfiles.CatalogStyles, "information_schema",
            "Where the tables, columns and keys are read from. `information_schema`: the standard views. `odbc`: the ODBC driver's own catalog functions (SQLTables, SQLColumns, SQLPrimaryKeys, SQLForeignKeys, SQLStatistics), for a system with no information_schema; an `odbc` connection only. `none`: nothing is read, declare the tables by hand. A Flight SQL server is asked its metadata calls first, then information_schema.",
            "`project import`, the structure check of a copy (DDB-230), `connection inspect`."),
        new("identifier_case", DialectProfiles.IdentifierCases, "preserve", "What a name written without quotes becomes in the system (Oracle: upper, PostgreSQL: lower).",
            "the structure check finds a declared table stored in the other case and says how to spell the declaration; a copy still reads the name as declared, quoted."),
        new("limit", DialectProfiles.LimitStyles, "fetch", "How a bounded read is written: `LIMIT n`, `SELECT TOP n`, `FETCH FIRST n ROWS ONLY`.",
            "the zero-row read that learns the columns of a native query when the driver cannot say them without running it. Nothing else reads an origin with a bound."),
        new("types", ["catalog type name: logical type"], "the base's", "The names the catalog uses for types (lower case) and the logical type each is read as: " + string.Join(", ", DialectProfiles.LogicalTypeNames) + ". A decimal and a varchar take precision, scale and length from the catalog.",
            "`project import` and the structure check; a copy converts by the declared type and never needs them."),
    ];

    private const string Example = """
        dialects:
          clickhouse_sql:                # a backend with no built-in: named after the backend
            extends: ansi
            quote: '`'
            limit: limit
            types: { uint64: BIGINT, string: VARCHAR, datetime64: TIMESTAMP }

        connections:                     # one proxy is several connections, each with the dialect of the system behind it
          erp:        { engine: readonly flightsql postgres }
          sales:      { engine: readonly flightsql sqlserver }
          clicks:     { engine: readonly flightsql clickhouse_sql }
          legacy:     { engine: readonly odbc ansi }

        # each connection has its own login; the proxy picks the system behind it from a header
        #   DBDATABUILD_ERP_READ    = grpc+tls://proxy.example.com:8815;token_env=PROXY_TOKEN;header.x-datasource=erp
        #   DBDATABUILD_SALES_READ  = grpc+tls://proxy.example.com:8815;token_env=PROXY_TOKEN;header.x-datasource=sales
        """;

    public static int Run(CommandSpec spec, TextWriter output)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        output.WriteLine();
        output.WriteLine("A dialect is what the tool needs to know about the SQL a system takes when it only reads from it: an ODBC source, a Flight SQL server or a proxy in front of several systems. It is data, not code,");
        output.WriteLine("and nothing is rendered for it. The dialect belongs to the system a connection reaches, not to the driver or the server in front of it.");
        output.WriteLine();
        output.WriteLine("Which dialect a connection has: the dialect keyword of its `engine` (`readonly flightsql postgres`), else `ansi` for odbc and flightsql. sqlserver, postgres, fabric and duckdb connections bring their own.");
        output.WriteLine();
        output.WriteLine("Fields of a profile under `dialects:` in dbdatabuild.yml:");
        foreach (var f in Fields)
        {
            output.WriteLine($"  {f.Name,-16} {string.Join(" | ", f.Choices)}   (default {f.Default})");
            output.WriteLine($"  {"",-16} {f.Meaning}");
            output.WriteLine($"  {"",-16} used by: {f.Used}");
        }
        output.WriteLine();
        output.WriteLine($"Built in ({string.Join(", ", DialectProfiles.BuiltIn.Keys.Order(StringComparer.Ordinal))}):");
        output.WriteLine($"  {"name",-10} {"quote",-6} {"parameters",-11} {"catalog",-19} {"identifier_case",-16} {"limit",-6} types");
        var builtIn = DialectProfiles.BuiltIn.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        foreach (var p in builtIn)
            output.WriteLine($"  {p.Name,-10} {Quote(p),-6} {Choice(DialectProfiles.ParameterStyles, (int)p.Parameters),-11} {Choice(DialectProfiles.CatalogStyles, (int)p.Catalog),-19} {Choice(DialectProfiles.IdentifierCases, (int)p.Case),-16} {Choice(DialectProfiles.LimitStyles, (int)p.Limit),-6} {p.Types.Count}");
        output.WriteLine("  The engines' own rows are the tool's own SQL for them (a connection of `postgres` is not read through this table). On odbc and flightsql a dialect is read with positional parameters.");
        output.WriteLine();
        output.WriteLine("Extending: name a system that has no built-in after the system (a lower-case name that is not a keyword of `engine`), `extends:` the nearest dialect, change only what differs. A profile that is not valid is DDB-456 when the project loads.");
        output.WriteLine("A system behind a proxy is described by its own dialect, not by the proxy's: the proxy has many.");
        output.WriteLine();
        output.WriteLine("A Flight SQL connection with no dialect keyword is asked what it is by `project update`, which commits the answer in dialects.lock.yml (an explicit keyword always wins).");
        output.WriteLine();
        output.WriteLine("Example:");
        foreach (var line in Example.Split('\n')) output.WriteLine(line.TrimEnd().Length == 0 ? "" : "  " + line.TrimEnd());
        output.WriteLine();
        output.WriteLine("See `help code DDB-456` for the rules, and docs/operations.md (ODBC setup, Flight SQL setup).");

        output.Payload("fields", Fields.Select(f => new { name = f.Name, choices = f.Choices, @default = f.Default, meaning = f.Meaning, used_by = f.Used }).ToList());
        output.Payload("built_in", builtIn.Select(p => new
        {
            name = p.Name, quote = Quote(p), parameters = Choice(DialectProfiles.ParameterStyles, (int)p.Parameters), ping = p.Ping, catalog = Choice(DialectProfiles.CatalogStyles, (int)p.Catalog),
            identifier_case = Choice(DialectProfiles.IdentifierCases, (int)p.Case), limit = Choice(DialectProfiles.LimitStyles, (int)p.Limit), types = p.Types.Count,
        }).ToList());
        output.Payload("keywords", new { access = new[] { ConnectionKeywords.ReadOnlyKeyword }, driver = ConnectionKeywords.Drivers, dialect = ConnectionKeywords.BuiltInDialects });
        output.Payload("example", string.Join("\n", Example.Split('\n').Select(l => l.TrimEnd())));
        return CliApp.ExitOk;
    }

    private static string Quote(DialectProfile p) => p.QuoteOpen == p.QuoteClose ? p.QuoteOpen : p.QuoteOpen + " " + p.QuoteClose;

    private static string Choice(IReadOnlyList<string> choices, int index) => choices[index];
}
