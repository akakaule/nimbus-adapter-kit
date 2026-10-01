# NimBus Adapter Kit: patterns, skills and a `dotnet new` template

## Context

Context& builds NimBus integrations for customers, and the adapters to Dataverse/CE, Business Central and F&O are rebuilt from scratch each time. The know-how is spread across:
- **NimBus samples:** CrmErpDemo, DynamicsBcDemo and the `feat/dataverse-adapter` branch.
- **About ten production adapters on NimBus's predecessor platform** in three customer estates, built on the same architecture.
- **Customer review findings.**

Today:
- `docs/building-adapters.md` covers only the NimBus wiring.
- The `adapter-docs` skill only writes TDDs, and its API names are stale.
- Each sample and production adapter solves the same problems differently. Only `BusinessCentral.Adapter` gets error handling fully right.

**Goal:** one kit that makes adapters faster to build and consistently correct, for three groups of users:
- new NimBus users;
- customer teams taking over after Context& delivers the first integrations;
- consultants.

**What the kit contains:**
1. **An anonymized pattern catalog** distilled from the survey.
2. **Knowledge packs** for Dataverse/CE, Business Central and F&O.
3. **One agent skill**, portable across Claude Code, Codex and Copilot.
4. **A compilable `dotnet new nimbus-adapter` template.** It generates the deterministic part of an adapter, so agents don't regenerate it each time.

**Decisions already made:**
- **Repo:** its own private GitHub repo, `akakaule/nimbus-adapter-kit`, cloned at `C:\Git\nimbus-adapter-kit`.
  - The name is free and `gh` is signed in.
  - Customer teams get read access.
  - Because the content is anonymized, making the repo public later stays an option.
- **Anonymized content:**
  - No customer names, customer repo paths or verbatim customer code.
  - Provenance reads like "production Dataverse CE adapter".
  - A CI denylist enforces this.
- **Template hosting:** Worker/Container App only. A Functions host can't pause receivers when the circuit breaker opens. Functions hosting is a docs-only pattern. *(This was my default after the question was interrupted. Say so if you want a Functions variant.)*
- **System packs:** Dataverse/CE, BC and F&O. Salesforce is out of scope.
- **Distribution:** AGR (`agr.toml` + `agr sync`), the same mechanism an existing customer skill repo already uses, plus a `.claude-plugin/marketplace.json` so a user can install the skill before they have an adapter repo.

## Step 0: repo and tracking

1. `gh repo create akakaule/nimbus-adapter-kit --private`, then clone it to `C:\Git\nimbus-adapter-kit`.
2. Commit this plan as `docs/plan/2026-10-01-adapter-kit.md`, and open it as tracking issue #1 (`gh issue create --body-file`).
3. File the NimBus follow-ups listed at the end as issues on `akakaule/NimBus`. Only generic NimBus content goes in them.

## Kit layout

```
README.md  AGENTS.md  CLAUDE.md(@AGENTS.md)  CHANGELOG.md  LICENSE
.claude-plugin/{plugin.json,marketplace.json}
skills/nimbus-adapter/
  SKILL.md                         ~150 lines: mode routing + hard rules
  references/modes/{new,flow,review}.md
  references/patterns/NN-<slug>.md + anti-patterns.md
  references/systems/{dataverse,business-central,finance-operations}/
      README.md  simulator-routes.md  snippets/*.cs
  references/checklists/{flow-decisions,review}.md
  template/                        dotnet new template (compiles in place)
tests/Kit.Snippets/                compiles every snippets/*.cs
eng/instantiate-matrix.ps1
.github/workflows/ci.yml
```

**One skill with three modes, not three skills.**
- AGR installs each skill folder on its own, so separate skills can't share the pattern references without duplicating them.
- The template lives inside the skill folder, so AGR ships it. The skill runs `dotnet new install "<skill-dir>/template" --force`.

**Portability rules:**
- Frontmatter is only `name` and `description`.
- No tool names specific to one agent.
- Only `dotnet`/`az` commands, because customers are on Windows.
- Paths are relative to `SKILL.md`.

**`adapter-docs` stays a separate skill.** It is fixed upstream in NimBus and pulled in through AGR. If AGR can't install from a subfolder of the NimBus repo, the kit vendors a copy and CI checks it for drift.

## Pattern catalog (anonymized, derived from the survey)

**Every pattern file has the same sections:**
- Problem
- When / When not
- Solution, using the exact NimBus API
- Snippet link
- Per-system notes
- Tests to write
- Anti-patterns
- Provenance, with a confidence label

★ marks the first wave of 12, written in P3. The rest follow in P6.

**A. Contracts and topology**
1. ★ **Catalog and event conventions.**
   - Every event has `[Description]`, a static `Example`, `[Required]`, `[Sensitive]` and `[SessionKey]`.
   - Event names carry a producer prefix.
   - EventTypeIds are globally unique.
   - Commands have exactly one consumer.
   - Replies are DTOs, not events.
2. ★ **Session-key selection.**
   - Never use `Guid.NewGuid`, a constant fallback or a timestamp.
   - Key a child on its parent only when ordering requires it. In that case a failed parent blocks its children; production moved contacts to their own session for that reason.
3. **One endpoint per process.** Also covers low-priority lanes and a per-developer subscription suffix.

**B. Inbound (system → bus)**

4. ★ **Thin notification, then re-read:** plugin, webhook or business event → queue → re-read → map → publish.
5. ★ **Deterministic MessageId plus `UseInbox`.**
6. **Reliable publish.**
   - Outbox, with one `Publish` per event to keep order.
   - Or an ingress queue that publishes, then completes, and dead-letters with reason codes.
7. **Watermark polling,** plus the staging-table collapse with a cutoff.
8. ★ **Echo-loop prevention on both sides.**
   - Source side: filter on the integration identity and on relevant-field diffs.
   - Sink side: skip no-op writes and bypass plugins.
9. **Initial and bulk sync:** the same events, in parent order, rerunnable.
10. **Large-payload claim check.**

**C. Outbound (bus → system)**

11. ★ **Layering:** a thin handler, a lookup resolver, a pure mapper and the client. Payloads are never logged.
12. ★ **Idempotent upsert.**
    - Upsert by alternate key or external id.
    - Symmetric upsert: an Update creates the record if it is missing.
    - A duplicate-key error counts as success.
    - Don't set state fields on create.
13. **Cross-references stored in the target systems,** plus ID write-back events.
14. ★ **Missing-reference policy.**
    - Core entities throw with remediation text, so the session blocks until an operator resubmits.
    - Reference data is skipped with a warning.
15. **Stale-write guard** using a row-version high-water mark.
16. **Field ownership and business outcomes.** For example, a 409 meaning "owned by the other system" is completed as an outcome, not treated as a failure.
17. **Reference-data cache.**
    - Cache only foreign-key reads; read fresh for write decisions.
    - Never cache nulls; evict on write.
    - Bounded size with a TTL.
18. **PendingHandoff for asynchronous jobs** such as DMF: `MarkPendingHandoff` + `IHandoffClient`, keyed on `OriginatingMessageId ?? MessageId`.
19. **Request/reply.** `AddRequestHandler`; the handler never throws and returns `Unavailable` instead.

**D. Resilience**

20. ★ **Failure taxonomy at the client boundary.**
    - 429 → `Throttled(RetryAfter)`.
    - 408/502/503/504 and timeouts → `Unavailable`.
    - Other 4xx → `Rejected(status, code)`.
    - A `DescribeFailure` message for each, since NimBus stores only the exception type and message.
21. ★ **Retry rules.**
    - Use `ConfigureRetryPolicies` → `AddExceptionRule(nameof(...))` with `JitterMode.Bounded`. Set no default policy, so rejections block the session.
    - The matched text includes stack traces, so a rule must never key on a bare "429".
    - The total retry budget must fit inside the Service Bus lock duration.
    - Non-idempotent POSTs retry only on 429.
    - No hidden HttpClient retries.
22. ★ **Circuit breaker:** `WithCircuitBreaker`, with `Exclude<Rejected>()` and `Exclude<Throttled>()`, plus a state observer.
23. **Concurrency and throughput.**
    - `MaxConcurrentSessions` and `PrefetchCount=0`.
    - Dataverse degree of parallelism.
    - KEDA Service Bus scaler.

**E. Operations and quality**

24. **Auth and secrets.**
    - Managed identity and Key Vault.
    - An Azure.Identity token handler instead of a hand-written token cache.
25. **Observability and PII.**
    - OTel and a Meter, with correlation ids.
    - `[Sensitive]` fields.
    - Operator-readable errors in the form `[CATEGORY] … Action: …`.
26. ★ **Testing strategy.** The test list the template generates.
27. **Vendor-API simulator.**
    - Real routes, an auth check and vendor-shaped errors.
    - Injectable 429/503 with Retry-After.
    - `/emulator/*` driver endpoints that fire change notifications.
28. **Deploy and pipeline.**
    - Container App with a KEDA scaler.
    - Bicep that assigns managed-identity roles.
    - An Azure DevOps pipeline with a test gate and environment approvals.
29. **Functions hosting** (docs only).
    - A session trigger plus a `deferredprocessor` trigger; without the latter, messages stay Pending.
    - No breaker pause.
    - `host.json` settings.

**`anti-patterns.md` feeds the review checklist.** It covers:
- `async void` and never-started Tasks;
- sync-over-async and `Task.Run` around sync I/O;
- `BuildServiceProvider` during DI setup, and captive singletons;
- static caches that aren't thread-safe, and a ServiceClient per event;
- a swallowed GET error read as "not found", followed by a duplicate POST;
- deleting before processing, and in-memory work queues;
- logging payloads or PII;
- disabled auth and secrets in csproj files;
- CI without a test gate.

## System packs

**Each pack has:**
- A **README.md** covering:
  - auth;
  - change-detection options, ranked;
  - read and write APIs, including upsert, keys and batch limits;
  - throttling and error codes, mapped to Throttled/Unavailable/Rejected;
  - the echo identity;
  - gotchas.
- A **`simulator-routes.md`.**
- **Compiled snippets** in `snippets/*.cs`.

Every fact is tagged **[proven in production]** or **[Microsoft docs, verify]**. Simulator error shapes come from recorded, anonymized sandbox responses, not from guesses.

- **Dataverse/CE** (mostly proven):
  - a singleton `ServiceClient` with a managed-identity token provider and `EnableAffinityCookie=false`;
  - async APIs only, with a concurrency limiter;
  - `ColumnSet` instead of all columns;
  - `UpsertRequest` with alternate keys, and `ExecuteMultiple` of 1,000 or fewer;
  - echo filtering on `InitiatingUserId`/`Depth`;
  - localized labels: compare option-set values instead;
  - retry budget versus lock duration.
- **Business Central SaaS** (mostly docs-sourced; there is no production BC SaaS adapter in the corpus):
  - webhooks are thin, delayed and expire, so they need renewal;
  - ETag/`If-Match`;
  - API v2.0 routes;
  - custom cross-reference fields through table extensions;
  - echo filtering on `systemModifiedBy`.
- **F&O** (proven):
  - `cross-company=true`, composite-key URLs, and PATCH bodies with key and read-only fields stripped;
  - 429/Retry-After applies only to OData and custom services;
  - DMF + PendingHandoff, with RecordId error attribution, and `PartiallySucceeded` with zero errors treated as failure;
  - business events and virtual tables arrive through Logic Apps, with the webhook re-subscribe gotchas.

## Template: `dotnet new nimbus-adapter`

It is rewritten from the MIT-licensed `samples/DynamicsBcDemo/BusinessCentral.Adapter`, its tests in `tests/DynamicsBcDemo.Tests`, and `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`. API names were verified against v4.2.0, and every package it needs is published as `Akaule.NimBus.*` 4.2.0.

**Generated projects (sourceName `TemplateAdapter`, system token `ExternalSystem`):**
- **`.Contracts`** references Abstractions only.
- **`.Adapter`**:
  - `Program.cs` calls `AddTemplateAdapter()` and a static `ConfigureSubscriber(...)`, so tests reuse the exact composition.
  - Wiring: `AddNimBus` with the Logging and Validation middleware, then `AddNimBusSubscriber`, `ConfigureRetryPolicies`, `WithCircuitBreaker`, the optional `UseInbox`, `AddNimBusReceiver` with `PrefetchCount=0`, and `AddNimBusDeferredProcessorHostedService`.
  - Folders: `Clients/` (the `IExternalSystemClient`, the client, Throttled/Unavailable/Rejected exceptions and `DescribeFailure`), `Resilience/`, `Handlers/`, `Mapping/` and `Options/` with `Validate()`.
- **`.ServiceDefaults`** is owned by the template, because NimBus's isn't published. Its OTel setup **leaves out the global `AddStandardResilienceHandler`**, so `AddHttpClient` plus an auth `DelegatingHandler` is safe.
- **`.Provisioner`** runs `ServiceBusTopologyProvisioner.ApplyAsync`.
- **`.Simulator`** implements pattern 27.
- **`.Ingress`** (optional) implements pattern 4 + 5.
- **`.AppHost`:**
  - It defaults to a shared dev namespace through `ConnectionStrings:servicebus`, because NimBus's emulator isn't published.
  - It runs the provisioner (`WaitForCompletion`), SQL for the inbox, the simulator and the adapter.
- **`tests/`** (MSTest). Each test file starts with `#pragma warning disable CA1707, CA2007`. Tests cover:
  - client classification against the in-process simulator;
  - retry rules through `GetRetryPolicy(type, text)`, including a decoy "429" in the stack text;
  - breaker exclusions with `ManualTimeProvider`;
  - handlers with recording fakes;
  - catalog checks: unique EventTypeIds, valid `[SessionKey]`, `ValidateCommandConsumers`, every example passes `TryValidate()`;
  - architecture checks: the handler set equals `Consumes<T>`, the deferred processor and receiver are registered, and the system client has no resilience handler.
- **Shared repo files:**
  - `deploy/main.bicep`: Container App, KEDA Service Bus scaler with managed identity, role assignments, Key Vault references.
  - `azure-pipelines.yml`: Release build and test gate, then `nb topology apply`, then deploy stages with approvals.
  - `docs/` stubs, `AGENTS.md`, `CLAUDE.md` and a pre-filled `agr.toml`.
  - `Directory.Build.props`: Release `TreatWarningsAsErrors` and NuGetAudit.
  - `Directory.Packages.props` pins `NimBusVersion`.
  - `NuGet.config` and `global.json`.

**Parameters:**
- `-n`: the project and namespace prefix.
- `SystemName`: required.
- `Endpoint`: defaults to `{SystemName}Endpoint`.
- `Contracts`: `local` or `package`. Package mode adds `ContractsPackageId`/`Version`/`PlatformType`.
- `Ingress`: defaults to false.
- `Inbox`: defaults to true.
- `NimBusVersion`: defaults to 4.2.0.

**Conditionals:**
- Only structural content is conditional. System-specific code is never a template conditional; it lives in the pack snippets.
- An excluded `template.build.props` switches every symbol on, so the raw folder compiles in place.
- `.slnx` and `.bicep` conditionals come through `SpecialCustomOperations`. Verify this works in P1.

## Skill modes

- **`new`:**
  1. Interview the user: system, directions, entities and events, change detection, contracts mode, ownership rules.
  2. Write `docs/adapter-brief.md`.
  3. Run `dotnet new nimbus-adapter`.
  4. Specialize the client from the system pack's snippets.
  5. Build the first flow in `flow` mode.
  6. Run a Release build and tests.
  7. Draft the TDD with `adapter-docs`.
- **`flow`:**
  1. Work through `flow-decisions.md`: session key, MessageId, idempotency key, missing-reference policy, ownership, echo identity, failure mapping.
  2. Write `docs/flows/<flow>.md`.
  3. Implement the flow by following the patterns.
  4. Write the required tests.
  5. Update the endpoint's `Produces`/`Consumes` declarations.
- **`review`:** check the adapter against `review.md`. Findings come out as Critical/High/Medium/Low with `file:line` and a fix.

## Phases and exit checks

| Phase | Work | Exit check |
|---|---|---|
| P0 | Step 0 plus a distribution spike | In a scratch repo, `agr sync` installs the skill for Claude and Codex (verify AGR's Codex `tools` value), the marketplace install works, and `dotnet new install` works from the installed folder on Windows |
| P1 | Template core: Contracts, Adapter, ServiceDefaults, Provisioner, Tests | CI is green (see Verification). Seeded defects each fail a test: a missing deferred processor, a duplicate EventTypeId, a missing `[SessionKey]`, a resilience handler on the client |
| P2 | Simulator, Ingress, AppHost, Bicep, pipeline | In-process fault tests pass. `aspire run` works against the dev namespace. `az bicep build` and `what-if` pass against a sandbox resource group |
| P3 | `SKILL.md`, `new` mode, the 12 ★ patterns, both checklists | Trigger eval of about 10 prompts per mode in Claude Code and Codex routes correctly. The content CI job is green |
| P4 | The three system packs and their compiled snippets | Every fact is tagged. A reviewer with production experience signs off |
| P5 | `flow` and `review` modes, plus the upstream `adapter-docs` fix in NimBus | `review` on the NimBus samples reproduces a known findings list, e.g. `D365Sales.Adapter`: `AddHttpClient` under ServiceDefaults and no retry policies |
| P6 | Dogfood, the remaining patterns, the customer handover guide, the v0.1.0 tag | See Verification |

## Verification

- **Kit CI (GitHub Actions, everything in Release):**
  - **source:** build and test the template folder in place.
  - **instantiate:** `eng/instantiate-matrix.ps1` covers default, all features on, minimal, and `Contracts=package` (against the catalog shipped in the published `Akaule.NimBus` package). For each combo it runs `dotnet new install`, instantiates, builds and tests. It also checks that no `TemplateAdapter`/`ExternalSystem` tokens are left and runs `az bicep build`.
  - **content:**
    - `Kit.Snippets` compiles.
    - A grep fails on stale API names: `PublishAsync`, `RespondAsync`, `MetricsMiddleware`, `.RetryPolicies(`, `IPermanentFailureClassifier`.
    - Frontmatter is valid and links resolve.
  - **anonymization:** a customer-name denylist, held in an Actions secret and never committed, is applied to tracked files and generated output, plus gitleaks.
- **Dogfood (P6):** an agent uses only the skill to build "Dataverse account → BC customer + ID write-back" against the simulator, in a scratch Azure DevOps project.
  - The slice covers thin-notification ingress, symmetric upsert, echo suppression, missing-reference policy, 429/503/4xx handling and the breaker.
  - The generated pipeline runs end to end into a dev environment.
  - Fault scenarios that must pass:
    - a 429 burst;
    - a 503 window opens and then closes the circuit;
    - a 4xx blocks the session, and a WebApp resubmit clears it;
    - a duplicate notification is deduplicated;
    - an echo is suppressed.
  - Record the time to the first green end-to-end run, the human interventions, the review-checklist pass rate and the lines hand-edited afterwards, then feed the lessons back into the patterns.

## NimBus follow-ups (issues on akakaule/NimBus, generic content only)

1. **`.agents/skills/adapter-docs` has stale API names.**
   - `PublishAsync` should be `Publish`.
   - `RespondAsync` and `MetricsMiddleware` don't exist.
   - `RetryPolicies(` should be `ConfigureRetryPolicies`/`AddExceptionRule`.
   - `IPermanentFailureClassifier` should be `IFailureDispositionClassifier`.
   - It says permanent failures go "to Resolver as Failed"; they are actually dead-lettered.
2. **`docs/building-adapters.md` has gaps.**
   - Its "Complete Worker shape" leaves out the deferred processor.
   - It doesn't warn that `AddHttpClient` under ServiceDefaults adds hidden retries.
   - It doesn't cover typed failures and `DescribeFailure`, or that `AddExceptionRule` matches text that includes stack traces.
   - `UseInbox`, `WithCircuitBreaker`, the retry budget versus lock duration, and adapter testing are missing.
3. **Packaging:**
   - Publish `NimBus.ServiceDefaults`, or make its resilience handler optional.
   - Ship the Service Bus emulator as a tool or container.
   - Provide Aspire hosting for the Resolver and WebApp from packages.
4. **Core gaps:**
   - Handler retries should honour Retry-After (the larger of the policy delay and the hint, capped).
   - Add a type-based `AddExceptionRule<TException>()`.
   - Add an Azure.Identity token `DelegatingHandler`.
   - Add watermark and stale-write helpers.
5. **Samples:** `samples/DynamicsBcDemo/D365Sales.Adapter/Program.cs` has no retry policies and uses `AddHttpClient` under ServiceDefaults, which contradicts the BC adapter's guidance.
6. **Decide what happens to `feat/dataverse-adapter`.** If it ships as a package, the Dataverse pack should reference it rather than duplicate its ingress code.
