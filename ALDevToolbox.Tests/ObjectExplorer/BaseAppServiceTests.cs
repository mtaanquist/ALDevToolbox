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
    public async Task Search_matches_object_name_and_object_id_but_not_content()
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

        // Name search hits the object name.
        var byName = await svc.ListFilesAsync(versionId,
            new BaseAppFileFilter(Search: "Sales Header"), skip: 0, take: 50);
        byName.Rows.Should().ContainSingle().Which.ObjectName.Should().Be("Sales Header");

        // Numeric search matches the object id.
        var byId = await svc.ListFilesAsync(versionId,
            new BaseAppFileFilter(Search: "27"), skip: 0, take: 50);
        byId.Rows.Should().Contain(r => r.ObjectName == "Item");

        // Content search no longer matches — `DoTheThing` lives in the file
        // body of Sales-Post but not in its metadata, so the browser
        // ignores it. Content search lives in the file viewer instead.
        var byContent = await svc.ListFilesAsync(versionId,
            new BaseAppFileFilter(Search: "DoTheThing"), skip: 0, take: 50);
        byContent.Rows.Should().BeEmpty();
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

        // The unrelated codeunit's `OtherPoster.Post()` is a call on a
        // *different* codeunit variable, so the receiver-typed classifier
        // drops it entirely — gone from both buckets.
        result.Likely.Should().NotContain(h => h.ObjectName == "Unrelated Foo");
        result.PossiblyRelated.Should().NotContain(h => h.ObjectName == "Unrelated Foo");

        // The declaration line itself is excluded.
        result.Likely.Should().NotContain(h =>
            h.ObjectName == "Sales-Post"
            && h.LineNumber == symbol.LineNumber);
    }

    [Fact]
    public async Task FindReferences_on_local_procedure_only_searches_declaring_file()
    {
        // Two files in the same version each have a `local procedure
        // CommonHelper()` — the second file's procedure is unrelated to
        // the first's. A local procedure is only callable inside its
        // declaring file, so the references query must not return the
        // second file's call site even though the names match.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["A/FooA.Codeunit.al"] = """
                codeunit 100 "Foo A"
                {
                    local procedure CommonHelper()
                    begin
                    end;

                    procedure DoIt()
                    begin
                        CommonHelper();
                    end;
                }
                """,
            ["B/FooB.Codeunit.al"] = """
                codeunit 101 "Foo B"
                {
                    local procedure CommonHelper()
                    begin
                    end;

                    procedure DoOther()
                    begin
                        CommonHelper();
                    end;
                }
                """,
        });

        var svc = NewService();
        var allFiles = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var fooAId = allFiles.Rows.Single(r => r.ObjectName == "Foo A").Id;
        var localProc = (await svc.ListSymbolsInFileAsync(fooAId))
            .Single(s => s.Name == "CommonHelper" && s.Kind == "local_procedure");

        var result = await svc.FindReferencesAsync(localProc.Id);

        // The Foo A call site is included; nothing from Foo B.
        result.Likely.Concat(result.PossiblyRelated)
            .Should().OnlyContain(h => h.ObjectName == "Foo A");
    }

    [Fact]
    public async Task FindReferences_on_field_returns_quoted_use_sites_classified()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesHeader.Table.al"] = """
                table 36 "Sales Header"
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                    }
                }
                """,
            ["Sales/SalesPost.Codeunit.al"] = """
                codeunit 80 "Sales-Post"
                {
                    procedure Post()
                    var
                        SalesHdr: Record "Sales Header";
                    begin
                        SalesHdr."No." := '';
                    end;
                }
                """,
            ["Other/Unrelated.Codeunit.al"] = """
                codeunit 82 "Unrelated"
                {
                    procedure DoWork()
                    begin
                        // Mentions "No." in a string-y context that
                        // happens to ILIKE-match without a typed-var
                        // back reference to the Sales Header table.
                        Message('"No." is the field caption');
                    end;
                }
                """,
        });

        var svc = NewService();
        var allFiles = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var tableFileId = allFiles.Rows.Single(r => r.ObjectName == "Sales Header").Id;
        var fieldSymbol = (await svc.ListSymbolsInFileAsync(tableFileId))
            .Single(s => s.Kind == "field" && s.Name == "No.");

        var result = await svc.FindReferencesAsync(fieldSymbol.Id);

        // The Sales-Post codeunit declares `Record "Sales Header"` —
        // that's a Qualified hit. The unrelated codeunit doesn't, so
        // it's PossiblyRelated.
        result.Likely.Should().Contain(h =>
            h.ObjectName == "Sales-Post"
            && h.Confidence == BaseAppReferenceConfidence.Qualified);
        result.PossiblyRelated.Should().Contain(h => h.ObjectName == "Unrelated");
    }

    [Fact]
    public async Task FindReferences_drops_call_through_unrelated_codeunit_var()
    {
        // The receiver of `HttpClient.Create(URL)` resolves to a *different*
        // codeunit (`"Http Web Request Mgt."`), so the hit must vanish from
        // both buckets rather than slipping into PossiblyRelated. This is
        // the main pattern that surfaced as noise in the screenshots
        // motivating this change.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Agents/Agent.Codeunit.al"] = """
                codeunit 4321 Agent
                {
                    procedure Create(): Guid
                    begin
                    end;
                }
                """,
            ["Web/HttpCaller.Codeunit.al"] = """
                codeunit 9000 "Http Caller"
                {
                    procedure Run()
                    var
                        HttpClient: Codeunit "Http Web Request Mgt.";
                    begin
                        HttpClient.Create('http://example.com');
                    end;
                }
                """,
        });

        var svc = NewService();
        var files = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var agentFileId = files.Rows.Single(r => r.ObjectName == "Agent").Id;
        var symbol = (await svc.ListSymbolsInFileAsync(agentFileId))
            .Single(s => s.Name == "Create" && s.Kind == "procedure");

        var result = await svc.FindReferencesAsync(symbol.Id);

        result.Likely.Should().NotContain(h => h.ObjectName == "Http Caller");
        result.PossiblyRelated.Should().NotContain(h => h.ObjectName == "Http Caller");
    }

    [Fact]
    public async Task FindReferences_drops_static_call_on_system_type()
    {
        // `ErrorInfo.Create('boom')` — `ErrorInfo` is not a declared
        // variable in the caller file. Bare identifier `ErrorInfo` does
        // not match the declaring codeunit name `Agent`, so it is dropped.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Agents/Agent.Codeunit.al"] = """
                codeunit 4321 Agent
                {
                    procedure Create(): Guid
                    begin
                    end;
                }
                """,
            ["Errors/Reporter.Codeunit.al"] = """
                codeunit 9100 "Error Reporter"
                {
                    procedure Boom()
                    var
                        Err: ErrorInfo;
                    begin
                        Err := ErrorInfo.Create('boom');
                    end;
                }
                """,
        });

        var svc = NewService();
        var files = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var agentFileId = files.Rows.Single(r => r.ObjectName == "Agent").Id;
        var symbol = (await svc.ListSymbolsInFileAsync(agentFileId))
            .Single(s => s.Name == "Create" && s.Kind == "procedure");

        var result = await svc.FindReferencesAsync(symbol.Id);

        result.Likely.Should().NotContain(h => h.ObjectName == "Error Reporter");
        result.PossiblyRelated.Should().NotContain(h => h.ObjectName == "Error Reporter");
    }

    [Fact]
    public async Task FindReferences_keeps_qualified_call_through_matching_var()
    {
        // Variable named the same as its declared codeunit type — common
        // in AL. The keyword `Codeunit` proves the receiver is the
        // searched-for codeunit (not the runtime/system type that happens
        // to share a name), so the call must be Qualified.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Agents/Agent.Codeunit.al"] = """
                codeunit 4321 Agent
                {
                    procedure Create(): Guid
                    begin
                    end;
                }
                """,
            ["Agents/AgentCaller.Codeunit.al"] = """
                codeunit 4322 "Agent Caller"
                {
                    procedure Run()
                    var
                        Agent: Codeunit Agent;
                    begin
                        Agent.Create();
                    end;
                }
                """,
        });

        var svc = NewService();
        var files = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var agentFileId = files.Rows.Single(r => r.ObjectName == "Agent").Id;
        var symbol = (await svc.ListSymbolsInFileAsync(agentFileId))
            .Single(s => s.Name == "Create" && s.Kind == "procedure");

        var result = await svc.FindReferencesAsync(symbol.Id);

        result.Likely.Should().Contain(h =>
            h.ObjectName == "Agent Caller"
            && h.Confidence == BaseAppReferenceConfidence.Qualified);
    }

    [Fact]
    public async Task FindReferences_drops_call_through_var_named_after_system_type()
    {
        // `var HttpClient: HttpClient;` declares a variable typed to the
        // runtime/system `HttpClient` (no AL object keyword). Even if a
        // codeunit was also named `HttpClient`, this call goes to the
        // runtime instance — drop the hit when searching for a different
        // codeunit's procedure that happens to share the name `Create`.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Agents/Agent.Codeunit.al"] = """
                codeunit 4321 Agent
                {
                    procedure Create(): Guid
                    begin
                    end;
                }
                """,
            ["Web/HttpCaller.Codeunit.al"] = """
                codeunit 9000 "Http Caller"
                {
                    procedure Run()
                    var
                        HttpClient: HttpClient;
                        Response: HttpResponseMessage;
                    begin
                        HttpClient.Create('http://example.com');
                    end;
                }
                """,
        });

        var svc = NewService();
        var files = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var agentFileId = files.Rows.Single(r => r.ObjectName == "Agent").Id;
        var symbol = (await svc.ListSymbolsInFileAsync(agentFileId))
            .Single(s => s.Name == "Create" && s.Kind == "procedure");

        var result = await svc.FindReferencesAsync(symbol.Id);

        result.Likely.Should().NotContain(h => h.ObjectName == "Http Caller");
        result.PossiblyRelated.Should().NotContain(h => h.ObjectName == "Http Caller");
    }

    [Fact]
    public async Task FindReferences_drops_unrelated_same_named_procedure_declaration()
    {
        // Two unrelated codeunits both have a `procedure Create()`. When
        // searching from one, the other's *declaration* line itself must
        // not surface as a reference.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Agents/Agent.Codeunit.al"] = """
                codeunit 4321 Agent
                {
                    procedure Create(): Guid
                    begin
                    end;
                }
                """,
            ["Other/Factory.Codeunit.al"] = """
                codeunit 4322 Factory
                {
                    procedure Create(): Integer
                    begin
                    end;
                }
                """,
        });

        var svc = NewService();
        var files = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var agentFileId = files.Rows.Single(r => r.ObjectName == "Agent").Id;
        var symbol = (await svc.ListSymbolsInFileAsync(agentFileId))
            .Single(s => s.Name == "Create" && s.Kind == "procedure");

        var result = await svc.FindReferencesAsync(symbol.Id);

        result.Likely.Should().NotContain(h => h.ObjectName == "Factory");
        result.PossiblyRelated.Should().NotContain(h => h.ObjectName == "Factory");
    }

    [Fact]
    public async Task FindReferences_on_field_drops_other_tables_field_declaration()
    {
        // Searching for `Source Code`.`Code` must not surface
        // `Customer.field(1; "Code"; …)` — that's a separate table's own
        // field declaration, not a reference.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Audit/SourceCode.Table.al"] = """
                table 230 "Source Code"
                {
                    fields
                    {
                        field(1; "Code"; Code[10]) { }
                    }
                }
                """,
            ["Sales/Customer.Table.al"] = """
                table 18 Customer
                {
                    fields
                    {
                        field(1; "Code"; Code[20]) { }
                    }
                }
                """,
        });

        var svc = NewService();
        var files = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var sourceCodeFileId = files.Rows.Single(r => r.ObjectName == "Source Code").Id;
        var fieldSymbol = (await svc.ListSymbolsInFileAsync(sourceCodeFileId))
            .Single(s => s.Kind == "field" && s.Name == "Code");

        var result = await svc.FindReferencesAsync(fieldSymbol.Id);

        result.Likely.Should().NotContain(h => h.ObjectName == "Customer");
        result.PossiblyRelated.Should().NotContain(h => h.ObjectName == "Customer");
    }

    [Fact]
    public async Task FindReferences_on_field_drops_dot_access_through_other_table_var()
    {
        // `Cust."Code" := ''` — the receiver `Cust` resolves to
        // `Record Customer`, not `Record "Source Code"`. The hit belongs
        // to Customer's field of the same name and must be dropped.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Audit/SourceCode.Table.al"] = """
                table 230 "Source Code"
                {
                    fields
                    {
                        field(1; "Code"; Code[10]) { }
                    }
                }
                """,
            ["Sales/Customer.Table.al"] = """
                table 18 Customer
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                    }
                }
                """,
            ["Sales/Setup.Codeunit.al"] = """
                codeunit 1000 "Customer Setup"
                {
                    procedure Init()
                    var
                        Cust: Record Customer;
                    begin
                        Cust."Code" := '';
                    end;
                }
                """,
        });

        var svc = NewService();
        var files = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var sourceCodeFileId = files.Rows.Single(r => r.ObjectName == "Source Code").Id;
        var fieldSymbol = (await svc.ListSymbolsInFileAsync(sourceCodeFileId))
            .Single(s => s.Kind == "field" && s.Name == "Code");

        var result = await svc.FindReferencesAsync(fieldSymbol.Id);

        result.Likely.Should().NotContain(h => h.ObjectName == "Customer Setup");
        result.PossiblyRelated.Should().NotContain(h => h.ObjectName == "Customer Setup");
    }

    [Fact]
    public async Task FindReferences_on_field_keeps_intra_table_key_reference()
    {
        // The declaring table itself references its own field from
        // `key(...)` and `fieldgroup(...)` blocks. Those are SameObject
        // references and must remain.
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Audit/SourceCode.Table.al"] = """
                table 230 "Source Code"
                {
                    fields
                    {
                        field(1; "Code"; Code[10]) { }
                        field(2; Description; Text[100]) { }
                    }
                    keys
                    {
                        key(Key1; "Code") { Clustered = true; }
                    }
                    fieldgroups
                    {
                        fieldgroup(Brick; "Code", Description) { }
                    }
                }
                """,
        });

        var svc = NewService();
        var files = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var sourceCodeFileId = files.Rows.Single(r => r.ObjectName == "Source Code").Id;
        var fieldSymbol = (await svc.ListSymbolsInFileAsync(sourceCodeFileId))
            .Single(s => s.Kind == "field" && s.Name == "Code");

        var result = await svc.FindReferencesAsync(fieldSymbol.Id);

        result.Likely.Should().Contain(h =>
            h.ObjectName == "Source Code"
            && h.Confidence == BaseAppReferenceConfidence.SameObject);
        result.Likely.Count(h => h.ObjectName == "Source Code")
            .Should().BeGreaterOrEqualTo(2, "key and fieldgroup both reference the field");
    }

    [Fact]
    public async Task ListResolvableTokens_underlines_intra_table_quoted_field_reference()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesHeader.Table.al"] = """
                table 36 "Sales Header"
                {
                    fields
                    {
                        field(1; "No."; Code[20]) { }
                    }

                    trigger OnInsert()
                    begin
                        if "No." = '' then
                            "No." := '???';
                    end;
                }
                """,
        });

        var svc = NewService();
        var allFiles = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var tableFileId = allFiles.Rows.Single(r => r.ObjectName == "Sales Header").Id;

        var ranges = await svc.ListResolvableTokensInFileAsync(tableFileId);

        // Two quoted `"No."` use sites in the trigger body. The field
        // declaration line itself isn't a "resolvable" — that's a
        // `cm-symbol-decl` declaration row from the symbol list.
        ranges.Where(r => r.Line >= 9).Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchProcedures_returns_matches_excluding_fields_object_decls_and_triggers()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = """
                codeunit 80 "Sales-Post"
                {
                    procedure PostDocument()
                    begin
                    end;

                    local procedure PostInternal()
                    begin
                    end;

                    trigger OnRun()
                    begin
                    end;
                }
                """,
            ["Sales/SalesHeader.Table.al"] = """
                table 36 "Sales Header"
                {
                    fields
                    {
                        field(1; "Post Code"; Code[20]) { }
                    }
                }
                """,
        });

        var svc = NewService();
        var result = await svc.SearchProceduresAsync(versionId, "Post", skip: 0, take: 50);

        // The two procedures match; the `field "Post Code"`, the
        // `object_declaration` rows, and the trigger all stay out of
        // results.
        result.Rows.Select(r => r.SymbolName).Should().BeEquivalentTo(new[]
        {
            "PostDocument", "PostInternal",
        });
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task SearchProcedures_returns_empty_for_blank_term()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 80 \"Foo\"\n{\n    procedure DoIt() begin end;\n}\n",
        });
        var svc = NewService();

        var result = await svc.SearchProceduresAsync(versionId, "   ", 0, 50);

        result.Rows.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task SearchContent_finds_files_by_substring_with_first_match_line()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = """
                codeunit 80 "Sales-Post"
                {
                    procedure DoTheNeedful()
                    begin
                        Message('the needful is done');
                    end;
                }
                """,
            ["Other/Unrelated.Codeunit.al"] = """
                codeunit 81 "Unrelated"
                {
                    procedure DoOther()
                    begin
                    end;
                }
                """,
        });

        var svc = NewService();
        var result = await svc.SearchContentAsync(versionId, "needful", 0, 50);

        result.Rows.Should().ContainSingle()
            .Which.ObjectName.Should().Be("Sales-Post");
        result.Rows[0].LineNumber.Should().BeGreaterThan(1);
        // Snippet is the *first* matching line — could be the procedure
        // name (Needful) or the message body (needful). Either is right;
        // we just want a `needful` substring (case-insensitive).
        result.Rows[0].Snippet.Should().ContainEquivalentOf("needful");
    }

    [Fact]
    public async Task GetObjectDeclarationSymbol_returns_the_files_header_row()
    {
        var versionId = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesHeader.Table.al"] = """
                table 36 "Sales Header"
                {
                    fields { field(1; "No."; Code[20]) { } }
                }
                """,
        });

        var svc = NewService();
        var allFiles = await svc.ListFilesAsync(versionId, BaseAppFileFilter.Empty, 0, 50);
        var fileId = allFiles.Rows.Single().Id;

        var sym = await svc.GetObjectDeclarationSymbolAsync(fileId);

        sym.Should().NotBeNull();
        sym!.Kind.Should().Be("object_declaration");
        sym.Name.Should().Be("Sales Header");
    }

    [Fact]
    public async Task CompareVersions_classifies_added_removed_and_changed_by_hash()
    {
        var vA = await SeedVersionAsync(27, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = "codeunit 80 \"Sales-Post\"\n{\n    procedure Post(): Boolean begin exit(true); end;\n}\n",
            ["Sales/SalesHeader.Table.al"] = "table 36 \"Sales Header\" { fields { field(1; \"No.\"; Code[20]) { } } }\n",
            ["Removed/Old.Codeunit.al"] = "codeunit 99 \"Old\"\n{ }\n",
        });
        var vB = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Sales/SalesPost.Codeunit.al"] = "codeunit 80 \"Sales-Post\"\n{\n    procedure Post(): Boolean begin exit(false); end;\n}\n",
            ["Sales/SalesHeader.Table.al"] = "table 36 \"Sales Header\" { fields { field(1; \"No.\"; Code[20]) { } } }\n",
            ["New/Brand.Codeunit.al"] = "codeunit 100 \"Brand\"\n{ }\n",
        });

        var svc = NewService();
        var diff = await svc.CompareVersionsAsync(vA, vB);

        diff.Added.Select(r => r.ObjectName).Should().BeEquivalentTo(new[] { "Brand" });
        diff.Removed.Select(r => r.ObjectName).Should().BeEquivalentTo(new[] { "Old" });
        diff.Changed.Should().ContainSingle()
            .Which.Right.ObjectName.Should().Be("Sales-Post");
        // SalesHeader has identical content on both sides — should not
        // appear in any list.
        diff.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task CompareVersions_matches_by_name_for_id_less_objects()
    {
        // Interfaces have no ObjectId — should fall back to (Type, Name).
        var vA = await SeedVersionAsync(27, new Dictionary<string, string>
        {
            ["Iface.Interface.al"] = "interface \"Refresh\" { procedure Run(); }\n",
        });
        var vB = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["Iface.Interface.al"] = "interface \"Refresh\" { procedure Run(); procedure Reset(); }\n",
        });

        var svc = NewService();
        var diff = await svc.CompareVersionsAsync(vA, vB);

        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
        diff.Changed.Should().ContainSingle()
            .Which.Right.ObjectName.Should().Be("Refresh");
    }

    [Fact]
    public async Task CompareVersions_self_compare_is_empty()
    {
        var vA = await SeedVersionAsync(28, new Dictionary<string, string>
        {
            ["a/Foo.Codeunit.al"] = "codeunit 1 \"Foo\" { }\n",
        });
        var svc = NewService();

        var diff = await svc.CompareVersionsAsync(vA, vA);

        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
        diff.Changed.Should().BeEmpty();
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
            Major: major, CumulativeUpdate: 0,
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
