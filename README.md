# NimBus Adapter Kit

Patterns, an agent skill and a `dotnet new` template for building
[NimBus](https://github.com/akakaule/NimBus) adapters. These are the processes that connect an
external system (Dataverse / Dynamics 365 CE, Business Central, Dynamics 365 Finance &
Operations, …) to a NimBus event bus.

The kit captures what production adapters on the NimBus architecture taught the hard way:
- session-key choice;
- idempotent upserts;
- echo loops;
- missing references;
- throttling and outages;
- testing against a simulator.

You start from a scaffold that already gets these right.

| Part | Where | What it gives you |
|---|---|---|
| Pattern catalog | [`skills/nimbus-adapter/references/patterns/`](skills/nimbus-adapter/references/patterns/) | Each pattern covers one recurring adapter problem: when it applies, the NimBus API that solves it, the tests to write and the anti-patterns to avoid |
| System packs | [`skills/nimbus-adapter/references/systems/`](skills/nimbus-adapter/references/systems/) | Auth, change detection, write APIs, throttling and gotchas for Dataverse/CE, Business Central and F&O, with compiled snippets |
| Agent skill | [`skills/nimbus-adapter/`](skills/nimbus-adapter/) | `nimbus-adapter`, with modes `new` (scaffold an adapter), `flow` (add one integration flow) and `review` (check an adapter against the checklist). Works with Claude Code, Codex and GitHub Copilot |
| Template | [`skills/nimbus-adapter/template/`](skills/nimbus-adapter/template/) | `dotnet new nimbus-adapter`: a Worker / Container App adapter with the NimBus wiring, typed failures, retry rules, circuit breaker, simulator, tests, Aspire AppHost, Bicep and an Azure DevOps pipeline |

> Status: under construction. See the [plan](docs/plan/2026-10-01-adapter-kit.md).
