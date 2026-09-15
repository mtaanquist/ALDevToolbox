using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Domain.Entities;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Keeps /site-admin/email readable by someone who has never seen the code.
/// Both checks defend the jargon rule in CLAUDE.md at the one place it is easy
/// to break by accident: text that reaches the page as data rather than as copy.
/// </summary>
public sealed class EmailPurposeLabelTests
{
    [Fact]
    public void Every_purpose_has_a_label_written_for_an_operator()
    {
        // Without this, adding a purpose silently prints its enum name in the
        // "What it was" column.
        foreach (var purpose in Enum.GetValues<EmailPurpose>())
        {
            var label = SiteAdminEmail.Describe(purpose);
            label.Should().NotBe(purpose.ToString(), "{0} needs a label a person would use", purpose);
            label.Should().NotBe("Other email", "{0} has no label of its own", purpose);
        }
    }

    [Theory]
    // The mail library leads with its own type name; the sentence after it is
    // the part an operator can act on.
    [InlineData(
        "MailKit.Net.Smtp.SmtpCommandException: 5.7.57 Client was not authenticated",
        "5.7.57 Client was not authenticated")]
    // A server's own status code is not a type name, so it stays.
    [InlineData("550: no such user here", "550: no such user here")]
    // A stack trace is in the logs; the page shows the first line.
    [InlineData("Connection refused\n   at MailKit.Net.Smtp.SmtpClient.Connect()", "Connection refused")]
    [InlineData("Connection refused", "Connection refused")]
    public void The_reason_shown_drops_the_library_vocabulary(string stored, string expected)
        => SiteAdminEmail.Reason(stored).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_message_with_no_error_shows_no_reason_block(string? stored)
        => SiteAdminEmail.Reason(stored).Should().BeNull();
}
