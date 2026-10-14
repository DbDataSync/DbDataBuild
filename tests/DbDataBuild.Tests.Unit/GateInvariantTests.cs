using System.Text.RegularExpressions;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// DESIGN.md 9.3: "A test enumerates call paths to ExecuteNonQuery and fails on any path not routed through the gate."
/// This is a source scan, not a call-graph analysis: it pins the places that may hand text to a database driver, and the only project that may reference one.
/// </summary>
public class GateInvariantTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DbDataBuild.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // the only files that may touch a command object: the write executor and the read session
    private static readonly string[] DriverFiles = ["DbDataBuild.Execution/MutationGate.cs", "DbDataBuild.Execution/ReadSession.cs"];

    private static string Rel(string f) => Path.GetRelativePath(Path.Combine(Root(), "src"), f).Replace('\\', '/');

    [Fact]
    public void Commands_are_created_and_executed_only_in_the_gate_and_the_read_session()
    {
        var pattern = new Regex(@"\b(ExecuteNonQuery(Async)?|ExecuteScalar(Async)?|ExecuteReader(Async)?|CreateCommand|SqlCommand|NpgsqlCommand|DbCommand|NpgsqlBatch|SqlBatch|DbBatch|SqlBulkCopy|BeginBinaryImport(Async)?|BeginBinaryExport(Async)?|BeginTextImport(Async)?|BeginTextExport(Async)?|BeginRawBinaryCopy(Async)?)\b");
        var offenders = SourceFiles()
            .Where(f => !Rel(f).StartsWith("DbDataBuild.Targets.DuckDb/") && !Rel(f).StartsWith("DbDataBuild.Sample/")) // the in-memory DuckDB describer and sample runner have no target connection
            .Where(f => !DriverFiles.Contains(Rel(f)) && pattern.IsMatch(File.ReadAllText(f)))
            .Select(Rel).ToList();
        Assert.True(offenders.Count == 0, "Statements must go through MutationGate or ReadSession. Found driver calls in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void A_flight_sql_statement_is_sent_only_by_the_flight_sql_row_source_and_only_the_execution_project_knows_the_client()
    {
        // the Flight SQL client is a driver like the ADO.NET ones: its statements (ExecuteAsync, PrepareAsync, the metadata calls) are reached only through ReadSession
        var client = new Regex(@"\b(FlightSqlClient|PreparedStatement|FlightClient|GrpcChannel)\b|Apache\.Arrow\.Flight");
        var offenders = SourceFiles().Where(f => client.IsMatch(File.ReadAllText(f)))
            .Where(f => Rel(f) is not ("DbDataBuild.Execution/FlightSqlRowSource.cs")).Select(Rel).ToList();
        Assert.True(offenders.Count == 0, "The Flight SQL client may be used only by FlightSqlRowSource.cs, which only ReadSession reaches. Found it in: " + string.Join(", ", offenders));
        var projects = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => Regex.IsMatch(File.ReadAllText(p), @"PackageReference Include=""Apache\.Arrow")).Select(Rel).ToList();
        Assert.Equal(["DbDataBuild.Execution/DbDataBuild.Execution.csproj"], projects);
        var opener = File.ReadAllText(Path.Combine(Root(), "src/DbDataBuild.Execution/FlightSqlRowSource.cs"));
        Assert.Contains("internal sealed class FlightSqlRowSource", opener);                         // not public: no way to a Flight SQL connection but ReadSession
    }

    [Fact]
    public void Only_the_execution_project_references_a_target_database_driver()
    {
        var driver = new Regex(@"^\s*using\s+(Microsoft\.Data\.SqlClient|System\.Data\.SqlClient|Npgsql|Microsoft\.Data\.SqlClient\.Server)\b|\b(Microsoft\.Data\.SqlClient|Npgsql)\.", RegexMultiline);
        var usingDriver = SourceFiles().Where(f => driver.IsMatch(File.ReadAllText(f))).Select(Rel).ToList();
        Assert.All(usingDriver, f => Assert.StartsWith("DbDataBuild.Execution/", f));

        var projects = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => Regex.IsMatch(File.ReadAllText(p), @"PackageReference Include=""(Microsoft\.Data\.SqlClient|System\.Data\.SqlClient|Npgsql)""")).Select(Rel).ToList();
        Assert.Equal(["DbDataBuild.Execution/DbDataBuild.Execution.csproj"], projects);
    }

    [Fact]
    public void The_driver_connection_openers_are_not_public()
    {
        // a write-capable connection must not be obtainable by calling a public method outside the gate
        var text = File.ReadAllText(Path.Combine(Root(), "src/DbDataBuild.Execution/Logins.cs"));
        Assert.Contains("internal async Task<DbConnection> OpenAsync", text);
        Assert.DoesNotContain("public async Task<DbConnection>", text);
        Assert.DoesNotContain("public Task<DbConnection>", text);
    }

    private const RegexOptions RegexMultiline = RegexOptions.Multiline;
}
