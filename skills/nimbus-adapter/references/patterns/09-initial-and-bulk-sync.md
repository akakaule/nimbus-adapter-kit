# 09. Initial and bulk sync

**Confidence:** production. **Applies to:** go-live loads, re-syncs after an outage, onboarding a
new company or country.

## Problem

The steady-state flows assume both systems already agree. At go-live they don't. A big load
through the normal path can throttle the target for hours, and a separate one-off script skips
every guarantee the adapter has.

## Solution

Choose by volume:

| Volume | Approach |
|---|---|
| Up to tens of thousands of records | **The same events through the same path.** A "run initial sync" command publishes the normal `*Updated` events for every record, in **parent order** (reference data → customers → contacts → documents), with deterministic MessageIds |
| Hundreds of thousands and up, or tight windows | **A bulk path**: a separate job writes in batches through the system's bulk API (Dataverse `ExecuteMultiple`/`CreateMultiple`, F&O DMF, BC `$batch` or a configuration package). Then the event path takes over for deltas |

Either way:
- **Idempotent upserts** (`12`), so a rerun changes nothing.
- **Parent order.** With per-entity session keys, parents and children are separate sessions, so
  publish all parents first. Alternatively, rely on the missing-reference retries (`14`) to wait
  briefly.
- **Throttle the producer**, not just the consumer: publish in pages with a pause, and watch the
  target's throttling signals.
- **Report**: counts per entity of created, updated, unchanged and failed. Bulk-path failures go
  to an error file or table that an operator can act on.

## Tests

- A rerun of the initial sync performs no writes (unchanged).
- Parents are published before children.
- The bulk path maps its error rows back to records (see `18` for job-based targets).

## Anti-patterns

- Deleting bulk queue messages before processing.
- Mapping bulk faults by list position instead of the request index the API returns.
- A one-off script that bypasses mapping and validation.

## Provenance

- An initial sync through the normal events in parent order (NimBus BC sample).
- A bulk function with `ExecuteMultiple` (production CRM adapter; its delete-before-process and
  fault-index bugs are listed above).
- Full-load workflows for F&O (production F&O adapter).
