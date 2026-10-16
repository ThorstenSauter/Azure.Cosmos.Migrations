using ConsoleAppFramework;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tool;

/// <summary>
///     The <c>dotnet cosmos-migrations</c> commands.
/// </summary>
internal sealed class Commands
{
    private const string ConnectionStringVariable = "COSMOS_MIGRATIONS_CONNECTION_STRING";
    private const int UsageError = 2;

    /// <summary>Apply pending migrations, or those up to and including a target.</summary>
    /// <param name="target">The last migration ID to apply.</param>
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
    /// <param name="target">The migration ID to roll back to; it stays applied.</param>
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
        bool verbose = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            "status",
            null,
            new TargetOptions(project, assembly, configuration, noBuild),
            new ConnectionOptions(connectionString, connectionName, database, container, null, verbose),
            cancellationToken);

    /// <summary>Add a new migration class with the next ID.</summary>
    /// <param name="name">The migration name, e.g. OrdersAddDeletedAt; the class becomes M&lt;id&gt;&lt;name&gt;.</param>
    /// <param name="project">The project holding the migrations (a .csproj or its directory); the current directory by default.</param>
    /// <param name="outputDir">The folder, relative to the project, to write the migration to.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="id">Use this ID instead of the next number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> Add(
        [Argument] string name,
        string? project = null,
        string outputDir = "Migrations",
        string configuration = "Debug",
        string? id = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedName = MigrationScaffolder.NormalizeName(name);
            var target = await MigrationsTarget.ResolveAsync(project, null, configuration, false, cancellationToken);

            var list = await DotnetCli.CaptureAsync(target.ExecArguments(["list"]), cancellationToken);
            if (list.ExitCode != 0)
            {
                await Console.Error.WriteLineAsync(list.Error);
                return list.ExitCode;
            }

            var existing = MigrationScaffolder.ParseIds(list.Output);
            var newId = id ?? MigrationScaffolder.NextId(existing);
            if (existing.Contains(newId, StringComparer.Ordinal))
            {
                throw new ToolException($"A migration with ID \"{newId}\" already exists.");
            }

            var className = MigrationScaffolder.NormalizeName($"M{newId}{normalizedName}");
            var directory = Path.Combine(target.ProjectDirectory!, outputDir);
            var path = Path.Combine(directory, className + ".cs");
            if (File.Exists(path))
            {
                throw new ToolException($"'{path}' already exists.");
            }

            var namespaceName = MigrationScaffolder.NamespaceFor(target.RootNamespace ?? "Migrations", outputDir);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                path,
                MigrationScaffolder.Render(namespaceName, className, newId, normalizedName),
                cancellationToken);

            Console.WriteLine($"Added {className} ({newId}): {path}");
            return 0;
        }
        catch (ToolException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageError;
        }
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

            List<string> inner = [command];
            AddOption(inner, "--target", target);
            AddOption(inner, "--connection-name", connection.ConnectionName);
            AddOption(inner, "--database", connection.Database);
            AddOption(inner, "--container", connection.Container);
            AddOption(inner, "--lock-timeout", connection.LockTimeout?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (connection.Verbose)
            {
                inner.Add("--verbose");
            }

            // Handed over in the environment rather than on the child's command line, where it would show in
            // process listings.
            Dictionary<string, string> environment = [];
            if (connection.ConnectionString is not null)
            {
                environment[ConnectionStringVariable] = connection.ConnectionString;
            }

            return await DotnetCli.RunAsync(migrations.ExecArguments(inner), environment);
        }
        catch (ToolException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageError;
        }
    }

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
        bool Verbose);
}
