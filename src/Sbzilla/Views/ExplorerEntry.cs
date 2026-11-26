using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Sbzilla.Models;
using Sbzilla.ServiceBus;

namespace Sbzilla.Views;

public enum ExplorerEntryKind { Group, Namespace, FavoritesFolder, QueuesFolder, TopicsFolder, Queue, Topic, Subscription, Placeholder }

/// <summary>
/// View-model for one row in the namespace explorer:
/// group → namespace → Favourites / Queues / Topics → queue, or topic → subscription.
/// </summary>
public sealed class ExplorerEntry : ObservableObject
{
    private bool _isExpanded;
    private string _label;
    private string _detail = "";

    private ExplorerEntry(ExplorerEntryKind kind, NamespaceGroup group, NamespaceConnection? ns, string label)
    {
        Kind = kind;
        Group = group;
        Namespace = ns;
        _label = label;
    }

    public static ExplorerEntry ForGroup(NamespaceGroup g) => new(ExplorerEntryKind.Group, g, null, g.Name);

    public static ExplorerEntry ForNamespace(NamespaceGroup g, NamespaceConnection ns)
    {
        var entry = new ExplorerEntry(ExplorerEntryKind.Namespace, g, ns, ns.DisplayName);
        entry.Children.Add(Placeholder(g, ns, "Loading…"));
        return entry;
    }

    public static ExplorerEntry Folder(NamespaceGroup g, NamespaceConnection ns, bool topics, int count) =>
        new(topics ? ExplorerEntryKind.TopicsFolder : ExplorerEntryKind.QueuesFolder, g, ns, topics ? "Topics" : "Queues")
        {
            _detail = $"({count:N0})",
        };

    public static ExplorerEntry Favorites(NamespaceGroup g, NamespaceConnection ns, int count) =>
        new(ExplorerEntryKind.FavoritesFolder, g, ns, "Favourites") { _detail = $"({count:N0})" };

    /// <param name="scope">
    /// Set for copies listed under Favourites, so their ids differ from the same entity under Queues / Topics.
    /// </param>
    /// <param name="fullName">Label subscriptions "topic/subscription" (used under Favourites).</param>
    public static ExplorerEntry ForEntity(NamespaceGroup g, NamespaceConnection ns, EntityInfo info, string? scope = null, bool fullName = false)
    {
        var kind = info.Ref.Kind switch
        {
            EntityKind.Queue => ExplorerEntryKind.Queue,
            EntityKind.Topic => ExplorerEntryKind.Topic,
            _ => ExplorerEntryKind.Subscription,
        };
        return new ExplorerEntry(kind, g, ns, fullName ? info.Ref.DisplayName : info.Ref.Name) { Info = info, Scope = scope, _detail = info.Status == "unknown" ? "" : info.CountsLabel };
    }

    public static ExplorerEntry Placeholder(NamespaceGroup g, NamespaceConnection? ns, string text) =>
        new(ExplorerEntryKind.Placeholder, g, ns, text);

    public ExplorerEntryKind Kind { get; }
    public NamespaceGroup Group { get; }
    public NamespaceConnection? Namespace { get; }
    public EntityInfo? Info { get; private init; }
    public EntityRef? Entity => Info?.Ref;
    public string? Scope { get; private init; }
    public bool IsFavorite => Entity is { } e && Namespace!.Favorites.Contains(e.Key);
    public ObservableCollection<ExplorerEntry> Children { get; } = new();

    /// <summary>True once a namespace's entities have been fetched (or are being fetched).</summary>
    public bool Loaded { get; set; }

    public string Id => Kind switch
    {
        ExplorerEntryKind.Group => Group.Id,
        ExplorerEntryKind.Namespace => Namespace!.Id,
        ExplorerEntryKind.FavoritesFolder => $"{Namespace!.Id}/favorites",
        ExplorerEntryKind.QueuesFolder => $"{Namespace!.Id}/queues",
        ExplorerEntryKind.TopicsFolder => $"{Namespace!.Id}/topics",
        ExplorerEntryKind.Placeholder => $"{Namespace?.Id}/placeholder/{Label}",
        _ => Scope != null ? $"{Namespace!.Id}/{Scope}/{Entity!.Key}" : $"{Namespace!.Id}/{Entity!.Key}",
    };

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public string Detail
    {
        get => _detail;
        set
        {
            if (SetProperty(ref _detail, value)) OnPropertyChanged(nameof(DetailVisibility));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsEntity => Kind is ExplorerEntryKind.Queue or ExplorerEntryKind.Topic or ExplorerEntryKind.Subscription;
    public bool IsFolder => Kind is ExplorerEntryKind.FavoritesFolder or ExplorerEntryKind.QueuesFolder or ExplorerEntryKind.TopicsFolder;

    public string Glyph => Kind switch
    {
        ExplorerEntryKind.Namespace => "\uE753",        // cloud
        ExplorerEntryKind.FavoritesFolder => "\uE734",  // star
        ExplorerEntryKind.QueuesFolder or ExplorerEntryKind.TopicsFolder => "\uE8B7",
        ExplorerEntryKind.Queue => "\uE8C3",            // list
        ExplorerEntryKind.Topic => "\uE789",            // broadcast
        ExplorerEntryKind.Subscription => "\uE8F1",     // inbox-ish
        _ => "",
    };

    public Visibility SwatchVisibility => Kind == ExplorerEntryKind.Group ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadOnlyVisibility => Kind == ExplorerEntryKind.Group && Group.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public double LabelOpacity => Kind == ExplorerEntryKind.Placeholder ? 0.6 : Info?.Status is "Disabled" ? 0.55 : 1;
    public Windows.UI.Text.FontWeight LabelWeight => Kind == ExplorerEntryKind.Group ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public Brush GroupBrush => GroupColors.Brush(Group.Color);

    /// <summary>Icons take the group colour so every row shows which environment it belongs to.</summary>
    public Brush GlyphBrush => GroupColors.Brush(Group.Color);

    public string ToolTip => Info is { } i && i.Ref.HasMessages
        ? $"{i.Ref.DisplayName}\nActive {i.ActiveCount:N0} · Dead-letter {i.DeadLetterCount:N0} · Scheduled {i.ScheduledCount:N0}" +
          (i.RequiresSession ? "\nSession-enabled" : "") + (i.ForwardTo.Length > 0 ? $"\nForwards to {i.ForwardTo}" : "") +
          (i.Status is "Disabled" ? "\nDisabled" : "")
        : Label;

    public override string ToString() => Label;
}
