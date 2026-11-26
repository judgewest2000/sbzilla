using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Sbzilla.Models;

namespace Sbzilla.ServiceBus;

/// <summary>
/// One messaging client and one administration client per namespace, shared by every window so that Entra sign-in
/// happens once per namespace per session. Editing a namespace replaces its clients.
/// </summary>
public static partial class ServiceBusClients
{
    private sealed record Entry(string Fingerprint, ServiceBusClient Client, ServiceBusAdministrationClient Admin);

    private static readonly ConcurrentDictionary<string, Entry> s_entries = new();
    private static readonly object s_gate = new();

    /// <summary>Interactive (WAM) sign-in parents its window to this handle.</summary>
    public static Func<IntPtr>? ParentWindow { get; set; }

    public static ServiceBusClient Client(NamespaceConnection c) => Get(c).Client;

    public static ServiceBusAdministrationClient Admin(NamespaceConnection c) => Get(c).Admin;

    /// <summary>Drops the cached clients for a namespace (e.g. after it was edited or deleted).</summary>
    public static void Reset(string namespaceId)
    {
        if (s_entries.TryRemove(namespaceId, out var old)) _ = old.Client.DisposeAsync().AsTask();
    }

    private static string Fingerprint(NamespaceConnection c) =>
        string.Join("\u0001", c.AuthMode, c.ResolvedNamespace, c.ConnectionString, c.TenantId, c.ClientId, c.ClientSecret, c.UseWebSockets);

    private static Entry Get(NamespaceConnection c)
    {
        var fingerprint = Fingerprint(c);
        if (s_entries.TryGetValue(c.Id, out var e) && e.Fingerprint == fingerprint) return e;
        lock (s_gate)
        {
            if (s_entries.TryGetValue(c.Id, out e) && e.Fingerprint == fingerprint) return e;
            var created = Create(c, fingerprint);
            if (e != null) _ = e.Client.DisposeAsync().AsTask();
            s_entries[c.Id] = created;
            return created;
        }
    }

    private static Entry Create(NamespaceConnection c, string fingerprint)
    {
        var options = new ServiceBusClientOptions
        {
            TransportType = c.UseWebSockets ? ServiceBusTransportType.AmqpWebSockets : ServiceBusTransportType.AmqpTcp,
            Identifier = "Sbzilla",
        };
        if (c.AuthMode == AuthMode.ConnectionString)
        {
            if (c.ConnectionString.Trim().Length == 0) throw new InvalidOperationException("The namespace has no connection string.");
            var cs = WithoutEntityPath(c.ConnectionString);
            return new Entry(fingerprint, new ServiceBusClient(cs, options), new ServiceBusAdministrationClient(cs));
        }

        var ns = c.ResolvedNamespace;
        if (ns.Length == 0) throw new InvalidOperationException("The namespace has no host name.");
        var credential = CreateCredential(c);
        return new Entry(fingerprint, new ServiceBusClient(ns, credential, options), new ServiceBusAdministrationClient(ns, credential));
    }

    private static TokenCredential CreateCredential(NamespaceConnection c)
    {
        var tenant = c.TenantId.Trim().Length > 0 ? c.TenantId.Trim() : null;
        switch (c.AuthMode)
        {
            case AuthMode.EntraInteractive:
                var interactive = new InteractiveBrowserCredentialBrokerOptions(ParentWindow?.Invoke() ?? IntPtr.Zero)
                {
                    TenantId = tenant,
                    LoginHint = c.ClientId.Trim().Length > 0 ? c.ClientId.Trim() : null,
                    // Signs in silently as the Windows account when it can; only prompts when it must.
                    UseDefaultBrokerAccount = c.ClientId.Trim().Length == 0,
                };
                return new InteractiveBrowserCredential(interactive);
            case AuthMode.EntraDefault:
                return new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenant });
            case AuthMode.EntraServicePrincipal:
                if (tenant == null || c.ClientId.Trim().Length == 0)
                    throw new InvalidOperationException("A service principal needs a tenant id, client id and client secret.");
                return new ClientSecretCredential(tenant, c.ClientId.Trim(), c.ClientSecret);
            default:
                throw new ArgumentOutOfRangeException(nameof(c));
        }
    }

    /// <summary>
    /// Entity-scoped connection strings (EntityPath=…) can't browse a namespace; the explorer uses the rest of the string
    /// and lets Service Bus refuse what the key isn't allowed to do.
    /// </summary>
    internal static string WithoutEntityPath(string connectionString) =>
        EntityPathPattern().Replace(connectionString.Trim(), "").Trim(';') + ";";

    [GeneratedRegex(@"(^|;)\s*EntityPath\s*=[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex EntityPathPattern();
}
