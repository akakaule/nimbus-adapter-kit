#pragma warning disable CA1707, CA2007
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using TemplateAdapter.Adapter.Clients;

namespace TemplateAdapter.Tests;

/// <summary>A clock the test moves forward.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Answers every request with a canned response, or throws what it is given.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var response = respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    public static StubHandler Returning(HttpStatusCode status, string body = "{}", int? retryAfterSeconds = null) =>
        new(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is int seconds)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return response;
        });
}

/// <summary>
/// An in-memory ExternalSystem: records every call and answers from a dictionary. Set
/// <see cref="NextFailure"/> to make the next call throw.
/// </summary>
internal sealed class RecordingExternalSystemClient : IExternalSystemClient
{
    public ConcurrentDictionary<string, ExternalSystemAccount> Accounts { get; } = new();

    public List<ExternalSystemAccount> Upserts { get; } = [];

    public HashSet<string> OwnedByExternalSystem { get; } = [];

    public Exception? NextFailure { get; set; }

    public Task<ExternalSystemAccount?> GetAccountAsync(string accountKey, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult(Accounts.TryGetValue(accountKey, out var account) ? account : null);
    }

    public Task<UpsertResult> UpsertAccountAsync(ExternalSystemAccount account, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Upserts.Add(account);
        if (OwnedByExternalSystem.Contains(account.AccountKey))
            return Task.FromResult(UpsertResult.OwnedByExternalSystem);

        var created = !Accounts.ContainsKey(account.AccountKey);
        Accounts[account.AccountKey] = account;
        return Task.FromResult(created ? UpsertResult.Created : UpsertResult.Updated);
    }

    private void ThrowIfFailing()
    {
        if (NextFailure is { } failure)
        {
            NextFailure = null;
            throw failure;
        }
    }
}
