using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the vendored CodeMirror bundles under <c>wwwroot/lib/codemirror/</c>.
///
/// The editor used to import ten modules from a CDN at runtime, which took every
/// editor surface (the diff viewer, the source viewer, the admin TOML/JSON
/// editors) offline on a host without outbound HTTPS. These tests keep it
/// vendored: no remote import may creep back in, every relative import must
/// resolve to a file that ships, and the shared <c>@codemirror/state</c> must
/// stay a single copy — a second one breaks CodeMirror's extension system with
/// "Unrecognized extension value in extension set".
/// </summary>
public sealed class VendoredCodeMirrorTests
{
    [Fact]
    public void Code_editor_imports_nothing_over_the_network()
    {
        var js = File.ReadAllText(Path.Combine(FindWwwroot(), "code-editor.js"));

        var remote = Regex.Matches(js, @"from\s*[""'](?<url>https?://[^""']+)[""']")
            .Select(m => m.Groups["url"].Value)
            .ToList();

        remote.Should().BeEmpty(
            "code-editor.js must import only vendored files under wwwroot/lib/codemirror/. Found: {0}",
            string.Join(", ", remote));
    }

    [Fact]
    public void Every_import_in_the_editor_and_its_bundles_resolves_to_a_vendored_file()
    {
        var wwwroot = FindWwwroot();
        var files = new List<string> { Path.Combine(wwwroot, "code-editor.js") };
        files.AddRange(Directory.GetFiles(Path.Combine(wwwroot, "lib", "codemirror"), "*.js"));

        var unresolved = new List<string>();
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(source, @"from\s*[""'](?<spec>[^""']+)[""']"))
            {
                var spec = m.Groups["spec"].Value;
                if (!spec.StartsWith('.'))
                {
                    // A bare specifier ("@codemirror/state") has no meaning in the
                    // browser without an import map — it means a rewrite was missed.
                    unresolved.Add($"{Path.GetFileName(file)} -> {spec}");
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(file)!, spec.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(target))
                {
                    unresolved.Add($"{Path.GetFileName(file)} -> {spec}");
                }
            }
        }

        unresolved.Should().BeEmpty(
            "every import must resolve to a vendored file. Unresolved: {0}",
            string.Join(", ", unresolved));
    }

    [Fact]
    public void Only_one_copy_of_codemirror_state_is_vendored()
    {
        var dir = Path.Combine(FindWwwroot(), "lib", "codemirror");

        // The message is emitted from @codemirror/state's extension flattener and
        // survives minification, so it is a reliable fingerprint for the package.
        var copies = Directory.GetFiles(dir, "*.js")
            .Where(f => File.ReadAllText(f).Contains("Unrecognized extension value", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        copies.Should().BeEquivalentTo(["state.js"],
            "a duplicated @codemirror/state breaks CodeMirror's instanceof checks at runtime");
    }

    [Fact]
    public void Vendored_bundles_record_how_they_were_produced()
    {
        var readme = Path.Combine(FindWwwroot(), "lib", "codemirror", "README.md");

        File.Exists(readme).Should().BeTrue(
            "the pinned versions and download commands must be reproducible from {0}", readme);
        File.ReadAllText(readme).Should().Contain("@codemirror/state@6.4.1");
    }

    /// <summary>
    /// Walks up from the test binary to the repo root marker, then down into the
    /// app's wwwroot. Mirrors <c>FontAssetTests.FindWwwroot</c>.
    /// </summary>
    private static string FindWwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("could not locate repo root (looking for ALDevToolbox.slnx)");
        var wwwroot = Path.Combine(dir!.FullName, "ALDevToolbox", "wwwroot");
        Directory.Exists(wwwroot).Should().BeTrue("expected wwwroot folder at {0}", wwwroot);
        return wwwroot;
    }
}
