#pragma warning disable CA1707, CA2007
using System.Net;
using Azure;
using Azure.Core;
using Azure.Identity;
using TemplateAdapter.Adapter.Clients;

namespace TemplateAdapter.Tests;

/// <summary>
/// Token failures must reach NimBus as a classified failure, not as an untyped exception that
/// matches no retry rule and blocks the session.
/// </summary>
[TestClass]
public sealed class BearerTokenHandlerTests
{
    private const string Scope = "https://externalsystem.test/.default";

    [TestMethod]
    public async Task AToken_IsSentAsABearerHeader()
    {
        var stub = StubHandler.Returning(HttpStatusCode.NotFound);

        await Client(new FakeCredential(token: "t0ken"), stub).GetAccountAsync("ACC-1", CancellationToken.None);

        Assert.AreEqual("Bearer t0ken", stub.Requests.Single().Headers.Authorization?.ToString());
    }

    [TestMethod]
    public async Task AnUnreachableIdentityService_IsUnavailable_AndSaysItIsTransient()
    {
        var credential = new FakeCredential(failure: new AuthenticationFailedException("token request failed", new RequestFailedException(503, "Service Unavailable")));

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemUnavailableException>(
            () => Client(credential, StubHandler.Returning(HttpStatusCode.OK)).GetAccountAsync("ACC-1", CancellationToken.None));

        StringAssert.Contains(ex.Message, "identity service is unavailable", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AMissingCredential_IsUnavailable_AndSaysItIsMisconfigured()
    {
        // Misconfiguration fails every message, so it is counted by the circuit breaker (Unavailable)
        // rather than blocking each session as a rejection; the text tells the operator what to fix.
        var credential = new FakeCredential(failure: new CredentialUnavailableException("No managed identity endpoint found."));

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemUnavailableException>(
            () => Client(credential, StubHandler.Returning(HttpStatusCode.OK)).GetAccountAsync("ACC-1", CancellationToken.None));

        StringAssert.Contains(ex.Message, "authentication is misconfigured", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(400, false)]
    [DataRow(401, false)]
    [DataRow(429, true)]
    [DataRow(500, true)]
    [DataRow(503, true)]
    public void IdentityServiceStatuses_AreClassified(int status, bool transient)
    {
        var failure = new AuthenticationFailedException("token request failed", new RequestFailedException(status, "identity service answer"));

        Assert.AreEqual(transient, BearerTokenHandler.IsTransient(failure));
    }

    private static ExternalSystemClient Client(TokenCredential credential, HttpMessageHandler inner) =>
        new(new HttpClient(new BearerTokenHandler(credential, Scope) { InnerHandler = inner }) { BaseAddress = new Uri("http://externalsystem.test/") });

    private sealed class FakeCredential(string token = "token", Exception? failure = null) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            failure is null ? new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)) : throw failure;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            failure is null ? ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1))) : ValueTask.FromException<AccessToken>(failure);
    }
}
