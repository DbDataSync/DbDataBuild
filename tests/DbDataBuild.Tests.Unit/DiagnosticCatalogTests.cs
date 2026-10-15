using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class DiagnosticCatalogTests
{
    [Fact]
    public void Codes_are_unique_well_formed_and_fully_described()
    {
        Assert.Equal(DiagnosticCatalog.All.Count, DiagnosticCatalog.All.Select(d => d.Code).Distinct().Count());
        foreach (var d in DiagnosticCatalog.All)
        {
            Assert.Matches(@"^DDB-[1-69]\d\d$", d.Code);
            Assert.False(string.IsNullOrWhiteSpace(d.Title));
            Assert.False(string.IsNullOrWhiteSpace(d.Supported));
            Assert.False(string.IsNullOrWhiteSpace(d.Fix));
            Assert.False(string.IsNullOrWhiteSpace(d.Explanation));
        }
    }

    // One fixture per code (DESIGN.md 15.5). DDB-108 is covered in ProjectValidatorTests, DDB-109 in ProjectConfigTests, DDB-900 in CliTests,
    // DDB-310..312 in CollationCheckerTests, DDB-410..414 in QuestionResolverTests, DDB-218..220 in InferenceTests, DDB-221 in DefineEngineTests, DDB-317..320 in LoadRendererTests, DDB-321 in DdlGeneratorTests, DDB-322 in PlannerTests, DDB-323 in HookPlanningTests, DDB-324 in LoweringIntegrationTests, DDB-325 in ClockOverridesTests and MacroTests,DDB-424 in RenderCommandTests, DDB-420 in DefineEngineTests, DDB-446..448 in RefreshTests, DDB-244, DDB-449 and DDB-450 in RefreshPlansTests, DDB-451..453 in InputResolversTests, DDB-454 in InputMovedTests, DDB-455 and DDB-456 in ReadOriginsTests, DDB-457 to DDB-460 in DialectDiscoveryTests and ProjectUpdateDialectTests, DDB-461 in ProjectUpdateDialectTests, DDB-421/422 in DefinitionTextTests, DDB-222 and DDB-430..434 in PlannerTests, DDB-435 in PlanDocumentTests, DDB-443 in ColumnHistoryTests, DDB-436..442 in ApplyEngineTests (conformance) and ApplyCommandTests, DDB-501 in MutationGateTests, DDB-502..504 in MutationGateTests/ReadGuardTests, DDB-505 in TrackingStoreTests, DDB-301..308 in MatrixLinterTests (one fixture per matrix row plus the uncovered/parse cases).
    public static TheoryData<string, string> Fixtures => new()
    {
        { "DDB-101", "a: [1, 2\n" },
        { "DDB-102", "name: a\nname: b\n" },
        { "DDB-103", "a: &x 1\n" },
        { "DDB-104", ValidModel + "\nbogus: 1\n" },
        { "DDB-105", "name: marts.fct_orders\n" },
        { "DDB-106", ValidModel.Replace("incremental_by_unique_key", "nope") },
        { "DDB-107", ValidModel.Replace("name: marts.fct_orders", "name: marts.other") },
        { "DDB-214", ValidModel.Replace("    unique_key: [order_id]\n", "") },
        { "DDB-215", ValidModel.Replace("incremental_by_unique_key:\n    unique_key: [order_id]", "incremental_by_time_range") },
        { "DDB-216", ValidModel.Replace("grain: [order_id]", "grain: [customer_id]") },
        { "DDB-217", ValidModel.Replace("grain: [order_id]", "grain: [order_id, ghost]") },
        { "DDB-248", ValidModel.Replace("incremental_by_unique_key:\n    unique_key: [order_id]", "full: {}\n  view: {}") },
        { "DDB-243", ValidModel + "\nproperties:\n  \"two words\": 1\n" },
        { "DDB-245", ValidModel + "\nrefresh:\n  hourly: [default, default]\n" },
        { "DDB-246", ValidModel + "\nrefresh:\n  hourly: nope\n" },
    };

    [Theory, MemberData(nameof(Fixtures))]
    public void Fixture_produces_code_with_location_and_fix(string code, string yaml)
    {
        var (_, diags) = Load(yaml);
        var d = diags.First(x => x.Code == code);
        Assert.Equal("models/marts/fct_orders.yml", d.Location.File);
        Assert.True(d.Location.Line >= 1);
        var text = DiagnosticFormatter.Format(d);
        Assert.Contains("Supported:", text);
        Assert.Contains("Fix:", text);
        Assert.Contains($"dbdatabuild explain {code}", text);
        Assert.DoesNotContain("   at ", text); // no stack trace
    }

    [Fact]
    public void Every_code_has_a_fixture_or_a_documented_other_test()
    {
        var covered = Fixtures.Select(f => (string)f[0]).Concat(["DDB-108", "DDB-109", "DDB-247", "DDB-223", "DDB-224", "DDB-225", "DDB-226", "DDB-227", "DDB-228", "DDB-229", "DDB-230", "DDB-231", "DDB-232", "DDB-233", "DDB-234", "DDB-235", "DDB-236", "DDB-237", "DDB-238", "DDB-239", "DDB-240", "DDB-241", "DDB-242", "DDB-244", "DDB-445", "DDB-446", "DDB-447", "DDB-448", "DDB-449", "DDB-450", "DDB-451", "DDB-452", "DDB-453", "DDB-454", "DDB-455", "DDB-456", "DDB-457", "DDB-458", "DDB-459", "DDB-460", "DDB-461", "DDB-900", "DDB-301", "DDB-302", "DDB-303", "DDB-304", "DDB-305", "DDB-306", "DDB-307", "DDB-308", "DDB-310", "DDB-311", "DDB-312", "DDB-410", "DDB-411", "DDB-412", "DDB-413", "DDB-414", "DDB-218", "DDB-219", "DDB-220", "DDB-221", "DDB-317", "DDB-318", "DDB-319", "DDB-320", "DDB-321", "DDB-322", "DDB-323", "DDB-324", "DDB-325", "DDB-420", "DDB-424", "DDB-421", "DDB-422", "DDB-222", "DDB-430", "DDB-431", "DDB-432", "DDB-433", "DDB-434", "DDB-435", "DDB-436", "DDB-437", "DDB-438", "DDB-439", "DDB-440", "DDB-441", "DDB-442", "DDB-443", "DDB-601", "DDB-602", "DDB-603", "DDB-501", "DDB-502", "DDB-503", "DDB-504", "DDB-505"]).ToHashSet();
        Assert.Equal(DiagnosticCatalog.All.Select(d => d.Code).ToHashSet(), covered);
    }
}
