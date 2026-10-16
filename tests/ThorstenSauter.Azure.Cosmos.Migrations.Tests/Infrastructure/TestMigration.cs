namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests.Infrastructure;

/// <summary>
///     A migration whose behaviour a test supplies. It records every call so tests can assert order.
/// </summary>
internal sealed class TestMigration(
    string id,
    IList<string>? log = null,
    Func<CancellationToken, Task>? up = null,
    Func<CancellationToken, Task>? down = null) : CosmosMigration
{
    public override string Id => id;

    public override string Description => $"Test migration {id}";

    public override async Task UpAsync(CancellationToken cancellationToken)
    {
        log?.Add($"up:{id}");
        if (up is not null)
        {
            await up(cancellationToken);
        }
    }

    public override async Task DownAsync(CancellationToken cancellationToken)
    {
        log?.Add($"down:{id}");
        if (down is not null)
        {
            await down(cancellationToken);
        }
    }

    /// <summary>Gets the database the runner initialized this migration with.</summary>
    public string InitializedDatabaseName => DatabaseName;
}
