#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Logging.Abstractions;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Adapter.Handlers;
using TemplateAdapter.Adapter.Mapping;
using TemplateAdapter.Contracts.Upstream;

namespace TemplateAdapter.Tests;

/// <summary>The handler's decisions, against an in-memory ExternalSystem.</summary>
[TestClass]
public sealed class HandlerTests
{
    [TestMethod]
    public async Task ANewAccount_IsUpserted_KeyedOnTheUpstreamId()
    {
        var externalSystem = new RecordingExternalSystemClient();

        await Handler(externalSystem).Handle(Account(), null!, CancellationToken.None);

        var upsert = externalSystem.Upserts.Single();
        Assert.AreEqual("ACC-1001", upsert.AccountKey);
        Assert.AreEqual("Fabrikam Marine", upsert.Name);
    }

    [TestMethod]
    public async Task HandlingTheSameMessageTwice_WritesOnce()
    {
        var externalSystem = new RecordingExternalSystemClient();
        var handler = Handler(externalSystem);

        await handler.Handle(Account(), null!, CancellationToken.None);
        await handler.Handle(Account(), null!, CancellationToken.None);

        Assert.HasCount(1, externalSystem.Accounts);
        Assert.HasCount(1, externalSystem.Upserts, "The second delivery finds the account up to date and writes nothing.");
    }

    [TestMethod]
    public async Task AnAccountThatAlreadyHoldsTheValues_IsNotWritten()
    {
        // Every write fires the target's plug-ins and change notifications, and can echo back.
        var externalSystem = new RecordingExternalSystemClient();
        externalSystem.Accounts["ACC-1001"] = AccountMapper.ToExternalSystem(Account());

        await Handler(externalSystem).Handle(Account(), null!, CancellationToken.None);

        Assert.IsEmpty(externalSystem.Upserts);
    }

    [TestMethod]
    public async Task AChangedField_IsWritten()
    {
        var externalSystem = new RecordingExternalSystemClient();
        externalSystem.Accounts["ACC-1001"] = AccountMapper.ToExternalSystem(Account()) with { Name = "Fabrikam (old name)" };

        await Handler(externalSystem).Handle(Account(), null!, CancellationToken.None);

        Assert.AreEqual("Fabrikam Marine", externalSystem.Upserts.Single().Name);
    }

    [TestMethod]
    public async Task AMissingParent_ThrowsWithTheRemedy_AndWritesNothing()
    {
        var externalSystem = new RecordingExternalSystemClient();
        var message = Account();
        message.ParentAccountId = "ACC-0001";

        var ex = await Assert.ThrowsExactlyAsync<MissingReferenceException>(() => Handler(externalSystem).Handle(message, null!, CancellationToken.None));

        Assert.AreEqual("ACC-0001", ex.ReferenceKey);
        StringAssert.Contains(ex.Message, "resubmit", StringComparison.Ordinal);
        Assert.IsEmpty(externalSystem.Upserts);
    }

    [TestMethod]
    public async Task AnExistingParent_IsLinked()
    {
        var externalSystem = new RecordingExternalSystemClient();
        externalSystem.Accounts["ACC-0001"] = new ExternalSystemAccount("ACC-0001", "Fabrikam Group", "DK", null, null);
        var message = Account();
        message.ParentAccountId = "ACC-0001";

        await Handler(externalSystem).Handle(message, null!, CancellationToken.None);

        Assert.AreEqual("ACC-0001", externalSystem.Upserts.Single().ParentAccountKey);
    }

    [TestMethod]
    public async Task AnAccountOwnedByExternalSystem_IsSkipped_NotFailed()
    {
        var externalSystem = new RecordingExternalSystemClient();
        externalSystem.OwnedByExternalSystem.Add("ACC-1001");

        await Handler(externalSystem).Handle(Account(), null!, CancellationToken.None);

        Assert.IsFalse(externalSystem.Accounts.ContainsKey("ACC-1001"));
    }

    [TestMethod]
    public async Task AClientFailure_PropagatesUnchanged_SoNimBusCanClassifyIt()
    {
        var externalSystem = new RecordingExternalSystemClient { NextFailure = new ExternalSystemUnavailableException("503") };

        await Assert.ThrowsExactlyAsync<ExternalSystemUnavailableException>(() => Handler(externalSystem).Handle(Account(), null!, CancellationToken.None));
    }

    [TestMethod]
    public void TheMapper_NormalisesWithoutLosingData()
    {
        var message = Account();
        message.Name = "  Fabrikam Marine ";
        message.CountryCode = "dk";
        message.Email = " ";

        var account = AccountMapper.ToExternalSystem(message);

        Assert.AreEqual("Fabrikam Marine", account.Name);
        Assert.AreEqual("DK", account.CountryCode);
        Assert.IsNull(account.Email);
        var blankCountry = Account();
        blankCountry.CountryCode = "  ";
        Assert.IsNull(AccountMapper.ToExternalSystem(blankCountry).CountryCode, "Blank means null, as every client reads it.");
        Assert.IsNull(account.ParentAccountKey);
    }

    private static UpstreamAccountUpdatedHandler Handler(IExternalSystemClient externalSystem) =>
        new(externalSystem, NullLogger<UpstreamAccountUpdatedHandler>.Instance);

    private static UpstreamAccountUpdated Account() => new()
    {
        AccountId = UpstreamAccountUpdated.Example.AccountId,
        Name = UpstreamAccountUpdated.Example.Name,
        CountryCode = UpstreamAccountUpdated.Example.CountryCode,
        Email = UpstreamAccountUpdated.Example.Email,
        ModifiedOn = UpstreamAccountUpdated.Example.ModifiedOn,
    };
}
