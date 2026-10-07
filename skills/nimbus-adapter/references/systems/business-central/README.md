# Business Central system pack

**Scope:** Business Central online (SaaS), API v2.0 and custom API pages. Notes on
on-premises BC and NAV are at the end.

**Last verified:** 2026-10-01

## How to read the tags

| Tag | Meaning |
|---|---|
| **[proven in production]** | Seen in a production adapter's code, documentation or fix history; the kit authors did not re-observe it at runtime. This applies only to the on-premises NAV/BC adapters in the last section. **No production BC online adapter was in the source material.** |
| **[proven in the NimBus sample]** | Observed in the NimBus `DynamicsBcDemo` sample. Its "Business Central" is a simulator, not a real BC, so the tag proves the adapter side and says nothing about real BC behaviour. |
| **[Microsoft docs, verify]** | Taken from learn.microsoft.com (see Sources, `S1`…). Check it in a sandbox before you depend on it. |
| **[unverified]** | Neither documented nor observed. Treat it as a hypothesis and test it in a sandbox. |

Untagged text is guidance from this kit, not a claim about Business Central.

## At a glance

| Topic | Business Central online |
|---|---|
| Base URL | `https://api.businesscentral.dynamics.com/v2.0/<tenant-id>/<environment>/api/v2.0`; custom APIs use `.../api/<publisher>/<group>/<version>`. Most endpoints are company-scoped: `.../companies(<company-id>)/<entitySet>` **[Microsoft docs, verify]** (S16, S17) |
| Auth | Entra client credentials (service-to-service) with scope `https://api.businesscentral.dynamics.com/.default`. The app needs the `API.ReadWrite.All` application permission and a record on the **Microsoft Entra Applications** page in BC, with permission sets **[Microsoft docs, verify]** (S15) |
| Change detection | 1. Webhooks: thin (no field values), delayed about 30 s and collapsed per record, and expire after 3 days unless renewed **[Microsoft docs, verify]** (S1). Re-read the record after each one. 2. Delta polling on `lastModifiedDateTime`, which Microsoft gives as its own example **[Microsoft docs, verify]** (S6). 3. Events raised from an AL extension: business events, still in preview **[Microsoft docs, verify]** (S25), or an outbox table (the on-premises staging-table pattern **[proven in production]**, and the NimBus sample's outbox **[proven in the NimBus sample]**). See below for details. |
| Write API | A custom API page over the BC table, with an external-id field added by a table extension. The upsert is `GET ?$filter=externalId eq '…'`, then `POST`, or `PATCH` with `If-Match: <@odata.etag>` **[Microsoft docs, verify]** (S10, S18) |
| Throttling signal | `429` once a user exceeds 6,000 requests in a 5-minute sliding window, or 100 connections, or 95 queued requests. A request queued for 8 minutes returns `503`. Long requests return `408` or `504` **[Microsoft docs, verify]** (S5, S6) |
| Outage signal | `502` or `503` with a `retry-after` header **[Microsoft docs, verify]** (S8). Update windows default to 20:00–06:00 local time **[Microsoft docs, verify]** (S22) |
| Error body | `{"error":{"code":"…","message":"…"}}` with code families `BadRequest_*`, `Authentication_*`, `Authorization_*`, `Internal_*`, `Application_*` and `Request_EntityChanged` **[Microsoft docs, verify]** (S7, S20) |
| Echo identity | `SystemModifiedBy`, the user security ID (GUID) of the last modifier **[Microsoft docs, verify]** (S14). The standard v2.0 `customer` doesn't expose it **[Microsoft docs, verify]** (S11), so expose it on your custom API page. Webhook notifications carry no user **[Microsoft docs, verify]** (S1). |
| Batch | `$batch` takes at most 100 operations. Add `Isolation: snapshot` to make it transactional **[Microsoft docs, verify]** (S5, S20) |

## Authentication

**What BC accepts**

- **Token flow.** For integrations that run without a user, BC uses the OAuth 2.0 client credentials flow, called service-to-service (S2S). The token endpoint is `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token` and the scope is `https://api.businesscentral.dynamics.com/.default` **[Microsoft docs, verify]** (S15).
- **Entra setup.** The app registration needs the **Dynamics 365 Business Central → Application permissions → `API.ReadWrite.All`** permission, plus admin consent. `Automation.ReadWrite.All` is needed only for the automation APIs **[Microsoft docs, verify]** (S15).
- **BC setup.** In BC, open **Microsoft Entra applications** and choose **New**. Enter the Client ID, set **State = Enabled** and assign permission sets. You can't assign `SUPER` to an application **[Microsoft docs, verify]** (S15).
- **Permissions.** The integration can only do what those permission sets allow.
- **Throttling.** The operational limits apply to application users (service principals) exactly as they apply to people **[Microsoft docs, verify]** (S5).

**How the template maps to this**

| Template piece | Business Central setting |
|---|---|
| `ExternalSystem:Scope` | `https://api.businesscentral.dynamics.com/.default` |
| `ExternalSystem:BaseUrl` | The company root of your custom API page: `https://api.businesscentral.dynamics.com/v2.0/<tenant-id>/<environment>/api/<publisher>/<group>/<version>/companies(<company-id>)/`. The trailing slash is added for you. |
| `BearerTokenHandler` + `TokenCredential` | Sends a bearer token for that scope on every call. BC needs an **app-only** token for a service principal that is registered on the Entra Applications page. |

**Which credential `DefaultAzureCredential` picks.** It tries Environment, then Workload Identity, then Managed Identity, then the developer tools (Visual Studio, Azure CLI, …) **[Microsoft docs, verify]** (S26).

- **Managed identity (in Azure).** Microsoft's BC documentation describes only an app registration with a client secret (S15). It doesn't cover managed identity. **[unverified]** Community reports say a managed identity works when two things are true:
  - its service principal holds the `API.ReadWrite.All` app role on the Business Central API, assigned through Microsoft Graph;
  - its client ID is entered on the Entra Applications card.

  Test this in a sandbox before you design around it.
- **App registration (fallback).** Use an app registration with a certificate from Key Vault. `EnvironmentCredential` reads `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_CERTIFICATE_PATH` (or `AZURE_CLIENT_SECRET`) **[Microsoft docs, verify]** (S26).
- **Production.** Microsoft recommends that production code use one specific credential rather than `DefaultAzureCredential` **[Microsoft docs, verify]** (S26).
- **Locally.** `DefaultAzureCredential` falls through to your developer sign-in, which yields a *delegated* token. Whether BC accepts that token depends on your BC user. If it does, BC records the changes under **your** user, so local echo-filter tests prove nothing. Use the simulator (`Scope` empty), or a sandbox app registration through environment variables. To pin the credential, set `AZURE_TOKEN_CREDENTIALS=EnvironmentCredential` (requires Azure.Identity 1.15.0 or later) **[Microsoft docs, verify]** (S26).

**Smoke test from PowerShell.** Use a sandbox environment and never commit the secret. This follows Microsoft's client credentials sample **[Microsoft docs, verify]** (S15).

```powershell
$token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" -Body @{
    grant_type = 'client_credentials'; client_id = $clientId; client_secret = $clientSecret
    scope = 'https://api.businesscentral.dynamics.com/.default' }).access_token
Invoke-RestMethod -Headers @{ Authorization = "Bearer $token" } `
    -Uri "https://api.businesscentral.dynamics.com/v2.0/$tenantId/sandbox/api/v2.0/companies"
```

The `companies` call returns the company ids that you need for `BaseUrl` **[Microsoft docs, verify]** (S9).

**Local configuration.** Keep it in user secrets:

```powershell
dotnet user-secrets init --project src/<Name>.Adapter
dotnet user-secrets set "ExternalSystem:BaseUrl" "https://api.businesscentral.dynamics.com/v2.0/<tenant-id>/sandbox/api/<publisher>/<group>/v1.0/companies(<company-id>)/" --project src/<Name>.Adapter
dotnet user-secrets set "ExternalSystem:Scope" "https://api.businesscentral.dynamics.com/.default" --project src/<Name>.Adapter
```

## Change detection (BC → bus)

The options are ranked. Whichever you choose, the event you publish carries a deterministic MessageId, and every subscriber uses `UseInbox`. In NimBus 4.2.0 you pass the MessageId with `IPublisherClient.Publish(@event, sessionId, correlationId, messageId)`.

### 1. Webhooks: a thin notification, a re-read, a deterministic MessageId

**Flow**

1. BC posts to your ingress. Host the ingress in an HTTP app (a minimal API or an Azure Function), not in the adapter worker.
2. The ingress checks `clientState` and queues the raw notification durably. It answers `200` straight away.
3. A worker re-reads the record.
4. The worker drops echoes: records whose `systemModifiedBy` is the integration's own user.
5. It maps the record and publishes with `MessageIdFor(entitySet, id, lastModifiedDateTime)`.

The `BusinessCentralWebhookNotification.cs` snippet provides the DTOs, the handshake, the `clientState` check, resource parsing, a safe re-read URL and the message ids.

**Facts that shape the design**

- **Subscribing.** `POST <api-root>/subscriptions` with `notificationUrl`, `resource` (a company-scoped entity set) and an optional `clientState` **[Microsoft docs, verify]** (S1, S2).
  - The subscriptions endpoint is **not** company-scoped.
  - For a custom API page, both the subscriptions URL and the resource must include `<publisher>/<group>/<version>` **[Microsoft docs, verify]** (S1).
- **Handshake.** BC calls the `notificationUrl` with `validationToken` on the query string. You must answer `200` with the token in the body. The handshake is mandatory both when you create a subscription and when you renew it **[Microsoft docs, verify]** (S1).
  - The HTTP method of the validation call and its time limit are not documented **[unverified]**. Accept the token on any method.
- **Expiry and renewal.** Subscriptions expire after **three days** unless renewed. You renew with `PATCH subscriptions(<id>)`, which requires `If-Match` and triggers the handshake again **[Microsoft docs, verify]** (S1, S3).
- **No field values.** A notification carries only `subscriptionId`, `clientState`, `expirationDateTime`, `resource`, `changeType` (`created`, `updated`, `deleted`, `collection`) and `lastModifiedDateTime` **[Microsoft docs, verify]** (S1).
  - A single post can carry notifications from several subscriptions **[Microsoft docs, verify]** (S1). Check `clientState` per item.
- **Delay and collapsing.** BC waits **30 seconds** after the first change to a record and sends one notification for it. If more than **1,000** records change within that window, BC sends a single `collection` notification instead, whose resource carries a `$filter` **[Microsoft docs, verify]** (S1).
  - Re-read a collection page by page, following the continuation links **[Microsoft docs, verify]** (S6).
- **Retries and deletion.** If your endpoint answers `408`, `429` or `5xx`, BC retries for 36 hours. **Any other status deletes the subscription** **[Microsoft docs, verify]** (S1).
  - So an ingress never answers `4xx` to BC. It answers `503` when it can't enqueue.
  - Whether every `2xx` counts as delivered isn't stated **[unverified]**. Answer `200`.
- **Supported entities.**
  - The standard list includes `customers`, `vendors`, `items`, `salesQuotes`, `salesOrders`, `salesInvoices` and `employees`. It does **not** include `contacts` **[Microsoft docs, verify]** (S1).
  - Custom API pages are webhook-enabled, except pages with a temporary source table, a composite key or a system table, query APIs, and Job Queue Entry **[Microsoft docs, verify]** (S1).
  - A change to a document line notifies the header **[Microsoft docs, verify]** (S1).
- **Limits.** At most 200 webhook subscriptions per environment **[Microsoft docs, verify]** (S5).
- **When notifications are held back.**
  - Changes by a delegated admin are not notified straight away **[Microsoft docs, verify]** (S1).
  - Changes by a user who can't schedule job queues are not notified until a user who can makes another change to the same table **[Microsoft docs, verify]** (S1).
  - S2S integration sessions can create scheduled tasks from version 21.2 **[Microsoft docs, verify]** (S15). Whether that makes every S2S writer's changes notify straight away is **[unverified]**.
- **Environment copies.** A copied environment has **all webhooks disabled** and its job queue tasks stopped **[Microsoft docs, verify]** (S24).

**Renewal job.** Run it on a timer several times a day, not once every 3 days.

1. `GET subscriptions` and renew each one that `NeedsRenewal(expirationDateTime, now, margin: 1 day)` returns true for.
2. Re-create (`POST`) any subscription that is missing. BC may have deleted it, or it was lost in an environment copy.
3. Alert on failure.
4. Renewal needs the ingress up, because it triggers a handshake. Check the ingress's health before you renew.

**Deletes.** A `deleted` notification can't be re-read. Publish a deactivation event straight from the notification, with `NotificationId(...)` as its MessageId.

**Not proven anywhere yet.** The NimBus sample doesn't use webhooks. Its simulator publishes through an outbox (see option 3), so everything above is documentation only.

### 2. Delta polling on `lastModifiedDateTime` with a persisted watermark

- **The query.** `GET .../customers?$filter=lastModifiedDateTime gt <watermark>` is Microsoft's own example of reducing calls **[Microsoft docs, verify]** (S6).
  - `lastModifiedDateTime` is read-only and set by BC **[Microsoft docs, verify]** (S11).
  - Results come in pages that you must follow with continuation tokens **[Microsoft docs, verify]** (S6).
- **The watermark.**
  - Store it in the integration database, never in BC.
  - Advance it to the highest `lastModifiedDateTime` you have *published*, never to the adapter's clock.
  - Re-read with a small overlap (`ge watermark - a few seconds`). The deterministic MessageId (`id` + `lastModifiedDateTime`) removes the duplicates.
- **Deletes are invisible to polling.** Combine polling with `deleted` webhooks, or with a tombstone table in your AL extension.
- **Cost.** Every poll counts towards the per-user rate limit (6,000 requests per 5 minutes) **[Microsoft docs, verify]** (S5).
- **Row-version polling.** BC keeps a row version (`SystemRowVersion`, the SQL `timestamp`) that is meant for "synchronizing data changes" **[Microsoft docs, verify]** (S14). Exposing it on an API page and filtering on it is **[unverified]**.
- **Indexes.** `SystemModifiedAt` is not indexed automatically **[Microsoft docs, verify]** (S14). Filtering on it in a custom page can be slow without a key.

### 3. Events from an AL extension

- **External business events (preview).**
  - An AL method with `[ExternalBusinessEvent]` raises the event **[Microsoft docs, verify]** (S25).
  - You subscribe through `POST api/microsoft/runtime/v1.0/externaleventsubscriptions` with `notificationUrl` and `clientState`. The subscriber needs the **Ext. Events – Subscr** permission set **[Microsoft docs, verify]** (S25).
  - Notifications are sent **only after the transaction commits**. They carry the payload and `initiatingUserAADObjectId`, which is your echo identity **[Microsoft docs, verify]** (S25).
  - Expiry and retry behaviour are not documented **[unverified]**.
  - It's still in **preview**.
- **Outbox table in AL.**
  - Event subscribers write a row to your own table in the same transaction as the business change.
  - A custom API page exposes the table. The adapter reads the rows in order, publishes them, then deletes or marks them.
  - This is the on-premises staging-table pattern, moved into AL. See "On-premises" below for the collapse-and-cutoff details **[proven in production]** (for the on-prem variant).
  - The NimBus sample's simulator keeps the business write and its events atomic with an outbox. It publishes once per event, in order, to keep the order within a session **[proven in the NimBus sample]**.
- **Avoid calling out from AL with `HttpClient`.** Each extension blocks outbound HTTP calls by default (**Allow HttpClient Requests**) **[Microsoft docs, verify]** (S8). A call-out also isn't part of the database transaction.

### Initial sync

1. Page through every row in parent order: customers first, then contacts.
2. Publish the same events as live changes, with the same deterministic MessageIds, so you can run the sync again.

The NimBus sample triggers its go-live load (item categories, customers, contacts) from an endpoint that publishes everything through the outbox in one transaction. Its comment notes this would be a one-off job in production, and that downstream upserts make re-running it safe **[proven in the NimBus sample]**. The production on-prem adapter skipped a contact whose account hadn't been processed yet and retried it on the next run **[proven in production]**.

## Writing (bus → BC)

### Standard API or a custom API page

- **Standard API v2.0.** It is company-scoped CRUD, with entities such as `customers`, `contacts` and `salesQuotes` **[Microsoft docs, verify]** (S11–S13).
  - It has no field for the upstream id, and `customer` doesn't expose `systemModifiedBy` **[Microsoft docs, verify]** (S11).
  - `salesQuote` is customer-based. It has `externalDocumentNumber` and the bound actions `send`, `makeOrder` and `makeInvoice` **[Microsoft docs, verify]** (S13).
  - Use it for reference reads.
  - The NimBus sample reads `customers(<id>)` and `customers(<id>)/customerFinancialDetail` through the v2.0 routes and writes through a custom API **[proven in the NimBus sample]**.
- **Custom API page.** Use it to write, and to read anything keyed on the upstream id.
  - Required properties: `PageType = API`, `APIPublisher`, `APIGroup`, `APIVersion`, `EntityName`, `EntitySetName`, `ODataKeyFields = SystemId` and `DelayedInsert = true` **[Microsoft docs, verify]** (S18).
  - The URL is `.../api/<publisher>/<group>/<version>/companies(<id>)/<entitySetName>` **[Microsoft docs, verify]** (S17).
  - Responses include a weak `@odata.etag` (`W/"…"`) **[Microsoft docs, verify]** (S18).
- **Cross-references stored in BC.**
  - Add the upstream id (and the parent's upstream id) to the BC table with a **table extension**, and expose those fields on the page.
  - Store BC's `SystemId` in the upstream system, either through an ID write-back event or by reading it on the next sync.
  - The NimBus sample keeps the reference in both systems: the CRM account stores the BC customer id, and BC stores the CRM account id **[proven in the NimBus sample]**.
- **You can't enforce uniqueness from a table extension.** The `Unique` key property cannot be used in table extensions **[Microsoft docs, verify]** (S19).
  - The snippet rejects duplicate external ids.
  - Prevent duplicates in AL: check in `OnInsertRecord`, or keep the cross-reference in your own table, where `Unique = true` is allowed.

An AL sketch of the page. **It hasn't been compiled: verify it in a sandbox.**

```al
tableextension 50100 "Integration Customer Ext" extends Customer
{
    fields
    {
        field(50100; "External Id"; Text[100]) { DataClassification = SystemMetadata; }
        field(50101; "Parent External Id"; Text[100]) { DataClassification = SystemMetadata; }
    }
}

page 50100 "Integration Customer API"
{
    PageType = API;
    APIPublisher = 'myPublisher';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'customer';
    EntitySetName = 'customers';
    SourceTable = Customer;
    ODataKeyFields = SystemId;
    DelayedInsert = true;

    layout
    {
        area(Content)
        {
            repeater(Records)
            {
                field(id; Rec.SystemId) { Editable = false; }
                field(number; Rec."No.") { Editable = false; }
                field(displayName; Rec.Name) { }
                field(country; Rec."Country/Region Code") { }
                field(email; Rec."E-Mail") { }
                field(externalId; Rec."External Id") { }
                field(parentExternalId; Rec."Parent External Id") { }
                field(ownedByBusinessCentral; OwnedByBusinessCentral) { Editable = false; }
                field(systemModifiedBy; Rec.SystemModifiedBy) { Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Editable = false; }
            }
        }
    }

    var
        OwnedByBusinessCentral: Boolean; // set in OnAfterGetRecord from your ownership rule
}
```

### The upsert pattern (`BusinessCentralApiClient.cs`)

1. **Read.** `GET customers?$filter=externalId eq '<key>'`, with a single quote in the key doubled. Then decide:
   - **Zero rows** in a well-formed `{"value": []}`: the customer doesn't exist yet.
   - **Two or more rows:** `Rejected`, with the code `DuplicateExternalId`.
   - **A non-success status:** classified as a failure. A `404` on the collection means a wrong route, a wrong company or an uninstalled extension. It must never be read as "no customer", because that leads to a duplicate POST.
   - **A `200` that isn't that collection** (`{}`, `null`, no `value` array, a row without an `id`, an HTML page from a proxy): `Unavailable`, never "no customer", for the same reason. The external id isn't unique in BC, so nothing would stop the duplicate.
2. **Missing:** `POST customers`. Null fields are left out so that BC applies its defaults.
3. **`ownedByBusinessCentral` is set:** `UpsertResult.OwnedByExternalSystem`, and nothing is written.
4. **Nothing changed:** no write at all.
   - The template's `UpstreamAccountUpdatedHandler` checks this first: it calls `GetAccountAsync` and skips the upsert when the result equals the mapped record. A redelivery or a resubmit costs one GET. Because nothing is written, BC raises no change and no echo.
   - That check only works if `GetAccountAsync` returns the fields exactly as `AccountMapper.ToExternalSystem` produces them. The snippet's `ToAccount` trims the name, trims and upper-cases the country, trims the e-mail, turns BC's `""` into null, and reads the parent from `parentExternalId`. Change it together with the mapper.
   - The client compares again before it writes (same normalization), so a caller that skips the handler's check still writes nothing; the result is then `Updated`.
5. **Changed:** `PATCH customers(<id>)` with only the changed fields, and with `If-Match` set to the row's `@odata.etag`.
   - `If-Match` is required on PATCH **[Microsoft docs, verify]** (S10).
   - Microsoft advises not to send unchanged values **[Microsoft docs, verify]** (S10).
   - BC returns empty text fields as `""` **[Microsoft docs, verify]** (S10 example), so the snippet treats null and `""` as equal and sends `""` to clear a field.
6. **Stale read:** `412`, `409`, or `404` because the row was deleted in between. The client re-reads and retries **once**. A second conflict is `Rejected`: the session blocks and an operator resubmits.
   - If conflicts are routine for you (for example, BC users edit the same customers all day), add an `ExternalSystemConflictException` with its own retry rule and exclude it from the breaker.

**ETags and `If-Match`**

- **On a mismatch:** BC "won't update" the record when the ETag doesn't match **[Microsoft docs, verify]** (S10).
  - Microsoft documents `409 Conflict` as "another user has already changed the record(s)", and `Request_EntityChanged` with the same text **[Microsoft docs, verify]** (S7, S8).
  - Whether a stale `If-Match` returns `409` or `412` isn't stated **[unverified]**, so the snippet handles both.
- **When the header is missing:** the error is likely `BadRequest_InvalidToken` ("Could not validate the client concurrency token…"). The code is documented (S7); that a missing header triggers it is **[unverified]**.
- **`If-Match: *`:** Microsoft's own samples use it **[Microsoft docs, verify]** (S15, S20). It skips the concurrency check. Then a field changed in BC between your read and your write is overwritten, and your ownership and no-op decisions were made on stale data. The snippet uses the real ETag, and `*` only when the page returns none.

**Why a retried POST is safe**

- NimBus processes messages for one key in order, because they share a session.
- Every attempt starts with a read.
- So when a POST timed out after BC had committed it, the next attempt finds the row and updates it instead of creating a duplicate.
- If you send `id` on the POST, BC persists that value as the `SystemId` **[Microsoft docs, verify]** (S14). A retried POST of the same id should then fail with `Internal_EntityWithSameKeyExists`, which you can treat as success after a re-read. The status code is **[unverified]**.

### `$batch`

- **Limit:** at most **100** operations per `$batch` **[Microsoft docs, verify]** (S5).
- **Transactions:** `Isolation: snapshot` makes the batch transactional. Without it, each inner request succeeds or fails on its own, and the batch reports each inner response's status **[Microsoft docs, verify]** (S20).
- **`If-Match`:** inner PATCH requests carry their own `If-Match` **[Microsoft docs, verify]** (S20).
- **Rate limits:** whether each inner request counts against the rate limit is **[unverified]**.
- **When to use it:** for the initial sync and for bulk loads. One NimBus message normally means one record and one upsert, which keeps retries and operator resubmits per record.

### Routing by company and environment

- **One environment per country.** An environment's country determines its localization and the Azure region it runs in. You can create extra production environments for offices in other countries **[Microsoft docs, verify]** (S23).
- **Companies.** An environment holds at most 300 companies **[Microsoft docs, verify]** (S5). The company goes in the URL, as `companies(<id>)`, or in `?company=<id>` (S17).
- **In the adapter:**
  - Put the environment and the company in `BaseUrl`, giving one configured client per company.
  - If one adapter serves several countries, map a field on the event (for example the country or the legal entity) to a named client.
  - Use one integration identity per environment, and register it in every company it writes to.

### Data rules

- **Field lengths.** Text that is too long is rejected with `Application_StringExceededLength` **[Microsoft docs, verify]** (S7). Validate or truncate in the mapper against the lengths in your AL table.
- **Error language.** Error messages are localized, and `Accept-Language` sets the language of the response **[Microsoft docs, verify]** (S9). Classify on `error.code`, never on the message.
- **Country codes.** BC's Country/Region codes are the tenant's own setup data. They are ISO codes in most setups, but that isn't guaranteed **[unverified]**.

## Errors and throttling

The template's `HttpFailure` classifies failures by HTTP status. This table adds what BC's codes mean. The error-code families are **[Microsoft docs, verify]** (S7). Microsoft doesn't publish which status goes with each code, so where the status is marked unverified, capture real responses from a sandbox.

| Status | Typical BC code | Template type | Notes |
|---|---|---|---|
| 429 | rate limit | **Throttled** | Returned when a user exceeds 6,000 requests per 5-minute window, 100 simultaneous connections or 95 queued requests **[Microsoft docs, verify]** (S5). Microsoft prescribes retry with a cool-off **[Microsoft docs, verify]** (S6). A `Retry-After` on 429 isn't documented **[unverified]**: the template uses it when present. |
| 503 | maintenance or overload | **Unavailable** | BC adds `retry-after` **[Microsoft docs, verify]** (S8). Also returned when a request waited 8 minutes in the per-user queue **[Microsoft docs, verify]** (S5). |
| 502 | data plane unavailable | **Unavailable** | BC adds `retry-after` **[Microsoft docs, verify]** (S8). |
| 504 | request ran longer than 10 min | **Unavailable** | **[Microsoft docs, verify]** (S6). A POST that timed out may have committed. The client reads before it writes again. |
| 408 | operation timeout (8 min) | **Unavailable** | **[Microsoft docs, verify]** (S5). The two Microsoft pages disagree: 8 minutes and `408` (S5) against 10 minutes and `504` (S6). Treat both as Unavailable. |
| 400 | `BadRequest_*`, `BadRequest_RequiredParamNotProvided`, `BadRequest_InvalidToken` | **Rejected** | Fix the request **[Microsoft docs, verify]** (S7). |
| 401 | `Authentication_InvalidCredentials` | **Rejected** | The app isn't registered or enabled on the Entra Applications card, or the scope or token is wrong **[Microsoft docs, verify]** (S7, S15). |
| 403 | `Authorization_*` | **Rejected** | Permission sets on the Entra Applications card **[Microsoft docs, verify]** (S7). |
| 404 on a collection | `BadRequest_ResourceNotFound`, `Internal_CompanyNotFound` | **Rejected** | Wrong route, company or API version, or the extension isn't installed **[Microsoft docs, verify]** (S7, S8). Never read it as "not found". |
| 404 on `(<id>)` | `Internal_RecordNotFound` | re-read once, then **Rejected** | The row was deleted between the read and the write. The status is **[unverified]**. |
| 409 | `Request_EntityChanged` | re-read and retry once, then **Rejected** | "Another user has already changed the record" **[Microsoft docs, verify]** (S7, S8). **In BC, a 409 means a concurrency conflict, not "owned by BC"**, so don't reuse the template sample client's `409 → OwnedByExternalSystem` mapping. |
| 412 | ETag precondition | re-read and retry once, then **Rejected** | That BC uses 412 for `If-Match` is **[unverified]**. Microsoft lists 412 for missing required headers (S8). |
| 413 | body larger than 350 MB, page larger than 20,000 rows | **Rejected** | **[Microsoft docs, verify]** (S5, S8) |
| status **[unverified]** | `Application_*` (`DialogException`, `FieldValidationException`, `StringExceededLength`, `EvaluateException`, …) | **Rejected** | Business-logic errors: fix the data and resubmit **[Microsoft docs, verify]** (S7). The NimBus sample's simulator answers them with 422, 400 or 409. That is the simulator's choice, not BC's **[proven in the NimBus sample]**. |
| status **[unverified]** | `Internal_InvalidTableRelation`, `Internal_DataNotFoundFilter` | **Rejected** or `MissingReferenceException` | A referenced record is missing **[Microsoft docs, verify]** (S7). If BC answers these with 5xx, map the codes to `Rejected` yourself: otherwise they count against the breaker. |
| status **[unverified]** | `Internal_EntityWithSameKeyExists` | treat as success after a re-read | A duplicate key, for example from a retried POST with an explicit `id` **[Microsoft docs, verify]** (S7, S14). |
| status **[unverified]** | `Internal_ServerError`, `Internal_TenantUnavailable` | **Unavailable** | BC can't reach its database, or the tenant is unavailable **[Microsoft docs, verify]** (S7). |
| no response or timeout | (none) | **Unavailable** | `HttpFailure.SendAsync` handles it. |

**What the failure text contains**

- NimBus stores the exception message verbatim on the failed message and doesn't mask it. BC's `error.message` can echo field values, so no failure text from the snippet contains a response body.
- The text names the operation (with the external id), the method, the path without its query string, the status, and BC's `error.code`. The template's `HttpFailure.SanitizeErrorCode` keeps the code only if it looks like one: letters, digits and `_ . -`, at most 64 characters. So `Application_DialogException` and similar codes reach the operator, while free text is dropped.
- A malformed `200` on the lookup reads "… → 200 OK. The response is not the OData collection the adapter expects".

**Retry-After**

- The template keeps the hint on `ExternalSystemThrottledException.RetryAfter` for diagnostics.
- The retry delay itself comes from the policy (`ThrottledBaseDelaySeconds`, doubling up to `ThrottledMaxDelaySeconds`).
- The NimBus sample keeps the same split: the client reads `Retry-After`, and broker-scheduled retries do the waiting **[proven in the NimBus sample]**.

**Update windows and the circuit breaker**

- **What BC does.**
  - Each environment has an update window: by default 20:00–06:00 local time for its country, and at least six hours long **[Microsoft docs, verify]** (S22).
  - An update starts at some point inside the window, and the environment can stay unreachable until the window ends **[Microsoft docs, verify]** (S22).
  - An update that can't finish is cancelled at the end of the window and rescheduled seven days later **[Microsoft docs, verify]** (S22).
  - Users get "Service is under maintenance" **[Microsoft docs, verify]** (S22).
  - `503` is documented for "down for maintenance" **[Microsoft docs, verify]** (S8). That the API returns `503` throughout an update is **[unverified]**.
- **The template's retry budget.** The default Unavailable policy (30 s, doubling, capped at 600 s, 8 retries) gives up after about **45 minutes**. That doesn't cover a window that can last hours. Messages still failing at the end block their sessions, waiting for an operator.
- **Options:**
  - Agree a quiet update window with the BC admin.
  - Raise `UnavailableMaxRetries` and `UnavailableMaxDelaySeconds` for the BC adapter. For example, 16 retries with a 1,800-second cap covers about 6 hours.
  - Accept operator resubmits after an overrun window.
- **The breaker.** The circuit breaker pauses the receivers once outages dominate, so a window doesn't burn delivery attempts. That needs the worker host; an Azure Function can't pause its receivers.
- **The sample's evidence.** In the sample's simulator, a 20-second "update window" scene opens the circuit, probes, and closes it with nobody acting. The first retry delay was set longer than the window **[proven in the NimBus sample]**. Production timings must be minutes, not seconds.

## Concurrency

- **BC's per-user limits.** BC processes **5** OData requests per user at a time. More requests queue, and a request that waits **8 minutes** gets `503`. Above 100 connections, or 95 queued requests, BC answers `429` **[Microsoft docs, verify]** (S5).
- **What that means for the adapter.**
  - Each message makes 1–4 sequential calls: the handler's GET (plus one for a parent), then, only when something changed, the client's fresh GET and a POST or PATCH.
  - So the requests in flight to BC roughly equal `MaxConcurrentSessions` × replicas.
  - Keep that close to **5 per integration identity**. With the template default of `MaxConcurrentSessions = 8`, set it to 4 or 5 for a single replica, and lower it when KEDA scales out.
  - Requests above 5 only queue inside BC. A request that queues longer than the client timeout (`TimeoutSeconds`, 30 s by default) is cancelled on our side and classified as Unavailable, which feeds the breaker.
  - Whether BC then still executes the cancelled request is **[unverified]**. That's another reason to read before every write.
- **Rate.** 6,000 requests per 5 minutes is about **20 requests per second** sustained per user **[Microsoft docs, verify]** (S5). A change costs 3 requests (GET, GET, write), a no-op costs 1, and a parent check adds 1.
- **More throughput.** You can spread the load over several service principals **[Microsoft docs, verify]** (S5). Every one of them is then an echo identity.
- **The sample's numbers aren't evidence.** The NimBus sample runs `MaxConcurrentSessions = 16` against its simulator, which has no concurrency limit **[proven in the NimBus sample]**.

## Echo-loop prevention

- **Source side (BC → bus).**
  - After the re-read, compare `systemModifiedBy` with the integration's user security IDs, and don't publish a match (`BusinessCentralWebhook.IsEcho`).
  - `SystemModifiedBy` is the security ID of the user who last modified the record. BC sets it automatically on every insert and modify **[Microsoft docs, verify]** (S14).
  - Expose it on your custom API page, because the standard v2.0 customer doesn't **[Microsoft docs, verify]** (S11).
  - That S2S writes are recorded under a distinct user ID for the app is **[unverified]**. Find the value once: write a test record in a sandbox with the integration identity, read `systemModifiedBy` back, and put it in configuration.
- **Business events.** They identify the initiator by `initiatingUserAADObjectId`, an Entra **object id**, not a BC security ID **[Microsoft docs, verify]** (S25).
- **The trap in last-writer checks.**
  - Webhooks collapse changes for 30 seconds **[Microsoft docs, verify]** (S1).
  - When a user edits a record and the integration writes it within that window, the re-read shows the integration as the last writer, and the user's change is dropped.
  - To catch this, also compare the re-read values with the values you last wrote (a hash per record in the integration database), and publish when fields you don't write have changed.
- **Sink side (bus → BC).** Skip no-op writes, as the template's handler and the snippet do (see step 4 of the upsert pattern). An unchanged record isn't PATCHed, so BC sees no change and sends no notification.

## Gotchas

- **Writes**
  - **A 409 means a concurrency conflict, not ownership.** Signal ownership with a field on your API page (`ownedByBusinessCentral`), not with a status code **[Microsoft docs, verify]** (S7, S8).
    - The NimBus sample's simulator refuses BC-owned prospects with `409` and the code `Application_OwnedByBusinessCentral`, and its client's prospect upsert maps any `409` to "owned by BC" **[proven in the NimBus sample]**.
    - Against real BC, that mapping would turn a concurrency conflict into a silently skipped change, so don't copy it.
  - **A GET 404 on a collection is a configuration error, not "missing".** Swallowing it leads to a duplicate POST.
  - **Neither is a `200` without a `value` array.** `page?.Value ?? []` turns `{}` or `null` into "no match" and POSTs a duplicate. The snippet throws `Unavailable` instead.
  - **You can't make an external-id field unique from a table extension** **[Microsoft docs, verify]** (S19). Expect duplicates unless your AL prevents them.
  - **`$filter` can't use OR across two different fields.** For example, `externalId eq 'x' or number eq 'y'` fails **[Microsoft docs, verify]** (S21).
  - **The standard v2.0 `customer` has neither an external-id field nor `systemModifiedBy`** **[Microsoft docs, verify]** (S11). Plan the AL extension from day one.
- **Webhooks**
  - **An ingress that answers 4xx kills the subscription** **[Microsoft docs, verify]** (S1). That includes a 401 for a wrong `clientState` and a 400 for a body it can't parse. Answer 200 and drop the item, or 503 to make BC retry.
  - **Subscriptions die silently after 3 days** without renewal **[Microsoft docs, verify]** (S1). An environment copy disables all webhooks **[Microsoft docs, verify]** (S24). Monitor the expiry of every subscription.
  - **`contacts` is not in the webhook list** **[Microsoft docs, verify]** (S1). Poll it, or expose your own webhook-enabled API page.
- **Environments and updates**
  - **Sandbox and production limits differ.** The old per-environment rates were 300 per minute for a sandbox and 600 for production **[Microsoft docs, verify]** (S5). Load-test on a production-type environment **[Microsoft docs, verify]** (S23).
  - **An update that overruns its window is rescheduled seven days later** **[Microsoft docs, verify]** (S22). The same update window then hits again.
- **Error texts**
  - **Classify on `error.code`, never on the text.** Messages are localized **[Microsoft docs, verify]** (S9), and can end with a `CorrelationId` suffix **[Microsoft docs, verify]** (S20).
  - **The NimBus sample's simulator invents error codes.** Codes like `ServiceUnavailable_UpdateWindow`, `Application_TooManyRequests` and `Application_OwnedByBusinessCentral` don't come from real BC **[proven in the NimBus sample]**. Never match on them.

## On-premises Business Central and NAV

**On-premises APIs**

- The APIs are disabled by default and must be enabled. Authentication uses a user name plus a web service access key, which requires `NavUserPassword` or `AccessControlService` **[Microsoft docs, verify]** (S16).
- S2S on-premises needs version 18.11 or 19.5 or later, Entra authentication with OpenID Connect, and `ValidAudiences` that includes `https://api.businesscentral.dynamics.com` **[Microsoft docs, verify]** (S15).
- On-premises webhooks are configurable through `ApiSubscriptionExpiration`, `ApiSubscriptionMaxNumberOfSubscriptions`, `ApiSubscriptionDelayTime` and `ApiSubscriptionMaxNumberOfNotifications` **[Microsoft docs, verify]** (S1).

**SQL staging tables.** This pattern was observed in a production NAV adapter, where NAV and the integration met in a separate integration database.

- **The table shape.** One staging table per entity, with these columns **[proven in production]**:
  - an identity `Id`;
  - `Action` (`INSERT`, `UPDATE` or `DELETE`);
  - `Status` (`0` = unprocessed);
  - `CreatedDate` and `ProcessedDate`;
  - the entity's GUID;
  - a company or business-unit column.

  An index on `(Status, ProcessedDate, …)` serves the polling query.
- **NAV → bus: the collapse with a cutoff** **[proven in production]**:
  1. A timer (every minute by default) reads unprocessed rows.
  2. It collapses them to the newest row per entity: `ROW_NUMBER() OVER (PARTITION BY <entity key> ORDER BY CreatedDate DESC, Id DESC) = 1`.
  3. It takes `TOP (1000)` of those, ordered by `CreatedDate ASC`.
  4. It maps `Action` to a Created, Updated or Deactivated event and publishes it.
  5. It marks as processed **every** unprocessed row of that entity whose `CreatedDate` is before a cutoff. The cutoff is taken right after the read, so rows written during the run are picked up by the next run.
- **Parent first.** A contact whose account has no processed row yet is skipped and retried on the next run **[proven in production]**.
- **The cutoff clock.** The production adapter took the cutoff from its own UTC clock. In its schema scripts, `CreatedDate` defaulted to `GETDATE()` on some tables and `GETUTCDATE()` on others **[proven in production]**.
  - When the database's local time differs from UTC, the cutoff marks rows wrongly. Depending on the offset, it either republishes rows or marks rows that were never published.
  - Take the cutoff from the clock that stamps the rows (`SYSUTCDATETIME()` in the read query), or cut off on the highest `Id` you read.
- **Bus → NAV.** The handler *inserts* a staging row, append-only, for the NAV side to pick up **[proven in production]**.
- **Code pages.** The legacy NAV database kept text in an OEM code page. In production that was CP850, with CP852 for one Central European business unit. The adapter transcoded per company on both read and write **[proven in production]**. Find out each company's code page before go-live, or non-ASCII names come out garbled.
- **Column widths.**
  - The staging columns were narrow fixed-width `VARCHAR`s: 10 characters for codes, 30 for names and phone numbers, 80 for e-mail addresses. The mapper truncated values to fixed limits **[proven in production]**.
  - Some mapper limits were wider than the columns in the repository's schema script: 100 for a contact name and 50 for a job title, against columns of 30. Such a value fails with a SQL truncation error instead of being cut **[proven in production]**.
  - Keep each width in one place, and test the mapper against the real schema.
- **SQL error classification** **[proven in production]**:
  - Transient (retry): deadlock `1205`, timeout `-2`, and Azure SQL `40613`, `40197`, `40501`, `40143`, `49918`, `49919` and `49920`.
  - Permanent, with an operator-readable summary: duplicate key `2601`/`2627`, foreign key `547`, truncation `8152`/`2628`, and permission `229`/`230`.
  - In a NimBus adapter, map the first group to `Unavailable` and the second to `Rejected`.

**File-based exchange**

This was observed in a production adapter for on-premises NAV/BC **[proven in production]**.

- **ERP → bus.**
  1. The on-premises side drops XML files into blob storage at `<container>/<business-unit>/<event-type>/incoming/<file>`.
  2. A blob trigger parses the file, maps it and publishes the event.
  3. It then moves the file to `archives`: a copy, waiting for it to complete, then a delete of the source.
  4. An empty file is archived without publishing.
- **Large events.** An event whose JSON is 200,000 characters or more goes out as a claim check: the body goes to blob storage and a small reference event is published. That keeps it under the Service Bus message size limit.
- **Bus → ERP.**
  - The handler renders an XML file and uploads it to `<folder>/incoming/` for an on-premises job to import.
  - Note texts for NAV/BC were split into 250-character chunks.
- **Migration.** During a migration, the handler wrote both the old path (an HTTP workflow that writes to a file share) and the new one (blob). It failed only when both failed.
- **Fix these when you port the pattern:**
  - **The old path was fire-and-forget (`async void`), so its failures were never seen.** Alert on each path separately during a dual write.
  - **File names were built from a second-resolution local timestamp plus the session id, and uploaded with overwrite.** Two changes to one record within a second can therefore overwrite each other. Put the MessageId in the file name and never overwrite.

## Simulator

The local simulator should emulate the routes listed in [simulator-routes.md](simulator-routes.md). That includes:

- company-scoped routes, `$filter` on the external id, and `If-Match` on PATCH;
- errors in BC's shape, `429` and `503` with `retry-after`;
- the webhook handshake and delayed, collapsed notifications;
- `/emulator/*` driver endpoints.

It follows the template simulator's conventions (`/emulator/faults`, `/emulator/.../own`).

## Snippets

Both files compile into a generated adapter's `Clients/` folder (namespace `TemplateAdapter.Adapter.Clients`, `ExternalSystem*` names). They were checked with a Release build against this template and with scripted HTTP tests.

| File | What it gives you |
|---|---|
| [snippets/BusinessCentralApiClient.cs](snippets/BusinessCentralApiClient.cs) | `IExternalSystemClient` for BC. It maps accounts to customers on a custom API page, keyed on `externalId`. It finds rows with `GET ?$filter`, creates with `POST`, and updates with `PATCH` and `If-Match: <@odata.etag>`, sending changed fields only. `GetAccountAsync` returns the mapper's shape, so the template's handler can skip no-op writes, and the client skips them too. It re-reads and retries once after a 409, 412 or 404, rejects duplicate external ids, treats a `200` without a `value` array as `Unavailable` (never "not found"), keeps response bodies out of failure texts, and turns the ownership flag into `OwnedByExternalSystem`. `CustomersRoute` and `ExternalIdField` are the constants you change. |
| [snippets/BusinessCentralWebhookNotification.cs](snippets/BusinessCentralWebhookNotification.cs) | Framework-free webhook ingress helpers: the notification DTOs, `TryGetValidationToken` (the handshake), `TryParse`, `Authentic` (a constant-time `clientState` check per item), `TryParseResource` and `TryResolveResourceUri` (a re-read URL that refuses hosts and paths outside your environment), `NotificationId` and `MessageIdFor` (deterministic ids), `IsEcho` and `NeedsRenewal`. A minimal API usage sketch is in the file header. |

**Wiring the client.** In `ExternalSystemClientRegistration`, change the typed client to `AddHttpClient<IExternalSystemClient, BusinessCentralApiClient>(BusinessCentralApiClient.HttpClientName, …)`. Keep the `BearerTokenHandler` line, then set `BaseUrl` and `Scope` as described under Authentication.

**Tests to write.** Use a scripted `HttpMessageHandler` or the simulator to cover:

- not found → POST;
- a `200` of `{}`, `null` or a row without `id` → `Unavailable`, and no POST;
- `GetAccountAsync` after an upsert equals `AccountMapper.ToExternalSystem` of the same message (messy whitespace, a lower-case country, a blank e-mail), so the handler writes once for a message delivered twice;
- unchanged → no write;
- changed → PATCH with only the changed fields and the row's ETag;
- 412 → re-read and a second PATCH with the new ETag;
- a second 409 → `Rejected` with the code `Request_EntityChanged`;
- duplicates → `Rejected`;
- a collection 404 → `Rejected`, not null;
- 429 → `Throttled`, and 503 → `Unavailable`;
- no failure text contains the response body (put a name and an e-mail address in the error body and assert they are absent);
- the handshake, `clientState` filtering and resource parsing, using Microsoft's sample payload (S1).

## Sources

All pages were read on 2026-10-01.

| Id | Page |
|---|---|
| S1 | Working with webhooks: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/dynamics-subscriptions> |
| S2 | Create subscriptions: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/api/dynamics_subscriptions_create> |
| S3 | Update subscriptions (`If-Match` required): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/api/dynamics_subscriptions_update> |
| S4 | Subscription resource: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/resources/dynamics_subscriptions> |
| S5 | Operational limits (OData per environment and per user, `$batch`, webhooks, companies): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/administration/operational-limits-online> |
| S6 | Working with API limits (429, 504, filtering, paging): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/dynamics-rate-limits> |
| S7 | Troubleshooting REST API/OData calls (error codes): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/webservices/dynamics-error-codes>. Also the v1.0 list: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v1.0/dynamics_error_codes> |
| S8 | Troubleshooting web service errors (HTTP statuses, `retry-after` on 502/503): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/webservices/web-service-troubleshooting> |
| S9 | Tips for working with the APIs (companies, `Accept-Language`, `Data-Access-Intent`, transactional batch): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/devenv-connect-apps-tips> |
| S10 | Update customers (`If-Match` required, send only changed values): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/api/dynamics_customer_update> |
| S11 | Customer resource (v2.0 properties): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/resources/dynamics_customer> |
| S12 | Contact resource: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/resources/dynamics_contact> |
| S13 | salesQuote resource: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/resources/dynamics_salesquote> |
| S14 | Table system fields (`SystemId`, `SystemModifiedBy`/`At`, `SystemRowVersion`): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/devenv-table-system-fields> |
| S15 | Service-to-service authentication: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/administration/automation-apis-using-s2s-authentication> |
| S16 | Endpoints for the APIs (online and on-premises): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/endpoints-apis-for-dynamics> |
| S17 | API endpoint structure (environment, custom route, company): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/webservices/api-endpoint-structure> |
| S18 | Developing a custom API: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/devenv-develop-custom-api> |
| S19 | `Unique` key property (not allowed in table extensions): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/properties/devenv-unique-property> |
| S20 | OData transactional `$batch`: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/webservices/use-odata-batch> |
| S21 | Filter expressions in OData URIs: <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/webservices/use-filter-expressions-in-odata-uris> |
| S22 | Managing updates (update windows): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/administration/tenant-admin-center-update-management> |
| S23 | Production and sandbox environments (country, localization, region): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/administration/tenant-admin-center-environments> |
| S24 | Copy an environment (webhooks disabled, job queue stopped): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/administration/tenant-admin-center-environments-copy> |
| S25 | Business events (preview): <https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/business-events-overview> |
| S26 | Credential chains in Azure Identity for .NET: <https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/credential-chains> |
