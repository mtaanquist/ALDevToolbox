using ALDevToolbox.Components.Shared;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the public surface of <see cref="ConfirmDialog"/> — the
/// <c>Task&lt;bool&gt;</c> returned by <c>OpenAsync()</c> must resolve on
/// every dismissal path (Confirm button, Cancel button, backdrop click,
/// Escape key). Regressions here are silent until a user hits Delete, so
/// the dialog is worth covering even though it's a single component.
/// </summary>
public sealed class ConfirmDialogTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public ConfirmDialogTests()
    {
        // ConfirmDialog calls ElementReference.FocusAsync() from the focus
        // sentinels — that's a JS interop hop. Default bUnit mode throws on
        // unregistered invocations; loose mode returns the default value so
        // we don't have to stub each call individually.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Renders_nothing_until_OpenAsync_is_called()
    {
        var cut = _ctx.RenderComponent<ConfirmDialog>(p => p
            .Add(c => c.Title, "Delete X?"));

        cut.FindAll("div.confirm-modal").Should().BeEmpty(
            "the dialog is closed by default — the parent component opens it on demand");
    }

    [Fact]
    public async Task Confirm_button_resolves_OpenAsync_with_true()
    {
        var cut = _ctx.RenderComponent<ConfirmDialog>();

        Task<bool>? resultTask = null;
        // Brace-body lambda so InvokeAsync sees an Action, not Func<Task<bool>>
        // — otherwise it awaits OpenAsync's task, which never completes until
        // we click the button below, and the test deadlocks.
        await cut.InvokeAsync(() =>
        {
            resultTask = cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        cut.Find("h2.confirm-modal__title").TextContent.Should().Be("Delete X?");
        cut.Find("p.confirm-modal__message").TextContent.Should().Be("Are you sure?");

        // The confirm button is the second .btn — it carries the danger class.
        cut.Find("button.btn--danger").Click();

        (await resultTask!).Should().BeTrue();
        cut.FindAll("div.confirm-modal").Should().BeEmpty("Confirm closes the dialog");
    }

    [Fact]
    public async Task Cancel_button_resolves_OpenAsync_with_false()
    {
        var cut = _ctx.RenderComponent<ConfirmDialog>();

        Task<bool>? resultTask = null;
        await cut.InvokeAsync(() =>
        {
            resultTask = cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        // The cancel button is the first <button class="btn"> inside the
        // actions row — it does not carry the danger modifier.
        var cancel = cut.FindAll("button.btn")
            .First(b => !(b.GetAttribute("class") ?? string.Empty).Contains("btn--danger"));
        cancel.Click();

        (await resultTask!).Should().BeFalse();
        cut.FindAll("div.confirm-modal").Should().BeEmpty();
    }

    [Fact]
    public async Task Backdrop_click_resolves_OpenAsync_with_false()
    {
        var cut = _ctx.RenderComponent<ConfirmDialog>();

        Task<bool>? resultTask = null;
        await cut.InvokeAsync(() =>
        {
            resultTask = cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        cut.Find("div.confirm-modal__backdrop").Click();

        (await resultTask!).Should().BeFalse(
            "clicking outside the panel is a cancel affordance — standard modal behaviour");
    }

    [Fact]
    public async Task Escape_keydown_resolves_OpenAsync_with_false()
    {
        var cut = _ctx.RenderComponent<ConfirmDialog>();

        Task<bool>? resultTask = null;
        await cut.InvokeAsync(() =>
        {
            resultTask = cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        cut.Find("div.confirm-modal").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        (await resultTask!).Should().BeFalse(
            "the comment on OnKeyDown calls Escape out as the standard dialog affordance");
    }

    [Fact]
    public async Task Reopening_resolves_the_previous_task_with_false_and_starts_a_fresh_one()
    {
        var cut = _ctx.RenderComponent<ConfirmDialog>();

        Task<bool>? first = null;
        await cut.InvokeAsync(() =>
        {
            first = cut.Instance.OpenAsync("First?", "msg", "Yes");
        });

        Task<bool>? second = null;
        await cut.InvokeAsync(() =>
        {
            second = cut.Instance.OpenAsync("Second?", "msg", "Yes");
        });

        (await first!).Should().BeFalse(
            "OpenAsync's comment promises the previous unresolved task is replaced — "
            + "leaking the prior caller's continuation would be a memory hazard");

        cut.Find("h2.confirm-modal__title").TextContent.Should().Be("Second?");
        cut.Find("button.btn--danger").Click();
        (await second!).Should().BeTrue();
    }
}
