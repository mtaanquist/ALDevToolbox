using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

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
    public void PickNewest_returns_last_entry_when_no_pin()
    {
        // The NuGet flat-container index is SemVer-ascending, so newest is last.
        var versions = new[] { "16.2.28.57946", "17.0.27.27275-beta", "18.0.37.11445-beta" };
        AlCompilerProvisioner.PickNewest(versions, pin: null).Should().Be("18.0.37.11445-beta");
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
    public void PickTfm_returns_null_when_no_lib_framework_folder()
    {
        AlCompilerProvisioner.PickTfm(new[] { "tools/net8.0/alc", "README.md" }).Should().BeNull();
    }
}
