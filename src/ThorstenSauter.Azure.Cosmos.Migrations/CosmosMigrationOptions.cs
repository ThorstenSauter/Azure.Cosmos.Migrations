namespace ThorstenSauter.Azure.Cosmos.Migrations;

/// <summary>
///     Settings for a <see cref="CosmosMigrationRunner" />.
/// </summary>
public sealed class CosmosMigrationOptions
{
    /// <summary>The default name of the container that records applied migrations.</summary>
    public const string DefaultContainerName = "migrations";

    /// <summary>Gets the default <see cref="LeaseDuration" />: two minutes.</summary>
    public static TimeSpan DefaultLeaseDuration { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the default <see cref="LockTimeout" />: five minutes.</summary>
    public static TimeSpan DefaultLockTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the name of the database to migrate.</summary>
    public required string DatabaseName { get; init; }

    /// <summary>
    ///     Gets the name of the container that records applied migrations and holds the lease lock. It is created with
    ///     partition key <c>/id</c> if it does not exist.
    /// </summary>
    public string ContainerName { get; init; } = DefaultContainerName;

    /// <summary>
    ///     Gets how long a lease lasts before another runner may take it over. The holder renews it every third of
    ///     this period while it runs.
    /// </summary>
    public TimeSpan LeaseDuration { get; init; } = DefaultLeaseDuration;

    /// <summary>Gets how long to wait for a lease held by another runner before giving up.</summary>
    public TimeSpan LockTimeout { get; init; } = DefaultLockTimeout;

    /// <summary>
    ///     Gets how often to check whether a lease held by another runner has been released or has expired.
    /// </summary>
    public TimeSpan LockPollInterval { get; init; } = TimeSpan.FromSeconds(5);
}
