using Shouldly;
using ThorstenSauter.Azure.Cosmos.Migrations.Tool;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests;

public class MigrationScaffolderTests
{
    [Test]
    public void NextId_NoMigrations_ReturnsFirst()
    {
        MigrationScaffolder.NextId([]).ShouldBe("0001");
    }

    [Test]
    public void NextId_Existing_IncrementsHighestKeepingWidth()
    {
        MigrationScaffolder.NextId(["0001", "0018", "0002"]).ShouldBe("0019");
    }

    [Test]
    public void NextId_WiderIds_KeepsTheirWidth()
    {
        MigrationScaffolder.NextId(["000099"]).ShouldBe("000100");
    }

    [Test]
    public void NextId_NonNumericId_Throws()
    {
        Should.Throw<ToolException>(() => MigrationScaffolder.NextId(["0001", "2026-01-add"]));
    }

    [Test]
    public void ParseList_ListOutput_ReturnsIdsAndTypes()
    {
        MigrationScaffolder.ParseList("""[{"id":"0001","description":"a","type":"App.X"},{"id":"0002","description":"b","type":"App.Y"}]""")
            .ShouldBe([new ListedMigration("0001", "App.X"), new ListedMigration("0002", "App.Y")]);
    }

    [Test]
    public void ChooseLocation_ExplicitDirectory_UsesIt()
    {
        MigrationScaffolder.ChooseLocation("Data/Migrations", "App", [new ListedMigration("0001", "App.M0001")])
            .ShouldBe(("Data/Migrations", "App.Data.Migrations"));
    }

    [Test]
    public void ChooseLocation_LatestInRootNamespace_UsesProjectRoot()
    {
        MigrationScaffolder.ChooseLocation(null, "App", [new ListedMigration("0001", "App.Old.M0001"), new ListedMigration("0002", "App.M0002")])
            .ShouldBe((".", "App"));
    }

    [Test]
    public void ChooseLocation_LatestInSubNamespace_UsesMatchingFolder()
    {
        MigrationScaffolder.ChooseLocation(null, "App", [new ListedMigration("0001", "App.Data.Migrations.M0001")])
            .ShouldBe(("Data/Migrations", "App.Data.Migrations"));
    }

    [Test]
    public void ChooseLocation_NoMigrationsYet_DefaultsToMigrationsFolder()
    {
        MigrationScaffolder.ChooseLocation(null, "App", []).ShouldBe(("Migrations", "App.Migrations"));
    }

    [Test]
    public void ChooseLocation_LatestOutsideRootNamespace_DefaultsToMigrationsFolder()
    {
        MigrationScaffolder.ChooseLocation(null, "App", [new ListedMigration("0001", "Other.M0001")])
            .ShouldBe(("Migrations", "App.Migrations"));
    }

    [Test]
    [Arguments("ordersAddDeletedAt", "OrdersAddDeletedAt")]
    [Arguments("Items_Fix", "Items_Fix")]
    public void NormalizeName_ValidIdentifier_StartsUppercase(string name, string expected)
    {
        MigrationScaffolder.NormalizeName(name).ShouldBe(expected);
    }

    [Test]
    [Arguments("1Orders")]
    [Arguments("orders-add")]
    [Arguments("")]
    public void NormalizeName_InvalidIdentifier_Throws(string name)
    {
        Should.Throw<ToolException>(() => MigrationScaffolder.NormalizeName(name));
    }

    [Test]
    [Arguments("Migrations", "App.Migrations")]
    [Arguments("Data/Migrations", "App.Data.Migrations")]
    [Arguments(".", "App")]
    public void NamespaceFor_OutputDirectory_AppendsSegments(string outputDirectory, string expected)
    {
        MigrationScaffolder.NamespaceFor("App", outputDirectory).ShouldBe(expected);
    }

    [Test]
    public void Render_Migration_ContainsIdNamespaceAndBase()
    {
        var source = MigrationScaffolder.Render("App.Migrations", "M0019OrdersAddDeletedAt", "0019", "OrdersAddDeletedAt");

        source.ShouldContain("namespace App.Migrations;");
        source.ShouldContain("internal sealed class M0019OrdersAddDeletedAt : CosmosMigration");
        source.ShouldContain("public override string Id => \"0019\";");
    }
}
