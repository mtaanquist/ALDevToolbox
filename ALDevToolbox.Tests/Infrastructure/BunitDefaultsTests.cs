using AwesomeAssertions;
using Bunit;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Pins that <see cref="BunitDefaults"/> actually took effect.
///
/// <para>
/// It applies through a <c>[ModuleInitializer]</c>, which is a runtime
/// convention rather than something the compiler checks at the call site. If
/// it ever stops running, every bUnit wait silently reverts to the 1-second
/// default and the component tests start failing under CI load with
/// "render count 1" - a symptom that looks like a product bug and is not one.
/// That is a bad failure to debug twice, so assert the value directly.
/// </para>
/// </summary>
public sealed class BunitDefaultsTests
{
    [Fact]
    public void The_wait_timeout_override_is_applied()
    {
        BunitContext.DefaultWaitTimeout.Should().Be(TimeSpan.FromSeconds(30),
            "BunitDefaults raises it from bUnit's 1s default; component tests "
            + "that render from Postgres need the headroom on a busy runner");
    }
}
