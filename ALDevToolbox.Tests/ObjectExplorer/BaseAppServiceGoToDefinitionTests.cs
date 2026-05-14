using System.IO.Compression;
using System.Text;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Integration tests for <see cref="BaseAppService.GoToDefinitionAsync"/>.
/// Exercises the four main resolution shapes:
/// object-by-name, qualified-via-variable, qualified-via-quoted-caller,
/// and unqualified procedure name fallback.
/// </summary>
public sealed class BaseAppServiceGoToDefinitionTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Resolves_object_by_name_in_keyword_context()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = "codeunit 80 \"Sales-Post\" { }\n",
            ["Other/Caller.Codeunit.al"] = """
                codeunit 50100 "Caller"
                {
                    procedure DoIt()
                    var
                        SalesPostCu: Codeunit "Sales-Post";
                    begin
                    end;
                }
                """,
        });

        var svc = NewService();
        var callerFile = await GetFileIdByName(svc, versionId, "Caller");
        var salesPostFileId = await GetFileIdByName(svc, versionId, "Sales-Post");

        // Click on "Sales-Post" inside the var declaration line (line 5),
        // somewhere in the middle of the quoted name.
        var target = await svc.GoToDefinitionAsync(callerFile, line: 5, column: 35);

        target.Should().NotBeNull();
        target!.FileId.Should().Be(salesPostFileId);
        target.LineNumber.Should().Be(1);
        target.Description.Should().Contain("Sales-Post");
    }

    [Fact]
    public async Task Resolves_qualified_call_via_variable_declaration()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = """
                codeunit 80 "Sales-Post"
                {
                    procedure Post()
                    begin
                    end;
                }
                """,
            ["Other/Caller.Codeunit.al"] = """
                codeunit 50100 "Caller"
                {
                    procedure RunIt()
                    var
                        SalesPostCu: Codeunit "Sales-Post";
                    begin
                        SalesPostCu.Post(SomeHeader);
                    end;
                }
                """,
        });

        var svc = NewService();
        var callerFile = await GetFileIdByName(svc, versionId, "Caller");
        var salesPostFileId = await GetFileIdByName(svc, versionId, "Sales-Post");

        // Click on "Post" in the call line (line 7). Column lands on 'P'.
        var line = "        SalesPostCu.Post(SomeHeader);";
        var col = line.IndexOf("Post(", StringComparison.Ordinal) + 1; // 1-based
        var target = await svc.GoToDefinitionAsync(callerFile, line: 7, column: col);

        target.Should().NotBeNull();
        target!.FileId.Should().Be(salesPostFileId);
        target.LineNumber.Should().Be(3);
        target.Description.Should().Contain("Post");
    }

    [Fact]
    public async Task Resolves_qualified_call_via_quoted_caller()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = """
                codeunit 80 "Sales-Post"
                {
                    procedure Post()
                    begin
                    end;
                }
                """,
            ["Other/Caller.Codeunit.al"] = """
                codeunit 50100 "Caller"
                {
                    procedure RunIt()
                    begin
                        "Sales-Post".Post();
                    end;
                }
                """,
        });

        var svc = NewService();
        var callerFile = await GetFileIdByName(svc, versionId, "Caller");
        var salesPostFileId = await GetFileIdByName(svc, versionId, "Sales-Post");

        var line = "        \"Sales-Post\".Post();";
        var col = line.IndexOf("Post(", StringComparison.Ordinal) + 1;
        var target = await svc.GoToDefinitionAsync(callerFile, line: 5, column: col);

        target.Should().NotBeNull();
        target!.FileId.Should().Be(salesPostFileId);
        target.LineNumber.Should().Be(3);
    }

    [Fact]
    public async Task Falls_back_to_same_file_for_unqualified_procedure()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = """
                codeunit 80 "Sales-Post"
                {
                    procedure Post()
                    begin
                        Helper();
                    end;

                    local procedure Helper()
                    begin
                    end;
                }
                """,
        });

        var svc = NewService();
        var fileId = await GetFileIdByName(svc, versionId, "Sales-Post");

        // Click on Helper() inside Post's body (line 5).
        var line = "        Helper();";
        var col = line.IndexOf("Helper", StringComparison.Ordinal) + 1;
        var target = await svc.GoToDefinitionAsync(fileId, line: 5, column: col);

        target.Should().NotBeNull();
        target!.FileId.Should().Be(fileId);
        target.LineNumber.Should().Be(8);
    }

    [Fact]
    public async Task Returns_null_when_clicked_word_doesnt_resolve()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
        });

        var svc = NewService();
        var fileId = await GetFileIdByName(svc, versionId, "Foo");

        // Click on whitespace.
        (await svc.GoToDefinitionAsync(fileId, line: 1, column: 1))
            .Should().BeNull();
    }

    private BaseAppService NewService()
        => new(_db.NewContext(), NullLogger<BaseAppService>.Instance);

    private async Task<long> GetFileIdByName(BaseAppService svc, int versionId, string objectName)
    {
        var page = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        return page.Rows.Single(r => r.ObjectName == objectName).Id;
    }

    private async Task<int> SeedVersionAsync(int major, Dictionary<string, string> entries)
    {
        var importer = new BaseAppImportService(
            _db.NewContext(), NullLogger<BaseAppImportService>.Instance, _db.OrgContext);
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var s = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }
        ms.Position = 0;
        var summary = await importer.ImportAsync(ms, new BaseAppImportRequest(
            Major: major, CumulativeUpdate: 0,
            ApplicationVersionId: null, Notes: null, Mode: BaseAppImportMode.Reject));
        return summary.VersionId;
    }
}
