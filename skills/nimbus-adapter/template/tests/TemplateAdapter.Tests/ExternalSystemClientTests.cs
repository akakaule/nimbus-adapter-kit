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
    public async Task Upsert_MapsTheAnswer_ToAnOutcome(HttpStatusCode status, UpsertResult expected)
    {
        var client = Client(StubHandler.Returning(status));

        Assert.AreEqual(expected, await client.UpsertAccountAsync(Account, CancellationToken.None));
    }

    [TestMethod]
    public async Task AConflictThatSaysOwned_IsAnOutcome()
    {
        var client = Client(StubHandler.Returning(HttpStatusCode.Conflict, """{"error":{"code":"OwnedByExternalSystem","message":"Mastered in ExternalSystem."}}"""));

        Assert.AreEqual(UpsertResult.OwnedByExternalSystem, await client.UpsertAccountAsync(Account, CancellationToken.None));
    }

    [TestMethod]
    public async Task AnyOtherConflict_IsRejected_NeverSkipped()
    {
        // 409 also means "the record changed under you" (a concurrency conflict). Skipping it would
        // silently drop the change, so it fails and an operator looks at it.
        var client = Client(StubHandler.Returning(HttpStatusCode.Conflict, """{"error":{"code":"Request_EntityChanged","message":"Another user has already changed the record."}}"""));

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemRejectedException>(() => client.UpsertAccountAsync(Account, CancellationToken.None));

        Assert.AreEqual("Request_EntityChanged", ex.ErrorCode);
    }

    [TestMethod]
    public async Task TheFailureText_GivesTheOperatorWhatToActOn_WithoutAnyBody()
    {
        // NimBus stores the message verbatim in the audit trail and does not mask it; vendor error
        // texts echo field values. Only operation, request, status and the error code may appear.
        var vendorBody = """{"error":{"code":"Application_FieldValidation","message":"The e-mail 'echoed.secret@fabrikam-marine.example' is invalid."}}""";
        var client = Client(StubHandler.Returning(HttpStatusCode.UnprocessableEntity, vendorBody));
        var account = Account with { Email = "request.secret@fabrikam-marine.example" };

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemRejectedException>(() => client.UpsertAccountAsync(account, CancellationToken.None));

        StringAssert.Contains(ex.Message, "Upsert ExternalSystem account 'ACC-1001' failed", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "PUT /api/accounts/ACC-1001", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "422", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "Error code: Application_FieldValidation.", StringComparison.Ordinal);
        Assert.IsFalse(ex.Message.Contains("secret@", StringComparison.Ordinal), "Neither the request nor the response body may reach the failure text.");
    }

    [TestMethod]
    [DataRow("Uses spaces and an e-mail a@b.example", DisplayName = "free text")]
    [DataRow("TemplateAdapter.Adapter.Clients.ExternalSystemThrottledException:", DisplayName = "a retry rule key")]
    [DataRow("A_very_long_code_that_goes_on_and_on_well_past_sixty_four_characters_x", DisplayName = "too long")]
    public async Task ErrorCodesThatAreNotCodes_AreDropped(string code)
    {
        var client = Client(StubHandler.Returning(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"m\"}}"));

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemRejectedException>(() => client.UpsertAccountAsync(Account, CancellationToken.None));

        Assert.IsNull(ex.ErrorCode);
        Assert.IsFalse(ex.Message.Contains("Error code", StringComparison.Ordinal), ex.Message);
    }

    [TestMethod]
    public async Task VendorCodesThatEndInException_AreKept()
    {
        var client = Client(StubHandler.Returning(HttpStatusCode.BadRequest, """{"error":{"code":"Application_DialogException","message":"m"}}"""));

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemRejectedException>(() => client.UpsertAccountAsync(Account, CancellationToken.None));

        Assert.AreEqual("Application_DialogException", ex.ErrorCode);
    }

    [TestMethod]
    [DataRow("null", DisplayName = "null")]
    [DataRow("{}", DisplayName = "an empty object")]
    [DataRow("<html>proxy</html>", DisplayName = "not JSON")]
    public async Task ASuccessThatIsNotAnAccount_IsUnavailable_NeverNotFound(string body)
    {
        // "Not found" makes the caller create the record; a malformed success must not.
        var client = Client(StubHandler.Returning(HttpStatusCode.OK, body));

        await Assert.ThrowsExactlyAsync<ExternalSystemUnavailableException>(() => client.GetAccountAsync("ACC-1001", CancellationToken.None));
    }

    [TestMethod]
    public async Task TheFailureText_LeavesOutTheQueryString()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://externalsystem.test/") };
        using var response = await http.GetAsync(new Uri("api/accounts?$filter=email eq 'person@fabrikam.example'", UriKind.Relative));

        var text = HttpFailure.DescribeFailure(response, "Find account", "ExternalSystem API", errorCode: null);

        StringAssert.Contains(text, "GET /api/accounts → 400", StringComparison.Ordinal);
        Assert.IsFalse(text.Contains("person@", StringComparison.Ordinal), text);
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
