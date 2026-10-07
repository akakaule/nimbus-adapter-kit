using Azure.Messaging.ServiceBus;

namespace TemplateAdapter.Adapter.Ingress;

/// <summary>
/// Receives change notifications from the ingress queue and settles each one as
/// <see cref="IngressProcessor"/> decides. Complete only after publishing; abandon transient
/// problems (the queue redelivers, up to its max delivery count, then dead-letters); dead-letter
/// permanent ones with a reason code an operator can filter on.
/// </summary>
public sealed partial class IngressWorker(
    ServiceBusClient client,
    IngressProcessor processor,
    IngressOptions options,
    ILogger<IngressWorker> logger) : BackgroundService
{
    private ServiceBusProcessor? _receiver;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _receiver = client.CreateProcessor(options.QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = options.MaxConcurrentCalls,
            PrefetchCount = 0,
        });
        _receiver.ProcessMessageAsync += HandleAsync;
        _receiver.ProcessErrorAsync += args =>
        {
            LogReceiveError(logger, args.ErrorSource.ToString(), args.Exception);
            return Task.CompletedTask;
        };

        await _receiver.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_receiver is not null)
        {
            await _receiver.StopProcessingAsync(cancellationToken).ConfigureAwait(false);
            await _receiver.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAsync(ProcessMessageEventArgs args)
    {
        var result = await processor.ProcessAsync(args.Message.Body, args.CancellationToken).ConfigureAwait(false);
        switch (result.Disposition)
        {
            case IngressDisposition.Complete:
                await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
                break;
            case IngressDisposition.Abandon:
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken).ConfigureAwait(false);
                break;
            case IngressDisposition.DeadLetter:
                LogDeadLettered(logger, args.Message.MessageId, result.Reason);
                await args.DeadLetterMessageAsync(args.Message, result.Reason, result.Description, args.CancellationToken).ConfigureAwait(false);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingress receive error ({ErrorSource}).")]
    private static partial void LogReceiveError(ILogger logger, string errorSource, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettered ingress message {MessageId}: {Reason}.")]
    private static partial void LogDeadLettered(ILogger logger, string messageId, string reason);
}
