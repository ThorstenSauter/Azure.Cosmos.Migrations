using Shouldly;
using ThorstenSauter.Azure.Cosmos.Migrations.Tool;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests;

public class MigrationScaffolderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 21, 30, 45, TimeSpan.FromHours(2));

    [Test]
    public void CreateId_NoMigrations_IsUtcTimestampAndName()
    {
        MigrationScaffolder.CreateId(Now, "OrdersAddDeletedAt", []).ShouldBe("20261016193045_OrdersAddDeletedAt");
    }

    [Test]
    public void CreateId_NumericLegacyIds_SortsAfterThem()
    {
        var id = MigrationScaffolder.CreateId(Now, "OrdersAddDeletedAt", ["0001", "0018"]);

        string.CompareOrdinal(id, "0018").ShouldBeGreaterThan(0);
    }

    [Test]
    public void CreateId_LaterExistingId_Throws()
    {
        Should.Throw<ToolException>(() =>
            MigrationScaffolder.CreateId(Now, "OrdersAddDeletedAt", ["20261016193046_Newer"]));
    }

    [Test]
    [Arguments("App.Migrations.M0001OrdersAdd", "M0001OrdersAdd")]
    [Arguments("App.OrdersAdd", "OrdersAdd")]
    [Arguments("NoNamespace", "NoNamespace")]
    public void ClassNameOf_FullTypeName_ReturnsClassName(string fullName, string expected)
    {
        MigrationScaffolder.ClassNameOf(fullName).ShouldBe(expected);
    }

    [Test]
    public void FindSourceFiles_ByIdOrClassName_SkipsBinAndObj()
    {
        var root = Directory.CreateTempSubdirectory("cosmos-migrations-find-").FullName;
        try
        {
            Touch(root, "20261016193045_OrdersAdd.cs");
            Touch(root, "Old", "M0001Legacy.cs");
            Touch(root, "obj", "20261016193045_OrdersAdd.cs");
            Touch(root, "bin", "Debug", "M0001Legacy.cs");

            MigrationScaffolder.FindSourceFiles(root, new ListedMigration("20261016193045_OrdersAdd", "App.OrdersAdd"))
                .ShouldBe([Path.Combine(root, "20261016193045_OrdersAdd.cs")]);
            MigrationScaffolder.FindSourceFiles(root, new ListedMigration("0001", "App.Old.M0001Legacy"))
                .ShouldBe([Path.Combine(root, "Old", "M0001Legacy.cs")]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        var source = MigrationScaffolder.Render(
            "App.Migrations",
            "OrdersAddDeletedAt",
            "20261016193045_OrdersAddDeletedAt",
            "OrdersAddDeletedAt");

        source.ShouldContain("namespace App.Migrations;");
        source.ShouldContain("internal sealed class OrdersAddDeletedAt : CosmosMigration");
        source.ShouldContain("public override string Id => \"20261016193045_OrdersAddDeletedAt\";");
    }

    [Test]
    public void IsApplied_StatusJson_ReadsAppliedAt()
    {
        const string Json = """[{"id":"0001","description":"a","appliedAt":"2026-01-01T00:00:00Z"},{"id":"0002","description":"b","appliedAt":null}]""";

        Commands.IsApplied(Json, "0001").ShouldBeTrue();
        Commands.IsApplied(Json, "0002").ShouldBeFalse();
        Commands.IsApplied(Json, "0003").ShouldBeFalse();
    }

    [Test]
    public void RenderProject_Bundle_ReferencesMigrationsAsSingleFile()
    {
        var project = MigrationBundle.RenderProject(
            "net11.0",
            @"C:\src\App.Migrations\App.Migrations.csproj",
            "linux-arm64",
            selfContained: true);

        project.ShouldContain("<TargetFramework>net11.0</TargetFramework>");
        project.ShouldContain("<PublishSingleFile>true</PublishSingleFile>");
        project.ShouldContain("<RuntimeIdentifier>linux-arm64</RuntimeIdentifier>");
        project.ShouldContain("<SelfContained>true</SelfContained>");
        project.ShouldContain(@"<ProjectReference Include=""C:\src\App.Migrations\App.Migrations.csproj"" />");
    }

    [Test]
    [Arguments("win-x64", "cosmos-migrations-bundle.exe")]
    [Arguments("linux-arm64", "cosmos-migrations-bundle")]
    public void ExecutableName_Runtime_AddsExeOnWindowsOnly(string runtime, string expected)
    {
        MigrationBundle.ExecutableName(runtime).ShouldBe(expected);
    }

    private static void Touch(params string[] segments)
    {
        var path = Path.Combine(segments);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }
}
