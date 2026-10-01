# Business Central simulator routes

This is the subset of the Business Central online API that a local simulator should emulate, so the adapter and its tests take their real code paths. It follows the conventions of the template's `TemplateAdapter.Simulator`:

- the vendor API under the real route shape;
- an optional bearer-token check (`Simulator:RequireBearerToken`);
- errors in the vendor's shape;
- `/emulator/*` driver endpoints that play the BC user and the outage.

The tags mean the same as in [README.md](README.md). The `S…` numbers point to its Sources table.

**Last verified:** 2026-10-01

## Where the evidence comes from

- **The NimBus `DynamicsBcDemo` simulator** **[proven in the NimBus sample]** is a working example of this approach. It is a simulator, not a real BC. It serves:
  - the standard routes `/api/v2.0/companies({id})/customers({id})` and `.../customerFinancialDetail`;
  - a custom API under `/api/<publisher>/<group>/v1.0/companies({id})/...`;
  - fault middleware that answers `503` (an update window) and `429` (throttling), both with `Retry-After`;
  - BC-style error bodies.
- **Its error codes and PUT upserts are its own inventions.** Codes like `ServiceUnavailable_UpdateWindow`, `Application_TooManyRequests` and `Application_OwnedByBusinessCentral` don't come from BC, and real API pages are written with POST and PATCH.
- **Everything about real BC below is documentation only.** Error shapes and status codes still need to be confirmed with responses recorded from a sandbox. Until then, treat each row's tag as its confidence.

## Base path

Accept the real prefix, so that `ExternalSystem:BaseUrl` differs from production only in its host:

```text
https://<simulator>/v2.0/{tenant}/{environment}/api/v2.0/...
https://<simulator>/v2.0/{tenant}/{environment}/api/{publisher}/{group}/{version}/...
```

Accept any `{tenant}` and `{environment}`. Optionally, also serve the same routes without the `/v2.0/{tenant}/{environment}` prefix, as the NimBus sample does. The URL structure is **[Microsoft docs, verify]** (S16, S17).

## API routes

`{api}` is `api/v2.0` or `api/{publisher}/{group}/{version}`. `{c}` is `companies({companyId})`.

| Method and route | Behaviour to emulate | Source |
|---|---|---|
| `GET {api}/companies` | `{"value":[{"id","name","displayName",…}]}` | **[Microsoft docs, verify]** (S9) |
| `GET {api}/{c}/customers?$filter=externalId eq '…'` | `{"value":[…]}`, with each row carrying `"@odata.etag": "W/\"…\""`. Support `eq` on `externalId` and `number`, and `lastModifiedDateTime gt <utc>`. Answer `400` to an `or` across two different fields. | Shape: **[Microsoft docs, verify]** (S18, S6, S21). The 400 status for an `or` across fields is **[unverified]**: Microsoft documents only that it fails. |
| `GET {api}/{c}/customers({id})` | One row with `@odata.etag`. An unknown id returns `404` with `Internal_RecordNotFound`. | Code: **[Microsoft docs, verify]** (S7). The 404 status is **[unverified]**. |
| `POST {api}/{c}/customers` | Returns `201` and the row with a new `id` (SystemId), `number`, `@odata.etag` and `lastModifiedDateTime`. If the body carries `id`, use it. A duplicate `id` gets `Internal_EntityWithSameKeyExists`. Text that is too long gets `Application_StringExceededLength`. | Created entity: **[Microsoft docs, verify]** (S18, S14). The error statuses are **[unverified]**: use `400`. |
| `PATCH {api}/{c}/customers({id})` | `If-Match` is required. With no header, return `400` and `BadRequest_InvalidToken`. On a mismatch, return `409` with `Request_EntityChanged`, and let the status be configurable (409 or 412) so tests cover both. On success, change only the fields you were sent, issue a new `@odata.etag`, set `lastModifiedDateTime` and `systemModifiedBy`, and answer `200` with the row. | `If-Match` required, codes and messages: **[Microsoft docs, verify]** (S10, S7, S8). The statuses are **[unverified]**. |
| `If-Match: *` | Accept it, and skip the ETag check. | **[Microsoft docs, verify]** (S15, S20) |
| unknown company | `404` with `Internal_CompanyNotFound` | Code: **[Microsoft docs, verify]** (S7). Status **[unverified]**. |
| unknown entity set | `404` with `BadRequest_ResourceNotFound` | Code: **[Microsoft docs, verify]** (S7). Status **[unverified]**. |
| `POST {api}/$batch` | Optional. JSON `{"requests":[…]}` returns `{"responses":[{id,status,headers,body}]}` with a status for each inner request. Reject more than 100 operations. With `Isolation: snapshot`, roll everything back when one inner request fails. | **[Microsoft docs, verify]** (S20, S5). The status for more than 100 operations is **[unverified]**. |

**Rules for every API response**

- **Error body.** Return `{"error":{"code":"…","message":"…"}}`. You can append `  CorrelationId:  <guid>.` to the message as BC does, so the adapter never matches on message text **[Microsoft docs, verify]** (S7, S20).
- **ETags.** Make them opaque, weak (`W/"…"`), and new on every change **[Microsoft docs, verify]** (S18).
- **Audit fields.** Stamp `systemModifiedBy` with the caller's identity. Take it from the bearer token's app or object id, or from a test header such as `X-Sim-User`, so tests can tell the integration's own writes from a BC user's. That is what echo filtering needs. Stamp `lastModifiedDateTime` in UTC **[Microsoft docs, verify]** (S14, S11).
- **Bearer token.** When `Simulator:RequireBearerToken` is on, answer a missing token with `401` and `Authentication_InvalidCredentials` (kit convention, code from S7).

## Faults

| Fault | Behaviour | Source |
|---|---|---|
| Throttling | `429`, both with and without `Retry-After`, because BC documents `retry-after` only for 502 and 503. | `429`: **[Microsoft docs, verify]** (S5, S6). The header on 429 is **[unverified]**. The NimBus sample sends it **[proven in the NimBus sample]**. |
| Update window | `503` with `retry-after` on every API route until the window ends. The NimBus sample's 20-second window opens and then closes the adapter's circuit with no operator action. | `503` with `retry-after`: **[Microsoft docs, verify]** (S8). The NimBus sample behaviour is **[proven in the NimBus sample]**. |
| Data-plane blip | `502` with `retry-after` | **[Microsoft docs, verify]** (S8) |
| Long request | `504`, or `408` | **[Microsoft docs, verify]** (S6, S5) |
| Per-user concurrency (optional) | Process at most 5 requests at a time per caller and queue the rest. Answer `429` beyond 100 connections. A queue timeout gives `503`; make it configurable so it isn't 8 minutes in tests. | **[Microsoft docs, verify]** (S5) |

## Webhooks

| Method and route | Behaviour to emulate | Source |
|---|---|---|
| `POST {api}/subscriptions` (not company-scoped) | Body: `notificationUrl`, `resource` and `clientState`. First call `notificationUrl?validationToken=<random>` and require `200` with the token as the body; otherwise fail the create. Then answer `201` with the subscription, including `subscriptionId` and `expirationDateTime` (now + 3 days). For a custom API, require the publisher, group and version in both the URL and `resource`. | **[Microsoft docs, verify]** (S1, S2). The validation call's method and the status of a failed create are **[unverified]**: use POST and `400`. |
| `GET {api}/subscriptions` | `{"value":[…]}`, each with `@odata.etag` | **[Microsoft docs, verify]** (S4). The ETag on subscriptions is **[unverified]**. |
| `PATCH {api}/subscriptions({id})` | `If-Match` is required. It runs the handshake again, extends `expirationDateTime` and answers `200`. | **[Microsoft docs, verify]** (S3) |
| `DELETE {api}/subscriptions({id})` | `204` | **[Microsoft docs, verify]** (S1). The status is **[unverified]**. |
| Notification delivery | After a change, wait (the delay is configurable: BC uses 30 s, tests use about 1 s) and collapse to one notification per record. POST `{"value":[{subscriptionId, clientState, expirationDateTime, resource, changeType, lastModifiedDateTime}]}`. One post may mix subscriptions. With more than N changes in the window, send one `collection` notification whose `resource` carries `?$filter=lastModifiedDateTime gt <utc>`. | **[Microsoft docs, verify]** (S1) |
| Delivery result | On `408`, `429` or `5xx`, retry with backoff. Bound the retries; BC's limit is 36 hours. On any other non-2xx answer, **delete the subscription**. This proves the ingress never answers 4xx. Let a subscription expire unless it is renewed. | **[Microsoft docs, verify]** (S1) |
| Resource format | Single records use `api/v2.0/companies({id})/customers({id})` with no leading slash. Collections use `/api/v2.0/companies({id})/customers?$filter=…` with a leading slash. Emit both forms. | **[Microsoft docs, verify]** (S1) |

## `/emulator/*` driver endpoints

These are kit conventions, not BC behaviour. They extend the template simulator's `/emulator/faults` and `/emulator/accounts/{key}/own`.

| Method and route | What it does |
|---|---|
| `POST /emulator/faults` | Same body as the template: `{"status":503,"durationSeconds":30,"retryAfterSeconds":30}` opens an update window, and `{"status":429,"count":5,"retryAfterSeconds":2}` throttles the next 5 calls. Also accept `502`, `504` and `408`. |
| `DELETE /emulator/faults` | Ends any fault now. |
| `GET /emulator/customers` | Dumps the state, including `@odata.etag`, `systemModifiedBy` and `lastModifiedDateTime`, so tests can make assertions on it. |
| `POST /emulator/customers/{externalId}/edit` | Plays a BC user editing the record. Body: the fields to change. Stamps `systemModifiedBy` with a *non-integration* user, issues a new ETag and queues a notification. |
| `POST /emulator/customers/{externalId}/own` | Sets `ownedByBusinessCentral = true`, so the next upsert returns `OwnedByExternalSystem`. |
| `POST /emulator/conflicts` | `{"count":1,"status":409}` makes the next N PATCHes fail with a stale ETag, to prove the re-read-and-retry-once path and the second-conflict rejection. |
| `POST /emulator/customers/{externalId}/duplicate` | Inserts a second row with the same external id, to prove `DuplicateExternalId`. |
| `POST /emulator/bulk-change` | `{"count":1500}` changes many rows inside one notification window, to produce a `collection` notification. |
| `GET /emulator/notifications` | The notifications sent, with the HTTP status each delivery got. |
| `POST /emulator/notifications/flush` | Sends any pending notifications now, skipping the delay. |
| `POST /emulator/subscriptions/{id}/expire` | Sets `expirationDateTime` to now, to test the renewal job. |
| `DELETE /emulator/subscriptions/{id}` | Deletes a subscription as BC would after a 4xx, to test that the renewal job re-creates it. |
| `POST /emulator/upstream/accounts` | Unchanged from the template: publishes an upstream event with a deterministic MessageId. |

## Tests the simulator should make possible

1. Upsert creates, then updates only the changed fields, then skips a no-op.
2. A stale ETag is re-read once; a second conflict becomes `Rejected` with the code `Request_EntityChanged`.
3. An owned record returns `OwnedByExternalSystem` without a write.
4. A burst of `429` is retried by NimBus and completes on its own.
5. A `503` window opens the circuit, then a probe closes it.
6. A webhook handshake echoes the token, and a 4xx answer from the ingress deletes the subscription.
7. A user's edit is published, but the integration's own write (the echo) isn't.
8. A `collection` notification is re-read page by page.
9. The renewal job renews an expiring subscription and re-creates a deleted one.
