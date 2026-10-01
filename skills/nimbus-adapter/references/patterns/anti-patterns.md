# Anti-patterns that broke production adapters

Each entry: what it looks like, what it caused, and the pattern that prevents it. All were
observed in production adapters or their reviews.

## Messages lost or stuck

| Looks like | Caused | Instead |
|---|---|---|
| `GetSessionId()` default (random) on many events | Updates overtaking creates | `02` |
| A constant fallback session key | One failure blocking every keyless event | `02` |
| No deferred-replay processor or trigger | Parked messages never resumed after a resubmit | `03`, `29` |
| No `[ServiceBusTrigger]` class under Functions | Messages Pending forever | `29` |
| `async void` job methods | Exceptions lost, process crashes | `06` |
| `new Task(() => …)` never started, then awaited | Handler hangs until the lock expires | `06` |
| Fire-and-forget writes or deletes (`_ = …Async()`) | Silent partial failures | `06` |
| Queue message deleted before processing | Data loss on crash | `06` |
| Work kept in an in-memory channel between webhook and publish | Lost on restart | `06` |
| Unmatched input segments dropped by a TODO no-op | Silent data loss | `11` |

## Duplicates and wrong data

| Looks like | Caused | Instead |
|---|---|---|
| A GET helper returning "" or null on any HTTP error, then POST | Duplicate records on transient errors | `12`, `20` |
| Lookup by a field the create never sets | Duplicates piling up | `12`, `13` |
| Batch dedupe keyed on the session only ("last wins") | Lifecycle events silently collapsed | `18` |
| Second-resolution timestamps as an ordering signal | Wrong winner on fast updates | `15` |
| Comparing localized labels (option-set text) | Mismatches per user language | `01`, Dataverse pack |
| Rewriting target-owned fields on update | Status downgrades | `16` |
| Static non-thread-safe dictionary cache | Intermittent wrong mapper or type | `17` |

## Retries and throughput

| Looks like | Caused | Instead |
|---|---|---|
| SDK retry (5 × 70 s) under a 5-minute lock | Redelivery while still processing | `21` |
| HttpClient resilience handler from ServiceDefaults | Hidden retries, unauditable timeouts | `21` |
| Retry rule on "429" text | Rejections retried, outages not | `20`, `21` |
| No retry policy at all | An operator needed for every 429 | `21` |
| Sync SDK calls, `Task.Run` around sync I/O, `.Result` in predicates | Serialized throughput, deadlocks | `23` |
| New SDK client per message, or per resolution | Throttling, socket exhaustion | `23` |
| ~10 queries per message | Throttled at ~2 msg/s | `17`, `11` |
| CPU or HTTP scaler on a Service Bus worker | No scale-out under backlog | `23`, `28` |

## Security and operations

| Looks like | Caused | Instead |
|---|---|---|
| Secrets in csproj, appsettings or source | Credential exposure | `24` |
| Auth commented out or disabled outside prod | Open endpoints | `24` |
| Payload, IDoc or reflection dumps in logs | Personal data in logs | `25` |
| Key Vault secrets overwritten by a deploy | Outage after deploy | `24`, `28` |
| CD pipeline without tests; PR pipeline removed | Untested releases | `26`, `28` |
| `BuildServiceProvider()` during registration; captive singletons | Duplicate singletons, stale scoped state | review checklist |
| Logic Apps without inventory, alerting or promotion flow | Invisible failures, drift | F&O pack |
