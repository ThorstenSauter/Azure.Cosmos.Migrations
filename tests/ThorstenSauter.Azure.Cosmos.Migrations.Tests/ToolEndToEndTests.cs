using System.Diagnostics;
using System.Text;
using Shouldly;
using ThorstenSauter.Azure.Cosmos.Migrations.Tests.Infrastructure;

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
    public async Task Add_Project_WritesNextMigration()
    {
        // Scaffold into a copy of the sample project so the real one stays untouched.
        var copy = Path.Combine(Path.GetTempPath(), $"cosmos-migrations-add-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(Path.GetDirectoryName(SampleProject)!, copy);
            var project = Path.Combine(copy, "SampleMigrations.csproj");
            RewriteProjectPaths(project);

            var add = await RunToolAsync("add", "itemsAddStock", "--project", project, "--configuration", Configuration);

            add.ExitCode.ShouldBe(0, add.Output);
            var file = Path.Combine(copy, "Migrations", "M0004ItemsAddStock.cs");
            File.Exists(file).ShouldBeTrue(add.Output);
            var source = await File.ReadAllTextAsync(file);
            source.ShouldContain("namespace SampleMigrations.Migrations;");
            source.ShouldContain("public override string Id => \"0004\";");

            // The scaffolded file compiles.
            var build = await RunAsync(["build", project, "--configuration", Configuration, "--nologo"], null);
            build.ExitCode.ShouldBe(0, build.Output);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    private static Task<ProcessResult> RunToolAsync(params string[] arguments) => RunToolAsync(arguments, null);

    private static Task<ProcessResult> RunToolAsync(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment) =>
        RunAsync([ToolPath, .. arguments], environment);

    private static async Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var startInfo = new ProcessStartInfo("dotnet")
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);

        return new ProcessResult(process.ExitCode, await stdout + await stderr);
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

    private sealed record ProcessResult(int ExitCode, string Output);
}
