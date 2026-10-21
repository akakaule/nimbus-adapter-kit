#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Logging.Abstractions;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Ingress;
using TemplateAdapter.Contracts.ExternalSystem;

namespace TemplateAdapter.Tests;

/// <summary>
/// System → bus: every notification ends in an explicit disposition, publishing happens before
/// completing, and the same change always gets the same MessageId.
/// </summary>
[TestClass]
public sealed class IngressProcessorTests
{
    private static readonly DateTimeOffset ChangedOn = new(2026, 1, 15, 9, 45, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AChange_IsReReadAndPublished_WithADeterministicMessageIdAndTheSessionKey()
    {
        var (processor, externalSystem, publisher) = Processor();
        externalSystem.Accounts["ACC-1001"] = new ExternalSystemAccount("ACC-1001", "Fabrikam Marine", "DK", null, null);

        var result = await processor.ProcessAsync(Notification("ACC-1001"), CancellationToken.None);

        Assert.AreEqual(IngressDisposition.Complete, result.Disposition);
        var (published, sessionId, messageId) = publisher.Published.Single();
        var @event = (ExternalSystemAccountUpdated)published;
        Assert.AreEqual("Fabrikam Marine", @event.Name);
        Assert.AreEqual(ChangedOn, @event.ModifiedOn);
        Assert.AreEqual("ACC-1001", sessionId);
        Assert.AreEqual($"account:ACC-1001:{ChangedOn.UtcTicks}", messageId);
    }

    [TestMethod]
    public async Task TheSameNotificationTwice_GetsTheSameMessageId()
    {
        var (processor, externalSystem, publisher) = Processor();
        externalSystem.Accounts["ACC-1001"] = new ExternalSystemAccount("ACC-1001", "Fabrikam Marine", null, null, null);

        await processor.ProcessAsync(Notification("ACC-1001"), CancellationToken.None);
        await processor.ProcessAsync(Notification("ACC-1001"), CancellationToken.None);

        Assert.HasCount(1, publisher.Published.Select(p => p.MessageId).Distinct().ToArray(), "Subscribers' inboxes drop the second one.");
    }

    [TestMethod]
    public async Task ARecordThatIsGone_IsCompletedWithoutPublishing()
    {
        var (processor, _, publisher) = Processor();

        var result = await processor.ProcessAsync(Notification("ACC-404"), CancellationToken.None);

        Assert.AreEqual(new IngressResult(IngressDisposition.Complete, "RecordGone"), result);
        Assert.IsEmpty(publisher.Published);
    }

    [TestMethod]
    public async Task ADelete_IsCompletedByTheExplicitRule()
    {
        var (processor, _, publisher) = Processor();

        var result = await processor.ProcessAsync(Notification("ACC-1001", changeType: "deleted"), CancellationToken.None);

        Assert.AreEqual(IngressDisposition.Complete, result.Disposition);
        Assert.AreEqual("DeleteIgnored", result.Reason);
        Assert.IsEmpty(publisher.Published);
    }

    [TestMethod]
    public async Task AnUnavailableSource_IsAbandoned_ForTheQueueToRedeliver()
    {
        var (processor, externalSystem, publisher) = Processor();
        externalSystem.NextFailure = new ExternalSystemUnavailableException("503");

        var result = await processor.ProcessAsync(Notification("ACC-1001"), CancellationToken.None);

        Assert.AreEqual(IngressDisposition.Abandon, result.Disposition);
        Assert.IsEmpty(publisher.Published);
    }

    [TestMethod]
    public async Task ARejectedReRead_IsDeadLettered()
    {
        var (processor, externalSystem, _) = Processor();
        externalSystem.NextFailure = new ExternalSystemRejectedException("GET … → 403.", 403, "Forbidden");

        var result = await processor.ProcessAsync(Notification("ACC-1001"), CancellationToken.None);

        Assert.AreEqual(IngressDisposition.DeadLetter, result.Disposition);
        Assert.AreEqual("ReadRejected", result.Reason);
    }

    [TestMethod]
    [DataRow("not json", DisplayName = "not JSON")]
    [DataRow("""{"entity":"account","changeType":"updated","changedOn":"2026-01-15T09:45:00Z"}""", DisplayName = "no key")]
    [DataRow("""{"entity":"account","key":"ACC-1"}""", DisplayName = "no changedOn")]
    public async Task AMalformedNotification_IsDeadLettered(string body)
    {
        var (processor, _, _) = Processor();

        var result = await processor.ProcessAsync(BinaryData.FromString(body), CancellationToken.None);

        Assert.AreEqual(new IngressResult(IngressDisposition.DeadLetter, "InvalidNotification", result.Description), result);
    }

    [TestMethod]
    public async Task AnEntityWithoutAFlow_IsDeadLettered()
    {
        var (processor, _, _) = Processor();

        var result = await processor.ProcessAsync(Notification("C-1", entity: "contact"), CancellationToken.None);

        Assert.AreEqual("UnsupportedEntity", result.Reason);
    }

    [TestMethod]
    public async Task APublishFailure_IsAbandoned_NeverCompleted()
    {
        var (processor, externalSystem, publisher) = Processor();
        externalSystem.Accounts["ACC-1001"] = new ExternalSystemAccount("ACC-1001", "Fabrikam Marine", null, null, null);
        publisher.NextFailure = new InvalidOperationException("Service Bus is unreachable.");

        var result = await processor.ProcessAsync(Notification("ACC-1001"), CancellationToken.None);

        Assert.AreEqual(IngressDisposition.Abandon, result.Disposition);
    }

    [TestMethod]
    public async Task ARecordMissingRequiredData_IsDeadLettered_NotRetriedForever()
    {
        var (processor, externalSystem, _) = Processor();
        externalSystem.Accounts["ACC-1001"] = new ExternalSystemAccount("ACC-1001", "", null, null, null);

        var result = await processor.ProcessAsync(Notification("ACC-1001"), CancellationToken.None);

        Assert.AreEqual("InvalidRecord", result.Reason);
    }

    [TestMethod]
    public void LongKeys_AreHashedBelowTheServiceBusLimit()
    {
        var id = IngressProcessor.MessageIdFor(new ChangeNotification("account", new string('k', 200), "updated", ChangedOn));

        Assert.IsLessThanOrEqualTo(128, id.Length);
        Assert.AreEqual(id, IngressProcessor.MessageIdFor(new ChangeNotification("account", new string('k', 200), "updated", ChangedOn)));
    }

    private static BinaryData Notification(string key, string changeType = "updated", string entity = "account") =>
        BinaryData.FromObjectAsJson(new { entity, key, changeType, changedOn = ChangedOn });

    private static (IngressProcessor Processor, RecordingExternalSystemClient ExternalSystem, CapturingPublisher Publisher) Processor()
    {
        var externalSystem = new RecordingExternalSystemClient();
        var publisher = new CapturingPublisher();
        return (new IngressProcessor(externalSystem, publisher, NullLogger<IngressProcessor>.Instance), externalSystem, publisher);
    }
}
