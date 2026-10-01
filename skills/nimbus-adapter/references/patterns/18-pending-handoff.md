# 18. PendingHandoff for asynchronous work

**Confidence:** production. **Applies to:** targets that accept work and finish it later: batch
imports (F&O DMF, BC configuration packages), approval workflows, long-running jobs.

## Problem

A handler cannot wait minutes for a batch job. Completing the message at once loses the link
between the message and the outcome. Holding the session lock while polling expires the lock.

## Solution: NimBus PendingHandoff

1. **Handler.**
   1. Persist the work and its correlation: the six coordinates of
      `HandoffSettlement(EventId, SessionId, MessageId, EventTypeId, CorrelationId,
      OriginatingMessageId)`, plus your job id and the record key. Insert idempotently, so a
      duplicate key means it is already queued.
   2. Call `context.MarkPendingHandoff(reason, externalJobId, expectedBy)`.
   3. Return without throwing.

   The message is then *Pending*, and its session stays blocked, so the record's next change waits.
2. **Batch job** (a separate process or hosted service, one replica or a lease):
   1. Collect pending rows.
   2. Build the package and submit it.
   3. Persist the external job id *immediately*. If the process dies after the submit and before
      the id is stored, that is "submission unknown": treat it as a failure for the operator.
3. **Status poller.**
   1. Poll the job.
   2. Map each error row back to a record key, and from there to its coordinates.
   3. Settle each one with `IHandoffClient.CompleteAsync(coords)` or
      `FailAsync(coords, errorText, errorType)`.

   Use `OriginatingMessageId ?? MessageId` for the message id the settlement addresses.
4. **Timeouts.** Rows past `expectedBy` are failed with a clear text. Use ETag-guarded claims, so
   two pollers never settle the same row.

`IHandoffClient` is registered by `AddNimBusSubscriber` for the endpoint, or with
`AddNimBusHandoffClient(endpoint)` in a settlement-only process. Settlement does **not** rerun the
handler.

## Per system

- **F&O DMF**: ImportFromPackage → GetExecutionSummaryStatus. A `PartiallySucceeded` status with
  zero attributable errors is a failure. Entities whose keys F&O assigns (journals) cannot be
  attributed by record id: fail the whole batch, or use a correlation field. See the pack.
- **BC**: configuration packages or job queue entries. See the pack.
- **Dataverse**: async operations (`asyncoperation`) for bulk deletes and imports.

## Tests

- The handler marks the handoff and writes the correlation row; a duplicate is ignored.
- The poller maps errors to the right coordinates, and an unattributable error fails the batch.
- A timeout settles as Failed.

## Anti-patterns

- Polling inside the handler.
- Settling with the wrong MessageId.
- Batch dedupe keyed on the session alone ("last one wins" silently drops lifecycle events).
- `async void` batch jobs.

## Provenance

- DMF batch with correlation rows, PendingHandoff, a status poller, ETag-guarded timeouts and
  keyed handoff clients (production F&O adapter).
- An HTTP handoff job (NimBus CRM/ERP sample).
