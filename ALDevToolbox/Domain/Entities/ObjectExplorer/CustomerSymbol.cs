namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A third-party dependency symbol package (<c>.app</c>) an operator uploaded for a
/// <see cref="Customer"/> after a build failed for want of it. The customer-build
/// pipeline copies these into the symbol cache alongside the repos' committed
/// <c>.alpackages/</c> before compiling, so the next build (or a rebuild triggered
/// from the manage page's "Supply missing symbols" action) can resolve a dependency
/// that lives neither in the repo nor on a Microsoft artifact. Persisted at the
/// customer level — not the release — so it benefits every later build of that
/// customer, including a future auto-build. Org-scoped like the rest of the Object
/// Explorer admin surface. See <c>.design/object-explorer-customer-builds.md</c>
/// ("Manual-symbols recovery").
/// </summary>
public class CustomerSymbol
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the parent customer). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>The uploaded package's file name (e.g. <c>Continia_Document Capture_12.0.0.0.app</c>). Unique per customer — re-uploading the same name replaces it.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The raw <c>.app</c> bytes, written verbatim into the build's symbol cache.</summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>Byte length of <see cref="Content"/>, denormalised so the admin list can show a size without loading the blob.</summary>
    public int ContentLength { get; set; }

    public DateTime CreatedAt { get; set; }
}
