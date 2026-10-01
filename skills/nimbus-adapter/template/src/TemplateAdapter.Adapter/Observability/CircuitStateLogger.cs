using NimBus.Core.CircuitBreaker;
using NimBus.Core.Extensions;

namespace TemplateAdapter.Adapter.Observability;

/// <summary>
/// Logs every circuit transition: Warning when the adapter pauses, Information otherwise. Add an
/// alert rule on the Warning, or replace this with <c>AddNimBusNotifications</c> to post to Teams.
/// An observer must never change message handling, so it only logs.
/// </summary>
public sealed partial class CircuitStateLogger(ILogger<CircuitStateLogger> logger) : IMessageLifecycleObserver
{
    /// <inheritdoc />
    public Task OnCircuitStateChanged(CircuitStateChangeContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.To == CircuitState.Open)
            LogOpened(logger, context.Endpoint, context.From, context.Reason);
        else
            LogTransition(logger, context.Endpoint, context.From, context.To, context.Reason);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit for {Endpoint} opened (was {From}); receivers are paused: {Reason}")]
    private static partial void LogOpened(ILogger logger, string endpoint, CircuitState from, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Circuit for {Endpoint} went {From} -> {To}: {Reason}")]
    private static partial void LogTransition(ILogger logger, string endpoint, CircuitState from, CircuitState to, string? reason);
}
