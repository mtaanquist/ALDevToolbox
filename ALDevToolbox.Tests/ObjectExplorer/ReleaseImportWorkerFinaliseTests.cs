using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pins <see cref="ReleaseImportWorker.ShouldFinaliseJobRow"/>, the decision the
/// #483 fix hinges on: a job interrupted by graceful shutdown is left <c>running</c>
/// (not stamped <c>failed</c>) so the startup reconciler resumes it, while a real
/// success or failure is finalised normally. Paired with the reconciler tests in
/// <see cref="PersistedImportJobsTests"/>, which cover what happens to the
/// left-running row on the next boot.
/// </summary>
public sealed class ReleaseImportWorkerFinaliseTests
{
    [Fact]
    public void Interrupted_by_shutdown_is_left_running()
    {
        // Cancelled mid-job and never succeeded — leave the row for the reconciler.
        ReleaseImportWorker.ShouldFinaliseJobRow(jobSucceeded: false, cancellationRequested: true)
            .Should().BeFalse();
    }

    [Fact]
    public void Success_during_shutdown_is_still_persisted()
    {
        // Finished before the shutdown landed — persist the completion.
        ReleaseImportWorker.ShouldFinaliseJobRow(jobSucceeded: true, cancellationRequested: true)
            .Should().BeTrue();
    }

    [Fact]
    public void Real_failure_is_finalised()
    {
        // Not a shutdown — a genuine failure the admin should see.
        ReleaseImportWorker.ShouldFinaliseJobRow(jobSucceeded: false, cancellationRequested: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Normal_completion_is_finalised()
    {
        ReleaseImportWorker.ShouldFinaliseJobRow(jobSucceeded: true, cancellationRequested: false)
            .Should().BeTrue();
    }
}
