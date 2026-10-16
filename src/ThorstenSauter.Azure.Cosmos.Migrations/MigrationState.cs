namespace ThorstenSauter.Azure.Cosmos.Migrations;

/// <summary>
///     The state of one known migration, as reported by <see cref="CosmosMigrationRunner.GetStatusAsync" />.
/// </summary>
/// <param name="Id">The migration ID.</param>
/// <param name="Description">The migration description.</param>
/// <param name="AppliedAt">When the migration was applied (UTC), or <see langword="null" /> while it is pending.</param>
public sealed record MigrationState(string Id, string Description, DateTime? AppliedAt)
{
    /// <summary>Gets a value indicating whether the migration has been applied.</summary>
    public bool IsApplied => AppliedAt is not null;
}
