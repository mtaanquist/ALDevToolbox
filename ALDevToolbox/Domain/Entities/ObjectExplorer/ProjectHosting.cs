namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// Where a <see cref="OeProject"/>'s Business Central runs. Stored as text, like
/// <see cref="ProjectVisibility"/>. Everything except <see cref="MicrosoftCloud"/> is
/// on-premises as far as we are concerned: there is no admin API to call, so the
/// delivery surfaces are off. See <c>.design/solution-customer-info.md</c>.
/// </summary>
public enum ProjectHostingType
{
    /// <summary>Business Central online, in Microsoft's cloud.</summary>
    MicrosoftCloud,

    /// <summary>Hosted by us.</summary>
    OurCloud,

    /// <summary>Hosted by a third party.</summary>
    HostingPartner,

    /// <summary>On the customer's own hardware.</summary>
    CustomerHardware,
}

/// <summary>How the customer holds their Business Central licence.</summary>
public enum ProjectLicenseType
{
    Purchased,
    Leased,
    Cloud,
}

/// <summary>The Business Central user experience the customer is licensed for.</summary>
public enum ProjectUserExperience
{
    Essential,
    Premium,
}
