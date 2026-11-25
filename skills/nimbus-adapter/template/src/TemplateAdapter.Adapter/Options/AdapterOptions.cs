#if (Inbox)
using NimBus.Core.Inbox;

#endif
namespace TemplateAdapter.Adapter.Options;

/// <summary>Receiver and inbox settings, bound from the <c>Adapter</c> configuration section.</summary>
public sealed class AdapterOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Adapter";

    /// <summary>
    /// How many sessions (ordered streams, e.g. one per account) are processed in parallel. Size it to
    /// what the external system tolerates: every session can have one call in flight.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 8;
#if (Inbox)

#if (InboxSql)
    /// <summary>The inbox store. SQL Server in production; the tests use the in-memory store.</summary>
    public InboxStore InboxStore { get; set; } = InboxStore.SqlServer;
#endif
#if (InboxTable)
    /// <summary>The inbox store. Azure Table Storage in production; the tests use the in-memory store.</summary>
    public InboxStore InboxStore { get; set; } = InboxStore.AzureTable;

    /// <summary>The table that holds the inbox receipts. The Bicep creates it; locally the adapter does.</summary>
    public string InboxTableName { get; set; } = "NimBusInbox";
#endif

    /// <summary>
    /// How long a handled MessageId is remembered. Keep it longer than any redelivery you expect
    /// (scheduled retries, a source replaying its outbox after an outage).
    /// </summary>
    public int InboxRetentionDays { get; set; } = 7;
#endif
}
