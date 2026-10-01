using System.Net.Http.Headers;
using Azure.Core;

namespace TemplateAdapter.Adapter.Clients;

/// <summary>
/// Adds an Entra ID bearer token for <paramref name="scope"/> to every request. The credential
/// (managed identity in Azure, your developer sign-in locally) caches and refreshes tokens itself, so
/// there is no token cache to write or get wrong here.
/// </summary>
internal sealed class BearerTokenHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
