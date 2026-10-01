using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemplateAdapter.Adapter.Clients;

// Business Central online pack snippet (see ../README.md, "Writing (bus -> BC)").
//
// Use it in a generated adapter:
//   1. Copy this file to src/<Name>.Adapter/Clients/.
//   2. In ExternalSystemClientRegistration, register BusinessCentralApiClient instead of the sample
//      client: AddHttpClient<IExternalSystemClient, BusinessCentralApiClient>(BusinessCentralApiClient.HttpClientName, ...).
//      Keep the BearerTokenHandler line: it is how the client authenticates.
//   3. Set ExternalSystem:BaseUrl to the company root of YOUR custom API page:
//        https://api.businesscentral.dynamics.com/v2.0/<tenant-id>/<environment>/api/<publisher>/<group>/<version>/companies(<company-id>)/
//      and ExternalSystem:Scope to https://api.businesscentral.dynamics.com/.default.
//   4. Change CustomersRoute and the JSON field names below to match your API page.

/// <summary>
/// <see cref="IExternalSystemClient"/> for Business Central online. It talks to a CUSTOM API page
/// over the Customer table that exposes an external-id field (added by a table extension), because
/// the standard API v2.0 customer has no field for the upstream id and no <c>systemModifiedBy</c>.
/// <list type="bullet">
///   <item>Read: <c>GET {CustomersRoute}?$filter=externalId eq '...'</c>. Zero rows is "not found";
///   any non-success status throws (a 404 here means a wrong route or company, never "no customer").</item>
///   <item>Create: <c>POST {CustomersRoute}</c>.</item>
///   <item>Update: <c>PATCH {CustomersRoute}({id})</c> with <c>If-Match</c> set to the row's
///   <c>@odata.etag</c>, sending only the fields that changed. An unchanged record is not written
///   at all, so a redelivery or an echo costs one GET and changes nothing.</item>
///   <item>A stale read (412, 409 "another user has already changed the record", or 404 because the
///   row was deleted in between) is re-read and retried ONCE. A second conflict surfaces as
///   <see cref="ExternalSystemRejectedException"/>: the session blocks and an operator resubmits.</item>
///   <item><c>ownedByBusinessCentral</c> (a read-only field your API page computes) turns the
///   upsert into <see cref="UpsertResult.OwnedByExternalSystem"/> without writing. Do NOT map HTTP
///   409 to ownership for BC: in BC, 409 is a concurrency conflict.</item>
/// </list>
/// Every other failure goes through <see cref="HttpFailure"/>, so 429 is Throttled, 408/502/503/504
/// and timeouts are Unavailable and other 4xx are Rejected. There is no retry handler here: NimBus
/// retries failed messages as new, audited attempts.
/// </summary>
public sealed class BusinessCentralApiClient(HttpClient http) : IExternalSystemClient
{
    /// <summary>The name the client is registered under in <c>IHttpClientFactory</c>.</summary>
    public const string HttpClientName = "ExternalSystem";

    /// <summary>
    /// The <c>EntitySetName</c> of your custom API page, relative to <c>ExternalSystem:BaseUrl</c>
    /// (which ends at <c>companies(&lt;company-id&gt;)/</c>). Change it to your page's entity set.
    /// </summary>
    public const string CustomersRoute = "customers";

    /// <summary>The API page field (camelCase) that holds the upstream id. Used in <c>$filter</c>.</summary>
    public const string ExternalIdField = "externalId";

    private const string ApiName = "Business Central API";

    /// <summary>One write plus one re-read-and-retry after a stale read.</summary>
    private const int MaxWriteAttempts = 2;

    // Web defaults: camelCase names and case-insensitive reads, like BC's API JSON. Null values are
    // left out of POST bodies so BC applies its own defaults (templates, number series).
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<ExternalSystemAccount?> GetAccountAsync(string accountKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);

        var operation = $"Read Business Central customer with external id '{accountKey}'";
        var customer = await FindByExternalIdAsync(accountKey, operation, cancellationToken).ConfigureAwait(false);
        return customer is null ? null : ToAccount(customer, accountKey);
    }

    /// <inheritdoc />
    public async Task<UpsertResult> UpsertAccountAsync(ExternalSystemAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.AccountKey);

        var operation = $"Upsert Business Central customer with external id '{account.AccountKey}'";

        for (var attempt = 1; ; attempt++)
        {
            // Read first, every attempt: the write decision (create, skip, ownership, which fields)
            // must be made on fresh data, and the read makes a retried POST safe (see README).
            var existing = await FindByExternalIdAsync(account.AccountKey, operation, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                await CreateAsync(account, operation, cancellationToken).ConfigureAwait(false);
                return UpsertResult.Created;
            }

            if (existing.OwnedByBusinessCentral)
                return UpsertResult.OwnedByExternalSystem;

            var changes = ChangedFields(existing, account);
            if (changes.Count == 0)
                return UpsertResult.Updated; // No-op: nothing is written, so BC raises no change (no echo).

            using var request = new HttpRequestMessage(HttpMethod.Patch, CustomerUri(existing.Id))
            {
                Content = JsonContent.Create(changes, options: JsonOptions),
            };

            // BC requires If-Match on PATCH. The row's ETag makes BC refuse the write if the record
            // changed after our read; "*" (only when the page returns no ETag) skips that check.
            request.Headers.TryAddWithoutValidation("If-Match", existing.ETag ?? "*");

            using var response = await HttpFailure.SendAsync(
                () => http.SendAsync(request, cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return UpsertResult.Updated;

            if (attempt >= MaxWriteAttempts || !IsStaleRead(response.StatusCode))
            {
                await HttpFailure.ThrowOnFailureAsync(response, operation, ApiName, cancellationToken).ConfigureAwait(false);
                throw new UnreachableException("ThrowOnFailureAsync throws for every non-success status.");
            }
        }
    }

    /// <summary>
    /// Finds the customer by external id. Returns <c>null</c> only for an empty result; two or more
    /// rows is a data problem (the field cannot be made unique from a table extension) and throws.
    /// </summary>
    private async Task<BusinessCentralCustomer?> FindByExternalIdAsync(string externalId, string operation, CancellationToken cancellationToken)
    {
        var filter = $"{ExternalIdField} eq '{EscapeODataString(externalId)}'";
        var uri = new Uri($"{CustomersRoute}?$filter={Uri.EscapeDataString(filter)}", UriKind.Relative);

        using var response = await HttpFailure.SendAsync(
            () => http.GetAsync(uri, cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);
        await HttpFailure.ThrowOnFailureAsync(response, operation, ApiName, cancellationToken).ConfigureAwait(false);

        var page = await response.Content.ReadFromJsonAsync<BusinessCentralPage<BusinessCentralCustomer>>(JsonOptions, cancellationToken).ConfigureAwait(false);
        var matches = page?.Value ?? [];

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new ExternalSystemRejectedException(
                $"{operation} failed: {ApiName} has {matches.Count} customers with external id '{externalId}'. " +
                "Merge or clear the duplicates in Business Central, then resubmit this message.",
                (int)response.StatusCode,
                "DuplicateExternalId"),
        };
    }

    private async Task CreateAsync(ExternalSystemAccount account, string operation, CancellationToken cancellationToken)
    {
        var body = new BusinessCentralCustomerCreate(
            account.AccountKey,
            account.Name,
            NullIfEmpty(account.CountryCode),
            NullIfEmpty(account.Email),
            NullIfEmpty(account.ParentAccountKey));

        using var response = await HttpFailure.SendAsync(
            () => http.PostAsJsonAsync(new Uri(CustomersRoute, UriKind.Relative), body, JsonOptions, cancellationToken),
            operation,
            ApiName,
            cancellationToken).ConfigureAwait(false);

        await HttpFailure.ThrowOnFailureAsync(response, operation, ApiName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The fields to PATCH: only those that differ. BC returns empty text fields as <c>""</c>, so
    /// null and empty compare equal, and clearing a field sends <c>""</c>.
    /// </summary>
    private static Dictionary<string, string> ChangedFields(BusinessCentralCustomer existing, ExternalSystemAccount desired)
    {
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);

        AddIfChanged(changes, "displayName", existing.DisplayName, desired.Name);
        AddIfChanged(changes, "country", existing.Country, desired.CountryCode);
        AddIfChanged(changes, "email", existing.Email, desired.Email);
        AddIfChanged(changes, "parentExternalId", existing.ParentExternalId, desired.ParentAccountKey);

        return changes;
    }

    private static void AddIfChanged(Dictionary<string, string> changes, string field, string? current, string? desired)
    {
        if (!string.Equals(current ?? string.Empty, desired ?? string.Empty, StringComparison.Ordinal))
            changes[field] = desired ?? string.Empty;
    }

    /// <summary>
    /// 412 Precondition Failed or 409 Conflict: the ETag no longer matches (Microsoft documents 409 as
    /// "another user has already changed the record"). 404: the row was deleted after the read.
    /// </summary>
    private static bool IsStaleRead(HttpStatusCode status) =>
        status is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound;

    private static ExternalSystemAccount ToAccount(BusinessCentralCustomer customer, string requestedKey) =>
        new(
            customer.ExternalId is { Length: > 0 } key ? key : requestedKey,
            customer.DisplayName ?? string.Empty,
            NullIfEmpty(customer.Country),
            NullIfEmpty(customer.Email),
            NullIfEmpty(customer.ParentExternalId));

    private static Uri CustomerUri(Guid id) => new($"{CustomersRoute}({id:D})", UriKind.Relative);

    /// <summary>OData string literals escape a single quote by doubling it.</summary>
    private static string EscapeODataString(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>An OData collection response: <c>{"value": [...]}</c>.</summary>
/// <typeparam name="T">The entity type.</typeparam>
/// <param name="Value">The rows on this page.</param>
public sealed record BusinessCentralPage<T>(IReadOnlyList<T>? Value);

/// <summary>
/// A customer as YOUR custom API page returns it. Rename the JSON names to your page's field names.
/// </summary>
/// <param name="ETag">The row's <c>@odata.etag</c>; send it back in <c>If-Match</c>.</param>
/// <param name="Id">The record's <c>SystemId</c> (the page's <c>ODataKeyFields</c>).</param>
/// <param name="Number">The customer number (No.).</param>
/// <param name="DisplayName">The customer name.</param>
/// <param name="Country">The Country/Region Code. BC's codes are its own setup data, not guaranteed ISO.</param>
/// <param name="Email">The e-mail address.</param>
/// <param name="ExternalId">The upstream id, stored in a table-extension field.</param>
/// <param name="ParentExternalId">The upstream id of the parent account, stored in a table-extension field.</param>
/// <param name="OwnedByBusinessCentral">Read-only flag your page computes when BC owns the master data.</param>
/// <param name="SystemModifiedBy">Who changed the record last (user security ID). Used for echo filtering.</param>
/// <param name="LastModifiedDateTime">When the record changed last (UTC).</param>
public sealed record BusinessCentralCustomer(
    [property: JsonPropertyName("@odata.etag")] string? ETag,
    Guid Id,
    string? Number,
    string? DisplayName,
    string? Country,
    string? Email,
    string? ExternalId,
    string? ParentExternalId,
    bool OwnedByBusinessCentral,
    Guid? SystemModifiedBy,
    DateTimeOffset? LastModifiedDateTime);

/// <summary>The POST body that creates a customer through the custom API page.</summary>
/// <param name="ExternalId">The upstream id.</param>
/// <param name="DisplayName">The customer name.</param>
/// <param name="Country">The Country/Region Code, or null for BC's default.</param>
/// <param name="Email">The e-mail address, or null.</param>
/// <param name="ParentExternalId">The upstream id of the parent account, or null.</param>
public sealed record BusinessCentralCustomerCreate(
    string ExternalId,
    string DisplayName,
    string? Country,
    string? Email,
    string? ParentExternalId);
