using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Azure.Cosmos;
using ThorstenSauter.Azure.Cosmos.Migrations.Design;
using TUnit.Core.Interfaces;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests.Infrastructure;

/// <summary>
///     One Cosmos DB emulator (the Linux vnext image) per test session. Every test gets its own database.
/// </summary>
public sealed class CosmosEmulator : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The emulator's fixed, publicly documented account key.</summary>
    private const string WellKnownKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const ushort GatewayPort = 8081;
    private const ushort HealthPort = 8080;

    private readonly IContainer _container = new ContainerBuilder("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest")
        .WithPortBinding(GatewayPort, assignRandomHostPort: true)
        .WithPortBinding(HealthPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(HealthPort).ForPath("/ready")))
        .Build();

    private CosmosClient? _client;

    /// <summary>Gets the key-based connection string.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Gets a client connected to the emulator.</summary>
    public CosmosClient Client => _client ?? throw new InvalidOperationException("The emulator is not started.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (RequiresEmulatorAttribute.Disabled)
        {
            return;
        }

        await _container.StartAsync();
        ConnectionString =
            $"AccountEndpoint=http://{_container.Hostname}:{_container.GetMappedPublicPort(GatewayPort)}/;AccountKey={WellKnownKey};";
        _client = CosmosConnection.CreateClient(ConnectionString);
    }

    /// <summary>Creates a database with a unique name.</summary>
    /// <returns>The database name.</returns>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test-{Guid.NewGuid():N}";
        await Client.CreateDatabaseAsync(name);
        return name;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _container.DisposeAsync();
    }
}
