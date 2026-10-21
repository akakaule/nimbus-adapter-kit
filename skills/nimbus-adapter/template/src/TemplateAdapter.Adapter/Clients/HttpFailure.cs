using System.Net;
using System.Text.Json;

namespace TemplateAdapter.Adapter.Clients;

/// <summary>
/// Turns HTTP failures into the <see cref="ExternalSystemException"/> types.
/// <para>
/// NimBus stores the exception message verbatim in the audit trail (the failed message's error
/// text) and does not mask it. Vendor error bodies often echo field values (names, e-mail
/// addresses), so the failure text never contains the response body: only the operation, the
/// request method and path (without the query string), the status and the vendor's error code,
/// reduced to a safe character set. The error code plus the record key in the operation is what
/// an operator looks up in the external system.
/// </para>
/// </summary>
internal static class HttpFailure
{
    private const int MaxBodyChars = 4096;
    private const int MaxErrorCodeChars = 64;

    /// <summary>
    /// Sends a request. No connection or a timeout means the system is unavailable. A cancellation
    /// requested by the caller (shutdown) propagates unchanged.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        string operation,
        string apiName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await send().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalSystemUnavailableException($"{operation} failed: {apiName} could not be reached ({ex.HttpRequestError}).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalSystemUnavailableException($"{operation} failed: {apiName} did not answer in time.", ex);
        }
    }

    /// <summary>Throws the exception type NimBus should see for a non-success response.</summary>
    public static async Task ThrowOnFailureAsync(
        HttpResponseMessage response,
        string operation,
        string apiName,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var errorCode = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
        var message = DescribeFailure(response, operation, apiName, errorCode);
        var status = (int)response.StatusCode;

        throw status switch
        {
            (int)HttpStatusCode.TooManyRequests => new ExternalSystemThrottledException(message, RetryAfter(response)),
            408 or 502 or 503 or 504 => new ExternalSystemUnavailableException(message),
            >= 400 and < 500 => new ExternalSystemRejectedException(message, status, errorCode),
            _ => new ExternalSystemUnavailableException(message),
        };
    }

    /// <summary>
    /// <c>{operation} failed: {api} {METHOD} {path} → {status} {reason}. Error code: {code}.</c>
    /// The NimBus WebApp shows this text on the failed message. It deliberately contains no
    /// request or response body and no query string; pass only a code from <see cref="SanitizeErrorCode"/>.
    /// </summary>
    internal static string DescribeFailure(HttpResponseMessage response, string operation, string apiName, string? errorCode)
    {
        var method = response.RequestMessage?.Method.Method ?? "?";
        var path = response.RequestMessage?.RequestUri is { } uri
            ? (uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?')[0])
            : "?";
        var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
        var codePart = SanitizeErrorCode(errorCode) is { } code ? $" Error code: {code}." : string.Empty;

        return $"{operation} failed: {apiName} {method} {path} → {(int)response.StatusCode} {reason}.{codePart}";
    }

    /// <summary>Reads <c>error.code</c> from a response's OData-style error body, sanitized, or null.</summary>
    public static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        SanitizeErrorCode(ReadErrorCode(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// Keeps an error code only if it looks like one: letters, digits, <c>_ . -</c>, at most 64
    /// characters (for example <c>0x80040237</c>, <c>Application_DialogException</c>). Anything else
    /// is vendor free text, which can carry personal data, so it is dropped. No colon is allowed: the
    /// retry rules are keyed on <c>"&lt;full type name&gt;:"</c>, so a code can never match one.
    /// </summary>
    internal static string? SanitizeErrorCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxErrorCodeChars)
            return null;

        foreach (var c in code)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
                return null;
        }

        return code;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > MaxBodyChars ? body[..MaxBodyChars] : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return string.Empty; // Diagnostics must never fail the classification.
        }
    }

    /// <summary>Retry-After as a delay (it may be sent as seconds or as an HTTP date).</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
            return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        return null;
    }

    /// <summary>Reads <c>error.code</c> from an OData-style error body (Dataverse, Business Central and F&amp;O all use it).</summary>
    private static string? ReadErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
