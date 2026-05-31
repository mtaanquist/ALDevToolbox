using ALDevToolbox.Services.Cal;
using FluentAssertions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// Unit coverage for the C/AL call-site walker: receiver method calls resolve
/// by id, runtime built-ins are skipped, field-name-taking built-ins emit a
/// field_access for their first argument, bare self-calls resolve to the owner,
/// and implicit <c>Rec</c> field access is captured.
/// </summary>
public sealed class CalReferenceExtractorTests
{
    private static CalExtractScope Scope() => new()
    {
        OwnerKind = "table",
        OwnerId = 18,
        Rec = new CalTypeRef("table", 18),
        Variables =
        {
            ["SalesSetup"] = new CalTypeRef("table", 311),
            ["NoSeriesMgt"] = new CalTypeRef("codeunit", 396),
        },
    };

    private static List<CalRef> Extract(string body) =>
        CalReferenceExtractor.Extract(body, Scope()).References.ToList();

    [Fact]
    public void Resolves_receiver_method_call_by_id()
    {
        var refs = Extract("BEGIN NoSeriesMgt.InitSeries(X,Y); END;");

        refs.Should().ContainSingle(r => r.ReferenceKind == "method_call"
            && r.TargetKind == "codeunit" && r.TargetId == 396 && r.MemberName == "InitSeries");
    }

    [Fact]
    public void Skips_runtime_builtin_methods()
    {
        var refs = Extract("BEGIN SalesSetup.GET; SalesSetup.MODIFY; END;");

        refs.Should().NotContain(r => r.MemberName == "GET" || r.MemberName == "MODIFY");
    }

    [Fact]
    public void Field_name_taking_builtin_emits_field_access_for_first_arg()
    {
        var refs = Extract("BEGIN SalesSetup.TESTFIELD(\"Customer Nos.\"); END;");

        // TESTFIELD itself is skipped; its field argument resolves on SalesSetup (table 311).
        refs.Should().NotContain(r => r.MemberName == "TESTFIELD");
        refs.Should().ContainSingle(r => r.ReferenceKind == "field_access"
            && r.TargetKind == "table" && r.TargetId == 311 && r.MemberName == "Customer Nos.");
    }

    [Fact]
    public void Captures_implicit_rec_field_access()
    {
        var refs = Extract("BEGIN IF \"No.\" = '' THEN \"No.\" := '1'; END;");

        refs.Should().Contain(r => r.ReferenceKind == "field_access"
            && r.TargetKind == "table" && r.TargetId == 18 && r.MemberName == "No.");
    }

    [Fact]
    public void Bare_call_resolves_to_owner_object()
    {
        var refs = Extract("BEGIN SetDefaultSalesperson; UpdateReferencedIds; END;");

        // Bare unqualified identifiers followed by no args aren't calls; with args they are.
        var withArgs = Extract("BEGIN DoStuff(1); END;");
        withArgs.Should().ContainSingle(r => r.ReferenceKind == "method_call"
            && r.TargetKind == "table" && r.TargetId == 18 && r.MemberName == "DoStuff");
    }

    [Fact]
    public void Skips_bare_runtime_functions_and_unresolved_receivers()
    {
        var refs = Extract("BEGIN MESSAGE('hi'); Unknown.Frobnicate(); END;");

        refs.Should().NotContain(r => r.MemberName == "MESSAGE");
        // Unknown receiver isn't in scope → no reference, but counted as unresolved.
        var result = CalReferenceExtractor.Extract("BEGIN Unknown.Frobnicate(); END;", Scope());
        result.References.Should().BeEmpty();
        result.UnresolvedReceivers.Should().Be(1);
    }
}
