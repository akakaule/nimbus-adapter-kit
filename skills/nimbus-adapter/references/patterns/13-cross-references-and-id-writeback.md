# 13. Cross-references and ID write-back

**Confidence:** production. **Applies to:** every entity known to both systems.

## Problem

To upsert idempotently (`12`) and to resolve references, each side needs the other side's key. A
lookup table inside the adapter becomes a third source of truth that drifts, needs backup, and is
missing in the next environment.

## Solution

- **Store the other system's key in the target record**: a custom field, ideally an
  *alternate key* or unique index, so upserts and lookups use it directly.
  - A CRM account carries the ERP customer number.
  - The ERP customer carries the CRM account id.
- **Shared keys where possible.** If the target accepts a caller-supplied id, create the record
  with the source's GUID. Then the key is the same on both sides.
- **Composite keys** when one source id is not unique in the target (`{quoteNumber}-{line}`, or
  `{businessUnit}:{number}`).
- **ID write-back.** When the target generates the key (a customer number, a purchase order
  number), publish an event with the new key (`<Target><Entity>NumberAssigned`). The source
  adapter consumes it and stores the key. Until then, the source key in the target record is the
  link.
- **References between entities** (a parent account, the owner): resolve them by the stored
  cross-reference, falling back to the alternate key in the request (Dataverse `@odata.bind` with an
  alternate key). If neither works, see `14`.

## Per system

- **Dataverse**: a custom column plus an alternate key (`new_externalid`); `@odata.bind` with
  `accounts(new_externalid='...')`.
- **BC**: a table extension field, exposed on a custom API page, with `$filter` on it.
- **F&O**: a custom field on the data entity, with `$filter` on it. Some F&O keys are assigned
  (journals). See `18` for attributing those.

## Tests

- Mapping writes the cross-reference field.
- The write-back event handler stores the key idempotently.

## Anti-patterns

- An adapter-local mapping table as the only link.
- Matching by name or e-mail.
- Writing the cross-reference in a second request that can fail after the first succeeded.

## Provenance

- Customer and PO number write-back events (production F&O/CE adapters).
- A shared GUID as the record id (production CRM adapter).
- Cross-references kept in the target systems, never in adapter storage (NimBus sample).
