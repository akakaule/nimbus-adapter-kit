using NimBus.Core.CircuitBreaker;
using NimBus.Core.Messages;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Handlers;

namespace TemplateAdapter.Adapter.Resilience;

/// <summary>
/// Retry and circuit-breaker timings, bound from <c>ExternalSystem:Resilience</c>. The defaults
/// suit a SaaS system with update windows of minutes; tune them to your system's limits.
/// </summary>
public sealed class ExternalSystemResilienceOptions
{
    /// <summary>First retry delay after a 429. Doubles per attempt (bounded jitter).</summary>
    public int ThrottledBaseDelaySeconds { get; set; } = 15;

    /// <summary>Longest delay between throttling retries.</summary>
    public int ThrottledMaxDelaySeconds { get; set; } = 300;

    /// <summary>Throttling retries before the message fails and waits for an operator.</summary>
    public int ThrottledMaxRetries { get; set; } = 8;

    /// <summary>First retry delay after an outage (503, timeout, no connection). Doubles per attempt.</summary>
    public int UnavailableBaseDelaySeconds { get; set; } = 30;

    /// <summary>Longest delay between outage retries.</summary>
    public int UnavailableMaxDelaySeconds { get; set; } = 600;

    /// <summary>Outage retries before the message fails and waits for an operator.</summary>
    public int UnavailableMaxRetries { get; set; } = 8;

    /// <summary>Delay between retries of a message whose referenced record is missing (it may be on its way).</summary>
    public int MissingReferenceDelaySeconds { get; set; } = 60;

    /// <summary>Retries of a missing reference before the message fails and blocks its session.</summary>
    public int MissingReferenceMaxRetries { get; set; } = 3;

    /// <summary>Calls the breaker must see in its window before it may open.</summary>
    public int CircuitMinimumThroughput { get; set; } = 5;

    /// <summary>Failure percentage (outages only) that opens the circuit.</summary>
    public double CircuitFailurePercentageThreshold { get; set; } = 50;

    /// <summary>How far back the breaker looks when it computes the failure rate.</summary>
    public int CircuitSamplingWindowSeconds { get; set; } = 120;

    /// <summary>How long the receivers pause once the circuit opens, before a probe is let through.</summary>
    public int CircuitBreakDurationSeconds { get; set; } = 60;

    /// <summary>Successful probes needed to close the circuit again.</summary>
    public int CircuitHalfOpenProbeCount { get; set; } = 1;
}

/// <summary>How the adapter treats each kind of ExternalSystem failure.</summary>
public static class ExternalSystemResilience
{
    /// <summary>
    /// Throttling and outages are retried by NimBus with exponential backoff: each retry is a new,
    /// broker-scheduled and audited attempt. There is deliberately NO default policy: a rejection
    /// (any other 4xx) is a data problem, so the message fails at once, its session blocks, and an
    /// operator fixes the data and resubmits. Rules match on the exception TYPE NAME, which is part
    /// of the text NimBus matches (that text also contains the stack trace and the message, so a
    /// rule keyed on "429" could match a line number or a response body).
    /// </summary>
    public static void ConfigureRetries(DefaultRetryPolicyProvider policies, ExternalSystemResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(options);

        policies
            .AddExceptionRule(nameof(ExternalSystemThrottledException), new RetryPolicy
            {
                MaxRetries = options.ThrottledMaxRetries,
                Strategy = BackoffStrategy.Exponential,
                BaseDelay = TimeSpan.FromSeconds(options.ThrottledBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.ThrottledMaxDelaySeconds),
                Jitter = JitterMode.Bounded,
            })
            .AddExceptionRule(nameof(ExternalSystemUnavailableException), new RetryPolicy
            {
                MaxRetries = options.UnavailableMaxRetries,
                Strategy = BackoffStrategy.Exponential,
                BaseDelay = TimeSpan.FromSeconds(options.UnavailableBaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.UnavailableMaxDelaySeconds),
                Jitter = JitterMode.Bounded,
            })
            .AddExceptionRule(nameof(MissingReferenceException), new RetryPolicy
            {
                MaxRetries = options.MissingReferenceMaxRetries,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(options.MissingReferenceDelaySeconds),
            });
    }

    /// <summary>
    /// Pauses the adapter while ExternalSystem is down. Only outages count: throttling is paced by
    /// retries, and rejections and missing references are data problems, not a sign the system is
    /// unhealthy.
    /// </summary>
    public static void ConfigureCircuitBreaker(CircuitBreakerOptions circuit, ExternalSystemResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(options);

        circuit.MinimumThroughput = options.CircuitMinimumThroughput;
        circuit.FailurePercentageThreshold = options.CircuitFailurePercentageThreshold;
        circuit.SamplingWindow = TimeSpan.FromSeconds(options.CircuitSamplingWindowSeconds);
        circuit.BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakDurationSeconds);
        circuit.HalfOpenProbeCount = options.CircuitHalfOpenProbeCount;
        circuit.Exclude<ExternalSystemRejectedException>();
        circuit.Exclude<ExternalSystemThrottledException>();
        circuit.Exclude<MissingReferenceException>();
    }
}
