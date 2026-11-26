using System.Text.Json;
using Sbzilla.ServiceBus;

namespace Sbzilla.Services;

/// <summary>An open entity window, as remembered between runs.</summary>
public sealed class SessionTab
{
    public string NamespaceId { get; set; } = "";
    public EntityKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Topic { get; set; }
    public MessageSource Source { get; set; }
}

public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int SelectedIndex { get; set; }
}

/// <summary>Persists open entity windows to %LOCALAPPDATA%\Sbzilla\session.json.</summary>
public sealed class SessionStore
{
    public SessionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? ConnectionStore.DefaultRoot;
    }

    public string RootDirectory { get; }
    private string FilePath => Path.Combine(RootDirectory, "session.json");

    public SessionState Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath), ConnectionStore.JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    public void Save(SessionState state)
    {
        Directory.CreateDirectory(RootDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, ConnectionStore.JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
