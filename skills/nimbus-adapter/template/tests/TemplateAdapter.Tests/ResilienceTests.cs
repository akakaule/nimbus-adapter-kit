#pragma warning disable CA1707, CA2007
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Handlers;
using TemplateAdapter.Adapter.Resilience;

namespace TemplateAdapter.Tests;

/// <summary>
/// Which failures NimBus retries and which ones open the circuit. Retry rules match on the text
/// NimBus builds from the exception (type name, message and stack trace), so the tests use that
/// same text, including bodies that mention status codes as decoys.
/// </summary>
[TestClass]
public sealed class ResilienceTests
{
    private const string EventTypeId = "UpstreamAccountUpdated";

    [TestMethod]
    public void ThrottlingAndOutages_AreRetried_WithExponentialBackoff()
    {
        var options = new ExternalSystemResilienceOptions();
        var policies = Policies(options);

        var throttled = policies.GetRetryPolicy(EventTypeId, MatchedText(new ExternalSystemThrottledException("PUT … → 429 Too Many Requests.", null)));
        var unavailable = policies.GetRetryPolicy(EventTypeId, MatchedText(new ExternalSystemUnavailableException("PUT … → 503 Service Unavailable.")));

        Assert.IsNotNull(throttled);
        Assert.AreEqual(BackoffStrategy.Exponential, throttled.Strategy);
        Assert.AreEqual(TimeSpan.FromSeconds(options.ThrottledBaseDelaySeconds), throttled.BaseDelay);
        Assert.IsNotNull(unavailable);
        Assert.AreEqual(BackoffStrategy.Exponential, unavailable.Strategy);
        Assert.AreEqual(TimeSpan.FromSeconds(options.UnavailableBaseDelaySeconds), unavailable.BaseDelay);
    }

    [TestMethod]
    public void ARejection_IsNeverRetried_EvenWhenItsBodyMentions429Or503()
    {
        var policies = Policies(new ExternalSystemResilienceOptions());

        var rejected = policies.GetRetryPolicy(
            EventTypeId,
            MatchedText(new ExternalSystemRejectedException("PUT … → 422. Body: limits 429/503 apply, see ExternalSystem docs", 422, "AccountNameRequired")));

        Assert.IsNull(rejected, "A rejected request waits for an operator to fix the data and resubmit.");
    }

    [TestMethod]
    public void AMissingReference_IsRetriedAFewTimes_ThenBlocksTheSession()
    {
        var options = new ExternalSystemResilienceOptions();

        var policy = Policies(options).GetRetryPolicy(EventTypeId, MatchedText(new MissingReferenceException("parent account", "ACC-1", "Sync it first.")));

        Assert.IsNotNull(policy);
        Assert.AreEqual(options.MissingReferenceMaxRetries, policy.MaxRetries);
    }

    [TestMethod]
    public void Outages_OpenTheCircuit()
    {
        var breaker = Breaker(out _);

        for (var i = 0; i < 10; i++)
            breaker.RecordFailure(Wrapped(new ExternalSystemUnavailableException("PUT … → 503 Service Unavailable.")));

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public void RejectionsThrottlingAndMissingReferences_NeverOpenTheCircuit()
    {
        var breaker = Breaker(out _);

        for (var i = 0; i < 10; i++)
        {
            breaker.RecordFailure(Wrapped(new ExternalSystemRejectedException("422", 422, null)));
            breaker.RecordFailure(Wrapped(new ExternalSystemThrottledException("429", null)));
            breaker.RecordFailure(Wrapped(new MissingReferenceException("parent account", "ACC-1", "Sync it first.")));
        }

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public void TheRetryBudget_OutlastsTheBreakDuration()
    {
        // An outage that opens the circuit must not exhaust a message's retries before the circuit
        // can close again; otherwise every message in flight fails and needs an operator.
        var options = new ExternalSystemResilienceOptions();
        var policy = Policies(options).GetRetryPolicy(EventTypeId, MatchedText(new ExternalSystemUnavailableException("503")))!;

        var total = Enumerable.Range(0, policy.MaxRetries).Aggregate(TimeSpan.Zero, (sum, attempt) => sum + policy.GetDelay(attempt, new Random(1)));

        Assert.IsGreaterThan(TimeSpan.FromSeconds(options.CircuitBreakDurationSeconds * 3), total);
    }

    private static DefaultRetryPolicyProvider Policies(ExternalSystemResilienceOptions options)
    {
        var policies = new DefaultRetryPolicyProvider();
        ExternalSystemResilience.ConfigureRetries(policies, options);
        return policies;
    }

    private static EndpointCircuitBreaker Breaker(out ManualTimeProvider clock)
    {
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
        var options = new CircuitBreakerOptions();
        ExternalSystemResilience.ConfigureCircuitBreaker(options, new ExternalSystemResilienceOptions());
        return new EndpointCircuitBreaker("ExternalSystemEndpoint", options, clock);
    }

    /// <summary>What the breaker sees: the handler's exception, wrapped by the NimBus pipeline.</summary>
    private static EventContextHandlerException Wrapped(Exception inner) => new(inner);

    /// <summary>The text NimBus matches retry rules against: "{inner} {wrapper}", stack traces included.</summary>
    private static string MatchedText(Exception inner) => $"{inner} {Wrapped(inner)}";
}
