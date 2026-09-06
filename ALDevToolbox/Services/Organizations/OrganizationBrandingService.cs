using System.Text;
using System.Xml;
using System.Xml.Linq;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Owns how an organisation presents itself: its display name and its logo.
/// Split out of <see cref="OrganizationConfigService"/>, which keeps the read
/// model and the settings writes; every write here goes through that service's
/// cache (<see cref="OrganizationConfigService.InvalidateCache"/> for the config
/// snapshot, and the name cache for a rename) so readers see the change at once.
/// </summary>
public class OrganizationBrandingService
{
    /// <summary>Maximum logo size accepted from the upload form.</summary>
    public const int MaxLogoBytes = 256 * 1024;

    /// <summary>The two MIME types the upload form accepts.</summary>
    public static readonly IReadOnlySet<string> AllowedLogoContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/svg+xml", "image/png" };

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly OrganizationConfigService _config;
    private readonly ILogger<OrganizationBrandingService> _logger;

    public OrganizationBrandingService(
        AppDbContext db,
        IOrganizationContext orgContext,
        StorageQuotaGuard quotaGuard,
        OrganizationConfigService config,
        ILogger<OrganizationBrandingService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _config = config;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Renames the current organisation. The slug is intentionally not
    /// editable — it's baked into the <c>org_id</c>/<c>org_name</c> claim set
    /// at sign-in and into any saved URLs. Cached <c>org_name</c> claims on
    /// open sessions stay stale until the next sign-in (same posture as
    /// display-name and role changes).
    /// </summary>
    public async Task RenameOrganizationAsync(string newName, CancellationToken ct = default)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 80)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Name"] = "Organisation name must be 2-80 characters.",
            });
        }

        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.FirstAsync(o => o.Id == orgId, ct);
        if (string.Equals(org.Name, trimmed, StringComparison.Ordinal)) return;
        org.Name = trimmed;
        await _db.SaveChangesAsync(ct);
        // Set, not Remove. We know the new name, so there is no reason to make
        // the next render go and fetch it -- and that fetch was what raced the
        // page's own queries (#551). Removing the entry is what turned a rename
        // into a 500 on the very next page load.
        _config.CacheOrganizationName(orgId, trimmed);
        _logger.LogInformation("Renamed org {OrgId} to {Name}.", orgId, trimmed);
    }

    /// <summary>
    /// Replaces the logo for the current organisation with the supplied bytes.
    /// SVG uploads are rebuilt from an allow-list of elements and attributes by
    /// <see cref="SanitiseLogo"/> so the rendered logo can't smuggle JavaScript
    /// into a generated workspace or the admin preview; an SVG that isn't
    /// well-formed is refused.
    /// </summary>
    public async Task UploadLogoAsync(string contentType, byte[] content, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedLogoContentTypes.Contains(contentType))
        {
            errors[nameof(contentType)] = "Logo must be an SVG or a PNG.";
        }
        if (content is null || content.Length == 0)
        {
            errors[nameof(content)] = "Pick a logo file to upload.";
        }
        else if (content.Length > MaxLogoBytes)
        {
            errors[nameof(content)] = $"Logo must be {MaxLogoBytes / 1024} KB or smaller.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        await _quotaGuard.EnsureCanWriteAsync(ct);

        var bytes = SanitiseLogo(contentType!, content!);
        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        var row = await _db.OrganizationAssets
            .FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.Kind == OrganizationAssetKind.Logo, ct);
        if (row is null)
        {
            row = new OrganizationAsset
            {
                OrganizationId = orgId,
                Kind = OrganizationAssetKind.Logo,
            };
            _db.OrganizationAssets.Add(row);
        }
        row.ContentType = contentType!;
        row.Content = bytes;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);

        _logger.LogInformation(
            "Uploaded logo for org {OrgId}: {Bytes} bytes ({ContentType}).",
            orgId, bytes.Length, contentType);
    }

    /// <summary>
    /// Removes the per-org logo. With the on-disk seed retired, there is no
    /// "default" logo to revert to; <see cref="GenerationService"/> falls back
    /// to its built-in placeholder when no row is present.
    /// </summary>
    public async Task RevertLogoAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var row = await _db.OrganizationAssets
            .FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.Kind == OrganizationAssetKind.Logo, ct);
        if (row is not null) _db.OrganizationAssets.Remove(row);
        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);
        _logger.LogInformation("Removed logo for org {OrgId}.", orgId);
    }

    /// <summary>
    /// Sanitises an uploaded SVG by re-emitting it from a parsed document with
    /// an <b>allow-list</b> of elements and attributes: anything not named in
    /// <see cref="SvgAllowedElements"/> is dropped together with its subtree,
    /// and any attribute not in <see cref="SvgAllowedAttributes"/> is dropped.
    /// That removes <c>script</c>, <c>foreignObject</c>, <c>use</c>,
    /// <c>animate</c>, <c>animateTransform</c>, <c>set</c>, <c>image</c>,
    /// every <c>on*</c> handler and every namespace we don't understand (HTML
    /// or XLink content smuggled into an SVG) without needing a pattern per
    /// vector. <c>href</c> / <c>xlink:href</c> survive only when the scheme is
    /// http, https, a <c>data:image/*</c> URI, or a same-document fragment.
    ///
    /// <para>The <c>style</c> element and the <c>style</c> attribute survive a
    /// conservative content filter (<see cref="IsSafeCss"/>) instead of being
    /// dropped, because drawing tools put a logo's fills and strokes there and
    /// removing them renders most real logos black. <c>viewBox</c>,
    /// <c>xmlns</c> and the presentation attributes are preserved. The result
    /// is serialised without an XML declaration.</para>
    ///
    /// <para>The document is parsed with DTDs prohibited and no external
    /// resolver, so entity-expansion and external-entity tricks are refused by
    /// the parser. Content that is not well-formed XML — including the
    /// unterminated <c>&lt;script</c> that defeated the old regex pair — is
    /// rejected with a <see cref="PlanValidationException"/> rather than
    /// passed through half-cleaned.</para>
    ///
    /// <para>PNGs pass through unchanged. Public so the sanitiser can be
    /// exercised directly from tests.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">
    /// The bytes are not a well-formed SVG document.
    /// </exception>
    public static byte[] SanitiseLogo(string contentType, byte[] content)
    {
        if (!string.Equals(contentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var stream = new MemoryStream(content);
            using var reader = XmlReader.Create(stream, settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["content"] = "That file isn't a valid SVG image. Save it again from your drawing tool and retry.",
            });
        }

        if (doc.Root is null || doc.Root.Name != SvgNs + "svg")
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["content"] = "That file isn't a valid SVG image. Save it again from your drawing tool and retry.",
            });
        }

        var clean = CleanSvgElement(doc.Root)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["content"] = "That file isn't a valid SVG image. Save it again from your drawing tool and retry.",
            });

        return Encoding.UTF8.GetBytes(clean.ToString(SaveOptions.DisableFormatting));
    }

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLinkNs = "http://www.w3.org/1999/xlink";

    /// <summary>
    /// The SVG elements a logo is allowed to contain. Anything else — script,
    /// foreignObject, use, image, the animation elements — is dropped with its
    /// subtree, the one exception being <c>a</c>, which is unwrapped so a
    /// linked logo keeps its artwork (see <see cref="UnwrapSvgAnchor"/>).
    /// <c>style</c> is allowed but its body must pass
    /// <see cref="IsSafeCss"/>.
    /// </summary>
    private static readonly IReadOnlySet<string> SvgAllowedElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "defs", "linearGradient", "radialGradient", "stop",
        "clipPath", "mask", "pattern", "symbol", "title", "desc", "style",
    };

    /// <summary>
    /// Substrings that disqualify a <c>style</c> attribute or a
    /// <c>&lt;style&gt;</c> body. Everything CSS can use to fetch a resource,
    /// evaluate script, or break back out into markup is here; a declaration
    /// list of plain colours and lengths — what a real logo export contains —
    /// trips none of it. Matched against the value lower-cased with all
    /// whitespace removed, so <c>url ( javascript :</c>-style padding can't
    /// slip past.
    /// </summary>
    private static readonly string[] SvgUnsafeCssMarkers =
    [
        "url(", "expression(", "@import", "javascript:", "behavior:", "-moz-binding", "</", "<",
    ];

    /// <summary>
    /// True when a <c>style</c> attribute value or <c>&lt;style&gt;</c> body is
    /// safe to keep. Drawing tools (Illustrator, Inkscape, Figma) put a logo's
    /// fills and strokes in exactly these two places — dropping them outright
    /// renders most real logos black — so they get a conservative filter rather
    /// than a ban. Failing it costs only that attribute or element, not the
    /// whole file.
    /// </summary>
    private static bool IsSafeCss(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var cleaned = string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        return !SvgUnsafeCssMarkers.Any(marker => cleaned.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Geometry, presentation and layout attributes a logo needs. Deliberately
    /// excludes every <c>on*</c> handler; <c>href</c> and <c>style</c> are
    /// handled separately because their values need a content check.
    /// </summary>
    private static readonly IReadOnlySet<string> SvgAllowedAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        // structure / geometry
        "id", "class", "viewBox", "width", "height", "x", "y", "x1", "y1", "x2", "y2",
        "cx", "cy", "r", "rx", "ry", "d", "points", "dx", "dy", "transform",
        "preserveAspectRatio", "version", "gradientUnits", "gradientTransform",
        "patternUnits", "patternContentUnits", "patternTransform", "clipPathUnits",
        "maskUnits", "maskContentUnits", "spreadMethod", "offset",
        // presentation
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity",
        "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray",
        "stroke-dashoffset", "opacity", "color", "display", "visibility",
        "clip-path", "clip-rule", "mask", "shape-rendering", "vector-effect",
        "stop-color", "stop-opacity", "paint-order", "overflow",
        // text
        "font-family", "font-size", "font-weight", "font-style", "letter-spacing",
        "text-anchor", "dominant-baseline", "xml:space",
    };

    /// <summary>
    /// Recursively copies <paramref name="source"/> keeping only allow-listed
    /// elements and attributes. Returns <c>null</c> when the element itself is
    /// not allowed, so the caller drops it and its subtree.
    /// </summary>
    private static XElement? CleanSvgElement(XElement source)
    {
        if (source.Name.Namespace != SvgNs || !SvgAllowedElements.Contains(source.Name.LocalName))
        {
            return null;
        }

        if (source.Name.LocalName == "style")
        {
            // A style block carries CSS, not markup: keep its text (CDATA
            // included, re-emitted as escaped text) only when it passes the
            // filter, and drop the whole element otherwise.
            var css = string.Concat(source.Nodes().OfType<XText>().Select(t => t.Value));
            return IsSafeCss(css) ? new XElement(SvgNs + "style", new XText(css)) : null;
        }

        var clean = new XElement(SvgNs + source.Name.LocalName);

        foreach (var attr in source.Attributes())
        {
            if (attr.IsNamespaceDeclaration)
            {
                // Keep only the SVG default namespace declaration; a prefix
                // binding for XLink or HTML has nothing left to point at.
                if (attr.Name.LocalName == "xmlns" && attr.Value == SvgNs.NamespaceName)
                {
                    clean.SetAttributeValue("xmlns", SvgNs.NamespaceName);
                }
                continue;
            }

            var isHref = attr.Name.LocalName == "href"
                && (attr.Name.Namespace == XNamespace.None || attr.Name.Namespace == XLinkNs);
            if (isHref)
            {
                if (IsSafeSvgHref(attr.Value))
                {
                    clean.SetAttributeValue("href", attr.Value);
                }
                continue;
            }

            if (attr.Name == "style")
            {
                if (IsSafeCss(attr.Value))
                {
                    clean.SetAttributeValue("style", attr.Value);
                }
                continue;
            }

            // Namespaced attributes (xlink:*, HTML, editor metadata) are out,
            // except xml:space which is plain markup.
            var name = attr.Name.Namespace == XNamespace.None
                ? attr.Name.LocalName
                : attr.Name.Namespace == XNamespace.Xml ? "xml:" + attr.Name.LocalName : null;
            if (name is null || !SvgAllowedAttributes.Contains(name))
            {
                continue;
            }
            if (name == "xml:space")
            {
                clean.SetAttributeValue(XNamespace.Xml + "space", attr.Value);
            }
            else
            {
                clean.SetAttributeValue(name, attr.Value);
            }
        }

        foreach (var node in source.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == SvgNs + "a":
                    // Exporters wrap a whole logo group in <a> when the designer
                    // added a link. Unwrap it — the artwork inside is promoted
                    // into this element and the link itself (with its href and
                    // any target/on* attributes) is discarded. This is the one
                    // unwrapped element: every other unknown name stays dropped
                    // with its subtree.
                    foreach (var unwrapped in UnwrapSvgAnchor(child))
                    {
                        clean.Add(unwrapped);
                    }
                    break;
                case XElement child:
                    var cleanChild = CleanSvgElement(child);
                    if (cleanChild is not null) clean.Add(cleanChild);
                    break;
                case XText text:
                    clean.Add(new XText(text.Value));
                    break;
                // Comments, processing instructions and CDATA are dropped.
            }
        }

        return clean;
    }

    /// <summary>
    /// Cleaned children of an <c>&lt;a&gt;</c>, with nested anchors unwrapped
    /// too. The anchor's own attributes never survive, so a
    /// <c>javascript:</c> href goes with it.
    /// </summary>
    private static IEnumerable<XElement> UnwrapSvgAnchor(XElement anchor)
    {
        foreach (var child in anchor.Elements())
        {
            if (child.Name == SvgNs + "a")
            {
                foreach (var nested in UnwrapSvgAnchor(child)) yield return nested;
                continue;
            }
            var clean = CleanSvgElement(child);
            if (clean is not null) yield return clean;
        }
    }

    /// <summary>
    /// True when an <c>href</c> on an allow-listed SVG element is safe to keep:
    /// a same-document fragment, an http(s) URL, or a <c>data:image/*</c> URI.
    /// Whitespace and control characters are stripped first because browsers
    /// ignore them when parsing the scheme — the same evasion
    /// <see cref="MarkdownRenderer.IsSafeUrl"/> defends against.
    /// </summary>
    private static bool IsSafeSvgHref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var cleaned = string.Concat(value.Where(c => c > ' '));
        if (cleaned.Length == 0) return false;
        if (cleaned[0] == '#') return true;

        if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return cleaned.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }
}
