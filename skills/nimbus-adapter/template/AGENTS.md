# TemplateAdapter — Agent Instructions

This repo is a NimBus adapter between the NimBus event bus and **ExternalSystem**. It was
generated from the NimBus Adapter Kit. The `nimbus-adapter` skill (installed with `agr sync`)
holds the patterns and the ExternalSystem knowledge pack. Load it before you design or review a
flow.

## Build and test

```powershell
dotnet build TemplateAdapter.slnx -c Release   # CI configuration: compiler warnings and vulnerable packages are errors
dotnet test TemplateAdapter.slnx -c Release
dotnet run --project src/TemplateAdapter.AppHost   # local stack; see README.md
```

Report the checks you ran. Never declare work done on a Debug build.

## Rules

- **All wiring lives in `AdapterComposition`.** Tests build the same composition, so never
  register adapter services in `Program.cs`.
- **One endpoint per process.** The adapter receives only on `ExternalSystemEndpoint`. A second
  endpoint is a second adapter.
- **Every event a handler consumes is declared with `Consumes<T>()`** on the endpoint, and every
  `Consumes<T>()` has a handler. The architecture tests enforce this.
- **Every event has `[Description]`, a static valid `Example`, `[SessionKey]` on the aggregate's
  own id, and `[Sensitive]` on personal data.** Event names start with the producing system. The
  EventTypeId is the class name and is global to the Service Bus namespace.
- **Handlers are thin.**
  - Resolve references, call a pure mapper, then make one idempotent call (an upsert keyed on the
    source id).
  - No I/O in mappers.
  - Never log payloads: log ids and outcomes.
- **Failures are classified in the client, never in handlers.** The client throws
  `ExternalSystemThrottledException`, `ExternalSystemUnavailableException` or
  `ExternalSystemRejectedException`, with a message an operator can act on.
- **A missing referenced record throws `MissingReferenceException`** with the remedy. Business
  outcomes (for example "owned by ExternalSystem") are results, not exceptions.
- **Retries belong to NimBus.** Change them only in `Resilience/ExternalSystemResilience.cs`.
  - Never add an HttpClient resilience handler, Polly or a retry loop around client calls.
  - Never key a retry rule on a status code or on message text: use the exception type name.
- **The circuit breaker counts outages only.** New data-problem exception types must be excluded
  in `ConfigureCircuitBreaker`.
- **Topology is created only by the provisioner / `nb topology apply`.** The adapter never creates
  topics or subscriptions.
- **No secrets in the repo.** Connection strings come from user secrets locally, and from Key
  Vault references in Azure.
- **Tests come with every change.**
  - A new client call gets classification tests.
  - A new handler gets handler tests (happy path, missing reference, failure propagation).
  - A new event is covered by the catalog tests automatically.
