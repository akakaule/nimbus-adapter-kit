# Flow decision records

One file per integration flow (`<flow>.md`), written before the code. Each records the decisions
the `nimbus-adapter` skill's flow checklist asks for:

- direction and trigger (what change, detected how);
- the event and its session key;
- the MessageId recipe (deterministic, so redeliveries deduplicate);
- the idempotency key (what makes the write repeatable);
- the missing-reference policy (throw and block, or skip with a warning);
- field ownership (which system masters which field) and business outcomes;
- the echo identity (how the adapter's own writes are kept from coming back);
- the failure mapping (which answers are throttled, unavailable or rejected).

The TDD (`docs/TDD.md`) and the events catalog (`docs/events.md`) link here; the `adapter-docs`
skill generates them.
