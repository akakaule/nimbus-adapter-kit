# 04. Thin notification, then re-read

**Confidence:** production. **Applies to:** system → bus flows where the system can emit a change
signal (a plugin, webhook, business event or virtual-table trigger).

## Problem

Building the full event inside the source system is fragile:
- Dataverse plugins run sandboxed with a 2-minute limit and no NimBus SDK.
- BC webhooks and F&O business events carry only a resource reference.
- Logic Apps and plugins cannot reach the bus with ordering and MessageId guarantees.

Payloads built at trigger time can also be partial (only the changed attributes), or stale by the
time they are processed.

## When / when not

- **Use it** when the system can say "record X changed", and the adapter can read X back with
  its own credentials.
- **Don't use it** when the change is a *fact* that cannot be read back afterwards: a deleted
  record, or an intermediate state the source overwrites at once. Then the notification must carry
  that fact (the id plus the change type, or the deleted record's key), and the publisher maps it
  directly.

## Solution

```
source trigger ──► ingress queue ──► adapter ingress: validate ─► re-read current state ─► map ─► publish ─► complete
   {entity,id,changeType,changedOn}        (Service Bus / Storage queue, retries, DLQ)
```

1. **The trigger sends a thin notification**: entity, id, change type and change time (or
   version). The source side keeps only its *filters*, the echo filter (`08`) and the
   relevant-field filter.
2. **It lands on a queue the adapter owns**, not straight on the NimBus topic. The queue gives
   retries and dead-lettering. Source-side calls (plugin → queue) also stay simple and fast. Use
   a Service Bus queue, or a Storage queue with `maxDequeueCount`.
3. **The adapter re-reads the current state** with a narrow column set, maps it to the catalog
   event, and publishes. It uses a **deterministic MessageId** built from the record version
   (`05`) and the event's session key (`02`).
4. **It completes the queue message only after the publish succeeded.**
   - A permanent problem (a malformed notification, or a record that no longer exists and has no
     delete rule) is dead-lettered with a reason code.
   - A transient one (the source is throttled or unavailable) is abandoned, so it is retried.
5. **Deletes**: the re-read returns 404. Publish a `*Deleted` event if the flow needs one (from the
   notification's id), or ignore the notification by an explicit rule. Never drop it silently.

**Coalescing trade-off.** Re-reading publishes the *current* state. Three quick updates can produce
three events with the same content. That is harmless with idempotent upserts and a version in the
MessageId. Intermediate states are not published. If consumers need every transition (an audit
trail or state machine), carry the transition in the notification instead.

## Per system

- **Dataverse**: plugin step or Service Endpoint (async, post-operation) → Service Bus queue →
  re-read with `$select`. Filter on `InitiatingUserId` and relevant attributes in the step. See
  the pack.
- **BC**: webhook subscription → an HTTP receiver that answers the validation handshake and
  enqueues → re-read through the API. Subscriptions expire and must be renewed. See the pack.
- **F&O**: business event (Service Bus / Event Grid endpoint) or a Dataverse virtual-table trigger
  → queue → OData re-read with `cross-company=true`. See the pack.

## Tests

- Notification → exactly one publish, with the expected event, session id and MessageId (use a
  capturing publisher).
- Re-read 404 → the delete rule.
- A transient failure → the queue message is not completed.
- A malformed notification → dead-letter with its reason code.
- The same notification twice → the same MessageId.

## Anti-patterns

- Building the full payload in the plugin and posting it to an HTTP endpoint without a queue. A
  plugin timeout or adapter restart loses it.
- Re-reading every column (`ColumnSet(true)`). It is slow, gets throttled and leaks data into
  logs.
- Deleting or completing the queue message *before* publishing.

## Provenance

- Plugin → storage queue → re-read → publish (production CRM adapter).
- Event Grid notification with an id → re-read (production Dataverse adapter).
- Webhook → fetch details (production survey-SaaS adapter).
- Ingress queue → publish → complete, with dead-letter reason codes (NimBus Dataverse adapter
  preview).
