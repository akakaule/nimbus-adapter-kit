using Microsoft.Extensions.Configuration;
#if (ContractsPackage)
using CatalogPlatform = PLATFORM_TYPE;
#else
using CatalogPlatform = TemplateAdapter.Contracts.AdapterCatalog;
#endif

// Local stack: the NimBus Service Bus emulator, the provisioner, the simulator, the adapter, and the
// NimBus Resolver and WebApp so you can see the audit trail and resubmit or skip failed messages.
// Everything NimBus runs from NuGet packages (dotnet tools restored through this repo's NuGet.config);
// nothing is installed globally. It needs Docker, for the SQL Server the Resolver and WebApp store in.
//
// Try it: POST an account to the simulator's upstream driver, then open the "nimbus-ops" resource:
//   POST {simulator}/emulator/upstream/accounts  {"accountId":"ACC-1001","name":"Fabrikam Marine","modifiedOn":"2026-01-15T09:30:00Z"}
//   POST {simulator}/emulator/faults             {"status":503,"durationSeconds":60}   (an update window: the circuit opens)
//
// A real namespace instead of the emulator (for example to test against a sandbox): set a PERSONAL
// dev namespace (Standard tier) once in this project's user secrets:
//   dotnet user-secrets set "ConnectionStrings:servicebus" "Endpoint=sb://<ns>.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<key>" --project src/TemplateAdapter.AppHost
// Never point it at the namespace a deployed adapter listens on: two adapters on one subscription
// compete for the same messages. With a namespace of your own, the Resolver and WebApp are not
// started here; use the ones of the environment that namespace belongs to.
#if (IngressEnabled)
//
// Ingress (system -> bus) needs that namespace of your own: the emulator has no queues, so on the
// emulator the ingress queue is not created and the adapter's ingress worker is switched off.
#endif
var builder = DistributedApplication.CreateBuilder(args);

var ownNamespace = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("servicebus"));
IResourceBuilder<IResourceWithConnectionString> servicebus;
if (ownNamespace)
{
    servicebus = builder.AddConnectionString("servicebus");
}
else
{
    var emulator = builder.AddNimBusServiceBusEmulator("servicebus");
    // The emulator remembers the topology between runs. By default every AppHost on this machine
    // with a "servicebus" resource shares that file, so this adapter keeps its own: delete it to
    // start from an empty namespace.
    emulator.Emulator.WithEnvironment(
        "NIMBUS_SBEMULATOR_TOPOLOGY_PATH",
        Path.Combine(Path.GetTempPath(), "nimbus-sbemulator", "TemplateAdapter", "topology.json"));
    servicebus = emulator.ConnectionString;
}
#if (InboxSql)

// The integration platform's own database (the adapter's inbox), never the external system's.
var sql = builder.AddSqlServer("sql");
var integration = sql.AddDatabase("integration");
#endif
#if (InboxTable)

// The adapter's inbox table, in the Azurite storage emulator.
var inbox = builder.AddAzureStorage("storage").RunAsEmulator().AddTables("inbox");
#endif

// Projects are added by path so the resource names stay stable whatever the solution is called.
// The provisioner applies the catalog's topology; everything that uses the bus waits for it.
var provisioner = builder.AddProject("provisioner", "../TemplateAdapter.Provisioner/TemplateAdapter.Provisioner.csproj")
    .WithReference(servicebus)
    .WaitFor(servicebus);

var simulator = builder.AddProject("externalsystem-simulator", "../TemplateAdapter.Simulator/TemplateAdapter.Simulator.csproj")
    .WithReference(servicebus)
    // The simulator has no launch profile, so its endpoint is declared here: the adapter finds it
    // through service discovery ("https+http://externalsystem-simulator").
    .WithHttpEndpoint()
    .WithExternalHttpEndpoints()
    .WaitForCompletion(provisioner);

var adapter = builder.AddProject("adapter", "../TemplateAdapter.Adapter/TemplateAdapter.Adapter.csproj")
    .WithReference(servicebus)
    .WithReference(simulator)
#if (InboxSql)
    .WithReference(integration)
    .WaitFor(integration)
#endif
#if (InboxTable)
    .WithReference(inbox)
    .WaitFor(inbox)
#endif
    .WaitFor(simulator)
    .WaitForCompletion(provisioner);

if (!ownNamespace)
{
#if (IngressEnabled)
    // No queues on the emulator: no ingress queue, no ingress worker, and the simulator's change
    // driver answers 503 instead of sending to a queue that isn't there.
    provisioner.WithEnvironment("Ingress__Enabled", "false");
    simulator.WithEnvironment("Ingress__Enabled", "false");
    adapter.WithEnvironment("Ingress__Enabled", "false");

#endif
    // The NimBus message store: the audit trail the WebApp shows.
#if (InboxSql)
    var nimbusDb = sql.AddDatabase("nimbus");
#else
    var nimbusDb = builder.AddSqlServer("sql").AddDatabase("nimbus");
#endif
    var platformAssembly = typeof(CatalogPlatform).Assembly.Location;
    var platformType = typeof(CatalogPlatform).FullName!;

    var webApp = builder.AddNimBusWebApp("nimbus-ops", platformAssembly, platformType)
        .WithNimBusServiceBus(servicebus)
        .WithNimBusSqlServerStore(nimbusDb)
        .WaitForCompletion(provisioner);

    builder.AddNimBusResolver()
        .WithNimBusServiceBus(servicebus)
        .WithNimBusSqlServerStore(nimbusDb)
        .WithNimBusWebAppNotifications(webApp)
        .WaitForCompletion(provisioner);
}

builder.Build().Run();
