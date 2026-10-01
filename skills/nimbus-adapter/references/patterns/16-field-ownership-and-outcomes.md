# 16. Field ownership and business outcomes

**Confidence:** production. **Applies to:** every entity mastered by more than one system, or one
whose ownership changes over its life.

## Problem

"Sync the customer" hides the real rules:
- CRM masters a prospect until it gets a quote; then the ERP masters it.
- Contacts are mastered in the ERP but created in either system.
- The ERP owns the credit limit and CRM owns the sales owner.

Adapters that write every field overwrite the owner's data. Adapters that throw when the target
refuses produce endless "failures" for situations that are business as usual.

## Solution

- **Write down ownership per field** in the flow record (`docs/flows/`): which system masters it,
  and when ownership moves. It is a business decision, so ask.
- **The adapter writes only the fields its flow owns.** The mapper produces a body with exactly
  those fields. A PATCH with fewer fields leaves the others alone; don't send nulls for fields
  you don't own.
- **Fields the target owns after create** (status, numbering, agreement state) are written on
  create at most, and never on update.
- **Ownership checks belong where the data lives.**
  - The target refuses with an *explicit* signal (an error code such as "owned by ERP", or a field
    on a custom API), and the client maps that to a **business outcome**
    (`UpsertResult.OwnedByExternalSystem`). Never infer ownership from a bare 409, which is also how
    BC and Dataverse report concurrency conflicts.
  - The handler **logs it and completes**. It is not an exception: no retry, no failed message, and
    no circuit-breaker count.
- **State changes are separate operations**, done only when the state differs. Examples: activate
  or deactivate, win or lose, close.

## Tests

- The mapper omits fields the flow doesn't own.
- The owned-by-target answer becomes an outcome, and the handler completes without a write.
- An update never touches state fields.

## Anti-patterns

- Full-record PUTs from the source.
- Throwing on "already owned".
- Downgrading a status the target advanced (seen: an agreement status moved backwards by a stale
  update).

## Provenance

- Prospect locked once quoted, with a 409 mapped to an outcome (NimBus BC sample).
- A status-downgrade fix (production CE adapter).
- "One authoritative source per pay component" (production HR adapter).
- Fields owned after creation (production CE adapter).
