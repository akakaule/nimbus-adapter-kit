using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TemplateAdapter.Adapter.Clients;

// Dynamics 365 Finance & Operations pack snippet (see ../README.md, "Writing (bus -> F&O)").
//
// Use it in a generated adapter:
//   1. Copy this file to src/<Name>.Adapter/Clients/.
//   2. In ExternalSystemClientRegistration, register FinanceOperationsODataClient instead of the sample
//      client: AddHttpClient<IExternalSystemClient, FinanceOperationsODataClient>(FinanceOperationsODataClient.HttpClientName, ...).
//      Keep the BearerTokenHandler line: it is how the client authenticates.
//   3. Set ExternalSystem:BaseUrl to the OData service root, https://<environment>.operations.dynamics.com/data/
//      and ExternalSystem:Scope to https://<environment>.operations.dynamics.com/.default.
//   4. Change the constants below (entity set, company, correlation field, defaults for new records).
//      They are the only F&O-specific names in this file.

/// <summary>
/// <see cref="IExternalSystemClient"/> for Dynamics 365 Finance &amp; Operations over OData data
/// entities. Accounts are customers in <see cref="EntitySet"/>, keyed by <c>dataAreaId</c> and
/// <see cref="KeyField"/>; the upstream id lives in <see cref="CorrelationField"/>, a field you add
/// to the entity, because the customer account number comes from an F&amp;O number sequence.
/// <list type="bullet">
///   <item>Read: <c>GET CustomersV3?$filter={CorrelationField} eq '...' and dataAreaId eq '...'&amp;cross-company=true</c>.
///   Zero rows is "not found". Any failure throws: a failed lookup is never "not found", because the
///   create that would follow is how duplicates are made.</item>
///   <item>Create: <c>POST CustomersV3?cross-company=true</c> with the company, the correlation field
///   and the defaults a new customer needs. F&amp;O assigns the customer account.</item>
///   <item>Update: <c>PATCH CustomersV3(dataAreaId='...',CustomerAccount='...')?cross-company=true</c>
///   with only updatable fields: no key fields, no correlation field. An unchanged record is not
///   written, so a redelivery or an echo costs one GET and fires no change event in F&amp;O.</item>
///   <item>A create that fails with a timeout or a 5xx is safe to retry ONLY because the next attempt
///   looks the record up by <see cref="CorrelationField"/> first, and the create writes that field.
///   For an entity that cannot carry a correlation field, do not let NimBus retry the create; see
///   the README, "Non-idempotent creates".</item>
/// </list>
/// Failures go through <see cref="HttpFailure"/> (429 Throttled, 408/502/503/504 and timeouts
/// Unavailable, other 4xx Rejected), with one F&amp;O addition: a 500 whose body names
/// <see cref="WriteFailureType"/> is a validation failure and is Rejected, not Unavailable (see the
/// README; the status code is unverified). There is no retry handler here: NimBus retries failed
/// messages as new, audited attempts. This client never returns
/// <see cref="UpsertResult.OwnedByExternalSystem"/>: F&amp;O has no ownership status code, so decide
/// ownership from your data before calling it.
/// </summary>
public sealed class FinanceOperationsODataClient(HttpClient http) : IExternalSystemClient
{
    /// <summary>The name the client is registered under in <c>IHttpClientFactory</c>.</summary>
    public const string HttpClientName = "ExternalSystem";

    /// <summary>The OData entity set (public collection name) of the data entity.</summary>
    public const string EntitySet = "CustomersV3";

    /// <summary>
    /// The legal entity (<c>dataAreaId</c>) the adapter writes to. <c>usmf</c> is the Microsoft demo
    /// company; use yours. If one adapter serves several companies, make this a parameter.
    /// </summary>
    public const string Company = "usmf";

    /// <summary>The company key field. Lower-case <c>d</c>: OData property names are case-sensitive.</summary>
    public const string CompanyField = "dataAreaId";

    /// <summary>The entity's own key field next to <see cref="CompanyField"/>.</summary>
    public const string KeyField = "CustomerAccount";

    /// <summary>
    /// The field that holds the upstream id. Not a standard field: add it to the entity with an
    /// extension (and to the table behind it), and keep it unique per company.
    /// </summary>
    public const string CorrelationField = "UpstreamAccountId";

    /// <summary>The account name. <c>OrganizationName</c> is the name of an organization customer.</summary>
    public const string NameField = "OrganizationName";

    /// <summary>The primary e-mail address.</summary>
    public const string EmailField = "PrimaryContactEmail";

    /// <summary>
    /// The field that takes the ISO country code, or empty to not write the country. CustomersV3
    /// has both <c>AddressCountryRegionId</c> (F&amp;O's own country/region id) and
    /// <c>AddressCountryRegionISOCode</c>; check in a sandbox which one your environment accepts.
    /// </summary>
    public const string CountryField = "";

    /// <summary>Customer group for new customers. A new customer needs one; use a group that exists in <see cref="Company"/>.</summary>
    public const string DefaultCustomerGroupId = "10";

    /// <summary>Sales currency for new customers.</summary>
    public const string DefaultCurrencyCode = "USD";

    /// <summary>The inner error type F&amp;O reports when entity validation rejects a write.</summary>
    public const string WriteFailureType = "AxODataWriteException";

    private const string ApiName = "F&O OData";
    private const int MaxBodyChars = 500;

    /// <inheritdoc />
    public async Task<ExternalSystemAccount?> GetAccountAsync(string accountKey, CancellationToken cancellationToken)
    {
        var row = await FindAsync(accountKey, cancellationToken).ConfigureAwait(false);
        return row is null
            ? null
            : new ExternalSystemAccount(accountKey, row.Name ?? string.Empty, row.Country, row.Email, ParentAccountKey: null);
    }

    /// <inheritdoc />
    public async Task<UpsertResult> UpsertAccountAsync(ExternalSystemAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        // CustomersV3 has no parent-account field. If your parent concept is the invoice account,
        // resolve the parent's CustomerAccount with FindAsync and write InvoiceAccount.
        var existing = await FindAsync(account.AccountKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateAsync(account, cancellationToken).ConfigureAwait(false);
            return UpsertResult.Created;
        }

        if (!IsChanged(existing, account))
            return UpsertResult.Updated;

        await UpdateAsync(existing.CustomerAccount, account, cancellationToken).ConfigureAwait(false);
        return UpsertResult.Updated;
    }

    private async Task<CustomerRow?> FindAsync(string accountKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountKey);

        var operation = $"Find F&O customer with {CorrelationField} '{accountKey}'";
        var filter = $"{CorrelationField} eq {Literal(accountKey)} and {CompanyField} eq {Literal(Company)}";
        var select = string.IsNullOrEmpty(CountryField)
            ? $"{CompanyField},{KeyField},{CorrelationField},{NameField},{EmailField}"
            : $"{CompanyField},{KeyField},{CorrelationField},{NameField},{EmailField},{CountryField}";

        // cross-company=true on every call: without it F&O only returns rows from the default company
        // of the user mapped to the app, so the lookup misses and the create duplicates.
        var uri = new Uri(
            $"{EntitySet}?$filter={Uri.EscapeDataString(filter)}&$select={Uri.EscapeDataString(select)}&cross-company=true",
            UriKind.Relative);

        using var response = await HttpFailure.SendAsync(
            () => http.GetAsync(uri, cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);

        // No 404 -> null here: a 404 on a collection means a wrong entity set or URL, not "no customer".
        await ThrowOnFailureAsync(response, operation, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("value", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw NotACollection(response, operation, body);

            return rows.GetArrayLength() switch
            {
                0 => null,
                1 => ReadRow(rows[0], response, operation, body),
                var count => throw new ExternalSystemRejectedException(
                    $"{operation} failed: {count} customers in company '{Company}' carry this {CorrelationField}. " +
                    "It must be unique: merge or correct the duplicates in F&O, then resubmit this message.",
                    (int)response.StatusCode,
                    errorCode: null),
            };
        }
        catch (JsonException)
        {
            throw NotACollection(response, operation, body);
        }
    }

    private async Task CreateAsync(ExternalSystemAccount account, CancellationToken cancellationToken)
    {
        var operation = $"Create F&O customer for {CorrelationField} '{account.AccountKey}'";
        var body = UpdatableFields(account);
        body[CompanyField] = Company;
        body[CorrelationField] = account.AccountKey; // Written on create: it is what makes a retried create safe.
        body["CustomerGroupId"] = DefaultCustomerGroupId;
        body["SalesCurrencyCode"] = DefaultCurrencyCode;

        var uri = new Uri($"{EntitySet}?cross-company=true", UriKind.Relative);
        using var response = await HttpFailure.SendAsync(
            () => http.PostAsJsonAsync(uri, body, cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);
        await ThrowOnFailureAsync(response, operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAsync(string customerAccount, ExternalSystemAccount account, CancellationToken cancellationToken)
    {
        var operation = $"Update F&O customer '{customerAccount}' ({CorrelationField} '{account.AccountKey}')";

        // Only updatable fields. Key fields, the correlation field and read-only (computed) fields
        // make F&O reject the PATCH.
        var body = UpdatableFields(account);
        var uri = new Uri(
            $"{EntitySet}({CompanyField}={KeyLiteral(Company)},{KeyField}={KeyLiteral(customerAccount)})?cross-company=true",
            UriKind.Relative);

        using var response = await HttpFailure.SendAsync(
            () => http.PatchAsJsonAsync(uri, body, cancellationToken), operation, ApiName, cancellationToken).ConfigureAwait(false);
        await ThrowOnFailureAsync(response, operation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The fields the adapter owns. A null upstream value is not sent, so it never clears a value
    /// in F&amp;O; decide per field whether a cleared upstream value should clear F&amp;O too.
    /// </summary>
    private static Dictionary<string, object?> UpdatableFields(ExternalSystemAccount account)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { [NameField] = account.Name };
        if (account.Email is not null)
            fields[EmailField] = account.Email;
        if (!string.IsNullOrEmpty(CountryField) && account.CountryCode is not null)
            fields[CountryField] = account.CountryCode;
        return fields;
    }

    private static bool IsChanged(CustomerRow existing, ExternalSystemAccount account) =>
        !string.Equals(existing.Name, account.Name, StringComparison.Ordinal)
        || (account.Email is not null && !string.Equals(existing.Email, account.Email, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrEmpty(CountryField) && account.CountryCode is not null
            && !string.Equals(existing.Country, account.CountryCode, StringComparison.OrdinalIgnoreCase));

    private static CustomerRow ReadRow(JsonElement row, HttpResponseMessage response, string operation, string body)
    {
        var customerAccount = ReadString(row, KeyField);
        if (string.IsNullOrEmpty(customerAccount))
            throw NotACollection(response, operation, body);

        return new CustomerRow(
            customerAccount,
            ReadString(row, NameField),
            ReadString(row, EmailField),
            string.IsNullOrEmpty(CountryField) ? null : ReadString(row, CountryField));
    }

    private static string? ReadString(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// <see cref="HttpFailure.ThrowOnFailureAsync"/> plus one F&amp;O rule: a 500 whose inner error
    /// type is <see cref="WriteFailureType"/> ("Write failed for table row ... Infolog: ...") is a
    /// data problem. Treated as Unavailable it would be retried for nothing and count towards the
    /// circuit breaker. The status code F&amp;O uses for it is unverified; record one in a sandbox.
    /// </summary>
    private static async Task ThrowOnFailureAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Contains(WriteFailureType, StringComparison.Ordinal))
            {
                var detail = InnerErrorMessage(body) ?? body;
                throw new ExternalSystemRejectedException(
                    HttpFailure.Describe(response, operation, ApiName, Truncate(detail)),
                    (int)response.StatusCode,
                    WriteFailureType);
            }
        }

        await HttpFailure.ThrowOnFailureAsync(response, operation, ApiName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>error.innererror.message</c>: F&amp;O's outer <c>error.message</c> is a generic text, the
    /// inner one carries the Infolog an operator can act on.
    /// </summary>
    private static string? InnerErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("innererror", out var inner)
                && inner.ValueKind == JsonValueKind.Object
                && inner.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExternalSystemRejectedException NotACollection(HttpResponseMessage response, string operation, string body) =>
        new(
            HttpFailure.Describe(response, operation, ApiName, Truncate(body)) + " The response is not the OData collection the adapter expects.",
            (int)response.StatusCode,
            errorCode: null);

    private static string Truncate(string text) => text.Length > MaxBodyChars ? text[..MaxBodyChars] + "…" : text;

    /// <summary>An OData string literal for <c>$filter</c>: single quotes are doubled. The whole filter is URL-encoded afterwards.</summary>
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// An OData string literal inside a key segment, URL-encoded: a key value may contain <c>/</c>,
    /// <c>#</c>, <c>?</c>, <c>&amp;</c> or spaces, which would otherwise change the URL.
    /// </summary>
    private static string KeyLiteral(string value) => "'" + Uri.EscapeDataString(value.Replace("'", "''", StringComparison.Ordinal)) + "'";

    private sealed record CustomerRow(string CustomerAccount, string? Name, string? Email, string? Country);
}
