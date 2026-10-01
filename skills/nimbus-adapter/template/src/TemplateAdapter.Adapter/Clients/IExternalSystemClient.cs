namespace TemplateAdapter.Adapter.Clients;

/// <summary>
/// The ExternalSystem API as the adapter needs it: one method per API call, shaped like the real
/// API. Every failure surfaces as one of the <see cref="ExternalSystemException"/> types, so
/// handlers never see raw HTTP errors and NimBus can tell outages, throttling and rejections apart.
/// Replace the sample account calls with your system's calls (see the system packs for routes).
/// </summary>
public interface IExternalSystemClient
{
    /// <summary>
    /// <c>GET api/accounts/{accountKey}</c>. Returns <c>null</c> when the account does not exist
    /// (404); every other failure throws.
    /// </summary>
    Task<ExternalSystemAccount?> GetAccountAsync(string accountKey, CancellationToken cancellationToken);

    /// <summary>
    /// <c>PUT api/accounts/{accountKey}</c>: creates or updates the account keyed on the upstream id
    /// (an alternate key / external id in the real system), so repeating the call changes nothing.
    /// </summary>
    Task<UpsertResult> UpsertAccountAsync(ExternalSystemAccount account, CancellationToken cancellationToken);
}

/// <summary>An account as ExternalSystem stores it.</summary>
/// <param name="AccountKey">The upstream account id, stored as the account's external key.</param>
/// <param name="Name">The account name.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 country code.</param>
/// <param name="Email">General e-mail address.</param>
/// <param name="ParentAccountKey">External key of the parent account, if any.</param>
public sealed record ExternalSystemAccount(
    string AccountKey,
    string Name,
    string? CountryCode,
    string? Email,
    string? ParentAccountKey);

/// <summary>
/// What an upsert did. <see cref="OwnedByExternalSystem"/> is a business outcome, not a failure:
/// ExternalSystem owns that record now, so the change is deliberately not applied.
/// </summary>
public enum UpsertResult
{
    /// <summary>The account did not exist and was created.</summary>
    Created,

    /// <summary>The account existed and was updated.</summary>
    Updated,

    /// <summary>ExternalSystem owns the account's master data (the API answered 409); nothing was written.</summary>
    OwnedByExternalSystem,
}
