#pragma warning disable CA1707, CA2007
using System.Reflection;
using NimBus.Core;
using NimBus.Core.Events;
using TemplateAdapter.Contracts;

namespace TemplateAdapter.Tests;

/// <summary>
/// Catalog rules that NimBus does not enforce at compile time but that break integrations at run
/// time. They run against every event in the catalog, so new events are covered automatically.
/// </summary>
[TestClass]
public sealed class CatalogTests
{
    private static readonly AdapterCatalog Catalog = new();

    private static IEnumerable<Type> MessageTypes() =>
        Catalog.EventTypes.Select(t => t.GetEventClassType()).OfType<Type>();

    [TestMethod]
    public void EveryCommand_HasExactlyOneConsumer()
    {
        var errors = PlatformValidation.ValidateCommandConsumers(Catalog);

        Assert.IsEmpty(errors, string.Join("; ", errors));
    }

    [TestMethod]
    public void EventTypeIds_AreUnique()
    {
        // The EventTypeId is the unqualified class name and is global to the Service Bus namespace:
        // two classes with the same name in different namespaces collide and cross-route. Add the
        // other catalogs that share your namespace to this check.
        var duplicates = MessageTypes().GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);

        Assert.IsEmpty(duplicates.ToArray(), string.Join(", ", duplicates));
    }

    [TestMethod]
    public void EveryEvent_HasASessionKeyOnARealProperty()
    {
        // Without a session key an event gets a random session, so changes to the same record can
        // be applied out of order. If an event really has no ordering needs (a read-only request),
        // exempt it here explicitly, with the reason.
        foreach (var type in MessageTypes().Where(t => typeof(Event).IsAssignableFrom(t)))
        {
            var sessionKey = type.GetCustomAttribute<SessionKeyAttribute>(inherit: true);
            Assert.IsNotNull(sessionKey, $"{type.Name} has no [SessionKey].");
            var property = type.GetProperty(sessionKey.PropertyName);
            Assert.IsNotNull(property, $"{type.Name}'s [SessionKey] names '{sessionKey.PropertyName}', which is not a property.");

            // GetSessionId() falls back to a random session when the key is null. [Required] makes
            // the publisher reject such an event instead of silently losing the ordering.
            Assert.IsNotNull(
                property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>(),
                $"{type.Name}.{property.Name} is the session key, so it must be [Required].");
        }
    }

    [TestMethod]
    public void EveryEvent_HasAValidExample()
    {
        foreach (var type in MessageTypes())
        {
            var example = type.GetField("Example", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Event;
            Assert.IsNotNull(example, $"{type.Name} has no static Example (the NimBus WebApp shows it).");

            var result = example.TryValidate();
            Assert.IsTrue(result.IsValid, $"{type.Name}.Example is invalid.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(example.GetSessionId()), $"{type.Name}.Example has an empty session key.");
        }
    }

    [TestMethod]
    public void EventNames_StartWithTheProducingSystem()
    {
        foreach (var endpoint in Catalog.Endpoints)
        {
            var prefix = endpoint.Id.Replace("Endpoint", string.Empty, StringComparison.Ordinal);
            foreach (var produced in endpoint.EventTypesProduced)
                StringAssert.StartsWith(produced.Id, prefix, StringComparison.Ordinal, $"{produced.Id} is produced by {endpoint.Id}.");
        }
    }

    [TestMethod]
    public void EveryEvent_IsDescribed()
    {
        foreach (var type in MessageTypes())
        {
            Assert.IsNotNull(type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>(), $"{type.Name} has no [Description].");
        }
    }
}
