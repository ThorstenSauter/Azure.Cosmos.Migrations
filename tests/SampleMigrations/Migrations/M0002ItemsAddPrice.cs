using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using ThorstenSauter.Azure.Cosmos.Migrations;

namespace SampleMigrations.Migrations;

/// <summary>
///     Adds <c>Price = 0</c> to items without one. Down removes it where it is still 0.
/// </summary>
internal sealed class M0002ItemsAddPrice : CosmosMigration
{
    public override string Id => "0002";

    public override string Description => "Items: add Price";

    public override async Task UpAsync(CancellationToken cancellationToken)
    {
        var container = Database.GetContainer("items");
        var query = new QueryDefinition("SELECT c.id FROM c WHERE NOT IS_DEFINED(c.Price)");

        await foreach (var item in QueryAllAsync<IdProjection>(container, query, cancellationToken))
        {
            await container.PatchItemAsync<object>(
                item.Id,
                new PartitionKey(item.Id),
                [PatchOperation.Add("/Price", 0)],
                cancellationToken: cancellationToken);
        }
    }

    public override async Task DownAsync(CancellationToken cancellationToken)
    {
        var container = Database.GetContainer("items");
        var query = new QueryDefinition("SELECT c.id FROM c WHERE c.Price = 0");

        await foreach (var item in QueryAllAsync<IdProjection>(container, query, cancellationToken))
        {
            await container.PatchItemAsync<object>(
                item.Id,
                new PartitionKey(item.Id),
                [PatchOperation.Remove("/Price")],
                cancellationToken: cancellationToken);
        }
    }

    private sealed record IdProjection([property: JsonProperty("id")] string Id);
}
