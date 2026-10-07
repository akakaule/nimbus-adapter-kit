# Changelog

## 0.2.0 (unreleased)

Fixes from a cross-model (Codex) review of 0.1.0, plus the template options it found missing.

- **Licence**: MIT.
- **Template options**:
  - `--Ingress true`: thin change notification → re-read → publish with a deterministic MessageId,
    then complete. Dead-letter reason codes; queue in the provisioner and the Bicep; a simulator
    driver; tests.
  - `--Endpoint`: a custom endpoint (topic) name.
  - `--Contracts package`: the catalog from a shared platform package (with its feed mapped in
    NuGet.config, and no topology step in the adapter pipeline).
- **No personal data in failure texts.** NimBus stores exception messages unmasked in the audit
  trail. Failure texts now carry operation, method, path (no query), status and a sanitized
  vendor error code, but no response body.
- **Precise retry rules.** They are keyed on `<full type name>:`. Error codes can't contain `:`,
  so vendor text can't steer a rejection into a retry rule.
- **Malformed successes.** A `200` that isn't the expected shape is Unavailable, never "not found"
  (template client and all three snippets).
- **Token failures.** A failure to get a token is classified as Unavailable, with distinct
  "transient" and "misconfigured" texts.
- **No-op writes are skipped.** The handler compares the current record with the mapped one. The
  snippets return the mapper's exact shape.
- **Key Vault.** The inbox connection string is read by the Container App at runtime through a
  Key Vault reference (*Key Vault Secrets User* for the adapter's identity), not passed through the
  deployment.
- **Packs**:
  - more facts tagged;
  - **[proven in production]** now means "seen in production code, docs or fix history";
  - the DMF settlement text no longer copies F&O's error text into the audit trail.
- **Skill and docs.** Patterns 20, 21 and 25, the review checklist and the playbooks are updated,
  and the commands are PowerShell-ready.

## 0.1.0

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
