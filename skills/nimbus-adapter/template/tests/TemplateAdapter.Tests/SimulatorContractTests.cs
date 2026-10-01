#pragma warning disable CA1707, CA2007
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TemplateAdapter.Adapter.Clients;
using TemplateAdapter.Simulator;

namespace TemplateAdapter.Tests;

/// <summary>
/// The real client against the simulator, in process. Keeps the two in agreement: when you shape
/// the simulator like the real API (routes, status codes, error bodies), these tests show whether
/// the client still classifies every answer correctly.
/// </summary>
[TestClass]
public sealed class SimulatorContractTests
{
    private static WebApplicationFactory<SimulatorEntryPoint> _simulator = null!;

    [ClassInitialize]
    public static void StartSimulator(TestContext context) => _simulator = new WebApplicationFactory<SimulatorEntryPoint>();

    [ClassCleanup]
    public static void StopSimulator() => _simulator.Dispose();

    [TestMethod]
    public async Task CreateThenUpdate_AreReportedAsSuch()
    {
        var (client, _) = await Fresh();
        var account = new ExternalSystemAccount("ACC-2001", "Northwind Subsea", "NO", null, null);

        Assert.AreEqual(UpsertResult.Created, await client.UpsertAccountAsync(account, CancellationToken.None));
        Assert.AreEqual(UpsertResult.Updated, await client.UpsertAccountAsync(account with { Name = "Northwind Subsea AS" }, CancellationToken.None));
        Assert.AreEqual("Northwind Subsea AS", (await client.GetAccountAsync("ACC-2001", CancellationToken.None))?.Name);
    }

    [TestMethod]
    public async Task AnUnknownAccount_IsNull()
    {
        var (client, _) = await Fresh();

        Assert.IsNull(await client.GetAccountAsync("ACC-UNKNOWN", CancellationToken.None));
    }

    [TestMethod]
    public async Task AnOwnedAccount_IsAnOutcome()
    {
        var (client, http) = await Fresh();
        (await http.PostAsync(new Uri("/emulator/accounts/ACC-2002/own", UriKind.Relative), null)).EnsureSuccessStatusCode();

        var result = await client.UpsertAccountAsync(new ExternalSystemAccount("ACC-2002", "Contoso", null, null, null), CancellationToken.None);

        Assert.AreEqual(UpsertResult.OwnedByExternalSystem, result);
    }

    [TestMethod]
    public async Task InvalidData_IsRejected_WithTheSimulatorsErrorCode()
    {
        var (client, _) = await Fresh();

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemRejectedException>(
            () => client.UpsertAccountAsync(new ExternalSystemAccount("ACC-2003", "", null, null, null), CancellationToken.None));

        Assert.AreEqual("AccountNameRequired", ex.ErrorCode);
    }

    [TestMethod]
    public async Task InjectedThrottling_IsThrottled_WithRetryAfter()
    {
        var (client, http) = await Fresh();
        (await http.PostAsJsonAsync(new Uri("/emulator/faults", UriKind.Relative), new { status = 429, count = 1, retryAfterSeconds = 3 })).EnsureSuccessStatusCode();

        var ex = await Assert.ThrowsExactlyAsync<ExternalSystemThrottledException>(() => client.GetAccountAsync("ACC-2004", CancellationToken.None));

        Assert.AreEqual(TimeSpan.FromSeconds(3), ex.RetryAfter);
        Assert.IsNull(await client.GetAccountAsync("ACC-2004", CancellationToken.None), "The fault was planned for one call only.");
    }

    [TestMethod]
    public async Task AnInjectedOutage_IsUnavailable()
    {
        var (client, http) = await Fresh();
        (await http.PostAsJsonAsync(new Uri("/emulator/faults", UriKind.Relative), new { status = 503, count = 1 })).EnsureSuccessStatusCode();

        await Assert.ThrowsExactlyAsync<ExternalSystemUnavailableException>(() => client.GetAccountAsync("ACC-2005", CancellationToken.None));
    }

    /// <summary>The client under test, plus a raw HttpClient for the driver endpoints. Clears faults first.</summary>
    private static async Task<(ExternalSystemClient Client, HttpClient Http)> Fresh()
    {
        var http = _simulator.CreateClient();
        (await http.DeleteAsync(new Uri("/emulator/faults", UriKind.Relative))).EnsureSuccessStatusCode();
        return (new ExternalSystemClient(_simulator.CreateClient()), http);
    }
}
