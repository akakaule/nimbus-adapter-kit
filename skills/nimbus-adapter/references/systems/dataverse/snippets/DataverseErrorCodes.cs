using System.Text.Json;

namespace TemplateAdapter.Adapter.Clients;

// Dataverse pack snippet (see ../README.md, "Errors and throttling").
//
// Copy this file to src/<Name>.Adapter/Clients/ next to DataverseWebApiClient.cs, which uses it.
// Codes and names come from Microsoft's "Web service error codes" reference [Microsoft docs, verify];
// the README says which of them production adapters actually met.

/// <summary>What the adapter should do with a Dataverse failure.</summary>
public enum DataverseFailureKind
{
    /// <summary>Service protection limit (429): pace, then retry. Maps to <see cref="ExternalSystemThrottledException"/>.</summary>
    Throttled,

    /// <summary>Platform fault that clears by itself (SQL timeout, database unavailable, 5xx). Maps to <see cref="ExternalSystemUnavailableException"/>.</summary>
    Transient,

    /// <summary>A row with the same key already exists. Success for a create-only write; see the README for upserts.</summary>
    DuplicateKey,

    /// <summary>The addressed row does not exist (404).</summary>
    NotFound,

    /// <summary>Dataverse refused the request on data, privilege or plug-in grounds. Maps to <see cref="ExternalSystemRejectedException"/>.</summary>
    Rejected,
}

/// <summary>
/// Classifies Dataverse Web API errors (<c>{"error":{"code":"0x...","message":"..."}}</c>). The
/// HTTP status alone is not enough: 412 is either a duplicate key or a version conflict, and a
/// platform fault's code ("retry later") matters more than its status.
/// </summary>
public static class DataverseErrorCodes
{
    // ---- Service protection limits: sent with 429 and Retry-After (seconds) ----------------------
    /// <summary>ThrottlingBurstRequestLimitExceededError: too many requests in the 300-second window.</summary>
    public const string RequestCountLimit = "0x80072322";

    /// <summary>ThrottlingTimeExceededError: too much combined execution time in the 300-second window.</summary>
    public const string ExecutionTimeLimit = "0x80072321";

    /// <summary>ThrottlingConcurrencyLimitExceededError: too many concurrent requests (52 by default).</summary>
    public const string ConcurrentRequestLimit = "0x80072326";

    // ---- Row identity ----------------------------------------------------------------------------
    /// <summary>ObjectDoesNotExist: no row with that id.</summary>
    public const string ObjectDoesNotExist = "0x80040217";

    /// <summary>RecordNotFoundByEntityKey: no row with those alternate-key values.</summary>
    public const string RecordNotFoundByEntityKey = "0x80060891";

    /// <summary>DuplicateRecord: "Operation failed due to a SQL integrity violation." (also sent as 412).</summary>
    public const string DuplicateRecord = "0x80040237";

    /// <summary>DuplicateRecordEntityKey: an alternate key (entity key) would be violated.</summary>
    public const string DuplicateRecordEntityKey = "0x80060892";

    /// <summary>ConcurrencyVersionMismatch (412): the row changed since the ETag sent in If-Match.</summary>
    public const string ConcurrencyVersionMismatch = "0x80060882";

    // ---- Platform faults that clear by themselves ---------------------------------------------------
    // The codes below are listed by Microsoft; the production evidence is the message fragments
    // further down, because the SDK reported these faults without a distinguishing code.
    private static readonly string[] TransientCodes =
    [
        "0x80044151", // unManagedidssqltimeouterror: SQL timeout expired.
        "0x80048564", // DatabaseUnavailable: "This operation should be retried later."
        "0x8004A001", // CrmSqlGovernorDatabaseRequestDenied: the server is busy.
        "0x80073000", // CrmSQLNetworkingError: a networking error, try again.
        "0x80072323", // AzureSqlDatabaseResourceLimitExceededError.
        "0x80072324", // AzureSqlMaxConcurrentWorkersLimitExceededError.
        "0x80048559", // DBConnectionOpenCircuitBreakerThrottling.
        "0x80048553", // DBUpgradeCauseTimeout: the database is being upgraded.
    ];

    // Observed in production on faults that carried no usable code: a short SQL backend outage
    // ("... is not currently available. Please retry the connection later.") and SQL contention.
    // Deliberately narrower than "timeout" or "lock", which also match permanent errors and field names.
    private static readonly string[] TransientMessageFragments =
    [
        "should be retried later",
        "is currently unavailable",
        "is not currently available",
        "deadlock",
        "There is no active transaction",
    ];

    /// <summary>Reads <c>error.code</c> and <c>error.message</c>; both are null when the body is not an OData error.</summary>
    public static (string? Code, string? Message) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (ReadString(error, "code"), ReadString(error, "message"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Classifies a failed response. Call it only for non-success status codes.</summary>
    public static DataverseFailureKind Classify(int statusCode, string? code, string? message)
    {
        if (statusCode == 429 || Is(code, RequestCountLimit, ExecutionTimeLimit, ConcurrentRequestLimit))
            return DataverseFailureKind.Throttled;
        if (IsDuplicateKey(code, message))
            return DataverseFailureKind.DuplicateKey;
        if (statusCode is 408 or >= 500 || IsTransient(code, message))
            return DataverseFailureKind.Transient;
        if (statusCode == 404 || Is(code, ObjectDoesNotExist, RecordNotFoundByEntityKey))
            return DataverseFailureKind.NotFound;
        return DataverseFailureKind.Rejected;
    }

    /// <summary>
    /// True for "a row with this key already exists": the duplicate codes, or the message Dataverse
    /// sends with <c>412</c> for <c>If-None-Match: *</c> (that message was also met in production).
    /// </summary>
    public static bool IsDuplicateKey(string? code, string? message) =>
        Is(code, DuplicateRecord, DuplicateRecordEntityKey)
        || (message?.Contains("A record with matching key values already exists", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>True for platform faults that a later attempt usually survives.</summary>
    public static bool IsTransient(string? code, string? message) =>
        (code is not null && Array.Exists(TransientCodes, c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
        || (message is not null && Array.Exists(TransientMessageFragments, f => message.Contains(f, StringComparison.OrdinalIgnoreCase)));

    // Dataverse writes hex codes in mixed case ("0x80048d19"), so compare case-insensitively.
    private static bool Is(string? code, params string[] candidates) =>
        code is not null && Array.Exists(candidates, c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
