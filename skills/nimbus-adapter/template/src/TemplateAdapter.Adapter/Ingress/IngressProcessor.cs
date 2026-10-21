using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NimBus.SDK;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Mapping;

namespace TemplateAdapter.Adapter.Ingress;

/// <summary>What to do with an ingress message after processing.</summary>
public enum IngressDisposition
{
    /// <summary>Done (published, or deliberately ignored by an explicit rule).</summary>
    Complete,

    /// <summary>Transient problem: release the message so the queue redelivers it.</summary>
    Abandon,

    /// <summary>Permanent problem: move it to the dead-letter queue with a reason code.</summary>
    DeadLetter,
}

/// <summary>The outcome of one ingress message: disposition, a reason code and an operator-safe description.</summary>
public sealed record IngressResult(IngressDisposition Disposition, string Reason, string? Description = null);

/// <summary>
/// System → bus: thin notification → re-read → map → publish with a deterministic MessageId. Every
/// path ends in an explicit disposition; nothing is dropped silently. Publishing happens before the
/// queue message is completed (publish, then complete), so a crash republishes rather than loses,
/// and the deterministic MessageId lets subscribers' inboxes drop the duplicate.
/// </summary>
public sealed partial class IngressProcessor(
    IExternalSystemClient externalSystem,
    IPublisherClient publisher,
    ILogger<IngressProcessor> logger)
{
    /// <summary>The entity this sample ingress handles.</summary>
    public const string AccountEntity = "account";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Processes one notification body.</summary>
    public async Task<IngressResult> ProcessAsync(BinaryData body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        ChangeNotification? notification;
        try
        {
            notification = body.ToObjectFromJson<ChangeNotification>(JsonOptions);
        }
        catch (JsonException)
        {
            return new(IngressDisposition.DeadLetter, "InvalidNotification", "The notification is not valid JSON.");
        }

        if (notification is null || string.IsNullOrWhiteSpace(notification.Key) || notification.ChangedOn == default)
            return new(IngressDisposition.DeadLetter, "InvalidNotification", "entity, key and changedOn are required.");

        if (!string.Equals(notification.Entity, AccountEntity, StringComparison.OrdinalIgnoreCase))
            return new(IngressDisposition.DeadLetter, "UnsupportedEntity", "No flow handles this entity.");

        // Explicit rule for deletes: this sample does not propagate them. To do so, publish a
        // *Deleted event here from the notification's key (the record can't be re-read).
        if (string.Equals(notification.ChangeType, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            LogDeleteIgnored(logger, notification.Key);
            return new(IngressDisposition.Complete, "DeleteIgnored");
        }

        ExternalSystemAccount? account;
        try
        {
            account = await externalSystem.GetAccountAsync(notification.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExternalSystemThrottledException or ExternalSystemUnavailableException)
        {
            return new(IngressDisposition.Abandon, "SourceUnavailable");
        }
        catch (ExternalSystemRejectedException ex)
        {
            return new(IngressDisposition.DeadLetter, "ReadRejected", ex.Message);
        }

        if (account is null)
        {
            // Deleted (or no longer visible to the integration) between the notification and the re-read.
            LogRecordGone(logger, notification.Key);
            return new(IngressDisposition.Complete, "RecordGone");
        }

        var @event = AccountMapper.ToEvent(account, notification.ChangedOn);
        var messageId = MessageIdFor(notification);
        try
        {
            await publisher.Publish(@event, @event.GetSessionId(), messageId, messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (ValidationException)
        {
            // The publisher validates the event: the record lacks a required value.
            return new(IngressDisposition.DeadLetter, "InvalidRecord", "The record lacks a value the event requires.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPublishFailed(logger, notification.Key, ex);
            return new(IngressDisposition.Abandon, "PublishFailed");
        }

        LogPublished(logger, notification.Key, messageId);
        return new(IngressDisposition.Complete, "Published");
    }

    /// <summary>
    /// <c>account:{key}:{changedOn ticks}</c>: the same change always gets the same MessageId (so
    /// subscribers' inboxes drop redeliveries), and the next change a new one. Hashed above 128
    /// characters, the Service Bus limit.
    /// </summary>
    internal static string MessageIdFor(ChangeNotification notification)
    {
        var id = $"{AccountEntity}:{notification.Key}:{notification.ChangedOn.UtcTicks}";
        return id.Length <= 128 ? id : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Published the change to account {AccountKey} as {MessageId}.")]
    private static partial void LogPublished(ILogger logger, string accountKey, string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Account {AccountKey} no longer exists in ExternalSystem; nothing published.")]
    private static partial void LogRecordGone(ILogger logger, string accountKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ignored the delete of account {AccountKey}: deletes are not propagated.")]
    private static partial void LogDeleteIgnored(ILogger logger, string accountKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the change to account {AccountKey} failed; the notification is retried.")]
    private static partial void LogPublishFailed(ILogger logger, string accountKey, Exception exception);
}
