using DbDataBuild.Apply;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// Plan-time check of a native select against its declaration: the engine is asked for the result shape of the native text (schema only, nothing runs) and the column names and types are compared with the declared ones (not nullability: an engine calls any computed column nullable, so NOT NULL stays an assertion), as a
/// mapped origin's table is (DDB-230). A declaration is an assertion the tool cannot check against DuckDB, so this is the one place it is held to the engine's answer. A command is not described (dynamic SQL and
/// temporary tables make the answer unreliable): the transfer's own name and count check at apply is its net. A text the engine cannot describe is reported as not checked, never as a difference.
/// </summary>
internal static class NativeShapeCheck
{
    /// <summary>What differs (empty when the shape agrees), or null when the engine could not say.</summary>
    public static async Task<IReadOnlyList<string>?> DifferencesAsync(ProjectConfig config, DeclaredModel native, string connection, LoginSettings login)
    {
        var use = NativeInline.Prepare(native, config);
        var values = NativeInline.Values(native, use, config, connection);
        var parameters = use.Parameters.Where(p => values.ContainsKey(p.Key)).Select(p => ApplyEngine.ToGate(new PlanParameter(p.Placeholder, values[p.Key].Type, "parameter", values[p.Key].Value))).ToList();
        var engine = config.EngineOf(connection) ?? connection;
        // an origin that takes its values by position gets the text the transfer will send, and the values in the order the placeholders appear
        var (text, order) = ParameterPlaceholders.Rewrite(use.Text, use.Parameters.Select(p => p.Placeholder).ToList(), login.Dialect);
        if (order.Count > 0) parameters = order.Select(name => parameters.First(p => p.Name == name)).ToList();
        await using var read = await ReadSession.OpenAsync(login);
        var columns = await read.DescribeAsync(text, engine, parameters);
        if (columns == null) return null;
        var live = MappedModelImport.Describe(engine, new ObjectShape("native", native.Name, ObjectKind.Table, columns, []), null, dialect: login.Dialect);
        var now = MappedModelImport.ToDescriptor(live, native);
        return MappedModelImport.Compare(native, now)
            .Where(c => c.Kind is MappedModelChangeKind.ColumnRemoved or MappedModelChangeKind.TypeChanged)       // not nullability: an engine calls any computed column nullable, so a declared NOT NULL is the author's assertion
            .Select(c => c.Kind == MappedModelChangeKind.ColumnRemoved ? $"column `{c.Column}` is not returned" : $"column `{c.Column}` is returned as another type ({c.Detail})").ToList();
    }

    /// <summary>The native selects the plan's models read on <paramref name="target"/> (inlined, or landed by a local copy), each against the engine.</summary>
    public static IReadOnlyList<Diagnostic> Run(ProjectContext ctx, IEnumerable<string> readNames, string target, Func<string, string?> env)
    {
        var findings = new List<Diagnostic>();
        foreach (var native in readNames.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => ctx.Project.NativeModels.FirstOrDefault(d => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase)))
                     .Where(d => d is { Native.Access: NativeQuery.Select }).OrderBy(d => d!.Name, StringComparer.Ordinal))
        {
            var (login, missing) = LoginSettings.FromEnvironment(ctx.Config, target, Login.Read, env);
            if (login == null) { findings.Add(Finding(native!, $"{native!.Name}: not checked against the engine on `{target}`: {missing?.Found}", true)); continue; }
            var differences = Task.Run(() => DifferencesAsync(ctx.Config, native!, target, login)).GetAwaiter().GetResult();
            if (differences == null) findings.Add(Finding(native!, $"{native!.Name}: the engine on `{target}` could not describe the native text, so its columns were not checked.", true));
            else if (differences.Count > 0) findings.Add(Finding(native!, $"{native!.Name} on `{target}` returns something other than it declares: {string.Join("; ", differences)}.", false));
        }
        return findings;
    }

    internal static Diagnostic Finding(DeclaredModel native, string text, bool warning)
    {
        var d = new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(native.Native?.File ?? native.Name, native.Native?.Line ?? 0, 0), text);
        return warning ? d with { SeverityOverride = Severity.Warning } : d;
    }
}
