using System.Net.Http.Headers;
using Azure;
using Azure.Core;
using Azure.Identity;

namespace TemplateAdapter.Adapter.Clients;

/// <summary>
/// Adds an Entra ID bearer token for <paramref name="scope"/> to every request. The credential
/// (managed identity in Azure, your developer sign-in locally) caches and refreshes tokens itself, so
/// there is no token cache to write or get wrong here.
/// <para>
/// A failure to get a token is classified like any other call failure, as
/// <see cref="ExternalSystemUnavailableException"/>, with a message that says whether it looks
/// transient (identity service unavailable, throttled, network) or like misconfiguration (no
/// credential, or an identity or permission the service refuses). Both count for the circuit breaker
/// on purpose. A broken identity fails every message, so the adapter should pause and alert once,
/// not block every session one by one.
/// </para>
/// </summary>
internal sealed class BearerTokenHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, scope);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns a token failure into the exception NimBus should see.</summary>
    internal static ExternalSystemUnavailableException Classify(Exception exception, string scope) =>
        IsTransient(exception)
            ? new ExternalSystemUnavailableException(
                $"Could not get a token for '{scope}': the identity service is unavailable ({exception.GetType().Name}). NimBus retries.",
                exception)
            : new ExternalSystemUnavailableException(
                $"Could not get a token for '{scope}': authentication is misconfigured ({exception.GetType().Name}). " +
                "Check the identity, its access to the system and the scope, then resubmit.",
                exception);

    /// <summary>Network problems, timeouts and 5xx/429 answers from the identity service are transient.</summary>
    internal static bool IsTransient(Exception exception)
    {
        if (exception is CredentialUnavailableException)
            return false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException or TimeoutException or TaskCanceledException:
                    return true;
                case RequestFailedException failed when failed.Status is 0 or 408 or 429 or >= 500:
                    return true;
                case RequestFailedException:
                    return false;
            }
        }

        return false;
    }
}
