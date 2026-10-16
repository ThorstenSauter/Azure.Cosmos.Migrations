namespace ThorstenSauter.Azure.Cosmos.Migrations;

/// <summary>
///     The outcome of <see cref="CosmosMigrationRunner.MigrateAsync" /> or
///     <see cref="CosmosMigrationRunner.RollbackAsync" />.
/// </summary>
/// <param name="Completed">The IDs of the migrations applied or rolled back, in the order they ran.</param>
/// <param name="FailedMigrationId">The ID of the migration that failed, if one did.</param>
/// <param name="Error">The exception that stopped the run, if any.</param>
public sealed record MigrationResult(
    IReadOnlyList<string> Completed,
    string? FailedMigrationId = null,
    Exception? Error = null)
{
    /// <summary>Gets a value indicating whether the run completed without an error.</summary>
    public bool Succeeded => Error is null;
}
