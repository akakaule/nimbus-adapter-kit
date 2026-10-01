# 11. Handler layering

**Confidence:** production. **Applies to:** every consumed event.

## Problem

Handlers that query, map, decide and write inline get untestable, slow (about 10 queries per
message was seen in review) and inconsistent. Each one invents its own error handling.

## Solution

```
IEventHandler<T>            thin: orchestrates; decides nothing about HTTP or retries
 ├─ lookup resolver         resolves references (parent, owner, codes) → values or MissingReferenceException
 ├─ mapper (pure)           event + resolved values → target request body
 └─ client                  one method per API call; classifies failures; returns business outcomes
```

```csharp
public async Task Handle(UpstreamAccountUpdated message, IEventHandlerContext context, CancellationToken ct = default)
{
    var parent = await references.ParentAccountAsync(message.ParentAccountId, ct);   // throws MissingReferenceException
    var result = await client.UpsertAccountAsync(AccountMapper.ToTarget(message, parent), ct);
    LogOutcome(logger, message.AccountId, result);                                    // ids and outcome only
}
```

- **Mappers are pure static functions**: no I/O, clock, configuration or logging. Pass in what
  they need. Truncate to the target's field lengths there, and map code lists there.
- **The client owns HTTP**: routes, auth, failure classification (`20`), outcome enums. Handlers
  never see `HttpResponseMessage`.
- **The handler owns the decision**: create, update, skip or block. It never catches client
  exceptions to "handle" them, because NimBus does that (`21`). The only catches allowed are for a
  documented business outcome the client cannot express.
- **Several writes per message?** Order them so that a retry is safe: idempotent steps first, and
  the irreversible step last. Prefer one write per message: split the event, or chain events.
- **Logging**: `[LoggerMessage]` source-generated methods, with ids and outcomes only (`25`).
- **Registration**: `AddHandlersFromAssemblyContaining<T>()`. Every handler must match a
  `Consumes<T>()` (`03`).

## Tests

- Mapper unit tests for every rule.
- Handler tests with a recording fake client: the happy path, idempotency, the missing reference,
  the business outcome, and propagation of client failures.
- The pipeline test on the in-memory transport for wiring.

## Anti-patterns

- Mapping inside the client.
- Lookups inside the mapper.
- `try { … } catch (Exception) { log; }` in a handler: NimBus records a success.
- Sync calls (`.Result`) and `Task.Run` around sync I/O.

## Provenance

- Static pure mappers fed with pre-resolved lookups (production CRM adapter; rated the cleaner
  style in a cross-adapter review).
- A reference-maintainer template method (production CE adapter).
- The NimBus sample handlers.
