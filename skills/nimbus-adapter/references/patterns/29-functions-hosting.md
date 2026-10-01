# 29. Functions hosting

**Confidence:** production. **Applies to:** adapters that must run as Azure Functions (isolated
worker), for example a customer standard, consumption-based cost, or existing Function apps.

The template generates a worker (Container App), because only a worker can pause its receivers
when the circuit breaker opens. Use Functions knowingly:

| | Worker (template) | Azure Functions isolated |
|---|---|---|
| Circuit breaker pauses receiving | Yes | **No.** The trigger keeps pulling |
| Deferred replay | `AddNimBusDeferredProcessorHostedService` | **A second trigger class** on the `deferredprocessor` subscription |
| Scaling | KEDA rule in the Bicep | Functions scale controller |
| Local run | Aspire AppHost project | Aspire `AddAzureFunctionsProject` / Functions tools |

## What a NimBus subscriber needs under Functions

1. `AddNimBusSubscriber(...)` in `Program.cs`, the same as a worker, but **no**
   `AddNimBusReceiver` and **no** deferred hosted service.
2. **A session-enabled `[ServiceBusTrigger]` class** for the endpoint's subscription, with
   `AutoCompleteMessages = false`, passing each message to NimBus's subscriber client.
   **Without this class, `AddNimBusSubscriber` receives nothing and messages stay Pending.**
3. **A second, non-session `[ServiceBusTrigger]` class** on the `deferredprocessor`
   subscription that calls `DeferredMessageDispatcher.ProcessAsync`. Without it, parked messages
   never replay.
4. `host.json`:
   - session handling (`maxConcurrentSessions`);
   - `maxAutoLockRenewalDuration` longer than any single attempt;
   - Service Bus extension settings consistent with `PrefetchCount = 0`.
5. `AzureWebJobsServiceBus` (or the identity-based `__fullyQualifiedNamespace` setting) for the
   trigger binding, separate from the NimBus client's connection.

Copy the exact trigger shapes from the NimBus CRM/ERP sample
(`samples/CrmErpDemo/Erp.Adapter.Functions`, `ErpEndpointFunction` and
`ErpDeferredProcessorFunction`), and check them against your NimBus version. NimBus's
`docs/azure-functions-hosting.md` has the details.

## Tests

- An architecture test reflecting over the Functions assembly: one session trigger on the endpoint
  subscription and one non-session trigger on `deferredprocessor`.
- The template's other test groups apply unchanged, because they don't depend on hosting.

## Anti-patterns

- In-process Functions for new work (end of support): use the isolated worker.
- Several endpoints in one Function app.
- Relying on a circuit breaker you configured but that cannot pause the trigger.

## Provenance

- A missing trigger class left messages Pending (NimBus sample fix).
- Separate session and deferred triggers (NimBus CRM/ERP sample, production CRM/NAV adapters).
- Worker chosen for its breaker (NimBus BC sample).
