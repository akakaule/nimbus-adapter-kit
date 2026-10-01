# 05. Deterministic MessageId + inbox

**Confidence:** production. **Applies to:** every published event, and every consumer that cannot
tolerate a duplicate write.

## Problem

Delivery is at least once. The same change arrives twice when:
- the publisher retries after a timeout;
- the outbox dispatcher recovers;
- a source replays its notifications;
- an operator re-triggers a sync.

Idempotent writes (`12`) make most duplicates harmless. Some writes cannot be idempotent, though:
counters, appended lines, emails, calls to non-idempotent APIs. And every duplicate still costs
an API call against a throttled system.

## Solution

**Publisher: a deterministic MessageId per source change.**

```csharp
var messageId = $"account:{account.Id:N}:{account.ModifiedOn.UtcTicks}";   // or the row version / sequence
await publisher.Publish(evt, evt.GetSessionId(), correlationId: messageId, messageId: messageId);
```

- Build it from **entity + id + version**. The version is a row version, a modified-on timestamp
  in ticks, or an event sequence number. Same change, same id; next change, new id.
- SHA-256 it (hex) if it could exceed 128 characters, or if it contains characters you don't want
  in ids.
- **Know the NimBus default.** When you pass no MessageId, NimBus uses
  `{EventTypeId}-{hash(payload)}`. That deduplicates identical *content*, which has two traps:
  - a value that goes X → Y → X publishes the same id twice, and the inbox skips the second X;
  - a payload with a "published at" timestamp never deduplicates.

  Pass an explicit id with a version, or make sure the payload carries the source version.

**Consumer: the inbox** (record-on-success deduplication):

```csharp
builder.Services.AddNimBusSqlServerInbox(integrationDbConnectionString);   // the integration platform's DB
subscriber.UseInbox(inbox =>
{
    inbox.DeduplicationStore = InboxStore.SqlServer;
    inbox.RetentionPeriod = TimeSpan.FromDays(7);    // longer than any replay you expect
});
```

- A MessageId that has already succeeded for this endpoint is skipped
  (`HandlerOutcome.DuplicateDetected`).
- **Operator resubmits get a new MessageId and run again**, so handlers must stay idempotent
  anyway.
- Retries and replays of a message that eventually succeeded are recognized: NimBus carries the
  original id along.
- The inbox lives in the **integration platform's own database**. Never use the external system's
  database, which is often SaaS and not yours. The table is created on first use, so either grant
  that once or create it up front (`AutoCreateTable = false`).

## Per system

- **Dataverse**: `versionnumber` (a row version), or `modifiedon` ticks.
- **BC**: `lastModifiedDateTime` ticks, or the record's `@odata.etag`.
- **F&O**: a modified date-time field on the entity, or the business event's sequence/id.

## Tests

- Publisher: the same change gives the same MessageId, and a new version gives a new one.
- Consumer (in-memory transport with `AddNimBusInMemoryInbox`): the same MessageId delivered twice
  runs the handler once. The template's `PipelineTests` show this.

## Anti-patterns

- `Guid.NewGuid()` MessageIds on the publisher. The inbox can never match them.
- Relying on content-hash ids for state that can flip back.
- Putting the inbox in the target system's database.
- A retention shorter than the longest replay window.

## Provenance

- Deterministic `entity:id:ticks` ids with an SQL inbox (NimBus Dynamics/BC sample).
- A hash of org, table, record and version (NimBus Dataverse adapter preview).
- The inbox recognizing retried originals (NimBus core fix).
