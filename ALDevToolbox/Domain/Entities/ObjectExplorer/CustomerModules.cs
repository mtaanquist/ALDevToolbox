namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

// Which third-party modules a customer has, and at which version. See
// .design/solution-customer-info.md, "Modules".

/// <summary>
/// One entry in the organisation's catalogue of modules support cares about - the
/// handful of add-ons a caller is likely to be asking about, not every app there is.
/// </summary>
public class CustomerModule
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }

    /// <summary>
    /// The Business Central app id, when the module is an app. It is what matches the
    /// catalogue to what an online environment reports; a module without one (an old NAV
    /// add-on) can only be recorded by hand.
    /// </summary>
    public Guid? AppId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A catalogue module recorded by hand on a solution, with the version somebody typed.
/// For on-premises solutions; an online one reads <see cref="OeEnvironmentApp"/> instead.
/// </summary>
public class OeProjectModule
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    public int ModuleId { get; set; }
    public CustomerModule? Module { get; set; }

    public string? Version { get; set; }
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// What Business Central last reported as installed in an environment. A mirror, rewritten
/// wholesale by each successful read (the nightly sweep, or somebody opening the
/// environment's Apps tab), and readable by everyone who can see the solution - which the
/// live read, made with the customer's credentials, is not.
/// </summary>
public class OeEnvironmentApp
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int EnvironmentId { get; set; }
    public OeProjectEnvironment? Environment { get; set; }

    public Guid AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    public DateTime FetchedAt { get; set; }
}
