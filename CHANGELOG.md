# Changelog

## 0.1.0 (unreleased)

First version.

- **Skill `nimbus-adapter`** with the modes `new`, `flow` and `review`, plus the flow-decision and
  review checklists.
- **Pattern catalog**: 29 patterns plus anti-patterns, distilled from production adapters on the
  NimBus architecture and from the NimBus samples. Anonymized.
- **System packs** for Dataverse / Dynamics 365 CE, Business Central and Finance & Operations:
  - READMEs with tagged facts;
  - simulator specifications;
  - client snippets that compile inside the template's adapter.
- **Template `dotnet new nimbus-adapter`** (NimBus 4.2, .NET 10):
  - **Worker adapter**: typed failures (Throttled / Unavailable / Rejected), exception-type retry
    rules, a circuit breaker, a SQL inbox, a missing-reference policy and deferred replay.
  - **ServiceDefaults without hidden HttpClient retries.**
  - **Simulator**: fault injection plus an upstream driver.
  - **Local stack and deployment**:
    - provisioner;
    - Aspire AppHost;
    - Bicep for a Container App with managed identity and a KEDA scale rule;
    - an Azure DevOps pipeline with a test gate and dev/test/prod stages.
  - **51 tests**:
    - client classification and contract tests against the simulator;
    - retry rules and the circuit breaker;
    - handlers;
    - the pipeline on the in-memory transport;
    - catalog rules and architecture guards.
- **CI**: the template builds in place, every option combination instantiates, builds and tests,
  the snippets compile, content checks pass, and the anonymization and secret scans are clean.
