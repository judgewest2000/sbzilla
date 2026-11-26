using Sbzilla.Models;
using Sbzilla.ServiceBus;
using Sbzilla.Services;

namespace Sbzilla.Tests;

public class ConnectionTests
{
    private const string Key = "Endpoint=sb://Contoso.servicebus.windows.net/;SharedAccessKeyName=Root;SharedAccessKey=abc=";

    [Theory]
    [InlineData("contoso", "contoso.servicebus.windows.net")]
    [InlineData("Contoso.servicebus.windows.net", "contoso.servicebus.windows.net")]
    [InlineData("sb://contoso.servicebus.windows.net/", "contoso.servicebus.windows.net")]
    [InlineData("https://contoso.servicebus.usgovcloudapi.net", "contoso.servicebus.usgovcloudapi.net")]
    [InlineData("  ", "")]
    public void NormalizesNamespaces(string input, string expected) =>
        Assert.Equal(expected, NamespaceConnection.NormalizeNamespace(input));

    [Fact]
    public void ResolvesNamespaceAndEntityFromConnectionString()
    {
        var c = new NamespaceConnection { ConnectionString = Key + ";EntityPath=orders" };

        Assert.Equal("contoso.servicebus.windows.net", c.ResolvedNamespace, ignoreCase: true);
        Assert.Equal("orders", c.ResolvedEntityPath);
        Assert.Equal("contoso", c.DisplayName, ignoreCase: true);
    }

    [Fact]
    public void BadConnectionStringResolvesToNothing()
    {
        var c = new NamespaceConnection { ConnectionString = "not a connection string" };

        Assert.Equal("", c.ResolvedNamespace);
        Assert.Equal("(no namespace)", c.DisplayName);
    }

    [Theory]
    [InlineData(Key + ";EntityPath=orders")]
    [InlineData("EntityPath=orders;" + Key)]
    [InlineData(Key + ";entitypath = orders;")]
    public void StripsEntityPath(string connectionString) =>
        Assert.Equal(Key + ";", ServiceBusClients.WithoutEntityPath(connectionString));

    [Fact]
    public void StoreRoundTripsAndEncryptsSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sbzilla-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConnectionStore(dir);
            var group = new NamespaceGroup { Name = "Prod", Color = "#C42B1C", IsReadOnly = true };
            group.Namespaces.Add(new NamespaceConnection { Name = "Main", ConnectionString = Key, Favorites = ["queue:orders", "subscription:events/subscriptions/audit"] });
            group.Namespaces.Add(new NamespaceConnection { AuthMode = AuthMode.EntraServicePrincipal, Namespace = "contoso", ClientSecret = "s3cret" });

            store.Save([group]);
            var json = File.ReadAllText(Path.Combine(dir, "connections.json"));
            var loaded = Assert.Single(store.Load());

            Assert.DoesNotContain("abc=", json);
            Assert.DoesNotContain("s3cret", json);
            Assert.Equal(("Prod", true), (loaded.Name, loaded.IsReadOnly));
            Assert.Equal(Key, loaded.Namespaces[0].ConnectionString);
            Assert.Equal(["queue:orders", "subscription:events/subscriptions/audit"], loaded.Namespaces[0].Favorites);
            Assert.Empty(loaded.Namespaces[1].Favorites);
            Assert.Equal((AuthMode.EntraServicePrincipal, "s3cret"), (loaded.Namespaces[1].AuthMode, loaded.Namespaces[1].ClientSecret));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
