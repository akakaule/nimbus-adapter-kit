#pragma warning disable CA1032 // Only the client creates these, always with the details NimBus needs.
namespace TemplateAdapter.Adapter.Clients;

// The three ways an ExternalSystem call can fail, as distinct exception TYPES:
//
// - NimBus retry rules match on text that contains the exception type name (and the stack trace),
//   so the rules in Resilience/ are keyed on these names, never on a bare status code like "429",
//   which could also match a line number or a response body.
// - The circuit breaker excludes rejections and throttling by type.
// - None of the names contains "Validation", "Serialization" or "Deserialization": the opt-in
//   permanent-failure classifier dead-letters exceptions whose type name contains those words.
//
// NimBus stores only the exception type and message, so the message carries everything an operator
// needs (see HttpFailure.DescribeAsync).

/// <summary>Base type of every ExternalSystem call failure.</summary>
public abstract class ExternalSystemException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// 429 Too Many Requests: ExternalSystem's rate limit is exceeded. Transient: NimBus retries it with
/// exponential backoff. Excluded from the circuit breaker, because throttling is pacing, not an outage.
/// </summary>
public sealed class ExternalSystemThrottledException(string message, TimeSpan? retryAfter)
    : ExternalSystemException(message)
{
    /// <summary>The server's Retry-After hint, if it sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// 408/502/503/504, a timeout or no connection: ExternalSystem is unavailable (an update window, an
/// outage). Transient: counted by the circuit breaker, which pauses the adapter, and retried by NimBus.
/// </summary>
public sealed class ExternalSystemUnavailableException(string message, Exception? innerException = null)
    : ExternalSystemException(message, innerException);

/// <summary>
/// Any other 4xx: ExternalSystem refused the request on business or data grounds. Not transient: the
/// message fails, its session blocks, and an operator fixes the data and resubmits it from the NimBus
/// WebApp. Excluded from the circuit breaker, because a data problem is not an outage.
/// </summary>
public sealed class ExternalSystemRejectedException(string message, int statusCode, string? errorCode)
    : ExternalSystemException(message)
{
    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The error code from the response body (<c>error.code</c>), if any.</summary>
    public string? ErrorCode { get; } = errorCode;
}
