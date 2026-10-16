using Microsoft.Azure.Cosmos;
using Shouldly;
using ThorstenSauter.Azure.Cosmos.Migrations.Tests.Infrastructure;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests;

public class MigrationSelectionTests
{
    private static readonly IReadOnlyList<CosmosMigration> All =
        [new TestMigration("0001"), new TestMigration("0002"), new TestMigration("0003"), new TestMigration("0004")];

    [Test]
    public void SelectPending_NoTarget_ReturnsUnappliedInOrder()
    {
        var pending = CosmosMigrationRunner.SelectPending(All, Applied("0001", "0003"), null);

        pending.Select(m => m.Id).ShouldBe(["0002", "0004"]);
    }

    [Test]
    public void SelectPending_Target_IncludesTarget()
    {
        var pending = CosmosMigrationRunner.SelectPending(All, Applied("0001"), "0003");

        pending.Select(m => m.Id).ShouldBe(["0002", "0003"]);
    }

    [Test]
    public void SelectRollback_NoTarget_ReturnsOnlyLastApplied()
    {
        var rollback = CosmosMigrationRunner.SelectRollback(All, Applied("0001", "0002", "0003"), null);

        rollback.Select(m => m.Id).ShouldBe(["0003"]);
    }

    [Test]
    public void SelectRollback_Target_ExcludesTargetNewestFirst()
    {
        var rollback = CosmosMigrationRunner.SelectRollback(All, Applied("0001", "0002", "0003", "0004"), "0002");

        rollback.Select(m => m.Id).ShouldBe(["0004", "0003"]);
    }

    [Test]
    public void SelectRollback_TargetBeforeFirst_ReturnsEverythingApplied()
    {
        var rollback = CosmosMigrationRunner.SelectRollback(All, Applied("0001", "0002"), "0000");

        rollback.Select(m => m.Id).ShouldBe(["0002", "0001"]);
    }

    [Test]
    public void SelectRollback_NothingApplied_ReturnsEmpty()
    {
        CosmosMigrationRunner.SelectRollback(All, Applied(), null).ShouldBeEmpty();
    }

    [Test]
    public void Constructor_UnorderedMigrations_OrdersOrdinally()
    {
        using var client = DummyClient();
        var runner = new CosmosMigrationRunner(
            client,
            Options(),
            [new TestMigration("0010"), new TestMigration("0002"), new TestMigration("0001")]);

        runner.Migrations.Select(m => m.Id).ShouldBe(["0001", "0002", "0010"]);
    }

    [Test]
    public void Constructor_DuplicateIds_Throws()
    {
        using var client = DummyClient();

        var ex = Should.Throw<ArgumentException>(() => new CosmosMigrationRunner(
            client,
            Options(),
            [new TestMigration("0001"), new TestMigration("0001")]));

        ex.Message.ShouldContain("\"0001\"");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("__lock")]
    public void Constructor_InvalidId_Throws(string id)
    {
        using var client = DummyClient();

        Should.Throw<ArgumentException>(() => new CosmosMigrationRunner(client, Options(), [new TestMigration(id)]));
    }

    [Test]
    public void DiscoverMigrations_SampleAssembly_FindsConcreteMigrationsInOrder()
    {
        var migrations = CosmosMigrationRunner.DiscoverMigrations(System.Reflection.Assembly.Load("SampleMigrations"));

        migrations.Select(m => m.Id).ShouldBe(["0001", "0002", "0003"]);
    }

    [Test]
    public void Client_BeforeInitialization_Throws()
    {
        var migration = new TestMigration("0001");

        Should.Throw<InvalidOperationException>(() => migration.InitializedDatabaseName);
    }

    private static HashSet<string> Applied(params string[] ids) => new(ids, StringComparer.Ordinal);

    private static CosmosMigrationOptions Options() => new() { DatabaseName = "db" };

    private static CosmosClient DummyClient() =>
        new("AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;");
}
