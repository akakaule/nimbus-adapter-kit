using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace TemplateAdapter.Adapter.Clients;

// Dataverse pack snippet (see ../README.md, "Writing (bus -> Dataverse)").
//
// Use it in a generated adapter:
//   1. Copy this file and DataverseErrorCodes.cs to src/<Name>.Adapter/Clients/.
//   2. In ExternalSystemClientRegistration, register DataverseWebApiClient instead of the sample client,
//      and switch the affinity cookie off (Dataverse limits apply per web server):
//        services.AddHttpClient<IExternalSystemClient, DataverseWebApiClient>(DataverseWebApiClient.HttpClientName, ...)
//                .WithoutAffinityCookie();
//      Keep the BearerTokenHandler line: it is how the client authenticates.
//   3. Set ExternalSystem:BaseUrl to https://<org>.crm<n>.dynamics.com/api/data/v9.2/ (trailing slash)
//      and ExternalSystem:Scope to https://<org>.crm<n>.dynamics.com/.default.
//   4. Change the table, key and column names below to your own. new_externalid must be defined as
//      an ALTERNATE KEY on the table (and its index must be Active), or every call returns 400.

/// <summary>
/// <see cref="IExternalSystemClient"/> for the Dataverse Web API, for the <c>account</c> table with an
/// alternate key on <c>new_externalid</c> that holds the upstream account id.
/// <list type="bullet">
///   <item>Read: <c>GET accounts?$filter=new_externalid eq '...'</c> with <c>$select</c> and the parent's
///   key through <c>$expand</c>. A filter, not the key segment <c>accounts(new_externalid='...')</c>,
///   because key segments reject values containing <c>/ &lt; &gt; * % &amp; : \ ? +</c>.</item>
///   <item>Write: <c>PATCH accounts(new_externalid='...')</c> is an upsert: Dataverse creates the row
///   when the key is new and updates it otherwise, so a redelivery changes nothing. The parent is set
///   with <c>parentaccountid@odata.bind</c> to the parent's ALTERNATE KEY, so no GUID lookup is needed.</item>
///   <item><c>Prefer: return=representation</c> turns the 204 into 201 (created) or 200 (updated), at
///   the cost of an extra read on the server; drop it if you do not need the distinction.</item>
///   <item>Errors are classified by <see cref="DataverseErrorCodes"/> first (a 4xx with a "retry later"
///   code is Unavailable), then by <see cref="HttpFailure"/> (429 Throttled with Retry-After, 5xx and
///   timeouts Unavailable, other 4xx Rejected).</item>
/// </list>
/// Dataverse has no "owned elsewhere" answer, so this client never returns
/// <see cref="UpsertResult.OwnedByExternalSystem"/>: read an ownership column in the handler instead.
/// There is no retry handler here: NimBus retries failed messages as new, audited attempts.
/// </summary>
public sealed class DataverseWebApiClient(HttpClient http) : IExternalSystemClient
{
    /// <summary>The name the client is registered under in <c>IHttpClientFactory</c>.</summary>
    public const string HttpClientName = "ExternalSystem";

    private const string ApiName = "Dataverse Web API";
    private const string EntitySet = "accounts";
    private const string KeyColumn = "new_externalid";

    // Only the columns the adapter maps. Never read all columns: it is slower and couples the adapter
    // to every column a maker adds.
    private const string Columns = "$select=name,address1_country,emailaddress1," + KeyColumn
        + "&$expand=parentaccountid($select=" + KeyColumn + ")";

    // Characters Dataverse rejects inside a key segment (Microsoft docs: alternate keys).
    private static readonly SearchValues<char> KeySegmentUnsafe = SearchValues.Create("/<>*%&:\\?+");

    /// <inheritdoc />
    public async Task<ExternalSystemAccount?> GetAccountAsync(string accountKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountKey);

        var operation = $"Read Dataverse account '{accountKey}'";
        var uri = new Uri($"{EntitySet}?{Columns}&$filter={KeyColumn} eq {Literal(accountKey)}", UriKind.Relative);

        using var request = NewRequest(HttpMethod.Get, uri);
        using var response = await HttpFailure.SendAsync(
            () => http.SendAsync(request, cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);
        await ThrowOnFailureAsync(response, operation, cancellationToken).ConfigureAwait(false);

        var page = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        return page?["value"] is JsonArray { Count: > 0 } rows && rows[0] is JsonObject row
            ? new ExternalSystemAccount(
                AccountKey: (string?)row[KeyColumn] ?? accountKey,
                Name: (string?)row["name"] ?? string.Empty,
                CountryCode: (string?)row["address1_country"],
                Email: (string?)row["emailaddress1"],
                ParentAccountKey: (string?)(row["parentaccountid"] as JsonObject)?[KeyColumn])
            : null;
    }

    /// <inheritdoc />
    public async Task<UpsertResult> UpsertAccountAsync(ExternalSystemAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var operation = $"Upsert Dataverse account '{account.AccountKey}'";
        var uri = new Uri($"{KeySegment(account.AccountKey, operation)}?$select=accountid", UriKind.Relative);

        using var response = await PatchAsync(uri, account, updateOnly: false, operation, cancellationToken).ConfigureAwait(false);
        if (await IsDuplicateKeyAsync(response, cancellationToken).ConfigureAwait(false))
            return await RepeatAsUpdateAsync(uri, account, response, operation, cancellationToken).ConfigureAwait(false);

        await ThrowOnFailureAsync(response, operation, cancellationToken).ConfigureAwait(false);
        // 201 = created, 200 = updated. A 204 means the Prefer header was not applied; the write succeeded.
        return response.StatusCode == HttpStatusCode.Created ? UpsertResult.Created : UpsertResult.Updated;
    }

    /// <summary>
    /// An upsert answered "duplicate key". Either another writer inserted the same key between
    /// Dataverse's lookup and its insert (a race: the row exists now), or ANOTHER unique key on the row
    /// (say accountnumber) belongs to a different record (a data conflict). One update-only repeat
    /// (<c>If-Match: *</c>) tells them apart: it succeeds after a race, and returns 404 for a conflict,
    /// which is reported as the original, operator-readable duplicate error (Rejected).
    /// </summary>
    private async Task<UpsertResult> RepeatAsUpdateAsync(
        Uri uri, ExternalSystemAccount account, HttpResponseMessage duplicate, string operation, CancellationToken cancellationToken)
    {
        using var repeat = await PatchAsync(uri, account, updateOnly: true, operation, cancellationToken).ConfigureAwait(false);
        if (repeat.StatusCode == HttpStatusCode.NotFound)
            await ThrowOnFailureAsync(duplicate, operation, cancellationToken).ConfigureAwait(false);

        await ThrowOnFailureAsync(repeat, operation, cancellationToken).ConfigureAwait(false);
        return UpsertResult.Updated;
    }

    private async Task<HttpResponseMessage> PatchAsync(
        Uri uri, ExternalSystemAccount account, bool updateOnly, string operation, CancellationToken cancellationToken)
    {
        // Only the columns the adapter owns. Never send the key column: on an update Dataverse ignores
        // it, on a create it copies the key from the URL. Sending unchanged values still fires every
        // plug-in and flow filtered on those columns, so skip no-op writes in the handler.
        var body = new JsonObject
        {
            ["name"] = account.Name,
            ["address1_country"] = account.CountryCode,
            ["emailaddress1"] = account.Email,
            // A lookup is bound by the related row's URL, here by its alternate key; null clears it.
            ["parentaccountid@odata.bind"] = account.ParentAccountKey is { } parent ? KeySegment(parent, operation) : null,
        };

        using var request = NewRequest(HttpMethod.Patch, uri);
        request.Content = JsonContent.Create(body);
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        if (updateOnly)
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Any); // 404 instead of a create

        return await HttpFailure.SendAsync(
            () => http.SendAsync(request, cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws the exception NimBus should see. A 4xx that Dataverse marks as a platform fault (SQL
    /// timeout, database unavailable) is Unavailable, so NimBus retries it instead of blocking the
    /// session; everything else is classified by status in <see cref="HttpFailure"/>.
    /// </summary>
    private static async Task ThrowOnFailureAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var (code, message) = DataverseErrorCodes.ReadError(body);
        if ((int)response.StatusCode < 500
            && DataverseErrorCodes.Classify((int)response.StatusCode, code, message) == DataverseFailureKind.Transient)
        {
            throw new ExternalSystemUnavailableException(
                HttpFailure.Describe(response, operation, ApiName, body.Length > 500 ? body[..500] + "…" : body));
        }

        await HttpFailure.ThrowOnFailureAsync(response, operation, ApiName, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsDuplicateKeyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return false;

        var (code, message) = DataverseErrorCodes.ReadError(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return DataverseErrorCodes.IsDuplicateKey(code, message);
    }

    /// <summary>The headers Microsoft asks for on every Web API request.</summary>
    private static HttpRequestMessage NewRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.TryAddWithoutValidation("If-None-Match", "null");
        return request;
    }

    /// <summary><c>accounts(new_externalid='...')</c>, the alternate-key address of one row.</summary>
    private static string KeySegment(string key, string operation)
    {
        if (key.AsSpan().ContainsAny(KeySegmentUnsafe))
        {
            throw new ExternalSystemRejectedException(
                $"{operation} failed: the key '{key}' contains a character Dataverse rejects in an alternate-key URL (/ < > * % & : \\ ? +). "
                + "Clean the id in the source system, or look the row up by $filter and write it by its GUID.",
                (int)HttpStatusCode.BadRequest,
                errorCode: null);
        }

        return $"{EntitySet}({KeyColumn}={Literal(key)})";
    }

    /// <summary>
    /// An OData string literal for a URL: single quotes around the value, embedded quotes doubled
    /// (<c>O'Brien</c> becomes <c>'O''Brien'</c>) and the value percent-encoded.
    /// </summary>
    private static string Literal(string value) =>
        "'" + Uri.EscapeDataString(value.Replace("'", "''", StringComparison.Ordinal)) + "'";
}

/// <summary>Dataverse-specific <see cref="IHttpClientBuilder"/> settings.</summary>
public static class DataverseHttpClientBuilderExtensions
{
    /// <summary>
    /// Stops the client from storing the ARRAffinity cookie. With the cookie, every request of a
    /// handler lifetime goes to ONE web server, and service protection limits apply per web server, so
    /// a burst is capped at one server's budget. Production adapters run with affinity off.
    /// </summary>
    public static IHttpClientBuilder WithoutAffinityCookie(this IHttpClientBuilder builder) =>
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false });
}
