using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sbzilla.Models;

namespace Sbzilla.Services;

/// <summary>Persists namespace groups to %LOCALAPPDATA%\Sbzilla\connections.json.</summary>
public sealed class ConnectionStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConnectionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? DefaultRoot;
    }

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sbzilla");

    public string RootDirectory { get; }
    private string FilePath => Path.Combine(RootDirectory, "connections.json");

    /// <summary>Loads the groups; on first run creates Production / Develop / Read-only groups.</summary>
    public List<NamespaceGroup> Load()
    {
        var groups = LoadFile();
        ApplyReadOnly(groups);
        return groups;
    }

    private List<NamespaceGroup> LoadFile()
    {
        if (!File.Exists(FilePath)) return DefaultGroups();
        try
        {
            return JsonSerializer.Deserialize<List<NamespaceGroup>>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection rather than silently overwriting it on the next save.
            File.Copy(FilePath, FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return DefaultGroups();
        }
    }

    /// <summary>Saves the groups, first copying each group's read-only flag onto its namespaces (every edit is followed by a save).</summary>
    public void Save(IEnumerable<NamespaceGroup> groups)
    {
        var list = groups.ToList();
        ApplyReadOnly(list);
        Directory.CreateDirectory(RootDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static void ApplyReadOnly(IEnumerable<NamespaceGroup> groups)
    {
        foreach (var group in groups)
            foreach (var ns in group.Namespaces) ns.IsReadOnly = group.IsReadOnly;
    }

    public static List<NamespaceGroup> DefaultGroups() =>
    [
        new() { Name = "Production", Color = "#C42B1C" },
        new() { Name = "Develop", Color = "#2EA043" },
        new() { Name = "Read-only", Color = "#2575D6", IsReadOnly = true },
    ];
}
