# 03. One endpoint per process

**Confidence:** production. **Applies to:** every subscriber.

## Problem

A NimBus subscriber is bound to one endpoint, which is one topic with its subscription.
`ISubscriberClient` is a non-keyed singleton, and `AddNimBusSubscriber` throws if a second call
names a different endpoint. Three wiring gaps fail silently:

- **A handler without a matching `Consumes<T>()`.** The topology has no rule for that event, so
  the handler never runs.
- **A `Consumes<T>()` without a handler.** Every message of that type fails.
- **No deferred replay.** Messages parked behind a blocked session are never replayed after the
  resubmit, so the session looks resolved but its later changes never arrive.

## Solution

Worker hosting (the template):

```csharp
builder.Services.AddNimBusSubscriber(EndpointName, sub => ConfigureSubscriber(sub, ...));
builder.Services.AddNimBusReceiver(o => { o.TopicName = EndpointName; o.SubscriptionName = EndpointName; o.PrefetchCount = 0; });
builder.Services.AddNimBusDeferredProcessorHostedService(EndpointName);   // replay of parked messages
```

- The endpoint name equals the catalog endpoint's class name (`nameof(XEndpoint)`). Test it.
- **Two endpoints means two processes.** That includes two Container Apps, or two Function apps.
- **Priority lanes.** If urgent and bulk traffic share a system, use a second endpoint (for
  example `<System>LowEndpoint`) in its own process with fewer sessions. Don't use one endpoint
  with two receivers.
- **Developer isolation.** Developers use their own namespace. Never attach a local adapter to a
  subscription a deployed adapter consumes: they compete for messages.
- Topology is created by the provisioner (`ServiceBusTopologyProvisioner`) or
  `nb topology apply`. Adapters never create topics or subscriptions.

## Tests

The template's `ArchitectureTests`:
- the receiver and the deferred-processor hosted services are registered;
- the set of handlers equals the endpoint's `Consumes<T>()`;
- the endpoint name equals the catalog endpoint's id.

## Anti-patterns

- A "multi-endpoint" Function app.
- Handlers registered by hand and forgotten in the endpoint.
- Relying on "it worked locally" after adding a `Consumes<T>()` without re-applying the topology.

## Provenance

- One-endpoint-per-process rule (NimBus core).
- Missing trigger left messages Pending (NimBus sample fix).
- A missing `Consumes<>` declaration found in review (production F&O adapter).
- Low-priority lane with its own endpoint (production F&O adapter).
