using System;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace LetterboxdSync.Tests.Integration;

/// <summary>
/// Bridges Microsoft.Extensions.Logging output to xUnit's per-test output buffer
/// so the integration suite's auth, list, write, and cleanup paths surface their
/// log lines on the test runner's console (and in CI run logs) without polluting
/// stdout. Used by every integration test class; instantiate one per test class
/// from the test's constructor with the injected <see cref="ITestOutputHelper"/>.
/// </summary>
internal sealed class XunitLogger : ILogger
{
    private readonly ITestOutputHelper _output;

    public XunitLogger(ITestOutputHelper output) { _output = output; }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // WriteLine can throw if the test has already completed and the output
        // buffer is closed (rare but real with async logger paths that outlive
        // the test). Swallow to avoid masking the actual test result.
        try { _output.WriteLine($"[{logLevel}] {formatter(state, exception)}"); }
        catch { }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
