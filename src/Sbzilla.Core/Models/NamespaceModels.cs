using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Sbzilla.Services;

namespace Sbzilla.Models;

public enum AuthMode
{
    ConnectionString,
    EntraInteractive,
    EntraDefault,
    EntraServicePrincipal,
}

/// <summary>A named, coloured set of namespaces (e.g. Production, Develop). Entity windows take on the group's colour.</summary>
public sealed class NamespaceGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Group";

    /// <summary>#RRGGBB.</summary>
    public string Color { get; set; } = "#2EA043";

    /// <summary>Browse and peek only: sending, deleting, purging, resubmitting and editing entities are disabled.</summary>
    public bool IsReadOnly { get; set; }

    public List<NamespaceConnection> Namespaces { get; set; } = new();

    /// <summary>The name with a "(read-only)" suffix, unless the name already says so.</summary>
    [JsonIgnore]
    public string LabelWithReadOnly => IsReadOnly && !Name.Contains("read", StringComparison.OrdinalIgnoreCase) ? $"{Name} (read-only)" : Name;
}

/// <summary>
/// One Service Bus namespace. Secrets are held in memory as plain text and written to disk encrypted with DPAPI (current user).
/// </summary>
public sealed class NamespaceConnection
{
    public const string DefaultSuffix = ".servicebus.windows.net";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Optional display name; the tree falls back to the namespace name.</summary>
    public string Name { get; set; } = "";

    public AuthMode AuthMode { get; set; } = AuthMode.ConnectionString;

    /// <summary>Namespace host for Entra sign-in, e.g. "contoso" or "contoso.servicebus.windows.net".</summary>
    public string Namespace { get; set; } = "";

    /// <summary>Optional for interactive/default sign-in; required for a service principal.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Application (client) id for a service principal, or a login hint for interactive sign-in.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>AMQP over WebSockets (port 443) for networks that block AMQP's port 5671.</summary>
    public bool UseWebSockets { get; set; }

    /// <summary><see cref="ServiceBus.EntityRef.Key"/>s of the queues, topics and subscriptions pinned under Favourites.</summary>
    public List<string> Favorites { get; set; } = new();

    [JsonIgnore] public string ClientSecret { get; set; } = "";
    [JsonIgnore] public string ConnectionString { get; set; } = "";

    [JsonPropertyName("clientSecret")]
    public string ProtectedClientSecret
    {
        get => SecretProtector.Protect(ClientSecret);
        set => ClientSecret = SecretProtector.Unprotect(value);
    }

    [JsonPropertyName("connectionString")]
    public string ProtectedConnectionString
    {
        get => SecretProtector.Protect(ConnectionString);
        set => ConnectionString = SecretProtector.Unprotect(value);
    }

    /// <summary>The fully qualified namespace, e.g. "contoso.servicebus.windows.net" (or "" if it can't be worked out).</summary>
    [JsonIgnore]
    public string ResolvedNamespace
    {
        get
        {
            if (AuthMode != AuthMode.ConnectionString) return NormalizeNamespace(Namespace);
            try { return ServiceBusConnectionStringProperties.Parse(ConnectionString).FullyQualifiedNamespace ?? ""; }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return ""; }
        }
    }

    /// <summary>An entity-scoped connection string's EntityPath, else "".</summary>
    [JsonIgnore]
    public string ResolvedEntityPath
    {
        get
        {
            if (AuthMode != AuthMode.ConnectionString) return "";
            try { return ServiceBusConnectionStringProperties.Parse(ConnectionString).EntityPath ?? ""; }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return ""; }
        }
    }

    [JsonIgnore]
    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : ResolvedNamespace.Length > 0 ? ShortName : "(no namespace)";

    /// <summary>"contoso.servicebus.windows.net" becomes "contoso".</summary>
    [JsonIgnore]
    public string ShortName
    {
        get
        {
            var ns = ResolvedNamespace;
            return ns.EndsWith(DefaultSuffix, StringComparison.OrdinalIgnoreCase) ? ns[..^DefaultSuffix.Length] : ns;
        }
    }

    /// <summary>Accepts "contoso", "contoso.servicebus.windows.net", "sb://contoso.servicebus.windows.net/" or an https URL.</summary>
    public static string NormalizeNamespace(string? value)
    {
        var s = (value ?? "").Trim();
        if (s.Length == 0) return "";
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Host.Length > 0) s = uri.Host;
        s = s.TrimEnd('/');
        return s.Contains('.') ? s.ToLowerInvariant() : s.ToLowerInvariant() + DefaultSuffix;
    }

    public NamespaceConnection Clone() => new()
    {
        Id = Id,
        Name = Name,
        AuthMode = AuthMode,
        Namespace = Namespace,
        TenantId = TenantId,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        ConnectionString = ConnectionString,
        UseWebSockets = UseWebSockets,
        Favorites = new List<string>(Favorites),
    };

    public static string AuthLabel(AuthMode mode) => mode switch
    {
        AuthMode.ConnectionString => "Connection string (shared access key)",
        AuthMode.EntraInteractive => "Microsoft Entra interactive (MFA)",
        AuthMode.EntraDefault => "Microsoft Entra default (Azure CLI / Visual Studio)",
        AuthMode.EntraServicePrincipal => "Microsoft Entra service principal",
        _ => mode.ToString(),
    };
}
