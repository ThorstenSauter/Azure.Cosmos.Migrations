using System.Runtime.CompilerServices;
using Microsoft.Azure.Cosmos;

namespace ThorstenSauter.Azure.Cosmos.Migrations;

/// <summary>
///     A single, reversible Cosmos DB migration.
///     <para>
///         Derive from this class and give the migration a parameterless constructor (public or not). The runner
///         creates it and sets <see cref="Client" />, <see cref="DatabaseName" /> and <see cref="Database" /> before
///         calling <see cref="UpAsync" /> or <see cref="DownAsync" />.
///     </para>
/// </summary>
public abstract class CosmosMigration
{
    private CosmosClient? _client;
    private string? _databaseName;

    /// <summary>
    ///     Gets the unique identifier. Migrations run in ordinal order of this value, so use fixed-width,
    ///     zero-padded numbers such as <c>"0001"</c>.
    /// </summary>
    public abstract string Id { get; }

    /// <summary>Gets a human-readable description, stored with the applied-migration record.</summary>
    public abstract string Description { get; }

    /// <summary>Gets the Cosmos DB client the migration runs with.</summary>
    /// <exception cref="InvalidOperationException">The runner has not initialized the migration yet.</exception>
    protected CosmosClient Client => _client ?? throw NotInitialized();

    /// <summary>Gets the name of the database being migrated.</summary>
    /// <exception cref="InvalidOperationException">The runner has not initialized the migration yet.</exception>
    protected string DatabaseName => _databaseName ?? throw NotInitialized();

    /// <summary>Gets the database being migrated.</summary>
    /// <exception cref="InvalidOperationException">The runner has not initialized the migration yet.</exception>
    protected Database Database => Client.GetDatabase(DatabaseName);

    /// <summary>Applies the migration.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public abstract Task UpAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Reverses the migration. Must be idempotent and should only undo what <see cref="UpAsync" /> did, leaving data
    ///     changed since then intact.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public abstract Task DownAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Executes <paramref name="query" /> and streams the results page by page, so callers can process (e.g. patch)
    ///     each item as pages arrive instead of buffering the full result set.
    /// </summary>
    /// <typeparam name="T">The projection type to deserialize each result into.</typeparam>
    /// <param name="container">The container to query.</param>
    /// <param name="query">The query to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async sequence of matching documents.</returns>
    protected static async IAsyncEnumerable<T> QueryAllAsync<T>(
        Container container,
        QueryDefinition query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);

        using var iterator = container.GetItemQueryIterator<T>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            foreach (var item in page)
            {
                yield return item;
            }
        }
    }

    internal void Initialize(CosmosClient client, string databaseName)
    {
        _client = client;
        _databaseName = databaseName;
    }

    private InvalidOperationException NotInitialized() =>
        new($"Migration {GetType().Name} has not been initialized; run it through {nameof(CosmosMigrationRunner)}.");
}
