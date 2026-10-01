using TemplateAdapter.Adapter.Resilience;

namespace TemplateAdapter.Adapter.Options;

/// <summary>How to reach ExternalSystem, bound from the <c>ExternalSystem</c> configuration section.</summary>
public sealed class ExternalSystemOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "ExternalSystem";

    /// <summary>
    /// Base URL of the ExternalSystem API. Locally this is the simulator through service discovery
    /// (<c>https+http://externalsystem-simulator</c>); in Azure the real API URL.
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// The Entra ID scope to request a token for (for example <c>https://{org}.crm.dynamics.com/.default</c>).
    /// Leave empty for the simulator, which needs no token.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Timeout for one call. Keep it well inside the Service Bus lock: NimBus retries a failed call
    /// as a new, audited attempt, so one attempt never needs to wait long.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Retry and circuit-breaker timings.</summary>
    public ExternalSystemResilienceOptions Resilience { get; set; } = new();

    /// <summary>Fails fast at startup on a configuration that cannot work.</summary>
    public void Validate()
    {
        if (BaseUrl is null)
            throw new InvalidOperationException($"{SectionName}:{nameof(BaseUrl)} is required.");
        if (TimeoutSeconds is <= 0 or > 120)
            throw new InvalidOperationException($"{SectionName}:{nameof(TimeoutSeconds)} must be between 1 and 120.");
    }
}
