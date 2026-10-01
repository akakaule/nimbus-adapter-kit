# 12. Idempotent upsert

**Confidence:** production. **Applies to:** every write to an external system.

## Problem

The same event reaches a handler more than once (retries, redelivery, resubmits, replays).
Upstream may also deliver "update" before "create", or collapse them, so that only an update ever
arrives. A plain `POST` creates duplicates. A plain `PATCH` by internal id fails when the record
was never created.

## Solution

**Key every write on the source system's id, stored in the target.**

1. **Native upsert on an alternate key or external id** (best):
   - Dataverse `PATCH accounts(new_externalid='ACC-1001')`, which creates or updates;
   - Salesforce external-id upsert.

   See the system packs for exact semantics.
2. **Read, then create or update** where there is no native upsert (BC standard APIs, F&O OData):
   1. `GET` by the external-id field.
   2. If found: `PATCH` (with `If-Match` where required).
   3. If not found: `POST`.

   The race between two writers is avoided by the session key: one key, one handler at a time.
3. **Symmetric handlers.** "Created" and "Updated" both upsert. An update for a record that does
   not exist creates it.
4. **Create-with-id.** Some targets let you create with the source's GUID (Dataverse `accountid` =
   the source id). A second create then answers "already exists". Treat that as success and update.

Also:
- **Skip no-ops.** Compare the fields you own and skip the write when nothing changes. This saves
  quota and prevents echo loops (`08`).
- **Never set state fields on create** (`statecode`, blocked, closed). Create the record, then
  change its state with the system's state API, and only when the state differs.
- **A failed read is not "not found".** Only a 404 (or an empty result) means create. Any other
  failure throws (`20`). Swallowing a transient GET error and then POSTing is a classic source of
  duplicates.
- **Non-idempotent operations** (POST with a server-assigned key, posting a journal): only
  retry what certainly did not commit (429, which is rejected before processing). A 5xx after a
  POST may have committed, so read back before trying again, or move the step to a job with
  status (`18`).

## Per system

- **Dataverse**: `PATCH` on an alternate key upserts. `If-Match: *` forces update-only and
  `If-None-Match: *` create-only. The duplicate-key error on create is success. See the pack.
- **BC**: there is no upsert on standard APIs. Use a custom API page that exposes the external-id
  field, `$filter` on it, then POST or PATCH with the `@odata.etag` in `If-Match`. See the pack.
- **F&O**: filter on a correlation field (`$filter=... eq '...'` with `cross-company=true`), then
  POST, or PATCH on the composite key. Strip key and read-only fields from the PATCH body. See the
  pack.

## Tests

- Handling the same message twice gives one record and the same state.
- An update for a missing record creates it.
- A transient failure on the existence check throws, and does not create.
- Duplicate-key on create counts as success.

## Anti-patterns

- `GetIdFromEndpoint` returning `""` on any HTTP error, followed by a POST.
- Upserting by display name.
- Create-only handlers for "Created" events.

## Provenance

- Symmetric New/Modified upserts after upstream delivered "update" hours before "create"
  (production CE adapter).
- Update handlers that create when missing (production F&O and CRM adapters).
- A swallowed GET error causing duplicate F&O records (production ERP adapter; open finding).
- Lookups by a code field that was never set on create, causing duplicates (production ERP
  adapter).
