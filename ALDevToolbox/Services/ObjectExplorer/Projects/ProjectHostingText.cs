using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Where a customer's Business Central runs, in the few words the Solutions
/// list's "Hosted by" column uses. Lives here rather than on the page because
/// the command palette says the same thing about the same solution in a result
/// row, and two lists describing one customer's hosting two ways is the sort of
/// difference nobody notices until they are comparing them.
///
/// <para>The longer form the Customer tab uses ("Microsoft (Business Central
/// online)") stays on that page: it is a form label with room for a sentence,
/// not a column.</para>
/// </summary>
public static class ProjectHostingText
{
    /// <summary>The "Hosted by" column's words for <paramref name="hosting"/>.</summary>
    public static string Short(ProjectHostingType hosting) => hosting switch
    {
        ProjectHostingType.MicrosoftCloud => "Microsoft cloud",
        ProjectHostingType.OurCloud => "Our hosting",
        ProjectHostingType.HostingPartner => "Hosting partner",
        ProjectHostingType.CustomerHardware => "Customer's hardware",
        _ => hosting.ToString(),
    };
}
