using System.Net;
using System.Net.Http.Json;

namespace TemplateAdapter.Adapter.Clients;

/// <summary>
/// Typed HTTP client for the ExternalSystem API. Maps every failure to one of the three
/// <see cref="ExternalSystemException"/> types. It has no retry handler of its own: NimBus retries
/// failed messages as new, audited attempts (see <c>Resilience/</c>).
/// </summary>
public sealed class ExternalSystemClient(HttpClient http) : IExternalSystemClient
{
    /// <summary>The name the client is registered under in <c>IHttpClientFactory</c>.</summary>
    public const string HttpClientName = "ExternalSystem";

    private const string ApiName = "ExternalSystem API";
    private const string AccountsPath = "api/accounts";

    /// <summary>The error code with which the API refuses a write because it owns the record.</summary>
    private const string OwnedErrorCode = "OwnedByExternalSystem";

    /// <inheritdoc />
    public async Task<ExternalSystemAccount?> GetAccountAsync(string accountKey, CancellationToken cancellationToken)
    {
        var operation = $"Read ExternalSystem account '{accountKey}'";
        using var response = await HttpFailure.SendAsync(
            () => http.GetAsync(AccountUri(accountKey), cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await HttpFailure.ThrowOnFailureAsync(response, operation, ApiName, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ExternalSystemAccount>(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UpsertResult> UpsertAccountAsync(ExternalSystemAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var operation = $"Upsert ExternalSystem account '{account.AccountKey}'";
        using var response = await HttpFailure.SendAsync(
            () => http.PutAsJsonAsync(AccountUri(account.AccountKey), account, cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);

        // Ownership is a business outcome only when the API says so explicitly. Real systems also
        // answer 409 (or 412) for concurrency conflicts, and those must fail as rejections, never
        // be skipped silently. See the system pack for how your system signals ownership.
        if (response.StatusCode == HttpStatusCode.Conflict
            && await HttpFailure.ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false) == OwnedErrorCode)
        {
            return UpsertResult.OwnedByExternalSystem;
        }

        await HttpFailure.ThrowOnFailureAsync(response, operation, ApiName, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.Created ? UpsertResult.Created : UpsertResult.Updated;
    }

    private static Uri AccountUri(string accountKey) =>
        new($"{AccountsPath}/{Uri.EscapeDataString(accountKey)}", UriKind.Relative);
}
