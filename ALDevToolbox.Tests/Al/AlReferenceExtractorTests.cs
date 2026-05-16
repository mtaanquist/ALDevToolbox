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
