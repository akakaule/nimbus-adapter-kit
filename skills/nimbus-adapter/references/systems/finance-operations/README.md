# Dynamics 365 Finance & Operations (F&O) system pack

How to connect Dynamics 365 Finance & Operations (cloud) to a NimBus bus. Covers OData data entities
(`/data/{EntitySet}`), custom services, and the Data management framework (DMF) package API. It does
not cover on-premises F&O.

Last verified: 2026-10-01

**Tags.** Every statement about F&O, Dataverse or Logic Apps carries one of these tags:
- **[proven in production]**: observed in a production F&O adapter or its Logic Apps.
- **[Microsoft docs, verify]**: taken from learn.microsoft.com (see [Sources](#sources)). Check it
  against your environment's version.
- **[unverified]**: neither observed in production nor documented on Learn. Test it in a sandbox
  before you rely on it, and record what you see in the simulator.

Statements about NimBus behaviour come from the NimBus 4.2.0 source.

## At a glance

| Topic | What to do |
|---|---|
| Write API, low volume | OData PATCH and POST on data entities, one message per record **[proven in production]** |
| Write API, volume | DMF package import (`ImportFromPackage`), settled through NimBus PendingHandoff **[proven in production]** |
| Auth | An Entra app or a managed identity, registered in F&O under **Microsoft Entra applications**. Scope `https://{env}.operations.dynamics.com/.default` **[proven in production]** |
| Company | Put `dataAreaId` in every key, and `cross-company=true` on every call **[proven in production]** |
| Idempotent create | Use a correlation field that holds the upstream id. Look it up before every create, and never read a failed lookup as "not found" **[proven in production]** |
| Throttling | 429 with `Retry-After`, on OData and custom services only. DIXF and recurring integrations are exempt **[Microsoft docs, verify]** |
| Change detection | Best: business events, which are thin, so re-read the record. Next: virtual-table data events through a Logic App **[proven in production]** |
| Ordering of F&O events | Not guaranteed **[Microsoft docs, verify]**. Re-read current state and session on the record key |
| Echo identity | A dedicated Entra app, mapped to a dedicated F&O user. Skip no-op writes (see [Echo-loop prevention](#echo-loop-prevention)) |
| Local testing | A simulator of the OData subset and the DMF actions ([simulator-routes.md](simulator-routes.md)) |

## Authentication

- Register an app in Microsoft Entra ID. Then add its client id in F&O under **System administration >
  Setup > Microsoft Entra applications**, mapped to an F&O user **[Microsoft docs, verify]**. Calls
  run with that user's security roles. Map it to a dedicated service user, not Admin
  **[Microsoft docs, verify]**.
- Managed identity works. A production adapter used `DefaultAzureCredential` with the scope
  `https://{env}.operations.dynamics.com/.default` **[proven in production]**. A production Logic App
  called F&O OData from an HTTP action with its system-assigned managed identity, with the audience
  set to the environment URL **[proven in production]**. In both cases the identity's client id is
  what goes in the **Microsoft Entra applications** form **[proven in production]**. Learn documents
  only the client-credentials flow (a shared secret or a certificate), and we found no Learn page on
  managed identities for F&O **[Microsoft docs, verify]**.
- In the template, set `ExternalSystem:Scope` to `https://{env}.operations.dynamics.com/.default`.
  `BearerTokenHandler` and `DefaultAzureCredential` then do the rest. Don't cache tokens yourself:
  one production adapter cached them by hand behind a lock, which is code you don't need
  **[proven in production]**.
- One app (client id) per integration. Throttling priority is set per Entra app or user on the
  **Throttling priority mapping** page (Low, Medium or High) **[Microsoft docs, verify]**. The F&O
  user behind the app is also your echo identity.
- Business-event endpoints are separate. An endpoint for Service Bus or Event Grid authenticates
  with an Entra app secret kept in Key Vault. When that secret expires, events fail into the F&O
  error log. A `SecretExpiryDate` field on the endpoint drives a warning banner
  **[Microsoft docs, verify]**.

## Change detection (F&O → bus)

The options are ranked. Whichever you choose, the publisher re-reads the record and publishes the
current state with a version. It uses the record key (company + key) as the session key, and a
deterministic `MessageId`, so the inbox drops redeliveries.

### 1. Business events (thin notification → re-read → publish)

- Business events are lightweight notifications, not a data export **[Microsoft docs, verify]**. The
  payload holds the event's key fields plus standard fields. A production business-event schema had:
  - `BusinessEventId`, `BusinessEventLegalEntity`, `ControlNumber`, `EventId`, `EventTime`,
    `EventTimeIso8601`, `InitiatingUserAADObjectId`;
  - `MajorVersion`, `MinorVersion`, `ContextRecordSubject`, `ParentContextRecordSubjects`;
  - and the record's ids **[proven in production]**.
- Re-read the record before you publish. A production Logic App received the business event,
  re-read the full record by its id through a Dataverse virtual table, composed the payload and
  queued it for the publisher **[proven in production]**.
- Endpoint types:
  - Azure Service Bus queue or topic, Event Grid, Event Hub, Blob storage, HTTPS, Power Automate and
    Dataverse **[Microsoft docs, verify]**;
  - Logic Apps can also subscribe through the Fin & Ops connector trigger **When a Business Event
    occurs** (`dynamicsax`), which takes the environment host, category, event id and legal entity
    **[proven in production]**.
  - For a NimBus adapter, the simplest robust route is a Service Bus queue endpoint read by a small
    ingress, which re-reads the record and publishes the catalog event. The F&O payload is not a
    NimBus message, so never point an endpoint at a NimBus topic.
- Activation is per event and per legal entity. A blank legal entity activates the event in all of
  them **[Microsoft docs, verify]**.
- Delivery:
  - F&O retries 3 times, 1,000 ms apart by default. It then keeps the event in an error log, where
    you can **Resend** it **[Microsoft docs, verify]**.
  - Order is not guaranteed **[Microsoft docs, verify]**.
  - `ControlNumber` is unique per event, for duplicate detection. It is not a sequence and can have
    gaps **[Microsoft docs, verify]**.
- Deterministic `MessageId`:
  - Use `{entity}:{company}:{key}:{version}` when the re-read record has a version or modified
    stamp. Two notifications that read the same state then dedupe.
  - Otherwise use `{entity}:{ControlNumber}`, which dedupes redeliveries of the same notification.

### 2. Virtual-table data events (Dataverse webhook → Logic App or function)

- F&O data entities can be exposed as Dataverse virtual tables. Their logical names are `mserp_` +
  the lower-cased entity name, for example `mserp_custcustomerv3entity` **[proven in production]**.
  The data stays in F&O, and each call runs as the F&O user mapped to the calling Dataverse user
  **[Microsoft docs, verify]**.
- A production Logic App (Standard) used the Dataverse trigger **When a row is added, modified or
  deleted** on the virtual table **[proven in production]**:
  - It filtered on `mserp_dataareaid eq '<company>'`, with scope Organization.
  - It Base64-encoded the trigger body and put it on a Storage queue.
  - A queue-triggered Azure Function built the catalog event and published it.
- Registering that trigger creates a Dataverse callback registration, and that registration
  activates the matching data event in F&O **[proven in production]**. Data events need the Power
  Platform integration enabled on the F&O environment **[Microsoft docs, verify]**.
- Payload: data events carry the entity record **[Microsoft docs, verify]**.
  - The production payloads had lower-case `mserp_*` fields, and every option set came as an
    integer plus a `_<field>_label` string **[proven in production]**.
  - `SdkMessage` is `Create`, `Update` or `Delete` **[Microsoft docs, verify]**.
  - `RunAsSystemUserId` was present **[proven in production]**. What it holds for a change made
    inside F&O is **[unverified]**.
  - Null datetime fields are left out of the payload **[Microsoft docs, verify]**.
- Limits **[Microsoft docs, verify]**:
  - Bursts of 5,000 events per 5 minutes, and up to 50,000 per hour, across the environment.
  - No event for a change to a virtual (computed) field, or for an entity whose primary data source
    is a view.
  - Order is not guaranteed.
  - The trigger's **Select columns** filter is not supported on virtual tables. Every update fires
    the trigger, even when no value changed.
- Before a bulk import or bulk processing, deactivate the data events, or the event volume can slow
  the environment **[Microsoft docs, verify]**.
- Deletes: a delete arrives as `SdkMessage = Delete`. Publish it as its own event type, or skip it
  on purpose. Don't let it fall through to the upsert path.

### 3. DMF export or recurring integration (files)

- `ExportToPackage` exports a data project as a package. With change tracking on, it exports only
  the delta since the last run. `GetExportedPackageUrl` then returns a SAS URL for it
  **[Microsoft docs, verify]**.
- Recurring integrations **[Microsoft docs, verify]**:
  - `GET /api/connector/dequeue/{activityId}` returns the next export, and `POST
    /api/connector/ack/{activityId}` with the dequeue response body confirms it.
  - Until it is acknowledged, the same message comes back every 30 minutes.
  - The dequeue response can carry an `http://` URL. Rewrite it to `https://`.
- One production publisher received F&O export files on Blob storage **[proven in production]**:
  - Each file was a JSON list of `{ FileName, FileContent.rows }`. A blob-triggered function mapped
    each file name to an event type.
  - It published parents before children (prospect, customer, quote, note, …), so subscribers never
    saw a child before its parent.
  - A gap to avoid: rows that failed to parse were logged and skipped, so they were lost. Fail the
    blob, or park the bad rows, instead.

### 4. Polling a modified date-time field

- Possible only when the entity exposes a modified date-time field. Look in `$metadata` first;
  without one you need an entity extension. No production adapter in the corpus polls F&O.
- Query with `$filter=dataAreaId eq '<company>' and <Modified> gt <watermark>&cross-company=true`
  **[Microsoft docs, verify]**. Page through the results: server-driven paging allows at most
  10,000 rows per page **[Microsoft docs, verify]**.
- Overlap the watermark by a few minutes, and dedupe with the deterministic `MessageId`.

### Full and initial load

- A production full load was an HTTP-triggered Logic App **[proven in production]**:
  - It listed the virtual table with `odata.maxpagesize=100`, following the connector's next link
    (its `$skiptoken`) until there were no more pages.
  - For each row it queued a synthetic payload shaped like the trigger's (`SdkMessage = Update`), so
    the same publisher handled the bulk load and the live changes.
- In the same estate, some full-load flows read only the first page **[proven in production]**.
  Always follow the next link.
- For large volumes, export a DMF package instead (option 3).

### Alternatives to a bus adapter

- **Dual-write** **[Microsoft docs, verify]**:
  - Out-of-box, tightly coupled, near-real-time and bidirectional sync between F&O and Dataverse.
  - Every change on one side writes to the other.
  - Choose it when both ends are Dynamics 365 and table-to-table sync is the goal. Use a NimBus
    adapter when other systems consume the changes, or when you need the Resolver's audit trail and
    operator resubmit.
- **Virtual tables, read-only use** **[Microsoft docs, verify]**:
  - Dataverse and Power Platform apps can read and write F&O data live, with no copy.
  - If the only consumer is a Power App, you may not need an integration at all.

## Writing (bus → F&O)

### OData data entities: keys, company and URLs

- Every call must include every key field **[Microsoft docs, verify]**. For company-specific
  entities, `dataAreaId` is part of the key:
  `CustomersV3(dataAreaId='usmf',CustomerAccount='US-001')` **[proven in production]**.
- `dataAreaId` is spelled with a lower-case `d`, and OData property names are case-sensitive
  **[proven in production]**.
- Company id values: lower case in OData filters (`dataAreaId eq 'usmf'`) and in virtual-table
  filters, upper case (`USMF`) in DMF `legalEntityId` and business-event `legalEntity`, and all of
  these worked **[proven in production]**. Compare company ids case-insensitively in your own code.
- OData returns only the default company of the calling user, unless you add `cross-company=true`
  **[Microsoft docs, verify]**.
  - Both production adapters added it to every GET, PATCH and POST **[proven in production]**.
  - Without it, a lookup in another company returns nothing, and the create that follows makes a
    duplicate.
- URL-encode every key value after you double its single quotes. A production adapter had to send
  `/` in key values as `%2F` **[proven in production]**.
  - Use `Uri.EscapeDataString`, not `WebUtility.UrlEncode`: `WebUtility.UrlEncode` turns spaces
    into `+`, which a URL path does not decode.
  - The snippet's `KeyLiteral` does both steps.
- `$filter` supports `eq`, `ne`, `gt`, `ge`, `lt`, `le`, `and`, `or` and `not`, but not `in` or
  `has`. Enums are written as `Microsoft.Dynamics.DataEntities.NoYes'Yes'`, and only `eq` and `ne`
  work on them **[Microsoft docs, verify]**.
- `$expand` goes one level deep only **[Microsoft docs, verify]**.

### PATCH bodies

- Strip key fields, the correlation field and read-only fields from the body
  **[proven in production]**:
  - One production adapter removed `dataAreaId` and the entity key as unnecessary.
  - It also removed display-only fields (a person's composed name, a formatted address, the parent
    account lookup) because F&O rejected the PATCH when they were included.
- Send only fields the adapter owns. With the OData client library, use
  `SaveChangesOptions.PostOnlySetProperties`, so unset properties don't overwrite F&O defaults with
  client defaults **[Microsoft docs, verify]**. A production adapter used it on every save
  **[proven in production]**.
- Don't write a record that hasn't changed. Every update fires data events and virtual-table
  triggers, even when nothing changed **[Microsoft docs, verify]**. The snippet compares before it
  PATCHes.

### Lookup before create (correlation field)

- F&O keys usually come from number sequences, so the upstream id lives in a correlation field. You
  add that field to the entity and the table with an extension.
- A production adapter looked the record up first, then PATCHed it, or POSTed when the lookup
  returned no rows **[proven in production]**. The lookup was
  `GET /data/{EntitySet}?$filter=<CorrelationField> eq <id> and dataAreaId eq '<company>'&$select=<key>&cross-company=true`.
  Its update handlers also fell back to a create when the record didn't exist yet, so updates kept
  working after a failed first create **[proven in production]**.
- On POST, omit the key so F&O assigns it from the number sequence. A production adapter did this
  for prospect and contact entities **[proven in production]**. The sequence must not be manual
  **[unverified]**.
- **Never treat a failed lookup as "not found".**
  - In one production adapter, the lookup helper returned an empty id on any non-success status, and
    on any exception **[proven in production]**.
  - The caller read the empty id as "not found" and POSTed, so every 401, 429 or outage on the GET
    created a duplicate, and hid the real error.
  - The fix: only zero rows in a successful response means "not found". Every other outcome throws a
    classified exception.
  - Two or more rows means the correlation field isn't unique. That is a data problem, so reject it.
- Validate GUID correlation values before you put them in a filter. A production adapter wrote GUID
  values unquoted (`<Field> eq 3f2b…`) **[proven in production]**. Quote string literals and double
  any single quote inside them.

### Non-idempotent creates and retries

- A 429 means F&O rejected the request before running it. Resource-based throttling rejects a
  request when it arrives during high utilization **[Microsoft docs, verify]**, so a throttled
  create can be retried safely. A production adapter relies on this and retries only 429 on its
  create path **[proven in production]**.
- A 5xx, a timeout or a dropped connection after a POST gives no such guarantee. F&O may have
  committed before the response was lost, so resending can create a second record
  **[proven in production]**. That adapter writes a document header and then its lines with
  non-idempotent POSTs. A saved header cannot be deleted through that entity, so it marks the header
  as failed instead **[proven in production]**.
- With a correlation field, the lookup before create makes a retried create safe: the next attempt
  finds the record the lost response created, and updates it. That's why the snippet writes the
  correlation field in the POST body and lets NimBus retry Unavailable.
- Without a correlation field (documents with F&O-assigned numbers), don't let NimBus retry the
  create on 5xx or timeout. Throw `ExternalSystemRejectedException` so the session blocks and an
  operator checks F&O before resubmitting. Retry only `ExternalSystemThrottledException`.
- **NimBus retries are the default.** Throw `ExternalSystemThrottledException`; NimBus schedules
  the retry as a new, audited attempt.
  - Don't retry 429 inside the handler unless one message makes several calls that must not be
    split.
  - If you must, share one retry budget across all calls for the message. In production: 120 s in
    total, and each `Retry-After` capped at 60 s **[proven in production]**.
  - Keep the budget well inside the receiver's lock renewal (`MaxAutoLockRenewalDuration`, 5 minutes
    by default in NimBus 4.2.0). An expired lock means redelivery, which means concurrent duplicate
    writes.
- **Exhausted throttling is not a business rejection.** When the budget runs out, the production
  adapter throws a distinct "throttling exhausted" exception **[proven in production]**:
  - It doesn't mark the record failed in F&O, or move it to any other terminal business state.
  - The message goes back to the queue and retries when F&O has capacity.
  - In NimBus terms, throw `ExternalSystemThrottledException`, never `ExternalSystemRejectedException`.
- A `$batch` with `SaveChangesOptions.BatchWithSingleChangeset` runs its requests as one atomic
  changeset: all succeed or none run **[Microsoft docs, verify]**.
  - With `Microsoft.OData.Client`, a 429 on a `$batch` didn't reliably appear in the exception chain.
    The production adapter read the status from `DataServiceContext.ReceivingResponse` instead
    **[proven in production]**.
  - A batch can hold up to 5,000 operations, but long batches count against the execution-time
    limit **[Microsoft docs, verify]**.

### DMF batch import with PendingHandoff

Use DMF for volume, and for entities that are only importable through DMF. The production flow
**[proven in production]** has three loops. [`snippets/DmfHandoffSketch.cs`](snippets/DmfHandoffSketch.cs)
holds the handler side and the settlement rules.

1. **Handler (per message).** Persist a correlation row in your integration store:
   - the payload;
   - the record keys F&O will report (see [record keys](#record-keys-and-error-attribution));
   - the six `HandoffSettlement` coordinates from the context;
   - status `Pending`.

   Then call `context.MarkPendingHandoff(reason, externalJobId, expectedBy)` and return without
   throwing. NimBus blocks the session, so later changes to the same record wait.
   - The DMF execution id doesn't exist yet, so pass your own row id as `externalJobId` (production
     passed its correlation id).
   - Production used `expectedBy` = 30 minutes.
2. **Batch job (every minute, one instance).**
   - Drain up to `MaxBatchSize` pending rows. Production default: 50.
   - If the same record appears twice in one package, the last row wins.
   - Build the package, then call `GetAzureWriteUrl`, upload, and call `ImportFromPackage`.
   - Store the returned execution id on the rows (`Pending → InFlight`) and return at once. Don't
     poll in the batch job.
3. **Poller (every 30 seconds).**
   - For each `InFlight` execution, call `GetExecutionSummaryStatus`.
   - When it is terminal, call `GetExecutionErrors`, map each error to rows by record key, and
     settle each row with `IHandoffClient.CompleteAsync` or `FailAsync`.
   - Then mark the rows terminal.

Why it is split: the first version imported and then polled inside the batch tick, for up to 5
minutes **[proven in production]**. That blocked new work. A restart in between also lost the
execution id, and the sender waited out the full handoff timeout.

**DMF package API** (`POST /data/DataManagementDefinitionGroups/Microsoft.Dynamics.DataEntities.<Action>`)
**[Microsoft docs, verify]**:
- **`GetAzureWriteUrl { uniqueFileName }`**:
  - Returns `value` as a JSON string, so it is double-encoded. It holds `BlobId` and a SAS `BlobUrl`.
    The production adapter parses it twice **[proven in production]**.
  - The SAS expires, and the package blob is kept for 7 days **[Microsoft docs, verify]**.
- **`ImportFromPackage`**:
  - Body: `{ packageUrl, definitionGroupId, executionId: "", execute: true, overwrite: true, legalEntityId }`.
    It returns the execution id.
  - Set `overwrite: false` when the package contains a composite entity, and include only that one
    composite entity.
  - There is also an async variant, `ImportFromPackageAsync`.
- **Don't call `ImportFromPackage` in parallel.** It fails **[Microsoft docs, verify]**.
  - Production pinned the batch worker to exactly one replica **[proven in production]**.
  - Parallel package imports need **Enhanced parallel package import** turned on in Data management
    framework parameters **[Microsoft docs, verify]**.
- **`GetExecutionSummaryStatus { executionId }`** returns `Unknown`, `NotRun`, `Executing`,
  `Succeeded`, `PartiallySucceeded`, `Failed` or `Canceled` **[Microsoft docs, verify]**.
- **`GetExecutionErrors { executionId }`** returns the error list **[Microsoft docs, verify]**:
  - In production, `value` was a JSON string holding an array of `{ RecordId, Field, ErrorMessage }`
    **[proven in production]**.
  - F&O sometimes put raw XML with unescaped quotes into `ErrorMessage`, which made the whole
    `value` invalid JSON **[proven in production]**. Treat that as one error without a record id;
    the package then fails, instead of every row waiting forever.
- Per-entity error files: `GenerateImportTargetErrorKeysFile`, then `GetImportTargetErrorKeysFileUrl`
  in a loop until it returns a URL, and `GetImportStagingErrorFileUrl` **[Microsoft docs, verify]**.
  The production flow uses `GetExecutionErrors` only.

**Package contents [proven in production]:**
- A ZIP of the definition group's `Manifest.xml` and `PackageHeader.xml`, both exported once from
  F&O, plus one data file per entity, named as the manifest's `InputFilePath`.
- The `XMLField` tags must be mapped to entities in the manifest **[Microsoft docs, verify]**.
- Upload only the data files that have rows. A stale or empty file left in the package was imported
  again.
- Line numbers must be unique within an execution, because the staging table's primary key needs
  it. Assign them while you build the package.

**Settlement rules [proven in production]** (implemented by `DmfHandoff.Decide`):

| Execution result | Settlement |
|---|---|
| `NotRun` / `Executing` | Not terminal: keep polling |
| `Canceled` / `Unknown` | Fail every row ("import did not complete") |
| `Succeeded`, no errors | Complete every row |
| `Succeeded` / `PartiallySucceeded`, every error matched to a row | Fail the matched rows with F&O's text and complete the rest |
| Any error whose `RecordId` is null or matches no row | Fail **every** row in the execution: there is no telling what F&O dropped |
| `PartiallySucceeded` with **zero** errors | **Fail every row.** F&O says something failed and doesn't say what |
| `Failed` | Matched rows fail with their own text; the rest fail with an "undetermined cause" text listing F&O's errors |

<a id="record-keys-and-error-attribution"></a>**Record keys and error attribution [proven in production]:**
- `RecordId` is the entity's natural key values joined by single spaces, for example
  `"{PersonnelNumber} {LegalEntityId} {EmploymentId}"`. Dates are in `MM/dd/yyyy HH:mm:ss`.
- Some entities append values from F&O's own storage, for example date-effective
  `ValidFrom`/`ValidTo`. Match those by prefix, and end the prefix with the separator space, so
  `"P1 "` cannot match the different key `"P1"`.
- Line entities have their own formats: one header-line pair reported `"{LineNumber} {ExternalId}"`
  and another `"{ExternalId} 0"`. Confirm every format from a real error before you rely on it.
- One message can produce several entities. Register all of their keys to the message; if any of
  them fails, the message fails.
- **Journals whose batch number F&O assigns during the import cannot be attributed.** You can't know
  the key in advance, so any error fails the whole package.
  - Cap the package size (`MaxBatchSize`) to bound the blast radius. A clean import still completes
    every row.
  - If you create the journal header through OData first, you get its number and can register
    line keys. Production does this for one journal type, but no real error has confirmed that key
    format yet.
- Persist the keys with the row (production stored them as JSON on the correlation row). The poller
  runs later, in a different tick or even a different process.
- A row that F&O skips without reporting an error settles as succeeded **[proven in production]**.
  The F&O import history is the only place to see it.

**Timeouts and races [proven in production]:**
- **`InFlightTimeout`** (production: 2 h) fails rows whose execution never reaches a terminal
  status. Without it the session stays blocked forever. Run this check before you authenticate:
  when F&O is down, authentication throws, and the stale rows that most need releasing would never
  be reached.
- **`PendingTimeout`** (production: 1 h) fails rows that never got an execution id. That covers a
  crash between `ImportFromPackage` and saving the id. The text says the import may have happened:
  "verify in F&O before resubmitting".
- **Every transition is a conditional update on the row's ETag.**
  - First claim the row as `TimingOut`, then settle, then mark it `Failed`.
  - A poller that records a terminal status first wins the race, and the timeout skips that row.
  - A claim that is never finished is retried once its lease expires (production: 5 minutes).
- **Record the observed terminal status on the rows before you fetch errors or settle.** A
  settlement retry then reuses that status, and doesn't time out an import that already completed.
- If saving the execution id fails after `ImportFromPackage` succeeded, retry a few times, then log
  the execution id at Critical for manual reconciliation. Delete the drained source messages only
  after the id is saved.
- Settlement must address the session the message arrived on: take it from
  `IEventHandlerContext.SessionId`, never recompute it. NimBus only settles the event that blocks
  that session. A misaddressed settlement is logged and completed, and the original stays pending.
  NimBus treats a duplicate settlement the same way, so settle first and mark the row second.
- A failed handoff keeps the session blocked until an operator resubmits or skips. A resubmit runs
  the handler again with the same `EventId`, so let the registration replace a terminal row for that
  `EventId`.

## Errors and throttling

Classify into the template's three exception types. The circuit breaker counts only Unavailable.

| F&O answer | Classify as | Notes |
|---|---|---|
| 429 on OData or a custom service | Throttled | `Retry-After` is in seconds **[Microsoft docs, verify]**. Retries get no priority over new requests, and the wait grows if you keep sending **[Microsoft docs, verify]** |
| 429 after the in-process budget is spent | Throttled | Never Rejected, and never a business-error state in F&O **[proven in production]** |
| 408, 502, 503, 504, timeout, no connection | Unavailable | The first OData call after an AOS restart can be slow while metadata is cached **[Microsoft docs, verify]**. Keep `TimeoutSeconds` generous |
| 500 whose inner error is `AxODataWriteException` ("Write failed for table row … Infolog: …") | Rejected | A validation failure, not an outage. The status F&O uses for it is **[unverified]**: the snippet catches the 500 case |
| Other 500 | Unavailable | After a POST it may have committed: only safe to retry with lookup-before-create (see [Non-idempotent creates](#non-idempotent-creates-and-retries)) **[proven in production]** |
| 400 | Rejected | A production adapter dead-lettered 400 and 403 at once **[proven in production]** |
| 401, 403 | Rejected | Configuration: the app is missing from **Microsoft Entra applications**, or the mapped user lacks a role **[Microsoft docs, verify]**. An operator must fix it; retrying won't |
| 404 on a `$filter` query | Rejected | A wrong entity set or URL, never "not found" |
| A failed lookup GET of any kind | Its own class, **never** "not found" | See [Lookup before create](#lookup-before-create-correlation-field) **[proven in production]** |
| Lookup found the record that a lost POST response created | Treat as success: update it | |
| PATCH where nothing changed | Treat as success: don't write | |
| DMF `PartiallySucceeded` with zero errors | Failure (FailAsync) | **[proven in production]** |
| DMF `GetExecutionErrors` with invalid JSON | Failure of the whole package | **[proven in production]** |

**Where throttling applies [Microsoft docs, verify]:**
- Service protection limits apply only to F&O OData and custom-service endpoints.
- Exempt are the Data Import/Export Framework (DIXF), recurring integrations, the Fin & Ops
  connector, and virtual tables when the Power Platform integration is on. Dataverse's own API
  limits can still apply to virtual-table calls.
- The DMF package API is an OData action. Its exemption isn't spelled out, and Microsoft recommends
  a 429 handler with `Retry-After` even on exempt services, so classify 429 on DMF calls as
  Throttled too.

**Which limits exist [Microsoft docs, verify]:**
- Resource-based limits are on from version 10.0.19. They throttle when web-server CPU or memory is
  high.
- User-based limits (6,000 requests, 1,200 s of execution time and 52 concurrent requests, per user
  per 5 minutes per web server) are disabled from 10.0.36.

**Error body:**
- The production adapters didn't record a real error body.
- Community and GitHub reports show `{"error":{"code":"","message":"An error has occurred.","innererror":{"message":"Write failed for table row of type '…'. Infolog: …","type":"Microsoft.Dynamics.Platform.Integration.Services.OData.AxODataWriteException"}}}`
  **[unverified]**.
- `error.code` is often empty, so the template's `ExternalSystemRejectedException.ErrorCode` is
  rarely useful for F&O; read `innererror.message` instead (the snippet does).

**Retry budget vs. lock:**
- The template's NimBus retry rule for Throttled starts at 15 s and doubles up to 300 s; it does not
  read `Retry-After`. If F&O's `Retry-After` values are longer, raise
  `ExternalSystem:Resilience:ThrottledBaseDelaySeconds`.
- Under sustained throttling, lower `Adapter:MaxConcurrentSessions` rather than raising the retry
  count, and set a throttling priority for the app. A production adapter's throttling design
  recommends the same **[proven in production]**.

## Concurrency

- **One record at a time.** Session on the F&O record identity. If the same key can exist in
  several companies, include the company in the session key. Sessions serialize writes to one
  record; across records, F&O's last write wins.
- **ETags.** F&O returns `@odata.etag` on entity reads **[proven in production]**. Whether a PATCH
  with a stale `If-Match` gets a 412 is not documented for F&O on Learn **[unverified]**. Don't
  build on it until you have tested it. The snippet relies on sessions, not ETags.
- **DMF.**
  - Only one `ImportFromPackage` at a time **[Microsoft docs, verify]**; run one batch worker.
  - Assign line numbers in the single-threaded package build **[proven in production]**.
  - Use ETag-guarded transitions on your own correlation rows **[proven in production]**.
- **Parallelism and throttling.** Resource-based throttling responds to the whole environment's
  load **[Microsoft docs, verify]**. Size `MaxConcurrentSessions` for F&O, not for the bus. For
  sustained throttling, a production adapter's design recommends lower concurrency, and separate
  service principals per integration **[proven in production]**, each with a throttling priority
  **[Microsoft docs, verify]**.
- **Virtual tables and Logic Apps.** Event order is not guaranteed **[Microsoft docs, verify]**, and
  a scaled-out publisher adds its own reordering. Two quick updates can therefore reach the bus out
  of order.
  - Re-read the current state.
  - Put a version (a modified date-time or row version) in the event, so subscribers can drop an
    older state.
  - Standard entities don't always expose one. A production integration added a small row-version
    entity for one record type to get it **[proven in production]**.

## Echo-loop prevention

An echo happens when the adapter writes to F&O, F&O raises a business event or data event for that
write, and the change goes back onto the bus.

- **Identity.** Give the adapter its own Entra app, mapped in **Microsoft Entra applications** to
  its own F&O user. Every write the adapter makes then runs as that user **[Microsoft docs, verify]**.
  Don't share the app or user with the change-capture Logic App, or with other integrations.
- **Business events.** The payload carries `InitiatingUserAADObjectId` **[proven in production]**
  (present in a production schema). Drop events whose value is the adapter's identity. Which object
  id appears for an app-only (S2S) call is **[unverified]**: record one event caused by the adapter
  in a sandbox, and filter on the value you see.
- **Virtual-table data events.**
  - The trigger payload carries `RunAsSystemUserId` **[proven in production]**, but what it holds
    for a change made in F&O is **[unverified]**.
  - If the entity exposes a "modified by" field, filter on it in the trigger's **Filter rows**
    expression. The flow then runs only when the expression is true after the change is saved
    **[Microsoft docs, verify]**.
- **Content, which always works.**
  - The subscriber doesn't write when nothing changed (the snippet compares first).
  - The publisher's deterministic `MessageId` and the subscribers' inbox absorb the rest.
  - This also stops ping-pong when no identity filter is possible. Data events fire on every update,
    even unchanged ones **[Microsoft docs, verify]**, so a no-op write is still an event.
- **Observed gap.** One production F&O publisher published every F&O change unconditionally
  **[proven in production]**. It had no echo filter and relied on every destination being
  idempotent. That only holds if every subscriber skips no-op writes. Add a filter.

## Logic Apps gotchas

Change capture often runs in Logic Apps (Standard): virtual-table webhooks and business-event
triggers.

- **A redeploy does not re-subscribe webhook triggers** **[proven in production]**.
  - A zip deploy of unchanged workflows left the Dataverse callback registrations, and so the F&O
    data-event activation, as they were. The workflows showed Healthy, the events sat in the F&O
    catalog, and they never activated.
  - Disabling and re-enabling a workflow (`Workflows.<name>.FlowState` app settings) doesn't
    re-subscribe either: trigger state, including the subscription, survives it.
  - Only a change to the trigger definition makes the runtime unsubscribe and subscribe again. The
    fix was a per-deploy nonce stamped into the trigger definition, as an opt-in "force
    re-subscribe" switch.
  - The runtime has no `.../workflows/{name}/disable` route; use the `FlowState` app settings.
- **Don't force-re-subscribe business-event triggers** (`dynamicsax`). It leaves the previous
  activation behind as a duplicate in the F&O business-event catalog **[proven in production]**.
  Stamp the nonce only on triggers whose path contains `callbackregistrations`.
- **Rewrite hosts when you promote a workflow** **[proven in production]**.
  - Workflows hard-code the Dataverse org URL in the trigger's `organization` header.
  - The business-event trigger hard-codes the bare F&O host in its subscribe path:
    `/datasets/<host>/subscribebusinessevent/<EventName>`.
  - Workflows promoted from test to UAT kept subscribing to the test F&O until the pipeline
    rewrote these hosts. Use a scheme-less host pair for the business-event path.
  - Sandbox and production hosts differ, and a wrong host deploys a workflow that silently talks to
    the wrong environment.
  - `connections.json` holds absolute ARM ids, subscription included; rewrite those for a
    cross-subscription promotion.
- **A site deploy replaces all app settings** **[proven in production]**.
  - Deploying the `Microsoft.Web/sites` resource replaces the whole app-settings collection.
  - It wiped the `*_connectionId` and `*_connectionRuntimeUrl` settings that bind workflows to their
    API connections, so the connections looked "lost".
  - It also wiped any `Workflows.<name>.FlowState`, so a workflow disabled in the portal came back
    enabled.
  - Let the infrastructure template (Bicep) own the connection settings, not a post-deploy script.
- **Managed-identity API connections** **[proven in production]**.
  - The connections (Dataverse `commondataservice`, Fin & Ops `dynamicsax`) use the Logic App's
    managed identity. `connections.json` sets `authentication.type: ManagedServiceIdentity`.
  - Creating them from Bicep stripped the manual environment binding and consent on every deploy.
    Create them once per environment in the portal, reference them as `existing`, and let Bicep add
    only the access policies.
  - The managed identity must be an **Application User** in Dataverse and an app in F&O
    **Microsoft Entra applications**. Both are one-time manual steps per environment.
- **Monitoring gap** **[proven in production]**.
  - A failed or never-firing workflow is invisible to NimBus, because nothing reaches the bus.
  - The production Logic App ran without Application Insights until that was added explicitly, and
    the "Healthy but never activated" state above raised no error at all.
  - Wire the Logic App to Application Insights and alert on failed runs. Also alert on "no event
    for N hours" per trigger, and check the F&O business-event error log (**Resend** recovers
    failed events) **[Microsoft docs, verify]**.
- **The queue hop** **[proven in production]**. Production workflows Base64-encoded the trigger
  body before `putMessage`, to match the queue trigger of the publishing function. That publisher
  ran on the Flex Consumption plan and could scale out to many instances. Parallel instances can publish two changes to one
  record in the wrong order, and the session key only keeps the order they were published in. So
  carry a version in the event and let subscribers drop an older state that arrives after a newer
  one (see [Concurrency](#concurrency)).

## Gotchas

- `cross-company=true` is easy to forget on one call; that one call then duplicates records
  **[proven in production]** **[Microsoft docs, verify]**.
- A GET helper that returns "empty" on failure is a duplicate generator **[proven in production]**.
- OData property names are case-sensitive: `dataAreaId`, not `DataAreaId` **[proven in production]**.
- Dataverse virtual-table names and fields are lower case with an `mserp_` prefix. Option sets come
  as an integer plus a `_..._label`. Compare the integer values: labels are localized
  **[proven in production]**.
- The DMF response `value` is double-encoded JSON (`GetAzureWriteUrl`, `GetExecutionErrors`)
  **[proven in production]**.
- `PartiallySucceeded` with zero errors means failure; `Succeeded` can still come with errors.
  Always read the errors **[proven in production]**.
- Journals with F&O-assigned batch numbers fail or succeed as a whole package. Cap the package size
  **[proven in production]**.
- One unattributable error fails every message in its package **[proven in production]**.
- An empty or stale data file left in a package is imported again. Upload only the files that have
  rows **[proven in production]**.
- A DMF row skipped without an error settles as succeeded **[proven in production]**.
- `ImportFromPackage` must not run in parallel **[Microsoft docs, verify]**.
- Business events and data events arrive in any order **[Microsoft docs, verify]**.
- Business events are thin. Don't try to export data through them **[Microsoft docs, verify]**.
- Data events skip view-based entities and virtual fields **[Microsoft docs, verify]**.
- The first OData call after an AOS restart is slow **[Microsoft docs, verify]**.
- Business-event endpoint secrets expire. Events then pile up in the F&O error log until you resend
  them **[Microsoft docs, verify]**.
- The recurring-integration dequeue URL can come back as `http://` **[Microsoft docs, verify]**.
- A writable-looking field can be computed. If a PATCH fails on one field, drop it from the body
  rather than retrying **[proven in production]**.
- F&O country/region ids are F&O's own codes, not necessarily ISO alpha-2 **[unverified]**. Map them
  explicitly (the snippet's `CountryField`).

## Simulator

The local simulator emulates the OData subset and the DMF actions an F&O adapter calls, with
switches for throttling, failures and DMF outcomes. It also has driver endpoints that fire
business-event and data-event notifications. Routes, shapes and switches:
[simulator-routes.md](simulator-routes.md).

A production simulator for this kind of adapter emulated:
- the four DMF actions;
- `$batch`, with 429 injection;
- a token stub;
- an outbox that published entity changes as events, with retry.

It drove the throttling tests (throttled then recovered, and throttled beyond the budget)
**[proven in production]**.

## Snippets

| File | What it is |
|---|---|
| [`snippets/FinanceOperationsODataClient.cs`](snippets/FinanceOperationsODataClient.cs) | `IExternalSystemClient` for F&O OData, keyed by `dataAreaId` + `CustomerAccount`, with the upstream id in a correlation field. It looks up with `$filter` and `cross-company=true`, POSTs to create, and PATCHes only changed, updatable fields to update. A failed lookup is never "not found". A 500 `AxODataWriteException` is Rejected. One place to change the entity set, company, correlation field and create defaults |
| [`snippets/DmfHandoffSketch.cs`](snippets/DmfHandoffSketch.cs) | Handler side of DMF + PendingHandoff: capture the settlement coordinates, then `MarkPendingHandoff`. Also the settlement rules (`Decide`), the `GetExecutionErrors` parser, and `SettleAsync` over `IHandoffClient`. Storage, the package builder and the poller loop are yours |

Both files use namespace `TemplateAdapter.Adapter.Clients` and the template's `ExternalSystem*`
types, so the generator's renaming applies to them. Both compile with the template in a Release
build: 0 warnings, 0 errors.

## Sources

**Production systems (anonymized):**
- A production F&O adapter on NimBus's predecessor platform. It wrote by OData (documents with
  lines) and by DMF + PendingHandoff (master data and journals). Sources: its throttling, record-id
  matching and settlement docs, its ADRs, the resilient OData executor, the batching and settlement
  services, its simulator, and its fix history.
- A second production F&O adapter on the same platform. It wrote OData data entities with
  `cross-company=true`, composite keys and lookup-before-create, and published F&O export files.
- The production Logic Apps (Standard) change-capture workflows for the first adapter: virtual-table
  triggers, business-event triggers, full loads and lifecycle flows. Also their deployment and
  promotion pipeline docs and its fix history.

**Microsoft Learn (checked 2026-10-01):**
- [Open Data Protocol (OData)](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/odata): key fields, `cross-company`, query options, paging, enums, `$batch` changesets, `PostOnlySetProperties`, AOS warm-up
- [Service endpoints overview](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/services-home-page): OAuth, Microsoft Entra applications
- [Service protection API limits](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-protection-api-limits): 429, limits, exemptions
- [Throttling prioritization](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/priority-based-throttling)
- [Retry operations](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-protection-retry-operations): `Retry-After`
- [Data management package REST API](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/data-management-api)
- [Recurring integrations](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/recurring-integrations)
- [Business events overview](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/business-events/home-page)
- [Manage business event endpoints](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/business-events/managing-business-event-endpoints)
- [Data events](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/business-events/data-events)
- [Virtual entities overview](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/power-platform/virtual-entities-overview)
- [Dual-write overview](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/dual-write/dual-write-overview)
- [Trigger flows when a row is added, modified, or deleted](https://learn.microsoft.com/en-us/power-automate/dataverse/create-update-delete-trigger): `SdkMessage`, filter rows, Select columns on virtual tables

**Not on Learn, so tagged [unverified]:** the OData error body shape (community and GitHub reports,
for example [microsoft/Dynamics-AX-Integration#74](https://github.com/microsoft/Dynamics-AX-Integration/issues/74)).
