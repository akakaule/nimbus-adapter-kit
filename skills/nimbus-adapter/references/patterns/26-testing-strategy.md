# 26. Testing strategy

**Confidence:** production. **Applies to:** every adapter.

## Problem

Production adapters had:
- zero tests;
- tests that only checked delegation, while mappers and batch jobs went untested;
- disabled integration tests;
- CD pipelines without a test gate.

Defects then showed up as stuck sessions and duplicate records in production.

## Solution: the test groups the template generates

| Group | Proves | Tool |
|---|---|---|
| **Client classification** | Every HTTP answer becomes the right exception or outcome, with an actionable text | `StubHandler` (`HttpMessageHandler`) |
| **Contract against the simulator** | The client and the simulator agree on routes, codes and bodies | `WebApplicationFactory<SimulatorEntryPoint>` |
| **Retry rules** | Throttled and unavailable get policies; rejections don't, even with decoy "429" text; the budget is long enough | `DefaultRetryPolicyProvider.GetRetryPolicy(eventTypeId, matchedText)` |
| **Circuit breaker** | Outages open it; data problems and throttling don't | `EndpointCircuitBreaker` + a manual `TimeProvider` |
| **Mapper** | Every mapping rule, including nulls, trimming, codes and truncation | Plain unit tests |
| **Handler** | The happy path, idempotency, a missing reference, business outcomes, failure propagation | A recording fake client, `NullLogger` |
| **Pipeline** | The real subscriber composition on the in-memory transport: delivery and inbox dedup | `AddNimBusTestTransport(ConfigureSubscriber, endpoint)` + `AddNimBusInMemoryInbox` |
| **Catalog** | Unique EventTypeIds, a `[SessionKey]` that is `[Required]`, valid examples, descriptions, prefixes, command consumers | Reflection over the `Platform` |
| **Architecture** | Receiver and deferred replay registered, handlers = `Consumes<T>`, no resilience handler on the client | Build the host from `AddAdapter()`, don't start it |

Plus, for inbound flows:
- **Publisher**: a notification gives one publish with the expected event, session id and
  MessageId. Use a capturing `IPublisherClient`.
- **Polling**: page boundaries, restart republishes and parent/child ordering (`07`).

Rules:
- **Assert on side effects** (what the fake external system received), not on internal flags.
- **No real external systems in unit tests.** Use the simulator for contract tests, and an
  optional, environment-gated integration test project against a sandbox.
- **Release builds in CI**, with the test gate before any deployment (`28`).
- **Mutation check when adding guards.** When you add a test meant to catch a mistake, break the
  code once to see it fail. The kit's template tests were proven this way.

## Anti-patterns

- Tests that mock the client and assert the mock was called, with no outcome checked.
- `Thread.Sleep` in tests: use a manual `TimeProvider`.
- Skipping the pipeline test because "the handler test covers it". The inbox and retry wiring are
  only proven by the pipeline.

## Provenance

- The test groups of the NimBus BC sample and the kit template.
- 319 unit tests and SQL-container integration tests in a production CRM/NAV adapter.
- An adapter scorecard rating testability lowest where tests were absent.
