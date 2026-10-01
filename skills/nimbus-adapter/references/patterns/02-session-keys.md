# 02. Session keys

**Confidence:** production. **Applies to:** every event.

## Problem

NimBus delivers the messages of one Service Bus *session* in order, one at a time. When a message
fails, its session **blocks**: later messages with the same key are parked until an operator
resubmits or skips the failed one, and are then replayed in order. The session key therefore
decides two things at once:
- **ordering**: same key, strict order;
- **blast radius**: a failure blocks everything with that key.

Production mistakes, all observed:

| Mistake | Effect |
|---|---|
| Default `Guid.NewGuid()` session | No ordering at all: an update can overtake its create |
| A constant fallback key (for example `new Guid()` when the key is missing) | Every keyless event shares one session; one failure blocks the whole adapter |
| A timestamp in the key | Every message gets its own session: no ordering |
| Child keyed on its parent | A failing parent blocks all its children, even unrelated ones |

## When / when not

Every event needs a deliberate key. A *read-only request* (for example a credit check) carries
**no** session key, so it never blocks or waits behind a customer's changes.

## Solution

```csharp
[SessionKey(nameof(AccountId))]          // the aggregate's own id
public class CrmAccountUpdated : Event { public string AccountId { get; set; } = ""; ... }
```

- **Default: the aggregate's own stable id.** It must not change over the record's life, so use
  a GUID or system number, not a name.
- **Key a child on its parent only when the child's write depends on the parent's latest state,
  and you accept that a failed parent blocks its children.** Otherwise key the child on itself,
  and handle a missing parent with the missing-reference policy (`14`).
- **Mark the key property `[Required]`.**
  - `Event.GetSessionId()` falls back to a *random* GUID when the attribute is missing or the
    property is null. A null key therefore silently loses ordering.
  - With `[Required]`, the publisher's validation rejects the event instead.
  - The template's catalog tests enforce both the attribute and `[Required]`.
- **Normalize the key** (for example lower-case GUIDs). `ABC` and `abc` are different sessions.
- **Over 128 bytes?** Hash it (SHA-256, hex), and keep the readable id in the payload.
- **Explicit override:** `publisher.Publish(evt, sessionId, correlationId, messageId)` replaces
  `GetSessionId()`. Use it only with a reason, and test it.
- **An empty key at the edge** (an ingress validator, the publisher) is a rejected input, never
  "use a default".

## Per system

- **Dataverse**: `accountid`/`contactid` GUIDs (normalize the case).
- **BC**: the record `id` (a GUID) or the customer `number`. Pick one and keep it.
- **F&O**: `dataAreaId` plus the natural key (for example `usmf:C-10001`). The natural key alone
  repeats across companies.

## Tests

- Catalog test: every event has `[SessionKey]` on a real property, and the example's
  `GetSessionId()` is not empty.
- Publisher tests pin the session id for representative inputs, including the child-versus-parent
  choice.

## Anti-patterns

See the table above. Also: generating a random fallback inside `GetSessionId()`. It is called
more than once per message, so the message gets inconsistent ids.

## Provenance

- An audit of about 40 events defaulting to a random session (production catalog).
- A contact keyed on its parent account blocking contacts until the change was reverted
  (production CRM adapter).
- A constant fallback key collapsing every keyless event into one session (production ERP
  catalog).
