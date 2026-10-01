# Mode `new`: scaffold a new adapter

Goal: a compiling, tested adapter repo for one external system, with its first flow done end to
end. Work through the steps in order and keep the user informed between steps.

## 1. Preflight

- Check `dotnet --version` (needs a .NET 10 SDK) and, for deployment work, `az bicep version`.
- Find out which NimBus version the customer's environment runs (the Resolver and WebApp). The
  template defaults to 4.2.0; pass `--NimBusVersion` if it differs.
- Ask whether the customer keeps the message catalog in a shared **platform package** (a
  `*.Platform` / `*.Contracts` NuGet package that registers all endpoints). If so, note its
  package id and version; step 6 switches the adapter to it.

## 2. Interview, then write the brief

Ask (one compact question block; offer sensible defaults) and record the answers in
`docs/adapter-brief.md`:

| Topic | Ask |
|---|---|
| System | Which system, which product and version (online/on-prem), which environments (one per country/company?) |
| Direction | Bus → system (the adapter writes), system → bus (the adapter publishes), or both |
| Entities and events | Which entities, which events, who produces and who consumes each |
| Ownership | Which system masters which entity, and which fields. When does ownership move (for example "locked once a quote exists")? |
| Change detection | For system → bus: what can the system emit (plugin/webhook/business event), or is polling needed? Read the system pack's "Change detection" section and propose the top-ranked option |
| Keys | The natural key of each entity on each side; where cross-references can live (alternate keys, custom fields) |
| References | Which references must exist before a write (parent account, owner, price list), and what happens when one is missing |
| Volume and timing | Peak messages per minute, bursts, initial load size, business hours, update windows |
| Auth | How the adapter authenticates (managed identity, app registration) and who grants it access |
| Hosting | Container App (template default). Functions only if the customer insists (see `patterns/29-functions-hosting.md`) |

Brief format: a short Markdown file with a table of flows (`flow | direction | trigger | event |
session key | target operation`), an ownership table, an open-questions list, and the answers
above. It is the input for `docs/flows/` and for the TDD.

## 3. Generate the solution

```powershell
dotnet new install "<path-to-this-skill>/template" --force
dotnet new nimbus-adapter -n <Company>.<System>Adapter --SystemName <System> -o <folder>
#   options: --Inbox false (no SQL inbox), --NimBusVersion 4.1.0
dotnet build <folder>/<name>.slnx -c Release
dotnet test <folder>/<name>.slnx -c Release
```

`SystemName` is PascalCase without spaces (`BusinessCentral`, `Dataverse`, `FinanceOperations`).
It names the endpoint (`<System>Endpoint`, the Service Bus topic), so it must be unique in the
namespace. The generated solution passes its tests before you change anything; if it does not,
stop and report.

## 4. Specialise for the system

1. Read `references/systems/<system>/README.md` completely.
2. Replace the generic client with the pack's snippet:
   - copy `snippets/*.cs` into `src/<name>.Adapter/Clients/`;
   - replace the token `ExternalSystem` with `<System>` in the copied files, exactly as the
     template did;
   - register the new client in `<System>ClientRegistration` in place of the generic one;
   - adjust constants (entity sets, alternate-key columns, company).
3. Shape the simulator like the real API (`simulator-routes.md`): routes, status codes and error
   bodies. Update `SimulatorContractTests` so client and simulator agree.
4. Set the auth scope in `appsettings.json` and the Bicep parameter (`ExternalSystem:Scope`).
5. Rename the sample `Account` names to the first real entity, or delete the sample flow once the
   first real flow exists.

## 5. Build the first flow

Switch to [flow.md](flow.md) for the first flow from the brief. A new adapter is done when one
real flow passes its tests end to end against the simulator.

## 6. Shared catalog package (if the customer has one)

1. Add the catalog's endpoint and events to the platform package, following
   `patterns/01-catalog-and-event-conventions.md`. Release it.
2. In the adapter:
   - delete `src/<name>.Contracts` and remove it from the `.slnx`;
   - add a `PackageReference` to the catalog package (and its feed in `NuGet.config`);
   - point `AdapterComposition.EndpointName`, the provisioner and `CatalogTests` at the package's
     `Platform` and endpoint types.
3. In `azure-pipelines.yml`, remove the `contracts` artifact and the topology step: the platform
   pipeline applies the topology (`nb topology apply --platform-package Id@Version`).
4. Keep `CatalogTests`. Extend `EventTypeIds_AreUnique` with the other catalogs that share the
   namespace.

## 7. Documentation

Use the `adapter-docs` skill (installed next to this one through the generated `agr.toml`) in
*draft-from-spec* mode with `docs/adapter-brief.md` and `docs/flows/` as input. It writes
`docs/TDD.md` and `docs/events.md`.

## 8. Hand-off summary

Tell the user, at minimum:
- what was generated;
- which flow works;
- which tests ran (with the counts);
- which facts are **[Microsoft docs, verify]** and still need checking in their environment;
- the remaining one-time setup from the generated README's "Deploy" section.
