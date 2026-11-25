#if (InboxTable)
using Azure.Data.Tables;

namespace TemplateAdapter.Adapter.Inbox;

/// <summary>
/// Creates the inbox table in the local storage emulator before the receiver starts. Not registered
/// in Azure: there the Bicep creates the table and the adapter's role covers only that table.
/// </summary>
internal sealed class LocalInboxTableCreator(TableClient table) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
#endif
