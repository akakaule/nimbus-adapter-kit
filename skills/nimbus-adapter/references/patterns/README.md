# NimBus adapter patterns

Each pattern solves one problem that kept coming back across the production adapters on the
NimBus architecture and the NimBus samples. The sources cover:
- Dynamics 365 CE / Dataverse;
- Finance & Operations;
- Business Central and NAV;
- HR and survey SaaS;
- file feeds.

They are written generically. Provenance names the kind of adapter, never the customer.

Every pattern file has the same sections:
- **Problem**;
- **When / when not**;
- **Solution** (with the NimBus API);
- **Per system**;
- **Tests**;
- **Anti-patterns**;
- **Provenance**.

The confidence label says how far the pattern is proven:
- *production*: used in production adapters;
- *sample*: shown in a NimBus sample against a simulator;
- *design*: recommended, not yet proven in production.

## Index

| # | Pattern | Problem it solves | Confidence |
|---|---|---|---|
| **A** | **Contracts and topology** | | |
| 01 | [Catalog and event conventions](01-catalog-and-event-conventions.md) | Events that collide, can't be validated, or leak personal data | production |
| 02 | [Session keys](02-session-keys.md) | Changes applied out of order, or one failure blocking unrelated work | production |
| 03 | [One endpoint per process](03-endpoint-per-process.md) | Silent non-delivery, stuck parked messages, cross-talk | production |
| **B** | **Inbound (system → bus)** | | |
| 04 | [Thin notification, then re-read](04-thin-notification-reread.md) | Building payloads inside the source system; stale or partial data | production |
| 05 | [Deterministic MessageId + inbox](05-deterministic-message-id-and-inbox.md) | Duplicates from redelivery and replays | production |
| 06 | [Reliable publish](06-reliable-publish.md) | A change committed but never published, or published twice | production |
| 07 | [Watermark polling](07-watermark-polling.md) | Systems that cannot notify | production |
| 08 | [Echo-loop prevention](08-echo-loop-prevention.md) | The adapter's own writes coming back as new changes | production |
| 09 | [Initial and bulk sync](09-initial-and-bulk-sync.md) | Go-live loads and re-syncs | production |
| 10 | [Large-payload claim check](10-large-payload-claim-check.md) | Messages over the broker's size limit | production |
| **C** | **Outbound (bus → system)** | | |
| 11 | [Handler layering](11-handler-layering.md) | Untestable handlers mixing I/O, mapping and policy | production |
| 12 | [Idempotent upsert](12-idempotent-upsert.md) | Duplicates and lost updates on retry, resubmit and out-of-order create/update | production |
| 13 | [Cross-references and ID write-back](13-cross-references-and-id-writeback.md) | Knowing the other system's key | production |
| 14 | [Missing-reference policy](14-missing-reference-policy.md) | A parent or lookup not there yet | production |
| 15 | [Stale-write guard](15-stale-write-guard.md) | Older data overwriting newer data | production |
| 16 | [Field ownership and business outcomes](16-field-ownership-and-outcomes.md) | Overwriting data the other system masters | production |
| 17 | [Reference-data cache](17-reference-data-cache.md) | Chatty lookups and throttling | production |
| 18 | [PendingHandoff for asynchronous work](18-pending-handoff.md) | Targets that process in batches or jobs | production |
| 19 | [Request/reply](19-request-reply.md) | Synchronous questions over the bus | sample |
| **D** | **Resilience** | | |
| 20 | [Failure taxonomy at the client boundary](20-failure-taxonomy.md) | Retrying data errors, failing on throttling | production |
| 21 | [Retry rules](21-retry-rules.md) | Retry storms, hidden retries, lock expiry | production |
| 22 | [Circuit breaker](22-circuit-breaker.md) | Burning retries during an outage or update window | sample |
| 23 | [Concurrency and throughput](23-concurrency-and-throughput.md) | Throttling and lock contention under load | production |
| **E** | **Operations and quality** | | |
| 24 | [Auth and secrets](24-auth-and-secrets.md) | Credentials in code, hand-written token caches | production |
| 25 | [Observability and personal data](25-observability-and-pii.md) | Unactionable failures and PII in logs | production |
| 26 | [Testing strategy](26-testing-strategy.md) | Adapters that only work in production | production |
| 27 | [Vendor-API simulator](27-vendor-api-simulator.md) | No way to run or test without the real system | production |
| 28 | [Deploy and pipeline](28-deploy-and-pipeline.md) | Untested deployments, drift between environments | production |
| 29 | [Functions hosting](29-functions-hosting.md) | When the adapter must be an Azure Function | production |
| | [Anti-patterns](anti-patterns.md) | What broke production adapters | production |

## Which patterns does my flow need?

**Every flow:** 01, 02, 11, 20, 21, 26.

**Bus → system (the adapter writes):** add 12, 14, 16, and 13 when keys cross systems.
- Unordered or multiple sources: 15.
- Chatty lookups: 17.
- Batch or job-based targets: 18.

**System → bus (the adapter publishes):** add 04 (or 07 when the system cannot notify), 05, 06
and 08.
- Go-live data: 09.
- Big documents: 10.

**Worker hosting (template default):** 22, 23. **Functions:** 29.

**Before go-live:** 24, 25, 27, 28.
