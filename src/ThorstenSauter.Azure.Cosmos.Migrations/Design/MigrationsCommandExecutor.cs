using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Design;

/// <summary>
///     Runs migration commands against the migrations in an assembly. This is what <c>dotnet cosmos-migrations</c>
///     calls inside the migrations project's process; it can also back a project's own console host.
/// </summary>
/// <remarks>
///     <para>Commands:</para>
///     <list type="bullet">
///         <item><c>migrate [--target &lt;id&gt;]</c>: apply pending migrations, up to and including the target.</item>
///         <item><c>rollback [--target &lt;id&gt;]</c>: roll back the last migration, or everything after the target.</item>
///         <item><c>status</c>: list applied and pending migrations.</item>
///         <item><c>list</c>: print the known migrations as JSON without connecting.</item>
///     </list>
///     <para>Options, each falling back to an environment variable:</para>
///     <list type="bullet">
///         <item>
///             <c>--connection-string &lt;value&gt;</c> (<c>COSMOS_MIGRATIONS_CONNECTION_STRING</c>), or
///             <c>--connection-name &lt;name&gt;</c> to read <c>ConnectionStrings__&lt;name&gt;</c>, as .NET Aspire sets it.
///         </item>
///         <item><c>--database &lt;name&gt;</c> (<c>COSMOS_MIGRATIONS_DATABASE</c>).</item>
///         <item><c>--container &lt;name&gt;</c>: the records container, <c>migrations</c> by default.</item>
///         <item><c>--lock-timeout &lt;seconds&gt;</c>: how long to wait for another runner's lease.</item>
///         <item><c>--verbose</c>: debug logging.</item>
///     </list>
/// </remarks>
public static class MigrationsCommandExecutor
{
    /// <summary>Environment variable holding the connection string when no option names one.</summary>
    public const string ConnectionStringVariable = "COSMOS_MIGRATIONS_CONNECTION_STRING";

    /// <summary>Environment variable holding the database name when <c>--database</c> is not given.</summary>
    public const string DatabaseVariable = "COSMOS_MIGRATIONS_DATABASE";

    private const int Success = 0;
    private const int Failure = 1;
    private const int UsageError = 2;

    /// <summary>
    ///     Runs the command in <paramref name="args" /> against the migrations in
    ///     <paramref name="migrationsAssembly" />.
    /// </summary>
    /// <param name="migrationsAssembly">The assembly holding the migrations.</param>
    /// <param name="args">The command and its options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The process exit code: 0 on success, 1 on failure, 2 on invalid arguments.</returns>
    public static async Task<int> RunAsync(
        Assembly migrationsAssembly,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrationsAssembly);
        ArgumentNullException.ThrowIfNull(args);

        CommandLine command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageError;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(command.Verbose ? LogLevel.Debug : LogLevel.Information)
            .AddFilter("Azure", LogLevel.Warning)
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.ColorBehavior = LoggerColorBehavior.Default;
            }));

        IReadOnlyList<CosmosMigration> migrations;
        try
        {
            migrations = CosmosMigrationRunner.DiscoverMigrations(migrationsAssembly);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ReflectionTypeLoadException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return Failure;
        }

        if (command.Name == "list")
        {
            await WriteListAsync(migrations, cancellationToken);
            return Success;
        }

        var connectionString = command.ConnectionString;
        var databaseName = command.Database ?? Environment.GetEnvironmentVariable(DatabaseVariable);
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(databaseName))
        {
            await Console.Error.WriteLineAsync(
                "A connection string (--connection-string, --connection-name or " +
                $"{ConnectionStringVariable}) and a database (--database or {DatabaseVariable}) are required.");
            return UsageError;
        }

        var options = new CosmosMigrationOptions
        {
            DatabaseName = databaseName,
            ContainerName = command.Container ?? CosmosMigrationOptions.DefaultContainerName,
            LockTimeout = command.LockTimeout ?? CosmosMigrationOptions.DefaultLockTimeout
        };

        try
        {
            using var client = CosmosConnection.CreateClient(connectionString);
            var runner = new CosmosMigrationRunner(
                client,
                options,
                migrations,
                loggerFactory.CreateLogger<CosmosMigrationRunner>());

            switch (command.Name)
            {
                case "migrate":
                    return (await runner.MigrateAsync(command.Target, cancellationToken)).Succeeded ? Success : Failure;

                case "rollback":
                    return (await runner.RollbackAsync(command.Target, cancellationToken)).Succeeded ? Success : Failure;

                default:
                    WriteStatus(await runner.GetStatusAsync(cancellationToken));
                    return Success;
            }
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return UsageError;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory.CreateLogger(typeof(MigrationsCommandExecutor)).LogRunFailedToStart(ex);
            return Failure;
        }
    }

    private static async Task WriteListAsync(IReadOnlyList<CosmosMigration> migrations, CancellationToken cancellationToken)
    {
        await using var stdout = Console.OpenStandardOutput();
        await using var writer = new Utf8JsonWriter(stdout);
        writer.WriteStartArray();
        foreach (var migration in migrations)
        {
            writer.WriteStartObject();
            writer.WriteString("id", migration.Id);
            writer.WriteString("description", migration.Description);
            writer.WriteString("type", migration.GetType().FullName);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
    }

    private static void WriteStatus(IReadOnlyList<MigrationState> states)
    {
        if (states.Count == 0)
        {
            Console.WriteLine("No migrations found.");
            return;
        }

        var idWidth = Math.Max(2, states.Max(s => s.Id.Length));
        foreach (var state in states)
        {
            var applied = state.AppliedAt is { } at
                ? at.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : "Pending";
            Console.WriteLine($"{state.Id.PadRight(idWidth)}  {applied,-20}  {state.Description}");
        }
    }

    /// <summary>The parsed command line.</summary>
    private sealed record CommandLine(
        string Name,
        string? Target,
        string? ConnectionString,
        string? Database,
        string? Container,
        TimeSpan? LockTimeout,
        bool Verbose)
    {
        private static readonly string[] Commands = ["migrate", "rollback", "status", "list"];

        internal static CommandLine Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 0 || !Commands.Contains(args[0], StringComparer.Ordinal))
            {
                throw new ArgumentException($"Expected a command: {string.Join(", ", Commands)}.");
            }

            string? target = null, connectionString = null, connectionName = null, database = null, container = null;
            TimeSpan? lockTimeout = null;
            var verbose = false;

            for (var i = 1; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "--target":
                        target = Value(args, ref i);
                        break;
                    case "--connection-string":
                        connectionString = Value(args, ref i);
                        break;
                    case "--connection-name":
                        connectionName = Value(args, ref i);
                        break;
                    case "--database":
                        database = Value(args, ref i);
                        break;
                    case "--container":
                        container = Value(args, ref i);
                        break;
                    case "--lock-timeout":
                        lockTimeout = int.TryParse(Value(args, ref i), CultureInfo.InvariantCulture, out var seconds)
                                      && seconds >= 0
                            ? TimeSpan.FromSeconds(seconds)
                            : throw new ArgumentException("--lock-timeout must be a whole number of seconds.");
                        break;
                    case "--verbose":
                        verbose = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            if (target is not null && args[0] is not ("migrate" or "rollback"))
            {
                throw new ArgumentException($"--target does not apply to '{args[0]}'.");
            }

            connectionString ??= connectionName is not null
                ? Environment.GetEnvironmentVariable($"ConnectionStrings__{connectionName}")
                  ?? throw new ArgumentException(
                      $"--connection-name {connectionName}: environment variable ConnectionStrings__{connectionName} is not set.")
                : Environment.GetEnvironmentVariable(ConnectionStringVariable);

            return new CommandLine(args[0], target, connectionString, database, container, lockTimeout, verbose);
        }

        private static string Value(IReadOnlyList<string> args, ref int i) =>
            ++i < args.Count && !args[i].StartsWith("--", StringComparison.Ordinal)
                ? args[i]
                : throw new ArgumentException($"Option '{args[i - 1]}' needs a value.");
    }
}
