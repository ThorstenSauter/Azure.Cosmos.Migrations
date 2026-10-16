using System.Net;
using Microsoft.Azure.Cosmos;
using ThorstenSauter.Azure.Cosmos.Migrations;

namespace SampleMigrations.Migrations;

/// <summary>
///     Seeds a second item. Down deletes it.
/// </summary>
internal sealed class M0003ItemsSeedSecond : CosmosMigration
{
    // Not public: discovery must handle non-public constructors.
    private M0003ItemsSeedSecond()
    {
    }

    public override string Id => "0003";

    public override string Description => "Items: seed a second item";

    public override async Task UpAsync(CancellationToken cancellationToken) =>
        await Database.GetContainer("items").UpsertItemAsync(
            new { id = "item-2", Name = "Second", Price = 5 },
            new PartitionKey("item-2"),
            cancellationToken: cancellationToken);

    public override async Task DownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Database.GetContainer("items").DeleteItemAsync<object>(
                "item-2",
                new PartitionKey("item-2"),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }
}
