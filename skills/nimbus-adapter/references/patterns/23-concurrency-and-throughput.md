# 23. Concurrency and throughput

**Confidence:** production. **Applies to:** adapters under real load.

## Problem

Throughput problems found in production reviews:
- **Too much parallelism**: the target throttles, and every call becomes a retry.
- **Hidden serialization**:
  - sync SDK calls taking a per-client lock;
  - `Task.Run` around sync I/O;
  - a sequential polling worker.
- **Scaling on the wrong signal**: CPU or HTTP instead of queue length.

## Solution

- **`MaxConcurrentSessions` = parallel ordered streams.** Each session has at most one call in
  flight. Size it from the target's limits. One session with three writes means three calls. Start
  low (4–16), measure throttling, then raise.
- **Scale out on the backlog.** Use a KEDA `azure-servicebus` rule on the endpoint's subscription
  with managed identity (the template's Bicep), and keep `minReplicas` ≥ 1 for steady sessions.
  Remember that replicas × `MaxConcurrentSessions` is your real concurrency against the target.
- **Async all the way.** No `.Result`, `.Wait()` or `Task.Run` around sync SDK calls.
- **Reuse clients.** One `HttpClient` per target from the factory. A singleton for SDK clients that
  are thread-safe (the Dataverse `ServiceClient` is, with async methods).
- **Respect the target's parallelism hints.** For example, Dataverse returns a recommended
  degree of parallelism; cap the adapter's concurrency per replica to it. See the pack.
- **Priority lanes.** Bulk and urgent traffic in separate endpoints, and processes with their own
  concurrency (`03`).
- **Payloads**: narrow column sets, `$select`, and no `ColumnSet(true)`.

## Tests

- A load test against the simulator with injected 429s: throughput degrades gracefully, and
  nothing fails permanently.
- An architecture test with no sync-over-async (analyzers help: `CA1849`, AsyncFixer).

## Anti-patterns

- An HTTP or CPU scaler on a Service Bus worker.
- A new `ServiceClient` per message.
- A `ServiceClient` without disabling the affinity cookie (all traffic pinned to one node).
- `MaxConcurrentSessions = 100` "to be fast".

## Provenance

- Sync `ServiceClient` calls serialized by a per-instance lock (verified from IL in a production
  CE adapter).
- A concurrency limiter sized from the recommended degree of parallelism (same adapter).
- KEDA Service Bus scaler versus an HTTP scaler (production F&O review).
