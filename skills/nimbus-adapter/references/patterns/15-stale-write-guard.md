# 15. Stale-write guard

**Confidence:** production. **Applies to:** targets fed by unordered or multiple upstream paths,
or flows where re-reads can be delayed.

## Problem

Sessions order messages *per key within one endpoint*. Ordering still breaks when:
- the same record reaches the adapter through two paths (a webhook plus a full load, or two
  trigger types);
- a resubmit of an old failed message runs after newer changes;
- the upstream delivers out of order (seen: "update" before "create" by hours).

The older state then overwrites the newer.

## Solution

- Carry a **monotonic version** in the event: a row version, a sequence, or a modified-on
  timestamp *with enough resolution*. Second-resolution timestamps collide.
- Keep a **high-water mark per record**:
  - in the target, as a "last synced version" field compared before writing, or with an ETag; or
  - in an adapter-owned table keyed on the record.
- In the handler:
  1. If the incoming version is ≤ the mark, **acknowledge and skip** (log at Information). Don't
     fail or dead-letter: stale is a valid outcome.
  2. Otherwise write, and **advance the mark only after the write succeeded**.
  3. A version of 0 or empty means unknown. Let it through, and don't advance the mark.
- Prefer the target's own optimistic concurrency where it exists. Dataverse and BC support ETags
  (`If-Match`); a 412 then means "re-read and decide".

## Tests

- Version 5 after version 7: skipped, and no write.
- A failed write does not advance the mark.
- Version 0 passes through.

## Anti-patterns

- Comparing timestamps with second resolution.
- Advancing the mark before the write.
- Dead-lettering stale messages, which floods operators with non-problems.

## Provenance

- A row-version gate in an adapter-owned Azure Table, with an architecture decision record
  (production CE adapter, fed by F&O virtual-table webhooks).
- A last-synced field written but never compared (NimBus sample, a gap noted in the survey).
