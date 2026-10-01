#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
#if (Inbox)
using NimBus.Core.Inbox;
#endif
using NimBus.Core.Messages;
using NimBus.Testing;
using NimBus.Testing.Extensions;
using TemplateAdapter.Adapter;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Options;
using TemplateAdapter.Adapter.Resilience;
using TemplateAdapter.Contracts.Upstream;

namespace TemplateAdapter.Tests;

/// <summary>
/// The real subscriber pipeline (the same <see cref="AdapterComposition.ConfigureSubscriber"/> that
/// runs in production) on the NimBus in-memory transport. Assert on side effects in ExternalSystem,
/// not on internal bookkeeping.
/// </summary>
[TestClass]
public sealed class PipelineTests
{
    [TestMethod]
    public async Task AMessage_ReachesExternalSystem_ThroughThePipeline()
    {
        var externalSystem = new RecordingExternalSystemClient();
        using var provider = Pipeline(externalSystem);

        var context = Delivery(UpstreamAccountUpdated.Example, messageId: "account:ACC-1001:1");
        await provider.GetRequiredService<IMessageHandler>().Handle(context);

        Assert.IsTrue(context.IsCompleted);
        Assert.AreEqual("Fabrikam Marine", externalSystem.Accounts["ACC-1001"].Name);
    }
#if (Inbox)

    [TestMethod]
    public async Task TheSameMessageDeliveredTwice_IsHandledOnce()
    {
        var externalSystem = new RecordingExternalSystemClient();
        using var provider = Pipeline(externalSystem);
        var handler = provider.GetRequiredService<IMessageHandler>();

        await handler.Handle(Delivery(UpstreamAccountUpdated.Example, messageId: "account:ACC-1001:1"));
        var duplicate = Delivery(UpstreamAccountUpdated.Example, messageId: "account:ACC-1001:1");
        await handler.Handle(duplicate);

        Assert.HasCount(1, externalSystem.Upserts);
        Assert.AreEqual(HandlerOutcome.DuplicateDetected, duplicate.HandlerOutcome);
    }
#endif

    private static ServiceProvider Pipeline(RecordingExternalSystemClient externalSystem)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IExternalSystemClient>(externalSystem);
#if (Inbox)
        services.AddNimBusInMemoryInbox();
        var adapter = new AdapterOptions { InboxStore = InboxStore.InMemory };
#else
        var adapter = new AdapterOptions();
#endif
        services.AddNimBusTestTransport(
            subscriber => AdapterComposition.ConfigureSubscriber(subscriber, adapter, new ExternalSystemResilienceOptions()),
            AdapterComposition.EndpointName);
        return services.BuildServiceProvider();
    }

    private static InMemoryMessageContext Delivery(UpstreamAccountUpdated message, string messageId)
    {
        var eventTypeId = nameof(UpstreamAccountUpdated);
        return new InMemoryMessageContext(
            new Message
            {
                MessageId = messageId,
                EventId = messageId,
                EventTypeId = eventTypeId,
                To = AdapterComposition.EndpointName,
                From = "UpstreamEndpoint",
                OriginatingFrom = "UpstreamEndpoint",
                SessionId = message.GetSessionId(),
                CorrelationId = messageId,
                MessageType = MessageType.EventRequest,
                ParentMessageId = Constants.Self,
                OriginatingMessageId = Constants.Self,
                MessageContent = new MessageContent
                {
                    EventContent = new EventContent
                    {
                        EventTypeId = eventTypeId,
                        EventJson = JsonConvert.SerializeObject(message),
                    },
                },
            },
            new InMemorySessionState());
    }
}
