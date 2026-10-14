namespace DbDataBuild.Core;

/// <summary>Single source for every product-derived name, so a rename is mechanical.</summary>
public static class ProductInfo
{
    public const string Name = "DbDataBuild";
    public const string Cli = "dbdatabuild";
    public const string ConfigFile = "dbdatabuild.yml";

    /// <summary>The project file a folder may hold: the same sections as the root file, layered onto it for everything beneath the folder. It is not a model.</summary>
    public const string FolderConfigFile = "_dbdatabuild.yml";

    /// <summary>The committed file `project update` writes: the dialect each `readonly flightsql` connection without a dialect keyword was found to have (docs/research/read-origins.md section 10). Offline commands read it; only `project update` writes it.</summary>
    public const string DialectLockFile = "dialects.lock.yml";
    public const string TrackingSchemaName = "dbdatabuild";
    public const string DiagnosticPrefix = "DDB-";
    public const string NamespacePrefix = "DbDataBuild";

    /// <summary>
    /// The release's version: the one `scripts/publish.sh` stamps (`VERSION`), and in a development build the one in Directory.Build.props, which a release bumps. It is in plans, in the JSON documents and in
    /// the tracking tables as provenance. It is not in anything rendered: a rendered file changes when its text changes, not when the tool is upgraded.
    /// </summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
