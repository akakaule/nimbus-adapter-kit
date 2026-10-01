# 14. Missing-reference policy

**Confidence:** production. **Applies to:** every write that links to another record (a parent,
owner, price list, warehouse, salesperson…).

## Problem

Events for different records travel in different sessions, so a contact can arrive before its
account. Reference data (owners, codes) may be missing in one environment. Without a policy,
handlers do one of three wrong things:
- link to null silently, so data is lost;
- create a stub, so junk data appears;
- throw an unclassified exception, so the operator gets a cryptic failure.

## Solution: one explicit policy per reference

| Reference | Policy | Mechanism |
|---|---|---|
| **Core** (the parent account, the order's customer) | **Retry briefly, then block** | Throw `MissingReferenceException` with the remedy text. A few fixed-delay retries (it may be in flight), then the message fails, its session blocks, and the operator syncs the reference and resubmits |
| **Optional** (a secondary contact, an industry code) | **Skip the field and warn** | Write without the link and log a warning with the key. Document it in the flow record |
| **Defaultable** (owner → team → business-unit team) | **Fall back down a chain** | Resolve with an explicit fallback order. Skip disabled users. Log which level was used |

```csharp
throw new MissingReferenceException(
    "parent account", parentId,
    $"Sync account '{parentId}' to {System} first, then resubmit this message.");
```

- The template's `MissingReferenceException` has an exception rule (3 × 60 s by default) and is
  **excluded from the circuit breaker**, because it is a data problem.
- **Throw only when the event provided a value** that cannot be resolved. An empty optional
  reference is not missing.
- **Deterministic diagnostics.** When several references are resolved in parallel, inspect the
  results in a fixed order, so the same message always reports the same missing reference.
- **Never create a stub parent** unless the business explicitly wants placeholders, with a way to
  find and complete them.

## Per system

- **Dataverse**: lookups through `@odata.bind` fail with a "does not exist" error at write time.
  Check first with a narrow GET, or map that error to `MissingReferenceException` in the client.
- **BC**: salesperson, payment terms and posting groups are validated at insert. Map those
  validation errors to Rejected with a clear text, or check beforehand.
- **F&O**: cross-entity foreign-key validation (a customer group or a price group) rejects the
  insert. The same applies.

## Tests

- A missing core reference throws `MissingReferenceException`, with the key and the remedy, and
  writes nothing.
- A missing optional reference writes without the link and logs a warning.
- Its retry rule exists, and the breaker excludes it (`ResilienceTests`).

## Anti-patterns

- `LookupGuard` that throws for an *empty* optional value.
- Catching the target's "parent not found" error and retrying forever.
- A generic `InvalidOperationException("not found")` with no key.

## Provenance

- Throw for core entities with remediation text; warn and skip for reference data (production CE
  adapter).
- `LookupGuard` throwing only when a value was provided (production CRM adapter).
- An owner → team → BU-team fallback (production CRM adapter).
- A legal entity without a business unit skipped by rule (production Dataverse adapter).
