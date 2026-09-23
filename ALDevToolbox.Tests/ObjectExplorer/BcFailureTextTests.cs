using ALDevToolbox.Services.ObjectExplorer.Delivery;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Business Central's failure text, taken apart (#930). The shapes are the ones a real
/// failed install produced: the Data Plane Admin Service wrapper around a JSON fragment
/// whose message is in the environment's language, with escaped quotes.
/// </summary>
public sealed class BcFailureTextTests
{
    private const string DanishMessage =
        "Udvidelsen \"CRONUS Core by CRONUS International 28.2.17.126\" kunne ikke installeres, fordi feltet 12 \"Zone Priority\" "
        + "i tabel 50110 \"Pick Zone\" er fjernet, og synkroniseringstilstanden tillader kun tilføjelser.";

    // As errorMessage arrived: the wrapper on one line, the JSON after it, quotes escaped.
    private const string RealErrorMessage =
        "A request to the Data Plane Admin Service failed. Http status code: BadRequest Error: { \"code\": \"ExtensionChangeFailed\", "
        + "\"message\": \"Udvidelsen \\\"CRONUS Core by CRONUS International 28.2.17.126\\\" kunne ikke installeres, fordi feltet 12 \\\"Zone Priority\\\" "
        + "i tabel 50110 \\\"Pick Zone\\\" er fjernet, og synkroniseringstilstanden tillader kun tilføjelser.\" }";

    [Fact]
    public void The_real_message_gives_the_code_and_the_Danish_message_verbatim_without_the_wrapper()
    {
        var detail = BcFailureText.Parse(RealErrorMessage);

        detail.Code.Should().Be("ExtensionChangeFailed");
        detail.Message.Should().Be(DanishMessage, "the message is shown as given, never translated or trimmed");
        detail.FromBusinessCentral.Should().BeTrue();
        detail.Truncated.Should().BeFalse();
        detail.Message.Should().NotContain("Data Plane Admin Service").And.NotContain("{");
    }

    [Fact]
    public void The_multi_line_shape_with_an_inner_error_gives_both_codes_and_the_inner_message()
    {
        const string text = "A request to the Data Plane Admin Service failed.\r\nHttp status code: BadRequest\r\nError:\r\n{\r\n"
            + "  \"code\": \"ExtensionChangeFailed\",\r\n  \"message\": \"Installationen mislykkedes.\",\r\n"
            + "  \"innerError\": {\r\n    \"code\": \"TenantSyncFailure\",\r\n    \"message\": \"Tabellen kunne ikke synkroniseres.\"\r\n  }\r\n}";

        var detail = BcFailureText.Parse(text);

        detail.Code.Should().Be("ExtensionChangeFailed");
        detail.InnerCode.Should().Be("TenantSyncFailure");
        detail.Message.Should().Be("Installationen mislykkedes.");
        detail.InnerMessage.Should().Be("Tabellen kunne ikke synkroniseres.");
    }

    [Fact]
    public void A_row_stored_before_the_fix_reads_the_same_even_when_it_was_clipped_mid_message()
    {
        // The old run put its own opening first and cut the whole thing at 300 characters.
        var legacy = ("Business Central reported the install as failed (ExtensionChangeFailed). " + RealErrorMessage)[..300];

        var detail = BcFailureText.Parse(legacy);

        detail.Code.Should().Be("ExtensionChangeFailed");
        detail.FromBusinessCentral.Should().BeTrue();
        detail.Truncated.Should().BeTrue("the JSON never closed");
        DanishMessage.Should().StartWith(detail.Message);
        detail.Message.Should().StartWith("Udvidelsen \"CRONUS Core");
    }

    [Fact]
    public void A_message_with_no_JSON_comes_back_whole_as_ours()
    {
        const string text = "The build's deliverables changed under the delivery.";

        var detail = BcFailureText.Parse(text);

        detail.Code.Should().BeEmpty();
        detail.Message.Should().Be(text);
        detail.FromBusinessCentral.Should().BeFalse();
    }

    [Fact]
    public void An_http_error_from_the_Admin_Center_API_has_no_code_and_keeps_its_text()
    {
        // What BcAppManagementClient throws for a non-success status: our sentence, then
        // the "code: message" the error body carried - no JSON left in it.
        const string text = "The Admin Center API returned 409 while uploading the app. OperationInProgress: Another operation is running on this environment.";

        var detail = BcFailureText.Parse(text);

        detail.Code.Should().BeEmpty("the code is not branched on when it only survives as prose");
        detail.Message.Should().Be(text);
        detail.FromBusinessCentral.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_parses_to_nothing(string? text)
    {
        BcFailureText.Parse(text).Should().Be(BcFailureDetail.Empty);
    }

    [Fact]
    public void Known_unknown_and_missing_codes_each_get_their_sentence()
    {
        BcFailureText.Sentence("ExtensionChangeFailed").Should().Be(
            "Business Central refused a schema change (a renamed or removed table or field). "
            + "Release again with Force sync to push it through, or keep the old names.");
        BcFailureText.Sentence("SomethingNew").Should().Be("Business Central refused the install (SomethingNew).");
        BcFailureText.Sentence("").Should().Be("Business Central reported the install as failed.");

        BcFailureText.Summary("ExtensionChangeFailed").Should().Be("Business Central refused a schema change");
        BcFailureText.WhatHappened("ExtensionChangeFailed", "CRONUS Core 28.2.17.126").Should()
            .Be("Business Central refused a schema change while installing CRONUS Core 28.2.17.126.");
        BcFailureText.WhatHappened("SomethingNew", "CRONUS Core 1.0.0.0").Should()
            .Be("Business Central refused the install of CRONUS Core 1.0.0.0 (SomethingNew).");
    }

    [Fact]
    public void The_app_message_is_the_sentence_the_codes_and_the_message_as_given()
    {
        var detail = BcFailureText.Parse(RealErrorMessage) with { InnerCode = "TenantSyncFailure" };

        BcFailureText.AppMessage(detail).Should().Be(
            BcFailureText.Sentence("ExtensionChangeFailed")
            + " Error code: ExtensionChangeFailed / TenantSyncFailure."
            + " Business Central's message: " + DanishMessage);
    }

    [Fact]
    public void Force_sync_is_not_suggested_to_a_release_that_already_used_it()
    {
        BcFailureText.NextStep("ExtensionChangeFailed", "Test", forceSync: false).Should()
            .Contain("release this build again with Force sync").And.Contain("\"Test\"");
        BcFailureText.NextStep("ExtensionChangeFailed", "Test", forceSync: true).Should()
            .Contain("already used Force sync").And.NotContain("release this build again");
    }
}
