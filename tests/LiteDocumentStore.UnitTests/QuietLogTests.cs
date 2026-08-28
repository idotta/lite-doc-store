using Microsoft.Extensions.Logging;
using Xunit;

namespace LiteDocumentStore.UnitTests;

/// <summary>
/// Unit tests for <see cref="QuietLog"/> — the wrappers that keep a caller-supplied
/// <see cref="ILogger"/> from throwing out of a path that has a resource to hand back.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QuietLogTests
{
    /// <summary>
    /// A consumer logger that fails on every call, and records what it was asked to write.
    /// </summary>
    private sealed class ThrowingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Attempts { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Attempts.Add((logLevel, formatter(state, exception), exception));
            throw new InvalidOperationException("logger failed");
        }
    }

    [Fact]
    public void LogDebugQuietly_WithAThrowingLogger_DoesNotThrow()
    {
        var logger = new ThrowingLogger();

        logger.LogDebugQuietly("a message");

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Attempts).Level);
    }

    [Fact]
    public void LogInformationQuietly_WithAThrowingLogger_DoesNotThrow()
    {
        var logger = new ThrowingLogger();

        logger.LogInformationQuietly("a message");

        Assert.Equal(LogLevel.Information, Assert.Single(logger.Attempts).Level);
    }

    [Fact]
    public void LogWarningQuietly_WithAThrowingLogger_DoesNotThrow()
    {
        var logger = new ThrowingLogger();

        logger.LogWarningQuietly("a message");

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Attempts).Level);
    }

    [Fact]
    public void LogWarningQuietly_WithAnExceptionAndAThrowingLogger_DoesNotThrow()
    {
        var logger = new ThrowingLogger();
        var error = new InvalidOperationException("the original failure");

        logger.LogWarningQuietly(error, "a message");

        var attempt = Assert.Single(logger.Attempts);
        Assert.Equal(LogLevel.Warning, attempt.Level);
        Assert.Same(error, attempt.Error);
    }

    [Fact]
    public void LoudLogging_WithAThrowingLogger_StillThrows()
    {
        // The counterpart of every test above: quiet is a deliberate carve-out for release
        // paths, not the library's default. An operation path still surfaces a broken logger.
        var logger = new ThrowingLogger();

        Assert.Throws<InvalidOperationException>(() => logger.LogWarning("a message"));
    }

    [Fact]
    public void QuietLogging_ForwardsTheTemplateAndItsArguments()
    {
        // The wrappers forward the template rather than collapsing it into "{Message}", so the
        // structured fields a consumer's sink sees are the ones the call site named.
        var logger = new RecordingLogger();

        logger.LogWarningQuietly("Discarding a pooled connection: {Reason}", "it was dirty");

        Assert.Equal("Discarding a pooled connection: it was dirty", Assert.Single(logger.Messages));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
