namespace TemplateAdapter.Adapter.Ingress;

/// <summary>Ingress settings, bound from the <c>Ingress</c> configuration section.</summary>
public sealed class IngressOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Ingress";

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
