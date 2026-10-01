# 20. Failure taxonomy at the client boundary

**Confidence:** production. **Applies to:** every external-system client.

## Problem

When every failure is an `HttpRequestException` (or `EnsureSuccessStatusCode`), NimBus cannot tell
"retry later" from "an operator must fix the data". The result is one of two failure modes:
- retry storms on 4xx;
- messages failing on the first 429.

The circuit breaker cannot tell an outage from bad data. Operators see `Response status code does
not indicate success: 400` with nothing to act on.

## Solution: exactly three failure types, decided in the client

| Answer | Exception | NimBus treatment |
|---|---|---|
| 429 Too Many Requests | `<System>ThrottledException(message, RetryAfter)` | Retried with exponential backoff; excluded from the breaker |
| 408, 502, 503, 504, 5xx, timeout, no connection | `<System>UnavailableException` | Retried; **counted by the breaker** |
| Any other 4xx | `<System>RejectedException(message, status, errorCode)` | Not retried: the session blocks for an operator; excluded from the breaker |
| Expected non-success answers (404 on read, an explicit "owned by…" error code, duplicate key on create) | **Not exceptions**: `null`, an outcome enum, or success | The handler decides |

**Never map a bare 409 or 412 to an outcome.** Business Central and Dataverse also use them for
concurrency conflicts ("the record changed under you"). Skipping those silently drops a change.
Key an outcome on the system's explicit error code, and let every other conflict be Rejected (or
re-read and retry once; see the system pack).

The template's `Clients/` implements this: `HttpFailure.SendAsync` and `ThrowOnFailureAsync`, plus
the three exception types.

- **The failure message carries what an operator needs**, because NimBus stores only the exception
  type and message:
  `"{operation} failed: {api} {METHOD} {path} → {status} {reason}. Body: {first 500 chars}"`.
  It never includes the request body or secrets. The response body is truncated, because vendor
  error texts can echo values.
- **Parse the vendor error code** (`error.code` in OData-style bodies) into `ErrorCode`. It lets
  you special-case a code without matching on text.
- **System-specific remaps happen here, not in handlers.** Examples:
  - a 400 that is really throttling;
  - Dataverse error codes for service-protection limits;
  - a 412 that should re-read;
  - a "record already exists" that is success.

  See the system packs' error tables.
- **Name rules**:
  - Exception names must not contain "Validation", "Serialization" or "Deserialization". The
    opt-in permanent-failure classifier dead-letters those.
  - The names *are* the retry-rule keys (`21`).
- **Caller cancellation (shutdown) propagates unchanged.** Only timeouts become Unavailable.

## Tests

One test per row of the table, using a `StubHandler`:
- 429 with Retry-After;
- each outage status;
- 4xx with an error code;
- no connection;
- a timeout versus shutdown cancellation;
- each expected non-success answer;
- the failure text contains the operation, method, path and status but not the request body, and
  is truncated.

## Anti-patterns

- Classifying by `ex.Message.Contains("429")`.
- Catching in handlers and rethrowing `new Exception("failed")`, which loses type and context.
- Marking throttling-exhausted records as "Error" in the source system as if they were rejected.

## Provenance

- Throttled/Unavailable/Rejected with `DescribeFailure` (NimBus BC sample).
- Operator-readable `[CATEGORY] … Action:` messages (production CRM/NAV adapters).
- A 400-versus-500 distinction required by an adapter review checklist.
- Throttling-exhausted kept distinct from business rejection (production F&O adapter).
