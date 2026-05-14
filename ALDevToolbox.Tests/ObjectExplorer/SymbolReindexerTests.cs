using System.IO.Compression;
using System.Text;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pins the symbol reindexer's behaviour: a tick over a version with
/// <c>SymbolsIndexedAt = null</c> populates <c>base_app_symbols</c> and
/// stamps the timestamp; a tick over already-indexed versions is a no-op.
/// Tests bypass the <c>Task.Delay</c> loop by calling <c>TickOnceAsync</c>
/// directly, the same pattern <c>BackupScheduler</c> uses.
/// </summary>
public sealed class SymbolReindexerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ReindexVersion_extracts_symbols_for_existing_content()
    {
        var versionId = await SeedVersionWithoutSymbolsAsync();

        var reindexer = new SymbolReindexer(null!, new SymbolReindexQueue(), NullLogger<SymbolReindexer>.Instance);
        using var ctx = _db.NewContext();
        var count = await reindexer.ReindexVersionAsync(ctx, versionId, default);

        count.Should().BeGreaterThan(0);

        using var assertCtx = _db.NewContext();
        var version = await assertCtx.BaseAppVersions.SingleAsync(v => v.Id == versionId);
        version.SymbolsIndexedAt.Should().NotBeNull();

        var symbols = await assertCtx.BaseAppSymbols
            .Where(s => s.VersionId == versionId).ToListAsync();
        symbols.Should().NotBeEmpty();
        symbols.Should().Contain(s => s.Name == "Post" && s.Kind == "procedure");
    }

    [Fact]
    public async Task TickOnce_skips_when_no_versions_pending()
    {
        // Seed a version that's already indexed.
        var versionId = await SeedVersionWithoutSymbolsAsync();
        using (var prep = _db.NewContext())
        {
            var v = await prep.BaseAppVersions.SingleAsync(x => x.Id == versionId);
            v.SymbolsIndexedAt = DateTime.UtcNow;
            await prep.SaveChangesAsync();
        }

        var reindexer = new SymbolReindexer(null!, new SymbolReindexQueue(), NullLogger<SymbolReindexer>.Instance);
        using var ctx = _db.NewContext();
        await reindexer.TickOnceAsync(ctx, default);

        using var assertCtx = _db.NewContext();
        var symbols = await assertCtx.BaseAppSymbols
            .Where(s => s.VersionId == versionId).CountAsync();
        symbols.Should().Be(0, "the tick should have found nothing to do");
    }

    [Fact]
    public async Task TickOnce_picks_up_one_pending_version_per_call()
    {
        var v1 = await SeedVersionWithoutSymbolsAsync(major: 27);
        var v2 = await SeedVersionWithoutSymbolsAsync(major: 28);

        var reindexer = new SymbolReindexer(null!, new SymbolReindexQueue(), NullLogger<SymbolReindexer>.Instance);

        // First tick processes v1 (earliest UploadedAt).
        using (var ctx = _db.NewContext()) await reindexer.TickOnceAsync(ctx, default);
        using (var ctx = _db.NewContext())
        {
            (await ctx.BaseAppVersions.SingleAsync(x => x.Id == v1)).SymbolsIndexedAt.Should().NotBeNull();
            (await ctx.BaseAppVersions.SingleAsync(x => x.Id == v2)).SymbolsIndexedAt.Should().BeNull();
        }

        // Second tick picks up v2.
        using (var ctx = _db.NewContext()) await reindexer.TickOnceAsync(ctx, default);
        using (var ctx = _db.NewContext())
        {
            (await ctx.BaseAppVersions.SingleAsync(x => x.Id == v2)).SymbolsIndexedAt.Should().NotBeNull();
        }
    }

    /// <summary>
    /// Imports a small ZIP via the regular import service, then clears
    /// <c>SymbolsIndexedAt</c> and deletes the symbol rows so the reindexer
    /// sees the version as fresh. Mirrors the upgrade-from-pre-feature
    /// state.
    /// </summary>
    private async Task<int> SeedVersionWithoutSymbolsAsync(int major = 28)
    {
        var importer = new BaseAppImportService(
            _db.NewContext(), NullLogger<BaseAppImportService>.Instance, _db.OrgContext);

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Base Application/SalesPost.Codeunit.al");
            using var s = entry.Open();
            var bytes = Encoding.UTF8.GetBytes("""
                codeunit 80 "Sales-Post"
                {
                    procedure Post()
                    begin
                    end;

                    trigger OnRun()
                    begin
                    end;
                }
                """);
            s.Write(bytes, 0, bytes.Length);
        }
        ms.Position = 0;

        var summary = await importer.ImportAsync(ms, new BaseAppImportRequest(
            Major: major, CumulativeUpdate: 0,
            ApplicationVersionId: null, Notes: null, Mode: BaseAppImportMode.Reject));

        // Wipe the inline-extracted symbols and reset the indexed flag so
        // the reindexer treats this version as pending.
        using var ctx = _db.NewContext();
        await ctx.BaseAppSymbols.Where(s => s.VersionId == summary.VersionId).ExecuteDeleteAsync();
        var v = await ctx.BaseAppVersions.SingleAsync(x => x.Id == summary.VersionId);
        v.SymbolsIndexedAt = null;
        await ctx.SaveChangesAsync();

        return summary.VersionId;
    }
}
