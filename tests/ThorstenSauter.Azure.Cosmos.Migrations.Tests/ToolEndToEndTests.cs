using System.Diagnostics;
using System.Text;
using Shouldly;
using ThorstenSauter.Azure.Cosmos.Migrations.Tests.Infrastructure;
using ThorstenSauter.Azure.Cosmos.Migrations.Tool;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests;

/// <summary>
///     Runs the built dotnet-cosmos-migrations tool against the SampleMigrations project, the way a consumer would.
/// </summary>
[RequiresEmulator]
[ClassDataSource<CosmosEmulator>(Shared = SharedType.PerTestSession)]
[NotInParallel(nameof(ToolEndToEndTests))]
public class ToolEndToEndTests(CosmosEmulator emulator)
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static readonly string SampleProject =
        Path.Combine(RepositoryRoot, "tests", "SampleMigrations", "SampleMigrations.csproj");

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    // The project reference copies the tool, its runtimeconfig and exec/ next to the tests.
    private static string ToolPath => Path.Combine(AppContext.BaseDirectory, "dotnet-cosmos-migrations.dll");

    [Test]
    public async Task Migrate_Project_AppliesMigrations()
    {
        var database = await emulator.CreateDatabaseAsync();

        var migrate = await RunToolAsync("migrate", "--project", SampleProject, "--configuration", Configuration,
            "--connection-string", emulator.ConnectionString, "--database", database);

        migrate.ExitCode.ShouldBe(0, migrate.Output);
        migrate.Output.ShouldContain("Applied 3 migration(s)");

        var status = await RunToolAsync("status", "--project", SampleProject, "--configuration", Configuration,
            "--connection-string", emulator.ConnectionString, "--database", database);

        status.ExitCode.ShouldBe(0, status.Output);
        status.Output.ShouldContain("0003");
        status.Output.ShouldNotContain("Pending");
    }

    [Test]
    public async Task Migrate_ConnectionNameFromEnvironment_UsesIt()
    {
        var database = await emulator.CreateDatabaseAsync();

        var migrate = await RunToolAsync(
            ["migrate", "--project", SampleProject, "--configuration", Configuration,
             "--connection-name", "cosmos-db", "--database", database, "--target", "0001"],
            new Dictionary<string, string> { ["ConnectionStrings__cosmos-db"] = emulator.ConnectionString });

        migrate.ExitCode.ShouldBe(0, migrate.Output);
        migrate.Output.ShouldContain("Applied 1 migration(s)");
    }

    [Test]
    public async Task Migrate_PublishedAssembly_AppliesMigrations()
    {
        var database = await emulator.CreateDatabaseAsync();
        var publishDirectory = Path.Combine(Path.GetTempPath(), $"cosmos-migrations-publish-{Guid.NewGuid():N}");
        try
        {
            var publish = await RunAsync(
                ["publish", SampleProject, "--configuration", Configuration, "--output", publishDirectory, "--nologo"],
                null);
            publish.ExitCode.ShouldBe(0, publish.Output);

            var migrate = await RunToolAsync("migrate", "--assembly", Path.Combine(publishDirectory, "SampleMigrations.dll"),
                "--connection-string", emulator.ConnectionString, "--database", database);

            migrate.ExitCode.ShouldBe(0, migrate.Output);
            migrate.Output.ShouldContain("Applied 3 migration(s)");
        }
        finally
        {
            Directory.Delete(publishDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Migrate_MissingDatabaseOption_FailsWithUsageError()
    {
        var migrate = await RunToolAsync("migrate", "--project", SampleProject, "--configuration", Configuration,
            "--connection-string", emulator.ConnectionString);

        migrate.ExitCode.ShouldBe(2);
        migrate.Output.ShouldContain("--database");
    }

    [Test]
    public async Task Add_Project_WritesTimestampedMigration()
    {
        // Scaffold into a copy of the sample project so the real one stays untouched.
        var copy = CopySample("add");
        try
        {
            var project = Path.Combine(copy, "SampleMigrations.csproj");

            var add = await RunToolAsync("add", "itemsAddStock", "--project", project, "--configuration", Configuration);

            add.ExitCode.ShouldBe(0, add.Output);
            var file = Directory.GetFiles(Path.Combine(copy, "Migrations"), "*_ItemsAddStock.cs").ShouldHaveSingleItem();
            var id = Path.GetFileNameWithoutExtension(file);
            id.ShouldMatch("^[0-9]{14}_ItemsAddStock$");
            var source = await File.ReadAllTextAsync(file);
            source.ShouldContain("namespace SampleMigrations.Migrations;");
            source.ShouldContain("internal sealed class ItemsAddStock : CosmosMigration");
            source.ShouldContain($"public override string Id => \"{id}\";");

            // The scaffolded file compiles and sorts after the existing numeric IDs.
            var list = await RunToolCapturingStdoutAsync("list", "--project", project, "--configuration", Configuration, "--json");
            list.ExitCode.ShouldBe(0, list.Output);
            MigrationScaffolder.ParseList(list.Output).Select(m => m.Id).ShouldBe(["0001", "0002", "0003", id]);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Test]
    public async Task Remove_LatestNotApplied_DeletesItsFile()
    {
        var database = await emulator.CreateDatabaseAsync();
        var copy = CopySample("remove");
        try
        {
            var project = Path.Combine(copy, "SampleMigrations.csproj");
            var migrate = await RunToolAsync("migrate", "--project", project, "--configuration", Configuration,
                "--connection-string", emulator.ConnectionString, "--database", database, "--target", "0002");
            migrate.ExitCode.ShouldBe(0, migrate.Output);

            var remove = await RunToolAsync("remove", "--project", project, "--configuration", Configuration,
                "--connection-string", emulator.ConnectionString, "--database", database);

            remove.ExitCode.ShouldBe(0, remove.Output);
            File.Exists(Path.Combine(copy, "Migrations", "M0003ItemsSeedSecond.cs")).ShouldBeFalse();
            File.Exists(Path.Combine(copy, "Migrations", "SampleMigrationBase.cs")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Test]
    public async Task Remove_LatestApplied_RefusesAndKeepsFile()
    {
        var database = await emulator.CreateDatabaseAsync();
        var copy = CopySample("remove-applied");
        try
        {
            var project = Path.Combine(copy, "SampleMigrations.csproj");
            var migrate = await RunToolAsync("migrate", "--project", project, "--configuration", Configuration,
                "--connection-string", emulator.ConnectionString, "--database", database);
            migrate.ExitCode.ShouldBe(0, migrate.Output);

            var remove = await RunToolAsync("remove", "--project", project, "--configuration", Configuration,
                "--connection-string", emulator.ConnectionString, "--database", database);

            remove.ExitCode.ShouldBe(2);
            remove.Output.ShouldContain("is applied");
            File.Exists(Path.Combine(copy, "Migrations", "M0003ItemsSeedSecond.cs")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Test]
    public async Task Status_Json_ReportsAppliedAndPending()
    {
        var database = await emulator.CreateDatabaseAsync();
        await RunToolAsync("migrate", "--project", SampleProject, "--configuration", Configuration,
            "--connection-string", emulator.ConnectionString, "--database", database, "--target", "0001");

        var status = await RunToolCapturingStdoutAsync("status", "--project", SampleProject, "--configuration", Configuration,
            "--connection-string", emulator.ConnectionString, "--database", database, "--json");

        status.ExitCode.ShouldBe(0, status.Output);
        Commands.IsApplied(status.Output, "0001").ShouldBeTrue();
        Commands.IsApplied(status.Output, "0002").ShouldBeFalse();
    }

    [Test]
    public async Task Bundle_Project_AppliesMigrationsWithoutTheTool()
    {
        var database = await emulator.CreateDatabaseAsync();
        var outputDirectory = Directory.CreateTempSubdirectory("cosmos-migrations-bundle-").FullName;
        try
        {
            // Framework-dependent keeps the test fast; self-contained only adds the runtime.
            var bundle = await RunToolAsync("bundle", "--project", SampleProject, "--configuration", Configuration,
                "--output", outputDirectory + Path.DirectorySeparatorChar);
            bundle.ExitCode.ShouldBe(0, bundle.Output);

            var executable = Path.Combine(
                outputDirectory,
                MigrationBundle.ExecutableName(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
            File.Exists(executable).ShouldBeTrue(bundle.Output);

            var migrate = await RunExecutableAsync(
                executable,
                ["migrate", "--connection-string", emulator.ConnectionString, "--database", database]);

            migrate.ExitCode.ShouldBe(0, migrate.Output);
            migrate.Output.ShouldContain("Applied 3 migration(s)");
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static string CopySample(string purpose)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"cosmos-migrations-{purpose}-{Guid.NewGuid():N}");
        CopyDirectory(Path.GetDirectoryName(SampleProject)!, copy);
        RewriteProjectPaths(Path.Combine(copy, "SampleMigrations.csproj"));
        return copy;
    }

    private static Task<ProcessResult> RunToolAsync(params string[] arguments) => RunToolAsync(arguments, null);

    private static Task<ProcessResult> RunToolAsync(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment) =>
        RunAsync([ToolPath, .. arguments], environment);

    /// <summary>Runs the tool and returns only its standard output as <see cref="ProcessResult.Output" />.</summary>
    private static async Task<ProcessResult> RunToolCapturingStdoutAsync(params string[] arguments)
    {
        var result = await RunExecutableAsync("dotnet", [ToolPath, .. arguments], null);
        return result with { Output = result.Stdout };
    }

    private static Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment) =>
        RunExecutableAsync("dotnet", arguments, environment);

    private static async Task<ProcessResult> RunExecutableAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Keep the test runner's own settings (and any connection string a developer has set) out of the tool.
        startInfo.Environment.Remove("COSMOS_MIGRATIONS_CONNECTION_STRING");
        startInfo.Environment.Remove("COSMOS_MIGRATIONS_DATABASE");
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);

        var output = await stdout;
        return new ProcessResult(process.ExitCode, output + await stderr, output);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Azure.Cosmos.Migrations.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.Ordinal)
                || relative.StartsWith("obj", StringComparison.Ordinal)
                || relative == "packages.lock.json")
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        // The copy lives outside the repository, so it needs the repository's package versions and SDK, but not its
        // locked restore: the copy is throwaway.
        foreach (var name in new[] { "Directory.Packages.props", "NuGet.config", "global.json" })
        {
            File.Copy(Path.Combine(RepositoryRoot, name), Path.Combine(destination, name));
        }

        File.WriteAllText(
            Path.Combine(destination, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
            </Project>
            """);
    }

    private static void RewriteProjectPaths(string project)
    {
        // Relative paths to the library point into the repository from the original location only.
        var library = Path.Combine(RepositoryRoot, "src", "ThorstenSauter.Azure.Cosmos.Migrations");
        var text = File.ReadAllText(project).Replace("../../src/ThorstenSauter.Azure.Cosmos.Migrations", library, StringComparison.Ordinal);
        File.WriteAllText(project, text);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Stdout);
}
