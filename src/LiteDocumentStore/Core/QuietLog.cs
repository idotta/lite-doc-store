using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace LiteDocumentStore;

/// <summary>
/// Logging that cannot throw, for the paths that hand a resource back.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ILogger"/> is caller-supplied, so a log call is caller code and may throw. On a
/// path that returns a pooled connection, releases a slot or closes a handle, a throw does not
/// merely lose the message: it skips the hand-back, and every one of those paths guards itself
/// with a "already released" flag, so no retry can reach the release afterwards. The resource is
/// then lost for the lifetime of the process, and once that has happened as many times as the
/// pool is wide, the store deadlocks with no error anywhere.
/// </para>
/// <para>
/// So the rule is: <b>a log that sits on a release path is quiet</b>. Logging on an operation
/// path is deliberately left loud — a caller is waiting there and should learn that their logger
/// is broken.
/// </para>
/// </remarks>
internal static class QuietLog
{
    /// <summary>
    /// Logs at <see cref="LogLevel.Debug"/>, swallowing a failure from the caller's logger.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "The template is forwarded verbatim from a call site whose own template is constant.")]
    internal static void LogDebugQuietly(this ILogger logger, string message, params object?[] args)
    {
        try
        {
            logger.LogDebug(message, args);
        }
        catch
        {
            // Nothing left to report it to.
        }
    }

    /// <inheritdoc cref="LogDebugQuietly" />
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "The template is forwarded verbatim from a call site whose own template is constant.")]
    internal static void LogInformationQuietly(this ILogger logger, string message, params object?[] args)
    {
        try
        {
            logger.LogInformation(message, args);
        }
        catch
        {
            // Nothing left to report it to.
        }
    }

    /// <inheritdoc cref="LogDebugQuietly" />
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "The template is forwarded verbatim from a call site whose own template is constant.")]
    internal static void LogWarningQuietly(this ILogger logger, string message, params object?[] args)
    {
        try
        {
            logger.LogWarning(message, args);
        }
        catch
        {
            // Nothing left to report it to.
        }
    }

    /// <inheritdoc cref="LogDebugQuietly" />
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "The template is forwarded verbatim from a call site whose own template is constant.")]
    internal static void LogWarningQuietly(this ILogger logger, Exception error, string message, params object?[] args)
    {
        try
        {
            logger.LogWarning(error, message, args);
        }
        catch
        {
            // Nothing left to report it to.
        }
    }
}
