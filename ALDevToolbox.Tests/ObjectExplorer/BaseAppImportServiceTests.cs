using System.IO.Compression;
using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Integration tests for <see cref="BaseAppImportService"/>: ZIP parsing,
/// batch insert, validation, duplicate-version rejection, overwrite path,
/// cross-org isolation, and the import summary's failed-file count.
/// </summary>
public sealed class BaseAppImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Import_parses_files_and_creates_version_row()
    {
        var svc = NewService();
        var zip = BuildZip(new Dictionary<string, string>
        {
            ["Base Application/Sales/SalesPost.Codeunit.al"] =
                "namespace Microsoft.Sales.Document;\nusing System;\n\ncodeunit 80 \"Sales-Post\"\n{\n}\n",
            ["Base Application/Sales/SalesHeader.Table.al"] =
                "namespace Microsoft.Sales.Document;\n\ntable 36 \"Sales Header\"\n{\n}\n",
            ["System Application/Customer/ICustomerLookup.Interface.al"] =
                "interface ICustomerLookup\n{\n}\n",
        });

        var summary = await svc.ImportAsync(zip, new BaseAppImportRequest(
            Major: 28, Minor: 0, CumulativeUpdate: 1,
            ApplicationVersionId: null, Notes: "Test upload", Mode: BaseAppImportMode.Reject));

        summary.TotalFiles.Should().Be(3);
        summary.ParsedFiles.Should().Be(3);
        summary.FailedFiles.Should().Be(0);

        using var ctx = _db.NewContext();
        var version = await ctx.BaseAppVersions.SingleAsync(v => v.Id == summary.VersionId);
        version.Major.Should().Be(28);
        version.Minor.Should().Be(0);
        version.CumulativeUpdate.Should().Be(1);
        version.FileCount.Should().Be(3);
        version.Notes.Should().Be("Test upload");

        var files = await ctx.BaseAppFiles
            .Where(f => f.VersionId == summary.VersionId)
            .OrderBy(f => f.ObjectType)
            .ToListAsync();
        files.Should().HaveCount(3);

        var codeunit = files.Single(f => f.ObjectType == "codeunit");
        codeunit.ObjectId.Should().Be(80);
        codeunit.ObjectName.Should().Be("Sales-Post");
        codeunit.Namespace.Should().Be("Microsoft.Sales.Document");
        codeunit.Module.Should().Be("Base Application");
        codeunit.FileName.Should().Be("SalesPost.Codeunit.al");

        var iface = files.Single(f => f.ObjectType == "interface");
        iface.ObjectId.Should().BeNull();
        iface.ObjectName.Should().Be("ICustomerLookup");
        iface.Module.Should().Be("System Application");
    }

    [Fact]
    public async Task Import_skips_non_al_entries_and_records_unparseable_files()
    {
        var svc = NewService();
        var zip = BuildZip(new Dictionary<string, string>
        {
            ["Base Application/Sales/SalesPost.Codeunit.al"] = "codeunit 80 \"Sales-Post\" { }\n",
            ["Base Application/app.json"] = "{ }\n",
            ["Base Application/Stray.al"] = "// just a comment, no declaration\n",
        });

        var summary = await svc.ImportAsync(zip, NewRequest(major: 27));

        summary.TotalFiles.Should().Be(2); // only the two .al files
        summary.ParsedFiles.Should().Be(1);
        summary.FailedFiles.Should().Be(1);
        summary.FailedPaths.Should().ContainSingle().Which.Should().Be("Base Application/Stray.al");
    }

    [Fact]
    public async Task Duplicate_version_is_rejected_by_default_and_replaced_when_mode_is_replace()
    {
        var svc = NewService();
        var first = BuildZip(new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
        });
        await svc.ImportAsync(first, NewRequest(major: 28));

        // Same (org, major, minor, cu) in Reject mode -> validation error.
        var dup = BuildZip(new Dictionary<string, string>
        {
            ["a/Bar.Codeunit.al"] = "codeunit 2 \"Bar\" { }\n",
        });
        var act = () => svc.ImportAsync(dup, NewRequest(major: 28));
        await act.Should().ThrowAsync<PlanValidationException>()
            .Where(ex => ex.Errors.ContainsKey("Major"));

        // Replace mode soft-deletes the old version and the new one lands.
        var replacement = BuildZip(new Dictionary<string, string>
        {
            ["a/Bar.Codeunit.al"] = "codeunit 2 \"Bar\" { }\n",
        });
        var summary = await svc.ImportAsync(
            replacement, NewRequest(major: 28, mode: BaseAppImportMode.Replace));

        summary.WasAppend.Should().BeFalse();

        using var ctx = _db.NewContext();
        var active = await ctx.BaseAppVersions.Where(v => v.DeletedAt == null).ToListAsync();
        active.Should().ContainSingle().Which.Id.Should().Be(summary.VersionId);
        var soft = await ctx.BaseAppVersions.IgnoreQueryFilters()
            .Where(v => v.DeletedAt != null).ToListAsync();
        soft.Should().HaveCount(1);
    }

    [Fact]
    public async Task Append_mode_adds_files_to_existing_version_without_replacing_it()
    {
        var svc = NewService();
        var baseApp = BuildZip(new Dictionary<string, string>
        {
            ["Base Application/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
            ["Base Application/Bar.Table.al"] = "table 1 \"Bar\" { }\n",
        });
        var first = await svc.ImportAsync(baseApp, NewRequest(major: 28));
        first.WasAppend.Should().BeFalse();

        var systemApp = BuildZip(new Dictionary<string, string>
        {
            ["System Application/Baz.Codeunit.al"] = "codeunit 2 \"Baz\" { }\n",
        });
        var second = await svc.ImportAsync(
            systemApp, NewRequest(major: 28, mode: BaseAppImportMode.Append));

        second.WasAppend.Should().BeTrue();
        second.VersionId.Should().Be(first.VersionId, because: "Append reuses the existing version row.");
        second.ReplacedPaths.Should().Be(0);

        using var ctx = _db.NewContext();
        var files = await ctx.BaseAppFiles.Where(f => f.VersionId == first.VersionId).ToListAsync();
        files.Should().HaveCount(3);
        files.Select(f => f.Module).Should().Contain(new[] { "Base Application", "System Application" });

        var version = await ctx.BaseAppVersions.SingleAsync(v => v.Id == first.VersionId);
        version.FileCount.Should().Be(3);
    }

    [Fact]
    public async Task Append_mode_overwrites_rows_with_the_same_path()
    {
        var svc = NewService();
        var initial = BuildZip(new Dictionary<string, string>
        {
            ["Base Application/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
        });
        var first = await svc.ImportAsync(initial, NewRequest(major: 28));

        // Re-uploading the same path replaces the row rather than duplicating it.
        var hotfix = BuildZip(new Dictionary<string, string>
        {
            ["Base Application/Foo.Codeunit.al"] = "codeunit 1 \"Foo Patched\" { }\n",
        });
        var second = await svc.ImportAsync(
            hotfix, NewRequest(major: 28, mode: BaseAppImportMode.Append));

        second.WasAppend.Should().BeTrue();
        second.ReplacedPaths.Should().Be(1);

        using var ctx = _db.NewContext();
        var rows = await ctx.BaseAppFiles
            .Where(f => f.VersionId == first.VersionId && f.Path == "Base Application/Foo.Codeunit.al")
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].ObjectName.Should().Be("Foo Patched");
    }

    [Fact]
    public async Task Validation_rejects_out_of_range_version_numbers()
    {
        var svc = NewService();
        var zip = BuildZip(new Dictionary<string, string>());

        var act = () => svc.ImportAsync(zip, new BaseAppImportRequest(
            Major: 0, Minor: 0, CumulativeUpdate: 0,
            ApplicationVersionId: null, Notes: null, Mode: BaseAppImportMode.Reject));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Major");
    }

    [Fact]
    public async Task Cross_org_isolation()
    {
        var svc = NewService();
        var zip = BuildZip(new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
        });
        await svc.ImportAsync(zip, NewRequest(major: 28));

        // Switch to OtherOrg and confirm it sees no versions.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        using var ctx = _db.NewContext();
        var fromOther = await ctx.BaseAppVersions.ToListAsync();
        fromOther.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_extracts_symbols_and_stamps_indexed_timestamp()
    {
        var svc = NewService();
        var zip = BuildZip(new Dictionary<string, string>
        {
            ["Base Application/Sales/SalesPost.Codeunit.al"] = """
                codeunit 80 "Sales-Post"
                {
                    procedure Post(var SalesHeader: Record "Sales Header"; Commit: Boolean)
                    begin
                    end;

                    procedure Post(var SalesHeader: Record "Sales Header")
                    begin
                    end;

                    [IntegrationEvent(false, false)]
                    local procedure OnAfterPostSalesDoc(var SalesHeader: Record "Sales Header")
                    begin
                    end;

                    trigger OnRun()
                    begin
                    end;
                }
                """,
        });

        var summary = await svc.ImportAsync(zip, NewRequest(major: 28));

        using var ctx = _db.NewContext();
        var version = await ctx.BaseAppVersions.SingleAsync(v => v.Id == summary.VersionId);
        version.SymbolsIndexedAt.Should().NotBeNull();

        var symbols = await ctx.BaseAppSymbols
            .Where(s => s.VersionId == summary.VersionId)
            .OrderBy(s => s.LineNumber)
            .ToListAsync();
        symbols.Should().HaveCount(4); // 2 overloads + 1 publisher + 1 trigger

        symbols.Count(s => s.Name == "Post").Should().Be(2, "overloads land as separate rows");
        symbols.Should().Contain(s => s.Kind == "event_publisher" && s.Name == "OnAfterPostSalesDoc");
        symbols.Should().Contain(s => s.Kind == "trigger" && s.Name == "OnRun");
    }

    [Fact]
    public async Task Delete_marks_version_soft_deleted()
    {
        var svc = NewService();
        var zip = BuildZip(new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
        });
        var summary = await svc.ImportAsync(zip, NewRequest(major: 28));

        var svc2 = NewService();
        await svc2.DeleteAsync(summary.VersionId);

        using var ctx = _db.NewContext();
        // The base query filter only scopes by organisation, not by DeletedAt;
        // services that "list active versions" add the DeletedAt == null clause
        // themselves (see BaseAppService.ListVersionsAsync). Mirror that here.
        var active = await ctx.BaseAppVersions.AnyAsync(v => v.Id == summary.VersionId && v.DeletedAt == null);
        active.Should().BeFalse();
        var soft = await ctx.BaseAppVersions.IgnoreQueryFilters()
            .SingleAsync(v => v.Id == summary.VersionId);
        soft.DeletedAt.Should().NotBeNull();
    }

    private BaseAppImportService NewService()
    {
        var ctx = _db.NewContext();
        return new BaseAppImportService(ctx, NullLogger<BaseAppImportService>.Instance, _db.OrgContext);
    }

    private static BaseAppImportRequest NewRequest(
        int major = 28, int minor = 0, int cu = 1,
        BaseAppImportMode mode = BaseAppImportMode.Reject)
        => new(major, minor, cu, ApplicationVersionId: null, Notes: null, Mode: mode);

    private static Stream BuildZip(Dictionary<string, string> entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        ms.Position = 0;
        return ms;
    }
}
