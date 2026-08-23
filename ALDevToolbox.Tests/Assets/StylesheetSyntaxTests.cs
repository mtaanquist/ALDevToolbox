using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Cheap structural checks on the stylesheets, for the failures that produce no
/// error anywhere - the browser just drops what it cannot parse and the page
/// looks subtly wrong.
/// </summary>
/// <remarks>
/// Written after an edit left prose sitting outside the comment it belonged to,
/// followed by a second <c>*/</c>. CSS then read that prose as the start of a
/// selector and swallowed the rule below it. Nothing failed: the build was
/// green, the file was served, and `curl` showed the rule right there in the
/// text. It only surfaced by measuring the element in a browser and finding
/// display:block where the sheet said grid.
/// </remarks>
public sealed class StylesheetSyntaxTests
{
    public static TheoryData<string> Stylesheets()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(FindWwwroot(), "*.css").OrderBy(f => f))
        {
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Stylesheets))]
    public void Comments_are_balanced(string sheet)
    {
        var text = File.ReadAllText(Path.Combine(FindWwwroot(), sheet));

        // CSS comments do not nest: once one is open, everything up to the next
        // */ is body, including any further /*. So a /* inside a comment is not
        // a finding - admin.css legitimately writes /admin/administration/* in
        // prose. The two that ARE findings: a */ with nothing open (the text
        // before it becomes a selector and eats the next rule), and a comment
        // that never closes (which eats the rest of the file).
        var open = false;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (!open && text[i] == '/' && text[i + 1] == '*')
            {
                open = true;
                i++;
            }
            else if (text[i] == '*' && text[i + 1] == '/')
            {
                open.Should().BeTrue(
                    "{0} has a stray */ at offset {1} with no comment open. Whatever precedes it is " +
                    "being read as a selector, and the rule after it is silently dropped",
                    sheet, i);
                open = false;
                i++;
            }
        }

        open.Should().BeFalse("{0} leaves a comment unclosed, which eats the rest of the file", sheet);
    }

    [Theory]
    [MemberData(nameof(Stylesheets))]
    public void Braces_are_balanced(string sheet)
    {
        var text = StripComments(File.ReadAllText(Path.Combine(FindWwwroot(), sheet)));

        var depth = 0;
        foreach (var c in text)
        {
            if (c == '{') depth++;
            else if (c == '}') depth--;
            depth.Should().BeGreaterThanOrEqualTo(0, "{0} closes a brace it never opened", sheet);
        }

        depth.Should().Be(0, "{0} leaves {1} brace(s) open", sheet, depth);
    }

    /// <summary>
    /// Blanks each comment's body in place, keeping length, so the brace walker
    /// never counts a brace that is only ever mentioned in prose.
    /// </summary>
    private static string StripComments(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length - 1; i++)
        {
            if (chars[i] != '/' || chars[i + 1] != '*') continue;
            var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            if (end < 0) end = chars.Length - 2;
            for (var j = i; j < end + 2 && j < chars.Length; j++)
            {
                if (chars[j] != '\n') chars[j] = ' ';
            }
            i = end + 1;
        }
        return new string(chars);
    }

    private static string FindWwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("could not locate repo root (looking for ALDevToolbox.slnx)");
        return Path.Combine(dir!.FullName, "ALDevToolbox", "wwwroot");
    }
}
