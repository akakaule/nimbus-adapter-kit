using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace TemplateAdapter.Contracts.ExternalSystem;

/// <summary>
/// Published by this adapter's ingress when an account changes in ExternalSystem: a change
/// notification arrives on the ingress queue, the adapter re-reads the account and publishes its
/// current state. Keyed (session) on the account's external key.
/// </summary>
[Description("Published by the ExternalSystem adapter when an account is created or changed in ExternalSystem. Carries the account's current state.")]
[SessionKey(nameof(AccountKey))]
public class ExternalSystemAccountUpdated : Event
{
    /// <summary>Example shown in the NimBus WebApp; must pass validation (see CatalogTests).</summary>
    public static readonly ExternalSystemAccountUpdated Example = new()
    {
        AccountKey = "ACC-1001",
        Name = "Fabrikam Marine",
        CountryCode = "DK",
        Email = "info@fabrikam-marine.example",
        ParentAccountKey = null,
        ModifiedOn = new DateTimeOffset(2026, 1, 15, 9, 45, 0, TimeSpan.Zero),
    };

    /// <summary>The account's external key in ExternalSystem (the upstream id). Session key.</summary>
    [Required]
    [Description("The account's external key in ExternalSystem. Session key.")]
    public string AccountKey { get; set; } = string.Empty;

    /// <summary>The account name.</summary>
    [Required]
    [Description("The account name.")]
    public string Name { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    [Description("ISO 3166-1 alpha-2 country code.")]
    public string? CountryCode { get; set; }

    /// <summary>The account's general e-mail address.</summary>
    [Sensitive(Mode = MaskMode.PartialReveal)]
    [Description("The account's general e-mail address.")]
    public string? Email { get; set; }

    /// <summary>External key of the parent account, if any.</summary>
    [Description("External key of the parent account, if any.")]
    public string? ParentAccountKey { get; set; }

    /// <summary>When ExternalSystem reported the change (from the change notification).</summary>
    [Description("When ExternalSystem reported the change.")]
    public DateTimeOffset ModifiedOn { get; set; }
}
