# 27. Vendor-API simulator

**Confidence:** production. **Applies to:** every adapter; it is what makes local runs, demos and
contract tests possible.

## Problem

Sandboxes of SaaS ERPs and CRMs are shared, slow, rate-limited and often unavailable. Developers
then test against production-like data or not at all, and failure modes (429, an update window)
are never exercised.

## Solution: emulate the vendor API over HTTP

Of the three emulator styles seen in production adapters, the **HTTP-level simulator** is the one
to copy:

| Style | Verdict |
|---|---|
| **HTTP simulator of the vendor API** (real routes, an auth check, vendor-shaped errors, fault injection) | **Copy this.** The adapter only switches its base URL, so the real client code is exercised |
| In-process swap of the data layer (an in-memory repository) | Fast, but the real client is never tested; drifts from production (a copied publish path that diverged was observed) |
| Real dependency in a container (SQL for on-prem staging tables) | Right for database-based integrations |

The template's `Simulator` project shows the shape:
- **API routes like the real system's**, with the vendor's error body
  (`{"error":{"code","message"}}` for Dataverse, BC and F&O) and the status codes it really uses.
  Each system pack has a `simulator-routes.md`.
- **An optional auth check** (`Simulator:RequireBearerToken`).
- **Fault injection**: `POST /emulator/faults {"status":429,"count":5,"retryAfterSeconds":2}` or
  `{"status":503,"durationSeconds":60}`.
- **Driver endpoints** under `/emulator/*`: seed data, mark records as owned, and *play the user*:
  change a record, fire the change notification, or publish an upstream event.
- **Simulated behaviour where it matters**: for example, a job stays "running" for N polls, so the
  polling loop visibly terminates.
- **Resets on restart.** No persistence, so every run starts from the seed data.
- **A marker type** (`SimulatorEntryPoint`) for `WebApplicationFactory`. It avoids the `Program`
  clash with the adapter (CS0433).

Keep it honest:
- Shape status codes and error bodies from **recorded responses of a real sandbox** (anonymized),
  not from guesses.
- Contract tests run the real client against it (`26`). When the real API surprises you, fix the
  simulator first, then the client.

## Anti-patterns

- A simulator that accepts everything and returns 200.
- Emulator code that re-implements the adapter's publish logic: it will drift.
- Query filters ignored by the fake, so tests pass on wrong queries.

## Provenance

- HTTP emulators with vendor routes, token checks, vendor-shaped errors, `/emulator/*` drivers and
  a Blazor UI (production survey and web-lead adapters).
- Fault-injection middleware for 429 and 503 (NimBus BC sample).
- A throttling simulator for `$batch` (production F&O adapter).
