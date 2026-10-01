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
    .AddExceptionRule(nameof(ErpThrottledException), new RetryPolicy
    {
        MaxRetries = 8, Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(15), MaxDelay = TimeSpan.FromMinutes(5), Jitter = JitterMode.Bounded,
    })
    .AddExceptionRule(nameof(ErpUnavailableException), new RetryPolicy { MaxRetries = 8, Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromMinutes(10), Jitter = JitterMode.Bounded })
    .AddExceptionRule(nameof(MissingReferenceException), new RetryPolicy { MaxRetries = 3, BaseDelay = TimeSpan.FromSeconds(60) }));
```

- **Each NimBus retry is a new, broker-scheduled, audited attempt.** It is visible in the WebApp,
  and it does not hold the lock while waiting.
- **Rules match a substring of `"{inner exception} {exception}"`** (type names, messages and
  **stack traces**). Key on exception *type names* (`nameof(...)`), never on a status code or
  message text.
- **No default policy, deliberately.** A failure without a rule (a rejection) fails at once and
  blocks its session for an operator. Add `SetDefaultPolicy` only with a reason.
- **No other retry layer.**
  - No `AddStandardResilienceHandler`. The template's ServiceDefaults leave it out, and an
    architecture test guards that.
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
- **Retry-After.** NimBus handler retries use the policy schedule; the server's Retry-After hint
  is captured on the Throttled exception but not yet honoured. Pick a `BaseDelay` above the
  system's usual Retry-After.
- **Non-idempotent writes**: only retry what certainly did not commit (`12`).

## Tests

`ResilienceTests` in the template:
- throttled and unavailable failures get exponential policies;
- **a rejection whose body mentions "429" and "503" gets none** (the decoy);
- a missing reference gets its short policy;
- the total retry budget is bigger than 3 break durations.

Use the real matched text: `$"{inner} {new EventContextHandlerException(inner)}"`.

## Anti-patterns

- `AddExceptionRule("429", …)`.
- `AddExceptionRule("Exception", …)`, which matches everything.
- A retry loop in the handler.
- `ServiceClient` defaults (5 × 70 s) under a 5-minute lock.
- `.Result` inside a Polly predicate.

## Provenance

- Exception-name rules with bounded jitter and no default policy (NimBus BC sample).
- Lock renewal raised to 10 minutes after the ServiceClient retry outlived it, and a per-message
  retry budget (production CE and F&O adapters).
- Adapters without any retry policy needed an operator for every 429 (production CRM adapters).
