using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The dialog behind "Copy this environment...". The named user is a consultant who
/// needs a fresh sandbox of a customer's production and knows nothing about this
/// codebase - so what is pinned here is what they are told before they press the
/// button, and that the button will not go until the name is one Business Central
/// takes. The write itself is the page's job; this collects the answer.
/// </summary>
public sealed class CopyEnvironmentDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public CopyEnvironmentDialogTests()
    {
        // The confirm moves focus on open, which is a JS interop hop.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<CopyEnvironmentDialog> Open(
        bool sourceIsProduction = true, double? storageUse = null)
    {
        var cut = _ctx.Render<CopyEnvironmentDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            "CRONUS Denmark", "Production", sourceIsProduction, storageUse));
        cut.WaitForAssertion(() => cut.Find("#copy-env-name").Should().NotBeNull());
        return cut;
    }

    [Fact]
    public void It_suggests_a_name_and_the_button_says_what_it_makes()
    {
        var cut = Open();

        cut.Find("#copy-env-name").GetAttribute("value").Should().Be("Production-Copy");
        cut.Find("#copy-env-name").GetAttribute("placeholder").Should().Be("e.g. CRONUS-Test");
        // The rules are mirrored in HTML so the browser answers before the server has to.
        cut.Find("#copy-env-name").GetAttribute("maxlength").Should().Be("29");
        cut.Find("#copy-env-name").GetAttribute("pattern").Should().NotBeNullOrWhiteSpace();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Copy to"))
            .TextContent.Should().Contain("Copy to Production-Copy");
    }

    /// <summary>
    /// The name is the only thing the person has to get right, and Business Central's
    /// refusal arrives minutes later as a code - so the rule is on screen as they type
    /// and the button will not go until it is met.
    /// </summary>
    [Fact]
    public void The_button_waits_for_a_name_business_central_would_take()
    {
        var cut = Open();

        cut.Find(".field__hint").TextContent.Should().Contain("Start with a letter");

        cut.WaitForAssertion(() => cut.Find("#copy-env-name").Input("9Lives"));
        cut.WaitForAssertion(() =>
        {
            cut.Find(".field-error").TextContent.Should().Contain("start with a letter");
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Copy")
                .HasAttribute("disabled").Should().BeTrue();
        });

        cut.WaitForAssertion(() => cut.Find("#copy-env-name").Input("CRONUS-Test"));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".field-error").Should().BeEmpty();
            cut.FindAll("button").Single(b => b.TextContent.Contains("Copy to CRONUS-Test"))
                .HasAttribute("disabled").Should().BeFalse();
        });
    }

    /// <summary>
    /// Clearing the suggestion to type your own leaves a dead button; without a message
    /// beside the field that is a dead end, since nothing says what is missing.
    /// </summary>
    [Fact]
    public void Clearing_the_name_says_what_is_missing_rather_than_just_greying_the_button()
    {
        var cut = Open();

        cut.WaitForAssertion(() => cut.Find("#copy-env-name").Input(string.Empty));
        cut.WaitForAssertion(() =>
        {
            cut.Find(".field-error").TextContent.Should().Contain("Enter a name");
            cut.Find("#copy-env-name").GetAttribute("aria-invalid").Should().Be("true");
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Copy")
                .HasAttribute("disabled").Should().BeTrue();
        });
    }

    /// <summary>
    /// A sandbox made from production is not a blank environment: it holds the customer's
    /// real customers, invoices and people, and whoever is given access can read all of it.
    /// </summary>
    [Fact]
    public void Copying_production_to_a_sandbox_says_the_sandbox_holds_real_data()
    {
        var cut = Open(sourceIsProduction: true);

        cut.Find(".note--warn").TextContent.Should().Contain("real data");
        cut.Markup.Should().Contain("CRONUS Denmark");
    }

    [Fact]
    public void Copying_a_sandbox_to_a_sandbox_does_not_warn_about_real_data()
    {
        var cut = Open(sourceIsProduction: false);

        cut.FindAll(".note--warn").Should().BeEmpty();
    }

    /// <summary>
    /// A production copy costs the customer a licence and one of the production
    /// environments they are allowed, which is not something to find out afterwards.
    /// </summary>
    [Fact]
    public void Choosing_production_warns_about_licences_and_turns_the_button_red()
    {
        var cut = Open();

        cut.FindAll(".note--danger").Should().BeEmpty("sandbox is the default");

        cut.WaitForAssertion(() => cut.FindAll(".copy-env__type-opt input")[1].Change(true));
        cut.WaitForAssertion(() =>
        {
            cut.Find(".note--danger").TextContent.Should()
                .Contain("licences").And.Contain("production environments they are allowed");
            cut.FindAll("button").Single(b => b.TextContent.Contains("Copy to"))
                .ClassList.Should().Contain("btn--danger");
        });
    }

    /// <summary>
    /// Microsoft decides whether there is room, so this warns and never blocks: a customer
    /// over their allowance may still have had capacity added by the time they read it.
    /// </summary>
    [Fact]
    public void A_customer_out_of_storage_is_warned_but_not_stopped()
    {
        var cut = Open(sourceIsProduction: false, storageUse: 1.04);

        cut.Find(".note--warn").TextContent.Should().Contain("used up the storage");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Copy to"))
            .HasAttribute("disabled").Should().BeFalse("Business Central decides, not us");
    }

    [Fact]
    public void A_customer_with_room_hears_nothing_about_storage()
    {
        var cut = Open(sourceIsProduction: false, storageUse: 0.4);

        cut.Markup.Should().NotContain("used up the storage");
    }

    /// <summary>The copy outlives the dialog, so the dialog says so and where to watch it.</summary>
    [Fact]
    public void It_says_the_copy_takes_a_while_and_where_to_watch_it()
    {
        var cut = Open();

        cut.Find(".copy-env__wait").TextContent.Should().Contain("an hour or more");
        cut.Find(".copy-env__wait").TextContent.Should().Contain("Operations tab");
        // "You can close this" would read as Cancel, which is the opposite of what happens.
        cut.Find(".copy-env__wait").TextContent.Should().NotContain("close this");
    }

    [Fact]
    public void The_done_message_names_the_copy_and_what_it_was_made_from()
    {
        var message = CopyEnvironmentDialog.DoneMessage(
            new CopyEnvironmentDialog.Copy("CRONUS-Test", BcEnvironmentTypes.Sandbox), "Production");

        message.Should().Contain("Production").And.Contain("CRONUS-Test");
    }
}
