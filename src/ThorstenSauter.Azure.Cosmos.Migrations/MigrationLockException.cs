namespace ThorstenSauter.Azure.Cosmos.Migrations;

/// <summary>
///     Thrown when the runner cannot hold the migration lease: another runner kept it for longer than
///     <see cref="CosmosMigrationOptions.LockTimeout" />, or the lease could not be renewed and another runner may
///     have taken it over. Nothing else is applied or rolled back after this.
/// </summary>
public sealed class MigrationLockException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="MigrationLockException" /> class.</summary>
    public MigrationLockException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MigrationLockException" /> class.</summary>
    /// <param name="message">The error message.</param>
    public MigrationLockException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MigrationLockException" /> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public MigrationLockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
