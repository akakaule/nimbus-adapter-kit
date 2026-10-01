# 19. Request/reply

**Confidence:** sample. **Applies to:** a system that needs an answer from another system's data
(credit status, stock, price), where a synchronous API call between the systems is not allowed or
not reachable.

## Solution

```csharp
// Contracts: the request is an event; the reply is a plain DTO (not an event).
public class CrmCreditCheckRequested : Event { [Required] public string CustomerId { get; set; } = ""; }   // no [SessionKey]
public sealed class CreditStatus { public string Status { get; set; } = "Unavailable"; public decimal? Limit { get; set; } }

// Adapter
subscriber.AddRequestHandler<CrmCreditCheckRequested, CreditStatus, CreditCheckHandler>();   // not picked up by assembly scanning

public sealed class CreditCheckHandler(IErpClient erp) : IRequestHandler<CrmCreditCheckRequested, CreditStatus>
{
    public async Task<CreditStatus> Handle(CrmCreditCheckRequested request, CancellationToken ct = default)
    {
        try { return Map(await erp.GetCreditAsync(request.CustomerId, ct)); }
        catch (ErpException) { return new CreditStatus { Status = "Unavailable" }; }   // never throw
    }
}
```

- **No session key on the request.** A read-only question must not block or wait behind the
  customer's changes.
- **The request handler never throws.** It answers `Unavailable` (or similar) instead. A thrown
  exception leaves a failed request message for an operator to clean up, and the caller just times
  out. If the request carried a session key, it would also block that session.
- **The caller handles the timeout.** Request/reply over a bus is best effort: show "unknown",
  never block a user flow on it.
- **Declare the request** with `Consumes<TRequest>()` on the answering endpoint.

## Tests

- The handler maps the answer, and maps a client failure to `Unavailable` without throwing.
- The catalog test exempts the request type from the session-key rule *explicitly*.

## Anti-patterns

- Request/reply for writes.
- Sharing the customer's session.
- Long-running work behind a request (use `18`).

## Provenance

- A credit check answered by the BC adapter, with no session key and a handler that never throws
  (NimBus BC sample). A CRM/ERP sample variant that shared the session was the counter-example.
