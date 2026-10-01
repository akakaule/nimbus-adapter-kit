using NimBus.ServiceBus.Provisioning;
using TemplateAdapter.Contracts;

// Creates the topics, forwarding subscriptions and deferred subscriptions the catalog declares.
// Idempotent, and it refuses a catalog that declares a command without exactly one consumer. The
// local AppHost holds the adapter back (WaitForCompletion) until this exits successfully. In Azure,
// the pipeline applies the same topology with `nb topology apply`.
//
// Topology is created ONLY by the provisioner: the adapter never creates topics or subscriptions.
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException("ConnectionStrings__servicebus is required.");

var provisioner = new ServiceBusTopologyProvisioner(connectionString, () => new AdapterCatalog());

Console.WriteLine("Provisioning the Service Bus topology for the adapter catalog...");
await provisioner.ApplyAsync(CancellationToken.None);
Console.WriteLine("Topology provisioning complete.");
