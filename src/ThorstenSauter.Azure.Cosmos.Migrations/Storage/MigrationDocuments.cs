using System.Text.Json.Serialization;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Storage;

/// <summary>
///     Records an applied migration. The property names are the document format and must not change: <c>id</c>,
///     <c>Description</c> and <c>AppliedAt</c>, in a container partitioned by <c>/id</c>.
/// </summary>
/// <param name="Id">The migration ID.</param>
/// <param name="Description">The migration description at the time it was applied.</param>
/// <param name="AppliedAt">When the migration was applied (UTC).</param>
internal sealed record MigrationRecord(
    [property: JsonPropertyName("id")] string Id,
    string? Description,
    DateTime? AppliedAt);

/// <summary>
///     The lease document that keeps two runners from migrating at the same time.
/// </summary>
/// <param name="Id">Always <see cref="MigrationStore.LeaseId" />.</param>
/// <param name="Owner">Identifies the runner holding the lease.</param>
/// <param name="AcquiredAt">When the current holder acquired the lease.</param>
/// <param name="ExpiresAt">When another runner may take the lease over unless the holder renews it.</param>
internal sealed record LeaseDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("acquiredAt")] DateTimeOffset AcquiredAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>
///     The envelope of a query response read through the stream API.
/// </summary>
/// <param name="Documents">The documents of one page.</param>
internal sealed record QueryPage(IReadOnlyList<MigrationRecord>? Documents);

/// <summary>
///     Serializes the documents this package owns independently of the serializer the <c>CosmosClient</c> is
///     configured with, so a consumer's custom serializer can never change their shape.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(MigrationRecord))]
[JsonSerializable(typeof(LeaseDocument))]
[JsonSerializable(typeof(QueryPage))]
internal sealed partial class MigrationsJsonContext : JsonSerializerContext;
