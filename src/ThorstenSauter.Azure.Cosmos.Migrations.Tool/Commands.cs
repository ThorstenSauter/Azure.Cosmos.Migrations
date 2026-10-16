using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tool;

/// <summary>
///     The <c>dotnet cosmos-migrations</c> commands.
/// </summary>
internal sealed class Commands
{
    private const string ConnectionStringVariable = "COSMOS_MIGRATIONS_CONNECTION_STRING";
    private const int Failure = 1;
    private const int UsageError = 2;

    /// <summary>Apply pending migrations, or those up to and including a target.</summary>
    /// <param name="target">The last migration to apply: its ID, or an EF-style ID's timestamp or name.</param>
    /// <param name="project">The project holding the migrations (a .csproj or its directory); the current directory by default.</param>
    /// <param name="assembly">A prebuilt or published migrations assembly, instead of building a project.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="noBuild">Use the existing build output of the project.</param>
    /// <param name="connectionString">The Cosmos DB connection string or account endpoint; also read from COSMOS_MIGRATIONS_CONNECTION_STRING.</param>
    /// <param name="connectionName">Read the connection string from ConnectionStrings__&lt;name&gt;, as .NET Aspire sets it.</param>
    /// <param name="database">The database to migrate; also read from COSMOS_MIGRATIONS_DATABASE.</param>
    /// <param name="container">The container recording applied migrations.</param>
    /// <param name="lockTimeout">Seconds to wait for another runner's lease.</param>
    /// <param name="verbose">Log debug output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    public Task<int> Migrate(
        string? target = null,
        string? project = null,
        string? assembly = null,
        string configuration = "Debug",
        bool noBuild = false,
        string? connectionString = null,
        string? connectionName = null,
        string? database = null,
        string? container = null,
        int? lockTimeout = null,
        bool verbose = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            "migrate",
            target,
            new TargetOptions(project, assembly, configuration, noBuild),
            new ConnectionOptions(connectionString, connectionName, database, container, lockTimeout, verbose),
            cancellationToken);

    /// <summary>Roll back the last applied migration, or every applied migration after a target.</summary>
    /// <param name="target">The migration to roll back to, which stays applied: its ID, or an EF-style ID's timestamp or name.</param>
    /// <param name="project">The project holding the migrations (a .csproj or its directory); the current directory by default.</param>
    /// <param name="assembly">A prebuilt or published migrations assembly, instead of building a project.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="noBuild">Use the existing build output of the project.</param>
    /// <param name="connectionString">The Cosmos DB connection string or account endpoint; also read from COSMOS_MIGRATIONS_CONNECTION_STRING.</param>
    /// <param name="connectionName">Read the connection string from ConnectionStrings__&lt;name&gt;, as .NET Aspire sets it.</param>
    /// <param name="database">The database to migrate; also read from COSMOS_MIGRATIONS_DATABASE.</param>
    /// <param name="container">The container recording applied migrations.</param>
    /// <param name="lockTimeout">Seconds to wait for another runner's lease.</param>
    /// <param name="verbose">Log debug output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    public Task<int> Rollback(
        string? target = null,
        string? project = null,
        string? assembly = null,
        string configuration = "Debug",
        bool noBuild = false,
        string? connectionString = null,
        string? connectionName = null,
        string? database = null,
        string? container = null,
        int? lockTimeout = null,
        bool verbose = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            "rollback",
            target,
            new TargetOptions(project, assembly, configuration, noBuild),
            new ConnectionOptions(connectionString, connectionName, database, container, lockTimeout, verbose),
            cancellationToken);

    /// <summary>List the migrations and whether each is applied.</summary>
    /// <param name="project">The project holding the migrations (a .csproj or its directory); the current directory by default.</param>
    /// <param name="assembly">A prebuilt or published migrations assembly, instead of building a project.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="noBuild">Use the existing build output of the project.</param>
    /// <param name="connectionString">The Cosmos DB connection string or account endpoint; also read from COSMOS_MIGRATIONS_CONNECTION_STRING.</param>
    /// <param name="connectionName">Read the connection string from ConnectionStrings__&lt;name&gt;, as .NET Aspire sets it.</param>
    /// <param name="database">The database to migrate; also read from COSMOS_MIGRATIONS_DATABASE.</param>
    /// <param name="container">The container recording applied migrations.</param>
    /// <param name="json">Print JSON (id, description, appliedAt) instead of a table.</param>
    /// <param name="verbose">Log debug output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    public Task<int> Status(
        string? project = null,
        string? assembly = null,
        string configuration = "Debug",
        bool noBuild = false,
        string? connectionString = null,
        string? connectionName = null,
        string? database = null,
        string? container = null,
        bool json = false,
        bool verbose = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            "status",
            null,
            new TargetOptions(project, assembly, configuration, noBuild),
            new ConnectionOptions(connectionString, connectionName, database, container, null, verbose, json),
            cancellationToken);

    /// <summary>List the migrations in the project, without connecting to a database.</summary>
    /// <param name="project">The project holding the migrations (a .csproj or its directory); the current directory by default.</param>
    /// <param name="assembly">A prebuilt or published migrations assembly, instead of building a project.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="noBuild">Use the existing build output of the project.</param>
    /// <param name="json">Print JSON (id, description, type) instead of a table.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> List(
        string? project = null,
        string? assembly = null,
        string configuration = "Debug",
        bool noBuild = false,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await MigrationsTarget.ResolveAsync(project, assembly, configuration, noBuild, cancellationToken);
            var list = await ListAsync(target, cancellationToken);
            if (json)
            {
                Console.WriteLine(list.Json);
                return 0;
            }

            if (list.Migrations.Count == 0)
            {
                Console.WriteLine("No migrations found.");
                return 0;
            }

            var idWidth = list.Migrations.Max(m => m.Id.Length);
            foreach (var migration in list.Migrations)
            {
                Console.WriteLine($"{migration.Id.PadRight(idWidth)}  {migration.Type}");
            }

            return 0;
        }
        catch (ToolException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageError;
        }
    }

    /// <summary>Add a new migration with an EF-style ID (yyyyMMddHHmmss_Name).</summary>
    /// <param name="name">The migration name, e.g. OrdersAddDeletedAt; it becomes the class name.</param>
    /// <param name="project">The project holding the migrations (a .csproj or its directory); the current directory by default.</param>
    /// <param name="outputDir">The folder, relative to the project, to write the migration to; next to the latest migration (or Migrations) by default.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="id">Use this ID instead of a timestamp-based one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> Add(
        [Argument] string name,
        string? project = null,
        string? outputDir = null,
        string configuration = "Debug",
        string? id = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var className = MigrationScaffolder.NormalizeName(name);
            var target = await MigrationsTarget.ResolveAsync(project, null, configuration, false, cancellationToken);
            var existing = (await ListAsync(target, cancellationToken)).Migrations;
            var existingIds = existing.Select(m => m.Id).ToList();

            var newId = id ?? MigrationScaffolder.CreateId(DateTimeOffset.UtcNow, className, existingIds);
            if (existingIds.Contains(newId, StringComparer.Ordinal))
            {
                throw new ToolException($"A migration with ID \"{newId}\" already exists.");
            }

            if (existing.FirstOrDefault(m => MigrationScaffolder.ClassNameOf(m.Type) == className) is { } clash)
            {
                throw new ToolException($"A migration class named {className} already exists ({clash.Id}); choose another name.");
            }

            var (relativeDirectory, namespaceName) = MigrationScaffolder.ChooseLocation(
                outputDir,
                target.RootNamespace ?? Path.GetFileNameWithoutExtension(target.AssemblyPath),
                existing);
            var directory = Path.GetFullPath(Path.Combine(target.ProjectDirectory!, relativeDirectory));
            var path = Path.Combine(directory, newId + ".cs");
            if (File.Exists(path))
            {
                throw new ToolException($"'{path}' already exists.");
            }

            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                path,
                MigrationScaffolder.Render(namespaceName, className, newId, className),
                cancellationToken);

            Console.WriteLine($"Added {newId}: {path}");
            return 0;
        }
        catch (ToolException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageError;
        }
    }

    /// <summary>Remove the latest migration's source file, after checking it isn't applied to the database.</summary>
    /// <param name="project">The project holding the migrations (a .csproj or its directory); the current directory by default.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="force">Remove without checking the database (e.g. offline, or after rolling it back elsewhere).</param>
    /// <param name="connectionString">The Cosmos DB connection string or account endpoint; also read from COSMOS_MIGRATIONS_CONNECTION_STRING.</param>
    /// <param name="connectionName">Read the connection string from ConnectionStrings__&lt;name&gt;, as .NET Aspire sets it.</param>
    /// <param name="database">The database to check; also read from COSMOS_MIGRATIONS_DATABASE.</param>
    /// <param name="container">The container recording applied migrations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> Remove(
        string? project = null,
        string configuration = "Debug",
        bool force = false,
        string? connectionString = null,
        string? connectionName = null,
        string? database = null,
        string? container = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await MigrationsTarget.ResolveAsync(project, null, configuration, false, cancellationToken);
            var latest = (await ListAsync(target, cancellationToken)).Migrations.LastOrDefault()
                         ?? throw new ToolException("There are no migrations to remove.");

            if (!force)
            {
                var connection = new ConnectionOptions(connectionString, connectionName, database, container, null, false, true);
                var status = await DotnetCli.CaptureAsync(
                    target.ExecArguments(InnerArguments("status", null, connection)),
                    cancellationToken,
                    InnerEnvironment(connection));

                if (status.ExitCode != 0)
                {
                    await Console.Error.WriteLineAsync(status.Error);
                    throw new ToolException(
                        "Could not check whether the migration is applied. Pass the connection options, or --force to " +
                        "remove it without checking.");
                }

                if (IsApplied(status.Output, latest.Id))
                {
                    throw new ToolException(
                        $"{latest.Id} is applied to the database. Roll it back first " +
                        "(dotnet cosmos-migrations rollback), or pass --force to remove only the file.");
                }
            }

            var files = MigrationScaffolder.FindSourceFiles(target.ProjectDirectory!, latest);
            switch (files.Count)
            {
                case 0:
                    throw new ToolException(
                        $"No file named {latest.Id}.cs or {MigrationScaffolder.ClassNameOf(latest.Type)}.cs found for " +
                        $"{latest.Type}; delete it by hand.");
                case > 1:
                    throw new ToolException($"Several files could hold {latest.Id}: {string.Join(", ", files)}; delete it by hand.");
            }

            File.Delete(files[0]);
            Console.WriteLine($"Removed {latest.Id}: {files[0]}");
            return 0;
        }
        catch (ToolException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageError;
        }
    }

    /// <summary>Build a single executable that applies the migrations without the .NET SDK or this tool.</summary>
    /// <param name="project">The project holding the migrations (a .csproj or its directory); the current directory by default.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="runtime">The target runtime identifier, e.g. linux-x64 or linux-arm64; the current one by default.</param>
    /// <param name="selfContained">Include the .NET runtime, so the target machine needs none.</param>
    /// <param name="output">The output file or directory; cosmos-migrations-bundle in the current directory by default.</param>
    /// <param name="force">Overwrite an existing bundle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> Bundle(
        string? project = null,
        string configuration = "Release",
        string? runtime = null,
        bool selfContained = false,
        string? output = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = await MigrationBundle.BuildAsync(
                project,
                configuration,
                runtime,
                selfContained,
                output,
                force,
                cancellationToken);

            Console.WriteLine($"Built {path}");
            Console.WriteLine("Run it with the same commands and options, e.g.: migrate --connection-string <value> --database <name>");
            return 0;
        }
        catch (ToolException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return Failure;
        }
    }

    /// <summary>Reads the migrations from the project through the executable's <c>list</c>.</summary>
    /// <param name="target">The migrations assembly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw JSON and the parsed migrations.</returns>
    /// <exception cref="ToolException">Listing failed.</exception>
    private static async Task<(string Json, List<ListedMigration> Migrations)> ListAsync(
        MigrationsTarget target,
        CancellationToken cancellationToken)
    {
        var list = await DotnetCli.CaptureAsync(target.ExecArguments(["list"]), cancellationToken);
        if (list.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync(list.Error);
            throw new ToolException("Could not list the migrations.");
        }

        return (list.Output.Trim(), MigrationScaffolder.ParseList(list.Output));
    }

    /// <summary>Whether <c>status --json</c> output reports <paramref name="id" /> as applied.</summary>
    /// <param name="statusJson">The JSON array.</param>
    /// <param name="id">The migration ID.</param>
    /// <returns><see langword="true" /> if it is applied.</returns>
    internal static bool IsApplied(string statusJson, string id)
    {
        using var document = JsonDocument.Parse(statusJson);
        return document.RootElement.EnumerateArray().Any(e =>
            e.GetProperty("id").GetString() == id && e.GetProperty("appliedAt").ValueKind != JsonValueKind.Null);
    }

    private static async Task<int> RunAsync(
        string command,
        string? target,
        TargetOptions targetOptions,
        ConnectionOptions connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var migrations = await MigrationsTarget.ResolveAsync(
                targetOptions.Project,
                targetOptions.Assembly,
                targetOptions.Configuration,
                targetOptions.NoBuild,
                cancellationToken);

            return await DotnetCli.RunAsync(
                migrations.ExecArguments(InnerArguments(command, target, connection)),
                InnerEnvironment(connection));
        }
        catch (ToolException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageError;
        }
    }

    private static List<string> InnerArguments(string command, string? target, ConnectionOptions connection)
    {
        List<string> inner = [command];
        AddOption(inner, "--target", target);
        AddOption(inner, "--connection-name", connection.ConnectionName);
        AddOption(inner, "--database", connection.Database);
        AddOption(inner, "--container", connection.Container);
        AddOption(inner, "--lock-timeout", connection.LockTimeout?.ToString(CultureInfo.InvariantCulture));
        if (connection.Json)
        {
            inner.Add("--json");
        }

        if (connection.Verbose)
        {
            inner.Add("--verbose");
        }

        return inner;
    }

    // The connection string is handed over in the environment rather than on the child's command line, where it would
    // show in process listings.
    private static Dictionary<string, string> InnerEnvironment(ConnectionOptions connection) =>
        connection.ConnectionString is { } connectionString
            ? new Dictionary<string, string> { [ConnectionStringVariable] = connectionString }
            : [];

    private static void AddOption(List<string> arguments, string name, string? value)
    {
        if (value is not null)
        {
            arguments.Add(name);
            arguments.Add(value);
        }
    }

    private sealed record TargetOptions(string? Project, string? Assembly, string Configuration, bool NoBuild);

    private sealed record ConnectionOptions(
        string? ConnectionString,
        string? ConnectionName,
        string? Database,
        string? Container,
        int? LockTimeout,
        bool Verbose,
        bool Json = false);
}
