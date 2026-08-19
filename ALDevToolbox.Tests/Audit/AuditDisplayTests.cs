using System.Text.RegularExpressions;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using FluentAssertions;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// The audit log's entity labels are read by admins on three surfaces - both
/// audit pages and the /admin dashboard's activity panel - and they are
/// produced by a switch with a <c>ToString()</c> fallback. That fallback is the
/// hazard: a new enum member reaches the reader as its own identifier, which is
/// how "ApplicationVersion" and "PersonalAccessToken" were on screen for
/// months. CLAUDE.md's jargon test forbids exactly that.
/// </summary>
public sealed class AuditDisplayTests
{
    [Fact]
    public void No_audit_entity_label_reaches_the_reader_as_a_camel_case_identifier()
    {
        var glued = new Regex("[a-z][A-Z]");

        var offenders = Enum.GetValues<AuditEntityType>()
            .Select(t => (Type: t, Label: AdminPageHelpers.FriendlyAuditType(t)))
            .Where(x => glued.IsMatch(x.Label))
            .Select(x => $"{x.Type} -> \"{x.Label}\"")
            .ToList();

        offenders.Should().BeEmpty(
            "every audited type needs a label written for the admin reading it; add a case "
            + "to FriendlyAuditType rather than letting the enum name fall through");
    }

    [Fact]
    public void Every_audit_entity_label_is_non_empty_and_starts_with_a_capital()
    {
        foreach (var type in Enum.GetValues<AuditEntityType>())
        {
            var label = AdminPageHelpers.FriendlyAuditType(type);
            label.Should().NotBeNullOrWhiteSpace();
            char.IsUpper(label[0]).Should().BeTrue($"'{label}' opens a sentence in an audit row");
        }
    }
}
