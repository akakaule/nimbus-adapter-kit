using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TemplateAdapter.Adapter.Clients;

// Business Central online pack snippet (see ../README.md, "Change detection (BC -> bus)").
//
// Framework-free building blocks for a webhook ingress: the DTOs Business Central posts, the
// validation handshake, clientState checks, resource parsing and deterministic message ids. Host
// them in whatever receives HTTP (an ASP.NET Core minimal API, an Azure Function), not in the
// adapter worker. A minimal API endpoint looks like this:
//
//   app.MapPost("/bc/notifications", async (HttpRequest request, IIngressQueue queue) =>
//   {
//       if (BusinessCentralWebhook.TryGetValidationToken(request.QueryString.Value, out var token))
//           return Results.Text(token, "text/plain", statusCode: 200);    // handshake: echo the token
//
//       using var reader = new StreamReader(request.Body);
//       var body = await reader.ReadToEndAsync();
//       if (!BusinessCentralWebhook.TryParse(body, out var batch))
//           return Results.StatusCode(200);   // not BC's format: drop and log, never 4xx
//
//       var ours = BusinessCentralWebhook.Authentic(batch, expectedClientState);
//       return await queue.TryEnqueueAsync(ours)           // durable first, work later
//           ? Results.StatusCode(200)
//           : Results.StatusCode(503);                     // BC retries 408/429/5xx; any other code deletes the subscription
//   });

/// <summary>The body Business Central posts to a <c>notificationUrl</c>: <c>{"value": [...]}</c>.</summary>
/// <param name="Value">The notifications. One post can carry notifications from several subscriptions.</param>
public sealed record BusinessCentralNotificationBatch(IReadOnlyList<BusinessCentralNotification>? Value);

/// <summary>
/// One thin change notification. It carries no field values: re-read <see cref="Resource"/> before
/// you map and publish anything.
/// </summary>
/// <param name="SubscriptionId">The subscription that produced the notification.</param>
/// <param name="ClientState">The secret you set on the subscription; compare it before trusting the item.</param>
/// <param name="ExpirationDateTime">When the subscription expires unless it is renewed.</param>
/// <param name="Resource">
/// The changed record (<c>api/v2.0/companies({id})/customers({id})</c>) or, for
/// <c>collection</c>, the entity set with a <c>$filter</c> that selects the changed rows.
/// </param>
/// <param name="ChangeType"><c>created</c>, <c>updated</c>, <c>deleted</c> or <c>collection</c>.</param>
/// <param name="LastModifiedDateTime">When the change happened (UTC).</param>
public sealed record BusinessCentralNotification(
    string? SubscriptionId,
    string? ClientState,
    DateTimeOffset? ExpirationDateTime,
    string? Resource,
    string? ChangeType,
    DateTimeOffset? LastModifiedDateTime);

/// <summary>A notification's <c>resource</c>, split into its parts.</summary>
/// <param name="ApiRoute">
/// The API route before <c>companies(...)</c>, for example <c>api/v2.0</c> or
/// <c>api/&lt;publisher&gt;/&lt;group&gt;/&lt;version&gt;</c>.
/// </param>
/// <param name="CompanyId">The company the change happened in.</param>
/// <param name="EntitySet">The entity set, for example <c>customers</c>.</param>
/// <param name="RecordId">The record's id (<c>SystemId</c>), or <c>null</c> for a collection.</param>
/// <param name="Query">The query string of a collection resource (its <c>$filter</c>), without <c>?</c>.</param>
public sealed record BusinessCentralResource(
    string ApiRoute,
    Guid CompanyId,
    string EntitySet,
    Guid? RecordId,
    string? Query);

/// <summary>
/// Webhook rules for Business Central online (Microsoft docs, verify; see ../README.md):
/// <list type="bullet">
///   <item>Creating AND renewing a subscription triggers a handshake: BC calls the
///   <c>notificationUrl</c> with <c>validationToken</c> on the query string, and you must answer
///   200 with the token as the body.</item>
///   <item>Notifications are delayed (30 s by default) and collapsed per record; more than 1,000
///   changes in that window arrive as one <c>collection</c> notification.</item>
///   <item>BC retries delivery for 36 hours only when you answer 408, 429 or 5xx. Any other
///   error status deletes the subscription, so an ingress never answers 4xx to BC.</item>
///   <item>Subscriptions expire after three days unless renewed.</item>
/// </list>
/// </summary>
public static partial class BusinessCentralWebhook
{
    /// <summary>The query-string parameter that carries the handshake token.</summary>
    public const string ValidationTokenParameter = "validationToken";

    /// <summary>A record was created.</summary>
    public const string ChangeCreated = "created";

    /// <summary>A record was updated.</summary>
    public const string ChangeUpdated = "updated";

    /// <summary>A record was deleted. It cannot be re-read; publish from the notification itself.</summary>
    public const string ChangeDeleted = "deleted";

    /// <summary>Many records changed. Re-read the resource (it carries a <c>$filter</c>) page by page.</summary>
    public const string ChangeCollection = "collection";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns the handshake token when the request is a validation call. Answer it with status 200
    /// and the token, unchanged, as a <c>text/plain</c> body.
    /// </summary>
    /// <param name="queryString">The raw query string, with or without the leading <c>?</c>.</param>
    /// <param name="token">The token to echo.</param>
    public static bool TryGetValidationToken(string? queryString, [NotNullWhen(true)] out string? token)
    {
        token = null;
        if (string.IsNullOrEmpty(queryString))
            return false;

        foreach (var pair in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
                continue;

            var name = Uri.UnescapeDataString(pair[..separator]);
            if (string.Equals(name, ValidationTokenParameter, StringComparison.OrdinalIgnoreCase))
            {
                token = Uri.UnescapeDataString(pair[(separator + 1)..]);
                return token.Length > 0;
            }
        }

        return false;
    }

    /// <summary>Parses a notification body. Returns <c>false</c> for anything that is not BC's shape.</summary>
    /// <param name="json">The request body.</param>
    /// <param name="batch">The parsed notifications.</param>
    public static bool TryParse(string? json, [NotNullWhen(true)] out BusinessCentralNotificationBatch? batch)
    {
        batch = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            batch = JsonSerializer.Deserialize<BusinessCentralNotificationBatch>(json, JsonOptions);
            return batch?.Value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The notifications whose <c>clientState</c> matches the secret you set on your subscriptions.
    /// Compare per item: one post can mix subscriptions. Drop (and log) the rest; do not answer 4xx.
    /// </summary>
    /// <param name="batch">The parsed post.</param>
    /// <param name="expectedClientState">The secret from your configuration (Key Vault).</param>
    public static IReadOnlyList<BusinessCentralNotification> Authentic(BusinessCentralNotificationBatch batch, string expectedClientState)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrEmpty(expectedClientState);

        var expected = Encoding.UTF8.GetBytes(expectedClientState);
        return (batch.Value ?? [])
            .Where(n => n.ClientState is not null
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(n.ClientState), expected))
            .ToList();
    }

    /// <summary>
    /// Splits a notification's <c>resource</c>. Accepts the forms Microsoft documents: with or
    /// without a leading slash, a single record, or an entity set with a query (collection).
    /// </summary>
    /// <param name="resource">The <c>resource</c> value.</param>
    /// <param name="parsed">The parts.</param>
    public static bool TryParseResource(string? resource, [NotNullWhen(true)] out BusinessCentralResource? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(resource))
            return false;

        var match = ResourcePattern().Match(resource.Trim());
        if (!match.Success
            || !Guid.TryParse(match.Groups["company"].Value, out var companyId))
        {
            return false;
        }

        Guid? recordId = null;
        if (match.Groups["id"].Success)
        {
            if (!Guid.TryParse(match.Groups["id"].Value, out var id))
                return false;
            recordId = id;
        }

        parsed = new BusinessCentralResource(
            match.Groups["route"].Value,
            companyId,
            match.Groups["set"].Value,
            recordId,
            match.Groups["query"].Success ? match.Groups["query"].Value : null);
        return true;
    }

    /// <summary>
    /// The absolute URL to re-read a notification's resource. The resource comes from an inbound
    /// HTTP body, so only a relative <c>api/...</c> path under your environment root is accepted;
    /// anything else (an absolute URL, <c>..</c>, another host) returns <c>false</c> instead of
    /// being fetched with your token.
    /// </summary>
    /// <param name="environmentRoot">
    /// <c>https://api.businesscentral.dynamics.com/v2.0/&lt;tenant-id&gt;/&lt;environment&gt;/</c> (trailing slash).
    /// </param>
    /// <param name="resource">The notification's <c>resource</c>.</param>
    /// <param name="uri">The URL to GET.</param>
    public static bool TryResolveResourceUri(Uri environmentRoot, string? resource, [NotNullWhen(true)] out Uri? uri)
    {
        ArgumentNullException.ThrowIfNull(environmentRoot);
        uri = null;

        if (string.IsNullOrWhiteSpace(resource) || !TryParseResource(resource, out _))
            return false;

        var relative = resource.Trim().TrimStart('/');
        if (!relative.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("..", StringComparison.Ordinal)
            || Uri.TryCreate(relative, UriKind.Absolute, out _))
        {
            return false;
        }

        var candidate = new Uri(environmentRoot, relative);
        if (!string.Equals(candidate.Host, environmentRoot.Host, StringComparison.OrdinalIgnoreCase)
            || !candidate.AbsolutePath.StartsWith(environmentRoot.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    /// <summary>
    /// A stable id for the raw notification, for the ingress queue's duplicate detection: the same
    /// resource, change type and timestamp always give the same id.
    /// </summary>
    /// <param name="notification">The notification.</param>
    public static string NotificationId(BusinessCentralNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var key = string.Join(
            '|',
            (notification.Resource ?? string.Empty).Trim().TrimStart('/').ToUpperInvariant(),
            (notification.ChangeType ?? string.Empty).ToUpperInvariant(),
            notification.LastModifiedDateTime?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        return "bcn-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// The deterministic MessageId for the event you publish after the re-read: the same record
    /// state always gives the same id, so the subscriber's inbox drops duplicate notifications and
    /// redelivered polls. Use the re-read record's <c>lastModifiedDateTime</c> (or row version).
    /// </summary>
    /// <param name="entitySet">The entity set, for example <c>customers</c>.</param>
    /// <param name="recordId">The record's <c>SystemId</c>.</param>
    /// <param name="version">The re-read record's <c>lastModifiedDateTime</c>.</param>
    public static string MessageIdFor(string entitySet, Guid recordId, DateTimeOffset version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySet);
        return string.Create(CultureInfo.InvariantCulture, $"bc:{entitySet}:{recordId:N}:{version.UtcTicks}");
    }

    /// <summary>
    /// Echo check after the re-read: <c>true</c> when the record was last changed by one of the
    /// integration's own BC users (the user security IDs your service principals write as), so
    /// publishing it would send your own write back to the bus.
    /// </summary>
    /// <param name="systemModifiedBy">The re-read record's <c>systemModifiedBy</c>.</param>
    /// <param name="integrationUserIds">The integration's user security IDs (one per service principal).</param>
    public static bool IsEcho(Guid? systemModifiedBy, IReadOnlyCollection<Guid> integrationUserIds)
    {
        ArgumentNullException.ThrowIfNull(integrationUserIds);
        return systemModifiedBy is { } user && integrationUserIds.Contains(user);
    }

    /// <summary>
    /// <c>true</c> when a subscription should be renewed now: it expires within
    /// <paramref name="margin"/>. Run the renewal job several times a day with a margin of a day or
    /// more, so one failed run (or a BC update window) does not let it lapse.
    /// </summary>
    /// <param name="expirationDateTime">The subscription's <c>expirationDateTime</c>.</param>
    /// <param name="now">The current time.</param>
    /// <param name="margin">How long before expiry to renew.</param>
    public static bool NeedsRenewal(DateTimeOffset expirationDateTime, DateTimeOffset now, TimeSpan margin) =>
        expirationDateTime - now <= margin;

    [GeneratedRegex(
        @"^/?(?<route>api/[^?]*?)/companies\((?<company>[0-9A-Fa-f-]{36})\)/(?<set>[A-Za-z0-9_]+)(?:\((?<id>[0-9A-Fa-f-]{36})\))?(?:\?(?<query>.*))?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ResourcePattern();
}
