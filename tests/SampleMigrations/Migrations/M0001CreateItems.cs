using System.Net;
using Microsoft.Azure.Cosmos;
using ThorstenSauter.Azure.Cosmos.Migrations;

namespace SampleMigrations.Migrations;

/// <summary>
///     Creates the <c>items</c> container with one document. Down deletes the container.
/// </summary>
internal sealed class M0001CreateItems : CosmosMigration
{
    public override string Id => "0001";

    public override string Description => "Items: create container and seed one item";

    public override async Task UpAsync(CancellationToken cancellationToken)
    {
        var response = await Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("items", "/id"),
            cancellationToken: cancellationToken);

        await response.Container.UpsertItemAsync(
            new { id = "item-1", Name = "First" },
            new PartitionKey("item-1"),
            cancellationToken: cancellationToken);
    }

    public override async Task DownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Database.GetContainer("items").DeleteContainerAsync(cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }
}
