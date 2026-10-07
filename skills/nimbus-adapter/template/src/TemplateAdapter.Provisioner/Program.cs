#if (IngressEnabled)
using Azure.Messaging.ServiceBus.Administration;
#endif
using NimBus.ServiceBus.Provisioning;
#if (ContractsPackage)
using CatalogPlatform = PLATFORM_TYPE;
#else
using CatalogPlatform = TemplateAdapter.Contracts.AdapterCatalog;
#endif

// Creates the topics, forwarding subscriptions and deferred subscriptions the catalog declares.
// Idempotent, and it refuses a catalog that declares a command without exactly one consumer. The
// local AppHost holds the adapter back (WaitForCompletion) until this exits successfully. In Azure,
// the pipeline applies the same topology with `nb topology apply`.
//
// Topology is created ONLY by the provisioner: the adapter never creates topics or subscriptions.
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException("ConnectionStrings__servicebus is required.");

var provisioner = new ServiceBusTopologyProvisioner(connectionString, () => new CatalogPlatform());

Console.WriteLine("Provisioning the Service Bus topology for the adapter catalog...");
await provisioner.ApplyAsync(CancellationToken.None);
#if (IngressEnabled)

// The adapter's own ingress queue for ExternalSystem's change notifications (not part of the NimBus
// catalog). In Azure the Bicep creates it with the same settings.
var ingressQueue = Environment.GetEnvironmentVariable("Ingress__QueueName") ?? "externalsystem-changes";
var admin = new ServiceBusAdministrationClient(connectionString);
if (!await admin.QueueExistsAsync(ingressQueue))
{
    await admin.CreateQueueAsync(new CreateQueueOptions(ingressQueue)
    {
        MaxDeliveryCount = 10,
        LockDuration = TimeSpan.FromMinutes(1),
        DeadLetteringOnMessageExpiration = true,
        DefaultMessageTimeToLive = TimeSpan.FromDays(7),
    });
    Console.WriteLine($"Created the ingress queue '{ingressQueue}'.");
}
#endif
Console.WriteLine("Topology provisioning complete.");
