using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The settings tabs each POST only their own fields, so the builder's job is
/// to overlay those onto everything already stored. The checkbox reading is the
/// part with a rule worth pinning: a switch posts a hidden <c>false</c> next to
/// the checkbox so "off" and "not on this form" stay different answers.
/// </summary>
public class SettingsInputBuilderTests
{
    private static SystemSettingsView Current(bool? startTls = true) => new(
        SmtpHost: "smtp.cronus.com",
        SmtpPort: 587,
        SmtpUser: "toolbox",
        HasSmtpPassword: true,
        SmtpFrom: "noreply@cronus.com",
        SmtpFromName: "AL Dev Toolbox",
        SmtpUseStartTls: startTls,
        BannerText: "Scheduled maintenance on Sunday.",
        BackupScheduleEnabled: true,
        BackupScheduleTimeUtc: new TimeOnly(2, 0),
        BackupRetentionCount: 30,
        PerTenantBackupRetentionCount: 7,
        DefaultStorageQuotaMb: 10_240,
        IndexSizeMultiplier: 1.5m,
        McpEnabled: true,
        SignupEmailDomainAllowlist: "cronus.com",
        ReleaseDownloadDomainAllowlist: "download.microsoft.com",
        DisabledTools: Array.Empty<string>(),
        UpdatedAt: new DateTime(2026, 8, 16, 9, 42, 0, DateTimeKind.Utc));

    private static IFormCollection Form(params (string Key, string[] Values)[] fields) =>
        new FormCollection(fields.ToDictionary(f => f.Key, f => new StringValues(f.Values)));

    [Fact]
    public void A_switch_that_is_on_posts_both_values_and_still_reads_as_on()
    {
        // What Switch.razor renders when checked: the paired hidden false, then
        // the checkbox's own true. StringValues joins these as "false,true",
        // which an equality check against the whole string reads as neither.
        var form = Form(("SmtpUseStartTls", ["false", "true"]));

        var input = SettingsInputBuilder.WithSmtp(Current(), form);

        input.SmtpUseStartTls.Should().BeTrue();
    }

    [Fact]
    public void A_switch_that_is_off_posts_false_and_turns_the_setting_off()
    {
        // The bug this pins: an unticked box used to vanish from the form
        // entirely, save null, and resolve back to true through `?? true` -- so
        // STARTTLS could not be switched off from the UI at all.
        var form = Form(("SmtpUseStartTls", ["false"]));

        var input = SettingsInputBuilder.WithSmtp(Current(startTls: true), form);

        input.SmtpUseStartTls.Should().BeFalse();
        ResolvedSmtpSettings.TryFrom(
                host: "smtp.cronus.com", port: 587, user: "toolbox", password: "pw",
                from: "noreply@cronus.com", fromName: null,
                useStartTls: input.SmtpUseStartTls)!
            .UseStartTls.Should().BeFalse();
    }

    [Fact]
    public void A_tab_that_does_not_post_the_field_leaves_the_stored_value_alone()
    {
        // The General tab saving must not disturb SMTP.
        var form = Form(("BannerText", ["Back on Monday."]));

        var input = SettingsInputBuilder.WithGeneral(Current(startTls: false), form);

        input.SmtpUseStartTls.Should().BeFalse();
        input.SmtpHost.Should().Be("smtp.cronus.com");
        input.BannerText.Should().Be("Back on Monday.");
    }

    [Fact]
    public void The_tools_tab_disables_every_tool_whose_box_is_absent()
    {
        var form = Form(($"tool_{ToolKey.Mcp}", ["true"]));

        var input = SettingsInputBuilder.WithTools(Current(), form);

        input.McpEnabled.Should().BeTrue();
        input.DisabledTools.Should().NotContain(ToolKey.Mcp);
        input.DisabledTools.Should().Contain(
            ToolCatalog.All.Where(t => t.Key != ToolKey.Mcp).Select(t => t.Key));
    }
}
