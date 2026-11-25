namespace TemplateAdapter.Adapter.Ingress;

/// <summary>Ingress settings, bound from the <c>Ingress</c> configuration section.</summary>
public sealed class IngressOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Ingress";

    /// <summary>
    /// Whether the ingress worker runs. The local AppHost sets it to false on the NimBus Service Bus
    /// emulator, which has no queues; everywhere else leave it on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The Service Bus queue ExternalSystem's trigger writes change notifications to. Created by the
    /// provisioner locally and by the Bicep in Azure. Not a NimBus topic: it belongs to this adapter.
    /// </summary>
    public string QueueName { get; set; } = "externalsystem-changes";

    /// <summary>
    /// Notifications processed in parallel. Order on the queue does not matter: every notification
    /// re-reads the current state, so a late one publishes the same (current) data.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 4;
}
