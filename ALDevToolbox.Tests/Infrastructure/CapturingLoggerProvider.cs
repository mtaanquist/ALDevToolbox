using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Keeps whatever the code under test logged, so a component test can put the
/// real cause in its failure message instead of leaving it in a discarded
/// logger.
///
/// <para>This exists because of #739. A page that catches its own exceptions
/// and renders an error banner tells a test nothing when the test registers
/// <c>NullLoggerFactory</c>: the save failed, the success banner never
/// appeared, and the assertion timed out with no idea why. Thirty seconds of
/// waiting produced the sentence "the assertion did not pass within the
/// timeout period" and nothing else, which is what made the failure look like
/// slowness for as long as it did.</para>
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> _entries = new();

    public ILogger CreateLogger(string categoryName) => new Capturing(_entries);

    /// <summary>Everything logged at Error or above, newest last, one per line.</summary>
    public string ErrorsForFailureMessage()
    {
        var errors = _entries.Where(e => e.Level >= LogLevel.Error).ToList();
        if (errors.Count == 0) return "(nothing was logged at Error level)";
        return string.Join("\n", errors.Select(e =>
            e.Error is null ? $"  {e.Level}: {e.Message}" : $"  {e.Level}: {e.Message}\n    {e.Error}"));
    }

    public void Dispose() { }

    private sealed class Capturing(ConcurrentQueue<(LogLevel, string, Exception?)> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => sink.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
