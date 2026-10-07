# Adapter review checklist

Each item: what to check, where to look, and the pattern with the fix. Suggested severity in
brackets; adjust it to the actual impact.

## Catalog

- [ ] **[High]** **Every event has a deliberate `[SessionKey]`** on the aggregate's id, or overrides
  `GetSessionId()`.
  - It is never random, constant, a timestamp or empty.
  - Look for the session key attribute, and for `Guid.NewGuid()` near session ids.
  - See `patterns/02`.
- [ ] **[High]** **EventTypeIds are unique** across every catalog that shares the Service Bus
  namespace (the class name is the id). A catalog test enforces it. See `patterns/01`.
- [ ] **[Medium]** **Each event has its metadata:** `[Description]`, a valid static `Example`,
  `[Required]` on what consumers need, `[Sensitive]` on personal data, and the producing-system
  prefix. See `patterns/01`.
- [ ] **[High]** **The endpoint's `Consumes<T>()` matches the handlers exactly**, and every
  published event is declared with `Produces<T>()`. See `patterns/03`.
- [ ] **[Medium]** **Each command has exactly one consumer**
  (`PlatformValidation.ValidateCommandConsumers`). See `patterns/01`.

## Composition and hosting

- [ ] **[Critical]** **Deferred replay is wired:** `AddNimBusDeferredProcessorHostedService` in a
  worker, or the second, non-session `[ServiceBusTrigger]` class in Functions. Without it, parked
  messages never resume. See `patterns/03` and `patterns/29`.
- [ ] **[Critical]** **Under Functions, a `[ServiceBusTrigger]` class exists for the endpoint.**
  `AddNimBusSubscriber` alone receives nothing, and messages stay Pending. See `patterns/29`.
- [ ] **[High]** **One endpoint per process.** No second `AddNimBusSubscriber` with another
  endpoint. See `patterns/03`.
- [ ] **[High]** **No `BuildServiceProvider()` inside registration, and no captive dependencies.**
  Watch for a singleton holding a scoped `DbContext`, or a per-call client built inside a
  singleton.
- [ ] **[Medium]** **The adapter never creates topology at run time.** Topology comes from the
  provisioner or `nb topology apply`. See `patterns/28`.

## External-system client

- [ ] **[High]** **Failures are classified** into Throttled (429, with Retry-After), Unavailable
  (408/5xx, timeout, no connection) and Rejected (other 4xx, with the vendor code). Look for
  `EnsureSuccessStatusCode` or a raw `HttpRequestException` escaping to handlers. See `patterns/20`.
- [ ] **[Critical]** **A failed read is never treated as "not found".** Look for a GET helper that
  returns null or an empty string on any error before a create. That turns a transient failure
  into a duplicate record. See `patterns/12` and `anti-patterns.md`.
- [ ] **[High]** **No hidden retries.** Check for:
  - `AddStandardResilienceHandler` or Polly on the client, or a ServiceDefaults that adds one to
    every `HttpClient`;
  - SDK-internal retries (for example ServiceClient `MaxRetryCount` and `RetryPauseTime`) that
    exceed the Service Bus lock.

  See `patterns/21`.
- [ ] **[High]** **Failure messages are actionable but safe:** operation, method, path (no query
  string), status and a sanitized error code. Never a request or response body, never secrets.
  NimBus stores the text unmasked in the audit trail. See `patterns/20` and `patterns/25`.
- [ ] **[Critical]** **A malformed success is never "not found".** A `200` with `null`, `{}` or no
  `value` collection must throw (Unavailable), not lead to a create. See `patterns/20`.
- [ ] **[High]** **Retry rules are keyed on full type names plus a colon**, and no untrusted text can
  name a retried exception type. See `patterns/21`.
- [ ] **[High]** **The client is a singleton or comes from the factory, with async APIs only.**
  - Not built per message, and no sync-over-async (`.Result`, `.Wait()`).
  - Dataverse: one `ServiceClient`, async methods. See the system pack.
- [ ] **[Medium]** **Tokens come from `Azure.Identity` credentials**, with no hand-written,
  non-thread-safe token cache. See `patterns/24`.

## Handlers and mapping

- [ ] **[High]** **Writes are idempotent:** an upsert on a stable key, so a redelivery or resubmit
  is safe. Look for a plain POST/create in consumed-event handlers. See `patterns/12`.
- [ ] **[High]** **Missing references have an explicit policy:** throw with the remedy, or skip
  with a warning. Never a silent no-op or a null link. See `patterns/14`.
- [ ] **[Medium]** **Mapping is a pure function and is unit-tested.** No I/O inside mappers. See
  `patterns/11`.
- [ ] **[Medium]** **Business outcomes are results, not exceptions.** Examples: "owned by the other
  system", "no change". See `patterns/16`.
- [ ] **[High]** **Target-owned fields and state fields are not overwritten on update**, and state
  fields are not set on create. See `patterns/16`.
- [ ] **[Critical]** **No `async void`, no never-started `Task`, no fire-and-forget writes**
  (`_ = SomethingAsync()`). See `anti-patterns.md`.
- [ ] **[High]** **Payloads are never logged** (look for `JsonConvert.SerializeObject(message)` in
  log calls). See `patterns/25`.

## Resilience

- [ ] **[High]** **Retry rules use exception type names**, with exponential backoff and bounded
  jitter for Throttled and Unavailable. They are never keyed on status codes or message text. See
  `patterns/21`.
- [ ] **[Medium]** **No default retry policy unless deliberate.** A rejection should block the
  session for an operator, not retry blindly. See `patterns/21`.
- [ ] **[Medium]** **Worker hosting has a circuit breaker** that excludes rejections, throttling
  and missing references. See `patterns/22`.
- [ ] **[Medium]** **The total retry budget outlasts the expected outage and break duration.** See
  `patterns/21`.
- [ ] **[Medium]** **`PrefetchCount = 0` with a circuit breaker, and `MaxConcurrentSessions` sized
  to the target's limits.** See `patterns/23`.

## Inbound (publishing)

- [ ] **[High]** **MessageIds are deterministic per source change, and the consumer uses the
  inbox.** See `patterns/05`.
- [ ] **[High]** **Publishing is reliable.** Either:
  - an outbox in the same transaction as the source write; or
  - publish-then-complete from an ingress queue, with no deletion before processing and no
    in-memory work queue.

  See `patterns/06`.
- [ ] **[High]** **Echo-loop prevention:** the source filters the integration identity, and the
  sink skips no-op writes. See `patterns/08`.
- [ ] **[Medium]** **Polling cursors are persisted and advanced only after a successful
  publish.** See `patterns/07`.

## Tests and pipeline

- [ ] **[High]** **The CI pipeline builds and tests in Release before any deployment** (a test
  gate). See `patterns/28`.
- [ ] **[Medium]** **These test groups exist:**
  - client classification;
  - retry rules (including decoy status codes in the text);
  - breaker exclusions;
  - handler decisions;
  - mapping;
  - catalog rules;
  - architecture guards.

  See `patterns/26`.
- [ ] **[Medium]** **A simulator or emulator lets the adapter run locally** without the real
  system. See `patterns/27`.

## Security and operations

- [ ] **[Critical]** **No secrets in the repo** (appsettings, csproj, pipeline YAML, Bicep
  parameters). Key Vault references or managed identity only. See `patterns/24`.
- [ ] **[Critical]** **Any HTTP ingress endpoint is authenticated**, and auth is not disabled
  "outside prod". See `patterns/24`.
- [ ] **[Medium]** **Logs carry correlation ids**, metrics or traces exist, and someone is
  alerted when the circuit opens. See `patterns/25`.
- [ ] **[Low]** **README and AGENTS.md describe how to build, test, run locally and deploy.**
