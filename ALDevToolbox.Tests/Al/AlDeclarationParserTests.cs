using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.Al;

/// <summary>
/// Pins the regex-based declaration extractor used by the Object Explorer
/// import pipeline. AL files can prepend a <c>namespace</c> line and one or
/// more <c>using</c> lines before the object declaration, so the parser walks
/// past those plus comments and attributes before matching the declaration.
/// </summary>
public sealed class AlDeclarationParserTests
{
    [Fact]
    public void Parses_simple_codeunit_with_quoted_name()
    {
        var source = "codeunit 80 \"Sales-Post\"\n{\n}";

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("codeunit");
        declaration.Id.Should().Be(80);
        declaration.Name.Should().Be("Sales-Post");
        declaration.Namespace.Should().BeNull();
    }

    [Fact]
    public void Parses_table_with_namespace_and_using_lines()
    {
        var source = """
            namespace Microsoft.Sales.Document;

            using Microsoft.Foundation.NoSeries;
            using Microsoft.Inventory.Item;

            table 36 "Sales Header"
            {
                Caption = 'Sales Header';
            }
            """;

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("table");
        declaration.Id.Should().Be(36);
        declaration.Name.Should().Be("Sales Header");
        declaration.Namespace.Should().Be("Microsoft.Sales.Document");
    }

    [Fact]
    public void Parses_interface_without_id_and_unquoted_name()
    {
        var source = "interface ICustomerLookup\n{\n}";

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("interface");
        declaration.Id.Should().BeNull();
        declaration.Name.Should().Be("ICustomerLookup");
    }

    [Fact]
    public void Parses_pageextension_with_extends_clause()
    {
        var source = """
            namespace Microsoft.Inventory.Item;

            pageextension 50100 "Item Card Ext" extends "Item Card"
            {
            }
            """;

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("pageextension");
        declaration.Id.Should().Be(50100);
        declaration.Name.Should().Be("Item Card Ext");
    }

    [Fact]
    public void Skips_leading_attribute_before_declaration()
    {
        var source = """
            namespace Microsoft.Finance;
            using System;

            [Obsolete('Use Customer 2.0 instead.', '24.0')]
            codeunit 99999 "Legacy Customer Helper"
            {
            }
            """;

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("codeunit");
        declaration.Id.Should().Be(99999);
        declaration.Name.Should().Be("Legacy Customer Helper");
    }

    [Fact]
    public void Ignores_block_comment_above_declaration()
    {
        var source = """
            /*
             * Posts a sales order. Touch this and feel the wrath of the audit log.
             */
            codeunit 80 "Sales-Post"
            {
            }
            """;

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("codeunit");
        declaration.Id.Should().Be(80);
    }

    [Fact]
    public void Ignores_line_comment_above_declaration()
    {
        var source = """
            // Header comment.
            // Another comment.
            page 21 "Customer Card"
            {
                PageType = Card;
            }
            """;

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("page");
        declaration.Id.Should().Be(21);
        declaration.Name.Should().Be("Customer Card");
    }

    [Fact]
    public void Tolerates_crlf_line_endings()
    {
        var source = "namespace X.Y;\r\nusing System;\r\n\r\ntable 18 \"Customer\"\r\n{\r\n}\r\n";

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("table");
        declaration.Id.Should().Be(18);
        declaration.Name.Should().Be("Customer");
        declaration.Namespace.Should().Be("X.Y");
    }

    [Fact]
    public void Strips_utf8_bom()
    {
        var source = "﻿codeunit 1 \"Application Management\"\n{\n}";

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("codeunit");
        declaration.Id.Should().Be(1);
        declaration.Name.Should().Be("Application Management");
    }

    [Fact]
    public void Parses_enum_extension()
    {
        var source = """
            namespace Microsoft.Inventory.Item;

            enumextension 50100 "Item Type Ext" extends "Item Type"
            {
                value(50100; ServiceContract) { Caption = 'Service Contract'; }
            }
            """;

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("enumextension");
        declaration.Id.Should().Be(50100);
        declaration.Name.Should().Be("Item Type Ext");
    }

    [Fact]
    public void Returns_null_when_no_declaration_found()
    {
        var source = """
            namespace X.Y;
            using System;

            // No object here, just a stray header file.
            """;

        AlDeclarationParser.Parse(source).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_source()
    {
        AlDeclarationParser.Parse("").Should().BeNull();
        AlDeclarationParser.Parse("   \n  \n").Should().BeNull();
    }

    [Fact]
    public void Lower_cases_type_keyword_regardless_of_input_casing()
    {
        var source = "Codeunit 80 \"Sales-Post\" { }";

        var declaration = AlDeclarationParser.Parse(source);

        declaration.Should().NotBeNull();
        declaration!.Type.Should().Be("codeunit");
    }
}
