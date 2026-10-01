# F&O simulator routes

What a local F&O simulator must emulate so an adapter built from this pack can be tested without an
F&O environment:
- the OData subset that [`snippets/FinanceOperationsODataClient.cs`](snippets/FinanceOperationsODataClient.cs) calls;
- the DMF actions behind the PendingHandoff flow;
- driver endpoints (`/emulator/*`) that tests and demos use to inject failures and fire F&O-style
  notifications.

Last verified: 2026-10-01. Tags as in the [README](README.md):
**[proven in production]**, **[Microsoft docs, verify]**, **[unverified]**.

Rule for shapes: where a response is tagged **[unverified]**, the simulator uses a best guess.
Replace it with a recorded, anonymized sandbox response as soon as you have one, and keep the
recording next to the simulator.

## Base URL and auth

- Service root: `/data/`. The adapter's `ExternalSystem:BaseUrl` points at
  `https+http://<simulator>/data/` (service discovery) locally, and at `https://<env>.operations.dynamics.com/data/` in
  Azure **[Microsoft docs, verify]**.
- **Auth.** By default, requests without a token are accepted, because the template leaves
  `ExternalSystem:Scope` empty locally. With the `requireAuth` switch on:
  - A request without `Authorization: Bearer <anything>` gets 401 with `WWW-Authenticate: Bearer`.
  - With `unregisteredApp` on, every request gets 403, which mimics an app missing from **Microsoft
    Entra applications**.
  - The status codes are **[unverified]** for real F&O; both must classify as Rejected.
- A production F&O simulator skipped token validation and offered a token-stub endpoint instead
  **[proven in production]**.

## Company behaviour

- Each simulated company holds its own records. The simulated caller's default company is
  configurable (`PUT /emulator/companies/default`). Set it to something other than `usmf`.
- Without `cross-company=true`, queries return only rows of the default company. That matches F&O
  **[Microsoft docs, verify]** and reproduces the "lookup misses → duplicate create" bug.
- `dataAreaId` is compared case-insensitively. Production used lower case in OData and upper case
  in DMF, and both worked **[proven in production]**.

## OData subset (CustomersV3)

| Route | Behaviour |
|---|---|
| `GET /data/CustomersV3?$filter=UpstreamAccountId eq '{id}' and dataAreaId eq 'usmf'&$select=…&cross-company=true` | 200 `{"@odata.context":"…/$metadata#CustomersV3(…)","value":[…]}`. Zero, one or many rows. Honours `$select`. Supports `eq`, `and`, `or` and quoted strings with doubled quotes. Rejects `in` and `has` with 400, as F&O doesn't support them **[Microsoft docs, verify]** |
| `GET /data/CustomersV3(dataAreaId='usmf',CustomerAccount='{key}')?cross-company=true` | 200 with the entity, or 404 when it is missing. Every key field is required **[Microsoft docs, verify]**; a key with a field missing gets 400. Key values arrive URL-encoded (`%2F`, `%27%27`) and are decoded before matching |
| `POST /data/CustomersV3?cross-company=true` | 201 with the created entity. When `CustomerAccount` is absent, assign the next number from a per-company sequence, as an F&O number sequence would **[proven in production]**. Answer a create without `CustomerGroupId` with the [error body](#error-body); that a customer group is mandatory is **[unverified]** |
| `PATCH /data/CustomersV3(dataAreaId='…',CustomerAccount='…')?cross-company=true` | 204. Rejects a body that contains a key field with a new value, or a field listed as read-only, using the [error body](#error-body). F&O rejected PATCH bodies with read-only fields **[proven in production]**; the status code is **[unverified]** |
| `GET /data/$metadata` | Optional. A static CSDL for the entities you emulate, if you generate an OData client |
| `POST /data/$batch` | Optional, only if your adapter uses `Microsoft.OData.Client` with `BatchWithSingleChangeset`. It must apply all parts or none **[Microsoft docs, verify]**, and the [throttle switch](#driver-endpoints-emulator) can reject the whole batch with 429. A production simulator emulated exactly this **[proven in production]** |

Every entity in a response carries `@odata.etag`, as F&O does **[proven in production]**. Use an
opaque `W/"…"` value. Its real format **[unverified]** doesn't matter, because the snippet doesn't
send `If-Match`.

Record every request (`GET /emulator/requests`). Then tests can assert that nothing was POSTed after
a failed lookup.

### Error body

```json
{
  "error": {
    "code": "",
    "message": "An error has occurred.",
    "innererror": {
      "message": "Write failed for table row of type 'CustCustomerV3Entity'. Infolog: Error: Customer group '99' does not exist.",
      "type": "Microsoft.Dynamics.Platform.Integration.Services.OData.AxODataWriteException",
      "stacktrace": ""
    }
  }
}
```

- The outer shape follows OData's JSON error format.
- The empty `code`, the generic outer `message` and the `innererror` with the `AxODataWriteException`
  type come from community and GitHub reports **[unverified]**.
- Return it with status 500 for validation failures, the case the snippet maps to Rejected. Also
  offer it with 400 through the outcome switch. The real status is **[unverified]**, and the
  adapter must reject both.

### Throttling response

- Status `429`, header `Retry-After: <seconds>` **[Microsoft docs, verify]**.
- Body: the documented messages are, for example, "This request could not be processed at this time
  due to system experiencing high resource utilization." or "Number of concurrent requests exceeded
  the limit of 52." **[Microsoft docs, verify]**. The JSON envelope around them is **[unverified]**;
  use the [error body](#error-body) shape with that message in `error.message`.
- A throttled request must change nothing: F&O rejects it when it arrives
  **[Microsoft docs, verify]**.

## DMF actions (handoff tests)

All are `POST /data/DataManagementDefinitionGroups/Microsoft.Dynamics.DataEntities.<Action>`. Each
returns `{"@odata.context":"…/$metadata#Edm.String","value":…}` **[Microsoft docs, verify]**.

| Action | Request | Response `value` |
|---|---|---|
| `GetAzureWriteUrl` | `{"uniqueFileName":"<guid>.zip"}` | A **JSON string**: `"{\"BlobId\":\"{guid}\",\"BlobUrl\":\"<simulator>/emulator/blob/<name>?sig=…\"}"`. Double-encoded, as in F&O **[Microsoft docs, verify]** **[proven in production]** |
| `ImportFromPackage` | `{"packageUrl","definitionGroupId","executionId":"","execute":true,"overwrite":true,"legalEntityId"}` | A new execution id (string) **[Microsoft docs, verify]**. Record the package's entries. Optionally fail a second import that starts while one is executing: F&O fails parallel imports **[Microsoft docs, verify]**, but how it fails is **[unverified]** |
| `GetExecutionSummaryStatus` | `{"executionId"}` | `NotRun` → `Executing` (at least one poll) → terminal: `Succeeded`, `PartiallySucceeded`, `Failed`, `Canceled` or `Unknown` **[Microsoft docs, verify]**, per the outcome switch |
| `GetExecutionErrors` | `{"executionId"}` | A **JSON string** holding `[{"RecordId":"…","Field":"…","ErrorMessage":"…"}]` **[proven in production]**. An empty list is `"[]"` |
| `GenerateImportTargetErrorKeysFile` / `GetImportTargetErrorKeysFileUrl` | `{"executionId","entityName"}` | Optional: `true`/`false`, then `""` for one poll and a URL after that **[Microsoft docs, verify]** |

- `PUT /emulator/blob/{name}` receives the uploaded package, so the adapter's upload to `BlobUrl`
  works locally.
- Status progression: report `Executing` on at least the first poll, so the poller's
  "not terminal yet" path always runs. A production simulator did the same **[proven in production]**.

## Driver endpoints (`/emulator/*`)

| Route | Effect |
|---|---|
| `POST /emulator/throttle` `{ "count": 3, "retryAfterSeconds": 2, "scope": "odata" \| "batch" \| "dmf" }` | The next `count` matching calls get 429 + `Retry-After` and change nothing |
| `POST /emulator/odata/outcome` `{ "operation": "create" \| "update" \| "read", "status": 500, "body": "writeValidation" \| "generic" \| "none", "count": 1 }` | Fails the next matching calls. `"read"` with 503 or 401 drives the "failed lookup must not create" test |
| `POST /emulator/odata/commit-then-fail` `{ "count": 1 }` | **Commits** the next create, then answers 500, or drops the connection. This is the case where a retried POST would duplicate the record: the adapter's lookup-before-create must find the record on the next attempt |
| `PUT /emulator/companies/default` `{ "company": "dat" }` | Sets the simulated caller's default company |
| `PUT /emulator/auth` `{ "requireAuth": true, "unregisteredApp": false }` | The auth switches |
| `POST /emulator/dmf/outcome` `{ "outcome": "Succeeded" \| "PartiallySucceeded" \| "PartiallySucceededNoErrors" \| "Failed" \| "Canceled" \| "Unknown" \| "NeverFinishes", "failRecordIds": ["US-001"], "unattributedErrors": 0, "malformedErrorsJson": false, "pollsBeforeTerminal": 1 }` | Sets the result of the next import. See the notes below the table |
| `POST /emulator/business-events/{businessEventId}` `{ "legalEntity": "USMF", "fields": { … } }` | Sends a business-event-style thin notification to the configured target. See the notes below the table |
| `POST /emulator/data-events/{entity}` `{ "sdkMessage": "Create" \| "Update" \| "Delete", "row": { … } }` | Sends a virtual-table-style row change to the configured target. See the notes below the table |
| `PUT /emulator/targets` `{ "businessEvents": "<url or queue>", "dataEvents": "<url or queue>" }` | Where the notifications are delivered: an HTTP endpoint of your ingress, or a Storage queue (Base64 body) |
| `GET /emulator/requests` / `DELETE /emulator/requests` | The recorded request log (method, path, query, body), and clearing it |
| `POST /emulator/reset` | Clears records, sequences, switches and executions |

**Notes on `/emulator/dmf/outcome`:**
- `PartiallySucceededNoErrors` reports `PartiallySucceeded` with an empty error list; the adapter
  must fail every row **[proven in production]**.
- `unattributedErrors` adds errors without a `RecordId`.
- `malformedErrorsJson` returns a `value` that is invalid JSON (an error message with raw XML), as
  F&O did in production **[proven in production]**.
- `NeverFinishes` stays `Executing`, for the in-flight timeout test.

**Notes on `/emulator/business-events/{businessEventId}`:** the payload carries the standard fields
seen in production plus `fields`, the event's own key fields:
- `BusinessEventId`, `BusinessEventLegalEntity`, `ControlNumber` (unique, not sequential), `EventId`;
- `EventTime`, `EventTimeIso8601`, `InitiatingUserAADObjectId`;
- `MajorVersion`, `MinorVersion`, `ContextRecordSubject`, `ParentContextRecordSubjects`.

Thin, as F&O sends them **[proven in production]** **[Microsoft docs, verify]**. Add `"duplicate":
true` to send the same `ControlNumber` twice, and `"delayMs"` to reorder two events.

**Notes on `/emulator/data-events/{entity}`:** the row is shaped like the production Dataverse
trigger output **[proven in production]**:
- lower-case `mserp_*` fields, every option set as an integer plus `_<field>_label`;
- `SdkMessage`, `RunAsSystemUserId` and `ItemInternalId`.

## Tests the simulator must make possible

1. A failed lookup (503, 401, 429) never leads to a POST. Check `GET /emulator/requests`.
2. Without `cross-company=true`, a record in another company isn't found. The snippet always sends
   it.
3. A commit-then-fail create, retried, ends with exactly one record.
4. A PATCH body never contains `dataAreaId`, the key field or the correlation field. An unchanged
   record is not PATCHed.
5. A 500 `AxODataWriteException` is Rejected, not Unavailable, and doesn't trip the circuit breaker.
6. A 429 is Throttled and changes nothing.
7. DMF covers every row of the settlement table in the README, including
   `PartiallySucceededNoErrors`, unattributed errors, malformed error JSON and `NeverFinishes`.
8. A business event is re-read before it is published. A duplicate `ControlNumber` leads to one
   publish, or to an inbox duplicate.
9. An adapter-caused change (`InitiatingUserAADObjectId` = the adapter's identity) is not
   republished.
