using Azure.Messaging.ServiceBus.Administration;
using Sbzilla.Models;
using Sbzilla.ServiceBus;
using Sbzilla.Services;

namespace Sbzilla.Tests;

public class ReadOnlyTests
{
    // Points at a namespace that doesn't exist: the guard must throw before anything is sent.
    private const string Key = "Endpoint=sb://sbzilla-readonly-test.servicebus.windows.net/;SharedAccessKeyName=Root;SharedAccessKey=abc=";

    private static readonly EntityRef Queue = EntityRef.Queue("orders");
    private static readonly EntityRef Subscription = EntityRef.Subscription("events", "audit");

    public static TheoryData<string> WriteOperations => new(Writes.Keys);

    private static readonly Dictionary<string, Func<NamespaceConnection, Task>> Writes = new()
    {
        ["send"] = c => ServiceBusOperations.SendAsync(c, "orders", new MessageDraft { Body = "x" }, 1, null, CancellationToken.None),
        ["resubmit"] = c => ServiceBusOperations.ResubmitAsync(c, Queue, MessageSource.DeadLetter, false, [], true, null, CancellationToken.None),
        ["delete messages"] = c => ServiceBusOperations.DeleteAsync(c, Queue, MessageSource.Active, false, [new(1, null)], null, CancellationToken.None),
        ["purge"] = c => ServiceBusOperations.PurgeAsync(c, Queue, MessageSource.Active, false, null, CancellationToken.None),
        ["create entity"] = c => ServiceBusOperations.CreateAsync(c, new CreateQueueOptions("new"), null, CancellationToken.None),
        ["delete entity"] = c => ServiceBusOperations.DeleteEntityAsync(c, Queue, CancellationToken.None),
        ["update properties"] = c => ServiceBusOperations.UpdatePropertiesAsync(c, new object(), CancellationToken.None),
        ["create rule"] = c => ServiceBusOperations.CreateRuleAsync(c, Subscription, new CreateRuleOptions("r"), CancellationToken.None),
        ["delete rule"] = c => ServiceBusOperations.DeleteRuleAsync(c, Subscription, "r", CancellationToken.None),
    };

    [Theory]
    [MemberData(nameof(WriteOperations))]
    public async Task WritesAreRefusedForReadOnlyNamespaces(string operation)
    {
        var ns = new NamespaceConnection { Name = "Prod", ConnectionString = Key, IsReadOnly = true };

        await Assert.ThrowsAsync<ReadOnlyNamespaceException>(() => Writes[operation](ns));
    }

    [Fact]
    public void StoreKeepsNamespacesInStepWithTheirGroup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sbzilla-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConnectionStore(dir);
            var group = new NamespaceGroup { Name = "Prod", IsReadOnly = true };
            var ns = new NamespaceConnection { Name = "Main", ConnectionString = Key };
            group.Namespaces.Add(ns);

            store.Save([group]);
            var savedReadOnly = ns.IsReadOnly;
            var loaded = Assert.Single(Assert.Single(store.Load()).Namespaces);

            group.IsReadOnly = false;
            store.Save([group]);

            Assert.True(savedReadOnly);
            Assert.True(loaded.IsReadOnly);
            Assert.False(ns.IsReadOnly);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
