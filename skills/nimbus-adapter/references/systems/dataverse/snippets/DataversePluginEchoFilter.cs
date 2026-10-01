namespace TemplateAdapter.Adapter.Clients;

// Dataverse pack snippet (see ../README.md, "Echo-loop prevention").
//
// The decision a Dataverse plug-in (or the adapter's ingress, reading the execution context JSON) makes
// before it announces a change. Plain C# with no Dataverse SDK reference, so it is unit-testable and can
// be copied into a plug-in project unchanged (a .NET Framework 4.6.2 plug-in project needs
// <LangVersion>10</LangVersion> or later for the file-scoped namespace and nullable annotations).
//
// In the plug-in, convert SDK values before calling Evaluate: EntityReference -> its Id (Guid),
// OptionSetValue -> its Value (int), Money -> its Value (decimal), OptionSetValueCollection -> a sorted
// int[] joined to a string. Read the integration user's systemuserid from an environment variable,
// never from code.

/// <summary>Why a change is, or is not, announced to the bus.</summary>
public enum EchoFilterDecision
{
    /// <summary>A person (or another system) changed a column the integration cares about: announce it.</summary>
    Publish,

    /// <summary>The adapter's own application user made the change: it is the echo of a bus message.</summary>
    SkipIntegrationUser,

    /// <summary>An update touched none of the relevant columns (or wrote the values they already had).</summary>
    SkipNoRelevantChange,
}

/// <summary>
/// Source-side echo filter. Two checks, both observed in production Dataverse plug-ins:
/// <list type="number">
///   <item>The initiating user is the integration's application user: the change came FROM the bus,
///   so announcing it would send it straight back.</item>
///   <item>On Update, no relevant column changed between the pre-image and the post-image. Filtering
///   attributes on the step are not enough: they fire when a column is IN the request, even with the
///   value it already had.</item>
/// </list>
/// </summary>
public static class DataversePluginEchoFilter
{
    /// <summary>Decides whether to announce one Create, Update or Delete.</summary>
    /// <param name="initiatingUserId"><c>IExecutionContext.InitiatingUserId</c> (not <c>UserId</c>, which a step's
    /// "run in user's context" setting replaces).</param>
    /// <param name="integrationUserId">The <c>systemuserid</c> of the adapter's application user.</param>
    /// <param name="messageName"><c>IExecutionContext.MessageName</c>: Create, Update or Delete.</param>
    /// <param name="preImage">Pre-image columns (Update and Delete), converted to plain values; null when not registered.</param>
    /// <param name="postImage">Post-image columns (Create and Update), converted to plain values; null when not registered.</param>
    /// <param name="relevantColumns">Logical names of the columns the event carries. Empty means every column is relevant.</param>
    public static EchoFilterDecision Evaluate(
        Guid initiatingUserId,
        Guid integrationUserId,
        string messageName,
        IReadOnlyDictionary<string, object?>? preImage,
        IReadOnlyDictionary<string, object?>? postImage,
        IReadOnlyCollection<string> relevantColumns)
    {
#pragma warning disable CA1510 // ArgumentNullException.ThrowIfNull does not exist on .NET Framework (plug-ins).
        if (relevantColumns is null)
            throw new ArgumentNullException(nameof(relevantColumns));
#pragma warning restore CA1510

        // Fail closed: without the integration user, every adapter write would echo.
        if (integrationUserId == Guid.Empty)
            throw new ArgumentException("The integration user's systemuserid is not configured.", nameof(integrationUserId));

        if (initiatingUserId == integrationUserId)
            return EchoFilterDecision.SkipIntegrationUser;

        // Create and Delete are always news; only an Update can be a no-op.
        if (!string.Equals(messageName, "Update", StringComparison.OrdinalIgnoreCase) || relevantColumns.Count == 0)
            return EchoFilterDecision.Publish;

        // Without both images there is nothing to compare: announce rather than lose a change.
        if (preImage is null || postImage is null)
            return EchoFilterDecision.Publish;

        foreach (var column in relevantColumns)
        {
            preImage.TryGetValue(column, out var before);
            postImage.TryGetValue(column, out var after);
            if (!Equals(before, after))
                return EchoFilterDecision.Publish;
        }

        return EchoFilterDecision.SkipNoRelevantChange;
    }
}
