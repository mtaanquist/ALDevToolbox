using System.IO.Compression;
using System.Text;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Read-side tests for <see cref="BaseAppService"/>: filtered listing, search
/// (content / name / id), counterpart matching across versions (by id then
/// by name), and the DiffPlex-backed diff output.
/// </summary>
public sealed class BaseAppServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Search_matches_content_object_name_and_object_id()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] =
                "codeunit 80 \"Sales-Post\"\n{\n    procedure Post()\n    begin\n        Foo.DoTheThing();\n    end;\n}\n",
            ["Sales/SalesHeader.Table.al"] =
                "table 36 \"Sales Header\" { }\n",
            ["Inventory/Item.Table.al"] =
                "table 27 \"Item\" { }\n",
        });

        var svc = NewService();

        // Content search hits the procedure body.
        var byContent = await svc.ListFilesAsync(versionId,
            new BaseAppFileFilter(null, null, "DoTheThing"), skip: 0, take: 50);
        byContent.Rows.Should().ContainSingle().Which.ObjectName.Should().Be("Sales-Post");

        // Name search hits the object name.
        var byName = await svc.ListFilesAsync(versionId,
            new BaseAppFileFilter(null, null, "Sales Header"), skip: 0, take: 50);
        byName.Rows.Should().ContainSingle().Which.ObjectName.Should().Be("Sales Header");

        // Numeric search matches the object id.
        var byId = await svc.ListFilesAsync(versionId,
            new BaseAppFileFilter(null, null, "27"), skip: 0, take: 50);
        byId.Rows.Should().Contain(r => r.ObjectName == "Item");
    }

    [Fact]
    public async Task FindCounterpart_matches_by_type_and_id_first()
    {
        var v27 = await SeedVersionAsync(27, new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 80 \"Sales-Post v27\"\n{ }\n",
        });
        var v28 = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 80 \"Sales-Post Renamed\"\n{ }\n",
        });

        var svc = NewService();
        var fileV28 = (await svc.ListFilesAsync(v28, BaseAppFileFilter.Empty, 0, 50)).Rows.Single();
        var full = await svc.GetFileAsync(fileV28.Id);
        full.Should().NotBeNull();

        // Counterpart in v27 has the same id but a different name — should
        // still match because (Type, Id) wins over (Type, Name).
        var counterpart = await svc.FindCounterpartAsync(v27, full!);
        counterpart.Should().NotBeNull();
        counterpart!.ObjectName.Should().Be("Sales-Post v27");
    }

    [Fact]
    public async Task FindCounterpart_falls_back_to_name_when_id_missing()
    {
        var v27 = await SeedVersionAsync(27, new Dictionary<string, string>
        {
            ["a/IFoo.Interface.al"] = "interface IFoo\n{ }\n",
        });
        var v28 = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["a/IFoo.Interface.al"] = "interface IFoo\n{ }\n",
        });

        var svc = NewService();
        var fileV28 = (await svc.ListFilesAsync(v28, BaseAppFileFilter.Empty, 0, 50)).Rows.Single();
        var full = (await svc.GetFileAsync(fileV28.Id))!;

        var counterpart = await svc.FindCounterpartAsync(v27, full);
        counterpart.Should().NotBeNull();
        counterpart!.ObjectName.Should().Be("IFoo");
    }

    [Fact]
    public void ComputeDiff_reports_unchanged_inserted_and_deleted_lines()
    {
        var svc = NewService();
        var diff = svc.ComputeDiff(
            leftContent: "alpha\nbeta\ngamma\n",
            rightContent: "alpha\nbeta-renamed\ngamma\ndelta\n");

        diff.Left.Should().NotBeEmpty();
        diff.Right.Should().NotBeEmpty();
        diff.Left.Count.Should().Be(diff.Right.Count);

        // Some change classification should appear on the changed and added lines.
        diff.Right.Should().Contain(l => l.Change == BaseAppDiffChange.Modified || l.Change == BaseAppDiffChange.Inserted);
    }

    [Fact]
    public async Task Filter_by_object_type_returns_only_matching_rows()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
            ["a/Bar.Table.al"] = "table 2 \"Bar\" { }\n",
            ["a/Baz.Page.al"] = "page 3 \"Baz\" { }\n",
        });

        var svc = NewService();
        var tables = await svc.ListFilesAsync(versionId,
            new BaseAppFileFilter(ObjectType: "table", Module: null, Search: null),
            skip: 0, take: 50);
        tables.Rows.Should().ContainSingle().Which.ObjectName.Should().Be("Bar");
    }

    [Fact]
    public async Task ListObjectTypes_and_ListModules_return_distinct_values()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Base Application/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
            ["Base Application/Bar.Codeunit.al"] = "codeunit 2 \"Bar\" { }\n",
            ["System Application/Baz.Table.al"] = "table 3 \"Baz\" { }\n",
        });

        var svc = NewService();
        (await svc.ListObjectTypesAsync(versionId)).Should().BeEquivalentTo(new[] { "codeunit", "table" });
        (await svc.ListModulesAsync(versionId)).Should().BeEquivalentTo(new[] { "Base Application", "System Application" });
    }

    [Fact]
    public async Task FindReferences_groups_hits_by_confidence()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = """
                codeunit 80 "Sales-Post"
                {
                    procedure Post(var H: Record "Sales Header"): Boolean
                    begin
                        InternalHelper();
                        exit(true);
                    end;

                    local procedure InternalHelper()
                    begin
                        Post(SalesHeader);
                    end;
                }
                """,
            ["Sales/SalesPostHook.Codeunit.al"] = """
                codeunit 81 "Sales Post Hook"
                {
                    procedure RunPost()
                    var
                        SalesPostCu: Codeunit "Sales-Post";
                    begin
                        SalesPostCu.Post(SalesHeader);
                    end;
                }
                """,
            ["Other/UnrelatedFoo.Codeunit.al"] = """
                codeunit 82 "Unrelated Foo"
                {
                    procedure DoWork()
                    var
                        OtherPoster: Codeunit "Other Poster";
                    begin
                        OtherPoster.Post();
                    end;
                }
                """,
        });

        var svc = NewService();
        var allFiles = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var salesPostFileId = allFiles.Rows.Single(r => r.ObjectName == "Sales-Post").Id;
        var symbol = (await svc.ListSymbolsInFileAsync(salesPostFileId))
            .Single(s => s.Name == "Post" && s.Kind == "procedure");

        var result = await svc.FindReferencesAsync(symbol.Id);

        // Self-call inside Sales-Post is "SameObject" — high confidence.
        result.Likely.Should().Contain(h =>
            h.ObjectName == "Sales-Post"
            && h.Confidence == BaseAppReferenceConfidence.SameObject);

        // The hook codeunit mentions "Sales-Post" on the same call line —
        // "Qualified" confidence.
        result.Likely.Should().Contain(h =>
            h.ObjectName == "Sales Post Hook"
            && h.Confidence == BaseAppReferenceConfidence.Qualified);

        // The unrelated codeunit's `OtherPoster.Post()` is a bare match
        // with no mention of "Sales-Post" — "PossiblyRelated".
        result.PossiblyRelated.Should().Contain(h =>
            h.ObjectName == "Unrelated Foo");

        // The declaration line itself is excluded.
        result.Likely.Should().NotContain(h =>
            h.ObjectName == "Sales-Post"
            && h.LineNumber == symbol.LineNumber);
    }

    private BaseAppService NewService()
    {
        var ctx = _db.NewContext();
        return new BaseAppService(ctx, NullLogger<BaseAppService>.Instance);
    }

    private async Task<int> SeedVersionAsync(int major, Dictionary<string, string> entries)
    {
        var importer = new BaseAppImportService(
            _db.NewContext(), NullLogger<BaseAppImportService>.Instance, _db.OrgContext);
        var zip = BuildZip(entries);
        var summary = await importer.ImportAsync(zip, new BaseAppImportRequest(
            Major: major, Minor: 0, CumulativeUpdate: 0,
            ApplicationVersionId: null, Notes: null, Mode: BaseAppImportMode.Reject));
        return summary.VersionId;
    }

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
