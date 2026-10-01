# 22. Circuit breaker

**Confidence:** sample. Shown against a simulator, with core fixes driven by it; production
adapters on the predecessor platform had no breaker. **Applies to:** worker-hosted adapters
calling a system with outages or update windows.

## Problem

During an outage every message fails, retries, fails again, and burns its retry budget. Hundreds
of messages then end up failed, and an operator resubmits them one by one. Meanwhile the adapter
hammers a system that is down.

## Solution

```csharp
subscriber.WithCircuitBreaker(circuit =>
{
    circuit.MinimumThroughput = 5;
    circuit.FailurePercentageThreshold = 50;
    circuit.SamplingWindow = TimeSpan.FromMinutes(2);
    circuit.BreakDuration = TimeSpan.FromMinutes(1);
    circuit.HalfOpenProbeCount = 1;
    circuit.Exclude<ErpRejectedException>();      // data problems are not outages
    circuit.Exclude<ErpThrottledException>();     // throttling is pacing, not an outage
    circuit.Exclude<MissingReferenceException>();
});
```

- **Open:** the receivers are paused, so no messages are taken. **Half-open:** a probe goes
  through. **Closed:** normal operation again.
- **Only outages count.** Exclude every data-problem and throttling type (exclusions match derived
  types and inner exceptions). Cancellation on shutdown is never counted.
- **Worker hosting only.** An Azure Functions trigger cannot be paused (`29`).
- **`PrefetchCount = 0`.** Prefetched messages would burn delivery attempts on every open cycle.
- **A short `SessionIdleTimeout`** (seconds) helps half-open probes start promptly.
- **Observe it.** Add an `IMessageLifecycleObserver` with `OnCircuitStateChanged` that logs, as
  the template's `CircuitStateLogger` does, or `AddNimBusNotifications(...)` with
  `NotifyOnCircuitOpen`. Alert on the open transition.
- **Tune the sampling window to the traffic.** If it is too long, earlier successes dilute an
  outage and the circuit never opens. If it is too short, a blip opens it.
- **Tune the break duration to the system's outages.** Every half-open probe uses up one delivery
  attempt. For systems with update windows of hours, a break of 5–15 minutes keeps messages from
  exhausting their retries during the window (`21`).

## Tests

`ResilienceTests`, using `EndpointCircuitBreaker` with a `ManualTimeProvider`:
- repeated Unavailable failures open the circuit;
- Rejected, Throttled and MissingReference failures never open it.

Wrap them in `EventContextHandlerException`, because that is what the breaker sees.

## Anti-patterns

- A breaker without exclusions, where one bad record pauses the adapter.
- A breaker in Functions hosting.
- Prefetch above 0.
- Counting throttling as failure, which turns rate limiting into outages.

## Provenance

- Worker chosen over Functions because only a worker can pause receivers.
- Window tuning found while rehearsing an update-window scenario.
- Core fixes for breaker counting and settlement after stop (NimBus BC sample and NimBus core).
