# TemplateAdapter

A [NimBus](https://github.com/akakaule/NimBus) adapter that connects **ExternalSystem** to the
NimBus event bus. It runs as a worker (Azure Container App), receives on
`ExternalSystemEndpoint`, and calls the ExternalSystem API.

Generated with `dotnet new nimbus-adapter` from the NimBus Adapter Kit. The decisions behind each
flow live in [`docs/flows/`](docs/flows/), and the TDD and events catalog in [`docs/`](docs/).

## Layout

| Path | What |
|---|---|
| `src/TemplateAdapter.Contracts` | The catalog: endpoints, events, `AdapterCatalog` (NimBus.Abstractions only) |
| `src/TemplateAdapter.Adapter` | The adapter: `AdapterComposition` (all wiring), `Clients/` (ExternalSystem API + failure types), `Handlers/`, `Mapping/`, `Resilience/`, and `Ingress/` when generated with `--Ingress true` (change notifications from a queue, re-read and published) |
| `src/TemplateAdapter.Simulator` | A local stand-in for the ExternalSystem API with fault injection, plus an upstream driver that publishes sample events |
| `src/TemplateAdapter.Provisioner` | Applies the Service Bus topology from the catalog (local runs) |
| `src/TemplateAdapter.AppHost` | Aspire: provisioner, simulator and adapter in one command |
| `src/TemplateAdapter.ServiceDefaults` | OpenTelemetry, health checks, service discovery. No HttpClient retry handler on purpose |
| `tests/TemplateAdapter.Tests` | Client classification, retry rules, circuit breaker, handlers, pipeline, catalog and architecture tests |
| `deploy/` | Bicep for the Container App, per-environment parameters, the pipeline deploy stage |
| `azure-pipelines.yml` | Build, test (gate), image, then deploy dev, test and prod |

## Build and test

```powershell
dotnet build TemplateAdapter.slnx -c Release   # what CI runs: compiler warnings and vulnerable packages fail it
dotnet test TemplateAdapter.slnx -c Release
```

## Run it locally

1. Use a Service Bus namespace of your own (Standard tier). Never use the one a deployed adapter
   listens on: two adapters on one subscription compete for the same messages.

   ```powershell
   dotnet user-secrets set "ConnectionStrings:servicebus" "<connection string>" --project src/TemplateAdapter.AppHost
   ```

2. Start the stack (needs Docker for the inbox's SQL Server):

   ```powershell
   dotnet run --project src/TemplateAdapter.AppHost
   ```

3. In the Aspire dashboard, open the simulator's URL and drive it:
   - `POST /emulator/upstream/accounts` with
     `{"accountId":"ACC-1001","name":"Fabrikam Marine","modifiedOn":"2026-01-15T09:30:00Z"}` publishes
     an `UpstreamAccountUpdated`. The adapter upserts it: check `GET /emulator/accounts`.
   - Post the same body again. The inbox skips it as a duplicate (same MessageId).
   - `POST /emulator/faults` with `{"status":503,"durationSeconds":60}` simulates an update window.
     NimBus retries with backoff, and the circuit breaker pauses the receivers.
   - `POST /emulator/faults` with `{"status":429,"count":3,"retryAfterSeconds":2}` throttles three calls.
   - If the adapter was generated with `--Ingress true` (system → bus):
     `POST /emulator/accounts/ACC-2001/changed` with `{"accountKey":"ACC-2001","name":"Northwind Subsea"}`
     changes the account as a user would and sends the change notification. The adapter re-reads the
     account and publishes `ExternalSystemAccountUpdated`. The adapter's own writes send no
     notification; that is the echo filter at the source, which the real system's trigger must do too.

The NimBus Resolver and WebApp are not part of this stack. Use your dev environment's, or run NimBus
from source, to see the audit trail and to resubmit or skip failed messages.

## Add a flow

Use the `nimbus-adapter` agent skill (`flow` mode). Install it with AGR: `agr sync` reads
[`agr.toml`](agr.toml). Or follow the same checklist by hand:
1. Decide the session key, the MessageId, the idempotency key, the missing-reference policy, field
   ownership and the echo identity (`docs/flows/<flow>.md`).
2. Add the event to the catalog (with `[Description]`, `Example`, `[SessionKey]`), and add
   `Consumes<T>` or `Produces<T>` on the endpoint.
3. Add the client call (classified failures), the pure mapper and a thin handler.
4. Add tests next to the existing ones. The catalog and architecture tests cover the new event
   automatically.

## Deploy

`azure-pipelines.yml` builds and tests every pull request. On `main` it also:
1. pushes an image;
2. applies the Service Bus topology (`nb topology apply`);
3. deploys `deploy/main.bicep` to dev, then test, then prod, with the approvals set on the pipeline
   environments.

One-time setup:
1. Fill in the `<placeholders>` in `azure-pipelines.yml` and `deploy/main.*.bicepparam`.
2. Create the service connections and pipeline environments listed at the top of
   `azure-pipelines.yml`. Each environment's service connection needs **Contributor** and
   **Role Based Access Control Administrator** on its resource group, because the Bicep creates
   the adapter's identity and its role assignments.
3. After the first deployment, give the adapter's managed identity (output
   `identityPrincipalId`) access to ExternalSystem, for example as an application user. Then set
   `externalSystemScope`.
4. Inbox:
   - Store the integration database connection string as a secret in a Key Vault in the same
     resource group. The vault must use the Azure RBAC permission model.
   - Name the secret as `integrationDatabaseSecretName` says (default
     `integration-db-connection-string`).
   - The Bicep gives the adapter's identity **Key Vault Secrets User** on the vault. The Container
     App reads the secret at runtime, so the pipeline never handles it.
   - The inbox creates its table on first use, so the database user needs permission to create it
     once. Otherwise create the table up front and set `AutoCreateTable` to false.
   - Managed-identity SQL authentication needs the `Microsoft.Data.SqlClient.Extensions.Azure`
     package with SqlClient 7.x. Verify it before relying on it.

## Operate

- **Failed messages** appear in the NimBus WebApp with the client's failure text (operation, request,
  status and the system's error). Fix the data in ExternalSystem, then resubmit. Or skip the
  message if it no longer matters.
- **Blocked sessions**: a failed message blocks later messages for the same key (for example the
  same account). They are parked and replayed in order after the resubmit or skip.
- **Outages**: `Circuit for ExternalSystemEndpoint opened` (Warning) means the receivers are paused.
  They resume on their own after a successful probe. Alert on that log line.

## Upgrade NimBus

Change `<NimBusVersion>` in `Directory.Packages.props` and `nimbusVersion` in
`azure-pipelines.yml`, keeping them equal to the NimBus version your Resolver and WebApp run. Then
build in Release and run the tests.
