using NimBus.Core.Endpoints;
using TemplateAdapter.Contracts.Upstream;

namespace TemplateAdapter.Contracts.Endpoints;

/// <summary>
/// This adapter's endpoint: the ExternalSystem side of the integration. Its class name is the
/// Service Bus topic and subscription name, so it must be unique in the namespace. Declare every
/// event the adapter consumes and produces here; the architecture tests check that the handlers
/// match the <c>Consumes</c> list.
/// </summary>
public class ExternalSystemEndpoint : Endpoint
{
    /// <summary>Declares the events this endpoint consumes and produces.</summary>
    public ExternalSystemEndpoint()
    {
        Consumes<UpstreamAccountUpdated>();
    }

    /// <inheritdoc />
    public override ISystem System => new ExternalSystemSystem();

    /// <inheritdoc />
    public override string Description =>
        "ExternalSystem adapter endpoint. Keeps accounts from the upstream system in ExternalSystem.";
}

internal sealed class ExternalSystemSystem : ISystem
{
    public string SystemId => "ExternalSystem";
}
