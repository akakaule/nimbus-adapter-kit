using NimBus.Core;
using TemplateAdapter.Contracts.Endpoints;

namespace TemplateAdapter.Contracts;

/// <summary>
/// The platform catalog this adapter runs in: every endpoint and the events they produce and
/// consume. The provisioner creates the Service Bus topology from it, and the NimBus WebApp loads it
/// (<c>NimBus__PlatformType</c>) to show the flows. In a real platform this class usually lives in a
/// shared catalog package; it is local here so the adapter builds and runs on its own.
/// </summary>
public class AdapterCatalog : Platform
{
    /// <summary>Registers the endpoints of this catalog.</summary>
    public AdapterCatalog()
    {
        AddEndpoint(new UpstreamEndpoint());
        AddEndpoint(new ExternalSystemEndpoint());
    }
}
