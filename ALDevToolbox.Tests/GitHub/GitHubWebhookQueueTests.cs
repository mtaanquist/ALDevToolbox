using ALDevToolbox.Services.GitHub;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The supersession contract of <see cref="GitHubWebhookQueue"/> (issue #627):
/// one build per pull-request head at a time, an older head dropped when it is
/// dequeued, and an in-flight build cancelled the moment a newer head is
/// announced. This is the part of the compile gate that decides whether a
/// reviewer sees a tick about the commit they are looking at.
/// </summary>
public sealed class GitHubWebhookQueueTests
{
    private static GitHubPullRequestJob Job(string sha, int number = 7) =>
        new(
            InstallationId: 42,
            RepositoryFullName: "cronus-dk/customer-app",
            CloneUrl: "https://github.com/cronus-dk/customer-app.git",
            PullRequestNumber: number,
            HeadSha: sha,
            HeadRef: "feature/vat",
            BaseRef: "main",
            DeliveryId: "delivery-" + sha);

    [Fact]
    public void The_key_ignores_case_so_two_spellings_are_one_pull_request()
    {
        var lower = Job("abc") with { RepositoryFullName = "cronus-dk/customer-app" };
        var upper = Job("abc") with { RepositoryFullName = "CRONUS-DK/Customer-App" };

        upper.Key.Should().Be(lower.Key);
    }

    [Fact]
    public void Different_pull_requests_are_different_keys() =>
        Job("abc", number: 7).Key.Should().NotBe(Job("abc", number: 8).Key);

    [Fact]
    public void An_unknown_key_is_treated_as_current()
    {
        // A restart drops the bookkeeping. Refusing to build a job we have no
        // record of would be worse than building it.
        var queue = new GitHubWebhookQueue();

        queue.IsLatest("never-seen", "abc").Should().BeTrue();
    }

    [Fact]
    public void The_announced_head_is_the_latest_and_the_previous_one_is_not()
    {
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");

        queue.Announce(job.Key, "aaa");
        queue.IsLatest(job.Key, "aaa").Should().BeTrue();

        queue.Announce(job.Key, "bbb");
        queue.IsLatest(job.Key, "bbb").Should().BeTrue();
        queue.IsLatest(job.Key, "aaa").Should().BeFalse("a newer commit was pushed to the same pull request");
    }

    [Fact]
    public void Announcing_a_newer_head_cancels_the_build_running_for_the_older_one()
    {
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        queue.Announce(job.Key, "aaa");

        using var running = new CancellationTokenSource();
        queue.BeginBuild(job.Key, running);

        queue.Announce(job.Key, "bbb");

        running.IsCancellationRequested.Should().BeTrue(
            "compiling a commit nobody is reviewing any more is work spent on the wrong answer");
    }

    [Fact]
    public void Re_announcing_the_same_head_does_not_cancel_the_build()
    {
        // GitHub redelivers; a redelivery is not a new commit.
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        queue.Announce(job.Key, "aaa");

        using var running = new CancellationTokenSource();
        queue.BeginBuild(job.Key, running);
        queue.Announce(job.Key, "aaa");

        running.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void A_build_for_another_pull_request_is_untouched()
    {
        var queue = new GitHubWebhookQueue();
        var mine = Job("aaa", number: 7);
        var theirs = Job("zzz", number: 8);
        queue.Announce(mine.Key, "aaa");
        queue.Announce(theirs.Key, "zzz");

        using var otherBuild = new CancellationTokenSource();
        queue.BeginBuild(theirs.Key, otherBuild);

        queue.Announce(mine.Key, "bbb");

        otherBuild.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void EndBuild_clears_the_registration_so_a_later_head_cancels_nothing()
    {
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        queue.Announce(job.Key, "aaa");

        var finished = new CancellationTokenSource();
        queue.BeginBuild(job.Key, finished);
        queue.EndBuild(job.Key, finished);
        finished.Dispose();

        // Cancelling a disposed source would throw; the queue must not try.
        var act = () => queue.Announce(job.Key, "bbb");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Enqueued_jobs_come_back_off_the_channel_in_order()
    {
        var queue = new GitHubWebhookQueue();

        await queue.EnqueueAsync(Job("aaa"));
        await queue.EnqueueAsync(Job("bbb"));

        queue.Reader.TryRead(out var first).Should().BeTrue();
        queue.Reader.TryRead(out var second).Should().BeTrue();
        first!.HeadSha.Should().Be("aaa");
        second!.HeadSha.Should().Be("bbb");
    }

    [Theory]
    [InlineData("https://github.com/cronus-dk/customer-app.git")]
    [InlineData("https://github.com/CRONUS-DK/Customer-App")]
    [InlineData("https://github.com/cronus-dk/customer-app/")]
    [InlineData("git@github.com:cronus-dk/customer-app.git")]
    public void Repository_urls_that_name_the_same_repository_normalise_to_one_value(string url) =>
        GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(url)
            .Should().Be("github.com/cronus-dk/customer-app");

    [Fact]
    public void A_different_repository_does_not_normalise_to_the_same_value() =>
        GitHubPullRequestBuildWorker.NormaliseRepositoryUrl("https://github.com/cronus-dk/other-app")
            .Should().NotBe(GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(
                "https://github.com/cronus-dk/customer-app"));
}
