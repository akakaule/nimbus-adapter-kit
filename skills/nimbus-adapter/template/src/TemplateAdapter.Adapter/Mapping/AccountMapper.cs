using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Contracts.Upstream;

namespace TemplateAdapter.Adapter.Mapping;

/// <summary>
/// Pure mapping between the catalog event and ExternalSystem's shape: no I/O, no clock, no
/// configuration, so every rule is unit-testable. Lookups (owners, parents, option-set values)
/// are resolved by the handler before mapping and passed in.
/// </summary>
public static class AccountMapper
{
    /// <summary>Maps an upstream account to the ExternalSystem account keyed on the upstream id.</summary>
    public static ExternalSystemAccount ToExternalSystem(UpstreamAccountUpdated message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new ExternalSystemAccount(
            AccountKey: message.AccountId,
            Name: message.Name.Trim(),
            CountryCode: message.CountryCode?.Trim().ToUpperInvariant(),
            Email: string.IsNullOrWhiteSpace(message.Email) ? null : message.Email.Trim(),
            ParentAccountKey: string.IsNullOrWhiteSpace(message.ParentAccountId) ? null : message.ParentAccountId);
    }
}
