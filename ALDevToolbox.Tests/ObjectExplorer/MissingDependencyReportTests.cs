using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The sentence a failed extension's build-report row carries when the compiler
/// could not find a dependency (issue #901). The reader is a consultant who has to
/// go and get the right package: it has to say which app, by whom, which version,
/// where the build looked, and - when the solution already holds that app - that
/// the stored one is the wrong version.
/// </summary>
public sealed class MissingDependencyReportTests
{
    private const string ContiniaCoreId = "4b915d7e-c02a-435f-85ab-649086c1e002";
    private const string ForNavCoreId = "6f0293d3-86fc-4ff8-9632-54a580be6546";

    private static readonly string[] LookedIn =
    [
        "the Business Central 26.0 (dk) symbols",
        "the repositories' .alpackages folders",
        "the symbols stored on this solution",
    ];

    private static AppJsonManifest Manifest(params AppJsonDependency[] dependencies) => new(
        Id: "11111111-2222-3333-4444-555555555555",
        Name: "CRONUS Continia Extensions",
        Publisher: "CRONUS",
        Version: "1.0.0.0",
        Application: "26.0.0.0",
        Platform: "26.0.0.0",
        Runtime: "14.0",
        Dependencies: dependencies);

    private static readonly AlcMissingPackage ContiniaCore = new("Continia Software", "Continia Core", "12.1.0.0");

    [Fact]
    public void Names_the_app_its_publisher_its_version_its_id_and_where_the_build_looked()
    {
        var message = MissingDependencyReport.Compose(
            Manifest(new AppJsonDependency(ContiniaCoreId, "Continia Core", "12.1.0.0")),
            [ContiniaCore], [], [], LookedIn);

        message.Should().Be(
            $"Missing dependency: Continia Core by Continia Software, version 12.1.0.0 or later (app id {ContiniaCoreId}). "
            + "Looked in the Business Central 26.0 (dk) symbols, the repositories' .alpackages folders and the symbols stored on this solution.");
        MissingDependencyReport.NamesMissingDependency(message).Should().BeTrue();
    }

    [Fact]
    public void Says_which_version_the_solution_has_stored_when_it_is_too_old()
    {
        var stored = new StoredSymbolPackage("Continia Core 12.0.app", Guid.Parse(ContiniaCoreId),
            "Continia Software", "Continia Core", "12.0.0.0");

        var message = MissingDependencyReport.Compose(
            Manifest(new AppJsonDependency(ContiniaCoreId, "Continia Core", "12.1.0.0")),
            [ContiniaCore], [stored], [], LookedIn);

        message.Should().Contain(
            "12.1.0.0 or later (app id " + ContiniaCoreId + "). This solution has Continia Core 12.0.0.0 stored; the build needs 12.1.0.0 or later. Looked in",
            "what to fetch comes before where the build looked");
    }

    [Fact]
    public void Says_which_version_the_customer_runs_preferring_production()
    {
        var installed = new[]
        {
            new InstalledAppFact("SANDBOX", false, Guid.Parse(ContiniaCoreId), "13.0.0.0"),
            new InstalledAppFact("PROD", true, Guid.Parse(ContiniaCoreId), "12.1.0.4"),
            new InstalledAppFact("PROD", true, Guid.Parse(ForNavCoreId), "7.2.0.0"),
        };
        var stored = new StoredSymbolPackage("cc.app", Guid.Parse(ContiniaCoreId), "Continia Software", "Continia Core", "12.0.0.0");

        var message = MissingDependencyReport.Compose(
            Manifest(new AppJsonDependency(ContiniaCoreId, "Continia Core", "12.1.0.0")),
            [ContiniaCore], [stored], [], LookedIn, installed);

        message.Should().Contain(
            "The customer's PROD environment has Continia Core 12.1.0.4 installed. "
            + "This solution has Continia Core 12.0.0.0 stored; the build needs 12.1.0.0 or later. Looked in");
    }

    [Fact]
    public void Matches_a_stored_package_by_app_id_even_when_it_was_renamed()
    {
        // The vendor renamed the app between versions; the id is what identifies it.
        var stored = new StoredSymbolPackage("cc.app", Guid.Parse(ContiniaCoreId),
            "Continia Software", "Continia Core Platform", "11.0.0.0");

        var message = MissingDependencyReport.Compose(
            Manifest(new AppJsonDependency(ContiniaCoreId, "Continia Core", "12.1.0.0")),
            [ContiniaCore], [stored], [], LookedIn);

        message.Should().Contain("This solution has Continia Core Platform 11.0.0.0 stored");
    }

    [Fact]
    public void Says_nothing_about_a_stored_package_that_is_new_enough_or_another_app()
    {
        var newer = new StoredSymbolPackage("cc.app", Guid.Parse(ContiniaCoreId), "Continia Software", "Continia Core", "12.2.0.0");
        var other = new StoredSymbolPackage("fn.app", Guid.Parse(ForNavCoreId), "ForNAV", "ForNAV Core", "1.0.0.0");

        var message = MissingDependencyReport.Compose(
            Manifest(new AppJsonDependency(ContiniaCoreId, "Continia Core", "12.1.0.0")),
            [ContiniaCore], [newer, other], [], LookedIn);

        message.Should().NotContain("stored;");
    }

    [Fact]
    public void Lists_every_missing_dependency_in_one_message()
    {
        var message = MissingDependencyReport.Compose(
            Manifest(
                new AppJsonDependency(ContiniaCoreId, "Continia Core", "12.1.0.0"),
                new AppJsonDependency(ForNavCoreId, "ForNAV Core", "7.0.0.0")),
            [ContiniaCore, new AlcMissingPackage("ForNAV", "ForNAV Core", "7.0.0.0")], [], [], LookedIn);

        message.Should().StartWith("Missing dependencies: Continia Core by Continia Software")
            .And.Contain($"; ForNAV Core by ForNAV, version 7.0.0.0 or later (app id {ForNavCoreId}).");
        MissingDependencyReport.NamesMissingDependency(message).Should().BeTrue();
    }

    [Fact]
    public void A_microsoft_package_has_no_app_id_to_report()
    {
        // Application and System are declared through app.json's application and
        // platform fields, not its dependencies.
        var message = MissingDependencyReport.Compose(
            Manifest(), [new AlcMissingPackage("Microsoft", "Application", "26.0.0.0")], [], [], LookedIn);

        message.Should().StartWith("Missing dependency: Application by Microsoft, version 26.0.0.0 or later. Looked in");
    }

    [Fact]
    public void A_dependency_on_a_sibling_that_failed_is_not_a_symbols_problem()
    {
        const string siblingId = "22222222-2222-3333-4444-555555555555";

        var message = MissingDependencyReport.Compose(
            Manifest(new AppJsonDependency(siblingId, "CRONUS Base", "1.0.0.0")),
            [new AlcMissingPackage("CRONUS", "CRONUS Base", "1.0.0.0")], [], [siblingId], LookedIn);

        message.Should().Be("Needs CRONUS Base, which is built by this solution but did not compile - fix that first.");
        MissingDependencyReport.NamesMissingDependency(message).Should().BeFalse(
            "uploading symbols would not help; the sibling has to build");
    }

    [Fact]
    public void Nothing_missing_means_nothing_to_say()
    {
        MissingDependencyReport.Compose(Manifest(), [], [], [], LookedIn).Should().BeNull();
        MissingDependencyReport.NamesMissingDependency("Compilation failed (see the build report for X).").Should().BeFalse();
        MissingDependencyReport.NamesMissingDependency(null).Should().BeFalse();
    }
}
