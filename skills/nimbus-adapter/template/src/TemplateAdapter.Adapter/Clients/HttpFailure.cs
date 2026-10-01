using System.Net;
using System.Text.Json;

namespace TemplateAdapter.Adapter.Clients;

/// <summary>Turns HTTP failures into the <see cref="ExternalSystemException"/> types.</summary>
internal static class HttpFailure
{
    private const int MaxBodyChars = 500;

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
            throw new ExternalSystemUnavailableException($"{operation} failed: {apiName} could not be reached ({ex.Message}).", ex);
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

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var message = Describe(response, operation, apiName, body);
        var status = (int)response.StatusCode;

        throw status switch
        {
            (int)HttpStatusCode.TooManyRequests => new ExternalSystemThrottledException(message, RetryAfter(response)),
            408 or 502 or 503 or 504 => new ExternalSystemUnavailableException(message),
            >= 400 and < 500 => new ExternalSystemRejectedException(message, status, ReadErrorCode(body)),
            _ => new ExternalSystemUnavailableException(message),
        };
    }

    /// <summary>
    /// <c>{operation} failed: {api} {METHOD} {path} → {status} {reason}. Body: {first 500 characters}</c>.
    /// The NimBus WebApp shows this text on the failed message, so it must be enough to act on. It
    /// never includes the request body; the response body is truncated, because vendor error texts
    /// can echo field values and NimBus does not mask error text.
    /// </summary>
    internal static string Describe(HttpResponseMessage response, string operation, string apiName, string body)
    {
        var method = response.RequestMessage?.Method.Method ?? "?";
        var path = response.RequestMessage?.RequestUri?.PathAndQuery ?? "?";
        var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
        var bodyPart = string.IsNullOrWhiteSpace(body) ? string.Empty : $" Body: {body}";

        return $"{operation} failed: {apiName} {method} {path} → {(int)response.StatusCode} {reason}.{bodyPart}";
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > MaxBodyChars ? body[..MaxBodyChars] + "…" : body;
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

    /// <summary>Reads <c>error.code</c> from a response's OData-style error body, or null.</summary>
    public static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ReadErrorCode(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));

    /// <summary>Reads <c>error.code</c> from an OData-style error body (Dataverse, Business Central and F&amp;O all use it).</summary>
    private static string? ReadErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body.TrimEnd('…'));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                    ? code.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
