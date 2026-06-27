using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The in-memory dedupe / in-flight contract of <see cref="ProjectDiscoveryQueue"/>:
/// a second enqueue for the same project coalesces into the running one, and the
/// in-flight flag reflects enqueue → <see cref="ProjectDiscoveryQueue.Complete"/>.
/// </summary>
public sealed class ProjectDiscoveryQueueTests
{
    private static ProjectDiscoveryJob Job(int projectId) =>
        new(projectId, new AmbientOrganizationScope.OrganizationIdentity(
            TestDb.DefaultOrgId, UserId: 1, IsSiteAdmin: false, IsSystemOrganization: false));

    [Fact]
    public async Task Enqueue_marks_the_project_in_flight()
    {
        var queue = new ProjectDiscoveryQueue();

        var enqueued = await queue.EnqueueAsync(Job(42));

        enqueued.Should().BeTrue();
        queue.IsInFlight(42).Should().BeTrue();
        queue.IsInFlight(99).Should().BeFalse("only the enqueued project is in flight");
    }

    [Fact]
    public async Task Enqueue_dedupes_an_already_in_flight_project()
    {
        var queue = new ProjectDiscoveryQueue();

        (await queue.EnqueueAsync(Job(42))).Should().BeTrue();
        (await queue.EnqueueAsync(Job(42))).Should().BeFalse("a discovery is already queued/running for this project");

        // The first job is still the only one on the channel — the duplicate was dropped.
        queue.Reader.TryRead(out var first).Should().BeTrue();
        first!.ProjectId.Should().Be(42);
        queue.Reader.TryRead(out _).Should().BeFalse("the duplicate enqueue wrote nothing");
    }

    [Fact]
    public async Task Complete_clears_the_flag_and_allows_re_enqueue()
    {
        var queue = new ProjectDiscoveryQueue();
        await queue.EnqueueAsync(Job(42));

        queue.Complete(42);

        queue.IsInFlight(42).Should().BeFalse();
        (await queue.EnqueueAsync(Job(42))).Should().BeTrue("once finished, the project can be discovered again");
    }
}
