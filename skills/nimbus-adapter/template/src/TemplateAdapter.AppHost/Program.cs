// Local stack: provisioner -> simulator -> adapter, against a Service Bus namespace of your own.
//
// Service Bus: a PERSONAL dev namespace (Standard tier), set once in this project's user secrets:
//   dotnet user-secrets set "ConnectionStrings:servicebus" "Endpoint=sb://<ns>.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<key>" --project src/TemplateAdapter.AppHost
// Never point it at the namespace a deployed adapter listens on: two adapters on one subscription
// compete for the same messages. The NimBus Resolver and WebApp are not part of this stack; use the
// ones of your dev environment, or run NimBus from source, to see the audit trail and resubmit.
//
// Try it: POST an account to the simulator's upstream driver and watch the adapter's log:
//   POST {simulator}/emulator/upstream/accounts  {"accountId":"ACC-1001","name":"Fabrikam Marine","modifiedOn":"2026-01-15T09:30:00Z"}
//   POST {simulator}/emulator/faults             {"status":503,"durationSeconds":60}   (an update window: the circuit opens)
var builder = DistributedApplication.CreateBuilder(args);

var servicebus = builder.AddConnectionString("servicebus");
#if (Inbox)

// The integration platform's own database (the adapter's inbox), never the external system's.
var integration = builder.AddSqlServer("sql").AddDatabase("integration");
#endif

// Projects are added by path so the resource names stay stable whatever the solution is called.
var provisioner = builder.AddProject("provisioner", "../TemplateAdapter.Provisioner/TemplateAdapter.Provisioner.csproj")
    .WithReference(servicebus);

var simulator = builder.AddProject("externalsystem-simulator", "../TemplateAdapter.Simulator/TemplateAdapter.Simulator.csproj")
    .WithReference(servicebus)
    .WithExternalHttpEndpoints()
    .WaitForCompletion(provisioner);

builder.AddProject("adapter", "../TemplateAdapter.Adapter/TemplateAdapter.Adapter.csproj")
    .WithReference(servicebus)
    .WithReference(simulator)
#if (Inbox)
    .WithReference(integration)
    .WaitFor(integration)
#endif
    .WaitFor(simulator)
    .WaitForCompletion(provisioner);

builder.Build().Run();
