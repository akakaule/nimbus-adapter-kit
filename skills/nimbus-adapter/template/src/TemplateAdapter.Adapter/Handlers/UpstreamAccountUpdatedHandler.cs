using NimBus.SDK.EventHandlers;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Mapping;
using TemplateAdapter.Contracts.Upstream;

namespace TemplateAdapter.Adapter.Handlers;

/// <summary>
/// Keeps the upstream account in ExternalSystem. The handler is thin: it resolves references,
/// maps with a pure function and makes one idempotent upsert. It never logs the payload.
/// <list type="bullet">
///   <item>Idempotent: the upsert is keyed on the upstream id, and an account that already holds
///   the mapped values is not written at all, so a redelivery or an operator resubmit changes
///   nothing.</item>
///   <item>Missing reference: a parent that is not in ExternalSystem yet throws
///   <see cref="MissingReferenceException"/> with the remedy (retried a few times, then the session
///   blocks for an operator).</item>
///   <item>Ownership: when ExternalSystem owns the record, the change is skipped on purpose and
///   logged; that is an outcome, not a failure.</item>
/// </list>
/// </summary>
public sealed partial class UpstreamAccountUpdatedHandler(
    IExternalSystemClient externalSystem,
    ILogger<UpstreamAccountUpdatedHandler> logger) : IEventHandler<UpstreamAccountUpdated>
{
    /// <inheritdoc />
    public async Task Handle(UpstreamAccountUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.ParentAccountId is { Length: > 0 } parentId
            && await externalSystem.GetAccountAsync(parentId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new MissingReferenceException(
                "parent account",
                parentId,
                $"Sync account '{parentId}' to ExternalSystem first, then resubmit this message.");
        }

        // Skip no-op writes: a redelivery, a resubmit or an unchanged upstream save must not write
        // again, because every write fires the target's own plug-ins, flows and change notifications
        // (and can echo back as a new change). The comparison covers exactly the fields this flow
        // owns, so the client's read must return them in the same shape the mapper produces.
        var desired = AccountMapper.ToExternalSystem(message);
        var current = await externalSystem.GetAccountAsync(desired.AccountKey, cancellationToken).ConfigureAwait(false);
        if (current == desired)
        {
            LogUnchanged(logger, message.AccountId);
            return;
        }

        var result = await externalSystem.UpsertAccountAsync(desired, cancellationToken).ConfigureAwait(false);

        switch (result)
        {
            case UpsertResult.Created:
                LogCreated(logger, message.AccountId);
                break;
            case UpsertResult.Updated:
                LogUpdated(logger, message.AccountId);
                break;
            case UpsertResult.OwnedByExternalSystem:
                LogOwnedByExternalSystem(logger, message.AccountId);
                break;
        }
    }

    // Source-generated logging: no allocations when the level is off. Log ids and outcomes, never
    // the payload (it can hold personal data; the NimBus WebApp shows it masked instead).
    [LoggerMessage(Level = LogLevel.Information, Message = "Created ExternalSystem account {AccountId}.")]
    private static partial void LogCreated(ILogger logger, string accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated ExternalSystem account {AccountId}.")]
    private static partial void LogUpdated(ILogger logger, string accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ExternalSystem account {AccountId} is already up to date; nothing written.")]
    private static partial void LogUnchanged(ILogger logger, string accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped the change to account {AccountId}: ExternalSystem owns its master data.")]
    private static partial void LogOwnedByExternalSystem(ILogger logger, string accountId);
}
