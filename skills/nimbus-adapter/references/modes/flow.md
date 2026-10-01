# Mode `flow`: add or change one integration flow

A flow is one change in one system that ends as a write in another, for example "a BC customer
change updates the CRM account". Do one flow at a time, all the way through tests.

## 0. Orient

- Read the adapter's `AGENTS.md`, `AdapterComposition.cs`, the endpoint class and an existing
  handler with its tests. Follow the adapter's own conventions where they differ from the template.
- Read the system pack for the target system (`references/systems/<system>/README.md`).
- If the adapter was not generated from the template, map its equivalents first:
  - the client and its failure types;
  - the resilience configuration;
  - the handler registration;
  - the tests.

  Check `review.md` for anything it lacks that this flow depends on (for example retry rules or
  the deferred processor), and raise it before you start.

## 1. Decide, then record

Work through [../checklists/flow-decisions.md](../checklists/flow-decisions.md). Write the
answers to `docs/flows/<flow-name>.md`. Ask the user for every business decision you cannot
derive from the brief or the code. Typical examples are ownership, missing-reference policy and
deletes. The record must exist before code changes.

## 2. Catalog

- New event: follow `patterns/01-catalog-and-event-conventions.md`:
  - `[Description]`, a static `Example`, `[Required]` and `[Sensitive]`;
  - the `[SessionKey]` chosen in step 1;
  - a producing-system prefix.
- Declare `Produces<T>()` on the producing endpoint and `Consumes<T>()` on the consuming one.
- Changing an existing event: only additive, optional properties. Anything else is a new event (a
  `V2` suffix) consumed alongside the old one until all producers have moved.

## 3. Outbound (bus → system): client, mapper, handler

1. **Client call.**
   - Add a method per API call to `I<System>Client`, shaped like the API.
   - Classify through `HttpFailure` (or the pack snippet's helper).
   - Return business outcomes as results (an enum or null), never as exceptions.
   - Add classification tests for the new statuses (`<System>ClientTests`), plus contract tests
     against the simulator.
2. **Mapper.**
   - A pure static function from the event (plus resolved lookups) to the request body.
   - No I/O, clock or configuration.
   - Unit tests for every rule, including nulls, trimming, code lists and truncation to the
     target's field lengths.
3. **Handler.**
   - Keep it thin: resolve references, map, make one idempotent write, then log the outcome by id.
   - Missing references go through `MissingReferenceException` with the remedy
     (`patterns/14-missing-reference-policy.md`).
   - Handler tests: the happy path, idempotency (handling twice gives the same state), the missing
     reference, the business outcome, and propagation of client failures.
4. **New failure types.** A new exception that is a data problem must be excluded from the circuit
   breaker. A new transient one needs an exception rule (`patterns/21-retry-rules.md`). Add tests
   in `ResilienceTests`.

## 4. Inbound (system → bus)

Follow the system pack's ranked change-detection options and:
- `patterns/04-thin-notification-reread.md`;
- `patterns/05-deterministic-message-id-and-inbox.md`;
- `patterns/06-reliable-publish.md`;
- `patterns/08-echo-loop-prevention.md`.

Requirements:
- The MessageId is deterministic.
- The session key is the event's `[SessionKey]`.
- The adapter's own writes are filtered out at the source.
- A change that cannot be read (the record was deleted, or no permission) is handled by an
  explicit rule, never dropped silently.

## 5. Simulator

Teach the simulator the new routes and failure modes (`simulator-routes.md`) and extend the
contract tests. Add a driver endpoint if the flow starts in the simulated system.

## 6. Prove it

```powershell
dotnet build <solution>.slnx -c Release
dotnet test <solution>.slnx -c Release
```

Optionally run the AppHost and drive the flow through the simulator (see the generated README).
Then update `docs/events.md` and the TDD with the `adapter-docs` skill (*update-from-diff* mode).

## 7. Summary

Report:
- the decisions you made, and the ones that still need a human (open questions in the flow record);
- the files you changed;
- the tests you added, with the pass counts;
- any **[Microsoft docs, verify]** facts the flow depends on.
