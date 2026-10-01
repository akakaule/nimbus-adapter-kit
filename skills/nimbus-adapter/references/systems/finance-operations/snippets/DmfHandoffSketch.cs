using System.Text.Json;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;

namespace TemplateAdapter.Adapter.Clients;

// Dynamics 365 Finance & Operations pack sketch (see ../README.md, "DMF batch import with
// PendingHandoff"). Framework-light: it shows the parts that are easy to get wrong and leaves
// storage, the package builder and the poller loop to you.
//
//   Handler (per message)           Batch job (every minute)          Poller (every 30 s)
//   ---------------------           ------------------------          -------------------
//   CaptureSettlement(context)      drain Pending rows (<= cap)       for each InFlight execution:
//   store row: Pending, payload,    build package, GetAzureWriteUrl,    GetExecutionSummaryStatus
//     record keys, settlement       upload, ImportFromPackage           Decide(status, errors, rows)
//   MarkHandedOff(context, ...)     store execution id on the rows:     SettleAsync(handoff, ...)
//   return (no throw)                 Pending -> InFlight               mark rows terminal
//
// Every row transition (Pending -> InFlight -> Succeeded/Failed, or -> TimingOut) is a conditional
// update on the row's ETag, so a timeout and a late result can never both settle the same message.
//
// Example handler body:
//
//   var settlement = DmfHandoff.CaptureSettlement(context);
//   await store.AddPendingAsync(settlement, payloadJson, [new DmfRecordKey(customerAccount, IsPrefix: false)], cancellationToken);
//   DmfHandoff.MarkHandedOff(context, externalJobId: settlement.EventId, expectedBy: TimeSpan.FromMinutes(30));

/// <summary>
/// One key under which F&amp;O may report an error for a handed-off message, in the format of the
/// <c>RecordId</c> that <c>GetExecutionErrors</c> returns: the entity's key values joined by spaces.
/// Use <see cref="IsPrefix"/> when F&amp;O appends values you cannot know in advance (for example
/// date-effective ranges); end the prefix with the separating space so it cannot match a longer key.
/// </summary>
/// <param name="Key">The exact record id, or its stable prefix.</param>
/// <param name="IsPrefix"><c>true</c> to match record ids that start with <paramref name="Key"/>.</param>
public sealed record DmfRecordKey(string Key, bool IsPrefix);

/// <summary>A handed-off message as the poller sees it: where to settle it and how F&amp;O names its records.</summary>
/// <param name="Settlement">The coordinates captured by <see cref="DmfHandoff.CaptureSettlement"/>.</param>
/// <param name="RecordKeys">
/// Every record the message put in the package. Empty when F&amp;O assigns the key during the import
/// (journals): such a message can only succeed or fail together with its whole package.
/// </param>
public sealed record DmfPendingRecord(HandoffSettlement Settlement, IReadOnlyList<DmfRecordKey> RecordKeys);

/// <summary>One entry of <c>GetExecutionErrors</c>.</summary>
/// <param name="RecordId">The failing record's key, or <c>null</c> when F&amp;O did not name one.</param>
/// <param name="ErrorMessage">F&amp;O's error text.</param>
public sealed record DmfExecutionError(string? RecordId, string? ErrorMessage);

/// <summary>How one handed-off message settles.</summary>
/// <param name="Record">The message.</param>
/// <param name="Succeeded"><c>true</c> to complete the handoff, <c>false</c> to fail it.</param>
/// <param name="ErrorText">Shown on the failed message in the NimBus WebApp.</param>
/// <param name="ErrorType">A classifier for grouping and alerting.</param>
public sealed record DmfSettlementDecision(DmfPendingRecord Record, bool Succeeded, string? ErrorText, string? ErrorType);

/// <summary>The values <c>GetExecutionSummaryStatus</c> returns.</summary>
public static class DmfExecutionStatus
{
    /// <summary>The status is not known.</summary>
    public const string Unknown = "Unknown";

    /// <summary>The execution has not started.</summary>
    public const string NotRun = "NotRun";

    /// <summary>The execution is running.</summary>
    public const string Executing = "Executing";

    /// <summary>Every record was imported, or so F&amp;O says: still read the errors.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>Some records failed.</summary>
    public const string PartiallySucceeded = "PartiallySucceeded";

    /// <summary>The execution failed.</summary>
    public const string Failed = "Failed";

    /// <summary>The execution was canceled.</summary>
    public const string Canceled = "Canceled";
}

/// <summary>The handler side and the settlement rules of the DMF + PendingHandoff flow.</summary>
public static class DmfHandoff
{
    /// <summary>Error type when F&amp;O names the failing record.</summary>
    public const string RecordRejected = "DmfRecordRejected";

    /// <summary>Error type when the package failed and the cause cannot be tied to this message.</summary>
    public const string ImportFailed = "DmfImportFailed";

    /// <summary>Error type when the execution ended Canceled or Unknown.</summary>
    public const string ImportIncomplete = "DmfImportIncomplete";

    private const string Reason = "Awaiting F&O data management import";
    private const int MaxErrorTextChars = 4000;

    /// <summary>
    /// Captures what the settlement needs, in the handler, before <see cref="MarkHandedOff"/>.
    /// Persist it with the payload. The session id must be the session the message arrived on:
    /// NimBus only accepts a settlement for the event that blocks that session.
    /// </summary>
    public static HandoffSettlement CaptureSettlement(IEventHandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sessionId = context.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException("The message has no session id; a pending handoff needs the session it blocks.");

        // OriginatingMessageId from the context, not MessageId: on a retry or resubmission MessageId
        // is the replay's id, and settling with it breaks the audit lineage.
        return new HandoffSettlement(
            EventId: context.EventId,
            SessionId: sessionId,
            MessageId: context.MessageId,
            EventTypeId: context.EventType,
            CorrelationId: string.IsNullOrEmpty(context.CorrelationId) ? context.MessageId : context.CorrelationId,
            OriginatingMessageId: string.IsNullOrEmpty(context.OriginatingMessageId) ? context.MessageId : context.OriginatingMessageId);
    }

    /// <summary>
    /// Marks the message as handed off. The DMF execution id does not exist yet (the batch job
    /// creates it later), so pass your own row id as <paramref name="externalJobId"/>; operators
    /// search for it in the WebApp. The handler then returns normally: it does not throw.
    /// </summary>
    public static void MarkHandedOff(IEventHandlerContext context, string externalJobId, TimeSpan expectedBy)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.MarkPendingHandoff(Reason, externalJobId, expectedBy);
    }

    /// <summary>
    /// Reads the <c>value</c> of a <c>GetExecutionErrors</c> response, which is itself a JSON
    /// string holding the error array. F&amp;O can embed raw XML in an error message and make that
    /// string invalid JSON: then the whole text becomes one error without a record id, which fails
    /// the package instead of leaving every message pending.
    /// </summary>
    public static IReadOnlyList<DmfExecutionError> ParseExecutionErrors(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [new DmfExecutionError(null, Truncate(value))];

            var errors = new List<DmfExecutionError>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    errors.Add(new DmfExecutionError(ReadString(item, "RecordId"), ReadString(item, "ErrorMessage")));
            }

            return errors;
        }
        catch (JsonException)
        {
            return [new DmfExecutionError(null, Truncate(value))];
        }
    }

    /// <summary>
    /// Maps an execution result to one decision per handed-off message, or <c>null</c> while the
    /// execution is still running (keep polling; your timeout settles it if it never ends).
    /// <list type="bullet">
    ///   <item>Canceled or Unknown: every message fails.</item>
    ///   <item>Succeeded without errors: every message succeeds.</item>
    ///   <item>Any error whose record id matches no message: every message fails, because there is
    ///   no telling which records F&amp;O dropped.</item>
    ///   <item>PartiallySucceeded with no error that names a message: every message fails. F&amp;O
    ///   says something failed and does not say what.</item>
    ///   <item>Otherwise the messages F&amp;O names fail with its error text and the rest succeed;
    ///   for Failed, the rest fail with an undetermined cause.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<DmfSettlementDecision>? Decide(
        string status,
        IReadOnlyList<DmfExecutionError> errors,
        IReadOnlyList<DmfPendingRecord> records)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(records);

        switch (status)
        {
            case DmfExecutionStatus.Canceled or DmfExecutionStatus.Unknown:
                return FailAll(records, $"The F&O import did not complete (status {status}). Check the import in F&O before you resubmit.", ImportIncomplete);
            case DmfExecutionStatus.Succeeded or DmfExecutionStatus.PartiallySucceeded or DmfExecutionStatus.Failed:
                break;
            default:
                return null; // NotRun, Executing, or a value this sketch does not know: not terminal.
        }

        var (byRecord, unattributed) = Attribute(errors, records);
        var undetermined = UndeterminedCause(status, unattributed);

        if (status != DmfExecutionStatus.Failed
            && (unattributed.Count > 0 || (status == DmfExecutionStatus.PartiallySucceeded && byRecord.Count == 0)))
        {
            return FailAll(records, undetermined, ImportFailed);
        }

        return [.. records.Select(record => byRecord.TryGetValue(record, out var text)
            ? new DmfSettlementDecision(record, Succeeded: false, Truncate(text), RecordRejected)
            : status == DmfExecutionStatus.Failed
                ? new DmfSettlementDecision(record, Succeeded: false, undetermined, ImportFailed)
                : new DmfSettlementDecision(record, Succeeded: true, ErrorText: null, ErrorType: null))];
    }

    /// <summary>
    /// Settles each decision through NimBus. Settle first, then mark your rows terminal: if the
    /// process stops in between, the next poll settles again, and NimBus completes a settlement
    /// that no longer matches a blocked session without changing anything.
    /// </summary>
    public static async Task SettleAsync(
        IHandoffClient handoff,
        IEnumerable<DmfSettlementDecision> decisions,
        string executionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(decisions);

        foreach (var decision in decisions)
        {
            if (decision.Succeeded)
            {
                await handoff.CompleteAsync(decision.Record.Settlement, new { executionId }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await handoff.FailAsync(
                    decision.Record.Settlement,
                    decision.ErrorText ?? $"The F&O import {executionId} failed.",
                    decision.ErrorType,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static (Dictionary<DmfPendingRecord, string> ByRecord, List<string> Unattributed) Attribute(
        IReadOnlyList<DmfExecutionError> errors,
        IReadOnlyList<DmfPendingRecord> records)
    {
        var byRecord = new Dictionary<DmfPendingRecord, string>(ReferenceEqualityComparer.Instance);
        var unattributed = new List<string>();

        foreach (var error in errors)
        {
            var text = error.ErrorMessage ?? "F&O reported an error without a message.";
            var matched = error.RecordId is { } recordId
                ? records.Where(record => record.RecordKeys.Any(key => Matches(key, recordId))).ToList()
                : [];

            if (matched.Count == 0)
            {
                unattributed.Add(error.RecordId is null ? text : $"{error.RecordId}: {text}");
                continue;
            }

            foreach (var record in matched)
                byRecord[record] = byRecord.TryGetValue(record, out var existing) ? existing + "\n" + text : text;
        }

        return (byRecord, unattributed);
    }

    private static bool Matches(DmfRecordKey key, string recordId) =>
        key.IsPrefix
            ? recordId.StartsWith(key.Key, StringComparison.Ordinal)
            : string.Equals(recordId, key.Key, StringComparison.Ordinal);

    private static List<DmfSettlementDecision> FailAll(IReadOnlyList<DmfPendingRecord> records, string errorText, string errorType) =>
        [.. records.Select(record => new DmfSettlementDecision(record, Succeeded: false, errorText, errorType))];

    private static string UndeterminedCause(string status, List<string> unattributed) =>
        unattributed.Count == 0
            ? $"The F&O import ended {status} and F&O returned no error for this message's records. Check the import in F&O before you resubmit."
            : Truncate($"The F&O import ended {status} with errors that cannot be tied to one message, so the whole package failed. F&O reported:\n{string.Join("\n", unattributed)}");

    private static string? ReadString(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
        }

        return null;
    }

    private static string Truncate(string text) => text.Length > MaxErrorTextChars ? text[..MaxErrorTextChars] + "…" : text;
}
