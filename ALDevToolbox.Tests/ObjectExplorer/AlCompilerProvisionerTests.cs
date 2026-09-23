using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The pure selection logic of <see cref="AlCompilerProvisioner"/>: which package
/// version to install (newest, or a pin) and which target-framework folder to
/// extract (prefer net10.0 so it runs natively on the runtime image, else the
/// highest netN). The download/extract IO is exercised by the end-to-end smoke,
/// not here.
/// </summary>
public sealed class AlCompilerProvisionerTests
{
    [Fact]
    public void PickNewest_returns_the_newest_stable_when_no_pin()
    {
        // The NuGet flat-container index is SemVer-ascending, so newest is last;
        // a prerelease is never the default (#921), only a pin selects one.
        var versions = new[] { "16.2.28.57946", "17.0.27.27275-beta", "17.0.34.45391", "18.0.37.11445-beta" };
        AlCompilerProvisioner.PickNewest(versions, pin: null).Should().Be("17.0.34.45391");
        AlCompilerProvisioner.PickNewest(new[] { "18.0.37.11445-beta" }, pin: null).Should().Be("18.0.37.11445-beta",
            "a feed with nothing but prereleases still yields something");
    }

    [Fact]
    public void PickNewest_honours_a_matching_pin()
    {
        var versions = new[] { "16.2.28.57946", "17.0.27.27275-beta", "18.0.37.11445-beta" };
        AlCompilerProvisioner.PickNewest(versions, pin: "16.2.28.57946").Should().Be("16.2.28.57946");
    }

    [Fact]
    public void PickNewest_returns_null_when_pin_absent_or_no_versions()
    {
        AlCompilerProvisioner.PickNewest(new[] { "16.2.28.57946" }, pin: "99.0.0.0").Should().BeNull();
        AlCompilerProvisioner.PickNewest(Array.Empty<string>(), pin: null).Should().BeNull();
    }

    [Fact]
    public void PickTfm_prefers_net10_for_native_runtime()
    {
        var entries = new[]
        {
            "lib/net8.0/alc", "lib/net8.0/altool.dll",
            "lib/net10.0/alc", "lib/net10.0/altool.dll",
            "package/services/metadata/core-properties/x.psmdcp",
        };
        AlCompilerProvisioner.PickTfm(entries).Should().Be("net10.0");
    }

    [Fact]
    public void PickTfm_falls_back_to_highest_net_when_no_net10()
    {
        // Numeric ordering — net8.0 beats net6.0, and beats a lexical trap.
        var entries = new[] { "lib/net6.0/alc", "lib/net8.0/alc" };
        AlCompilerProvisioner.PickTfm(entries).Should().Be("net8.0");
    }

    [Fact]
    public void PickTfm_returns_null_when_no_compiler_in_either_layout()
    {
        // tools/<tfm>/alc is neither layout: the main package nests one more folder ("any").
        AlCompilerProvisioner.PickTfm(new[] { "tools/net8.0/alc", "README.md" }).Should().BeNull();
    }

    // ── The main package's layout (#921) ────────────────────────────────

    [Fact]
    public void PickLayout_takes_the_framework_dependent_compiler_from_the_main_package()
    {
        // Microsoft.Dynamics.BusinessCentral.Development.Tools 18.0.41.62505, as published.
        var entries = new[]
        {
            "tools/net8.0/any/alc.dll", "tools/net8.0/any/alc.runtimeconfig.json",
            "tools/net10.0/any/alc.dll", "tools/net10.0/any/alc.exe", "tools/net10.0/any/altool.dll",
            "templates/README.md",
        };
        AlCompilerProvisioner.PickLayout(entries).Should().Be(new CompilerLayout("tools/net10.0/any/", "net10.0", "alc.dll"));
    }

    [Fact]
    public void PickLayout_prefers_the_framework_dependent_compiler_over_an_apphost_in_the_same_package()
    {
        var entries = new[] { "lib/net10.0/alc", "tools/net10.0/any/alc.dll" };
        AlCompilerProvisioner.PickLayout(entries)!.Entry.Should().Be("alc.dll");
    }

    [Fact]
    public void PickLayout_returns_null_for_an_analyzers_only_package()
    {
        // The .Linux package from 18.x: cops and their deps.json, no compiler.
        var entries = new[]
        {
            "lib/net10.0/Microsoft.Dynamics.Nav.CodeCop.dll", "lib/net10.0/Microsoft.Dynamics.Nav.AppSourceCop.dll",
            "lib/net8.0/Microsoft.Dynamics.Nav.UICop.dll",
        };
        AlCompilerProvisioner.PickLayout(entries).Should().BeNull(
            "an install must not succeed on a package that holds no alc, which is how #921 broke fresh volumes");
    }

    [Fact]
    public void PickCandidates_walks_the_newest_stable_few_so_a_compilerless_package_can_be_skipped()
    {
        var versions = new[] { "16.2.28.57946", "17.0.34.45391", "18.0.41.39415", "18.0.41.62505", "30.0.42.11883-beta" };
        AlCompilerProvisioner.PickCandidates(versions, pin: null)
            .Should().Equal(["18.0.41.62505", "18.0.41.39415", "17.0.34.45391"], "the beta is skipped and the stable ones walk newest first");
        AlCompilerProvisioner.PickCandidates(versions, pin: "30.0.42.11883-beta").Should().Equal("30.0.42.11883-beta");
        AlCompilerProvisioner.PickCandidates(versions, pin: "99.0.0.0").Should().BeEmpty();
    }

    [Fact]
    public void A_framework_dependent_compiler_runs_through_dotnet_and_an_apphost_runs_itself()
    {
        var dll = new AlCompilerInfo("/var/lib/aldevtoolbox/altool/bin/alc.dll", NeedsRollForward: false, "18.0.41.62505");
        dll.FileName.Should().Be("dotnet");
        dll.LeadingArguments.Should().Equal("/var/lib/aldevtoolbox/altool/bin/alc.dll");

        var apphost = new AlCompilerInfo("/var/lib/aldevtoolbox/altool/bin/alc", NeedsRollForward: false, "17.0.34.45391");
        apphost.FileName.Should().Be(apphost.AlcPath);
        apphost.LeadingArguments.Should().BeEmpty();
    }
}
