using System.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ThorstenSauter.Azure.Cosmos.Migrations.Storage;
using ThorstenSauter.Azure.Cosmos.Migrations.Tests.Infrastructure;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests;

[RequiresEmulator]
[ClassDataSource<CosmosEmulator>(Shared = SharedType.PerTestSession)]
public class LeaseIntegrationTests(CosmosEmulator emulator)
{
    [Test]
    public async Task MigrateAsync_LeaseHeldByAnotherRunner_WaitsThenFails()
    {
        var database = await emulator.CreateDatabaseAsync();
        var store = await OpenStoreAsync(database);
        await using var held = await MigrationLease.AcquireAsync(
            store.Container,
            Options(database, leaseDuration: TimeSpan.FromMinutes(10)),
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        List<string> log = [];
        var runner = new CosmosMigrationRunner(
            emulator.Client,
            Options(database, lockTimeout: TimeSpan.FromSeconds(2)),
            [new TestMigration("0001", log)]);

        var stopwatch = Stopwatch.StartNew();
        var result = await runner.MigrateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBeOfType<MigrationLockException>();
        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        log.ShouldBeEmpty();
    }

    [Test]
    public async Task MigrateAsync_LeaseReleasedWhileWaiting_ProceedsAfterRelease()
    {
        var database = await emulator.CreateDatabaseAsync();
        var store = await OpenStoreAsync(database);
        var held = await MigrationLease.AcquireAsync(
            store.Container,
            Options(database),
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var runner = new CosmosMigrationRunner(
            emulator.Client,
            Options(database, lockTimeout: TimeSpan.FromSeconds(30)),
            [new TestMigration("0001")]);

        var run = runner.MigrateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        run.IsCompleted.ShouldBeFalse();
        await held.DisposeAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        result.Completed.ShouldBe(["0001"]);
    }

    [Test]
    public async Task AcquireAsync_ExpiredLease_TakesItOver()
    {
        var database = await emulator.CreateDatabaseAsync();
        var store = await OpenStoreAsync(database);

        // A runner that died an hour ago: its clock put the expiry in the past.
        var past = new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(-1));
        var abandoned = await MigrationLease.AcquireAsync(store.Container, Options(database), past, NullLogger.Instance, CancellationToken.None);
        GC.KeepAlive(abandoned);

        var result = await new CosmosMigrationRunner(
            emulator.Client,
            Options(database, lockTimeout: TimeSpan.Zero),
            [new TestMigration("0001")]).MigrateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Completed.ShouldBe(["0001"]);
    }

    [Test]
    public async Task Lease_ChangedByAnotherRunner_IsLostOnNextRenewal()
    {
        var database = await emulator.CreateDatabaseAsync();
        var store = await OpenStoreAsync(database);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var lease = await MigrationLease.AcquireAsync(
            store.Container,
            Options(database, leaseDuration: TimeSpan.FromMinutes(3)),
            time,
            NullLogger.Instance,
            CancellationToken.None);

        // Another runner overwrites the document, changing its ETag.
        await store.Container.UpsertItemAsync(
            new { id = MigrationStore.LeaseId, owner = "someone-else", acquiredAt = DateTimeOffset.UtcNow, expiresAt = DateTimeOffset.UtcNow.AddHours(1) },
            new PartitionKey(MigrationStore.LeaseId));

        time.Advance(TimeSpan.FromMinutes(1));

        await WaitUntilAsync(() => lease.LostToken.IsCancellationRequested);
        lease.LostToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public async Task Lease_Renewal_ExtendsExpiry()
    {
        var database = await emulator.CreateDatabaseAsync();
        var store = await OpenStoreAsync(database);
        var start = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(start);
        await using var lease = await MigrationLease.AcquireAsync(
            store.Container,
            Options(database, leaseDuration: TimeSpan.FromMinutes(3)),
            time,
            NullLogger.Instance,
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(1));

        await WaitUntilAsync(async () =>
            (await store.Container.ReadItemAsync<LeaseProjection>(MigrationStore.LeaseId, new PartitionKey(MigrationStore.LeaseId)))
            .Resource.ExpiresAt > start.AddMinutes(3));
        lease.LostToken.IsCancellationRequested.ShouldBeFalse();
    }

    private static CosmosMigrationOptions Options(
        string database,
        TimeSpan? leaseDuration = null,
        TimeSpan? lockTimeout = null) =>
        new()
        {
            DatabaseName = database,
            LeaseDuration = leaseDuration ?? CosmosMigrationOptions.DefaultLeaseDuration,
            LockTimeout = lockTimeout ?? CosmosMigrationOptions.DefaultLockTimeout,
            LockPollInterval = TimeSpan.FromMilliseconds(200)
        };

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }

    private Task<MigrationStore> OpenStoreAsync(string database) =>
        MigrationStore.OpenAsync(emulator.Client, new CosmosMigrationOptions { DatabaseName = database }, CancellationToken.None);

    private sealed record LeaseProjection([property: Newtonsoft.Json.JsonProperty("expiresAt")] DateTimeOffset ExpiresAt);
}
