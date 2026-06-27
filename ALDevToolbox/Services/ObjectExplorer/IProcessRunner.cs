using System.Diagnostics;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// A minimal seam over <see cref="Process"/> for the project-build pipeline's two
/// external tools — <c>git</c> (clone) and <c>alc</c> (compile). It exists so
/// <see cref="ProjectBuildService"/> can be unit-tested without spawning real
/// processes: tests substitute a fake that returns canned exit codes / output for
/// each invocation. There's exactly one real implementation
/// (<see cref="ProcessRunner"/>), and this is the sanctioned reason to introduce
/// an interface (a genuine test seam), per CLAUDE.md.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="request"/> to completion, capturing stdout/stderr.
    /// Never throws on a non-zero exit — the caller inspects
    /// <see cref="ProcessRunResult.ExitCode"/>. Throws only when the executable
    /// can't be started at all (missing binary).
    /// </summary>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default);
}

/// <summary>One external-process invocation: the executable, its argument vector, and optional working dir / extra env.</summary>
/// <remarks>
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so each entry
/// is passed verbatim with no shell interpretation. Secrets never go in
/// <see cref="Arguments"/> — argv is visible via the world-readable
/// <c>/proc/&lt;pid&gt;/cmdline</c> in this multi-tenant process — they go in
/// <see cref="Environment"/> instead (e.g. git's PAT header via
/// <c>GIT_CONFIG_*</c>), which is not exposed there. See issue #430.
/// </remarks>
public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null);

/// <summary>The captured result of a finished process.</summary>
public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// The production <see cref="IProcessRunner"/>: starts the process with redirected
/// stdout/stderr and awaits exit. Stateless, registered as a singleton.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var arg in request.Arguments) psi.ArgumentList.Add(arg);
        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment) psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {request.FileName}.");

        // A Timeout converts a stalled child (e.g. a git clone blocked on a network
        // or credential stall) into a terminated, non-zero result the caller can
        // surface — rather than awaiting forever. A genuine external cancellation
        // (ct) still propagates as OperationCanceledException. See issue: discovery
        // clone hangs with no timeout.
        using var timeoutCts = request.Timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree so a stalled clone leaves nothing behind, and
            // observe the read tasks so they don't fault unobserved.
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            await Swallow(stdoutTask).ConfigureAwait(false);
            await Swallow(stderrTask).ConfigureAwait(false);

            // Timeout (not a real external cancel) → a non-zero result, so the
            // build/discovery treat it like any other clone failure.
            if (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
            {
                var seconds = (int)request.Timeout!.Value.TotalSeconds;
                return new ProcessRunResult(-1, string.Empty,
                    $"Timed out after {seconds}s and was terminated.");
            }
            throw; // genuine caller cancellation
        }

        static async Task Swallow(Task task)
        {
            try { await task.ConfigureAwait(false); } catch { /* cancelled / process gone */ }
        }
    }
}
