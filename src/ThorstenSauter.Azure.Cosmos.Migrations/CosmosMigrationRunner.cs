using System.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThorstenSauter.Azure.Cosmos.Migrations.Storage;

namespace ThorstenSauter.Azure.Cosmos.Migrations;

/// <summary>
///     Applies, rolls back and reports Cosmos DB migrations.
///     <para>
///         Applied migrations are recorded in <see cref="CosmosMigrationOptions.ContainerName" />, one document per
///         migration (<c>id</c>, <c>Description</c>, <c>AppliedAt</c>). Applying and rolling back hold a lease in the
///         same container, so concurrent runs against one database wait for each other instead of interleaving.
///     </para>
/// </summary>
public sealed class CosmosMigrationRunner
{
    private readonly CosmosClient _client;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<CosmosMigration> _migrations;
    private readonly CosmosMigrationOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CosmosMigrationRunner" /> class.
    /// </summary>
    /// <param name="client">The Cosmos DB client.</param>
    /// <param name="options">The database and container to migrate, and the lease settings.</param>
    /// <param name="migrations">The known migrations, in any order; see <see cref="DiscoverMigrations" />.</param>
    /// <param name="logger">The logger, or <see langword="null" /> for none.</param>
    /// <param name="timeProvider">The clock, or <see langword="null" /> for the system clock.</param>
    /// <exception cref="ArgumentException">A migration ID is empty, reserved or used more than once.</exception>
    public CosmosMigrationRunner(
        CosmosClient client,
        CosmosMigrationOptions options,
        IEnumerable<CosmosMigration> migrations,
        ILogger<CosmosMigrationRunner>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseName, nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContainerName, nameof(options));

        _client = client;
        _options = options;
        _logger = logger ?? NullLogger<CosmosMigrationRunner>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _migrations = Order(migrations);
    }

    /// <summary>Gets the known migrations in the order they are applied.</summary>
    public IReadOnlyList<CosmosMigration> Migrations => _migrations;

    /// <summary>
    ///     Creates one instance of every concrete <see cref="CosmosMigration" /> in <paramref name="assembly" />.
    /// </summary>
    /// <param name="assembly">The assembly holding the migrations.</param>
    /// <returns>The migrations, ordered by ID.</returns>
    /// <exception cref="InvalidOperationException">A migration has no parameterless constructor.</exception>
    public static IReadOnlyList<CosmosMigration> DiscoverMigrations(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return Order(
            assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                            && t.IsSubclassOf(typeof(CosmosMigration)))
                .Select(Create));

        static CosmosMigration Create(Type type) =>
            (CosmosMigration?)Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException($"Could not create migration {type.FullName}.");
    }

    /// <summary>
    ///     Applies every pending migration in order, or those up to and including <paramref name="target" />. Stops at
    ///     the first failure; migrations applied before it stay applied.
    /// </summary>
    /// <param name="target">The last migration ID to apply, or <see langword="null" /> to apply all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Which migrations were applied and, on failure, why the run stopped.</returns>
    public async Task<MigrationResult> MigrateAsync(string? target = null, CancellationToken cancellationToken = default)
    {
        if (target is not null)
        {
            _logger.LogMigratingUpTo(target);
        }

        return await RunLockedAsync(
            async (store, lease, token) =>
            {
                var applied = await store.GetAppliedAsync(token);
                var pending = SelectPending(_migrations, applied.Keys.ToHashSet(StringComparer.Ordinal), target);
                if (pending.Count == 0)
                {
                    _logger.LogNoPendingMigrations();
                    return new MigrationResult([]);
                }

                _logger.LogApplyingMigrations(pending.Count);
                List<string> completed = [];
                foreach (var migration in pending)
                {
                    _logger.LogApplyingMigration(migration.Id, migration.Description);
                    try
                    {
                        migration.Initialize(_client, _options.DatabaseName);
                        await migration.UpAsync(token);
                        await store.AddAsync(migration, _timeProvider.GetUtcNow().UtcDateTime, token);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        var error = WhenLeaseLost(lease, ex);
                        _logger.LogMigrationApplyFailed(error, migration.Id);
                        return new MigrationResult(completed, migration.Id, error);
                    }

                    completed.Add(migration.Id);
                    _logger.LogMigrationApplied(migration.Id);
                }

                _logger.LogMigrationsApplied(completed.Count);
                return new MigrationResult(completed);
            },
            cancellationToken);
    }

    /// <summary>
    ///     Rolls back the most recently applied migration, or every applied migration after
    ///     <paramref name="target" /> (exclusive), newest first. Stops at the first failure.
    /// </summary>
    /// <param name="target">
    ///     The migration ID to roll back to, which itself stays applied; <see langword="null" /> rolls back only the
    ///     last one. Any ID ordered before the first migration (e.g. <c>"0000"</c>) rolls back everything.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Which migrations were rolled back and, on failure, why the run stopped.</returns>
    public async Task<MigrationResult> RollbackAsync(string? target = null, CancellationToken cancellationToken = default)
    {
        if (target is not null)
        {
            _logger.LogRollingBackTo(target);
        }

        return await RunLockedAsync(
            async (store, lease, token) =>
            {
                var applied = await store.GetAppliedAsync(token);
                var toRollBack = SelectRollback(_migrations, applied.Keys.ToHashSet(StringComparer.Ordinal), target);
                if (toRollBack.Count == 0)
                {
                    _logger.LogNothingToRollBack();
                    return new MigrationResult([]);
                }

                _logger.LogRollingBackMigrations(toRollBack.Count);
                List<string> completed = [];
                foreach (var migration in toRollBack)
                {
                    _logger.LogRollingBackMigration(migration.Id, migration.Description);
                    try
                    {
                        migration.Initialize(_client, _options.DatabaseName);
                        await migration.DownAsync(token);
                        await store.RemoveAsync(migration.Id, token);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        var error = WhenLeaseLost(lease, ex);
                        _logger.LogMigrationRollBackFailed(error, migration.Id);
                        return new MigrationResult(completed, migration.Id, error);
                    }

                    completed.Add(migration.Id);
                    _logger.LogMigrationRolledBack(migration.Id);
                }

                _logger.LogMigrationsRolledBack(completed.Count);
                return new MigrationResult(completed);
            },
            cancellationToken);
    }

    /// <summary>
    ///     Reports every known migration as applied or pending, followed by any applied migration this runner does not
    ///     know (for example one removed from the code). Takes no lease.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The migrations in order.</returns>
    public async Task<IReadOnlyList<MigrationState>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var store = await MigrationStore.OpenAsync(_client, _options, cancellationToken);
        var applied = await store.GetAppliedAsync(cancellationToken);

        List<MigrationState> states =
        [
            .. _migrations.Select(m => new MigrationState(
                m.Id,
                m.Description,
                applied.TryGetValue(m.Id, out var record) ? record.AppliedAt ?? DateTime.MinValue : null))
        ];

        var known = _migrations.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        states.AddRange(
            applied.Values
                .Where(r => !known.Contains(r.Id))
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => new MigrationState(r.Id, r.Description ?? string.Empty, r.AppliedAt ?? DateTime.MinValue)));

        return states;
    }

    /// <summary>Selects the pending migrations to apply, in order.</summary>
    /// <param name="migrations">All known migrations, ordered.</param>
    /// <param name="applied">The IDs already applied.</param>
    /// <param name="target">The inclusive upper bound, if any.</param>
    /// <returns>The migrations to apply.</returns>
    internal static List<CosmosMigration> SelectPending(
        IReadOnlyList<CosmosMigration> migrations,
        IReadOnlySet<string> applied,
        string? target) =>
    [
        .. migrations
            .Where(m => !applied.Contains(m.Id))
            .Where(m => target is null || string.CompareOrdinal(m.Id, target) <= 0)
    ];

    /// <summary>Selects the applied migrations to roll back, newest first.</summary>
    /// <param name="migrations">All known migrations, ordered.</param>
    /// <param name="applied">The IDs already applied.</param>
    /// <param name="target">The exclusive lower bound, or <see langword="null" /> for only the last one.</param>
    /// <returns>The migrations to roll back.</returns>
    internal static List<CosmosMigration> SelectRollback(
        IReadOnlyList<CosmosMigration> migrations,
        IReadOnlySet<string> applied,
        string? target)
    {
        if (target is null)
        {
            var last = migrations.LastOrDefault(m => applied.Contains(m.Id));
            return last is null ? [] : [last];
        }

        return
        [
            .. migrations
                .Where(m => applied.Contains(m.Id))
                .Where(m => string.CompareOrdinal(m.Id, target) > 0)
                .Reverse()
        ];
    }

    private static List<CosmosMigration> Order(IEnumerable<CosmosMigration> migrations)
    {
        List<CosmosMigration> ordered = [.. migrations.OrderBy(m => m.Id, StringComparer.Ordinal)];

        foreach (var migration in ordered)
        {
            if (string.IsNullOrWhiteSpace(migration.Id))
            {
                throw new ArgumentException($"Migration {migration.GetType().FullName} has an empty ID.", nameof(migrations));
            }

            if (migration.Id == MigrationStore.LeaseId)
            {
                throw new ArgumentException(
                    $"Migration {migration.GetType().FullName} uses the reserved ID \"{MigrationStore.LeaseId}\".",
                    nameof(migrations));
            }
        }

        var duplicates = ordered
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"\"{g.Key}\" ({string.Join(", ", g.Select(m => m.GetType().Name))})")
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new ArgumentException(
                $"Migration IDs must be unique; duplicated: {string.Join("; ", duplicates)}.",
                nameof(migrations));
        }

        return ordered;
    }

    private static Exception WhenLeaseLost(MigrationLease lease, Exception ex) =>
        lease.LostToken.IsCancellationRequested && ex is not MigrationLockException
            ? new MigrationLockException("The migration lease was lost while the run was in progress.", ex)
            : ex;

    private async Task<MigrationResult> RunLockedAsync(
        Func<MigrationStore, MigrationLease, CancellationToken, Task<MigrationResult>> run,
        CancellationToken cancellationToken)
    {
        MigrationStore store;
        MigrationLease lease;
        try
        {
            store = await MigrationStore.OpenAsync(_client, _options, cancellationToken);
            lease = await MigrationLease.AcquireAsync(store.Container, _options, _timeProvider, _logger, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogRunFailedToStart(ex);
            return new MigrationResult([], Error: ex);
        }

        await using (lease)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LostToken);
            try
            {
                return await run(store, lease, linked.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Reading the applied records failed, before any migration ran.
                var error = WhenLeaseLost(lease, ex);
                _logger.LogRunFailedToStart(error);
                return new MigrationResult([], Error: error);
            }
        }
    }
}
