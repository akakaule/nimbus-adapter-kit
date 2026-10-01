# Flow decisions

Answer these for every flow, in order, and record them in `docs/flows/<flow>.md`. Each decision
names its default and the pattern that explains the options. **Bold** questions are business
decisions: ask the user unless the brief already answers them.

## Record template

```markdown
# <Flow name>

| | |
|---|---|
| Direction | <System A> → <System B> |
| Trigger | <what changes, detected how> |
| Event | `<EventName>` (session key: `<Property>`) |
| MessageId | `<entity>:<id>:<version>` |
| Target operation | <upsert on key …> |
| Idempotency key | <…> |
| Missing references | <reference: policy> |
| Ownership | <who masters which fields; when ownership moves> |
| Echo identity | <how the adapter's own writes are filtered> |
| Failure mapping | <non-default statuses / codes and their class> |
| Volume | <peak / burst / initial load> |

## Open questions
- …
```

## The decisions

1. **What starts the flow?** (direction, trigger)
   - System → bus: choose from the system pack's ranked change-detection options. Default: a thin
     notification followed by a re-read (`patterns/04`), with polling only where the system cannot
     notify (`patterns/07`).
   - Bus → system: which event(s) the adapter consumes.
2. **Which event, and is it new?**
   - Reuse an existing catalog event when its meaning fits exactly.
   - A new event follows `patterns/01`. Changing an existing one is additive only.
3. **Session key** (`patterns/02`).
   - Default: the aggregate's own id, so changes to one record stay in order.
   - Key a child on its parent only if the child's write needs the parent's last state. The cost:
     a failed parent then blocks all its children.
   - A read-only request carries no session key.
   - Never use a random, constant or timestamp key.
4. **MessageId** (`patterns/05`).
   - Deterministic from the source change: `{entity}:{id}:{version}`, where the version is a row
     version, a modified-on timestamp in ticks, or an event sequence.
   - Hash it if it could exceed 128 characters.
   - Needed for inbox deduplication.
5. **Idempotency of the write** (`patterns/12`).
   - Upsert on the source key (an alternate key or external id), so a redelivery, retry or
     resubmit changes nothing the second time.
   - Decide what makes an update a no-op (compare the fields you own and skip the write).
6. **Cross-references** (`patterns/13`).
   - Where the other system's key is stored: a field in the target system, preferably an
     alternate key, never adapter-local state.
   - Whether a write-back event is needed for keys the target generates.
7. **Missing references** (`patterns/14`), one policy per reference:
   - throw and block the session (core data);
   - skip the field with a warning (optional data);
   - or create a stub (rarely right).

   Default for core data: throw `MissingReferenceException` with the remedy (a few retries first).
8. **Ownership and outcomes** (`patterns/16`).
   - Which fields this flow may write, and which fields are mastered by the target.
   - When the target owns the record, the result is a logged business outcome, not a failure.
   - Never write state fields on create.
9. **Ordering and staleness** (`patterns/15`). Can older data arrive after newer data? If yes,
   add a stale-write guard on a version or row-version field.
10. **Echo identity** (`patterns/08`).
    - How the adapter's own writes are kept from coming back as new changes: filter on the
      integration user at the source, and skip no-op writes at the sink.
11. **Deletes and deactivation.** Hard delete, soft delete or deactivate in the target. What
    happens to the cross-reference? Is a delete event needed at all?
12. **Failure mapping** (`patterns/20`).
    - Any non-default classification: a status or error code that is really throttling, an
      outage, a rejection, or success (for example duplicate-key on create).
    - Check the system pack's error table.
13. **Long-running work** (`patterns/18`). Does the target process asynchronously (batch import,
    approval)? If so, use PendingHandoff and decide what settles it.
14. **Volume** (`patterns/09`, `patterns/23`).
    - The peak rate against the target's limits; this sizes `MaxConcurrentSessions` and the
      retry timings.
    - The initial load: the same events in parent order, or a separate bulk path.
15. **Personal data** (`patterns/25`). Mark it `[Sensitive]`, and keep it out of logs and failure
    texts.
