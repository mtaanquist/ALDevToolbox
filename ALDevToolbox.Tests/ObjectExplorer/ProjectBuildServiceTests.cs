using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The pure, IO-free decision logic of <see cref="ProjectBuildService"/> — the
/// parts that decide <em>what</em> to build and <em>in what order</em> before any
/// process is spawned: app.json parsing, project discovery (with test/.alpackages
/// pruning), the dependencies-first compile order, target-version selection, the
/// country fallback chain, and the git auth header. The clone/compile/ingest IO is
/// exercised by the worker integration test (Stage D), not here.
/// </summary>
public sealed class ProjectBuildServiceTests
{
    // ── ParseManifest ───────────────────────────────────────────────────

    [Fact]
    public void ParseManifest_reads_identity_versions_and_dependencies()
    {
        const string json = """
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "name": "Core",
          "publisher": "Contoso",
          "version": "1.2.3.4",
          "application": "26.0.0.0",
          "platform": "26.0.0.0",
          "dependencies": [
            { "id": "22222222-2222-2222-2222-222222222222", "name": "Base", "publisher": "X", "version": "1.0.0.0" }
          ]
        }
        """;

        var manifest = ProjectBuildService.ParseManifest(json);

        manifest.Should().NotBeNull();
        manifest!.Id.Should().Be("11111111-1111-1111-1111-111111111111");
        manifest.Name.Should().Be("Core");
        manifest.Application.Should().Be("26.0.0.0");
        manifest.Dependencies.Should().ContainSingle()
            .Which.Id.Should().Be("22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public void ParseManifest_accepts_legacy_appId_dependency_key()
    {
        // Older app.json spelled the dependency id "appId".
        const string json = """
        {
          "id": "aaa", "name": "Ext", "publisher": "X", "version": "1.0.0.0",
          "dependencies": [ { "appId": "bbb", "name": "Dep", "publisher": "Y", "version": "1.0.0.0" } ]
        }
        """;

        var manifest = ProjectBuildService.ParseManifest(json);

        manifest!.Dependencies.Should().ContainSingle().Which.Id.Should().Be("bbb");
    }

    [Fact]
    public void ParseManifest_returns_null_for_invalid_json()
    {
        ProjectBuildService.ParseManifest("not json at all {").Should().BeNull();
    }

    // ── DiscoverAppProjectDirs ──────────────────────────────────────────

    [Fact]
    public void DiscoverAppProjectDirs_finds_apps_and_prunes_excluded_and_test_folders()
    {
        using var temp = new TempDir();
        // A real app, a nested app, plus folders that must NOT be discovered.
        WriteAppJson(temp.Path, "Core");
        WriteAppJson(Path.Combine(temp.Path, "SubApp"), "Sub");
        WriteAppJson(Path.Combine(temp.Path, ".alpackages"), "ShouldSkip");
        WriteAppJson(Path.Combine(temp.Path, "Acme Tests"), "ShouldSkip");
        WriteAppJson(Path.Combine(temp.Path, ".git", "hooks"), "ShouldSkip");

        var dirs = ProjectBuildService.DiscoverAppProjectDirs(temp.Path);

        dirs.Select(Path.GetFileName).Should().BeEquivalentTo(new[]
        {
            Path.GetFileName(temp.Path), "SubApp",
        });
    }

    [Theory]
    [InlineData("Test", true)]
    [InlineData("Tests", true)]
    [InlineData("My App Test Library", true)]
    [InlineData("Acme Tests", true)]
    [InlineData("Core", false)]
    [InlineData("MyTestApp", false)]
    public void IsTestSegment_matches_folderzipwalker_rules(string segment, bool expected)
    {
        ProjectBuildService.IsTestSegment(segment).Should().Be(expected);
    }

    // ── SelectTargetMajorMinor ──────────────────────────────────────────

    [Fact]
    public void SelectTargetMajorMinor_picks_the_highest_application_version()
    {
        var manifests = new[]
        {
            Manifest(application: "24.0.0.0"),
            Manifest(application: "26.1.0.0"),
            Manifest(application: "25.0.0.0"),
        };

        ProjectBuildService.SelectTargetMajorMinor(manifests).Should().Be("26.1");
    }

    [Fact]
    public void SelectTargetMajorMinor_falls_back_to_platform_then_null()
    {
        ProjectBuildService.SelectTargetMajorMinor(new[] { Manifest(application: null, platform: "23.0.0.0") })
            .Should().Be("23.0");
        ProjectBuildService.SelectTargetMajorMinor(new[] { Manifest(application: null, platform: null) })
            .Should().BeNull();
    }

    // ── TopologicalOrder ────────────────────────────────────────────────

    [Fact]
    public void TopologicalOrder_places_dependencies_before_dependents()
    {
        // App "B" depends on "A"; "A" must come first regardless of input order.
        var a = App("A", "id-a");
        var b = App("B", "id-b", dependsOn: "id-a");
        var c = App("C", "id-c", dependsOn: "id-b");

        var ordered = ProjectBuildService.TopologicalOrder(new[] { c, b, a });

        ordered.Select(d => d.Manifest.Name).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void TopologicalOrder_is_cycle_safe_and_keeps_all_apps()
    {
        // A ↔ B mutual dependency must not loop forever or drop an app.
        var a = App("A", "id-a", dependsOn: "id-b");
        var b = App("B", "id-b", dependsOn: "id-a");

        var ordered = ProjectBuildService.TopologicalOrder(new[] { a, b });

        ordered.Select(d => d.Manifest.Name).Should().BeEquivalentTo(new[] { "A", "B" });
    }

    [Fact]
    public void TopologicalOrder_ignores_external_dependencies()
    {
        // A dependency id that isn't one of the built apps (a Microsoft/third-party
        // symbol) is simply not an ordering constraint.
        var a = App("A", "id-a", dependsOn: "microsoft-base-app");

        var ordered = ProjectBuildService.TopologicalOrder(new[] { a });

        ordered.Should().ContainSingle().Which.Manifest.Name.Should().Be("A");
    }

    // ── ResolveCountry ──────────────────────────────────────────────────

    [Theory]
    [InlineData("dk", "US", "dk")]   // per-project wins
    [InlineData(null, "US", "us")]   // org default, lower-cased
    [InlineData(null, null, "w1")]   // final fallback
    [InlineData("  ", "  ", "w1")]   // blank-safe
    public void ResolveCountry_follows_project_then_org_then_w1(string? project, string? org, string expected)
    {
        ProjectBuildService.ResolveCountry(project, org).Should().Be(expected);
    }

    // ── BasicAuthHeaderValue ────────────────────────────────────────────

    [Fact]
    public void BasicAuthHeaderValue_uses_empty_username_for_azure_devops()
    {
        var header = ProjectBuildService.BasicAuthHeaderValue(RepositoryProvider.AzureDevOps, "tok");

        header.Should().StartWith("Authorization: Basic ");
        Decode(header).Should().Be(":tok");
    }

    [Fact]
    public void BasicAuthHeaderValue_uses_x_access_token_username_for_github()
    {
        var header = ProjectBuildService.BasicAuthHeaderValue(RepositoryProvider.GitHub, "tok");

        Decode(header).Should().Be("x-access-token:tok");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static string Decode(string header)
    {
        var b64 = header["Authorization: Basic ".Length..];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    private static AppJsonManifest Manifest(string? application, string? platform = null) =>
        new("id", "Name", "Pub", "1.0.0.0", application, platform, Array.Empty<AppJsonDependency>());

    private static DiscoveredApp App(string name, string id, string? dependsOn = null)
    {
        var deps = dependsOn is null
            ? Array.Empty<AppJsonDependency>()
            : new[] { new AppJsonDependency(dependsOn, "Dep") };
        return new DiscoveredApp(
            $"/tmp/{name}",
            new AppJsonManifest(id, name, "Pub", "1.0.0.0", "26.0.0.0", null, deps),
            new ClonedRepo($"/tmp/{name}", "https://example.test/repo", null, null));
    }

    private static void WriteAppJson(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"),
            $$"""{ "id": "{{name}}", "name": "{{name}}", "publisher": "X", "version": "1.0.0.0" }""");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oe-build-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
