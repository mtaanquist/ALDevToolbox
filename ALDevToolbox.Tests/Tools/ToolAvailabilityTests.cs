using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Services.Tools;
using FluentAssertions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// Pure-logic cover for the site-wide tool toggles and the catalogue helpers.
/// No DB: <see cref="ToolAvailabilityState"/> is an in-memory singleton and
/// <see cref="ToolCatalog"/> is static data.
/// </summary>
public sealed class ToolAvailabilityTests
{
    private sealed class FakeMcp : IMcpAvailability
    {
        public bool Enabled { get; set; }
        public bool IsEnabled => Enabled;
    }

    [Fact]
    public void Every_tool_enabled_by_default()
    {
        var state = new ToolAvailabilityState(new FakeMcp { Enabled = true });

        foreach (var tool in ToolCatalog.All)
        {
            state.IsSiteEnabled(tool.Key).Should().BeTrue($"{tool.Key} should be on until disabled");
        }
    }

    [Fact]
    public void Set_disables_only_the_named_tools()
    {
        var state = new ToolAvailabilityState(new FakeMcp { Enabled = true });

        state.Set(new[] { ToolKey.Projects, ToolKey.Translator });

        state.IsSiteEnabled(ToolKey.Projects).Should().BeFalse();
        state.IsSiteEnabled(ToolKey.Translator).Should().BeFalse();
        state.IsSiteEnabled(ToolKey.Piper).Should().BeTrue();
    }

    [Fact]
    public void Mcp_delegates_to_mcp_availability_and_ignores_the_disabled_set()
    {
        var mcp = new FakeMcp { Enabled = false };
        var state = new ToolAvailabilityState(mcp);

        // Even if Mcp is wrongly handed to Set, the McpAvailability flag wins.
        state.Set(new[] { ToolKey.Mcp });
        state.IsSiteEnabled(ToolKey.Mcp).Should().BeFalse("MCP follows its own site flag");

        mcp.Enabled = true;
        state.IsSiteEnabled(ToolKey.Mcp).Should().BeTrue();
    }

    [Fact]
    public void ParseDisabled_round_trips_format_and_drops_unknown_names()
    {
        var keys = new[] { ToolKey.Cookbook, ToolKey.ObjectExplorer };

        var names = ToolCatalog.Format(keys);
        var parsed = ToolCatalog.ParseDisabled(names.Append("NotARealTool"));

        parsed.Should().BeEquivalentTo(keys, "unknown names are silently ignored");
    }

    [Fact]
    public void ParseDisabled_handles_null()
    {
        ToolCatalog.ParseDisabled(null).Should().BeEmpty();
    }
}
