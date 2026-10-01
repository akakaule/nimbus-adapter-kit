# 08. Echo-loop prevention

**Confidence:** production. **Applies to:** every bidirectional integration, and every system the
adapter both writes to and listens to.

## Problem

The adapter writes a change into system B. B's change detection fires and publishes it. System A
receives its own change back, writes it again, and so on. At best this wastes API calls; at worst
two systems ping-pong a field forever, or overwrite each other's newer values.

## Solution: filter on both sides

**Source side (preferred): don't publish the integration's own writes.**
- Identify the integration's identity in the source system: the application user, service
  principal or Entra app.
- Filter in the trigger:
  - **Dataverse**: the plugin skips when `InitiatingUserId` (or `UserId`) is the integration's
    application user. Store that id in an environment variable, never hard-coded. Also skip
    `Depth > 1` cascades.
  - **BC**: compare `systemModifiedBy` (or the webhook re-read's modified-by) with the
    integration's user.
  - **F&O**: the integration's Entra app user on the record, or a flag field set by the
    integration.
- **Only publish relevant changes.** Compare the pre-image with the post-image for the fields the
  integration carries. An unrelated field change publishes nothing.

**Sink side: never write a no-op.**
- Before updating, compare the fields you own with the current values, and skip the write when
  nothing changes.
- Writing a stamp field ("last synced from X at T") changes the record, so it must itself be
  excluded from the source's relevant-field filter.
- Where the target supports it, bypass custom plugins for integration writes. Dataverse
  `BypassCustomPluginExecution` needs a privilege; see the pack.

**Catalog: carry origin when needed.** Acknowledgement events can carry an `Origin` so a consumer
can ignore its own round-trip.

## Tests

- Source: a notification caused by the integration user publishes nothing.
- Sink: an event whose fields equal the current state produces no write. Assert on the fake
  client.
- End to end (AppHost or e2e): an upsert performed by the adapter does not come back as an event.

## Anti-patterns

- Relying on "the values are equal, so the loop stops". Floating-point, time zones, trimming and
  localized labels make them differ.
- Filtering on a display name instead of a user id.
- A shared integration user for several adapters that must see each other's changes.

## Provenance

- An initiating-user filter plus a depth check in plugins, with a manual re-trigger flag
  (production CRM solutions).
- `IsUnchanged` skipping no-op writes (production CE adapter).
- A "last change from ERP" flag plus plugin bypass on upsert (production Dataverse adapter).
- A test that upserts must not publish (NimBus sample).
