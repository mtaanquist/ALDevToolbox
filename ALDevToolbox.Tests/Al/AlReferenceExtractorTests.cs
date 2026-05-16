using System;
using System.Collections.Generic;
using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.Al;

public sealed class AlReferenceExtractorTests
{
    // ── Test fixtures: a small in-memory type catalog ───────────────

    private static readonly Guid BaseAppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");

    /// <summary>
    /// Small catalog with the few types every test below references.
    /// Real imports build the catalog from oe_module_objects /
    /// oe_module_symbols at start-of-pipeline; the resolver interface
    /// stays narrow so unit tests stub it directly.
    /// </summary>
    private static StubResolver MakeResolver()
    {
        var r = new StubResolver();
        // Types.
        r.AddType("Customer", new AlTypeRef(BaseAppId, "table", 18, "Customer"));
        r.AddType("Sales Header", new AlTypeRef(BaseAppId, "table", 36, "Sales Header"));
        r.AddType("Sales Line", new AlTypeRef(BaseAppId, "table", 37, "Sales Line"));
        r.AddType("Sales-Post", new AlTypeRef(BaseAppId, "codeunit", 80, "Sales-Post"));
        // Members on Customer.
        r.AddMember("Customer", new AlMember("Insert", "procedure", null, null));
        r.AddMember("Customer", new AlMember("Validate", "procedure", null, null));
        r.AddMember("Customer", new AlMember("Get", "procedure", null, null));
        r.AddMember("Customer", new AlMember("No.", "field", null, null));
        r.AddMember("Customer", new AlMember("Name", "field", null, null));
        // Members on Sales Header — one field returns a record so we can
        // test chained access through a record-typed field.
        r.AddMember("Sales Header", new AlMember("Sell-to Customer No.", "field", null, null));
        r.AddMember("Sales Header", new AlMember("Customer", "field", "Record", "Customer"));
        // Members on Sales Line.
        r.AddMember("Sales Line", new AlMember("InitRecord", "procedure", null, null));
        r.AddMember("Sales Line", new AlMember("No.", "field", null, null));
        // Members on Sales-Post.
        r.AddMember("Sales-Post", new AlMember("Run", "procedure", null, null));
        return r;
    }

    private static AlExtractContext OwnerCodeunit(StubResolver resolver,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "codeunit",
            OwnerName: "MyHelper",
            OwnerObjectId: 50000,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver);

    private static AlExtractContext OwnerTable(StubResolver resolver, string tableName,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "table",
            OwnerName: tableName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver);

    private static AlExtractContext OwnerPage(StubResolver resolver, string pageName, string? sourceTable,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "page",
            OwnerName: pageName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver,
            OwnerSourceTableName: sourceTable);

    private static AlExtractContext OwnerPageExtension(StubResolver resolver, string extName, string? sourceTable,
        Dictionary<string, ResolvedVariableType>? globals = null) => new(
            OwnerKind: "pageextension",
            OwnerName: extName,
            OwnerObjectId: null,
            OwnerAppId: BaseAppId,
            GlobalVars: globals ?? new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
            Resolver: resolver,
            OwnerSourceTableName: sourceTable);

    // ── Behavioural tests ───────────────────────────────────────────

    [Fact]
    public void Method_call_on_parameter_resolves_to_owner_table()
    {
        const string src = """
            procedure Process(var SalesLine: Record "Sales Line")
            begin
                SalesLine.InitRecord();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Sales Line"
            && r.TargetMemberName == "InitRecord"
            && r.TargetMemberKind == "procedure");
        result.Stats.ResolvedReferences.Should().Be(1);
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Field_access_on_local_var_resolves_to_table_field()
    {
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust."No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "field_access"
            && r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No.");
    }

    [Fact]
    public void Object_scoped_global_variable_resolves_across_procedures()
    {
        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cust"] = new ResolvedVariableType("Record", "Customer"),
        };
        const string src = """
            procedure A() begin Cust.Insert(); end;
            procedure B() begin Cust.Validate(); end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver(), globals));

        result.References.Should().HaveCount(2);
        result.References.Should().AllSatisfy(r =>
            r.TargetObjectName.Should().Be("Customer"));
        result.References.Select(r => r.TargetMemberName).Should().BeEquivalentTo(new[] { "Insert", "Validate" });
    }

    [Fact]
    public void Local_var_shadows_global_with_same_name()
    {
        // Object-global `Cust` is a Sales Line, but the procedure-local
        // `Cust` is a Customer. The reference inside the procedure
        // resolves to the LOCAL type.
        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cust"] = new ResolvedVariableType("Record", "Sales Line"),
        };
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver(), globals));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Validate");
    }

    [Fact]
    public void Rec_inside_a_table_resolves_to_the_owning_table()
    {
        // Inside a table object, `Rec` is the table itself.
        const string src = """
            trigger OnInsert()
            begin
                Rec."No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No.");
    }

    [Fact]
    public void System_typed_variable_yields_no_reference()
    {
        // `Client: HttpClient` is a runtime type, not an AL object.
        // Its members aren't in the type catalog, so the receiver
        // resolution returns null and the reference is dropped.
        const string src = """
            procedure Foo()
            var
                Client: HttpClient;
            begin
                Client.Get('http://example.com');
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(1);
    }

    [Fact]
    public void Chained_access_through_record_field_resolves_at_each_step()
    {
        // SalesHeader."Sell-to Customer No." is a plain field access
        // (scalar). SalesHeader.Customer is a record-typed field that
        // would let chaining continue (Customer."Name") — both refs
        // should appear in the output.
        const string src = """
            procedure Foo()
            var
                SalesHeader: Record "Sales Header";
            begin
                Message(SalesHeader."Sell-to Customer No.");
                Message(SalesHeader.Customer."No.");
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // Three references in total:
        //   1. SalesHeader."Sell-to Customer No." → field on Sales Header
        //   2. SalesHeader.Customer → field on Sales Header (record-typed)
        //   3. Customer."No." → field on Customer (chained via Customer field)
        result.References.Should().HaveCount(3);
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header" && r.TargetMemberName == "Sell-to Customer No.");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header" && r.TargetMemberName == "Customer");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "No.");
    }

    [Fact]
    public void Extension_member_resolves_and_targets_the_extension_object()
    {
        // CustomerExt is a tableextension on Customer that adds a
        // procedure named DKValidate. `Cust.DKValidate()` should
        // resolve through the extension widening and emit a reference
        // whose target is the EXTENSION (not the base Customer), so
        // Find references on DKValidate's declaration row picks the
        // call up.
        var resolver = MakeResolver();
        resolver.AddType("CustomerExt", new AlTypeRef(BaseAppId, "tableextension", 50000, "CustomerExt"));
        resolver.AddExtensionOf("Customer", "CustomerExt");
        resolver.AddMember("CustomerExt", new AlMember("DKValidate", "procedure", null, null));

        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.DKValidate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "CustomerExt"
            && r.TargetObjectKind == "tableextension"
            && r.TargetMemberName == "DKValidate"
            && r.ReferenceKind == "method_call");
    }

    [Fact]
    public void Base_member_shadows_same_name_extension_member()
    {
        // Customer has a Validate procedure of its own AND
        // CustomerExt also has one. AL dispatch picks the base, so the
        // extractor should emit Customer.Validate — not the extension.
        var resolver = MakeResolver();
        resolver.AddType("CustomerExt", new AlTypeRef(BaseAppId, "tableextension", 50000, "CustomerExt"));
        resolver.AddExtensionOf("Customer", "CustomerExt");
        resolver.AddMember("CustomerExt", new AlMember("Validate", "procedure", null, null));

        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Validate");
    }

    [Fact]
    public void Type_literal_receiver_resolves_directly()
    {
        // `Customer.Insert(true)` — the qualifier is a type name, not a
        // variable. The extractor recognises it via the type catalog and
        // resolves the member on the type itself.
        const string src = """
            procedure Foo()
            begin
                Customer.Insert(true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Insert");
    }

    [Fact]
    public void Unresolved_receiver_drops_the_reference_and_records_the_stat()
    {
        // `unknownVar.DoSomething()` — neither a variable nor a known
        // type. Receiver resolution returns null; nothing is emitted.
        const string src = """
            procedure Foo() begin unknownVar.DoSomething(); end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(1);
    }

    [Fact]
    public void Record_builtin_method_is_skipped_silently_not_counted_as_unresolved()
    {
        // SetRange / FindFirst / IsEmpty are AL runtime built-ins, not
        // user-declared procedures. The receiver resolves to Customer
        // but the member lookup misses (built-ins aren't in the
        // catalog). AlBuiltinMethods.IsBuiltin recognises them and
        // filters them out so the unresolved counter stays clean.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.SetRange("No.", 'C001');
                Cust.FindFirst();
                if Cust.IsEmpty() then exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.TargetMemberName is "SetRange" or "FindFirst" or "IsEmpty")
            .Should().BeEmpty(
                because: "SetRange/FindFirst/IsEmpty are runtime built-ins, not user-declared catalog members");
        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "built-ins are filtered explicitly so they don't muddy the diagnostic counter");
    }

    [Fact]
    public void User_declared_method_with_same_name_as_builtin_still_emits_reference()
    {
        // Customer.Validate IS in the resolver (added by MakeResolver).
        // The lookup succeeds before the built-in filter would run, so
        // the user-declared Validate procedure wins — the reference
        // gets emitted normally.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "Validate");
    }

    [Fact]
    public void System_field_on_record_is_treated_as_builtin()
    {
        // SystemId, SystemCreatedAt etc. are added to every table by
        // the AL runtime — not declared in source. Don't emit a
        // reference and don't count as unresolved.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                if Cust."SystemId" <> NullGuid then exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Method_call_emits_method_call_kind_field_access_emits_field_access_kind()
    {
        // The reference_kind comes from syntactic context: followed by
        // `(` is a method_call, anything else is a field_access.
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Insert();
                Cust."Name";
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.TargetMemberName == "Insert" && r.ReferenceKind == "method_call");
        result.References.Should().Contain(r =>
            r.TargetMemberName == "Name" && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Procedure_with_parameters_only_picks_up_param_types()
    {
        // No var block — parameter `c` should still be in scope inside
        // the body.
        const string src = """
            procedure Foo(c: Record Customer; n: Integer)
            begin
                c.Insert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "Insert");
    }

    [Fact]
    public void Typed_literal_receiver_resolves_kind_colon_colon_name()
    {
        // `Codeunit::"Sales-Post".Run(SalesHeader)` — the receiver is
        // a Kind::"Name" type literal, not a variable. Extremely common
        // BC pattern; should resolve to the Sales-Post codeunit and
        // emit a method_call reference for Run.
        const string src = """
            procedure Foo()
            var
                SalesHeader: Record "Sales Header";
            begin
                Codeunit::"Sales-Post".Run(SalesHeader);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "Sales-Post"
            && r.TargetMemberName == "Run");
    }

    [Fact]
    public void Typed_literal_with_unknown_name_drops_chain_as_unresolved()
    {
        // `Codeunit::"Unknown Helper".DoX()` — the literal name doesn't
        // resolve to any object in the catalog; chain drops without
        // false positives.
        const string src = """
            procedure Foo()
            begin
                Codeunit::"Unknown Helper".DoX();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(1);
    }

    [Fact]
    public void Generic_typed_var_does_not_swallow_the_next_declaration()
    {
        // `Dictionary of [Text, Integer]` parsed naively eats the `of`
        // as the next variable name, breaks looking for `:`, then
        // skips to the next `;` — silently dropping the var declared
        // right after. ReadTypeReference now consumes `of [...]` so
        // every subsequent declaration in the var block parses cleanly.
        const string src = """
            procedure Foo()
            var
                Lookup: Dictionary of [Text, Integer];
                Cust: Record Customer;
            begin
                Cust.Insert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // Cust IS in scope after the Dictionary declaration; its
        // .Insert resolves to Customer because the test fixture has
        // Customer.Insert in the user-declared catalog.
        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "Insert");
    }

    [Fact]
    public void List_typed_var_with_generic_parameter_parses_cleanly()
    {
        const string src = """
            procedure Foo()
            var
                Names: List of [Text];
                Cust: Record Customer;
            begin
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer" && r.TargetMemberName == "Validate");
    }

    [Fact]
    public void Reference_carries_source_line_and_column_of_the_member()
    {
        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                Cust.Insert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        var insertRef = result.References.Single();
        insertRef.Line.Should().Be(5,
            because: "Insert sits on the fifth line of the snippet (1-based)");
        // The member name starts at column 10 (after "Cust." inside the indent).
        insertRef.Column.Should().BeGreaterThan(1);
    }

    // ── Rec on pages / pageextensions resolves to SourceTable ──────

    [Fact]
    public void Rec_inside_a_page_resolves_to_the_source_table()
    {
        // On page 42 "Sales Order" with SourceTable = "Sales Header",
        // `Rec."No."` should resolve to the field "No." on Sales Header —
        // not as a member on the page itself (which doesn't have fields).
        const string src = """
            trigger OnAfterGetCurrRecord()
            begin
                Rec."Sell-to Customer No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Rec_method_call_inside_a_page_resolves_via_the_source_table()
    {
        // `Rec.Insert()` on a page triggers the Record's Insert built-in
        // via the SourceTable — but more importantly, calls to USER
        // procedures defined on the source table (or on tableextensions
        // of it) must resolve. AL pages routinely call procedures the
        // table itself declares; this guarantees those calls underline.
        var resolver = MakeResolver();
        // Add a user-declared procedure on Sales Header.
        resolver.AddMember("Sales Header", new AlMember("CheckCreditMaxBeforeInsert", "procedure", null, null));

        const string src = """
            trigger OnValidate()
            begin
                Rec.CheckCreditMaxBeforeInsert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "CheckCreditMaxBeforeInsert"
            && r.ReferenceKind == "method_call");
    }

    [Fact]
    public void Rec_inside_a_pageextension_resolves_to_the_propagated_source_table()
    {
        // Pageextensions don't carry SourceTable in their own symbol-package
        // properties; the importer's second-pass copies it from the base
        // page. The extractor consumes that propagated value the same way.
        const string src = """
            trigger OnAfterGetCurrRecord()
            begin
                Rec."Sell-to Customer No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src,
            OwnerPageExtension(MakeResolver(), "Sales Order Ext", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No.");
    }

    [Fact]
    public void Rec_inside_a_page_without_source_table_falls_back_silently()
    {
        // Pages without an explicit SourceTable (rare — listpages typically
        // have one, but pages without a table source exist). The extractor
        // shouldn't crash and shouldn't produce a wrong reference; it just
        // can't resolve the chain.
        const string src = """
            trigger OnOpenPage()
            begin
                Rec."No." := 'X';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Some Card", null));

        // Page owner type "page" doesn't have a "No." field; ResolveMember
        // returns null and the chain is silently dropped (no spurious
        // emit, no crash).
        result.References.Should().BeEmpty();
    }

    // ── More object-scope properties ───────────────────────────────

    [Fact]
    public void Run_object_property_emits_target_reference()
    {
        // Pages declare RunObject = Page "X" / Codeunit "Y" / Report "Z"
        // on action blocks. Same shape as SourceTable but with a kind
        // keyword in front of the name. The kind keyword is consumed
        // and the catalog resolves the name to the canonical kind.
        var resolver = MakeResolver();
        resolver.AddType("Customer Card", new AlTypeRef(BaseAppId, "page", 21, "Customer Card"));

        const string src = """
            page 22 "Customer List"
            {
                actions
                {
                    action("Card")
                    {
                        RunObject = Page "Customer Card";
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Customer List", null));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer Card"
            && r.TargetObjectKind == "page"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Table_relation_emits_target_table()
    {
        // Simple form: `TableRelation = Customer;` → property_object on
        // the Customer table. The user's primary use case is "click
        // through into the related table when investigating a record".
        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(2; "Sell-to Customer No."; Code[20])
                    {
                        TableRelation = Customer;
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetObjectKind == "table"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Table_relation_with_filter_still_emits_the_base_table()
    {
        // `TableRelation = Customer where(…)` — the with-filter form
        // is common. v1 emits the table reference; field references
        // inside the filter expression stay deferred. The user can
        // still click through to the Customer table.
        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(2; "Sell-to Customer No."; Code[20])
                    {
                        TableRelation = Customer where("Customer Posting Group" = const('DOMESTIC'));
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Sales Header"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Customer"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Table_relation_conditional_emits_a_row_per_branch_table()
    {
        // `TableRelation = if (X) A."Y" else B."Z"` — emit both A and B
        // so the user can shortcut into either branch's table.
        var resolver = MakeResolver();
        resolver.AddType("Item", new AlTypeRef(BaseAppId, "table", 27, "Item"));
        resolver.AddType("Resource", new AlTypeRef(BaseAppId, "table", 156, "Resource"));

        const string src = """
            table 37 "Sales Line"
            {
                fields
                {
                    field(6; "No."; Code[20])
                    {
                        TableRelation = if (Type = const(Item)) Item."No." else Resource."No.";
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Sales Line"));

        result.References.Where(r => r.ReferenceKind == "property_object")
            .Select(r => r.TargetObjectName)
            .Should().Contain(new[] { "Item", "Resource" });
    }

    [Fact]
    public void Permissions_emits_one_reference_per_tabledata_target()
    {
        // `Permissions = tabledata "Customer" = rm, tabledata "Sales Header" = m;`
        // — the rights spec is irrelevant to references; we just want
        // the click-through into each listed table.
        const string src = """
            codeunit 80 "Sales-Post"
            {
                Permissions = tabledata "Customer" = rm, tabledata "Sales Header" = m;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        var perms = result.References.Where(r => r.ReferenceKind == "property_object").ToList();
        perms.Should().HaveCount(2);
        perms.Select(r => r.TargetObjectName)
            .Should().BeEquivalentTo(new[] { "Customer", "Sales Header" });
        perms.Should().AllSatisfy(r => r.TargetObjectKind.Should().Be("table"));
    }

    [Fact]
    public void Field_declaration_with_enum_type_emits_enum_reference()
    {
        // `field(1; "Document Type"; Enum "Sales Document Type")` — the
        // third arg is an AL object reference (the enum). Click-through
        // from the field declaration into the enum.
        var resolver = MakeResolver();
        resolver.AddType("Sales Document Type", new AlTypeRef(BaseAppId, "enum", 39, "Sales Document Type"));

        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(1; "Document Type"; Enum "Sales Document Type") { }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Document Type"
            && r.TargetObjectKind == "enum"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Field_declaration_with_scalar_type_emits_no_reference()
    {
        // `field(2; "Sell-to Customer No."; Code[20])` — `Code` isn't an
        // AL object keyword (it's a primitive scalar). Must not try
        // to resolve it as an object.
        const string src = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(2; "Sell-to Customer No."; Code[20]) { }
                    field(3; "Amount"; Decimal) { }
                    field(4; "Posted"; Boolean) { }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Sales Header"));

        result.References.Where(r => r.ReferenceKind == "property_object")
            .Should().BeEmpty(
                because: "scalar primitive types (Code, Decimal, Boolean) aren't AL objects");
    }

    // ── Labels (tranche 3) ─────────────────────────────────────────

    [Fact]
    public void Bare_use_of_object_scope_label_emits_label_use()
    {
        // `Error(UnsupportedTypeErr)` — UnsupportedTypeErr is declared
        // in the codeunit's object-scope var block as a Label. The bare
        // identifier in the procedure body must resolve as a label_use
        // reference targeting the codeunit + label name + "label" kind.
        const string src = """
            codeunit 50100 "Foo"
            {
                var
                    UnsupportedTypeErr: Label 'Unsupported type %1.';

                procedure DoStuff()
                begin
                    Error(UnsupportedTypeErr);
                end;
            }
            """;
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "label_use"
            && r.TargetMemberName == "UnsupportedTypeErr"
            && r.TargetMemberKind == "label");
    }

    [Fact]
    public void Procedure_local_label_use_does_not_emit_object_scope_ref()
    {
        // A label declared inside a procedure's var block is local to
        // that procedure. The walker's existing ParseVarBlock adds it
        // to the procedure frame; the implicit-Rec / label_use handlers
        // see it as in-scope. Should still emit label_use targeting the
        // file owner (oe_module_symbols stores procedure-locals against
        // the owner object too).
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));

        const string src = """
            codeunit 50100 "Foo"
            {
                procedure DoStuff()
                var
                    LocalErr: Label 'Local error';
                begin
                    Error(LocalErr);
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "label_use"
            && r.TargetMemberName == "LocalErr");
    }

    [Fact]
    public void Non_label_in_scope_variable_does_not_emit_label_use()
    {
        // A regular Record variable in scope should NOT emit label_use
        // on its bare uses. The handler must check the type keyword
        // matches "Label" before emitting.
        const string src = """
            codeunit 50100 "Foo"
            {
                procedure DoStuff()
                var
                    Cust: Record Customer;
                begin
                    if Cust.IsEmpty() then exit;
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "label_use")
            .Should().BeEmpty();
    }

    [Fact]
    public void Multiple_label_uses_in_same_procedure_emit_one_row_each()
    {
        // Real BC code uses Error / Message / Confirm with different
        // label vars on different lines. Each call site should produce
        // its own label_use row so Find references shows them all.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50100, "MyHelper"));

        const string src = """
            codeunit 50100 "Foo"
            {
                var
                    ErrA: Label 'Err A';
                    ErrB: Label 'Err B';

                procedure DoStuff()
                begin
                    if true then
                        Error(ErrA);
                    if true then
                        Error(ErrB);
                    Message(ErrA);
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        var labelUses = result.References.Where(r => r.ReferenceKind == "label_use").ToList();
        labelUses.Should().HaveCount(3);
        labelUses.Count(r => r.TargetMemberName == "ErrA").Should().Be(2);
        labelUses.Count(r => r.TargetMemberName == "ErrB").Should().Be(1);
    }

    // ── CalcFormula + SourceTableView (tranche 2) ──────────────────

    [Fact]
    public void Calc_formula_emits_queried_table_and_target_field()
    {
        // sum("G/L Entry".Amount where(…)) — the queried table goes
        // out as a property_object, and the .Amount field after the
        // table name goes out as a field_access on that table.
        var resolver = MakeResolver();
        resolver.AddType("G/L Entry", new AlTypeRef(BaseAppId, "table", 17, "G/L Entry"));
        resolver.AddMember("G/L Entry", new AlMember("Amount", "field", null, null));
        resolver.AddMember("G/L Entry", new AlMember("G/L Account No.", "field", null, null));

        const string src = """
            table 15 "G/L Account"
            {
                fields
                {
                    field(50; "Balance"; Decimal)
                    {
                        FieldClass = FlowField;
                        CalcFormula = sum("G/L Entry".Amount where("G/L Account No." = field("No.")));
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "G/L Account"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "G/L Entry"
            && r.ReferenceKind == "property_object");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "G/L Entry"
            && r.TargetMemberName == "Amount"
            && r.ReferenceKind == "field_access");
        // LHS of the where pair is a field on the queried table.
        result.References.Should().Contain(r =>
            r.TargetObjectName == "G/L Entry"
            && r.TargetMemberName == "G/L Account No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Calc_formula_count_emits_only_the_queried_table_and_where_fields()
    {
        // count(…) and exist(…) don't have a target field after the
        // table name. The handler must still emit the table and the
        // where-clause field refs.
        var resolver = MakeResolver();
        resolver.AddType("Cust. Ledger Entry", new AlTypeRef(BaseAppId, "table", 21, "Cust. Ledger Entry"));
        resolver.AddMember("Cust. Ledger Entry", new AlMember("Customer No.", "field", null, null));

        const string src = """
            table 18 Customer
            {
                fields
                {
                    field(60; "Entry Count"; Integer)
                    {
                        FieldClass = FlowField;
                        CalcFormula = count("Cust. Ledger Entry" where("Customer No." = field("No.")));
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Customer"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Cust. Ledger Entry"
            && r.ReferenceKind == "property_object");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Cust. Ledger Entry"
            && r.TargetMemberName == "Customer No."
            && r.ReferenceKind == "field_access");
        // No target field after the table → no Amount-style ref.
        result.References.Where(r =>
            r.TargetObjectName == "Cust. Ledger Entry"
            && r.ReferenceKind == "field_access"
            && r.TargetMemberName != "Customer No.").Should().BeEmpty();
    }

    [Fact]
    public void Source_table_view_emits_field_refs_on_pages_source_table()
    {
        // Page's SourceTableView's where() and sorting() clauses both
        // reference fields on the SourceTable (Rec). Both LHS-of-where
        // and sorting list entries get field_access rows.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("No.", "field", null, null));
        resolver.AddMember("Sales Header", new AlMember("Document Type", "field", null, null));

        const string src = """
            page 42 "Sales Order"
            {
                SourceTableView = sorting("No."), where("Document Type" = filter(Order));
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Document Type"
            && r.ReferenceKind == "field_access");
        result.References.Should().Contain(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Source_table_view_filter_value_does_not_leak_into_field_refs()
    {
        // `filter(Order)` on the RHS of the where pair shouldn't be
        // mistaken for a field — its argument is an enum value, not
        // a field name. The handler must only emit on the LHS of `=`.
        var resolver = MakeResolver();
        resolver.AddMember("Sales Header", new AlMember("Document Type", "field", null, null));

        const string src = """
            page 42 "Sales Order"
            {
                SourceTableView = where("Document Type" = filter(Order));
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(resolver, "Sales Order", "Sales Header"));

        var fieldRefs = result.References.Where(r =>
            r.ReferenceKind == "field_access"
            && r.TargetObjectName == "Sales Header").ToList();
        fieldRefs.Should().ContainSingle();
        fieldRefs[0].TargetMemberName.Should().Be("Document Type");
    }

    [Fact]
    public void Calc_formula_with_unknown_queried_table_drops_silently()
    {
        // Cross-release / typo / customer-only table not in the catalog.
        // Don't crash, don't bump unresolved, don't emit garbage refs.
        const string src = """
            table 50000 "Custom"
            {
                fields
                {
                    field(10; "Total"; Decimal)
                    {
                        FieldClass = FlowField;
                        CalcFormula = sum("Nonexistent Table".Amount where("X" = field("Y")));
                    }
                }
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Custom"));

        result.References.Where(r =>
            r.ReferenceKind == "property_object" || r.ReferenceKind == "field_access")
            .Should().BeEmpty();
    }

    // ── EventSubscriber attribute bindings ─────────────────────────

    [Fact]
    public void Event_subscriber_attribute_with_legacy_quoted_event_name_emits_publisher_binding()
    {
        // Classic Microsoft-style EventSubscriber:
        //   [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post",
        //                    'OnAfterPostSalesDoc', '', false, false)]
        // The subscriber row's TargetMemberName carries the event name and
        // TargetMemberKind = "event_publisher" so Find references on the
        // publisher procedure surfaces this binding via the existing
        // member-scoped query.
        const string src = """
            codeunit 50100 "Sales Post Subscribers"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterPostSalesDoc', '', false, false)]
                local procedure HandleAfterPost(var SalesHeader: Record "Sales Header"; CommitIsSuppressed: Boolean)
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "event_publisher"
            && r.TargetObjectName == "Sales-Post"
            && r.TargetObjectKind == "codeunit"
            && r.TargetMemberName == "OnAfterPostSalesDoc"
            && r.TargetMemberKind == "event_publisher");
    }

    [Fact]
    public void Event_subscriber_attribute_with_bare_identifier_event_name_also_emits_binding()
    {
        // Newer BC dropped the apostrophes around the event name (and
        // element name) — bare identifiers now lex as Identifier tokens
        // instead of String tokens. Both shapes must produce the same
        // binding so subscribers in modern code aren't missed.
        const string src = """
            codeunit 50101 "Sales Post Subscribers"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", OnAfterPostSalesDoc, '', false, false)]
                local procedure HandleAfterPost()
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "event_publisher"
            && r.TargetObjectName == "Sales-Post"
            && r.TargetMemberName == "OnAfterPostSalesDoc");
    }

    [Fact]
    public void Event_subscriber_attribute_on_a_table_publisher_resolves()
    {
        // EventSubscriber against a Table publisher rather than a Codeunit
        // — the typed-literal in arg[1] is `Table::"Customer"` instead of
        // `Codeunit::"…"`. Catalog lookup uses the name; the kind comes
        // from whatever object the name resolves to.
        const string src = """
            codeunit 50102 "Cust Subscribers"
            {
                [EventSubscriber(ObjectType::Table, Database::Customer, 'OnAfterInsertEvent', '', false, false)]
                local procedure HandleAfterInsert()
                begin
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().Contain(r =>
            r.ReferenceKind == "event_publisher"
            && r.TargetObjectName == "Customer"
            && r.TargetObjectKind == "table"
            && r.TargetMemberName == "OnAfterInsertEvent");
    }

    [Fact]
    public void Multiple_event_subscribers_in_one_file_each_emit_a_binding()
    {
        // A subscribers codeunit often contains many subscribers. Each
        // attribute must mint its own binding row — the walker must
        // continue past the first matched attribute without losing track.
        const string src = """
            codeunit 50103 "Multi"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterPostSalesDoc', '', false, false)]
                local procedure A() begin end;

                [EventSubscriber(ObjectType::Table, Database::Customer, 'OnAfterInsertEvent', '', false, false)]
                local procedure B() begin end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "event_publisher")
            .Should().HaveCount(2);
        result.References.Where(r => r.ReferenceKind == "event_publisher")
            .Select(r => r.TargetMemberName)
            .Should().BeEquivalentTo(new[] { "OnAfterPostSalesDoc", "OnAfterInsertEvent" });
    }

    [Fact]
    public void Event_subscriber_attribute_with_unknown_target_drops_silently()
    {
        // Target codeunit isn't in the catalog (cross-release, customer
        // extension not imported, typo). Don't crash, don't bump the
        // unresolved counter — the same policy property-object refs
        // use for unknown targets.
        const string src = """
            codeunit 50104 "Unknown"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Nonexistent Cu", 'OnSomething', '', false, false)]
                local procedure A() begin end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "event_publisher")
            .Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Other_attributes_alongside_event_subscriber_do_not_emit_bindings()
    {
        // [Test] / [HandlerFunctions(...)] / [Scope('OnPrem')] etc. must
        // not produce event_publisher rows — only [EventSubscriber] does.
        // The narrow `[ EventSubscriber (` detector ensures attributes
        // with other names skip the binding path.
        const string src = """
            codeunit 50105 "Test Cu"
            {
                [Test]
                [HandlerFunctions('SomeHandler')]
                procedure TestSomething() begin end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Where(r => r.ReferenceKind == "event_publisher")
            .Should().BeEmpty();
    }

    // ── Object-scope property extraction ───────────────────────────

    [Fact]
    public void Source_table_property_emits_object_reference()
    {
        // `SourceTable = "Sales Header";` at the top of a page is a
        // reference to the Sales Header table. The extractor must emit
        // a property_object row at the line/column of the name token
        // so the source viewer can underline and Go-to-definition can
        // resolve via the object-name fallback.
        const string src = """
            page 42 "Sales Order"
            {
                SourceTable = "Sales Header";
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", null));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetObjectKind == "table"
            && r.TargetMemberName == null
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Lookup_page_id_property_emits_page_reference()
    {
        const string src = """
            table 18 Customer
            {
                LookupPageID = "Customer List";
            }
            """;
        var resolver = MakeResolver();
        resolver.AddType("Customer List", new AlTypeRef(BaseAppId, "page", 22, "Customer List"));

        var result = AlReferenceExtractor.Extract(src, OwnerTable(resolver, "Customer"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer List"
            && r.TargetObjectKind == "page"
            && r.ReferenceKind == "property_object");
    }

    [Fact]
    public void Data_caption_fields_emits_one_field_access_per_listed_field()
    {
        // DataCaptionFields = "No.", "Name"; lists two fields on the
        // owner table — each gets its own field_access row so the
        // source viewer underlines each independently and Go-to-
        // definition jumps to the field declaration on the table.
        const string src = """
            table 18 Customer
            {
                DataCaptionFields = "No.", "Name";
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().HaveCount(2);
        result.References.Select(r => r.TargetMemberName)
            .Should().BeEquivalentTo(new[] { "No.", "Name" });
        result.References.Should().AllSatisfy(r =>
        {
            r.TargetObjectName.Should().Be("Customer");
            r.ReferenceKind.Should().Be("field_access");
        });
    }

    [Fact]
    public void Data_caption_fields_on_a_page_targets_the_source_table()
    {
        // A page's DataCaptionFields refers to fields on its SourceTable,
        // not the page itself. Uses the same SourceTable-derived Rec
        // type the body's implicit-field-access path uses.
        const string src = """
            page 42 "Sales Order"
            {
                DataCaptionFields = "Sell-to Customer No.";
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No.");
    }

    [Fact]
    public void Property_extraction_does_not_fire_inside_a_procedure_body()
    {
        // `if x = 5 then` inside a procedure body uses the same `=`
        // punct as a property assignment, but it's not a property —
        // the scope-depth guard ensures property detection only fires
        // at object scope. Without the guard this would mis-parse
        // comparisons as property declarations.
        var resolver = MakeResolver();
        resolver.AddType("SourceTable", new AlTypeRef(BaseAppId, "table", 99, "SourceTable"));

        const string src = """
            codeunit 50100 "Foo"
            {
                procedure Foo()
                var
                    SourceTable: Integer;
                begin
                    if SourceTable = 5 then exit;
                end;
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // No reference should leak out — neither the comparison nor any
        // body-token gets mis-recognised as a SourceTable property
        // referring to the table named "SourceTable" in our fixture.
        result.References.Should().BeEmpty(
            because: "comparisons inside procedure bodies are not property declarations");
    }

    [Fact]
    public void Property_with_unknown_object_name_drops_silently()
    {
        // `SourceTable = "Some Unknown Table";` referencing a table
        // we don't have in the catalog must not throw and must not
        // bump the unresolved counter — silent drop. Unknown targets
        // in property values are common (cross-release, customer
        // extensions); they don't deserve the noise.
        const string src = """
            page 42 "X"
            {
                SourceTable = "Some Unknown Table";
            }
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "X", null));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    // ── Implicit-Rec field access (gap #5 sibling) ─────────────────

    [Fact]
    public void Bare_quoted_identifier_inside_table_trigger_resolves_to_field_on_owner()
    {
        // `"No." := 'X'` inside a table trigger is shorthand for
        // `Rec."No." := 'X'`. The extractor must emit a field_access
        // reference on the owner table even without the Rec qualifier.
        const string src = """
            trigger OnInsert()
            begin
                "No." := 'C001';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "No."
            && r.ReferenceKind == "field_access");
    }

    [Fact]
    public void Bare_quoted_identifier_inside_page_trigger_resolves_to_field_on_source_table()
    {
        // On a page with SourceTable = "Sales Header", a bare `"Sell-to
        // Customer No."` references that field on Sales Header.
        const string src = """
            trigger OnAfterGetCurrRecord()
            begin
                if "Sell-to Customer No." = '' then exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerPage(MakeResolver(), "Sales Order", "Sales Header"));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Sales Header"
            && r.TargetMemberName == "Sell-to Customer No.");
    }

    [Fact]
    public void Bare_unquoted_identifier_matching_owner_field_resolves()
    {
        // Field names that don't need quotes (no spaces) still trigger
        // implicit-field-access. Customer has a "Name" field in the
        // fixture; bare `Name` inside a table body resolves to it.
        const string src = """
            trigger OnInsert()
            begin
                if Name = '' then
                    Name := 'Default';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        // Two reads / writes of Name → two field_access emits.
        result.References.Should().HaveCount(2);
        result.References.Should().AllSatisfy(r =>
        {
            r.TargetObjectName.Should().Be("Customer");
            r.TargetMemberName.Should().Be("Name");
            r.ReferenceKind.Should().Be("field_access");
        });
    }

    [Fact]
    public void Local_variable_shadows_implicit_field_with_same_name()
    {
        // Customer has a field "Name". A local var named `Name` shadows
        // the field — bare `Name` inside the body references the local,
        // not the field. The extractor must not emit a field_access.
        const string src = """
            procedure Foo()
            var
                Name: Text;
            begin
                Name := 'shadowed';
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().BeEmpty(
            because: "the local variable Name shadows the field of the same name");
    }

    [Fact]
    public void Implicit_field_access_skips_statement_keywords_and_literals()
    {
        // `if`, `then`, `not`, `true`, `false`, `exit` etc. are bare
        // identifiers that look like potential field accesses. The
        // keyword filter must drop them before the field lookup so
        // they don't pollute the resolved counter or emit refs.
        const string src = """
            trigger OnInsert()
            begin
                if true then
                    exit;
                if not Confirm('Sure?', true) then
                    exit;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerTable(MakeResolver(), "Customer"));

        result.References.Should().BeEmpty();
    }

    [Fact]
    public void Implicit_field_access_does_not_fire_outside_a_record_owner()
    {
        // Codeunits don't have Rec — implicit-field-access shouldn't
        // even try. A bare quoted identifier inside a codeunit body
        // is most likely a label / string / enum value, none of which
        // we should treat as field accesses.
        const string src = """
            procedure Foo()
            begin
                "Some Token";
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
    }

    [Fact]
    public void Nested_begin_end_blocks_preserve_procedure_scope()
    {
        // Pre-fix bug: any `end;` inside a procedure body popped the
        // scope frame, so local variables declared in the procedure
        // were "out of scope" after the first nested end. This test
        // exercises a deeply nested begin/end and verifies the local
        // `Cust` parameter is still resolvable on the inner `Cust.X()`
        // — which only works if scope wasn't popped prematurely.
        const string src = """
            procedure Foo(Cust: Record Customer)
            begin
                if true then begin
                    if true then begin
                        Cust.Insert();
                    end;
                end;
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().ContainSingle(r =>
            r.TargetObjectName == "Customer"
            && r.TargetMemberName == "Insert");
    }

    [Fact]
    public void Case_blocks_count_toward_block_depth_alongside_begin()
    {
        // `case … end;` opens a block closed by `end` without a paired
        // `begin`. The depth tracker must count `case` as an opener too,
        // otherwise the case's `end` pops the procedure scope.
        const string src = """
            procedure Foo(Cust: Record Customer)
            begin
                case Cust.Insert() of
                    true: exit;
                    false: exit;
                end;
                Cust.Validate();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        // Both Cust.Insert() and Cust.Validate() must emit — the second
        // one only does if the procedure scope survived the case block.
        result.References.Should().HaveCount(2);
        result.References.Select(r => r.TargetMemberName)
            .Should().BeEquivalentTo(new[] { "Insert", "Validate" });
    }

    // ── Bare self-procedure calls (gap #4) ─────────────────────────

    [Fact]
    public void Bare_self_call_resolves_to_owner_member()
    {
        // `DoStuff();` with no receiver — the extractor must look up
        // DoStuff as a member on the file's owner object and emit a
        // method_call reference so Find references / Go to definition
        // can find it.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("DoStuff", "procedure", null, null));

        const string src = """
            procedure Outer()
            begin
                DoStuff();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().ContainSingle(r =>
            r.ReferenceKind == "method_call"
            && r.TargetObjectName == "MyHelper"
            && r.TargetObjectKind == "codeunit"
            && r.TargetMemberName == "DoStuff"
            && r.TargetMemberKind == "procedure");
        result.Stats.UnresolvedReceivers.Should().Be(0);
    }

    [Fact]
    public void Bare_AL_system_function_does_not_emit_a_reference()
    {
        // Message / Error / Confirm look like bare calls but are AL runtime
        // functions, not user procedures. Skip silently so we don't try to
        // resolve them as self-members.
        const string src = """
            procedure Foo()
            begin
                Message('Hello world');
                Error('Bad');
                Confirm('Sure?', true);
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(MakeResolver()));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(0,
            because: "system functions filter to silent skip — no diagnostic bump");
    }

    [Fact]
    public void Statement_keyword_before_paren_does_not_trigger_bare_call_resolution()
    {
        // `if (X) then` and `not (X)` syntactically place a keyword before
        // `(`. The bare-call detector must skip these so it doesn't try
        // to resolve `if` or `not` as procedures.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("DoStuff", "procedure", null, null));

        const string src = """
            procedure Foo()
            var
                Cust: Record Customer;
            begin
                if (Cust.Insert()) then
                    DoStuff();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().HaveCount(2);
        result.References.Should().Contain(r =>
            r.TargetMemberName == "Insert" && r.TargetObjectName == "Customer");
        result.References.Should().Contain(r =>
            r.TargetMemberName == "DoStuff" && r.TargetObjectName == "MyHelper");
    }

    [Fact]
    public void Bare_self_call_inside_a_system_function_argument_still_resolves()
    {
        // The system-function filter must NOT consume the argument list —
        // a `Message('x=%1', GetCount())` call needs the inner GetCount()
        // to be picked up by the main loop as its own bare self-call.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("DoStuff", "procedure", null, null));
        resolver.AddMember("MyHelper", new AlMember("GetCount", "procedure", null, null));

        const string src = """
            procedure Foo()
            begin
                Message('count=%1', GetCount());
                DoStuff();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().HaveCount(2);
        result.References.Select(r => r.TargetMemberName)
            .Should().BeEquivalentTo(new[] { "GetCount", "DoStuff" });
    }

    [Fact]
    public void Bare_call_with_variable_in_scope_is_skipped()
    {
        // A parameter or local named `Cust` shadows any same-named
        // self-member — bare `Cust(...)` isn't even valid AL, but the
        // extractor must not silently emit a self-call when the name is
        // a known variable. (The shadow rule also matches how AL itself
        // resolves the identifier.)
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));
        resolver.AddMember("MyHelper", new AlMember("Cust", "procedure", null, null));

        const string src = """
            procedure Foo(Cust: Record Customer)
            begin
                Cust.Insert();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        // The chain through the parameter Cust is what gets emitted; the
        // same-named self procedure must not.
        result.References.Should().ContainSingle();
        result.References[0].TargetObjectName.Should().Be("Customer");
        result.References[0].TargetMemberName.Should().Be("Insert");
    }

    [Fact]
    public void Bare_call_to_unknown_name_is_dropped_and_counted_as_unresolved()
    {
        // A bare call we can't match against any of (system function,
        // in-scope variable, owner-type member) is a real unresolved.
        // No reference row, but the counter ticks so operators can size
        // the gap.
        var resolver = MakeResolver();
        resolver.AddType("MyHelper", new AlTypeRef(BaseAppId, "codeunit", 50000, "MyHelper"));

        const string src = """
            procedure Foo()
            begin
                Mystery();
            end;
            """;
        var result = AlReferenceExtractor.Extract(src, OwnerCodeunit(resolver));

        result.References.Should().BeEmpty();
        result.Stats.UnresolvedReceivers.Should().Be(1);
    }

    // ── Stub resolver ───────────────────────────────────────────────

    private sealed class StubResolver : IAlTypeResolver
    {
        private readonly Dictionary<string, AlTypeRef> _types =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<AlMember>> _members =
            new(StringComparer.OrdinalIgnoreCase);
        // baseName -> list of extension type names targeting it
        private readonly Dictionary<string, List<string>> _extensionsByBase =
            new(StringComparer.OrdinalIgnoreCase);

        public void AddType(string name, AlTypeRef type) => _types[name] = type;

        public void AddMember(string ownerName, AlMember member)
        {
            if (!_members.TryGetValue(ownerName, out var list))
            {
                list = new List<AlMember>();
                _members[ownerName] = list;
            }
            list.Add(member);
        }

        /// <summary>
        /// Declares that <paramref name="extensionName"/> is an extension of
        /// <paramref name="baseName"/>. Member lookups on the base will
        /// fall through to the extension's members when not found, and the
        /// returned AlMember carries the extension's AlTypeRef in
        /// <see cref="AlMember.DeclaringType"/> so the extractor stamps
        /// the reference at the extension.
        /// </summary>
        public void AddExtensionOf(string baseName, string extensionName)
        {
            if (!_extensionsByBase.TryGetValue(baseName, out var list))
            {
                list = new List<string>();
                _extensionsByBase[baseName] = list;
            }
            list.Add(extensionName);
        }

        public AlTypeRef? ResolveTypeByName(string typeName) =>
            _types.TryGetValue(typeName, out var t) ? t : null;

        public AlMember? ResolveMember(AlTypeRef owner, string memberName)
        {
            // Owner's own members win — matches the real CatalogResolver's
            // shadow semantics.
            if (_members.TryGetValue(owner.Name, out var ownList))
            {
                var direct = ownList.FirstOrDefault(m =>
                    string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                if (direct is not null) return direct;
            }

            // Walk extensions.
            if (_extensionsByBase.TryGetValue(owner.Name, out var extensions))
            {
                foreach (var extName in extensions)
                {
                    if (!_members.TryGetValue(extName, out var extList)) continue;
                    var match = extList.FirstOrDefault(m =>
                        string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;
                    _types.TryGetValue(extName, out var extType);
                    return match with { DeclaringType = extType };
                }
            }

            return null;
        }
    }
}
