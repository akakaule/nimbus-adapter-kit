#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using NimBus.SDK.EventHandlers;
using TemplateAdapter.Adapter;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Contracts.Endpoints;

namespace TemplateAdapter.Tests;

/// <summary>
/// Turns the silent failure modes of a NimBus adapter into failing tests. Each test builds the
/// real composition (<see cref="AdapterComposition.AddAdapter"/>) without starting it.
/// </summary>
[TestClass]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void TheReceiverAndTheDeferredReplay_AreBothRegistered()
    {
        // Without the receiver nothing is consumed; without deferred replay, messages parked behind
        // a blocked session wait forever once the blocking message is resolved.
        using var host = BuildHost();

        var hostedServices = host.Services.GetServices<IHostedService>().Select(s => s.GetType().Name).ToList();

        CollectionAssert.Contains(hostedServices, "NimBusReceiverHostedService");
        CollectionAssert.Contains(hostedServices, "DeferredMessageProcessorHostedService");
    }

    [TestMethod]
    public void TheHandlers_MatchTheEndpointsConsumesDeclarations()
    {
        // A handler without a Consumes<T> never receives anything (no subscription rule), and a
        // Consumes<T> without a handler fails every message of that type.
        var handled = typeof(AdapterComposition).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>))
            .Select(i => i.GetGenericArguments()[0].Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        var consumed = new ExternalSystemEndpoint().EventTypesConsumed.Select(t => t.Id).Order(StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(consumed, handled);
    }

    [TestMethod]
    public void TheExternalSystemClient_HasNoHiddenRetryHandler()
    {
        // A resilience handler (for example the Aspire default AddStandardResilienceHandler) would
        // retry 429/5xx invisibly inside one NimBus attempt. Retries belong to NimBus.
        using var host = BuildHost();
        var handler = host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ExternalSystemClient.HttpClientName);

        var chain = new List<string>();
        for (HttpMessageHandler? current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
            chain.Add(current.GetType().FullName ?? current.GetType().Name);

        Assert.IsFalse(
            chain.Any(name => name.Contains("Resilience", StringComparison.OrdinalIgnoreCase) || name.Contains("Polly", StringComparison.OrdinalIgnoreCase)),
            "Handler chain: " + string.Join(" -> ", chain));
    }

    [TestMethod]
    public void TheEndpointName_IsTheCatalogEndpoint()
    {
        // The receiver listens on AdapterComposition.EndpointName; the provisioner creates the topic
        // from the catalog endpoint's Id. They must be the same string.
        Assert.AreEqual(AdapterComposition.EndpointName, new ExternalSystemEndpoint().Id);
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Never contacted: the host is built, not started.
            ["ConnectionStrings:servicebus"] = "Endpoint=sb://architecture-tests.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==",
#if (Inbox)
            ["ConnectionStrings:integration"] = "Server=localhost;Database=integration;Integrated Security=true;TrustServerCertificate=true",
#endif
            ["ExternalSystem:BaseUrl"] = "http://externalsystem.test",
        });
        builder.AddAdapter();
        return builder.Build();
    }
}
