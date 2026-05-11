using ALDevToolbox.Domain.ValueObjects;
using FluentAssertions;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// Covers <see cref="AuditDiff.Compute"/>: the diff contract that backs
/// <c>AuditDiffViewer</c>. Each test pins one shape — added, removed, changed,
/// nested object, array reorder, and the redaction-placeholder surface — so
/// the milestone P4.20 acceptance criteria stay verifiable without spinning
/// up a Blazor host.
/// </summary>
public sealed class AuditDiffTests
{
    [Fact]
    public void Compute_returns_empty_when_snapshots_are_identical()
    {
        const string snapshot = """{"Name":"acme","Deprecated":false}""";
        var diff = AuditDiff.Compute(snapshot, snapshot);
        diff.Should().BeEmpty();
    }

    [Fact]
    public void Compute_reports_added_field_when_only_after_has_it()
    {
        var diff = AuditDiff.Compute(
            beforeJson: """{"Name":"acme"}""",
            afterJson: """{"Name":"acme","Notes":"new line"}""");
        diff.Should().ContainSingle(e =>
            e.Path == "Notes"
            && e.Kind == AuditDiffKind.Added
            && e.BeforeDisplay == null
            && e.AfterDisplay == "new line");
    }

    [Fact]
    public void Compute_reports_removed_field_when_only_before_has_it()
    {
        var diff = AuditDiff.Compute(
            beforeJson: """{"Name":"acme","Notes":"old"}""",
            afterJson: """{"Name":"acme"}""");
        diff.Should().ContainSingle(e =>
            e.Path == "Notes"
            && e.Kind == AuditDiffKind.Removed
            && e.BeforeDisplay == "old"
            && e.AfterDisplay == null);
    }

    [Fact]
    public void Compute_reports_changed_field_when_values_differ()
    {
        var diff = AuditDiff.Compute(
            beforeJson: """{"Name":"acme"}""",
            afterJson: """{"Name":"acme-2"}""");
        diff.Should().ContainSingle(e =>
            e.Path == "Name"
            && e.Kind == AuditDiffKind.Changed
            && e.BeforeDisplay == "acme"
            && e.AfterDisplay == "acme-2");
    }

    [Fact]
    public void Compute_recurses_into_nested_objects()
    {
        var diff = AuditDiff.Compute(
            beforeJson: """{"Defaults":{"Publisher":"Acme","Target":"Cloud"}}""",
            afterJson: """{"Defaults":{"Publisher":"Acme","Target":"OnPrem"}}""");
        diff.Should().ContainSingle();
        var entry = diff.Single();
        entry.Path.Should().Be("Defaults.Target");
        entry.Kind.Should().Be(AuditDiffKind.Changed);
        entry.BeforeDisplay.Should().Be("Cloud");
        entry.AfterDisplay.Should().Be("OnPrem");
    }

    [Fact]
    public void Compute_indexes_array_changes_by_position()
    {
        var diff = AuditDiff.Compute(
            beforeJson: """{"Folders":[{"Path":"src"},{"Path":"test"}]}""",
            afterJson: """{"Folders":[{"Path":"src"},{"Path":"tests"}]}""");
        diff.Should().ContainSingle();
        var entry = diff.Single();
        entry.Path.Should().Be("Folders[1].Path");
        entry.Kind.Should().Be(AuditDiffKind.Changed);
        entry.BeforeDisplay.Should().Be("test");
        entry.AfterDisplay.Should().Be("tests");
    }

    [Fact]
    public void Compute_reports_array_growth_as_added_indices()
    {
        var diff = AuditDiff.Compute(
            beforeJson: """{"Folders":[{"Path":"src"}]}""",
            afterJson: """{"Folders":[{"Path":"src"},{"Path":"docs"}]}""");
        diff.Should().ContainSingle(e =>
            e.Path == "Folders[1].Path"
            && e.Kind == AuditDiffKind.Added
            && e.AfterDisplay == "docs");
    }

    [Fact]
    public void Compute_flags_redacted_sentinel_string()
    {
        var diff = AuditDiff.Compute(
            beforeJson: """{"SmtpPasswordEncrypted":"[redacted]"}""",
            afterJson: """{"SmtpPasswordEncrypted":null}""");
        diff.Should().ContainSingle(e =>
            e.Path == "SmtpPasswordEncrypted"
            && e.Kind == AuditDiffKind.Changed
            && e.BeforeRedacted
            && !e.AfterRedacted
            && e.AfterDisplay == null);
    }

    [Fact]
    public void Compute_flags_content_sha256_hash_as_redacted()
    {
        // File content is hashed for the audit snapshot; the diff viewer
        // renders <redacted> rather than the hash itself per milestone P4.20.
        var diff = AuditDiff.Compute(
            beforeJson: """{"ContentSha256":"a1b2c3"}""",
            afterJson: """{"ContentSha256":"d4e5f6"}""");
        diff.Should().ContainSingle(e =>
            e.Path == "ContentSha256"
            && e.Kind == AuditDiffKind.Changed
            && e.BeforeRedacted
            && e.AfterRedacted);
    }

    [Fact]
    public void Compute_treats_null_before_as_creation()
    {
        var diff = AuditDiff.Compute(
            beforeJson: null,
            afterJson: """{"Name":"new","Count":3}""");
        diff.Should().HaveCount(2);
        diff.Should().OnlyContain(e => e.Kind == AuditDiffKind.Added);
        diff.Should().Contain(e => e.Path == "Name" && e.AfterDisplay == "new");
        diff.Should().Contain(e => e.Path == "Count" && e.AfterDisplay == "3");
    }

    [Fact]
    public void Compute_treats_null_after_as_deletion()
    {
        var diff = AuditDiff.Compute(
            beforeJson: """{"Name":"gone"}""",
            afterJson: null);
        diff.Should().ContainSingle(e =>
            e.Path == "Name"
            && e.Kind == AuditDiffKind.Removed
            && e.BeforeDisplay == "gone");
    }

    [Fact]
    public void FormatCell_renders_redacted_for_redacted_values()
    {
        AuditDiff.FormatCell("hash-bytes", redacted: true, absent: false).Should().Be("<redacted>");
        AuditDiff.FormatCell(null, redacted: false, absent: true).Should().Be("<empty>");
        AuditDiff.FormatCell("acme", redacted: false, absent: false).Should().Be("acme");
        AuditDiff.FormatCell(null, redacted: false, absent: false).Should().Be("<null>");
    }
}
