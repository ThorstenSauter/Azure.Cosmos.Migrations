using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Storage;

/// <summary>
///     A lease on the migrations container, held in a single document (<see cref="MigrationStore.LeaseId" />) so that
///     only one runner applies or rolls back migrations at a time.
///     <para>
///         Every write is conditional on the document's ETag, so two runners can never both believe they hold it. The
///         lease carries an expiry instead of relying on container TTL, which keeps existing containers unchanged; a
///         runner that dies leaves the document behind and the next one takes it over once it has expired. Expiry is
///         compared against the local clock, so clocks skewed by more than the lease duration defeat the lock.
///     </para>
/// </summary>
internal sealed class MigrationLease : IAsyncDisposable
{
    private static readonly PartitionKey LeasePartitionKey = new(MigrationStore.LeaseId);

    private readonly DateTimeOffset _acquiredAt;
    private readonly Container _container;
    private readonly CancellationTokenSource _lost = new();
    private readonly ILogger _logger;
    private readonly CosmosMigrationOptions _options;
    private readonly string _owner;
    private readonly Task _renewal;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _timeProvider;
    private string _etag;
    private DateTimeOffset _expiresAt;

    private MigrationLease(
        Container container,
        CosmosMigrationOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        LeaseDocument document,
        string etag)
    {
        _container = container;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _owner = document.Owner;
        _acquiredAt = document.AcquiredAt;
        _expiresAt = document.ExpiresAt;
        _etag = etag;
        _renewal = RenewAsync();
    }

    /// <summary>Gets a token that is cancelled when the lease could not be renewed and may now belong to another runner.</summary>
    internal CancellationToken LostToken => _lost.Token;

    /// <summary>
    ///     Acquires the lease, waiting up to <see cref="CosmosMigrationOptions.LockTimeout" /> while another runner
    ///     holds it.
    /// </summary>
    /// <param name="container">The migrations container.</param>
    /// <param name="options">The runner options.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The held lease; dispose it to release.</returns>
    /// <exception cref="MigrationLockException">Another runner held the lease for longer than the timeout.</exception>
    internal static async Task<MigrationLease> AcquireAsync(
        Container container,
        CosmosMigrationOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var owner = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}";
        var deadline = timeProvider.GetUtcNow() + options.LockTimeout;
        var waitLogged = false;

        while (true)
        {
            var now = timeProvider.GetUtcNow();
            var document = new LeaseDocument(MigrationStore.LeaseId, owner, now, now + options.LeaseDuration);

            using (var content = MigrationStore.Serialize(document, MigrationsJsonContext.Default.LeaseDocument))
            using (var created = await container.CreateItemStreamAsync(
                       content,
                       LeasePartitionKey,
                       cancellationToken: cancellationToken))
            {
                if (created.IsSuccessStatusCode)
                {
                    logger.LogLeaseAcquired(owner);
                    return new MigrationLease(container, options, timeProvider, logger, document, created.Headers.ETag);
                }

                if (created.StatusCode != HttpStatusCode.Conflict)
                {
                    created.EnsureSuccessStatusCode();
                }
            }

            using var read = await container.ReadItemStreamAsync(
                MigrationStore.LeaseId,
                LeasePartitionKey,
                cancellationToken: cancellationToken);

            if (read.StatusCode == HttpStatusCode.NotFound)
            {
                // Released between our create and this read; try again straight away.
                continue;
            }

            read.EnsureSuccessStatusCode();
            var held = await JsonSerializer.DeserializeAsync(
                           read.Content,
                           MigrationsJsonContext.Default.LeaseDocument,
                           cancellationToken)
                       ?? throw new MigrationLockException("The migration lease document is empty.");

            if (held.ExpiresAt <= now)
            {
                logger.LogLeaseTakingOver(held.Owner, held.ExpiresAt);
                using var content = MigrationStore.Serialize(document, MigrationsJsonContext.Default.LeaseDocument);
                using var replaced = await container.ReplaceItemStreamAsync(
                    content,
                    MigrationStore.LeaseId,
                    LeasePartitionKey,
                    new ItemRequestOptions { IfMatchEtag = read.Headers.ETag },
                    cancellationToken);

                if (replaced.IsSuccessStatusCode)
                {
                    logger.LogLeaseAcquired(owner);
                    return new MigrationLease(container, options, timeProvider, logger, document, replaced.Headers.ETag);
                }

                if (replaced.StatusCode is not (HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound))
                {
                    replaced.EnsureSuccessStatusCode();
                }

                // Another runner renewed or took it over first; look again.
                continue;
            }

            if (now >= deadline)
            {
                throw new MigrationLockException(
                    $"Another runner ({held.Owner}) holds the migration lease until {held.ExpiresAt:O}; " +
                    $"gave up after waiting {options.LockTimeout}.");
            }

            if (!waitLogged)
            {
                logger.LogLeaseWaiting(held.Owner, held.ExpiresAt);
                waitLogged = true;
            }

            await Task.Delay(options.LockPollInterval, timeProvider, cancellationToken);
        }
    }

    /// <summary>Stops renewing and deletes the lease document unless it was lost.</summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _renewal;

        if (!_lost.IsCancellationRequested)
        {
            try
            {
                using var deleted = await _container.DeleteItemStreamAsync(
                    MigrationStore.LeaseId,
                    LeasePartitionKey,
                    new ItemRequestOptions { IfMatchEtag = _etag });

                if (deleted.IsSuccessStatusCode)
                {
                    _logger.LogLeaseReleased(_owner);
                }
                else
                {
                    _logger.LogLeaseReleaseFailed(null, (int)deleted.StatusCode);
                }
            }
            catch (CosmosException ex)
            {
                // The lease expires on its own; failing to delete it only delays the next runner.
                _logger.LogLeaseReleaseFailed(ex, (int)ex.StatusCode);
            }
        }

        _stop.Dispose();
        _lost.Dispose();
    }

    private async Task RenewAsync()
    {
        var interval = _options.LeaseDuration / 3;
        try
        {
            while (true)
            {
                await Task.Delay(interval, _timeProvider, _stop.Token);

                var now = _timeProvider.GetUtcNow();
                var document = new LeaseDocument(MigrationStore.LeaseId, _owner, _acquiredAt, now + _options.LeaseDuration);
                using var content = MigrationStore.Serialize(document, MigrationsJsonContext.Default.LeaseDocument);

                HttpStatusCode status;
                try
                {
                    using var renewed = await _container.ReplaceItemStreamAsync(
                        content,
                        MigrationStore.LeaseId,
                        LeasePartitionKey,
                        new ItemRequestOptions { IfMatchEtag = _etag },
                        _stop.Token);

                    if (renewed.IsSuccessStatusCode)
                    {
                        _etag = renewed.Headers.ETag;
                        _expiresAt = document.ExpiresAt;
                        continue;
                    }

                    status = renewed.StatusCode;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status = ex is CosmosException cosmos ? cosmos.StatusCode : HttpStatusCode.ServiceUnavailable;
                }

                // A changed or missing document means someone else has it now. Anything else (throttling, a
                // transient outage) is retried on the next tick while the lease is still ours.
                if (status is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound
                    || _timeProvider.GetUtcNow() + interval >= _expiresAt)
                {
                    _logger.LogLeaseLost(_owner, (int)status);
                    await _lost.CancelAsync();
                    return;
                }

                _logger.LogLeaseRenewalFailed((int)status);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Disposed.
        }
    }
}
