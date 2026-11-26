using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sbzilla.Models;
using Sbzilla.Services;
using Sbzilla.ServiceBus;
using Sbzilla.Views;
using Windows.Graphics;
using Windows.System;

namespace Sbzilla;

public sealed partial class MainWindow : Window
{
    private readonly ConnectionStore _store = new();
    private readonly SessionStore _sessionStore = new();
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _restoring;
    private readonly List<NamespaceGroup> _groups;
    private readonly Dictionary<string, NamespaceSnapshot> _snapshots = new();
    /// <summary>Queues/Topics folders start expanded; this remembers the ones the user collapsed.</summary>
    private readonly HashSet<string> _collapsedFolders = new();
    private string _explorerFilter = "";
    private HashSet<string> _expandedBeforeSearch = new();

    public ObservableCollection<ExplorerEntry> RootEntries { get; } = new();
    private TreeView NamespaceTree = null!;

    /// <summary>Window handle for file pickers.</summary>
    public static IntPtr Handle { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "Sbzilla";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
        SizeAndCenter();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        TitleBarIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")));
        WelcomeArt.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AboutArt.png")));

        _groups = _store.Load();
        RebuildTree(expandGroups: true);
        RestoreSession();
        _sessionTimer.Tick += (_, _) => SaveSession();
        Tabs.SizeChanged += (_, _) => { foreach (var t in Tabs.TabItems.OfType<TabViewItem>()) t.MaxWidth = TabMaxWidth; };
        Root.Loaded += async (_, _) => await CheckForUpdateAsync();
        AppWindow.Closing += OnClosing;
    }

    private XamlRoot XamlRoot => Root.XamlRoot;

    private void SizeAndCenter()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = (int)(area.Width * 0.8);
        var height = (int)(area.Height * 0.85);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private EntityTab? CurrentTab => (Tabs.SelectedItem as TabViewItem)?.Content as EntityTab;

    private IEnumerable<(TabViewItem Tab, EntityTab Entity)> OpenTabs =>
        Tabs.TabItems.OfType<TabViewItem>().Where(t => t.Content is EntityTab).Select(t => (t, (EntityTab)t.Content));

    private void Save() => _store.Save(_groups);

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var update = await Dialogs.ShowAboutAsync(XamlRoot);
        if (update is not null) await Dialogs.OfferUpdateAsync(XamlRoot, update);
    }

    private async Task CheckForUpdateAsync()
    {
        UpdateInfo? update;
        try { update = await UpdateService.CheckAsync(CancellationToken.None); }
        catch { return; }
        if (update is not null) await Dialogs.OfferUpdateAsync(XamlRoot, update);
    }

    private (NamespaceGroup Group, NamespaceConnection Namespace)? FindNamespace(string id) =>
        _groups.SelectMany(g => g.Namespaces.Select(n => (g, n))).FirstOrDefault        (x => x.n.Id == id) is ({ } group, { } ns) ? (group, ns) : null;

    // ------------------------------------------------------------------ tree

    private void RebuildTree(bool expandGroups = false, ISet<string>? expanded = null)
    {
        expanded ??= _explorerFilter.Length > 0 ? new HashSet<string>() : ExpandedIds();
        var selectedId = (NamespaceTree?.SelectedItem as ExplorerEntry)?.Id;
        RootEntries.Clear();
        foreach (var entry in BuildExplorerEntries(_explorerFilter, expanded, expandGroups)) RootEntries.Add(entry);
        ReplaceNamespaceTree();
        EmptyHint.Text = _groups.Count == 0
            ? "No groups yet. Create a group (e.g. Production, Develop) and add namespaces to it."
            : $"Nothing matches \u201C{_explorerFilter}\u201D. Search only covers namespaces that have been expanded.";
        EmptyHint.Visibility = RootEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedId != null && VisibleEntries(RootEntries).FirstOrDefault(e => e.Id == selectedId) is { } selected) SelectWhenShown(selected);
    }

    /// <summary>
    /// Swaps in a new TreeView. Reusing one lets recycled item containers keep their old collapsed state,
    /// so after a search some matches stayed hidden inside collapsed nodes.
    /// </summary>
    private void ReplaceNamespaceTree()
    {
        if (NamespaceTree != null) NamespaceTree.ItemsSource = null;
        NamespaceTree = new TreeView
        {
            ItemsSource = RootEntries,
            ItemTemplate = (DataTemplate)Root.Resources["ExplorerItemTemplate"],
            SelectionMode = TreeViewSelectionMode.Single,
            CanDragItems = false,
            AllowDrop = false,
        };
        NamespaceTree.ItemInvoked += NamespaceTree_ItemInvoked;
        NamespaceTree.Expanding += NamespaceTree_Expanding;
        NamespaceTree.Collapsed += NamespaceTree_Collapsed;
        NamespaceTree.DoubleTapped += NamespaceTree_DoubleTapped;
        NamespaceTree.RightTapped += NamespaceTree_RightTapped;
        NamespaceTreeHost.Child = NamespaceTree;
    }

    /// <summary>
    /// Selects the entry once the tree has laid it out. Selecting an item inside a collapsed node makes WinUI
    /// spin forever, so this waits until every ancestor is really expanded, and gives up if it never is.
    /// </summary>
    private void SelectWhenShown(ExplorerEntry entry)
    {
        var tree = NamespaceTree;
        var passes = 0;
        void Select()
        {
            if (tree != NamespaceTree || !Flatten(RootEntries).Contains(entry)) return;   // rebuilt since
            if (ShownInTree(tree, RootEntries, entry)) tree.SelectedItem = entry;
            else if (++passes < 10) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Select);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Select);
    }

    /// <summary>True when every ancestor's tree item is actually expanded, not just marked as expanded.</summary>
    private static bool ShownInTree(TreeView tree, IEnumerable<ExplorerEntry> roots, ExplorerEntry entry)
    {
        List<ExplorerEntry>? PathTo(IEnumerable<ExplorerEntry> level)
        {
            foreach (var e in level)
            {
                if (ReferenceEquals(e, entry)) return new();
                if (PathTo(e.Children) is { } rest) { rest.Insert(0, e); return rest; }
            }
            return null;
        }
        return PathTo(roots) is { } path && path.All(p => tree.ContainerFromItem(p) is TreeViewItem { IsExpanded: true });
    }

    /// <summary>
    /// Entries not hidden inside a collapsed parent. Only these may be given to TreeView.SelectedItem:
    /// selecting a hidden item makes WinUI spin forever.
    /// </summary>
    private static IEnumerable<ExplorerEntry> VisibleEntries(IEnumerable<ExplorerEntry> entries) =>
        entries.SelectMany(e => new[] { e }.Concat(e.IsExpanded ? VisibleEntries(e.Children) : []));

    private HashSet<string> ExpandedIds() => new(Flatten(RootEntries).Where(e => e.IsExpanded).Select(e => e.Id));

    /// <summary>
    /// Builds explorer entries. With a filter, keeps groups, namespaces and (already loaded) entities whose names contain it,
    /// and expands everything so the matches are visible.
    /// </summary>
    private List<ExplorerEntry> BuildExplorerEntries(string filter, ISet<string> expanded, bool expandGroups)
    {
        var searching = filter.Length > 0;
        bool Matches(string text) => text.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var result = new List<ExplorerEntry>();
        foreach (var g in _groups)
        {
            var groupMatches = searching && Matches(g.Name);
            var groupEntry = ExplorerEntry.ForGroup(g);
            foreach (var ns in g.Namespaces)
            {
                var nsMatches = !searching || groupMatches || Matches(ns.DisplayName) || Matches(ns.ResolvedNamespace);
                var nsEntry = ExplorerEntry.ForNamespace(g, ns);
                if (_snapshots.TryGetValue(ns.Id, out var snapshot))
                {
                    var hits = FillNamespace(nsEntry, snapshot, nsMatches ? "" : filter, expanded);
                    if (!nsMatches && hits == 0) continue;
                }
                else if (!nsMatches) continue;
                // While searching, open namespaces that are only listed because of a matching entity.
                nsEntry.IsExpanded = nsEntry.Loaded && (searching ? !nsMatches : expanded.Contains(ns.Id));
                groupEntry.Children.Add(nsEntry);
            }
            if (searching && !groupMatches && groupEntry.Children.Count == 0) continue;
            groupEntry.IsExpanded = searching || expandGroups || expanded.Contains(g.Id);
            result.Add(groupEntry);
        }
        return result;
    }

    /// <summary>Fills a namespace's Queues/Topics folders, keeping only entities matching <paramref name="filter"/>. Returns how many entities are listed.</summary>
    private int FillNamespace(ExplorerEntry nsEntry, NamespaceSnapshot snapshot, string filter, ISet<string> expanded)
    {
        var searching = filter.Length > 0;
        bool Matches(string text) => !searching || text.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var g = nsEntry.Group;
        var ns = nsEntry.Namespace!;
        nsEntry.Children.Clear();
        nsEntry.Loaded = true;

        var queues = snapshot.Queues.Where(q => Matches(q.Ref.Name)).ToList();
        var topics = snapshot.Topics
            .Select(t => (Topic: t, Subs: Matches(t.Ref.Name) ? t.Subscriptions : t.Subscriptions.Where(s => Matches(s.Ref.Name)).ToList()))
            .Where(x => Matches(x.Topic.Ref.Name) || x.Subs.Count > 0)
            .ToList();

        // Favourites first, just under the namespace. Ones whose entity no longer exists are kept but not shown.
        var entitiesByKey = snapshot.All.DistinctBy(e => e.Ref.Key).ToDictionary(e => e.Ref.Key);
        var favorites = ns.Favorites
            .Where(entitiesByKey.ContainsKey)
            .Select(key => entitiesByKey[key])
            .Where(f => Matches(f.Ref.DisplayName))
            .OrderBy(f => f.Ref.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (favorites.Count > 0)
        {
            var folder = ExplorerEntry.Favorites(g, ns, favorites.Count);
            foreach (var favorite in favorites)
            {
                var favoriteEntry = ExplorerEntry.ForEntity(g, ns, favorite, "favorites", fullName: true);
                foreach (var s in favorite.Subscriptions) favoriteEntry.Children.Add(ExplorerEntry.ForEntity(g, ns, s, $"favorites/{favorite.Ref.Key}"));
                favoriteEntry.IsExpanded = favorite.Subscriptions.Count > 0 && expanded.Contains(favoriteEntry.Id);
                folder.Children.Add(favoriteEntry);
            }
            folder.IsExpanded = searching || !_collapsedFolders.Contains(folder.Id);
            nsEntry.Children.Add(folder);
        }

        if (!searching || queues.Count > 0)
        {
            var folder = ExplorerEntry.Folder(g, ns, topics: false, queues.Count);
            foreach (var q in queues) folder.Children.Add(ExplorerEntry.ForEntity(g, ns, q));
            folder.IsExpanded = queues.Count > 0 && (searching || !_collapsedFolders.Contains(folder.Id));
            nsEntry.Children.Add(folder);
        }
        if (!searching || topics.Count > 0)
        {
            var folder = ExplorerEntry.Folder(g, ns, topics: true, topics.Count);
            foreach (var (topic, subs) in topics)
            {
                var topicEntry = ExplorerEntry.ForEntity(g, ns, topic);
                foreach (var s in subs) topicEntry.Children.Add(ExplorerEntry.ForEntity(g, ns, s));
                topicEntry.IsExpanded = subs.Count > 0 && (searching ? !Matches(topic.Ref.Name) || subs.Count < topic.Subscriptions.Count : expanded.Contains(topicEntry.Id));
                folder.Children.Add(topicEntry);
            }
            folder.IsExpanded = topics.Count > 0 && (searching || !_collapsedFolders.Contains(folder.Id));
            nsEntry.Children.Add(folder);
        }
        return queues.Count + topics.Count;
    }

    private static IEnumerable<ExplorerEntry> Flatten(IEnumerable<ExplorerEntry> entries) =>
        entries.SelectMany(e => new[] { e }.Concat(Flatten(e.Children)));

    private ExplorerEntry? FindEntry(string id) => Flatten(RootEntries).FirstOrDefault(e => e.Id == id);

    private async Task LoadNamespaceAsync(ExplorerEntry nsEntry, bool force)
    {
        if (nsEntry.Kind != ExplorerEntryKind.Namespace) return;
        if (nsEntry.Loaded && !force) return;
        nsEntry.Loaded = true;
        var ns = nsEntry.Namespace!;
        var expanded = ExpandedIds();
        nsEntry.Children.Clear();
        nsEntry.Children.Add(ExplorerEntry.Placeholder(nsEntry.Group, ns, "Loading…"));
        try
        {
            var snapshot = await Task.Run(() => ServiceBusOperations.LoadAsync(ns, CancellationToken.None));
            _snapshots[ns.Id] = snapshot;
            if (_explorerFilter.Length > 0) RebuildTree();
            else FillNamespace(nsEntry, snapshot, "", expanded);
        }
        catch (Exception ex)
        {
            nsEntry.Loaded = false;
            nsEntry.Children.Clear();
            nsEntry.Children.Add(ExplorerEntry.Placeholder(nsEntry.Group, ns, "⚠ " + FirstLine(ex.Message)));
        }
    }

    private Task ReloadNamespaceAsync(string namespaceId)
    {
        _snapshots.Remove(namespaceId);
        if (FindEntry(namespaceId) is not { } entry) return Task.CompletedTask;
        entry.IsExpanded = true;
        return LoadNamespaceAsync(entry, true);
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(['\r', '\n']);
        return i < 0 ? s : s[..i];
    }

    private void NamespaceTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is not ExplorerEntry entry) return;
        entry.IsExpanded = true;   // the template binds IsExpanded one way, so user expands are copied back here
        if (entry.IsFolder) _collapsedFolders.Remove(entry.Id);
        if (entry.Kind == ExplorerEntryKind.Namespace) _ = LoadNamespaceAsync(entry, false);
    }

    private void NamespaceTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Item is not ExplorerEntry entry) return;
        entry.IsExpanded = false;
        if (entry.IsFolder && _explorerFilter.Length == 0) _collapsedFolders.Add(entry.Id);
    }

    private void NamespaceTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not ExplorerEntry entry) return;
        if (entry.Kind is ExplorerEntryKind.Group or ExplorerEntryKind.Namespace || entry.IsFolder)
        {
            entry.IsExpanded = !entry.IsExpanded;
            if (entry.IsExpanded && entry.Kind == ExplorerEntryKind.Namespace) _ = LoadNamespaceAsync(entry, false);
        }
    }

    private void NamespaceTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var entry = EntryFromElement(e.OriginalSource as DependencyObject);
        if (entry is not { IsEntity: true }) return;
        e.Handled = true;
        OpenEntity(entry);
    }

    private ExplorerEntry? EntryFromElement(DependencyObject? element)
    {
        for (var d = element; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is TreeViewItem item) return NamespaceTree.ItemFromContainer(item) as ExplorerEntry ?? item.DataContext as ExplorerEntry;
        return null;
    }

    private void NamespaceTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var entry = EntryFromElement(e.OriginalSource as DependencyObject);
        if (entry == null || entry.Kind == ExplorerEntryKind.Placeholder && entry.Namespace == null) return;
        e.Handled = true;
        NamespaceTree.SelectedItem = entry;

        var readOnly = entry.Group.IsReadOnly;
        var suffix = readOnly ? " (read-only group)" : "";
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        void Separator() => menu.Items.Add(new MenuFlyoutSeparator());
        void AddFavoriteItem()
        {
            if (entry.IsFavorite) Add("Remove from favourites", "\uE8D9", () => ToggleFavorite(entry));
            else Add("Add to favourites", "\uE734", () => ToggleFavorite(entry));
        }

        var g = entry.Group;
        var ns = entry.Namespace;
        switch (entry.Kind)
        {
            case ExplorerEntryKind.Group:
                Add("Add namespace…", "\uE710", () => _ = AddNamespaceAsync(g));
                Add("Edit group…", "\uE70F", () => _ = EditGroupAsync(g));
                Separator();
                Add("Move up", "\uE70E", () => MoveGroup(g, -1));
                Add("Move down", "\uE70D", () => MoveGroup(g, 1));
                Separator();
                Add("Delete group", "\uE74D", () => _ = DeleteGroupAsync(g));
                break;
            case ExplorerEntryKind.Namespace:
                Add("Refresh", "\uE72C", () => _ = ReloadNamespaceAsync(ns!.Id));
                Separator();
                Add("New queue…" + suffix, "\uE710", () => _ = CreateEntityAsync(g, ns!, EntityKind.Queue, null), !readOnly);
                Add("New topic…" + suffix, "\uE710", () => _ = CreateEntityAsync(g, ns!, EntityKind.Topic, null), !readOnly);
                Separator();
                Add("Edit namespace…", "\uE70F", () => _ = EditNamespaceAsync(g, ns!));
                Add("Duplicate", "\uE8C8", () => DuplicateNamespace(g, ns!));
                Separator();
                Add("Remove from Sbzilla", "\uE74D", () => _ = DeleteNamespaceAsync(g, ns!));
                break;
            case ExplorerEntryKind.QueuesFolder:
                Add("New queue…" + suffix, "\uE710", () => _ = CreateEntityAsync(g, ns!, EntityKind.Queue, null), !readOnly);
                Add("Refresh", "\uE72C", () => _ = ReloadNamespaceAsync(ns!.Id));
                break;
            case ExplorerEntryKind.TopicsFolder:
                Add("New topic…" + suffix, "\uE710", () => _ = CreateEntityAsync(g, ns!, EntityKind.Topic, null), !readOnly);
                Add("Refresh", "\uE72C", () => _ = ReloadNamespaceAsync(ns!.Id));
                break;
            case ExplorerEntryKind.Queue:
            case ExplorerEntryKind.Subscription:
                Add("Open", "\uE8A7", () => OpenEntity(entry));
                Add("Open dead-letter queue", "\uE7BA", () => OpenEntity(entry, MessageSource.DeadLetter));
                Add("Send message…" + suffix, "\uE724", () => _ = OpenEntity(entry)?.SendAsync(), !readOnly);
                menu.Items.Add(OpenTabsSubMenu(entry));
                AddFavoriteItem();
                Separator();
                Add($"Delete {EntityRef.KindLabel(entry.Entity!.Kind).ToLowerInvariant()}…" + suffix, "\uE74D", () => _ = DeleteEntityAsync(entry), !readOnly);
                break;
            case ExplorerEntryKind.Topic:
                Add("Open", "\uE8A7", () => OpenEntity(entry));
                Add("Send message…" + suffix, "\uE724", () => _ = OpenEntity(entry)?.SendAsync(), !readOnly);
                Add("New subscription…" + suffix, "\uE710", () => _ = CreateEntityAsync(g, ns!, EntityKind.Subscription, entry.Entity!.Name), !readOnly);
                menu.Items.Add(OpenTabsSubMenu(entry));
                AddFavoriteItem();
                Separator();
                Add("Delete topic…" + suffix, "\uE74D", () => _ = DeleteEntityAsync(entry), !readOnly);
                break;
            case ExplorerEntryKind.Placeholder:
                Add("Retry", "\uE72C", () => _ = ReloadNamespaceAsync(ns!.Id));
                break;
        }
        if (menu.Items.Count > 0) menu.ShowAt(NamespaceTree, new FlyoutShowOptions { Position = e.GetPosition(NamespaceTree) });
    }

    /// <summary>Lists the open tabs for an entity; picking one selects that tab.</summary>
    private MenuFlyoutItemBase OpenTabsSubMenu(ExplorerEntry entry)
    {
        var matches = OpenTabs.Where(t => t.Entity.Namespace.Id == entry.Namespace!.Id && t.Entity.Entity.Key == entry.Entity!.Key).ToList();
        var sub = new MenuFlyoutSubItem { Text = $"Open tabs ({matches.Count})", Icon = new FontIcon { Glyph = "\uE737" } };
        if (matches.Count == 0)
        {
            sub.Items.Add(new MenuFlyoutItem { Text = "No open tabs", IsEnabled = false });
            return sub;
        }
        foreach (var (tab, entity) in matches)
        {
            var item = new MenuFlyoutItem { Text = $"{entity.Title} · {EntityRef.SourceLabel(entity.Source)}" };
            item.Click += (_, _) => Tabs.SelectedItem = tab;
            sub.Items.Add(item);
        }
        return sub;
    }

    private void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        _snapshots.Clear();
        foreach (var entry in Flatten(RootEntries).Where(x => x.Kind == ExplorerEntryKind.Namespace).ToList())
        {
            if (entry.IsExpanded) _ = LoadNamespaceAsync(entry, true);
            else
            {
                entry.Loaded = false;
                entry.Children.Clear();
                entry.Children.Add(ExplorerEntry.Placeholder(entry.Group, entry.Namespace, "Loading…"));
            }
        }
        foreach (var (_, tab) in OpenTabs.Where(t => ReferenceEquals(t.Tab, Tabs.SelectedItem))) _ = tab.RefreshAsync(peek: false);
    }

    /// <summary>Keeps the explorer's counts in step with what an open tab has just read.</summary>
    private void OnInfoUpdated(EntityTab tab, EntityInfo info)
    {
        if (_snapshots.TryGetValue(tab.Namespace.Id, out var snapshot))
        {
            var list = info.Ref.Kind switch
            {
                EntityKind.Queue => snapshot.Queues,
                EntityKind.Topic => snapshot.Topics,
                _ => snapshot.Topics.FirstOrDefault(t => t.Ref.Name == info.Ref.Topic)?.Subscriptions,
            };
            var index = list?.FindIndex(x => x.Ref.Key == info.Ref.Key) ?? -1;
            if (index >= 0) list![index] = info;
        }
        var updates = new[] { info }.Concat(info.Subscriptions);
        var labelsByKey = updates.DistinctBy(i => i.Ref.Key).ToDictionary(i => i.Ref.Key, i => i.CountsLabel);
        // The same entity can be listed twice: under Queues / Topics and under Favourites.
        var entries = Flatten(RootEntries)
            .Where(e => e.Namespace?.Id == tab.Namespace.Id && e.Entity is { } entity && labelsByKey.ContainsKey(entity.Key))
            .ToList();
        foreach (var entry in entries) entry.Detail = labelsByKey[entry.Entity!.Key];
    }

    /// <summary>Pins or unpins an entity under its namespace's Favourites. Allowed in read-only groups: it only changes Sbzilla's settings.</summary>
    private void ToggleFavorite(ExplorerEntry entry)
    {
        if (entry is not { Namespace: { } ns, Entity: { } entity }) return;
        if (!ns.Favorites.Remove(entity.Key)) ns.Favorites.Add(entity.Key);
        Save();
        RebuildTree();
    }

    // ------------------------------------------------------------------ groups & namespaces

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var color = GroupColors.Presets[_groups.Count % GroupColors.Presets.Length];
        var group = new NamespaceGroup { Name = "", Color = color };
        if (!await Dialogs.EditGroupAsync(XamlRoot, group, isNew: true)) return;
        _groups.Add(group);
        Save();
        RebuildTree();
        if (FindEntry(group.Id) is { } entry) entry.IsExpanded = true;
    }

    private async void NewNamespace_Click(object sender, RoutedEventArgs e)
    {
        var group = (NamespaceTree.SelectedItem as ExplorerEntry)?.Group ?? CurrentTab?.Group ?? _groups.FirstOrDefault();
        if (group == null)
        {
            group = new NamespaceGroup { Name = "", Color = GroupColors.Presets[4] };
            if (!await Dialogs.EditGroupAsync(XamlRoot, group, isNew: true)) return;
            _groups.Add(group);
            Save();
            RebuildTree();
        }
        await AddNamespaceAsync(group);
    }

    private async Task EditGroupAsync(NamespaceGroup group)
    {
        if (!await Dialogs.EditGroupAsync(XamlRoot, group, isNew: false)) return;
        Save();
        RebuildTree();
        RefreshTabs();
    }

    private void MoveGroup(NamespaceGroup group, int delta)
    {
        var index = _groups.IndexOf(group);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _groups.Count) return;
        _groups.RemoveAt(index);
        _groups.Insert(target, group);
        Save();
        RebuildTree();
    }

    private async Task DeleteGroupAsync(NamespaceGroup group)
    {
        var detail = group.Namespaces.Count == 0 ? "" : $" and its {group.Namespaces.Count} namespace{(group.Namespaces.Count == 1 ? "" : "s")}";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete group",
                $"Remove “{group.Name}”{detail} from Sbzilla? Nothing is deleted in Azure. Open tabs stay open.")) return;
        _groups.Remove(group);
        foreach (var ns in group.Namespaces) _snapshots.Remove(ns.Id);
        Save();
        RebuildTree();
    }

    private async Task AddNamespaceAsync(NamespaceGroup group)
    {
        var result = await Dialogs.EditNamespaceAsync(XamlRoot, new NamespaceConnection(), _groups, group, isNew: true);
        if (result is not { } r) return;
        r.Group.Namespaces.Add(r.Namespace);
        Save();
        RebuildTree();
        if (FindEntry(r.Group.Id) is { } g) g.IsExpanded = true;
        if (FindEntry(r.Namespace.Id) is { } entry)
        {
            entry.IsExpanded = true;
            _ = LoadNamespaceAsync(entry, false);
            SelectWhenShown(entry);
        }
    }

    private async Task EditNamespaceAsync(NamespaceGroup group, NamespaceConnection ns)
    {
        var result = await Dialogs.EditNamespaceAsync(XamlRoot, ns, _groups, group, isNew: false);
        if (result is not { } r) return;
        var index = group.Namespaces.IndexOf(ns);
        if (index < 0) return;
        if (r.Group == group) group.Namespaces[index] = r.Namespace;
        else
        {
            group.Namespaces.RemoveAt(index);
            r.Group.Namespaces.Add(r.Namespace);
        }
        ServiceBusClients.Reset(ns.Id);
        _snapshots.Remove(ns.Id);
        Save();
        RebuildTree();
        RefreshTabs();
    }

    private void DuplicateNamespace(NamespaceGroup group, NamespaceConnection ns)
    {
        var copy = ns.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = ns.Name.Length > 0 ? ns.Name + " (copy)" : ns.ShortName + " (copy)";
        group.Namespaces.Insert(group.Namespaces.IndexOf(ns) + 1, copy);
        Save();
        RebuildTree();
    }

    private async Task DeleteNamespaceAsync(NamespaceGroup group, NamespaceConnection ns)
    {
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Remove namespace",
                $"Remove “{ns.DisplayName}” from “{group.Name}”? Only the saved connection is removed — nothing changes in Azure.", "Remove")) return;
        group.Namespaces.Remove(ns);
        _snapshots.Remove(ns.Id);
        ServiceBusClients.Reset(ns.Id);
        Save();
        RebuildTree();
    }

    /// <summary>Re-applies group colours and connection settings to open tabs after groups or namespaces change.</summary>
    private void RefreshTabs()
    {
        foreach (var (_, tab) in OpenTabs)
            if (FindNamespace(tab.Namespace.Id) is ({ } group, { } ns)) tab.SetGroup(group, ns);
        UpdateChrome();
    }

    // ------------------------------------------------------------------ entities

    private async Task CreateEntityAsync(NamespaceGroup group, NamespaceConnection ns, EntityKind kind, string? topic)
    {
        if (group.IsReadOnly) return;
        if (await MessageDialogs.CreateEntityAsync(XamlRoot, kind, topic, group.Name) is not ({ } options, var rule)) return;
        EntityRef created;
        try { created = await Task.Run(() => ServiceBusOperations.CreateAsync(ns, options, rule, CancellationToken.None)); }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, $"Couldn't create {EntityRef.KindLabel(kind).ToLowerInvariant()}", ex.Message);
            return;
        }
        await ReloadNamespaceAsync(ns.Id);
        if (FindEntry($"{ns.Id}/{created.Key}") is { } entry)
        {
            SelectWhenShown(entry);
            OpenEntity(entry);
        }
        else OpenEntity(group, ns, created, MessageSource.Active, null);
    }

    private async Task DeleteEntityAsync(ExplorerEntry entry)
    {
        if (entry.Group.IsReadOnly || entry.Entity is not { } entity) return;
        var kind = EntityRef.KindLabel(entity.Kind).ToLowerInvariant();
        var extra = entity.Kind == EntityKind.Topic && entry.Info?.Subscriptions.Count > 0
            ? $" Its {entry.Info.Subscriptions.Count} subscription(s) and their messages are deleted too."
            : "";
        if (!await Dialogs.ConfirmTypedAsync(XamlRoot, $"Delete {kind}",
                $"Permanently delete {kind} “{entity.DisplayName}” and all its messages from {entry.Namespace!.DisplayName} in {entry.Group.Name}?{extra} This can't be undone.",
                entity.Name))
            return;
        try { await Task.Run(() => ServiceBusOperations.DeleteEntityAsync(entry.Namespace, entity, CancellationToken.None)); }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, $"Couldn't delete {kind}", ex.Message);
            return;
        }
        var gone = OpenTabs.Where(t => t.Entity.Namespace.Id == entry.Namespace.Id && (t.Entity.Entity.Key == entity.Key
                       || entity.Kind == EntityKind.Topic && t.Entity.Entity.Kind == EntityKind.Subscription && t.Entity.Entity.Topic == entity.Name))
            .Select(t => t.Tab).ToList();
        foreach (var tab in gone) CloseTab(tab);
        // Deleting a topic deletes its subscriptions, so unpin those too.
        var subscriptionPrefix = $"{EntityKind.Subscription}:{entity.Name}/Subscriptions/".ToLowerInvariant();
        var unpinned = entry.Namespace.Favorites.RemoveAll(key =>
            key == entity.Key || entity.Kind == EntityKind.Topic && key.StartsWith(subscriptionPrefix, StringComparison.Ordinal));
        if (unpinned > 0) Save();
        await ReloadNamespaceAsync(entry.Namespace.Id);
    }

    // ------------------------------------------------------------------ tabs

    private EntityTab? OpenEntity(ExplorerEntry entry, MessageSource source = MessageSource.Active) =>
        entry is { Namespace: { } ns, Entity: { } entity } ? OpenEntity(entry.Group, ns, entity, source, entry.Info) : null;

    /// <summary>Selects the entity's tab if it's already open with that source, else opens a new one.</summary>
    private EntityTab OpenEntity(NamespaceGroup group, NamespaceConnection ns, EntityRef entity, MessageSource source, EntityInfo? info)
    {
        if (!entity.HasMessages) source = MessageSource.Active;
        var existing = OpenTabs.FirstOrDefault(t => t.Entity.Namespace.Id == ns.Id && t.Entity.Entity.Key == entity.Key && t.Entity.Source == source);
        if (existing.Tab != null)
        {
            Tabs.SelectedItem = existing.Tab;
            return existing.Entity;
        }
        var tab = new EntityTab(group, ns, entity, source, info);
        AddTab(tab);
        return tab;
    }

    private void AddTab(EntityTab entity)
    {
        var tab = new TabViewItem
        {
            Content = entity,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            MaxWidth = TabMaxWidth,
        };
        entity.StateChanged += (_, _) =>
        {
            UpdateTabHeader(tab, entity);
            if (ReferenceEquals(Tabs.SelectedItem, tab)) UpdateChrome();
            QueueSessionSave();
        };
        entity.InfoUpdated += (_, info) => OnInfoUpdated(entity, info);
        entity.OpenRequested += (_, target) =>
        {
            var info = _snapshots.TryGetValue(entity.Namespace.Id, out var s) ? s.Find(target) : null;
            OpenEntity(entity.Group, entity.Namespace, target, MessageSource.Active, info);
        };
        entity.NamespaceChanged += (_, _) => _ = ReloadNamespaceAsync(entity.Namespace.Id);
        UpdateTabHeader(tab, entity);
        tab.RightTapped += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != tab; d = VisualTreeHelper.GetParent(d))
                if (d == entity) return;   // right-clicks inside the tab content have their own menus
            e.Handled = true;
            TabContextMenu(tab).ShowAt(tab, new FlyoutShowOptions { Position = e.GetPosition(tab) });
        };
        Tabs.TabItems.Add(tab);
        if (!_restoring) Tabs.SelectedItem = tab;
        UpdateWelcome();
        QueueSessionSave();
    }

    // ------------------------------------------------------------------ session (open tabs survive restarts)

    private void RestoreSession()
    {
        var state = _sessionStore.Load();
        _restoring = true;
        try
        {
            foreach (var saved in state.Tabs)
            {
                if (FindNamespace(saved.NamespaceId) is not ({ } group, { } ns)) continue;   // namespace was removed since
                if (saved.Name.Length == 0 || saved.Kind == EntityKind.Subscription && string.IsNullOrEmpty(saved.Topic)) continue;
                AddTab(new EntityTab(group, ns, new EntityRef(saved.Kind, saved.Name, saved.Topic), saved.Source));
            }
        }
        finally { _restoring = false; }
        if (Tabs.TabItems.Count > 0)
            Tabs.SelectedIndex = Math.Clamp(state.SelectedIndex, 0, Tabs.TabItems.Count - 1);
    }

    /// <summary>Debounced so a burst of changes is one write, but a crash loses at most a couple of seconds.</summary>
    private void QueueSessionSave()
    {
        if (_restoring) return;
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void SaveSession()
    {
        _sessionTimer.Stop();
        var tabs = OpenTabs.ToList();
        try
        {
            _sessionStore.Save(new SessionState
            {
                Tabs = tabs.Select(t => t.Entity.ToSessionTab()).ToList(),
                SelectedIndex = Math.Max(0, tabs.FindIndex(t => ReferenceEquals(t.Tab, Tabs.SelectedItem))),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A tab may take at most half the tab area before its text is trimmed.</summary>
    private double TabMaxWidth => Tabs.ActualWidth > 0 ? Tabs.ActualWidth / 2 : double.PositiveInfinity;

    private static void UpdateTabHeader(TabViewItem tab, EntityTab entity)
    {
        var color = GroupColors.Parse(entity.Group.Color);
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Add(FrameworkElement e, int column) { Grid.SetColumn(e, column); header.Children.Add(e); }
        Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
        }, 0);
        Add(new TextBlock { Text = entity.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 1);
        if (entity.Source != MessageSource.Active)
            Add(new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(GroupColors.WithAlpha(color, 0x90)),
                Child = new TextBlock { Text = entity.Source == MessageSource.DeadLetter ? "DLQ" : "TDLQ", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
            }, 2);
        var context = entity.Entity.Kind == EntityKind.Subscription ? $"{entity.Entity.Topic} · {entity.Namespace.ShortName}" : entity.Namespace.ShortName;
        Add(new TextBlock
        {
            Text = context,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        }, 3);
        tab.Header = header;
        tab.Background = new SolidColorBrush(GroupColors.WithAlpha(color, 0x30));
        ToolTipService.SetToolTip(tab, $"{entity.Group.Name} · {entity.Description}");
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CurrentTab?.Activate();
        UpdateChrome();
        QueueSessionSave();
    }

    /// <summary>Tints the title bar with the selected tab's group colour.</summary>
    private void UpdateChrome()
    {
        if (CurrentTab is not { } tab)
        {
            AppTitleBar.Background = null;
            TitleGroupBadge.Visibility = Visibility.Collapsed;
            TitleContext.Text = "";
            Title = "Sbzilla";
            return;
        }
        var color = GroupColors.Parse(tab.Group.Color);
        AppTitleBar.Background = new SolidColorBrush(GroupColors.WithAlpha(color, 0x50));
        TitleGroupBadge.Background = new SolidColorBrush(color);
        TitleGroupText.Foreground = new SolidColorBrush(GroupColors.ContrastText(color));
        TitleGroupText.Text = tab.Group.LabelWithReadOnly.ToUpperInvariant();
        TitleGroupBadge.Visibility = Visibility.Visible;
        TitleContext.Text = $"{tab.Entity.DisplayName} — {tab.Description}";
        Title = $"{tab.Title} - {tab.Namespace.DisplayName} - Sbzilla";
    }

    private void UpdateWelcome()
    {
        var any = Tabs.TabItems.Count > 0;
        Tabs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        UpdateChrome();
    }

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => CloseTab(args.Tab);

    private void CloseTab(TabViewItem tab) => CloseTabs(new[] { tab });

    private void CloseTabs(IEnumerable<TabViewItem> tabs)
    {
        foreach (var tab in tabs.ToList())
        {
            if (tab.Content is EntityTab entity) entity.Close();
            Tabs.TabItems.Remove(tab);
        }
        UpdateWelcome();
        QueueSessionSave();
    }

    private MenuFlyout TabContextMenu(TabViewItem tab)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Close", "\uE711", () => CloseTab(tab));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Close all but this", "\uE8BB", () => CloseTabs(Tabs.TabItems.OfType<TabViewItem>().Where(t => t != tab)));
        Add("Close all tabs", "\uE8BB", () => CloseTabs(Tabs.TabItems.OfType<TabViewItem>()));
        if (tab.Content is EntityTab entity && FindEntry($"{entity.Namespace.Id}/{entity.Entity.Key}") is { } entry)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Show in explorer", "\uE8A9", () => SelectWhenShown(entry));
        }
        return menu;
    }

    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args) => SaveSession();

    // ------------------------------------------------------------------ explorer search

    private void ExplorerSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = ExplorerSearch.Text.Trim();
        if (filter == _explorerFilter) return;
        if (_explorerFilter.Length == 0) _expandedBeforeSearch = ExpandedIds();
        _explorerFilter = filter;
        RebuildTree(expanded: filter.Length == 0 ? _expandedBeforeSearch : null);
    }

    private void ExplorerSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape when ExplorerSearch.Text.Length > 0:
                ExplorerSearch.Text = "";
                e.Handled = true;
                break;
            case VirtualKey.Enter when Flatten(RootEntries).FirstOrDefault(x => x.IsEntity) is { } first:
                OpenEntity(first);
                e.Handled = true;
                break;
            case VirtualKey.Down when RootEntries.Count > 0:
                NamespaceTree.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------ accelerators

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ExplorerSearch.Focus(FocusState.Keyboard);
        ExplorerSearch.SelectAll();
    }

    private void SendAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (NamespaceTree.SelectedItem is ExplorerEntry { IsEntity: true } entry && NamespaceTree.FocusState != FocusState.Unfocused)
            _ = OpenEntity(entry)?.SendAsync();
        else if (CurrentTab is { } tab) _ = tab.SendAsync();
    }

    private void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Tabs.SelectedItem is TabViewItem tab) CloseTab(tab);
    }

    /// <summary>F5 outside a tab refreshes the selected namespace in the explorer, or the current tab.</summary>
    private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (NamespaceTree.SelectedItem is ExplorerEntry { Namespace: { } ns }) _ = ReloadNamespaceAsync(ns.Id);
        else if (CurrentTab is { } tab) _ = tab.RefreshAsync(peek: tab.Entity.HasMessages);
    }
}
