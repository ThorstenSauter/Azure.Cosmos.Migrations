using Microsoft.Extensions.Logging;

namespace ThorstenSauter.Azure.Cosmos.Migrations;

/// <summary>
///     Source-generated log messages.
/// </summary>
internal static partial class MigrationsLog
{
    [LoggerMessage(LogLevel.Information, "Migrating up to (inclusive): {Target}")]
    internal static partial void LogMigratingUpTo(this ILogger logger, string target);

    [LoggerMessage(LogLevel.Information, "Rolling back to (exclusive): {Target}")]
    internal static partial void LogRollingBackTo(this ILogger logger, string target);

    [LoggerMessage(LogLevel.Information, "No pending migrations")]
    internal static partial void LogNoPendingMigrations(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Applying {Count} pending migration(s)")]
    internal static partial void LogApplyingMigrations(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "Applying [{Id}] {Description}")]
    internal static partial void LogApplyingMigration(this ILogger logger, string id, string description);

    [LoggerMessage(LogLevel.Information, "Applied  [{Id}]")]
    internal static partial void LogMigrationApplied(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Error, "Failed to apply [{Id}]; stopping")]
    internal static partial void LogMigrationApplyFailed(this ILogger logger, Exception exception, string id);

    [LoggerMessage(LogLevel.Information, "Applied {Count} migration(s)")]
    internal static partial void LogMigrationsApplied(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "Nothing to roll back")]
    internal static partial void LogNothingToRollBack(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Rolling back {Count} migration(s)")]
    internal static partial void LogRollingBackMigrations(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "Rolling back [{Id}] {Description}")]
    internal static partial void LogRollingBackMigration(this ILogger logger, string id, string description);

    [LoggerMessage(LogLevel.Information, "Rolled back [{Id}]")]
    internal static partial void LogMigrationRolledBack(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Error, "Failed to roll back [{Id}]; stopping")]
    internal static partial void LogMigrationRollBackFailed(this ILogger logger, Exception exception, string id);

    [LoggerMessage(LogLevel.Information, "Rolled back {Count} migration(s)")]
    internal static partial void LogMigrationsRolledBack(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Error, "Could not start the migration run")]
    internal static partial void LogRunFailedToStart(this ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Debug, "Acquired the migration lease as {Owner}")]
    internal static partial void LogLeaseAcquired(this ILogger logger, string owner);

    [LoggerMessage(LogLevel.Information, "Waiting for the migration lease held by {Owner} (expires {ExpiresAt:O})")]
    internal static partial void LogLeaseWaiting(this ILogger logger, string owner, DateTimeOffset expiresAt);

    [LoggerMessage(LogLevel.Warning, "Taking over the migration lease of {Owner}, which expired at {ExpiresAt:O}")]
    internal static partial void LogLeaseTakingOver(this ILogger logger, string owner, DateTimeOffset expiresAt);

    [LoggerMessage(LogLevel.Warning, "Could not renew the migration lease (status {StatusCode}); retrying")]
    internal static partial void LogLeaseRenewalFailed(this ILogger logger, int statusCode);

    [LoggerMessage(LogLevel.Error, "Lost the migration lease held as {Owner} (status {StatusCode}); stopping")]
    internal static partial void LogLeaseLost(this ILogger logger, string owner, int statusCode);

    [LoggerMessage(LogLevel.Debug, "Released the migration lease held as {Owner}")]
    internal static partial void LogLeaseReleased(this ILogger logger, string owner);

    [LoggerMessage(LogLevel.Warning, "Could not release the migration lease (status {StatusCode}); it expires on its own")]
    internal static partial void LogLeaseReleaseFailed(this ILogger logger, Exception? exception, int statusCode);
}
