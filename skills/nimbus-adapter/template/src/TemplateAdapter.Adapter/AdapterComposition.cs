#if (Inbox)
using NimBus.Core.Inbox;
#endif
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;
#if (Inbox)
using NimBus.Inbox.SqlServer;
#endif
using NimBus.SDK.Extensions;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Handlers;
using TemplateAdapter.Adapter.Observability;
using TemplateAdapter.Adapter.Options;
using TemplateAdapter.Adapter.Resilience;
using TemplateAdapter.Contracts.Endpoints;

namespace TemplateAdapter.Adapter;

/// <summary>
/// Everything the adapter registers. <see cref="Program"/> calls <see cref="AddAdapter"/>; the tests
/// call the same methods, so what is tested is what runs.
/// </summary>
public static class AdapterComposition
{
    /// <summary>The endpoint this adapter receives on: topic and subscription name.</summary>
    public const string EndpointName = nameof(ExternalSystemEndpoint);

    /// <summary>Connection name of the Service Bus namespace (<c>ConnectionStrings:servicebus</c>).</summary>
    public const string ServiceBusConnectionName = "servicebus";

#if (Inbox)
    /// <summary>
    /// Connection name of the integration database (<c>ConnectionStrings:integration</c>) that holds
    /// the inbox. It is the integration platform's own database, never the external system's.
    /// </summary>
    public const string IntegrationDatabaseConnectionName = "integration";

#endif
    /// <summary>Registers the NimBus subscriber, the ExternalSystem client and the hosted services.</summary>
    public static IHostApplicationBuilder AddAdapter(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceDefaults();
        builder.AddAzureServiceBusClient(ServiceBusConnectionName);

        var adapter = builder.Configuration.GetSection(AdapterOptions.SectionName).Get<AdapterOptions>() ?? new AdapterOptions();
        var externalSystem = builder.Configuration.GetSection(ExternalSystemOptions.SectionName).Get<ExternalSystemOptions>()
            ?? throw new InvalidOperationException($"The '{ExternalSystemOptions.SectionName}' configuration section is required.");
        externalSystem.Validate();

        builder.Services.AddExternalSystemClient(externalSystem);
#if (Inbox)

        var integrationDb = builder.Configuration.GetConnectionString(IntegrationDatabaseConnectionName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{IntegrationDatabaseConnectionName} is required for the inbox.");
        builder.Services.AddNimBusSqlServerInbox(integrationDb);
#endif

        builder.Services.AddNimBus(nimbus =>
        {
            nimbus.AddPipelineBehavior<LoggingMiddleware>();
            nimbus.AddPipelineBehavior<ValidationMiddleware>();
            nimbus.AddLifecycleObserver<CircuitStateLogger>();
        });

        builder.Services.AddNimBusSubscriber(EndpointName, subscriber => ConfigureSubscriber(subscriber, adapter, externalSystem.Resilience));

        builder.Services.AddNimBusReceiver(options =>
        {
            options.TopicName = EndpointName;
            options.SubscriptionName = EndpointName;
            // Sessions are processed in parallel; messages within one session stay in order.
            options.MaxConcurrentSessions = adapter.MaxConcurrentSessions;
            // No prefetch: while the circuit breaker is open, prefetched messages would burn delivery
            // attempts on every open cycle.
            options.PrefetchCount = 0;
        });

        // Replays messages parked behind a blocked session once the blocking message is resolved.
        // Every subscriber endpoint needs it; without it, parked messages wait forever.
        builder.Services.AddNimBusDeferredProcessorHostedService(EndpointName);

        return builder;
    }

    /// <summary>
    /// The subscriber pipeline: handlers, inbox deduplication, retry rules and the circuit breaker.
    /// Shared with the tests (which run it on the in-memory transport).
    /// </summary>
    public static void ConfigureSubscriber(
        NimBusSubscriberBuilder subscriber,
        AdapterOptions adapter,
        ExternalSystemResilienceOptions resilience)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(resilience);

        subscriber.AddHandlersFromAssemblyContaining<UpstreamAccountUpdatedHandler>();
#if (Inbox)

        // Skips a message whose MessageId was already handled successfully. Only works when the
        // publisher sets a deterministic MessageId (for example "{entity}:{id}:{version}"). Operator
        // resubmits get a new MessageId and run again, so handlers must stay idempotent anyway.
        subscriber.UseInbox(inbox =>
        {
            inbox.DeduplicationStore = adapter.InboxStore;
            inbox.RetentionPeriod = TimeSpan.FromDays(adapter.InboxRetentionDays);
        });
#endif

        subscriber.ConfigureRetryPolicies(policies => ExternalSystemResilience.ConfigureRetries(policies, resilience));
        subscriber.WithCircuitBreaker(circuit => ExternalSystemResilience.ConfigureCircuitBreaker(circuit, resilience));
    }
}
