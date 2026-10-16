using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Shouldly;
using ThorstenSauter.Azure.Cosmos.Migrations.Tests.Infrastructure;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests;

[RequiresEmulator]
[ClassDataSource<CosmosEmulator>(Shared = SharedType.PerTestSession)]
public class RunnerIntegrationTests(CosmosEmulator emulator)
{
    [Test]
    public async Task MigrateAsync_FreshDatabase_AppliesAllAndRecordsThem()
    {
        var database = await emulator.CreateDatabaseAsync();

        var result = await SampleRunner(database).MigrateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Completed.ShouldBe(["0001", "0002", "0003"]);
        (await ReadRawAsync(database, "item-1", "items")).GetProperty("Price").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task MigrateAsync_Applied_WritesRecordInDocumentFormat()
    {
        var database = await emulator.CreateDatabaseAsync();

        await SampleRunner(database).MigrateAsync("0001");

        var record = await ReadRawAsync(database, "0001");
        record.GetProperty("id").GetString().ShouldBe("0001");
        record.GetProperty("Description").GetString().ShouldBe("Items: create container and seed one item");
        record.GetProperty("AppliedAt").GetDateTime().ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-5));
    }

    [Test]
    public async Task MigrateAsync_SecondRun_HasNothingPending()
    {
        var database = await emulator.CreateDatabaseAsync();
        await SampleRunner(database).MigrateAsync();

        var result = await SampleRunner(database).MigrateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Completed.ShouldBeEmpty();
    }

    [Test]
    public async Task MigrateAsync_Target_StopsAfterTarget()
    {
        var database = await emulator.CreateDatabaseAsync();

        var result = await SampleRunner(database).MigrateAsync("0002");

        result.Completed.ShouldBe(["0001", "0002"]);
        var status = await SampleRunner(database).GetStatusAsync();
        status.Select(s => s.IsApplied).ShouldBe([true, true, false]);
    }

    [Test]
    public async Task MigrateAsync_RecordsWrittenByNewtonsoft_AreRecognised()
    {
        // The format older hand-rolled runners wrote with the Cosmos SDK's default (Newtonsoft) serializer.
        var database = await emulator.CreateDatabaseAsync();
        var container = (await emulator.Client.GetDatabase(database)
            .CreateContainerIfNotExistsAsync(new ContainerProperties("migrations", "/id"))).Container;
        await container.CreateItemAsync(
            new LegacyRecord { Id = "0001", Description = "legacy", AppliedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
            new PartitionKey("0001"));

        var status = await SampleRunner(database).GetStatusAsync();

        status[0].AppliedAt.ShouldBe(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        status[1].IsApplied.ShouldBeFalse();
    }

    [Test]
    public async Task RollbackAsync_NoTarget_RollsBackOnlyLast()
    {
        var database = await emulator.CreateDatabaseAsync();
        await SampleRunner(database).MigrateAsync();

        var result = await SampleRunner(database).RollbackAsync();

        result.Completed.ShouldBe(["0003"]);
        var status = await SampleRunner(database).GetStatusAsync();
        status.Select(s => s.IsApplied).ShouldBe([true, true, false]);
    }

    [Test]
    public async Task RollbackAsync_TargetBeforeFirst_RollsBackEverything()
    {
        var database = await emulator.CreateDatabaseAsync();
        await SampleRunner(database).MigrateAsync();

        var result = await SampleRunner(database).RollbackAsync("0000");

        result.Completed.ShouldBe(["0003", "0002", "0001"]);
        (await SampleRunner(database).GetStatusAsync()).ShouldAllBe(s => !s.IsApplied);
    }

    [Test]
    public async Task RollbackThenMigrate_RoundTrip_ReappliesMigration()
    {
        var database = await emulator.CreateDatabaseAsync();
        await SampleRunner(database).MigrateAsync();
        await SampleRunner(database).RollbackAsync("0002");

        var result = await SampleRunner(database).MigrateAsync();

        result.Completed.ShouldBe(["0003"]);
    }

    [Test]
    public async Task MigrateAsync_FailingMigration_StopsAndKeepsEarlierOnes()
    {
        var database = await emulator.CreateDatabaseAsync();
        List<string> log = [];
        var runner = Runner(
            database,
            new TestMigration("0001", log),
            new TestMigration("0002", log, up: _ => throw new InvalidOperationException("boom")),
            new TestMigration("0003", log));

        var result = await runner.MigrateAsync();

        result.Succeeded.ShouldBeFalse();
        result.FailedMigrationId.ShouldBe("0002");
        result.Error.ShouldBeOfType<InvalidOperationException>();
        result.Completed.ShouldBe(["0001"]);
        log.ShouldBe(["up:0001", "up:0002"]);
        (await runner.GetStatusAsync()).Select(s => s.IsApplied).ShouldBe([true, false, false]);
        (await LeaseExistsAsync(database)).ShouldBeFalse();
    }

    [Test]
    public async Task MigrateAsync_InitializesMigrationsWithDatabase()
    {
        var database = await emulator.CreateDatabaseAsync();
        var migration = new TestMigration("0001");

        await Runner(database, migration).MigrateAsync();

        migration.InitializedDatabaseName.ShouldBe(database);
    }

    [Test]
    public async Task GetStatusAsync_AppliedMigrationRemovedFromCode_IsListedAfterKnownOnes()
    {
        var database = await emulator.CreateDatabaseAsync();
        await Runner(database, new TestMigration("0001"), new TestMigration("0002")).MigrateAsync();

        var status = await Runner(database, new TestMigration("0001")).GetStatusAsync();

        status.Select(s => (s.Id, s.IsApplied)).ShouldBe([("0001", true), ("0002", true)]);
    }

    [Test]
    public async Task MigrateAsync_AfterRun_ReleasesLease()
    {
        var database = await emulator.CreateDatabaseAsync();

        await SampleRunner(database).MigrateAsync();

        (await LeaseExistsAsync(database)).ShouldBeFalse();
    }

    [Test]
    public async Task MigrateAsync_MissingDatabase_ReturnsFailure()
    {
        var result = await Runner($"missing-{Guid.NewGuid():N}", new TestMigration("0001")).MigrateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBeOfType<CosmosException>().StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private CosmosMigrationRunner SampleRunner(string database) =>
        new(
            emulator.Client,
            new CosmosMigrationOptions { DatabaseName = database },
            CosmosMigrationRunner.DiscoverMigrations(System.Reflection.Assembly.Load("SampleMigrations")));

    private CosmosMigrationRunner Runner(string database, params CosmosMigration[] migrations) =>
        new(emulator.Client, new CosmosMigrationOptions { DatabaseName = database }, migrations);

    private async Task<JsonElement> ReadRawAsync(string database, string id, string container = "migrations")
    {
        using var response = await emulator.Client.GetDatabase(database).GetContainer(container)
            .ReadItemStreamAsync(id, new PartitionKey(id));
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(response.Content, Encoding.UTF8);
        return JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
    }

    private async Task<bool> LeaseExistsAsync(string database)
    {
        using var response = await emulator.Client.GetDatabase(database).GetContainer("migrations")
            .ReadItemStreamAsync("__lock", new PartitionKey("__lock"));
        return response.IsSuccessStatusCode;
    }

    private sealed class LegacyRecord
    {
        [Newtonsoft.Json.JsonProperty("id")]
        public required string Id { get; init; }

        public required string Description { get; init; }

        public DateTime AppliedAt { get; init; }
    }
}
