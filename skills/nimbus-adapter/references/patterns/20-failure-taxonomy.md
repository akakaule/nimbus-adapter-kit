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

- **The failure message carries what an operator needs, and nothing personal.** NimBus stores the
  exception type and message verbatim in the audit trail and does **not** mask them. The format is:
  `"{operation} failed: {api} {METHOD} {path} → {status} {reason}. Error code: {code}."`
  - It never contains a request or response body. Vendor error messages echo field values (names,
    e-mail addresses), and truncating them doesn't remove that.
  - It never contains a query string.
  - The operation names the record by its key. The operator looks the code and the record up in
    the system itself (for example the Dataverse plug-in trace log, or BC telemetry).
- **Parse the vendor error code** (`error.code` in OData-style bodies) into `ErrorCode`. It lets
  you special-case a code without matching on text. Keep it only if it looks like a code
  (`HttpFailure.SanitizeErrorCode`: letters, digits, `_ . -`, at most 64 characters). Anything else
  is free text.
- **A success that isn't the expected shape is Unavailable, never "not found".** Examples are a
  `200` with `null`, `{}`, a missing `value` collection or a proxy's HTML page. Reading it as "not
  found" makes the caller create a duplicate. Only a 404 (or an empty, well-formed collection) means
  not found.
- **A failure to get a token is classified too** (the template's `BearerTokenHandler`):
  - a transient identity-service failure is Unavailable and retried;
  - misconfiguration is also Unavailable, with a message that says so. It fails every message, so
    it should open the breaker and alert, not block every session one by one.
- **System-specific remaps happen here, not in handlers.** Examples:
  - a 400 that is really throttling;
  - Dataverse error codes for service-protection limits;
  - a 412 that should re-read;
  - a "record already exists" that is success.

  See the system packs' error tables.
- **Name rules**:
  - Exception names must not contain "Validation", "Serialization" or "Deserialization". The
    opt-in permanent-failure classifier dead-letters those.
  - The full type names *are* the retry-rule keys (`21`).
- **Caller cancellation (shutdown) propagates unchanged.** Only timeouts become Unavailable.

## Tests

One test per row of the table, using a `StubHandler`:
- 429 with Retry-After;
- each outage status;
- 4xx with an error code;
- no connection;
- a timeout versus shutdown cancellation;
- each expected non-success answer;
- a malformed success (`null`, `{}`, HTML) is Unavailable, never "not found";
- the failure text contains the operation, method, path, status and code, but no request or
  response body and no query string.

## Anti-patterns

- Classifying by `ex.Message.Contains("429")`.
- Catching in handlers and rethrowing `new Exception("failed")`, which loses type and context.
- Marking throttling-exhausted records as "Error" in the source system as if they were rejected.

## Provenance

- Throttled/Unavailable/Rejected with `DescribeFailure` (NimBus BC sample).
- Operator-readable `[CATEGORY] … Action:` messages (production CRM/NAV adapters).
- A 400-versus-500 distinction required by an adapter review checklist.
- Throttling-exhausted kept distinct from business rejection (production F&O adapter).
