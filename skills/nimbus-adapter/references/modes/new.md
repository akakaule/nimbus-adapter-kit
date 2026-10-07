# Mode `new`: scaffold a new adapter

Goal: a compiling, tested adapter repo for one external system, with its first flow done end to
end. Work through the steps in order and keep the user informed between steps.

## 1. Preflight

- Check `dotnet --version` (needs a .NET 10 SDK) and, for deployment work, `az bicep version`.
- Find out which NimBus version the customer's environment runs (the Resolver and WebApp). The
  template defaults to 4.2.0; pass `--NimBusVersion` if it differs.
- Ask whether the customer keeps the message catalog in a shared **platform package** (a
  `*.Platform` / `*.Contracts` NuGet package that registers all endpoints). If so, note:
  - its package id and version;
  - the full type name of its `Platform` class;
  - its feed URL;
  - the name of this adapter's endpoint in it.

  Step 3 generates against it.

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
dotnet build <folder>/<name>.slnx -c Release
dotnet test <folder>/<name>.slnx -c Release
```

| Option | Use when |
|---|---|
| `--SystemName <System>` (required) | PascalCase, no spaces (`BusinessCentral`, `Dataverse`, `FinanceOperations`). Names the client, the exceptions and, by default, the endpoint |
| `--Endpoint <Name>Endpoint` | The endpoint (Service Bus topic) is not called `<System>Endpoint`, for example in an existing catalog |
| `--Ingress true` | The system publishes changes (system → bus). Adds a queue of thin change notifications that the adapter re-reads and publishes (patterns 04–06), with the queue in the provisioner and the Bicep, a simulator driver and tests. Local catalog only |
| `--Inbox false` | No SQL database for an inbox (then every handler must be safely re-runnable) |
| `--Contracts package` plus `--ContractsPackageId`, `--ContractsPackageVersion`, `--PlatformType`, `--ContractsFeed` | The catalog is a shared platform package (see step 6) |
| `--NimBusVersion 4.x.y` | The environment runs another NimBus version |

The endpoint name must be unique in the namespace. The generated solution passes its tests before
you change anything; if it does not, stop and report. One exception: with `--Contracts package`
the wiring test is *inconclusive* until the first handler exists, and it lists the consumed events
that still need one.

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

1. Add this adapter's endpoint and events to the platform package, following
   `patterns/01-catalog-and-event-conventions.md`. Release it.
2. Generate with `--Contracts package --ContractsPackageId <id> --ContractsPackageVersion <version>
   --PlatformType <Namespace.PlatformClass> --ContractsFeed <feed URL>` and `--Endpoint` if needed.
   The template then:
   - references the package instead of a local Contracts project;
   - adds the feed to `NuGet.config`, mapped to that package only;
   - points the provisioner and the tests at the package's `Platform`;
   - drops the topology step from the pipeline. The platform pipeline applies the topology with
     `nb topology apply --platform-package Id@Version`.
3. It has no sample handler (the sample events aren't in your catalog). Build the first handler in
   step 5.
4. A private feed needs credentials. In Azure DevOps, add the `NuGetAuthenticate@1` task before the
   build.
5. `CatalogTests` check the events this endpoint consumes and produces. EventTypeId uniqueness is
   checked across the whole catalog.

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
