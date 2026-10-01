# Dataverse simulator routes

This page lists the Dataverse Web API subset a local simulator should emulate, so that
`DataverseWebApiClient` and the ingress can be tested without an environment. Build it in the
template's `src/<Name>.Simulator` project. Keep that project's `/emulator/faults` driver and its
`Simulator:RequireBearerToken` switch, and replace the sample `/api/accounts` routes with the routes
below.

Tags follow the [README](README.md):

- **[proven in production]**: observed in a production Dataverse CE adapter.
- **[Microsoft docs, verify]**: from learn.microsoft.com.

Where Microsoft documents only the status or only the message, the simulator's choice is marked
**(simulator choice)**. Record a real response from a sandbox environment, anonymise it, and
replace the guess before you rely on the exact code or text.

The real API answers more than this subset. The goal is that every branch of the client and the
classifier can be driven from a test.

## Wiring

- **Base URL.** In local configuration, set `ExternalSystem:BaseUrl` to
  `https+http://externalsystem-simulator/api/data/v9.2/` (the template's service-discovery name plus
  the Web API path).
- **Scope.** Leave `ExternalSystem:Scope` empty locally. Set `Simulator:RequireBearerToken=true` in
  the tests that check the auth header.
- **Ingress.** Change notifications go to `Simulator:IngressUrl`, an HTTP endpoint of the adapter's
  ingress, or to a Service Bus queue if the ingress listens on one. See
  [Driver endpoints](#driver-endpoints-emulator).
- **Integration user.** `Simulator:IntegrationUserId` is the GUID the simulator treats as the
  adapter's application user. `WhoAmI` returns it, and writes made through the API are stamped with it.

## State

- One `account` table:
  - `accountid` (GUID, primary key).
  - `new_externalid`: the alternate key. It is unique, and matching is case-insensitive like the
    template simulator (verify your environment's collation).
  - `name`, `address1_country`, `emailaddress1`.
  - The `parentaccountid` lookup.
  - `statecode` and `statuscode`.
  - `versionnumber`, `modifiedon`, `modifiedby`.
- **Versioning.** Every write increments a global `versionnumber`, and sets `modifiedon` truncated
  to the second and `modifiedby`.
  - `versionnumber` is a BigInt column, and the Web API returns `modifiedon` to the second.
    **[Microsoft docs, verify]**
  - The second-precision truncation is what makes "two writes in one second" testable.
- **Recorded requests.** Every API request is recorded: method, path and query, headers without
  `Authorization`, and body. `GET /emulator/requests` returns them.

## Common behaviour of `/api/data/v9.2/*`

| Behaviour | Simulator | Source |
|---|---|---|
| Response headers | Every response carries `OData-Version: 4.0` | **[Microsoft docs, verify]** |
| Error body | `{"error":{"code":"0x...","message":"..."}}`, `Content-Type: application/json`. Microsoft notes that the code "is frequently empty" | **[Microsoft docs, verify]** |
| Auth | With `RequireBearerToken`, a request without `Authorization: Bearer …` gets **401** | **[Microsoft docs, verify]** (401 for missing or invalid auth); the error code is a simulator choice |
| Injected faults | Applied before routing, in this order: auth check, fault, route | template simulator |
| Parallelism hint | `x-ms-dop-hint: <n>` on every response (configurable; default 8) | **[Microsoft docs, verify]** (header); default value is a simulator choice |

## Routes

### `GET /api/data/v9.2/WhoAmI`

Returns `200 {"BusinessUnitId":"…","UserId":"<Simulator:IntegrationUserId>","OrganizationId":"…"}`
with the `x-ms-dop-hint` header. **[Microsoft docs, verify]**

- `WhoAmIResponse` has exactly these three GUID properties.
- Microsoft's parallel-requests sample reads the hint from a `WhoAmI` response.

### `GET /api/data/v9.2/accounts?$select=…&$filter=new_externalid eq '…'&$expand=parentaccountid($select=new_externalid)`

This is how the client reads a row by its key.

- **`$filter`.** Support the one predicate `<column> eq '<literal>'`: URL-decode it, then unescape
  doubled quotes (`''` becomes `'`). **[Microsoft docs, verify]**
- **Response.** `200 {"@odata.context":"…","value":[…]}`, with an empty `value` when nothing matches.
  **[Microsoft docs, verify]**
- **`$select`.** Return only the selected columns, plus the primary key, which is always returned.
  Each row carries `"@odata.etag": "W/\"<versionnumber>\""`. **[Microsoft docs, verify]**
- **`$expand`.** `parentaccountid($select=new_externalid)` nests the parent as an object, or `null`.
  **[Microsoft docs, verify]**
- **No `$select`.** Return all columns, but record the request so a test can fail on it. Microsoft
  warns that reading every column is slower. **[Microsoft docs, verify]**
- **Unknown column in `$select` or `$filter`.** Answer **400** with `0x80060888`. The code is listed
  as CrmHttpClientError; tying it to this case is a simulator choice. **[Microsoft docs, verify]**

### `GET /api/data/v9.2/accounts(new_externalid='…')`

Retrieve by alternate key.

- `200` and the row (honour `$select` and `$expand`).
- **404** when the row is missing. The docs example shows the message
  `account With Id = … Does Not Exist`; use code `0x80060891` RecordNotFoundByEntityKey.
  **[Microsoft docs, verify]**
- A key value that contains `/ < > * % & : \ ? +` gets **400**: Microsoft says these fail in a key
  segment. **[Microsoft docs, verify]**
- A column that is not an alternate key gets **400** with `The key in the request URI is not valid
  for resource 'Microsoft.Dynamics.CRM.account'…`. **[Microsoft docs, verify]**

### `PATCH /api/data/v9.2/accounts(new_externalid='…')`: upsert

| Request | Row exists | Row missing | Source |
|---|---|---|---|
| No conditional header | Update → **204** + `OData-EntityId: …/accounts(new_externalid='…')` | Create → **204**, same header | **[Microsoft docs, verify]** |
| `Prefer: return=representation` (`$select=accountid`) | **200** + `Preference-Applied: return=representation` + the selected columns and `@odata.etag` | **201** + the same | **[Microsoft docs, verify]** |
| `If-Match: *` (update only) | as above | **404** `account With Id = … Does Not Exist` | **[Microsoft docs, verify]** |
| `If-None-Match: *` (create only) | **412** `{"error":{"code":"","message":"A record with matching key values already exists."}}` | create as above | **[Microsoft docs, verify]** (status and message as in the docs example, including the empty code) |
| `If-None-Match: null` | treated as no header | treated as no header | **[Microsoft docs, verify]** (Microsoft asks clients to send it on every request) |

**Body rules.**

- **Key column in the body.** On update, ignore it. On create, use the body value over the URL
  value, as Dataverse does. **[Microsoft docs, verify]** Count it in `/emulator/requests`, so a
  test can assert the adapter never sends it.
- **`"parentaccountid@odata.bind": "accounts(new_externalid='P-1')"`** (with or without a leading
  `/`) resolves the parent by its key. `null` clears the lookup. **[Microsoft docs, verify]**
  - A missing parent gets **404** with `0x80060891` and `A record with the specified key values
    does not exist in account entity`. The code and message come from the error-code reference;
    the status is a simulator choice. **[Microsoft docs, verify]**
- **Unknown property.** Answer **400** with `0x80048d19` (ODataClientPayloadError, "Error
  identified in Payload provided by the user for Entity…"). **[Microsoft docs, verify]** (code
  listed; tying it to this case is a simulator choice)
- **Too long.** A string longer than the column's maximum length gets **400** with `0x80044331`,
  `A validation error occurred. The length of the '<column>' attribute of the 'account' entity
  exceeded the maximum allowed length of '<n>'.`. **[Microsoft docs, verify]**
  - Use 160 for `name` (simulator choice), 100 for `emailaddress1` and 80 for `address1_country`
    (account metadata). **[Microsoft docs, verify]**
- **State on create.** A create carrying a non-default `statecode`/`statuscode` gets **400**
  `<n> is not a valid status code for state code Active`.
  - That models the strict behaviour production met on a custom table. **[proven in production]**
  - Microsoft lists the code `0x80048408` InvalidStateCodeStatusCode. **[Microsoft docs, verify]**
  - The account metadata itself allows `statecode` on create. **[Microsoft docs, verify]** Keep
    the simulator strict, so the adapter is tested on the safe path: create, then change the state.

**Echo rule.**

- API writes are stamped with `Simulator:IntegrationUserId`, and by default they **fire no change
  notification**. That is what the plug-in's identity filter does in production.
  **[proven in production]**
- Set `Simulator:EchoNotifications=true` to fire a notification for API writes too, with
  `InitiatingUserId` = the integration user. Then test that the ingress or echo filter drops it.

### Out of scope, unless your flow needs it

- `POST /api/data/v9.2/accounts` (create).
- `$batch`.
- `DELETE`.
- Actions such as `WinOpportunity`.

Add them with the same error-body and recording rules when a flow uses them. NimBus's DynamicsBcDemo
simulator has a `WinOpportunity` route to copy from.

## Fault injection

Keep the template's `POST /emulator/faults` and `DELETE /emulator/faults`. Extend the body with an
optional `code` and `message`, so the Dataverse-specific classifications can be driven:

| Body | Simulates | Expected adapter outcome | Source |
|---|---|---|---|
| `{"status":429,"count":5,"retryAfterSeconds":2,"code":"0x80072322","message":"Number of requests exceeded the limit of 6000 over time window of 300 seconds."}` | number-of-requests limit | Throttled, `RetryAfter` = 2 s | **[Microsoft docs, verify]** |
| `{"status":429,"count":3,"retryAfterSeconds":5,"code":"0x80072321","message":"Combined execution time of incoming requests exceeded limit of 1,200,000 milliseconds over time window of 300 seconds. …"}` | execution-time limit | Throttled | **[Microsoft docs, verify]** |
| `{"status":429,"count":3,"retryAfterSeconds":1,"code":"0x80072326","message":"Number of concurrent requests exceeded the limit of 52."}` | concurrency limit | Throttled | **[Microsoft docs, verify]** |
| `{"status":503,"durationSeconds":30}` | update window or outage | Unavailable; the circuit opens, then closes after the window | **[Microsoft docs, verify]** (503) |
| `{"status":400,"count":2,"code":"0x80044151","message":"SQL timeout expired."}` | platform fault with a 4xx status | **Unavailable**, not Rejected | **[Microsoft docs, verify]** (code); the 400 status is a simulator choice |
| `{"status":500,"count":2,"message":"Database '…' is not currently available. Please retry the connection later."}` | SQL backend blip without a code | Unavailable | **[proven in production]** (message shape, anonymised) |
| `{"status":400,"count":1,"code":"0x80040265","message":"<plug-in message>"}` | a plug-in refusing the write | Rejected, with the plug-in's message in the error text | **[Microsoft docs, verify]** |
| `{"status":403,"count":1,"code":"0x80040220","message":"Principal user (Id=…) is missing prvWriteAccount privilege …"}` | missing privilege | Rejected | **[Microsoft docs, verify]** (code and 403); the message is abbreviated |

The template's validation restricts `status` to 429/500/502/503/504. Widen it to the statuses above
when you add `code`.

## Driver endpoints (`/emulator/*`)

These play the user, the plug-in and the outage. They are never called by the adapter.

| Route | Does | Notification fired |
|---|---|---|
| `GET /emulator/accounts` | Returns all rows, with `versionnumber`, `modifiedon` and `modifiedby` | none |
| `PUT /emulator/accounts/{key}` body `{"name":"…","address1_country":"…"}` | A **person** edits the row: it is created if missing, `versionnumber` is bumped, and `modifiedby` is set to a person user id | `Create` or `Update`, `InitiatingUserId` = the person |
| `PUT /emulator/accounts/{key}?unchanged=true` | A person saves without changing anything: `versionnumber` is bumped, the values stay | `Update`, with identical pre- and post-images |
| `POST /emulator/accounts/{key}/notify?message=Update` | Re-sends the last notification for the row without changing it | the same notification again (duplicate delivery) |
| `POST /emulator/accounts/{key}/burst?count=3` | Three edits within the same second | three notifications. A re-read after the burst sees one `versionnumber`, so the inbox must publish once |
| `DELETE /emulator/accounts/{key}` | A person deletes the row | `Delete`, with the pre-image only |
| `GET /emulator/requests` / `DELETE /emulator/requests` | Lists or clears the recorded API requests | none |
| `POST /emulator/faults` / `DELETE /emulator/faults` | As in the template, extended as above | none |

### Notification payloads

Send whichever shape the adapter's ingress parses.

**Thin notification.** The shape production queues used; the field names are this kit's choice.
**[proven in production]** (thin notification and re-read)

```json
{ "table": "account", "id": "<accountid>", "message": "Update", "initiatingUserId": "<systemuserid>" }
```

**Execution-context subset.** Use this when the ingress reads the Service Endpoint's JSON
`RemoteExecutionContext`. The property names below are as in Microsoft's webhook example.
**[Microsoft docs, verify]**

- Identity and routing: `MessageName`, `PrimaryEntityName`, `PrimaryEntityId`, `InitiatingUserId`,
  `UserId`, `Depth`, `OrganizationId`, `CorrelationId`, `OperationId`, `OwningExtension`.
- Stage: `Stage` (40) and `Mode` (1, asynchronous).
- Data: `InputParameters` (key `Target`), `PreEntityImages` and `PostEntityImages`, as
  `[{ "key": …, "value": … }]` arrays.
- Value types: lookups as `EntityReference` objects with `Id` and `LogicalName`, choices as
  `OptionSetValue` objects with `Value`.
- Above the size limit, drop `ParentContext`, `InputParameters` and the images, and set the
  truncation marker. That is the `MessageMaxSizeExceeded` property for a queue and the
  `x-ms-dynamics-msg-size-exceeded` header for a webhook. **[Microsoft docs, verify]**
- Add `POST /emulator/accounts/{key}/notify?truncated=true` to test that the ingress dead-letters
  it.

### Scenarios the routes make testable

- **Duplicate notification.** `notify` twice gives one publish, because the inbox deduplicates on
  `{table}:{id}:{versionnumber}`.
- **Echo.** An adapter write fires nothing. With `EchoNotifications=true`, it fires an `Update`
  whose `InitiatingUserId` is the integration user. The ingress or echo filter drops it.
- **No-op save.** `PUT …?unchanged=true` gives `SkipNoRelevantChange` from the echo filter.
- **Burst.** `burst?count=3`, then re-reads: one publish.
- **Missing parent.** An upsert with a parent key the simulator lacks gives the handler's
  `MissingReferenceException`. `PUT /emulator/accounts/{parent}` plus a resubmit clears it.
- **Race on create.** Two concurrent upserts for a new key: make the second insert fail once with
  `0x80040237`. The client repeats with `If-Match: *` and reports Updated.
- **Throttling, outage, platform fault, plug-in rejection.** Use the fault table above.
