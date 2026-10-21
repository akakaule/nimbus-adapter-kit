using NimBus.Core.Endpoints;
#if (IngressEnabled)
using TemplateAdapter.Contracts.ExternalSystem;
#endif
using TemplateAdapter.Contracts.Upstream;

namespace TemplateAdapter.Contracts.Endpoints;

/// <summary>
/// Placeholder for the system on the other side of the integration (another adapter in your
/// platform). Replace it, and the events under <c>Upstream/</c>, with your catalog's real endpoint.
/// </summary>
public class UpstreamEndpoint : Endpoint
{
    /// <summary>Declares the events this endpoint produces.</summary>
    public UpstreamEndpoint()
    {
        Produces<UpstreamAccountUpdated>();
#if (IngressEnabled)
        Consumes<ExternalSystemAccountUpdated>();
#endif
    }

    /// <inheritdoc />
    public override ISystem System => new UpstreamSystem();

    /// <inheritdoc />
    public override string Description =>
        "Placeholder for the upstream system that owns accounts. Replace with your catalog's real endpoint.";
}

internal sealed class UpstreamSystem : ISystem
{
    public string SystemId => "Upstream";
}
