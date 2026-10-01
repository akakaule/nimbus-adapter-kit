# Dataverse / Dynamics 365 CE system pack

This pack covers adapters that exchange data with **Microsoft Dataverse online**: the Web API at
`https://{org}.crm{n}.dynamics.com/api/data/v9.2/`, and the Dataverse SDK for .NET (`ServiceClient`)
where noted. Dataverse is the database behind Dynamics 365 CE apps (Sales, Customer Service,
Field Service) and Power Apps. The pack tells you how to authenticate, detect changes, write
idempotently, classify errors and stop echo loops. It does not cover Finance & Operations virtual
tables or business events; see the F&O pack for those.

**Last verified: 2026-10-01.**

Every statement about Dataverse behaviour carries one of two tags:

- **[proven in production]**: observed in a production Dataverse CE adapter (three adapters on the
  NimBus architecture, with their plug-ins and fix history).
- **[Microsoft docs, verify]**: taken from learn.microsoft.com on the date above (URLs under
  [Sources](#sources)). Microsoft changes these. Check them against your environment before you rely on them.

Statements without a tag are design advice for NimBus adapters, not claims about Dataverse.

## At a glance

| Topic | What to use |
|---|---|
| Auth | An Entra app registration or managed identity, added to the environment as an **application user** with a security role. Request a token for `https://{org}.crm{n}.dynamics.com/.default`. The template's `BearerTokenHandler` with `DefaultAzureCredential` already does this. **[proven in production]** |
| Recommended change detection | An **asynchronous** plug-in step or Service Endpoint sends a **thin notification** (table, id, message). The adapter's ingress re-reads the row and publishes it with a deterministic MessageId `{table}:{id}:{versionnumber}`. **[proven in production]** (thin notification and re-read) |
| Write API | `PATCH accounts(new_externalid='...')`, an upsert on an **alternate key**. Lookups use `"parentaccountid@odata.bind": "accounts(new_externalid='...')"`. With the SDK, use `UpsertRequest`. **[Microsoft docs, verify]**. Upsert writes with an external key are **[proven in production]** |
| Throttling signal | `429 Too Many Requests` with `Retry-After` in seconds and codes `0x80072322`, `0x80072321` and `0x80072326`. Limits apply **per user, per web server**, over a 5-minute sliding window. **[Microsoft docs, verify]** |
| Echo identity | The `systemuserid` of the adapter's application user. The plug-in compares it with `InitiatingUserId` and skips the change when they match. **[proven in production]** |

## Authentication

**Identity.** Dataverse authorizes Entra identities that exist in the environment as users.

- For a daemon such as an adapter, create an **application user**: Power Platform admin center >
  environment > Settings > Users + permissions > Application users > New app user.
  - Each Entra app registration can have one application user per environment.
  - The application user is unlicensed.
  - It needs security roles. **[Microsoft docs, verify]**
- A **managed identity** can be added by its **Application ID**, not its name.
  **[Microsoft docs, verify]**
- A user-assigned managed identity requesting `{orgUrl}/.default` works in production with the
  token-provider `ServiceClient`. **[proven in production]**

**The template covers it.** Configure the adapter like this:

```json
"ExternalSystem": {
  "BaseUrl": "https://contoso.crm4.dynamics.com/api/data/v9.2/",
  "Scope": "https://contoso.crm4.dynamics.com/.default"
}
```

`ExternalSystemClientRegistration` registers `DefaultAzureCredential` and `BearerTokenHandler`
when `Scope` is set:

- In Azure, the credential is the managed identity.
- On a developer machine, it is the developer's own sign-in. That user must also exist in the
  environment with a role. One production adapter ran the same way: managed identity in the cloud,
  developer credentials locally. **[proven in production]**
- Leave `Scope` empty for the simulator.
- Keep the trailing slash on `BaseUrl`, and keep the `/api/data/v9.2/` path in it.

**Smoke test from Windows.** Run these in PowerShell with your own sign-in:

```powershell
$org = "https://contoso.crm4.dynamics.com"
$token = az account get-access-token --resource $org --query accessToken -o tsv
Invoke-RestMethod -Uri "$org/api/data/v9.2/WhoAmI" -Headers @{ Authorization = "Bearer $token"; Accept = "application/json" }
```

To find the application user's `systemuserid`, query `systemusers?$filter=applicationid eq
<client-id>&$select=systemuserid,fullname`. `applicationid` is a readable column on `systemuser`.
**[Microsoft docs, verify]**

You need that id for the echo filter. Store it in a Dataverse **environment variable** and have the
plug-in read it there. Production plug-ins fail if the variable is missing or empty. **[proven in production]**

**Debugging auth.**

- One production adapter logs the token's `aud`, `appid`, `oid` and `idtyp` claims once at
  startup. That settles "which identity actually called Dataverse" in seconds. **[proven in production]**
- A 401 means the identity is not an application user in *this* environment, or the scope names
  a different org.
- A 403 with `PrivilegeDenied` (`0x80040220`) means the identity's security role lacks a privilege.
  **[Microsoft docs, verify]**

**`ServiceClient` alternative.** Use it if you prefer the SDK (`Microsoft.PowerPlatform.Dataverse.Client`):

- Construct it with a token-provider function: `new ServiceClient(new Uri(orgUrl), tokenProviderFunction: async _ => (await
  credential.GetTokenAsync(new TokenRequestContext([$"{orgUrl}/.default"]), ct)).Token)`.
  The function receives the instance URI and returns the access token. `useUniqueInstance` defaults
  to `true`. **[Microsoft docs, verify]**
- Register **one singleton** per process, set `EnableAffinityCookie = false`, and call **only the
  async methods** (see [Concurrency](#concurrency)). **[proven in production]**
- Keep `ServiceClient` out of handlers' constructors as a per-message `new`. Building it per event
  was observed in a production function. That is a connection setup per message.
  **[proven in production]**

## Change detection (Dataverse → bus)

Options are ranked. Use the first one unless a constraint rules it out.

### 1. Plug-in step or Service Endpoint → thin notification → re-read → publish (recommended)

**How it looks in production.** All three variants below were observed:

- A custom plug-in writes a thin notification `{table, id, action}` to an Azure Storage queue. An
  ingress function re-reads the row by id, maps it and publishes. **[proven in production]**
- A notification carrying only the record id and an event type arrives through Event Grid. The
  handler re-reads by id and publishes. **[proven in production]**
- A synchronous plug-in maps the *full* event and POSTs it to the adapter, which publishes it.
  **[proven in production]**
  - That team **rejected** the thin-notification design on purpose. A re-read publishes the row's
    state at re-read time, not at the moment of the change.
  - The plug-in also stamps a sync status and date back on the record.
  - Choose this variant only when consumers need each intermediate state.

**Without custom plug-in code.** Register a **Service Endpoint**, with a queue or topic contract
and the JSON message format, plus an **asynchronous** step per table and message. **[Microsoft docs, verify]**

- Dataverse's asynchronous service posts the `RemoteExecutionContext` as a system job.
- A failed post is retried with growing intervals. Failures are visible under System Jobs.
- Service Bus contracts support only asynchronous steps.
- Dataverse authenticates to Service Bus with SAS.

**Payload limits.**

- **Service Endpoint.** Above 192 KB, Dataverse drops `ParentContext`, `InputParameters` and the
  images, and adds a `MessageMaxSizeExceeded` property. If the context is still too large, the
  message is not sent. **[Microsoft docs, verify]**
  - Configure only the image columns you need.
  - Dead-letter a truncated context. Never publish it.
- **Webhook** (an alternative to the Service Endpoint). **[Microsoft docs, verify]**
  - The payload is the same JSON. Above 256 KB, the same properties are removed and an
    `x-ms-dynamics-msg-size-exceeded` header is sent.
  - The timeout is 60 seconds.
  - Dataverse makes **one** more attempt, and only on 502/503/504.
  - For a *synchronous* webhook, a later failure rolls the data back, but the request already sent
    can't be recalled. That gives you phantom events, so use asynchronous steps.

**Re-read, then publish with a deterministic MessageId.**

- Re-read the row with `$select` (or `ColumnSet`) of exactly the columns the event carries.
- Publish with `IPublisherClient.Publish(@event, sessionId, correlationId, messageId, ct)`, where
  `messageId = $"{table}:{id}:{versionnumber}"`, and turn on `UseInbox` in subscribers.
- `versionnumber` is a readable BigInt column on every table (`account` shows it). **[Microsoft docs, verify]**
  - Do not parse the `@odata.etag` value for this. Microsoft says to give ETags no meaning beyond
    equality. **[Microsoft docs, verify]**
- Avoid `modifiedon` ticks. The Web API returns `modifiedon` to the second, as in
  `2023-04-07T21:59:01Z`. **[Microsoft docs, verify]**
  - Two updates within one second can then give two different states the same MessageId.
  - The inbox drops the second, and the subscriber keeps the stale one.
- Both production ingress functions published with `Guid.NewGuid()` MessageIds. That disables
  deduplication: every notification, retry and replay becomes a new message.
  **[proven in production]** Don't copy that.

**Coalescing trade-off.** A re-read always reads the latest state.

- With a version-based MessageId, three notifications for the same row that are re-read after the
  third change publish one message. The inbox absorbs the other two.
- That is good for state synchronisation: there are fewer writes downstream, and a late
  notification can never publish an older state.
- It is wrong when consumers need every transition, such as a status history. For that, publish
  from the plug-in's images (the third variant above) and accept the extra mapping in plug-in code.

**Session key.** Key the session on the record (`{table}:{id}`), not on its parent.

- One production adapter put contact events in the parent account's session.
- It moved each contact to its own session so that one failed account no longer blocks its
  contacts. **[proven in production]**

**Ordering.** Asynchronous delivery and parallel ingress invocations can reorder notifications.

- Re-reading by id makes that harmless for state sync: a stale notification still reads the
  newest row.
- Delete notifications can't be re-read. Publish them from the pre-image.

**Ingress design.** NimBus's Dataverse ingress preview is not yet production-verified. Use its
design as a reference:

- It publishes before it completes the queue message.
- It dead-letters with reason codes such as `TruncatedContext`, `TableNotAllowed`,
  `OperationNotSupported`, `MissingRequiredImage` and `UnexpectedOrganization`.
- It uses a session per `{organization}:{table}:{id}`.

### 2. Event Grid or Power Automate

- **Event Grid.** A Service Endpoint can use an Event Grid contract, with the Event Grid or
  CloudEvents schema. **[Microsoft docs, verify]**
  - In production, a thin event re-read by id through Event Grid works. **[proven in production]**
  - Event Grid adds no ordering or replay beyond its own retries.
- **Power Automate.** The trigger is "When a row is added, modified or deleted". **[Microsoft docs, verify]**
  - It runs once per update, even when the values didn't change.
  - **Select columns** fires when a listed column is *in* the request, not when it *changed*.
  - Lookup columns are not supported in Select columns.
  - Bulk writes can bypass flows with `MSCRM.SuppressCallbackRegistrationExpanderJob`.
  - A flow that POSTs to the ingress is quick to build, but harder to version, test and monitor
    than a registered step.

### 3. Polling

- **Watermark on `modifiedon`.**
  - Query `$filter=modifiedon ge <watermark>` with `$orderby=modifiedon,accountid` and `$select`.
  - Deduplicate by `versionnumber`, because the watermark is only accurate to the second.
  - Deletes are invisible.
  - One production team rejected polling: it was hard to tell *which* columns changed and whether
    a change was already published. **[proven in production]**
- **Change tracking.** **[Microsoft docs, verify]**
  - Enable **Track changes** on the table. It can't be disabled again.
  - Web API: send `Prefer: odata.track-changes` and keep the returned `@odata.deltaLink`. Deleted
    rows come back as `$deletedEntity` with `"reason": "deleted"`.
  - `$filter`, `$orderby`, `$expand` and `$top` are not supported with change tracking.
  - SDK: `RetrieveEntityChangesRequest` with `DataToken`, paged by 5,000. The first call (without a
    token) returns **all rows**.
  - Tokens expire after 7 days by default (`ExpireChangeTrackingInDays`). After that, the call
    throws and you must resync.
  - The calling user needs organization-level read on the table.
  - It returns changed rows with their current values, not a log of intermediate updates. It
    coalesces like a re-read.

### Initial and bulk sync

Publish the **same events** as the live flow, with the same deterministic MessageIds:

- A rerun is then deduplicated, and consumers need no special code.
- Page through the table with `$select` and `Prefer: odata.maxpagesize`. A page has at most 5,000
  rows for standard tables. **[Microsoft docs, verify]**
- Alternatively, use the first change-tracking call above, which also gives you the token to
  continue from.
- Publish parents before children, so children don't hit the missing-reference policy.

## Writing (bus → Dataverse)

### Upsert on an alternate key

**Define the alternate key first.** **[Microsoft docs, verify]**

- Key columns can be text, whole number, decimal, date-time, lookup or choice.
- A table can have up to 10 keys, each within 900 bytes and 16 columns.
- Keys are not available on virtual tables.
- The key's index is built **asynchronously**. Its status runs Pending → In Progress → Active (or
  Failed). Check that it is Active before go-live.

**`PATCH accounts(new_externalid='ACC-1')` is an upsert.** It creates the row if the key is new and
updates it otherwise. **[Microsoft docs, verify]**

- It answers **`204 No Content` in both cases**, with an `OData-EntityId` header that uses the key
  values.
- With `Prefer: return=representation` (and `$select=accountid`), it answers **201** on create and
  **200** on update. That costs an extra server-side retrieve.
- **Don't send the key column in the body.** On an update it is ignored. On a create, the body value
  wins over the URL value.
- Restrict the upsert with conditional headers:
  - `If-Match: *` means update only: you get **404** if the row is missing.
  - `If-None-Match: *` means create only: you get **412** "A record with matching key values
    already exists." if it exists.
- Key values containing `/ < > * % & : \ ? +` fail in the URL. Read with
  `$filter=new_externalid eq '...'` instead.
  - Write those rows by GUID.
  - Parameter aliases don't work inside key predicates.
- In OData string literals, double single quotes: `'O''Brien'`.

**SDK.** **[Microsoft docs, verify]**

- Use `UpsertRequest { Target = new Entity("account", "new_externalid", key) }`.
  `UpsertResponse.RecordCreated` tells create from update.
- On standard tables, Upsert runs **Create or Update**, so plug-ins on those messages fire.
- Elastic tables behave differently.
- Upsert is slower than Create when you know the row is new.

**Upsert writes in production.**

- One production adapter writes with `UpsertRequest`. **[proven in production]**
- **Symmetric upserts.** A webhook delivered an "Update" for a brand-new record hours before its
  "Create". A create-only handler then made a **duplicate** row. **[proven in production]**
  - Make every create and modify path an upsert.
  - Let a "modified" event for a missing row create it, instead of blocking the session.
    **[proven in production]**

### Lookups with `@odata.bind`

Bind by the parent's alternate key, as `"parentaccountid@odata.bind": "accounts(new_externalid='P-1')"`.
**[Microsoft docs, verify]**

- This needs no GUID lookup.
- `"parentaccountid@odata.bind": null` (or `"parentaccountid": null`) clears the lookup.
- A missing parent fails the write. Dataverse lists `RecordNotFoundByEntityKey` (`0x80060891`).
  **[Microsoft docs, verify]** The HTTP status for a failed bind was not verified.
- The template's handler checks the parent first and throws `MissingReferenceException` with a
  remedy. Keep that check.

**SDK.** Use `new EntityReference("account", "new_externalid", key)`. Dataverse resolves the key to
the GUID. **[Microsoft docs, verify]**

### Write only what changed

Microsoft says to send only the changed columns. **[Microsoft docs, verify]**

- Sending unchanged values still fires plug-ins and flows registered on those columns, and writes
  audit entries.

Production maintainers read the row **fresh**, compare the owned columns and skip the write when
nothing differs. **[proven in production]**

- In one burst-heavy flow, most messages ended as "skipped, no change", at the cost of one read
  and no write.
- This is also the sink-side half of echo prevention.

Never write back an entity instance that `OrganizationServiceContext` (LINQ) materialised. It is
tracked as `Unchanged`. **[proven in production]**

- The update fails with "EntityState must be set to null, Created (for Create message) or Changed
  (for Update message)".
- Build a fresh `Entity` with the row's id.

### State fields (`statecode` and `statuscode`)

**What production saw.** **[proven in production]**

- **Don't rely on setting an inactive state on create.** Creating a custom-table row with an
  inactive `statecode` and `statuscode` failed with "`<n>` is not a valid status code for state
  code Active". The status was checked against the *default* state.
  - The fix: strip both fields, create, then set the state in a second call.
  - A plain update that set both fields together was also checked against the row's *current*
    state, so the same production code routes state changes through `SetStateRequest`.
- Some tables start in a non-active default state (products in Draft) and need a state change
  after create.
- Send the state **only when it differs**. That removed a state round-trip from every routine update.
- One production adapter retries "Cannot find record to change state" as transient.

**What Microsoft says.** **[Microsoft docs, verify]**

- `SetStateRequest` is deprecated in favour of an `Update` that sets `statecode` and `statuscode`.
- Always send `statuscode` with `statecode`. Otherwise the default status is applied.
- An update that mixes state fields with other fields runs Update plug-ins twice: once for the
  other fields, once for the state fields.
- The `account` metadata lists `statecode` as valid for create.

So the behaviour varies by table and by its custom status transitions. Test each table's create
and deactivate path against a real environment.

### Batches: `$batch` and `ExecuteMultiple`

**Limits.** **[Microsoft docs, verify]**

- **`$batch`**: up to 1,000 requests.
  - A changeset is atomic.
  - Without `Prefer: odata.continue-on-error`, the batch stops at the first failed request.
- **`ExecuteMultipleRequest`**: typically up to 1,000 requests. If you exceed the limit, it faults
  with `MaxBatchSize`.
  - Each request runs in its own transaction.
  - `ContinueOnError = true` returns per-item `Fault`s instead of throwing.
- Batches don't bypass service protection. They raise the execution time per request.
- Microsoft recommends single requests in parallel, or small batches (start at 10), over large
  batches.

**In production.** **[proven in production]**

- One adapter chunked to 1,000 with `ContinueOnError = true` and never read the responses. That
  fails silently and partially.
- Another bulk path set `ContinueOnError = false` and routed the faulted item to an error queue.
- In NimBus, one message is normally one write. Use a batch only for bulk events, and turn every
  item `Fault` into a failure the operator can see.

### Bypassing custom logic

**The options.** **[Microsoft docs, verify]**

- **`MSCRM.BypassBusinessLogicExecution: CustomSync,CustomAsync`** is a Web API header. The SDK
  equivalent is the optional parameter `BypassBusinessLogicExecution`.
  - It skips custom synchronous and/or asynchronous plug-ins and workflows.
  - It needs the `prvBypassCustomBusinessLogic` privilege.
- The legacy **`MSCRM.BypassCustomPluginExecution: true`** (`BypassCustomPluginExecution`) skips
  only synchronous logic. It needs `prvBypassCustomPlugins`.
- **Who has the privileges.** Only System Administrator has them by default. Add them to another
  role through the API (`AddPrivilegesRole`); the role designer can't.
- **What is not bypassed.**
  - Microsoft's own plug-ins and solutions.
  - Power Automate flows: use `MSCRM.SuppressCallbackRegistrationExpanderJob` for those.
- ISV solutions' logic *is* bypassed.
- `ServiceClient` has no `BypassPluginExecution` property. Set the optional parameter on the request.

**A production gotcha.** One production adapter sends `UpsertRequest` with
`Parameters = { { "BypassPluginExecution", true } }`. That is not the documented parameter name.
**[proven in production]** (that it is used; whether it takes effect was not verified)

- Check the plug-in trace log before you trust a bypass.

**Advice.** Bypass is a bulk-load tool. It also skips the customer's business rules and your
echo-filter plug-in. For echo prevention, the identity check below is the precise tool.

### Columns and choices

- **`$select` and `ColumnSet` always.**
  - Microsoft: without `$select`, all columns come back, which is slower. **[Microsoft docs, verify]**
  - When a production adapter moved reads to `QueryExpression`, it mirrored the previous projection
    in the `ColumnSet`, so the payload did not grow. **[proven in production]**
  - A helper that falls back to `new ColumnSet(true)` when no columns are passed was observed.
    Don't. **[proven in production]**
- **Compare choice (option set) values, never labels.**
  - Formatted values (`@OData.Community.Display.V1.FormattedValue`) are *localized* labels.
    **[Microsoft docs, verify]**
  - In production, a handler compared a yes/no choice's formatted label with "Yes". It silently
    set the target flag to false for rows that should have been true: the label was null when no
    formatted value was supplied, and a translated word under a non-English user language.
    **[proven in production]**
  - Map numeric values in the pure mapper. An unknown value is rejected with
    `PicklistValueOutOfRange` (`0x8004431A`). **[Microsoft docs, verify]**

### Stale writes, reference data and uniqueness

**Stale-write guard.** Use one when the *source* can deliver out of order, as several near-
simultaneous events from an ERP through a path without ordering can. **[proven in production]**

- Keep a per-row high-water mark of the source's row version in an adapter-owned store.
- Skip events at or below it, and advance it only **after** the Dataverse write succeeds.
- Leave the guard inert while the field is 0, so it can ship before the source stamps it.
- Sessions per row make the read-compare-advance race-free.
- The guard handles ordering. The no-op compare above handles echoes and redeliveries. Keep both.

**Reference-data cache.** **[proven in production]**

- Cache only foreign-key reads, where only the immutable id is used. Read fresh for write decisions.
- Never cache nulls.
- Bound the cache size, and use a short TTL (5 minutes in production).
- Evict on your own writes.
- The cache is per replica.

**Uniqueness beyond your key.** A platform or ISV plug-in can enforce uniqueness that your
alternate key doesn't know about. **[proven in production]**

- A product create was refused because another row already had that product number.
- The fix: look the row up by that business number, adopt it, and stamp the external id on it.

## Errors and throttling

`snippets/DataverseErrorCodes.cs` classifies by code first, then by status. A 412 can be a duplicate
or a version conflict, and a platform fault's status was not verified, so the code decides.

| Signal | Status | Code(s) | Adapter outcome | Source |
|---|---|---|---|---|
| Service protection: number of requests | 429 + `Retry-After` (s) | `0x80072322` | **Throttled** (honour `Retry-After`) | **[Microsoft docs, verify]** |
| Service protection: combined execution time | 429 + `Retry-After` | `0x80072321` | **Throttled** | **[Microsoft docs, verify]** |
| Service protection: concurrent requests | 429 + `Retry-After` | `0x80072326` | **Throttled**; lower your parallelism | **[Microsoft docs, verify]** |
| Web API unavailable, gateway errors | 502 / 503 / 504, or no response | none | **Unavailable** (counts toward the circuit breaker) | **[Microsoft docs, verify]** (503); **[proven in production]** (502/503/504, timeouts, socket errors treated as transient) |
| Platform fault with a "retry later" code | any; classify by code | SQL timeout `0x80044151`, DatabaseUnavailable `0x80048564`, server busy `0x8004A001`, SQL networking `0x80073000` | **Unavailable** | **[Microsoft docs, verify]** |
| SQL backend briefly unavailable, deadlock | fault without a usable code | message fragments: "should be retried later", "is not currently available", "deadlock", "There is no active transaction" | **Unavailable** | **[proven in production]** (a ~12 s backend outage failed 13 of 111 messages permanently, because the classifier didn't recognise these messages) |
| Duplicate key on a **create-only** write | 412 (Microsoft lists DuplicateRecord under 412); classify by code, not status | `0x80040237`, `0x80060892`, or the message "A record with matching key values already exists" | **Success**: an earlier attempt created it | **[Microsoft docs, verify]** (codes, 412); **[proven in production]** (that message treated as an idempotent no-op) |
| Duplicate key on an **upsert** | as above | same | Repeat once with `If-Match: *`. Success means a concurrent create; 404 means another unique key → **Rejected** | design (see the snippet) |
| Row version conflict | 412 | `0x80060882` ConcurrencyVersionMismatch | Only when you send `If-Match: <etag>`: re-read and re-apply once, then **Rejected** | **[Microsoft docs, verify]** |
| Row not found | 404 | `0x80040217`, `0x80060891` | GET: return `null`. Update-only write: the row is gone | **[Microsoft docs, verify]** |
| Privilege missing | 403 | `0x80040220` PrivilegeDenied | **Rejected**; the operator grants the role, then resubmits | **[Microsoft docs, verify]** |
| A plug-in threw | 400 | `0x80040265` IsvAborted; the message is the plug-in's | **Rejected** (business rule) | **[Microsoft docs, verify]** |
| Bad payload, URL or column (mapping or schema drift) | 400 | `0x80048d19` ODataClientPayloadError, `0x80060888` CrmHttpClientError | **Rejected** | **[Microsoft docs, verify]**; schema-mismatch messages reached operators in production **[proven in production]** |
| Invalid state/status pair | 400 | `0x80048408` InvalidStateCodeStatusCode | **Rejected**; fix the state handling | **[Microsoft docs, verify]**; message "`<n>` is not a valid status code for state code `<state>`" **[proven in production]** |
| Field too long, choice out of range | 400 | `0x80044331`, `0x8004431A` | **Rejected** | **[Microsoft docs, verify]** |
| Wrong identity or scope | 401 | none | **Rejected** by the template's mapping. It blocks every session and is not an outage, so alert on it | template behaviour |

**Service protection limits.** **[Microsoft docs, verify]**

- **The numbers, per user and per web server:**
  - 6,000 requests per 300-second sliding window.
  - 1,200,000 ms (20 minutes) of combined execution time per window.
  - 52 concurrent requests. That is the default; it can be higher.
- **Who is limited.** Application users get the same limits as everyone else.
- **When it bites.**
  - The request and time limits can be exceeded for a while before enforcement starts.
  - The concurrency limit fails at once.
  - A client that keeps pushing gets longer `Retry-After` values.
- **Plug-ins.** Operations by plug-ins don't count as requests, but their compute time is added to
  the triggering request.
- **Response headers.** `x-ms-ratelimit-burst-remaining-xrm-requests` and
  `x-ms-ratelimit-time-remaining-xrm-requests` are for debugging only.

**The `ServiceClient`'s own retry is a hidden retry.**

- In production, `MaxRetryCount = 5` with `RetryPauseTime = 70 s` meant up to **5 min 50 s** of
  pauses inside one call. **[proven in production]**
  - That outlived the Service Bus receiver's 5-minute lock renewal.
  - The broker redelivered a message that was still being processed.
- A handler-level retry of 5 attempts around such calls multiplied the worst case. The same team
  logged that as an open problem. **[proven in production]**
- During a backend outage, the `ServiceClient` retry did **not** apply at all. The fault came back
  as an `OrganizationServiceFault` after one ~2 s attempt. **[proven in production]**
- Microsoft says the SDK clients pause and resend after `Retry-After` on service protection errors.
  The `MaxRetryCount` and `RetryPauseTime` reference pages don't state defaults, so set both
  explicitly. **[Microsoft docs, verify]**

With NimBus owning retries:

- Prefer the Web API client from this pack. It has no retry handler; the template's
  `ArchitectureTests` enforce that.
- With `ServiceClient`, set `MaxRetryCount` to 0 or 1 (verify that 0 disables it in your version),
  or prove that `MaxRetryCount × max(RetryPauseTime, Retry-After) + call time` fits inside
  `NimBusReceiverOptions.MaxAutoLockRenewalDuration`. The default is 5 minutes in NimBus 4.2.0.
- Production raised the lock renewal to 10 minutes as the stop-gap. **[proven in production]**

## Concurrency

- **Parallelism hint.** Dataverse sends a recommended degree of parallelism in the `x-ms-dop-hint`
  response header. `ServiceClient` exposes it as `RecommendedDegreesOfParallelism`. Use it instead
  of the client's CPU count. **[Microsoft docs, verify]**
- **The production request bound.** One production adapter gates Dataverse reads per replica with
  a limiter. **[proven in production]**
  - The bound is a configured override if set; otherwise the server hint; otherwise 8.
  - The effective value and its source are logged once.
  - Service Bus sessions default to 16 per replica.
- **Affinity off.**
  - Limits apply per web server, and the affinity cookie pins a client to one server. Microsoft:
    disable it for parallel clients. **[Microsoft docs, verify]**
  - Production runs with `EnableAffinityCookie = false`. **[proven in production]**
  - For `HttpClient`, call `.WithoutAffinityCookie()` from the snippet. It sets `UseCookies = false`.
- **Sync calls on a shared `ServiceClient` serialize.** **[proven in production]**
  - Raising `MaxConcurrentSessions` from 8 to 50 gave none of the expected throughput.
  - The cause: in `Microsoft.PowerPlatform.Dataverse.Client` 1.2.10, every *synchronous* call on a
    shared instance holds a monitor across the network round trip (verified in the assembly's IL).
  - The *async* methods take no such lock.
  - `OrganizationServiceContext` LINQ exposes only the synchronous path. Use `QueryExpression` with
    `ColumnSet` and `RetrieveMultipleAsync`.
  - Analyzer `CA1849` turns a sync call in async code into a build error.
  - The throughput gain after the fix was not load-tested.
- **Microsoft's pattern** for .NET 6+ is one `ServiceClient`, its async methods and
  `Parallel.ForEachAsync`. Use `Clone()` only on .NET Framework. **[Microsoft docs, verify]**
- **Sizing.** Limits are per **user**, and every replica uses the same application user. Keep
  `MaxConcurrentSessions × replicas`, plus any reads you fan out per message, at or below the
  `x-ms-dop-hint` value.
  - Raising concurrency past the limit buys `429`s, not throughput.
  - Each throttled call holds a session lock for the length of its `Retry-After`.

## Echo-loop prevention

An echo happens like this: the adapter writes to Dataverse, the write fires the change-detection
step, and the change goes back onto the bus as if a person made it.

**Source side (before announcing a change).**

1. **Identity.** Skip when `InitiatingUserId` is the adapter's application user. **[proven in production]**
   - Production plug-ins read that `systemuserid` from an environment variable and fail if it is
     missing.
   - Another plug-in compared the row's `modifiedby` with the integration app user instead.
   - Use `InitiatingUserId`, not `UserId`, because the step's "run in user's context" setting
     replaces `UserId`.
   - With an out-of-the-box Service Endpoint there is no custom plug-in. Apply the same check in
     the ingress: `InitiatingUserId` is in the execution-context JSON. **[Microsoft docs, verify]**
2. **Relevant change.** On Update, compare the relevant columns between the **pre-image and
   post-image**: lookups by id, everything else by value. Announce only when one differs.
   **[proven in production]**
   - Filtering attributes on the step are not enough. They fire when a column is *in* the request,
     even with an unchanged value. **[Microsoft docs, verify]** (Power Automate documents this for
     its Select columns, and the NimBus ingress plan says the same of filtering attributes)
3. **`Depth`.** Several production plug-ins return early when `Depth > 1`. **[proven in production]**
   - Microsoft says not to depend on `Depth`: it changes with code paths you don't control. It is
     there for the platform's infinite-loop guard. **[Microsoft docs, verify]**
   - Use the identity check, not `Depth`, to stop echoes.

`snippets/DataversePluginEchoFilter.cs` implements checks 1 and 2 as plain C#. Plug-ins and the
ingress can share it.

**A trap.** A plug-in that writes back to the same row, for example a sync-status or published-on
stamp, triggers itself again. Exclude those columns from the step's filtering attributes and from
the relevant columns.

**Sink side (when writing).**

- **Skip no-op writes.** Read fresh, compare the owned columns, write only the difference. An
  unchanged write still fires logic. **[proven in production]**
- **Bypass custom logic** only for bulk loads (see above). It also skips the echo filter's plug-in,
  which hides the problem instead of fixing it.
- In the NimBus demo, the simulator never publishes the integration user's writes. Copy that rule
  into your simulator (see [simulator-routes.md](simulator-routes.md)).

## Gotchas

- An alternate key isn't usable until its index is **Active**. A solution import creates it
  asynchronously. **[Microsoft docs, verify]**
- Key values with `/ < > * % & : \ ? +` break key URLs. Use `$filter` for reads.
  **[Microsoft docs, verify]**
- An OData segment is limited to 260 characters, and a URL to 32 KB. **[Microsoft docs, verify]**
- `PATCH` upsert returns 204 for create *and* update. Only `Prefer: return=representation` tells
  them apart. **[Microsoft docs, verify]**
- A 412 is either a duplicate (create-only) or a version mismatch (`If-Match` with an ETag). Read
  the code. **[Microsoft docs, verify]**
- Webhooks: "Update" can arrive before "Create" for a new record. Create handlers must upsert.
  **[proven in production]**
- Inactive state on create: don't. Create, then change the state. Some tables start in Draft.
  **[proven in production]**
- Labels are localized. Compare choice values. **[proven in production]**
- Saving an entity tracked by `OrganizationServiceContext` fails ("EntityState ... Unchanged").
  **[proven in production]**
- Sync `ServiceClient` calls on a shared instance serialize. Use the async API.
  **[proven in production]**
- `ServiceClient` retry pauses can outlive the Service Bus lock. **[proven in production]**
- `ExecuteMultiple` with `ContinueOnError = true` and unread responses fails silently.
  **[proven in production]**
- `Guid.NewGuid()` MessageIds on the ingress defeat the inbox. **[proven in production]**
- A misspelled bypass parameter (`BypassPluginExecution`) was observed. Verify that bypasses take
  effect. **[proven in production]** (that it was used)
- Change-tracking tokens expire after 7 days by default. Plan a resync. **[Microsoft docs, verify]**
- Service Endpoint contexts above 192 KB are truncated (`MessageMaxSizeExceeded`); webhooks above
  256 KB. **[Microsoft docs, verify]**
- A synchronous webhook can't be recalled when the transaction rolls back.
  **[Microsoft docs, verify]**
- `BypassBusinessLogicExecution` needs `prvBypassCustomBusinessLogic`. Only System Administrator
  has it by default. **[Microsoft docs, verify]**

## Simulator

[simulator-routes.md](simulator-routes.md) lists the Web API subset a local simulator should
emulate. It also lists the `/emulator/*` driver endpoints that play a user editing a row and fire
the change notification at the adapter's ingress.

## Snippets

All snippets compile against the template, in namespace `TemplateAdapter.Adapter.Clients`. Copy
them to `src/<Name>.Adapter/Clients/`; `dotnet new` token replacement renames `ExternalSystem*`
types.

| File | What it gives you |
|---|---|
| [`snippets/DataverseWebApiClient.cs`](snippets/DataverseWebApiClient.cs) | `IExternalSystemClient` over the Web API. It reads with `$filter` + `$select` + `$expand`, upserts by alternate key with `PATCH`, binds the parent by its key, repeats a duplicate-key upsert as update-only, and includes `WithoutAffinityCookie()` for registration |
| [`snippets/DataverseErrorCodes.cs`](snippets/DataverseErrorCodes.cs) | Reads `error.code` and `error.message` and classifies them as Throttled, Transient, DuplicateKey, NotFound or Rejected, from the codes and production message fragments |
| [`snippets/DataversePluginEchoFilter.cs`](snippets/DataversePluginEchoFilter.cs) | A plain-C# source-side echo filter (integration-user identity plus a pre/post-image diff). It has no Dataverse SDK dependency, so plug-in projects and the ingress can share it |

## Sources

Microsoft Learn, read on 2026-10-01:

- Service protection API limits: https://learn.microsoft.com/power-apps/developer/data-platform/api-limits
- Send parallel requests: https://learn.microsoft.com/power-apps/developer/data-platform/send-parallel-requests
- Update and delete rows (upsert, `If-Match`, `Prefer`): https://learn.microsoft.com/power-apps/developer/data-platform/webapi/update-delete-entities-using-web-api
- Conditional operations (ETags, prevent create or update): https://learn.microsoft.com/power-apps/developer/data-platform/webapi/perform-conditional-operations-using-web-api
- Use Upsert: https://learn.microsoft.com/power-apps/developer/data-platform/use-upsert-insert-update-record
- Work with alternate keys: https://learn.microsoft.com/power-apps/developer/data-platform/define-alternate-keys-entity
- Use an alternate key to reference a record: https://learn.microsoft.com/power-apps/developer/data-platform/use-alternate-key-reference-record
- Associate and disassociate rows (`@odata.bind`): https://learn.microsoft.com/power-apps/developer/data-platform/webapi/associate-disassociate-entities-using-web-api
- Compose HTTP requests and handle errors (headers, status codes, error body, URL limits): https://learn.microsoft.com/power-apps/developer/data-platform/webapi/compose-http-requests-handle-errors
- Web service error codes: https://learn.microsoft.com/power-apps/developer/data-platform/reference/web-service-error-codes
- Filter rows (quote escaping): https://learn.microsoft.com/power-apps/developer/data-platform/webapi/query/filter-rows
- Select columns (`$select`, formatted values): https://learn.microsoft.com/power-apps/developer/data-platform/webapi/query/select-columns
- Join tables (`$expand`): https://learn.microsoft.com/power-apps/developer/data-platform/webapi/query/join-tables
- Execute batch operations: https://learn.microsoft.com/power-apps/developer/data-platform/webapi/execute-batch-operations-using-web-api
- Execute multiple requests: https://learn.microsoft.com/power-apps/developer/data-platform/org-service/execute-multiple-requests
- Bypass custom logic: https://learn.microsoft.com/power-apps/developer/data-platform/bypass-custom-business-logic
- Behaviour of specialized update operations (`SetState` deprecated): https://learn.microsoft.com/power-apps/developer/data-platform/special-update-operation-behavior
- Change tracking: https://learn.microsoft.com/power-apps/developer/data-platform/use-change-tracking-synchronize-data-external-systems
- Azure Service Bus integration (Service Endpoint, 192 KB): https://learn.microsoft.com/power-apps/developer/data-platform/azure-integration
- Webhooks (60 s, 256 KB, retry on 502/503/504): https://learn.microsoft.com/power-apps/developer/data-platform/use-webhooks
- Service Endpoint table (contracts incl. Event Grid): https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/serviceendpoint
- Power Automate row trigger: https://learn.microsoft.com/power-automate/dataverse/create-update-delete-trigger
- Execution context: https://learn.microsoft.com/power-apps/developer/data-platform/understand-the-data-context
- `IExecutionContext.Depth`: https://learn.microsoft.com/dotnet/api/microsoft.xrm.sdk.iexecutioncontext.depth
- `IExecutionContext.InitiatingUserId`: https://learn.microsoft.com/dotnet/api/microsoft.xrm.sdk.iexecutioncontext.initiatinguserid
- `ServiceClient` constructors: https://learn.microsoft.com/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.-ctor
- `ServiceClient.MaxRetryCount` / `RetryPauseTime`: https://learn.microsoft.com/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient.maxretrycount
- Application users: https://learn.microsoft.com/power-platform/admin/manage-application-users
- Table reference, `account` (`versionnumber`, `statecode`): https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/account
- Table reference, `systemuser` (`applicationid`): https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/systemuser
- `WhoAmIResponse` (simulator): https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/whoamiresponse

Production sources are three production Dataverse CE adapters on the NimBus architecture and
their plug-ins. Customer names are left out by design.
