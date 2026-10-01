#pragma warning disable CA1032 // Only handlers create it, always with the remediation text.
namespace TemplateAdapter.Adapter.Handlers;

/// <summary>
/// A record the message refers to (a parent account, an owner, a price list) does not exist in
/// ExternalSystem yet. NimBus retries it a few times, because the referenced record may be on its way
/// in another session; after that the message fails and its session blocks until an operator syncs
/// the reference and resubmits. The message says exactly what to do.
/// </summary>
public sealed class MissingReferenceException(string referenceKind, string referenceKey, string remedy)
    : Exception($"The {referenceKind} '{referenceKey}' does not exist in ExternalSystem. {remedy}")
{
    /// <summary>What kind of record is missing, e.g. "parent account".</summary>
    public string ReferenceKind { get; } = referenceKind;

    /// <summary>The key of the missing record.</summary>
    public string ReferenceKey { get; } = referenceKey;
}
