using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateAdapter.Adapter.Options;

namespace TemplateAdapter.Adapter.Clients;

/// <summary>Registers <see cref="IExternalSystemClient"/>.</summary>
public static class ExternalSystemClientRegistration
{
    /// <summary>
    /// Registers the typed client. The solution's ServiceDefaults add no resilience handler to
    /// HttpClients, so a failed call surfaces immediately and NimBus decides whether to retry it.
    /// </summary>
    public static IServiceCollection AddExternalSystemClient(this IServiceCollection services, ExternalSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baseUrl = options.BaseUrl ?? throw new InvalidOperationException("ExternalSystem:BaseUrl is required.");

        var http = services.AddHttpClient<IExternalSystemClient, ExternalSystemClient>(ExternalSystemClient.HttpClientName, client =>
        {
            // A base address without a trailing slash drops its last segment when relative paths
            // are resolved against it ("…/v9.2" + "accounts" = "…/accounts").
            client.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        if (!string.IsNullOrWhiteSpace(options.Scope))
        {
            var scope = options.Scope;
            services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
            http.AddHttpMessageHandler(sp => new BearerTokenHandler(sp.GetRequiredService<TokenCredential>(), scope));
        }

        return services;
    }
}
