using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.Al;

/// <summary>
/// Snapshot harness for <see cref="AlReferenceExtractor"/>.
///
/// Each fixture lives at <c>Al/Fixtures/&lt;owner-kind&gt;/&lt;Name&gt;.al</c>
/// with an optional <c>&lt;Name&gt;.context.json</c> sibling for extra
/// <see cref="AlExtractContext"/> fields (e.g. <c>OwnerSourceTableName</c>).
/// The test runs the extractor and compares the ordered list of
/// <see cref="ExtractedReference"/> rows — plus the unresolved sample stream —
/// against <c>&lt;Name&gt;.snapshot.json</c> committed alongside the fixture.
///
/// Drift between commits is caught here at the unit level so a regression
/// can be attributed to the commit that caused it, rather than discovered
/// later by a full BC DVD re-import.
///
/// First-run / new-fixture flow: if <c>.snapshot.json</c> is missing the
/// test writes it (so the author re-runs once, eyeballs the JSON diff and
/// commits the baseline) and fails with a clear message. Snapshot files are
/// copied to the test output directory but also written back to the source
/// tree via the resolved repo path, so the round-trip works against the
/// canonical location.
/// </summary>
public sealed class AlReferenceExtractorSnapshotTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        var fixturesDir = ResolveFixturesDirectory();
        if (!Directory.Exists(fixturesDir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(fixturesDir, "*.al", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            // Test ID is the path relative to Al/Fixtures so xUnit reports
            // "page/CustomerCardSample" rather than the absolute path that
            // varies per machine.
            var relative = Path.GetRelativePath(fixturesDir, file).Replace('\\', '/');
            yield return new object[] { relative };
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_extraction_matches_snapshot(string relativeFixturePath)
    {
        var fixturesDir = ResolveFixturesDirectory();
        var fixturePath = Path.Combine(fixturesDir, relativeFixturePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(fixturePath);

        var context = BuildContext(fixturePath);
        var result = AlReferenceExtractor.Extract(source, context);
        var actualJson = SerialiseSnapshot(result);

        var snapshotPath = Path.ChangeExtension(fixturePath, ".snapshot.json");
        if (!File.Exists(snapshotPath))
        {
            // Write the baseline into the source tree (resolved fixtures
            // directory) so the author can review + commit it on the next
            // git status. Fail loudly so a missing snapshot can't slip in
            // as a silently passing test.
            File.WriteAllText(snapshotPath, actualJson);
            Assert.Fail(
                $"Snapshot did not exist for fixture '{relativeFixturePath}'. "
                + $"A baseline has been written to {snapshotPath}. "
                + "Review the contents, then commit the snapshot file and re-run.");
        }

        var expectedJson = File.ReadAllText(snapshotPath);
        actualJson.Should().Be(
            expectedJson,
            $"snapshot {Path.GetFileName(snapshotPath)} should match the extractor's current output");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static AlExtractContext BuildContext(string fixturePath)
    {
        var ownerKind = NormaliseOwnerKind(
            Path.GetFileName(Path.GetDirectoryName(fixturePath)!));
        var ownerName = Path.GetFileNameWithoutExtension(fixturePath);

        var contextPath = Path.ChangeExtension(fixturePath, ".context.json");
        var extras = File.Exists(contextPath)
            ? JsonSerializer.Deserialize<FixtureContextOverrides>(File.ReadAllText(contextPath), JsonOptions)
              ?? new FixtureContextOverrides()
            : new FixtureContextOverrides();

        var globals = new Dictionary<string, ResolvedVariableType>(StringComparer.OrdinalIgnoreCase);
        if (extras.Globals is not null)
        {
            foreach (var g in extras.Globals)
            {
                globals[g.Name] = new ResolvedVariableType(g.TypeKeyword, g.TypeName);
            }
        }

        return new AlExtractContext(
            OwnerKind: ownerKind,
            OwnerName: extras.OwnerName ?? ownerName,
            OwnerObjectId: extras.OwnerObjectId,
            OwnerAppId: SnapshotCatalog.OwnerAppId,
            GlobalVars: globals,
            Resolver: SnapshotCatalog.Resolver,
            OwnerSourceTableName: extras.OwnerSourceTableName);
    }

    private static string NormaliseOwnerKind(string folderName) => folderName.ToLowerInvariant();

    private static string SerialiseSnapshot(AlExtractionResult result)
    {
        // Project to a stable shape (ordered + minimal) so noise like field
        // re-ordering doesn't churn snapshots. UnresolvedSample is included
        // because the Reason= stream is part of the contract per the plan's
        // telemetry-continuity checklist.
        var snapshot = new SnapshotDocument(
            References: result.References
                .Select(r => new SnapshotReference(
                    r.Line,
                    r.Column,
                    r.ReferenceKind,
                    r.TargetObjectKind,
                    r.TargetObjectId,
                    r.TargetObjectName,
                    r.TargetMemberKind,
                    r.TargetMemberName))
                .ToList(),
            Stats: new SnapshotStats(
                result.Stats.ResolvedReferences,
                result.Stats.UnresolvedReceivers,
                result.Stats.UnresolvedSamples
                    .Select(s => new SnapshotUnresolved(
                        s.Reason,
                        s.Token,
                        s.Line,
                        s.Column,
                        s.ReceiverKind,
                        s.ReceiverName))
                    .ToList()));

        return JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Walks up from the test assembly's output directory to the
    /// <c>Al/Fixtures</c> folder in the source tree. The .csproj also
    /// copies the fixtures into the output directory, but the source path
    /// is the canonical location for snapshot writes — copying back into
    /// <c>bin/</c> would lose the baseline on the next clean build.
    /// </summary>
    private static string ResolveFixturesDirectory()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(AlReferenceExtractorSnapshotTests).Assembly.Location)!);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Al", "Fixtures");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.Tests.csproj")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        // Fallback: the bin-copied location. Snapshot writes here would be
        // lost on a clean build, but read-only assertions still work.
        return Path.Combine(
            Path.GetDirectoryName(typeof(AlReferenceExtractorSnapshotTests).Assembly.Location)!,
            "Al", "Fixtures");
    }

    // ── Snapshot DTOs ───────────────────────────────────────────────

    private sealed record SnapshotDocument(
        IReadOnlyList<SnapshotReference> References,
        SnapshotStats Stats);

    private sealed record SnapshotReference(
        int Line,
        int Column,
        string ReferenceKind,
        string TargetObjectKind,
        int? TargetObjectId,
        string TargetObjectName,
        string? TargetMemberKind,
        string? TargetMemberName);

    private sealed record SnapshotStats(
        int ResolvedReferences,
        int UnresolvedReceivers,
        IReadOnlyList<SnapshotUnresolved> UnresolvedSamples);

    private sealed record SnapshotUnresolved(
        string Reason,
        string Token,
        int Line,
        int Column,
        string? ReceiverKind,
        string? ReceiverName);

    private sealed record FixtureContextOverrides
    {
        public string? OwnerName { get; init; }
        public int? OwnerObjectId { get; init; }
        public string? OwnerSourceTableName { get; init; }
        public List<FixtureGlobal>? Globals { get; init; }
    }

    private sealed record FixtureGlobal
    {
        public string Name { get; init; } = string.Empty;
        public string TypeName { get; init; } = string.Empty;
        public string? TypeKeyword { get; init; }
    }
}
