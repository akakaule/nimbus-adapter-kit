# 07. Watermark polling

**Confidence:** production. **Applies to:** systems that cannot notify: on-prem databases, legacy
ERPs, SaaS without webhooks, or webhooks you cannot reach.

## Problem

Polling must not miss changes or replay unbounded history, and it must survive restarts and
scale-out.

## Solution

1. **Persist the watermark** (the last processed modified-on, row version or sequence) in durable
   storage: an integration DB table, or a blob or table entity. Never keep it in memory.
2. **Query** `modified >= watermark - overlap`, ordered by modified ascending, then by key, in
   pages. The small overlap covers clock skew and same-timestamp rows. The deterministic MessageId
   (`05`) makes the overlap harmless.
3. **Publish each change, then advance the watermark** to the last *published* row. A crash
   republishes a page, and the inbox absorbs it.
4. **Run one poller per source.** With several replicas, use a lease (blob lease or DB lock), or
   make the poller a separate single-replica app.
5. **Parent before child.** When one poll returns both, publish parents first (accounts before
   contacts). Skip a child whose parent is not published yet in this run, so it is picked up on
   the next run.

### Staging-table variant (on-prem ERPs)

When the ERP writes changes to a staging table (`Status`, `CreatedDate`, key):
- select the newest row per key: `ROW_NUMBER() OVER (PARTITION BY key ORDER BY CreatedDate DESC) = 1`;
- take `TOP N` ordered by `CreatedDate`;
- capture a cutoff time;
- publish;
- then mark **all** rows for that key with `CreatedDate < cutoff` as processed.

This collapses bursts. Consumers must treat "update" as "create if missing" (`12`), because the
collapse can swallow the create.

## Per system

- **BC online**: `$filter=lastModifiedDateTime gt <watermark>` through the API. Prefer webhooks;
  use polling for re-sync and as a safety net.
- **Dataverse**: change tracking (`RetrieveEntityChangesRequest` / delta links) beats
  `modifiedon` polling. It also reports deletes.
- **F&O**: modified date-time fields on data entities, or a DMF incremental export.
- **NAV / BC on-prem**: the staging-table variant above (watch code pages and column widths).

## Tests

- Rows with equal timestamps across a page boundary are not lost.
- A restart after publish but before the watermark advanced republishes the same MessageIds.
- Parent/child ordering within a page.

## Anti-patterns

- A timer that loads "everything changed today".
- The watermark kept in memory.
- Two replicas polling the same source.
- Advancing the watermark before publishing.

## Provenance

- Staging-table collapse with a cutoff (production NAV adapter).
- A blob checkpoint cursor (production HR adapter).
- Snapshot diffing in blob storage (legacy HR adapter; works, but is expensive).
