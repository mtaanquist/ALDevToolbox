using System.Diagnostics;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// A minimal seam over <see cref="Process"/> for the customer-build pipeline's two
/// external tools — <c>git</c> (clone) and <c>alc</c> (compile). It exists so
/// <see cref="CustomerBuildService"/> can be unit-tested without spawning real
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
/// is passed verbatim with no shell interpretation — secrets in an arg (the git
/// <c>http.extraHeader</c>) are never word-split or logged by the shell. Callers
/// still avoid putting secrets where a process listing would show them; the git
/// header is a transient <c>-c</c> arg, acceptable here because the container is
/// single-tenant to the build.
/// </remarks>
public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessRunResult(process.ExitCode, stdout, stderr);
    }
}
