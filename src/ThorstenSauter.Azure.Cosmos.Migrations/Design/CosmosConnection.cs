using System.Data.Common;
using System.Net;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Design;

/// <summary>
///     Creates a <see cref="CosmosClient" /> from the connection string forms in common use.
/// </summary>
internal static class CosmosConnection
{
    /// <summary>
    ///     Creates a client for <paramref name="connectionString" />.
    ///     <list type="bullet">
    ///         <item>
    ///             A bare endpoint (<c>https://account.documents.azure.com:443/</c>), or <c>AccountEndpoint=...</c> without
    ///             <c>AccountKey</c>: Microsoft Entra ID via <see cref="DefaultAzureCredential" />, which honours
    ///             <c>AZURE_TOKEN_CREDENTIALS</c>.
    ///         </item>
    ///         <item><c>AccountEndpoint=...;AccountKey=...</c>: key authentication.</item>
    ///     </list>
    ///     A loopback or plain-HTTP endpoint is treated as the emulator: gateway mode, limited to that endpoint (the
    ///     emulator advertises addresses a container port mapping hides), and, for loopback only,
    ///     <c>DisableServerCertificateValidation=True</c> is honoured.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentException">The connection string has no usable endpoint.</exception>
    internal static CosmosClient CreateClient(string connectionString)
    {
        var settings = Parse(connectionString);
        var options = new CosmosClientOptions { ApplicationName = "cosmos-migrations" };

        if (settings.IsEmulator)
        {
            options.ConnectionMode = ConnectionMode.Gateway;
            options.LimitToEndpoint = true;

            if (settings.DisableServerCertificateValidation)
            {
                options.ServerCertificateCustomValidationCallback = static (_, _, _) => true;
            }
        }

        return settings.AccountKey is { } key
            ? new CosmosClient(settings.Endpoint.AbsoluteUri, key, options)
            : new CosmosClient(settings.Endpoint.AbsoluteUri, new DefaultAzureCredential(), options);
    }

    /// <summary>Parses <paramref name="connectionString" /> without creating a client.</summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The parsed settings.</returns>
    /// <exception cref="ArgumentException">The connection string has no usable endpoint.</exception>
    internal static CosmosConnectionSettings Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (Uri.TryCreate(connectionString.Trim(), UriKind.Absolute, out var bare) && IsHttp(bare))
        {
            return new CosmosConnectionSettings(bare, null, IsEmulator(bare), false);
        }

        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The Cosmos DB connection string is malformed.", nameof(connectionString), ex);
        }

        if (!builder.TryGetValue("AccountEndpoint", out var value)
            || !Uri.TryCreate(value?.ToString(), UriKind.Absolute, out var endpoint)
            || !IsHttp(endpoint))
        {
            throw new ArgumentException(
                "The Cosmos DB connection string must be an account endpoint URI or contain AccountEndpoint=<uri>.",
                nameof(connectionString));
        }

        var key = builder.TryGetValue("AccountKey", out var keyValue) && keyValue?.ToString() is { Length: > 0 } k
            ? k
            : null;

        var disableValidation = IsLoopback(endpoint)
                                && builder.TryGetValue("DisableServerCertificateValidation", out var disable)
                                && bool.TryParse(disable?.ToString(), out var parsed)
                                && parsed;

        return new CosmosConnectionSettings(endpoint, key, IsEmulator(endpoint), disableValidation);
    }

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

    private static bool IsEmulator(Uri endpoint) => endpoint.Scheme == Uri.UriSchemeHttp || IsLoopback(endpoint);

    private static bool IsLoopback(Uri endpoint) =>
        endpoint.IsLoopback
        || (IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address));
}

/// <summary>A parsed Cosmos DB connection string.</summary>
/// <param name="Endpoint">The account endpoint.</param>
/// <param name="AccountKey">The account key, or <see langword="null" /> for Microsoft Entra ID.</param>
/// <param name="IsEmulator">Whether the endpoint looks like the local emulator.</param>
/// <param name="DisableServerCertificateValidation">Whether to skip TLS validation (loopback only).</param>
internal sealed record CosmosConnectionSettings(
    Uri Endpoint,
    string? AccountKey,
    bool IsEmulator,
    bool DisableServerCertificateValidation);
