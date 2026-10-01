# 01. Catalog and event conventions

**Confidence:** production. **Applies to:** every adapter.

## Problem

The catalog (endpoints plus events) is the contract between adapters and the source of the
Service Bus topology. Several failures start in the catalog:

- **Colliding names.** NimBus uses the *unqualified class name* as the EventTypeId, and it is
  global to the Service Bus namespace. Two `CustomerUpdated` classes in different catalogs
  cross-route silently.
- **No usable example or description.** An event without them cannot be shown, simulated or
  documented in the WebApp.
- **Personal data in clear text.** The WebApp shows payloads to operators.
- **Breaking changes.** A rename or type change breaks every consumer still on the old shape.

## When / when not

Always. The only choice is where the catalog lives: a local `*.Contracts` project (template
default), or a shared platform package that every adapter references.

## Solution

```csharp
[Description("Published by Business Central when a customer is created or changed. ...")]
[SessionKey(nameof(CustomerId))]
public class BcCustomerUpdated : Event            // producer prefix: Bc, D365, Fo, ...
{
    public static readonly BcCustomerUpdated Example = new() { CustomerId = "C-10001", Name = "Fabrikam" /* valid! */ };

    [Required, Description("BC customer number. Session key.")]
    public string CustomerId { get; set; } = string.Empty;

    [Required, Description("Customer name.")]
    public string Name { get; set; } = string.Empty;

    [Sensitive(Mode = MaskMode.PartialReveal), Description("Primary e-mail.")]
    public string? Email { get; set; }
}
```

- **Name prefix = the producing system** (`Bc*`, `D365*`, `Fo*`, `Erp*`). This makes collisions
  unlikely and tells the reader who owns the event.
- **`[Description]` on the class and on every property.** It is shown in the WebApp and feeds the
  generated docs and AsyncAPI exports.
- **A static `Example` that passes `TryValidate()`.** The WebApp uses it, and the tests validate
  it.
- **`[Required]` on what consumers rely on.** The NimBus publisher calls `Validate()` before it
  sends, so an invalid event fails at the source with a `ValidationException` and never reaches a
  consumer. NimBus's `ValidationMiddleware` on the consumer side checks only the envelope (EventId,
  EventTypeId) and dead-letters messages without one.
- **`[Sensitive]`** (namespace `NimBus.Core.Messages.PII`; modes `Redact`, `PartialReveal`,
  `Hash`) on personal data. The WebApp masks it, and an operator without the PII role cannot
  resubmit a masked payload.
- **Commands** (`: Command`) have exactly one consumer
  (`PlatformValidation.ValidateCommandConsumers`). **Replies** are plain DTO classes, not events.
- **Evolution**: only additive, optional properties. Anything else is a new event (a `V2` suffix)
  that consumers handle alongside the old one until every producer has moved.

## Per system

- **Dataverse**: map option sets to string codes or enums in the event, not to localized labels;
  labels differ per user language.
- **F&O**: include `dataAreaId` (the company) when entities are company-specific. The same
  customer number exists in several companies.
- **BC**: include the company (and environment, if there are several countries) when one adapter
  serves several companies.

## Tests

`CatalogTests` in the template run against every event in the catalog:
- unique EventTypeIds;
- a `[SessionKey]` on a real property;
- a valid `Example` with a non-empty session id;
- a `[Description]`;
- the producer prefix;
- the command-consumer rule.

Extend the uniqueness check with the other catalogs that share the namespace.

## Anti-patterns

- Reusing one generic event (`EntityChanged { Type, Json }`) for everything. Routing, validation
  and docs all go blind.
- Copying an event class into a second catalog "to avoid a dependency". It collides by name.
- Renaming a property in place.

## Provenance

- An analyzer that requires every produced event to override `GetSessionId()` (production
  catalog).
- Producer prefixes enforced by tests (NimBus samples).
- An EventTypeId collision guard (integration platform design).
