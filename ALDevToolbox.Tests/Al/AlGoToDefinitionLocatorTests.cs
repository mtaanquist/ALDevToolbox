using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.Al;

/// <summary>
/// Pins the click-position inspector used by Go to definition. The locator
/// is stateless and pure; service-level resolution against the symbol
/// tables lives in <see cref="ObjectExplorer.BaseAppServiceGoToDefinitionTests"/>.
/// </summary>
public sealed class AlGoToDefinitionLocatorTests
{
    [Fact]
    public void Bare_identifier_returns_word_and_no_qualifier()
    {
        var source = "        Post(SalesHeader);\n";
        var click = AlGoToDefinitionLocator.Inspect(source, line: 1, column: 10); // 'P' of Post

        click.Should().NotBeNull();
        click!.Word.Should().Be("Post");
        click.LeftContext.Operator.Should().BeNull();
        click.LeftContext.Qualifier.Should().BeNull();
    }

    [Fact]
    public void Dot_qualified_call_captures_member_qualifier()
    {
        var source = "        SalesPostCu.Post(SalesHeader);\n";
        // Click on 'P' of Post (column 21).
        var click = AlGoToDefinitionLocator.Inspect(source, line: 1, column: 21);

        click.Should().NotBeNull();
        click!.Word.Should().Be("Post");
        click.LeftContext.Operator.Should().Be(".");
        click.LeftContext.Qualifier.Should().Be("SalesPostCu");
    }

    [Fact]
    public void Dot_qualified_quoted_caller_captures_quoted_qualifier()
    {
        var source = "        \"Sales-Post\".Post(SalesHeader);\n";
        // Click on 'P' of Post — after the closing quote + dot.
        var click = AlGoToDefinitionLocator.Inspect(source, line: 1, column: 22);

        click.Should().NotBeNull();
        click!.Word.Should().Be("Post");
        click.LeftContext.Operator.Should().Be(".");
        click.LeftContext.Qualifier.Should().Be("Sales-Post");
    }

    [Fact]
    public void Quoted_identifier_in_keyword_context_returns_full_name()
    {
        var source = "        SalesPostCu: Codeunit \"Sales-Post\";\n";
        // Click somewhere inside "Sales-Post".
        var click = AlGoToDefinitionLocator.Inspect(source, line: 1, column: 35);

        click.Should().NotBeNull();
        click!.Word.Should().Be("Sales-Post");
        click.LeftContext.Operator.Should().Be("keyword");
        click.LeftContext.Qualifier.Should().Be("Codeunit");
    }

    [Fact]
    public void Double_colon_context_captures_type_keyword()
    {
        var source = "        Run(Codeunit::\"Sales-Post\");\n";
        var click = AlGoToDefinitionLocator.Inspect(source, line: 1, column: 27);

        click.Should().NotBeNull();
        click!.Word.Should().Be("Sales-Post");
        click.LeftContext.Operator.Should().Be("::");
        click.LeftContext.Qualifier.Should().Be("Codeunit");
    }

    [Fact]
    public void Click_on_whitespace_returns_null()
    {
        AlGoToDefinitionLocator.Inspect("   foo   ", 1, 1).Should().BeNull();
    }

    [Fact]
    public void Resolve_variable_type_picks_up_codeunit_declaration()
    {
        var source = """
            codeunit 50100 "Caller"
            {
                procedure DoIt()
                var
                    SalesPostCu: Codeunit "Sales-Post";
                begin
                    SalesPostCu.Post(Foo);
                end;
            }
            """;

        AlGoToDefinitionLocator.ResolveVariableType(source, "SalesPostCu")
            .Should().Be("Sales-Post");
    }

    [Fact]
    public void Resolve_variable_type_handles_unquoted_single_word_object()
    {
        var source = """
            codeunit 50100 "Caller"
            {
                procedure DoIt()
                var
                    Mgt: Codeunit ItemMgt;
                begin
                end;
            }
            """;

        AlGoToDefinitionLocator.ResolveVariableType(source, "Mgt").Should().Be("ItemMgt");
    }
}
