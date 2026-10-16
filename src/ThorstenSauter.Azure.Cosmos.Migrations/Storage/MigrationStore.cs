using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Storage;

/// <summary>
///     Reads and writes applied-migration records through the stream API.
/// </summary>
internal sealed class MigrationStore(Container container)
{
    /// <summary>
    ///     The ID of the lease document. Migration IDs must not use it; record queries skip it.
    /// </summary>
    internal const string LeaseId = "__lock";

    /// <summary>Gets the container holding the records and the lease.</summary>
    internal Container Container { get; } = container;

    /// <summary>
    ///     Creates the records container (partition key <c>/id</c>) if it does not exist yet.
    /// </summary>
    /// <param name="client">The Cosmos DB client.</param>
    /// <param name="options">The runner options naming the database and container.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The store for that container.</returns>
    internal static async Task<MigrationStore> OpenAsync(
        CosmosClient client,
        CosmosMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var response = await client.GetDatabase(options.DatabaseName).CreateContainerIfNotExistsAsync(
            new ContainerProperties(options.ContainerName, "/id"),
            cancellationToken: cancellationToken);

        return new MigrationStore(response.Container);
    }

    /// <summary>Reads every applied-migration record, keyed by migration ID.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The applied records.</returns>
    internal async Task<Dictionary<string, MigrationRecord>> GetAppliedAsync(CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT c.id, c.Description, c.AppliedAt FROM c WHERE c.id != @lease")
            .WithParameter("@lease", LeaseId);

        Dictionary<string, MigrationRecord> applied = new(StringComparer.Ordinal);
        using var iterator = Container.GetItemQueryStreamIterator(query);
        while (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            var page = await JsonSerializer.DeserializeAsync(
                response.Content,
                MigrationsJsonContext.Default.QueryPage,
                cancellationToken);

            foreach (var record in page?.Documents ?? [])
            {
                applied[record.Id] = record;
            }
        }

        return applied;
    }

    /// <summary>Records <paramref name="migration" /> as applied.</summary>
    /// <param name="migration">The migration that was applied.</param>
    /// <param name="appliedAt">When it was applied (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task AddAsync(CosmosMigration migration, DateTime appliedAt, CancellationToken cancellationToken)
    {
        var record = new MigrationRecord(migration.Id, migration.Description, appliedAt);
        using var content = Serialize(record, MigrationsJsonContext.Default.MigrationRecord);
        using var response = await Container.UpsertItemStreamAsync(
            content,
            new PartitionKey(record.Id),
            cancellationToken: cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Removes the record of <paramref name="migrationId" />; a missing record is not an error.</summary>
    /// <param name="migrationId">The ID of the migration that was rolled back.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task RemoveAsync(string migrationId, CancellationToken cancellationToken)
    {
        using var response = await Container.DeleteItemStreamAsync(
            migrationId,
            new PartitionKey(migrationId),
            cancellationToken: cancellationToken);

        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Serializes <paramref name="value" /> into a stream for the stream API.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="value">The document.</param>
    /// <param name="typeInfo">The source-generated type info.</param>
    /// <returns>A stream positioned at the start.</returns>
    internal static MemoryStream Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, value, typeInfo);
        stream.Position = 0;
        return stream;
    }
}
