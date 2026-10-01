# 06. Reliable publish

**Confidence:** production. **Applies to:** every place that publishes.

## Problem

"Write, then publish" loses events when the process dies between the two, and "publish, then
write" publishes changes that never happened. Production adapters also lost work in other ways:
- in-memory work queues that vanish on restart;
- queue messages deleted before processing;
- `async void` and fire-and-forget publishes.

## Choose by where the change comes from

| Source of the change | Use |
|---|---|
| A database **you own** (an integration DB, a facade API's DB, a staging table) | **Transactional outbox**: the business write and the outbox row in one transaction |
| A notification on a queue (pattern 04) | **Publish, then complete**: the queue's redelivery is the retry |
| A poll (pattern 07) | **Publish, then advance the cursor**: at least once, with a deterministic MessageId |
| A synchronous API call into the adapter (a webhook) | **Enqueue, then answer 2xx**, then publish from the queue. Never publish inside the HTTP request and hope |

## Outbox (NimBus SQL Server outbox)

```csharp
services.AddNimBusSqlServerOutbox(connectionString);
services.AddNimBusOutboxDispatcher();   // background dispatcher; one Publish per event keeps order
```

- Share **one** `SqlConnection`/`SqlTransaction` between EF Core and the outbox with
  `SqlServerOutboxAmbientTransaction.Begin(connection, transaction)`. That keeps it atomic without
  MSDTC.
- Publish events in the order they happened, one `Publish` each.

## Publish, then complete (ingress queue)

- Complete the queue message only after `Publish` returned.
- Dead-letter permanent problems with a reason code (`InvalidNotification`, `RecordGone`, ...).
  Abandon transient ones.
- Never delete first. Never keep work in an in-memory channel.

## Tests

- Outbox: a failing business write leaves no outbox row, and a successful one leaves exactly one
  per event, in order.
- Ingress: a publish failure means the queue message is not completed (assert on the fake queue
  context).

## Anti-patterns

- `_ = publisher.Publish(...)` (fire-and-forget).
- `async void` event handlers.
- `new Task(() => ...)` without `Start()`: awaiting it hangs forever.
- An in-memory queue between a webhook and the publisher.
- Deleting the source queue message before processing, which loses messages on a crash.

## Provenance

- Outbox ordering fix (NimBus core).
- Ambient SQL transaction sharing (NimBus sample).
- In-memory channel flagged in review, and delete-before-process found in a bulk function
  (production HR and CRM adapters).
