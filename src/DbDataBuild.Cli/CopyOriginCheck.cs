using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// The check of one origin of a copy against the mapped model that declares it (version skew between the systems of one application), made by the apply just before that origin's own transfer. An origin whose table
/// lost a column or changed a type the copy reads is named, with what differs; the copy's `on_mismatch` decides whether that stops the apply (`fail`, the default) or leaves that origin out (`skip`), never silently.
/// An origin whose login is not in the environment cannot be described, and the apply says so. Only mapped origins are checked: a model's shape is the business of the plan that builds it.
/// </summary>
internal static class CopyOriginCheck
{
    /// <param name="Findings">What to say: notes and warnings, and the error DDB-230 when the origin differs and the copy says `fail`.</param>
    /// <param name="Skipped">The origin is to be left out (`on_mismatch: skip`).</param>
    public sealed record Result(IReadOnlyList<Diagnostic> Findings, bool Skipped);

    public static async Task<Result> RunOneAsync(ProjectContext ctx, ModelDefinition copy, CopyOrigin origin, Func<string, string?> env)
    {
        var findings = new List<Diagnostic>();
        var none = new Result(findings, false);
        var declared = ctx.Project.MappedModels.Concat(ctx.Project.NativeModels).FirstOrDefault(d => string.Equals(d.Name, copy.From, StringComparison.OrdinalIgnoreCase));
        // a model the project builds on the origin is declared by its own columns: the origin's table is what that model's plan made (or it is not built yet)
        var builtModel = declared == null && ctx.Project.Models.FirstOrDefault(m => string.Equals(m.Name, copy.From, StringComparison.OrdinalIgnoreCase) && !m.IsCopy) is { } model;
        if (builtModel) declared = new DeclaredModel(ctx.Project.Models.First(m => string.Equals(m.Name, copy.From, StringComparison.OrdinalIgnoreCase)).Name, ctx.Project.Models.First(m => string.Equals(m.Name, copy.From, StringComparison.OrdinalIgnoreCase)).Columns, []);
        if (declared == null) return none;
        if (declared.Native is { Access: NativeQuery.Command }) return none;      // a command is not described: the transfer's own check is its net

        var (login, missing) = LoginSettings.FromEnvironment(ctx.Config, origin.Connection, Login.Read, env);
        if (login == null)
        {
            findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), $"{copy.Name}: the origin `{origin.Connection}` was not checked against `{copy.From}`: {missing?.Found}") with { SeverityOverride = Severity.Note });
            return none;
        }
        if (login.Dialect.Catalog == CatalogStyle.None && !declared.IsNative)
        {
            findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), $"{copy.Name}: the origin `{origin.Connection}` has the dialect `{login.Dialect.Name}` (`catalog: none`), so `{copy.From}` was not checked against its declaration; the transfer's own check of the columns it reads is the net.") with { SeverityOverride = Severity.Note });
            return none;
        }
        IReadOnlyList<string>? differences;
        try { differences = declared.IsNative ? await NativeShapeCheck.DifferencesAsync(ctx.Config, declared, origin.Connection, login) : await Differences(login, origin, declared); }
        catch (CatalogUnavailableException ex)
        {
            // a Flight SQL server with no catalog to read: said, not guessed
            findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), $"{copy.Name}: `{copy.From}` was not checked against its declaration: {ex.Message}") with { SeverityOverride = Severity.Note });
            return none;
        }
        if (differences == null)
        {
            findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), $"{copy.Name}: the engine on `{origin.Connection}` could not describe `{copy.From}`, so its columns were not checked.") with { SeverityOverride = Severity.Warning });
            return none;
        }
        if (differences.Count == 0) return none;
        if (builtModel && differences is [{ } only] && only == "the table does not exist there")
        {
            findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), $"{copy.Name}: `{copy.From}` is not built on `{origin.Connection}` yet, so it was not checked against its declaration (plan and apply it there first).") with { SeverityOverride = Severity.Warning });
            return none;
        }
        var text = $"{copy.Name}: `{copy.From}` on `{origin.Connection}` differs from its declaration: {string.Join("; ", differences)}.";
        if (copy.OnMismatch == CopySlice.Skip)
        {
            findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), text + " The origin is left out of this apply (`on_mismatch: skip`).") with { SeverityOverride = Severity.Warning });
            return new Result(findings, true);
        }
        findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), text));
        return none;
    }

    private static async Task<List<string>> Differences(LoginSettings login, CopyOrigin origin, DeclaredModel declared)
    {
        await using var read = await ReadSession.OpenAsync(login);
        var (schema, name) = (origin.Table[..origin.Table.IndexOf('.')], origin.Table[(origin.Table.IndexOf('.') + 1)..]);
        // a name written without quotes is looked up the way the origin folds one (`identifier_case`); the read quotes the name as declared, so a declaration that is not spelled as stored is said
        var (shape, storedAs) = await CatalogReader.FindAsync(read, origin.Engine, schema, name);
        if (shape == null) return [$"the table does not exist there"];
        if (storedAs != null)
            return [$"the table is stored as `{storedAs}` there (the dialect `{login.Dialect.Name}` folds a name without quotes to {(login.Dialect.Case == IdentifierCase.Upper ? "upper" : "lower")} case), and the copy reads the name as declared: declare it as `{storedAs}`"];
        var live = MappedModelImport.Describe(origin.Engine, shape, null, dialect: login.Dialect);
        var now = MappedModelImport.ToDescriptor(live, declared);
        // what a copy reads: a column that is gone, a type that changed, a column that can now be NULL where the declaration says it cannot; an added column is not read, so it is no difference
        return MappedModelImport.Compare(declared, now)
            // a Flight SQL server's Arrow schemas may mark every column nullable (the one tried does), so a declared NOT NULL cannot be held against them
            .Where(c => c.Kind is MappedModelChangeKind.ColumnRemoved or MappedModelChangeKind.TypeChanged || (origin.Engine != "flightsql" && c is { Kind: MappedModelChangeKind.NullabilityChanged, Detail: "now nullable" }))
            .Select(c => c.Kind switch
            {
                MappedModelChangeKind.ColumnRemoved => $"column `{c.Column}` is gone",
                MappedModelChangeKind.TypeChanged => $"column `{c.Column}` changed type ({c.Detail})",
                _ => $"column `{c.Column}` can now be NULL",
            }).ToList();
    }
}
