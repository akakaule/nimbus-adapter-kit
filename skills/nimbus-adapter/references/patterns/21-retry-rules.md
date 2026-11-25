# 21. Retry rules

**Confidence:** production. **Applies to:** every subscriber.

## Problem

Retries were the most common root cause in production adapter fixes:
- **Hidden retries.** A `ServiceClient` retrying 5 × 70 s, or an HttpClient resilience handler,
  ran inside one delivery attempt and outlived the Service Bus lock. The message was redelivered
  while still being processed.
- **Retry rules matching the wrong failures**, because they keyed on text like "429".
- **No retries at all**, so every 429 needed an operator.
- **Retries that gave up during an update window**, failing hundreds of messages at once.

## Solution: NimBus owns retries, keyed on exception types

```csharp
subscriber.ConfigureRetryPolicies(policies => policies
    .AddExceptionRule<ErpThrottledException>(new RetryPolicy
    {
        MaxRetries = 8, Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(15), MaxDelay = TimeSpan.FromMinutes(5), Jitter = JitterMode.Bounded,
    })
    .AddExceptionRule<ErpUnavailableException>(new RetryPolicy { MaxRetries = 8, Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromMinutes(10), Jitter = JitterMode.Bounded })
    .AddExceptionRule<MissingReferenceException>(new RetryPolicy { MaxRetries = 3, BaseDelay = TimeSpan.FromSeconds(60) }));

// The server's Retry-After travels on the exception; NimBus never retries sooner than that.
public sealed class ErpThrottledException(string message, TimeSpan? retryAfter)
    : ErpException(message), IRetryAfterHint
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
```

- **Each NimBus retry is a new, broker-scheduled, audited attempt.** It is visible in the WebApp,
  and it does not hold the lock while waiting.
- **Rules match the exception's type.** `AddExceptionRule<TException>` (NimBus 4.3 and later)
  matches the handler's exception or any inner exception of that type, subclasses included.
  - Never use the text overload, `AddExceptionRule("…", policy)`. It matches a case-insensitive
    substring of `"{inner exception} {exception}"`, which includes messages and **stack traces**,
    so `"429"` also matches a line number, an id or an echoed response body.
  - Rules are checked in registration order and the first match wins, so register a subclass
    before its base type.
  - `ConfigureRetryPolicies` on the subscriber replaces any `IRetryPolicyProvider` registered in
    DI. Put every rule in the one place.
  - A test proves the typing: a rejection whose message names a retried type gets no policy.
- **No default policy, deliberately.** A failure without a rule (a rejection) fails at once and
  blocks its session for an operator. Add `SetDefaultPolicy` only with a reason.
- **No other retry layer.**
  - No `AddStandardResilienceHandler`. `Akaule.NimBus.ServiceDefaults` leaves it out unless you
    set `UseStandardResilienceHandler`, and an architecture test guards that.
  - No Polly around client calls, and no loops.
  - SDK clients with built-in retry (Dataverse `ServiceClient`): set `MaxRetryCount` and
    `RetryPauseTime` so the worst case fits well inside the lock (about 5 minutes with renewal),
    or disable them and let NimBus retry.
- **Budget.** The total retry time for outages must outlast a realistic outage or update window,
  and several circuit-breaker break durations (a template test checks this). The template's
  default (8 retries, 30 s doubling to 10 min) lasts about 45 minutes.
  - SaaS update windows can be much longer: the BC pack documents windows of hours.
  - Raise `UnavailableMaxRetries` and `CircuitBreakDurationSeconds` together. While the circuit is
    open the receivers are paused, but each half-open probe uses up one retry of one message.
  - A longer break duration means fewer probes, so fewer attempts are used up during the window.
- **Retry-After.** The Throttled exception implements `IRetryAfterHint`. NimBus waits for the
  longer of the policy delay and the hint, capped at the policy's `MaxDelay`. The hint only
  stretches a delay; the rule still decides whether there is a retry at all.
  - Set `MaxDelay` above the longest Retry-After the system sends, or the cap cuts the wait short.
- **Non-idempotent writes**: only retry what certainly did not commit (`12`).

## Tests

`ResilienceTests` in the template:
- throttled and unavailable failures get exponential policies;
- **a rejection whose body mentions "429" and "503" gets none** (the decoy);
- an unclassified exception gets none;
- the throttled exception exposes its Retry-After as `IRetryAfterHint`;
- a missing reference gets its short policy;
- the total retry budget is bigger than 3 break durations.

Look policies up with the exception NimBus sees, the handler's exception inside the pipeline's
wrapper: `policies.GetRetryPolicy(eventTypeId, new EventContextHandlerException(inner))`.

## Anti-patterns

- `AddExceptionRule("429", …)`, or any other text rule.
- `AddExceptionRule<Exception>(…)`, which matches everything.
- A Throttled exception that drops the server's Retry-After.
- A retry loop in the handler.
- `ServiceClient` defaults (5 × 70 s) under a 5-minute lock.
- `.Result` inside a Polly predicate.

## Provenance

- Typed rules with bounded jitter, a Retry-After hint and no default policy (NimBus BC sample,
  NimBus 5.6.0 source).
- Lock renewal raised to 10 minutes after the ServiceClient retry outlived it, and a per-message
  retry budget (production CE and F&O adapters).
- Adapters without any retry policy needed an operator for every 429 (production CRM adapters).
