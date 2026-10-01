#pragma warning disable CA1707, CA2007
using System.Net;
using TemplateAdapter.Adapter.Clients;

namespace TemplateAdapter.Tests;

/// <summary>
/// How the client classifies ExternalSystem's answers. Every failure must surface as one of three
/// exception types, because the retry rules and the circuit breaker are keyed on them.
/// </summary>
[TestClass]
public sealed class ExternalSystemClientTests
{
    private static readonly ExternalSystemAccount Account = new("ACC-1001", "Fabrikam Marine", "DK", null, null);

    [TestMethod]
    public async Task Status429_IsThrottled_WithTheRetryAfterHint()
    {
        var client = Client(StubHandler.Returning(HttpStatusCode.TooManyRequests, """{"error":{"code":"TooManyRequests","message":"Rate limit exceeded"}}""", retryAfterSeconds: 7));

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemThrottledException>(() => client.UpsertAccountAsync(Account, CancellationToken.None));

        Assert.AreEqual(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.RequestTimeout)]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.GatewayTimeout)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task OutageStatuses_AreUnavailable(HttpStatusCode status)
    {
        var client = Client(StubHandler.Returning(status));

        await Assert.ThrowsExactlyAsync<ExternalSystemUnavailableException>(() => client.UpsertAccountAsync(Account, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.PreconditionFailed)]
    [DataRow(HttpStatusCode.UnprocessableEntity)]
    public async Task OtherClientErrors_AreRejected_WithTheErrorCode(HttpStatusCode status)
    {
        var client = Client(StubHandler.Returning(status, """{"error":{"code":"AccountNameRequired","message":"The name is required."}}"""));

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemRejectedException>(() => client.UpsertAccountAsync(Account, CancellationToken.None));

        Assert.AreEqual((int)status, ex.StatusCode);
        Assert.AreEqual("AccountNameRequired", ex.ErrorCode);
    }

    [TestMethod]
    public async Task NoConnection_IsUnavailable()
    {
        var client = Client(new StubHandler(_ => throw new HttpRequestException("Connection refused")));

        await Assert.ThrowsExactlyAsync<ExternalSystemUnavailableException>(() => client.GetAccountAsync("ACC-1001", CancellationToken.None));
    }

    [TestMethod]
    public async Task ATimeout_IsUnavailable_ButShutdownStillCancels()
    {
        var timingOut = Client(new StubHandler(_ => throw new TaskCanceledException("The request timed out.")));
        await Assert.ThrowsExactlyAsync<ExternalSystemUnavailableException>(() => timingOut.GetAccountAsync("ACC-1001", CancellationToken.None));

        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var cancelled = Client(new StubHandler(_ => throw new TaskCanceledException("Cancelled.")));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled.GetAccountAsync("ACC-1001", shutdown.Token));
    }

    [TestMethod]
    public async Task AMissingAccount_IsNull_NotAFailure()
    {
        var client = Client(StubHandler.Returning(HttpStatusCode.NotFound));

        Assert.IsNull(await client.GetAccountAsync("ACC-404", CancellationToken.None));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Created, UpsertResult.Created)]
    [DataRow(HttpStatusCode.OK, UpsertResult.Updated)]
    [DataRow(HttpStatusCode.NoContent, UpsertResult.Updated)]
    [DataRow(HttpStatusCode.Conflict, UpsertResult.OwnedByExternalSystem)]
    public async Task Upsert_MapsTheAnswer_ToAnOutcome(HttpStatusCode status, UpsertResult expected)
    {
        var client = Client(StubHandler.Returning(status));

        Assert.AreEqual(expected, await client.UpsertAccountAsync(Account, CancellationToken.None));
    }

    [TestMethod]
    public async Task TheFailureText_GivesTheOperatorWhatToActOn_WithoutTheRequestBody()
    {
        var longBody = "{\"error\":{\"code\":\"X\",\"message\":\"" + new string('x', 2000) + "\"}}";
        var client = Client(StubHandler.Returning(HttpStatusCode.UnprocessableEntity, longBody));
        var account = Account with { Email = "secret@fabrikam-marine.example" };

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemRejectedException>(() => client.UpsertAccountAsync(account, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Upsert ExternalSystem account 'ACC-1001' failed", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "PUT /api/accounts/ACC-1001", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "422", StringComparison.Ordinal);
        Assert.IsFalse(ex.Message.Contains("secret@", StringComparison.Ordinal), "The request body must never reach the failure text.");
        Assert.IsLessThan(800, ex.Message.Length, "The response body is truncated.");
    }

    [TestMethod]
    public async Task AccountKeys_AreEscapedInThePath()
    {
        var handler = StubHandler.Returning(HttpStatusCode.NotFound);

        await Client(handler).GetAccountAsync("ACC/1001 x", CancellationToken.None);

        Assert.AreEqual("/api/accounts/ACC%2F1001%20x", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    private static ExternalSystemClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://externalsystem.test/") });
}
