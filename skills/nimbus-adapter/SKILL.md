---
name: nimbus-adapter
description: Build, extend, review or audit a NimBus adapter, the service that connects an external system (Dynamics 365 CE / Dataverse, Business Central, Dynamics 365 Finance & Operations, or any REST/OData API) to a NimBus event bus on Azure Service Bus. Use when the user wants to create a new adapter or integration ("scaffold an adapter", "new integration to Business Central", "dotnet new nimbus-adapter"); add or change a flow in an existing adapter (a new event, handler, mapping, upsert, inbound change detection, retries, circuit breaker, inbox, deduplication, echo loops, missing references); or review, audit or assess a NimBus adapter, integration, branch or PR for problems such as retries, session keys, idempotency, error handling, throttling or ordering. Contains a pattern catalog distilled from production adapters, knowledge packs for Dataverse, Business Central and F&O, decision and review checklists, and a compilable dotnet new template.
---

# nimbus-adapter: build, extend and review NimBus adapters

A NimBus adapter is a worker that receives events from a NimBus endpoint and writes them to an
external system, and/or detects changes in that system and publishes them as events. This skill
carries what production adapters taught: which decisions every flow needs, which NimBus APIs solve
them, which tests prove them, and what each target system does differently.

## Pick the mode

| The user wants to… | Mode | Playbook |
|---|---|---|
| Start a new adapter (no adapter repo yet, or an empty one) | `new` | [references/modes/new.md](references/modes/new.md) |
| Add or change an integration flow in an existing adapter | `flow` | [references/modes/flow.md](references/modes/flow.md) |
| Assess an adapter, a branch or a PR against the patterns | `review` | [references/modes/review.md](references/modes/review.md) |

If the request is ambiguous, ask which one. Read the playbook for the mode before doing anything
else; it says which other references to load.

## What is where

| Need | Read |
|---|---|
| The patterns: one recurring problem each, its NimBus solution, tests, anti-patterns | [references/patterns/README.md](references/patterns/README.md) (index and decision guide) |
| How a specific system behaves: auth, change detection, writes, throttling, gotchas | `references/systems/<dataverse \| business-central \| finance-operations>/README.md` |
| What a local simulator of that system must emulate | `references/systems/<system>/simulator-routes.md` |
| Drop-in client code for that system (compiles against the template) | `references/systems/<system>/snippets/*.cs` |
| The decisions every flow needs, in order | [references/checklists/flow-decisions.md](references/checklists/flow-decisions.md) |
| The review checklist | [references/checklists/review.md](references/checklists/review.md) |
| Things that broke production adapters | [references/patterns/anti-patterns.md](references/patterns/anti-patterns.md) |
| The scaffold | [template/](template/) (`dotnet new nimbus-adapter`; see its README.md) |

Load only what the current step needs. Every fact in the system packs is tagged:
- **[proven in production]**: seen in a production adapter's code, docs or fix history. It was not
  re-observed at runtime.
- **[proven in the NimBus sample]**: shown against a simulator only.
- **[Microsoft docs, verify]**: from documentation.
- **[unverified]**: neither documented nor observed.

Treat everything except the first tag as a claim. Check it against the customer's environment or
current Microsoft documentation before you depend on it, and say so in your summary.

## Hard rules (every mode)

1. **Decide before you code.** Every flow gets the decisions in `flow-decisions.md`, recorded in
   `docs/flows/<flow>.md`: session key, MessageId, idempotency key, missing-reference policy,
   ownership, echo identity and failure mapping. Ask the user for what you cannot infer; never
   guess business rules (who masters which field, what a missing parent means).
2. **Failures are classified in the client.**
   - It throws exactly one of Throttled, Unavailable or Rejected, with a message an operator can
     act on.
   - The message never contains a request or response body, because NimBus stores it unmasked in
     the audit trail.
   - A malformed success is Unavailable, never "not found".
   - Handlers never catch-and-swallow.
3. **Retries belong to NimBus.**
   - Configure them with `ConfigureRetryPolicies` exception rules keyed on the exception's full
     type name plus `:`.
   - Never add HttpClient resilience handlers, Polly or retry loops, and never key a rule on a
     status code or message text.
   - Keep any SDK-internal retry inside the Service Bus lock.
4. **Writes are idempotent.** Upsert on a stable key (alternate key / external id). A redelivery,
   a retry and an operator resubmit must all be safe.
5. **Every event has a deliberate `[SessionKey]`.** Never a random or constant one. It also has
   `[Description]`, a valid static `Example`, `[Sensitive]` on personal data and a
   producing-system name prefix. EventTypeIds are global to the Service Bus namespace.
6. **One endpoint per process.**
   - Handlers must match `Consumes<T>()` exactly.
   - Every subscriber needs deferred replay (`AddNimBusDeferredProcessorHostedService`, or the
     second trigger under Functions).
   - Topology comes only from the provisioner or `nb topology apply`.
7. **Never log payloads or secrets.** Log ids and outcomes.
8. **Prove it.** Every change comes with tests (see the testing pattern). Run
   `dotnet build <solution> -c Release` and `dotnet test <solution> -c Release`, and report the
   results. Release turns compiler warnings and vulnerable packages into errors. Never claim done
   on a Debug build or on untested code.
9. **Keep customer data out of shared places.** Don't paste customer names, data or credentials
   into this skill, the kit repo or issue trackers.

## Versions

The template and the patterns target **NimBus 4.2** (`Akaule.NimBus.*` packages) and .NET 10. If
the adapter pins another NimBus version (`<NimBusVersion>` in `Directory.Packages.props`), check
API names against that version's source before you use them.
