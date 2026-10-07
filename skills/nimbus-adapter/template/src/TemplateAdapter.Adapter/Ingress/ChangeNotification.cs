namespace TemplateAdapter.Adapter.Ingress;

/// <summary>
/// A thin change notification on the ingress queue: which record changed, how, and when. It carries
/// no data. The adapter re-reads the record, so a notification can never publish stale or partial
/// data. ExternalSystem's trigger (a plug-in step, a business event, a webhook relay) sends it, and
/// filters out changes made by the integration's own identity (echo prevention).
/// <para>JSON: <c>{"entity":"account","key":"ACC-1001","changeType":"updated","changedOn":"2026-01-15T09:45:00Z"}</c>.</para>
/// </summary>
/// <param name="Entity">The entity, e.g. <c>account</c>.</param>
/// <param name="Key">The record's external key.</param>
/// <param name="ChangeType"><c>created</c>, <c>updated</c> or <c>deleted</c>.</param>
/// <param name="ChangedOn">When the change happened; part of the deterministic MessageId.</param>
public sealed record ChangeNotification(string? Entity, string? Key, string? ChangeType, DateTimeOffset ChangedOn);
