using System.Text;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// Object-level Base-vs-Customer comparison over two self-contained C/AL
/// releases: objects line up by (kind, id) and are classified by their
/// source-slice hash — modified / unchanged / added / removed.
/// </summary>
public sealed class CalReleaseComparisonTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    static CalReleaseComparisonTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private async Task<int> ImportAsync(string cal, string label, string kind, int? parentId)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, cal, Encoding.ASCII);
        try
        {
            int releaseId;
            await using (var ctx = _db.NewContext())
                releaseId = await new ReleaseImportService(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
                    new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), NullLogger<TranslationImportService>.Instance),
                    NullLogger<ReleaseImportService>.Instance)
                    .BeginReleaseAsync(new ReleaseImportMetadata(label, kind, parentId, null));
            await using (var ctx = _db.NewContext())
                await new CalImportService(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
                    NullLogger<CalImportService>.Instance)
                    .ProcessReleaseAsync(releaseId, path, "850");
            return releaseId;
        }
        finally { File.Delete(path); }
    }

    private static string Obj(string header, string body) => $"OBJECT {header}\r\n{{\r\n{body}\r\n}}\r\n";

    [Fact]
    public async Task Classifies_objects_by_kind_id_and_hash()
    {
        // Base: Table 18 (v1), Codeunit 50000 (shared), Page 21 (base-only).
        var baseCal =
            Obj("Table 18 Customer", "  FIELDS\r\n  {\r\n    { 1 ; ;No. ;Code20 }\r\n  }")
          + Obj("Codeunit 50000 Mgt", "  CODE\r\n  {\r\n    BEGIN\r\n    END.\r\n  }")
          + Obj("Page 21 Customer Card", "  PROPERTIES\r\n  {\r\n    SourceTable=Table18;\r\n  }");

        // Customer: Table 18 (v2, extra field → modified), Codeunit 50000 (identical
        // → unchanged), Table 50001 (customer-only → added). No Page 21 → removed.
        var custCal =
            Obj("Table 18 Customer", "  FIELDS\r\n  {\r\n    { 1 ; ;No. ;Code20 }\r\n    { 2 ; ;Name ;Text50 }\r\n  }")
          + Obj("Codeunit 50000 Mgt", "  CODE\r\n  {\r\n    BEGIN\r\n    END.\r\n  }")
          + Obj("Table 50001 Loyalty", "  FIELDS\r\n  {\r\n    { 1 ; ;Code ;Code20 }\r\n  }");

        var baseId = await ImportAsync(baseCal, "Base", "first_party", null);
        var custId = await ImportAsync(custCal, "Customer", "project", baseId);

        await using var ctx = _db.NewContext();
        var svc = new ReleaseComparisonService(ctx, NullLogger<ReleaseComparisonService>.Instance);
        var rows = await svc.CompareReleaseObjectsAsync(baseId, custId);

        rows.Single(r => r.Kind == "table" && r.ObjectId == 18).Status.Should().Be("modified");
        rows.Single(r => r.Kind == "table" && r.ObjectId == 18).LeftFileId.Should().NotBeNull();
        rows.Single(r => r.Kind == "table" && r.ObjectId == 18).RightFileId.Should().NotBeNull();

        rows.Single(r => r.Kind == "codeunit" && r.ObjectId == 50000).Status.Should().Be("unchanged");
        rows.Single(r => r.Kind == "page" && r.ObjectId == 21).Status.Should().Be("removed");
        rows.Single(r => r.Kind == "table" && r.ObjectId == 50001).Status.Should().Be("added");
    }
}
