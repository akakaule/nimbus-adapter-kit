using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace TemplateAdapter.Contracts.Upstream;

/// <summary>
/// Sample event that shows the catalog conventions:
/// <list type="bullet">
///   <item>a <c>[Description]</c> on the class and on every property (shown in the NimBus WebApp and exports);</item>
///   <item>a static <c>Example</c> that passes validation;</item>
///   <item><c>[Required]</c> on what consumers depend on;</item>
///   <item><c>[Sensitive]</c> on personal data, so the WebApp masks it;</item>
///   <item>a <c>[SessionKey]</c> on the aggregate's own id, so changes to one account stay in order;</item>
///   <item>the producing system as name prefix (the EventTypeId is the class name and is global
///   to the Service Bus namespace).</item>
/// </list>
/// </summary>
[Description("Published by the upstream system when an account is created or changed. ExternalSystem keeps a copy keyed on AccountId.")]
[SessionKey(nameof(AccountId))]
public class UpstreamAccountUpdated : Event
{
    /// <summary>Example shown in the NimBus WebApp; must pass validation (see CatalogTests).</summary>
    public static readonly UpstreamAccountUpdated Example = new()
    {
        AccountId = "ACC-1001",
        Name = "Fabrikam Marine",
        CountryCode = "DK",
        Email = "info@fabrikam-marine.example",
        ParentAccountId = null,
        ModifiedOn = new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.Zero),
    };

    /// <summary>The upstream account id. Session key, and the external key the account has in ExternalSystem.</summary>
    [Required]
    [Description("The upstream account id. Session key, and the external key the account has in ExternalSystem.")]
    public string AccountId { get; set; } = string.Empty;

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

    /// <summary>
    /// The upstream id of the parent account, if any. The parent must already exist in
    /// ExternalSystem: the adapter does not create it (missing-reference policy).
    /// </summary>
    [Description("The upstream id of the parent account, if any. It must already exist in ExternalSystem.")]
    public string? ParentAccountId { get; set; }

    /// <summary>When the account last changed upstream. Feeds the deterministic MessageId at the publisher.</summary>
    [Description("When the account last changed upstream.")]
    public DateTimeOffset ModifiedOn { get; set; }
}
