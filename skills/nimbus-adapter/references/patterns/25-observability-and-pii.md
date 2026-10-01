# 25. Observability and personal data

**Confidence:** production. **Applies to:** every adapter.

## Problem

Operators see a failed message and must decide in seconds: fix the data, wait, or escalate.
Unactionable texts, missing correlation and silent outages turn that into investigations.
Meanwhile full IDoc XML, reflection dumps and payloads in logs leak personal data.

## Solution

- **OpenTelemetry through ServiceDefaults**: traces, metrics and logs, including the NimBus
  instrumentation (`AddNimBusInstrumentation`). Export with OTLP locally (the Aspire dashboard) and
  with Azure Monitor (`APPLICATIONINSIGHTS_CONNECTION_STRING`) in Azure.
- **Logs**:
  - Use `[LoggerMessage]` source-generated methods.
  - Log ids (event id, record key, external job id) and outcomes. Never payloads, request bodies,
    tokens or connection strings.
  - Information for outcomes, Warning for skipped references and breaker opens, Error only for
    what an operator must act on.
- **Failure text is the operator UI.** The client's message names the operation, request and
  status, plus the system's error (`20`). `MissingReferenceException` names the key and the
  remedy (`14`).
- **Personal data**: mark it `[Sensitive]` in the catalog, so the WebApp masks it. NimBus does not
  mask exception texts or logs, so keep personal data out of them: truncate vendor bodies, and
  never echo request bodies.
- **Metrics that matter**:
  - upserts by outcome (created, updated, unchanged, owned);
  - lookups and cache hit rate;
  - throttled calls;
  - breaker state.

  Use a `Meter` named after the adapter.
- **Alerts**: circuit opened, failed messages above N in an hour, dead letters, and missed
  heartbeats. Subscribers answer NimBus heartbeats automatically. Publish-only ingress apps don't,
  so watch them with health checks.
- **Notifications** (optional): `AddNimBusNotifications(channels => channels.AddTeams(...))`, with
  `NotifyOnFailure`, `NotifyOnDeadLetter` and `NotifyOnCircuitOpen`, plus a dedup window.

## Tests

- The failure-text test (`20`) asserts no request body.
- A logging test (optional) captures logs during the handler tests and asserts that no property of
  a `[Sensitive]` field appears.

## Anti-patterns

- `logger.LogInformation("Received {Payload}", JsonConvert.SerializeObject(message))`.
- Logging exception messages that contain vendor bodies at Information for every retry.
- No alert on a paused adapter.

## Provenance

- A handler instrumentation decorator plus log masking, and the rule "payloads must never be
  logged" (production CE adapter).
- Telemetry classes with messaging tags (production CRM/NAV adapters).
- Notification webhooks with dedup (NimBus BC sample).
- PII in logs found in HR adapter reviews.
