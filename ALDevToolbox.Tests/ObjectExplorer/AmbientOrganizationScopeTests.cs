using System.Security.Claims;
using ALDevToolbox.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The release-import background worker has no <c>HttpContext</c>, so it relies
/// on <see cref="AmbientOrganizationScope"/> feeding the submitting user's org
/// into <see cref="HttpOrganizationContext"/>. These guard that fallback —
/// and, critically, that a real request's claims always win over any ambient
/// value so the mechanism can't be used to widen a request's reach.
/// </summary>
public sealed class AmbientOrganizationScopeTests
{
    [Fact]
    public void Falls_back_to_ambient_when_no_http_context()
    {
        var ctx = new HttpOrganizationContext(new HttpContextAccessor());

        ctx.CurrentOrganizationId.Should().BeNull("nothing is in scope yet");

        using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(
            OrganizationId: 42, UserId: 7, IsSiteAdmin: true, IsSystemOrganization: false)))
        {
            ctx.CurrentOrganizationId.Should().Be(42);
            ctx.CurrentUserId.Should().Be(7);
            ctx.IsSiteAdmin.Should().BeTrue();
            ctx.OrganizationIdForFilter.Should().Be(42);
        }

        ctx.CurrentOrganizationId.Should().BeNull("the scope was disposed");
    }

    [Fact]
    public void Http_claims_win_over_ambient()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(HttpOrganizationContext.OrganizationIdClaim, "9"),
                }, authenticationType: "test")),
            },
        };
        var ctx = new HttpOrganizationContext(accessor);

        using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(
            OrganizationId: 42, UserId: 7, IsSiteAdmin: true, IsSystemOrganization: false)))
        {
            ctx.CurrentOrganizationId.Should().Be(9, "a real request's claim must override the ambient fallback");
            ctx.IsSiteAdmin.Should().BeFalse("the request has no site_admin claim, so the ambient flag must not leak in");
        }
    }

    [Fact]
    public void Nested_scopes_restore_the_previous_value()
    {
        var ctx = new HttpOrganizationContext(new HttpContextAccessor());

        using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(1, null, false, false)))
        {
            ctx.CurrentOrganizationId.Should().Be(1);
            using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(2, null, false, false)))
            {
                ctx.CurrentOrganizationId.Should().Be(2);
            }
            ctx.CurrentOrganizationId.Should().Be(1, "disposing the inner scope restores the outer org");
        }
    }
}
