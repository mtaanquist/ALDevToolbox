using Markdig;
using Microsoft.AspNetCore.Components;

namespace ALDevToolbox.Services;

/// <summary>
/// Singleton wrapper around <see cref="Markdig"/> for the few places in the UI
/// that render user-authored Markdown (snippet instructions, for now). The
/// pipeline is configured once on construction so the parser tables aren't
/// rebuilt per request, and raw HTML is stripped at the parser level so a
/// pasted <c>&lt;script&gt;</c> tag in a snippet's instructions can't escape
/// into the published page.
/// </summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        // UseAdvancedExtensions enables auto-links, fenced code, tables, task
        // lists, footnotes — the common GFM-ish set authors expect. DisableHtml
        // throws away inline / block HTML in the source so the renderer can't
        // emit attacker-controlled tags; combined with the default HTML
        // escaping on text content this gives us a safe sink without us
        // having to bolt on a separate sanitiser.
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();
    }

    /// <summary>
    /// Renders the supplied Markdown to a Blazor <see cref="MarkupString"/>.
    /// Returns an empty <see cref="MarkupString"/> for null / whitespace input
    /// so callers can chain without a null check.
    /// </summary>
    public MarkupString Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }
        return new MarkupString(Markdown.ToHtml(markdown, _pipeline));
    }
}
