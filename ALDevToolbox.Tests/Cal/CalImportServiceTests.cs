using System.Text;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// End-to-end coverage for <see cref="CalImportService"/> against a real
/// Postgres database: the C/AL TXT is parsed into the same <c>oe_*</c> rows the
/// AL path produces, the release flips to <c>ready</c>, and numeric object
/// references resolve to names within the single self-contained module.
/// </summary>
public sealed class CalImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    static CalImportServiceTests() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, NullLogger<TranslationImportService>.Instance),
            NullLogger<ReleaseImportService>.Instance);

    private CalImportService NewCalImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), NullLogger<CalImportService>.Instance);

    private async Task<int> ImportAsync(string txtPath, string label, string encoding = "850",
        string kind = "first_party", int? parentId = null)
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            releaseId = await NewImporter(ctx).BeginReleaseAsync(
                new ReleaseImportMetadata(label, kind, parentId, null));
        }
        await using (var ctx = _db.NewContext())
        {
            await NewCalImporter(ctx).ProcessReleaseAsync(releaseId, txtPath, encoding);
        }
        return releaseId;
    }

    [Fact]
    public async Task Imports_fixture_to_ready_release_with_objects_and_source()
    {
        var releaseId = await ImportAsync(CalObjectSplitterTests.FixturePath(), "C/AL Base");

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("ready");

        var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
        module.Version.Should().StartWith("NAV");

        var objects = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.ModuleId == module.Id).ToListAsync();
        objects.Should().HaveCount(6);

        var customer = objects.Single(o => o.Kind == "table" && o.ObjectId == 18);
        customer.Name.Should().Be("Customer");
        customer.SourceFileId.Should().NotBeNull();

        var symbols = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == customer.Id).ToListAsync();
        symbols.Should().Contain(s => s.Kind == "table_field" && s.Name == "No." && s.Signature == "Code20");
        symbols.Should().Contain(s => s.Kind == "procedure" && s.Name == "AssistEdit");
        symbols.Should().Contain(s => s.Kind == "trigger" && s.Name == "OnInsert");

        // The stored source slice round-trips with the OEM codepage intact.
        var content = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == customer.SourceFileId)
            .Select(f => f.FileContent!.Content)
            .SingleAsync();
        content.Should().Contain("OBJECT Table 18 Customer");
        content.Should().NotContain("�");
    }

    [Fact]
    public async Task Emits_call_site_references_from_bodies()
    {
        var releaseId = await ImportAsync(CalObjectSplitterTests.FixturePath(), "C/AL Base");

        await using var read = _db.NewContext();
        var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
        var customer = await read.OeModuleObjects.AsNoTracking()
            .SingleAsync(o => o.ModuleId == module.Id && o.Kind == "table" && o.ObjectId == 18);

        var refs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObjectId == customer.Id).ToListAsync();

        // The Customer table's triggers/procedures call into other objects.
        refs.Should().Contain(r => r.ReferenceKind == "method_call" && r.TargetMemberName != null);
        // Implicit Rec field access (e.g. "No." inside OnInsert) resolves to table 18 itself.
        refs.Should().Contain(r => r.ReferenceKind == "field_access"
            && r.TargetObjectKind == "table" && r.TargetObjectId == 18);
        // Call-site refs are stamped with the calling procedure/trigger symbol.
        refs.Should().Contain(r => r.SourceSymbolId != null && r.ReferenceKind == "method_call");
    }

    [Fact]
    public async Task Resolves_object_targets_by_id_within_the_release()
    {
        // A tiny self-contained export: a codeunit with a global typed to the
        // table that ships alongside it. The id post-pass must name the target.
        const string cal =
            "OBJECT Table 18 Customer\r\n{\r\n  FIELDS\r\n  {\r\n    { 1 ; ;No. ;Code20 }\r\n  }\r\n  CODE\r\n  {\r\n    BEGIN\r\n    END.\r\n  }\r\n}\r\n" +
            "OBJECT Codeunit 50000 My Mgt\r\n{\r\n  CODE\r\n  {\r\n    VAR\r\n      Cust@1000 : Record 18;\r\n    BEGIN\r\n    END.\r\n  }\r\n}\r\n";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "Synthetic");

            await using var read = _db.NewContext();
            var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);

            var global = await read.OeModuleVariables.AsNoTracking()
                .SingleAsync(v => v.ModuleId == module.Id && v.Name == "Cust");
            global.TargetObjectKind.Should().Be("table");
            global.TargetObjectId.Should().Be(18);
            global.TargetObjectName.Should().Be("Customer");        // resolved by id
            global.TargetAppId.Should().Be(module.AppId);

            var reference = await read.OeModuleReferences.AsNoTracking()
                .SingleAsync(r => r.ModuleId == module.Id && r.ReferenceKind == "variable_type");
            reference.TargetObjectId.Should().Be(18);
            reference.TargetObjectName.Should().Be("Customer");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resolves_nav_platform_virtual_table_names()
    {
        // A global typed to a NAV virtual table (2000000038 = AllObj) resolves by
        // the NAV map — not the BC map, which names that id differently.
        const string cal =
            "OBJECT Codeunit 50000 Mgt\r\n{\r\n  CODE\r\n  {\r\n    VAR\r\n      Obj@1000 : Record 2000000038;\r\n    BEGIN\r\n    END.\r\n  }\r\n}\r\n";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            var releaseId = await ImportAsync(path, "NAV VT");
            await using var read = _db.NewContext();
            var module = await read.OeModules.AsNoTracking().SingleAsync(m => m.ReleaseId == releaseId);
            var global = await read.OeModuleVariables.AsNoTracking()
                .SingleAsync(v => v.ModuleId == module.Id && v.Name == "Obj");
            global.TargetObjectId.Should().Be(2000000038);
            global.TargetObjectName.Should().Be("AllObj");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reimport_under_a_new_label_is_independent()
    {
        // Two releases (base + customer) from the same file get distinct
        // synthetic AppIds — each self-contained, no cross-release shadowing.
        var baseId = await ImportAsync(CalObjectSplitterTests.FixturePath(), "Base");
        var custId = await ImportAsync(CalObjectSplitterTests.FixturePath(), "Customer",
            kind: "customer", parentId: baseId);

        await using var read = _db.NewContext();
        var baseApp = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == baseId).Select(m => m.AppId).SingleAsync();
        var custApp = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == custId).Select(m => m.AppId).SingleAsync();
        baseApp.Should().NotBe(custApp);
    }
}
