#pragma warning disable CA1707, CA2007
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Handlers;
using TemplateAdapter.Adapter.Resilience;

namespace TemplateAdapter.Tests;

/// <summary>
/// Which failures NimBus retries and which ones open the circuit. Retry rules match on the
/// exception's type, so the tests pass the exception NimBus sees (the handler's exception inside
/// the pipeline's wrapper), with messages that mention status codes and type names as decoys.
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

        var throttled = policies.GetRetryPolicy(EventTypeId, Wrapped(new ExternalSystemThrottledException("PUT … → 429 Too Many Requests.", null)));
        var unavailable = policies.GetRetryPolicy(EventTypeId, Wrapped(new ExternalSystemUnavailableException("PUT … → 503 Service Unavailable.")));

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
            Wrapped(new ExternalSystemRejectedException("PUT … → 422. Body: limits 429/503 apply, see ExternalSystem docs", 422, "AccountNameRequired")));

        Assert.IsNull(rejected, "A rejected request waits for an operator to fix the data and resubmit.");
    }

    [TestMethod]
    public void ARejection_IsNeverRetried_EvenWhenItsTextNamesARetriedExceptionType()
    {
        // Rules match the exception's type, never its text, so a message that echoes another
        // exception's type name can't borrow that exception's retry policy.
        var policies = Policies(new ExternalSystemResilienceOptions());

        var rejected = policies.GetRetryPolicy(
            EventTypeId,
            Wrapped(new ExternalSystemRejectedException(
                "Upsert ExternalSystem account 'ExternalSystemThrottledException' failed: … → 422. Error code: ExternalSystemUnavailableException_x.", 422, null)));

        Assert.IsNull(rejected);
    }

    [TestMethod]
    public void AThrottledCall_CarriesTheServersRetryAfter_ForNimBusToHonour()
    {
        // NimBus waits for the longer of the policy delay and this hint, capped at MaxDelay.
        Exception throttled = new ExternalSystemThrottledException("PUT … → 429 Too Many Requests.", TimeSpan.FromSeconds(90));

        var hint = Assert.IsInstanceOfType<IRetryAfterHint>(throttled);
        Assert.AreEqual(TimeSpan.FromSeconds(90), hint.RetryAfter);
    }

    [TestMethod]
    public void AnUnclassifiedException_IsNeverRetried()
    {
        // No default policy: only the three typed rules retry.
        var policy = Policies(new ExternalSystemResilienceOptions())
            .GetRetryPolicy(EventTypeId, Wrapped(new InvalidOperationException("ExternalSystemThrottledException: 429")));

        Assert.IsNull(policy);
    }

    [TestMethod]
    public void AMissingReference_IsRetriedAFewTimes_ThenBlocksTheSession()
    {
        var options = new ExternalSystemResilienceOptions();

        var policy = Policies(options).GetRetryPolicy(EventTypeId, Wrapped(new MissingReferenceException("parent account", "ACC-1", "Sync it first.")));

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
        var policy = Policies(options).GetRetryPolicy(EventTypeId, Wrapped(new ExternalSystemUnavailableException("503")))!;

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

    /// <summary>What the retry rules and the breaker see: the handler's exception, wrapped by the NimBus pipeline.</summary>
    private static EventContextHandlerException Wrapped(Exception inner) => new(inner);

}
