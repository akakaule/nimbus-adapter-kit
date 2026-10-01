# 17. Reference-data cache

**Confidence:** production. **Applies to:** handlers that resolve the same lookups again and again
(units, currencies, owners, product groups, business units).

## Problem

Resolving every lookup with a query costs throughput and quota. About 10 queries per message
throttled one adapter at roughly 2 messages per second. Naive caches cause worse bugs: stale
decisions, cached "not found", unbounded memory.

## Solution

- **Cache only reads that are used as foreign keys**, by immutable id. Never cache the data a
  write decision depends on: read that fresh, with `useCache: false`.
- **Never cache null or not-found.** A missing reference may be synced a second later.
- **Evict on write.** When the adapter writes an entity it also caches, evict that key.
- **Bound it:** a size limit, a TTL (minutes), per replica. Expose hit and miss metrics.
- `IMemoryCache` with a size limit is enough. Use a distributed cache only if cross-replica
  consistency matters, and then rethink it: it usually doesn't.
- **Warm-up is optional.** Loading large reference tables on start delays readiness and hides
  stale data.

## Tests

- The second lookup hits the cache.
- A not-found result is not cached.
- A write evicts the key.
- The size limit is respected.

## Anti-patterns

- A static `Dictionary` with no locking (non-deterministic failures under load).
- Caching whole entities used for update decisions.
- Swallowing cache-store errors and returning null, which turns an outage into a "data issue".

## Provenance

- Cache rules (FK-only, fresh for writes, no nulls, evict on write, generation counter, bounded,
  300 s TTL, meters) in a production CE adapter.
- A table-storage reference cache refreshed every 2 hours, with live fallback (production CRM
  adapter).
- A static non-thread-safe type cache causing intermittent failures (production ERP adapter).
