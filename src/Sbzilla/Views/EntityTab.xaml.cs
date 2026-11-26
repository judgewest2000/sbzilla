using System.Collections.ObjectModel;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sbzilla.Models;
using Sbzilla.Services;
using Sbzilla.ServiceBus;
using Windows.ApplicationModel.DataTransfer;

namespace Sbzilla.Views;

public sealed class MessageRow
{
    public MessageRow(ServiceBusReceivedMessage m)
    {
        Message = m;
        SearchText = MessageFormatter.SearchText(m);
    }

    public ServiceBusReceivedMessage Message { get; }
    public string SearchText { get; }
    public string Seq => Message.SequenceNumber.ToString();
    public string MessageId => Message.MessageId ?? "";
    public string Subject => Message.Subject ?? "";
    public string Enqueued => MessageFormatter.Time(Message.EnqueuedTime);
    public string Size => MessageFormatter.Size(Message.Body.ToMemory().Length);
    public string Deliveries => Message.DeliveryCount.ToString();
    public string Session => Message.SessionId ?? "";
    public string ContentType => Message.ContentType ?? "";
    public string Reason => Message.DeadLetterReason ?? "";
    public string ReasonDetail => Message.DeadLetterErrorDescription is { Length: > 0 } d ? $"{Reason}: {d}" : Reason;
}

public sealed record PropertyRow(string Name, string Value);

public sealed class SubscriptionRow(EntityInfo info)
{
    public EntityInfo Info { get; } = info;
    public string Name => Info.Ref.Name;
    public string Active => Info.ActiveCount.ToString("N0");
    public string DeadLetter => Info.DeadLetterCount.ToString("N0");
    public string Scheduled => Info.ScheduledCount.ToString("N0");
    public string Status => Info.Status;
    public string Notes => string.Join(" · ", new[]
    {
        Info.RequiresSession ? "sessions" : "",
        Info.ForwardTo.Length > 0 ? $"forwards to {Info.ForwardTo}" : "",
    }.Where(s => s.Length > 0));
}

public sealed class RuleRow(RuleProperties rule)
{
    public RuleProperties Rule { get; } = rule;
    public string Name => Rule.Name;
    public string Kind => Rule.Filter switch
    {
        SqlRuleFilter and not TrueRuleFilter and not FalseRuleFilter => "SQL",
        CorrelationRuleFilter => "Correlation",
        TrueRuleFilter => "True",
        FalseRuleFilter => "False",
        _ => "",
    };
    public string Filter => EntityPropertyFields.Describe(Rule.Filter);
    public string Action => EntityPropertyFields.Describe(Rule.Action);
}

/// <summary>One open queue, topic or subscription: peek/send/resubmit/delete messages, view subscriptions and rules, edit settings.</summary>
public sealed partial class EntityTab : UserControl
{
    private static readonly MessageSource[] Sources = Enum.GetValues<MessageSource>();

    private readonly List<MessageRow> _peeked = new();
    private readonly ObservableCollection<MessageRow> _shown = new();
    private readonly CancellationTokenSource _closed = new();
    private CancellationTokenSource? _operation;
    private PropertyForm? _form;
    private object? _properties;
    private bool _activated;
    private bool _propertiesLoaded;
    private bool _rulesLoaded;
    private bool _updatingSources;
    private MessageDraft _lastDraft = new();

    public EntityTab(NamespaceGroup group, NamespaceConnection ns, EntityRef entity, MessageSource source = MessageSource.Active, EntityInfo? info = null)
    {
        InitializeComponent();
        ExternalScrollBars.Attach(MessageList, MessageVBar, MessageHBar, MessageHeaderScroll);
        ExternalScrollBars.Attach(PropertyList, PropertyVBar);
        ExternalScrollBars.Attach(SubscriptionList, SubscriptionVBar);
        ExternalScrollBars.Attach(RuleList, RuleVBar);
        Info = info;
        Group = group;
        Namespace = ns;
        Entity = entity;
        Source = entity.HasMessages ? source : MessageSource.Active;
        MessageList.ItemsSource = _shown;

        MessagesView.Visibility = entity.HasMessages ? Visibility.Visible : Visibility.Collapsed;
        SubscriptionsView.Visibility = entity.Kind == EntityKind.Topic ? Visibility.Visible : Visibility.Collapsed;
        RulesView.Visibility = entity.Kind == EntityKind.Subscription ? Visibility.Visible : Visibility.Collapsed;
        if (!entity.HasMessages) Views.SelectedItem = SubscriptionsView;
        ToolTipService.SetToolTip(SendButton, entity.Kind == EntityKind.Subscription
            ? $"Send a message to topic “{entity.Topic}” (Ctrl+N) — every matching subscription receives it"
            : "Send a message (Ctrl+N)");

        UpdateSourceBox();
        UpdateCounts();
        ApplyGroup();
        UpdateButtons();
        ListHint.Text = "Loading…";
    }

    public NamespaceGroup Group { get; private set; }
    public NamespaceConnection Namespace { get; private set; }
    public EntityRef Entity { get; }
    public MessageSource Source { get; private set; }
    public EntityInfo? Info { get; private set; }

    public string Title => Entity.Name;

    public string Description => Entity.Kind == EntityKind.Subscription
        ? $"{EntityRef.KindLabel(Entity.Kind)} of {Entity.Topic} · {Namespace.DisplayName}"
        : $"{EntityRef.KindLabel(Entity.Kind)} · {Namespace.DisplayName}";

    /// <summary>Title, counts or group changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Counts were refreshed; the explorer can update its labels.</summary>
    public event EventHandler<EntityInfo>? InfoUpdated;

    /// <summary>Asks the window to open another entity (e.g. a topic's subscription).</summary>
    public event EventHandler<EntityRef>? OpenRequested;

    /// <summary>Entities were created in the namespace; the explorer should reload it.</summary>
    public event EventHandler? NamespaceChanged;

    private bool ReadOnly => Group.IsReadOnly;
    private bool Busy => _operation != null;
    private bool RequiresSession => Info?.RequiresSession == true;
    private bool SessionPeek => RequiresSession && Source == MessageSource.Active;
    private new XamlRoot XamlRoot => Root.XamlRoot;

    public SessionTab ToSessionTab() => new() { NamespaceId = Namespace.Id, Kind = Entity.Kind, Name = Entity.Name, Topic = Entity.Topic, Source = Source };

    public void SetGroup(NamespaceGroup group, NamespaceConnection ns)
    {
        Group = group;
        Namespace = ns;
        ApplyGroup();
        UpdateButtons();
        if (_properties != null) ShowPropertiesForm();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyGroup()
    {
        var color = GroupColors.Parse(Group.Color);
        var text = new SolidColorBrush(GroupColors.ContrastText(color));
        Toolbar.BorderBrush = new SolidColorBrush(color);
        StatusBar.Background = new SolidColorBrush(color);
        foreach (var t in new[] { StateText, GroupText, NamespaceText, CountsText, ShownText }) t.Foreground = text;
        StateIcon.Foreground = text;
        BusyRing.Foreground = text;
        GroupText.Text = Group.LabelWithReadOnly;
        NamespaceText.Text = Namespace.ResolvedNamespace;
        ReadOnlyBadge.Visibility = ReadOnly ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyBadge.Background = new SolidColorBrush(color);
        ReadOnlyText.Foreground = ReadOnlyIcon.Foreground = text;
    }

    /// <summary>Loads counts and peeks the first time the tab is shown, so restoring many tabs doesn't hit every namespace at once.</summary>
    public void Activate()
    {
        if (_activated) return;
        _activated = true;
        _ = RefreshAsync(peek: true);
    }

    public void Close()
    {
        _closed.Cancel();
        _operation?.Cancel();
        BodyText.Close();
    }

    // ------------------------------------------------------------------ status

    private void SetState(string text, bool busy = false, bool error = false)
    {
        StateText.Text = text;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StateIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StateIcon.Glyph = error ? "\uE783" : "\uE73E";
        ToolTipService.SetToolTip(StateText, text);
    }

    private void UpdateCounts()
    {
        CountsText.Text = Info is not { } i ? ""
            : Entity.HasMessages ? $"{i.ActiveCount:N0} active · {i.DeadLetterCount:N0} dead-letter" + (i.ScheduledCount > 0 ? $" · {i.ScheduledCount:N0} scheduled" : "")
            : $"{i.Subscriptions.Count} subscription{(i.Subscriptions.Count == 1 ? "" : "s")} · {i.ScheduledCount:N0} scheduled";
        var shown = _shown.Count == _peeked.Count ? $"{_peeked.Count:N0} peeked" : $"{_shown.Count:N0} of {_peeked.Count:N0} peeked";
        ShownText.Text = Entity.HasMessages ? shown : "";
    }

    private void UpdateSourceBox()
    {
        _updatingSources = true;
        try
        {
            SourceBox.Items.Clear();
            foreach (var s in Sources)
                SourceBox.Items.Add(Info is { } i ? $"{EntityRef.SourceLabel(s)} ({i.CountFor(s):N0})" : EntityRef.SourceLabel(s));
            SourceBox.SelectedIndex = Array.IndexOf(Sources, Source);
        }
        finally { _updatingSources = false; }
        SessionBox.Visibility = SessionPeek ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<MessageRow> SelectedRows => MessageList.SelectedItems.OfType<MessageRow>().ToList();

    private void UpdateButtons()
    {
        var selected = MessageList.SelectedItems.Count;
        var idle = !Busy;
        var writable = idle && !ReadOnly;
        SendButton.IsEnabled = writable;
        PeekButton.IsEnabled = PeekCountBox.IsEnabled = SourceBox.IsEnabled = idle;
        PeekNextButton.IsEnabled = idle && _peeked.Count > 0;
        ResubmitButton.IsEnabled = writable && selected > 0;
        ResendButton.IsEnabled = writable && selected == 1;
        DeleteButton.IsEnabled = writable && selected > 0;
        CopyButton.IsEnabled = selected > 0;
        SaveButton.IsEnabled = _shown.Count > 0;
        PurgeButton.IsEnabled = writable;
        CancelButton.Visibility = Busy ? Visibility.Visible : Visibility.Collapsed;
        NewSubscriptionButton.IsEnabled = writable;
        AddRuleButton.IsEnabled = writable;
        DeleteRuleButton.IsEnabled = writable && RuleList.SelectedItem != null;
        SavePropertiesButton.IsEnabled = writable && _form != null;
        var tip = ReadOnly ? " (read-only group)" : "";
        foreach (var b in new[] { ResubmitButton, DeleteButton, PurgeButton })
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(b, tip);
    }

    /// <summary>Runs one operation at a time with a Stop button; errors land in the status bar and a dialog.</summary>
    private async Task<bool> RunAsync(string busyText, Func<CancellationToken, Task<string>> operation, bool quiet = false)
    {
        if (Busy) return false;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _operation = cts;
        UpdateButtons();
        SetState(busyText, busy: true);
        try
        {
            SetState(await operation(cts.Token));
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!_closed.IsCancellationRequested) SetState("Stopped.");
            return false;
        }
        catch (Exception ex)
        {
            var message = ex is ServiceBusException or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException or UnauthorizedAccessException
                ? ex.Message : ex.ToString();
            SetState(FirstLine(message), error: true);
            if (!quiet) await Dialogs.ShowErrorAsync(XamlRoot, busyText.TrimEnd('…'), message);
            return false;
        }
        finally
        {
            _operation = null;
            cts.Dispose();
            UpdateButtons();
        }
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(['\r', '\n']);
        return i < 0 ? s : s[..i];
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    // ------------------------------------------------------------------ refresh & peek

    private async Task RefreshInfoAsync(CancellationToken ct)
    {
        var info = await Task.Run(() => ServiceBusOperations.GetInfoAsync(Namespace, Entity, ct), ct);
        Info = info;
        UpdateSourceBox();
        UpdateCounts();
        if (Entity.Kind == EntityKind.Topic) SubscriptionList.ItemsSource = info.Subscriptions.Select(s => new SubscriptionRow(s)).ToList();
        UpdateRuntime();
        InfoUpdated?.Invoke(this, info);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(bool peek)
    {
        _activated = true;
        await RunAsync("Refreshing…", async ct =>
        {
            await RefreshInfoAsync(ct);
            if (Views.SelectedItem == PropertiesView || _propertiesLoaded) await LoadPropertiesAsync(ct);
            if (Views.SelectedItem == RulesView || _rulesLoaded) await LoadRulesAsync(ct);
            if (peek && Entity.HasMessages) return await PeekCoreAsync(append: false, ct);
            return $"Updated {DateTime.Now:HH:mm:ss}";
        });
    }

    private async Task<string> PeekCoreAsync(bool append, CancellationToken ct)
    {
        var max = double.IsNaN(PeekCountBox.Value) ? 100 : (int)PeekCountBox.Value;
        long? from = append && _peeked.Count > 0 ? _peeked.Max(r => r.Message.SequenceNumber) + 1 : null;
        var session = SessionPeek && SessionBox.Text.Trim().Length > 0 ? SessionBox.Text.Trim() : null;
        var source = Source;
        var requiresSession = RequiresSession;
        var messages = await Task.Run(() => ServiceBusOperations.PeekAsync(Namespace, Entity, source, max, from, requiresSession, session, ct), ct);

        if (!append) _peeked.Clear();
        var known = _peeked.Select(r => (r.Message.SequenceNumber, (string?)r.Message.SessionId)).ToHashSet();
        var added = messages.Where(m => !known.Contains((m.SequenceNumber, m.SessionId))).Select(m => new MessageRow(m)).ToList();
        _peeked.AddRange(added);
        ApplyFilter();
        if (_peeked.Count == 0)
            ListHint.Text = $"No messages in {EntityRef.SourceLabel(source).ToLowerInvariant()}" + (session != null ? $" for session “{session}”." : ".");
        return append
            ? $"Peeked {added.Count:N0} more message{(added.Count == 1 ? "" : "s")}"
            : $"Peeked {messages.Count:N0} message{(messages.Count == 1 ? "" : "s")} from {EntityRef.SourceLabel(source).ToLowerInvariant()} at {DateTime.Now:HH:mm:ss}";
    }

    private async void Peek_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Peeking…", async ct =>
        {
            await RefreshInfoAsync(ct);
            return await PeekCoreAsync(append: false, ct);
        });

    private async void PeekNext_Click(object sender, RoutedEventArgs e) => await RunAsync("Peeking…", ct => PeekCoreAsync(append: true, ct));

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(peek: Entity.HasMessages);

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RefreshAsync(peek: Entity.HasMessages);
    }

    private void FilterAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Entity.HasMessages) return;
        args.Handled = true;
        Views.SelectedItem = MessagesView;
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSources || SourceBox.SelectedIndex < 0) return;
        var source = Sources[SourceBox.SelectedIndex];
        if (source == Source) return;
        Source = source;
        SessionBox.Visibility = SessionPeek ? Visibility.Visible : Visibility.Collapsed;
        _peeked.Clear();
        ApplyFilter();
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (_activated) await RunAsync("Peeking…", ct => PeekCoreAsync(append: false, ct));
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        var selected = new HashSet<MessageRow>(SelectedRows);
        _shown.Clear();
        foreach (var row in _peeked.Where(r => filter.Length == 0 || r.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            _shown.Add(row);
        foreach (var row in _shown.Where(selected.Contains)) MessageList.SelectedItems.Add(row);
        ListHint.Text = _peeked.Count > 0 && _shown.Count == 0 ? $"No peeked messages match “{filter}”." : _peeked.Count == 0 ? ListHint.Text : "";
        ListHint.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCounts();
        UpdateButtons();
        ShowDetails();
    }

    // ------------------------------------------------------------------ selected message

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        ShowDetails();
    }

    private void ShowDetails()
    {
        var row = MessageList.SelectedItem as MessageRow ?? SelectedRows.FirstOrDefault();
        if (row == null)
        {
            BodyText.SetContent("", "plaintext");
            BodyInfo.Text = MessageList.SelectedItems.Count == 0 ? "Select a message" : "";
            PropertyList.ItemsSource = null;
            return;
        }
        var m = row.Message;
        var (text, kind) = MessageFormatter.BodyText(m.Body, m.ContentType);
        BodyText.SetContent(text, kind switch { BodyKind.Json => "json", BodyKind.Xml => "xml", _ => "plaintext" });
        BodyInfo.Text = $"{kind} · {MessageFormatter.Size(m.Body.ToMemory().Length)}" +
                        (MessageList.SelectedItems.Count > 1 ? $" · {MessageList.SelectedItems.Count} selected" : "");
        var rows = MessageFormatter.SystemProperties(m).Select(p => new PropertyRow(p.Key, p.Value)).ToList();
        if (m.ApplicationProperties.Count > 0)
        {
            rows.Add(new PropertyRow("", ""));
            rows.AddRange(m.ApplicationProperties.Select(p => new PropertyRow(p.Key, MessageFormatter.PropertyValue(p.Value))));
        }
        PropertyList.ItemsSource = rows;
    }

    private void MessageList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Delete && DeleteButton.IsEnabled)
        {
            e.Handled = true;
            Delete_Click(sender, e);
        }
        else if (ctrl && e.Key == Windows.System.VirtualKey.C && CopyButton.IsEnabled)
        {
            e.Handled = true;
            Copy_Click(sender, e);
        }
    }

    private void MessageList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != MessageList; d = VisualTreeHelper.GetParent(d))
        {
            if (d is not ListViewItem { Content: MessageRow row }) continue;
            if (!MessageList.SelectedItems.Contains(row))
            {
                MessageList.SelectedItems.Clear();
                MessageList.SelectedItems.Add(row);
            }
            break;
        }
        if (MessageList.SelectedItems.Count == 0) return;
        e.Handled = true;

        var menu = new MenuFlyout();
        void Add(string text, string glyph, bool enabled, RoutedEventHandler action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += action;
            menu.Items.Add(item);
        }
        Add("Resubmit…", "\uE72A", ResubmitButton.IsEnabled, Resubmit_Click);
        Add("Edit & resend…", "\uE70F", ResendButton.IsEnabled, Resend_Click);
        Add("View as JSON", "\uE943", MessageList.SelectedItems.Count == 1, (_, _) => _ = ShowJsonAsync());
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Copy as JSON", "\uE8C8", true, Copy_Click);
        Add("Copy body", "\uE8C8", MessageList.SelectedItems.Count == 1, (_, _) => CopyText(BodyText.Text));
        Add("Copy message ID", "\uE8C8", MessageList.SelectedItems.Count == 1,
            (_, _) => CopyText(string.Join(Environment.NewLine, SelectedRows.Select(r => r.MessageId))));
        Add("Save…", "\uE74E", true, Save_Click);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(ReadOnly ? "Delete (read-only group)" : "Delete…", "\uE74D", DeleteButton.IsEnabled, Delete_Click);
        menu.ShowAt(MessageList, new FlyoutShowOptions { Position = e.GetPosition(MessageList) });
    }

    private async void MessageList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await ShowJsonAsync();

    private async Task ShowJsonAsync()
    {
        if (SelectedRows.FirstOrDefault() is not { } row) return;
        await Dialogs.WhenNoDialogOpenAsync(XamlRoot);
        var box = new TextBox
        {
            Text = MessageFormatter.ToJson(new[] { row.Message }),
            Style = (Style)Application.Current.Resources["CodeTextBoxStyle"],
            IsReadOnly = true,
            Width = 760,
            Height = 520,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Message {row.Seq}",
            Content = box,
            PrimaryButtonText = "Copy",
            CloseButtonText = "Close",
            Resources = { ["ContentDialogMaxWidth"] = 900.0 },
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) CopyText(box.Text);
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        CopyText(MessageFormatter.ToJson(rows.Select(r => r.Message)));
        SetState($"Copied {rows.Count:N0} message{(rows.Count == 1 ? "" : "s")} as JSON");
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows.Count > 0 ? SelectedRows : _shown.ToList();
        if (rows.Count == 0) return;
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"{Entity.DisplayName.Replace('/', '-')}-{Source}".ToLowerInvariant() };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Handle);
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, MessageFormatter.ToJson(rows.Select(r => r.Message)));
            SetState($"Saved {rows.Count:N0} message{(rows.Count == 1 ? "" : "s")} to {file.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetState(ex.Message, error: true);
        }
    }

    // ------------------------------------------------------------------ send / resubmit / delete / purge

    public async Task SendAsync() => await SendDraftAsync(_lastDraft);

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private void Resend_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) _ = SendDraftAsync(MessageDrafts.FromReceived(row.Message));
    }

    private async Task SendDraftAsync(MessageDraft initial)
    {
        if (ReadOnly || Busy) return;
        var result = await MessageDialogs.SendAsync(XamlRoot, $"{EntityRef.KindLabel(Entity.Kind == EntityKind.Subscription ? EntityKind.Topic : Entity.Kind).ToLowerInvariant()} “{Entity.SendTarget}” in {Group.Name}",
            initial, RequiresSession && Entity.Kind == EntityKind.Queue);
        if (result is not ({ } draft, var copies)) return;
        _lastDraft = draft;
        var progress = new Progress<int>(n => SetState($"Sending… {n:N0} of {copies:N0}", busy: true));
        if (await RunAsync("Sending…", async ct =>
            {
                var sent = await Task.Run(() => ServiceBusOperations.SendAsync(Namespace, Entity.SendTarget, draft, copies, progress, ct), ct);
                var when = draft.ScheduledEnqueueTime is { } at ? $" scheduled for {at.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "";
                return $"Sent {sent:N0} message{(sent == 1 ? "" : "s")} to {Entity.SendTarget}{when}";
            }))
            await RefreshAfterChangeAsync();
    }

    private async void Resubmit_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0 || ReadOnly || Busy) return;
        if (await MessageDialogs.ConfirmResubmitAsync(XamlRoot, Group.Name, Entity, Source, rows.Count) is not { } remove) return;
        var messages = rows.Select(r => r.Message).ToList();
        var source = Source;
        var requiresSession = RequiresSession;
        var progress = new Progress<int>(n => SetState($"Resubmitting… {n:N0} of {messages.Count:N0}", busy: true));
        if (await RunAsync("Resubmitting…", async ct =>
            {
                var done = await Task.Run(() => ServiceBusOperations.ResubmitAsync(Namespace, Entity, source, requiresSession, messages, remove, progress, ct), ct);
                return $"Resubmitted {done:N0} message{(done == 1 ? "" : "s")} to {Entity.SendTarget}" + (remove ? " and removed the originals" : "");
            }))
            await RefreshAfterChangeAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0 || ReadOnly || Busy) return;
        var what = $"{rows.Count:N0} message{(rows.Count == 1 ? "" : "s")}";
        var message = $"Permanently delete {what} from {EntityRef.SourceLabel(Source).ToLowerInvariant()} of “{Entity.DisplayName}” in {Group.Name}?";
        if (Source == MessageSource.Active)
            message += "\n\nTo reach them, Service Bus locks messages in order and releases the others afterwards. Each one released counts as a delivery, " +
                       "so messages already at the max delivery count may be moved to the dead-letter queue.";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete messages", message)) return;

        var keys = rows.Select(r => new ServiceBusOperations.MessageKey(r.Message.SequenceNumber, r.Message.SessionId)).ToList();
        var source = Source;
        var requiresSession = RequiresSession;
        var progress = new Progress<int>(n => SetState($"Deleting… {n:N0} of {keys.Count:N0}", busy: true));
        if (await RunAsync("Deleting…", async ct =>
            {
                var deleted = await Task.Run(() => ServiceBusOperations.DeleteAsync(Namespace, Entity, source, requiresSession, keys, progress, ct), ct);
                var missing = keys.Count - deleted;
                return $"Deleted {deleted:N0} message{(deleted == 1 ? "" : "s")}" + (missing > 0 ? $" ({missing:N0} were already gone or couldn't be locked)" : "");
            }))
            await RefreshAfterChangeAsync();
    }

    private async void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (ReadOnly || Busy) return;
        var count = Info?.CountFor(Source) is { } n ? $"all {n:N0} messages" : "every message";
        var label = EntityRef.SourceLabel(Source).ToLowerInvariant();
        if (!await Dialogs.ConfirmTypedAsync(XamlRoot, "Purge messages",
                $"Permanently remove {count} from {label} of “{Entity.DisplayName}” in {Group.Name}? This can't be undone.",
                Entity.Name, "Purge"))
            return;
        var source = Source;
        var requiresSession = RequiresSession;
        var progress = new Progress<int>(n => SetState($"Purging… {n:N0} removed", busy: true));
        if (await RunAsync("Purging…", async ct =>
            {
                var removed = await Task.Run(() => ServiceBusOperations.PurgeAsync(Namespace, Entity, source, requiresSession, progress, ct), ct);
                return $"Purged {removed:N0} message{(removed == 1 ? "" : "s")} from {label}";
            }))
            await RefreshAfterChangeAsync();
    }

    /// <summary>Counts lag slightly behind changes, so wait a moment before re-reading them.</summary>
    private async Task RefreshAfterChangeAsync()
    {
        var status = StateText.Text;
        await Task.Delay(400);
        if (_closed.IsCancellationRequested) return;
        if (await RunAsync("Refreshing…", async ct =>
            {
                await RefreshInfoAsync(ct);
                if (Entity.HasMessages) await PeekCoreAsync(append: false, ct);
                return status;
            }, quiet: true))
            SetState(status);
    }

    // ------------------------------------------------------------------ views

    private async void Views_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var view = sender.SelectedItem;
        MessagesPane.Visibility = view == MessagesView ? Visibility.Visible : Visibility.Collapsed;
        SubscriptionsPane.Visibility = view == SubscriptionsView ? Visibility.Visible : Visibility.Collapsed;
        RulesPane.Visibility = view == RulesView ? Visibility.Visible : Visibility.Collapsed;
        PropertiesPane.Visibility = view == PropertiesView ? Visibility.Visible : Visibility.Collapsed;
        if (!_activated) return;
        if (view == PropertiesView && !_propertiesLoaded) await RunAsync("Loading properties…", async ct => { await LoadPropertiesAsync(ct); return "Loaded properties"; });
        if (view == RulesView && !_rulesLoaded) await RunAsync("Loading rules…", async ct => { await LoadRulesAsync(ct); return "Loaded rules"; });
    }

    private void SubscriptionList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SubscriptionList.SelectedItem is SubscriptionRow row) OpenRequested?.Invoke(this, row.Info.Ref);
    }

    private async void NewSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (ReadOnly || Busy) return;
        if (await MessageDialogs.CreateEntityAsync(XamlRoot, EntityKind.Subscription, Entity.Name, Group.Name) is not ({ } options, var rule)) return;
        if (await RunAsync("Creating subscription…", async ct =>
            {
                var created = await Task.Run(() => ServiceBusOperations.CreateAsync(Namespace, options, rule, ct), ct);
                await RefreshInfoAsync(ct);
                return $"Created subscription {created.Name}";
            }))
            NamespaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadRulesAsync(CancellationToken ct)
    {
        if (Entity.Kind != EntityKind.Subscription) return;
        var rules = await Task.Run(() => ServiceBusOperations.GetRulesAsync(Namespace, Entity, ct), ct);
        RuleList.ItemsSource = rules.Select(r => new RuleRow(r)).ToList();
        _rulesLoaded = true;
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (ReadOnly || Busy) return;
        if (await MessageDialogs.AddRuleAsync(XamlRoot, Entity) is not { } rule) return;
        await RunAsync("Adding rule…", async ct =>
        {
            await Task.Run(() => ServiceBusOperations.CreateRuleAsync(Namespace, Entity, rule, ct), ct);
            await LoadRulesAsync(ct);
            return $"Added rule {rule.Name}";
        });
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (ReadOnly || Busy || RuleList.SelectedItem is not RuleRow row) return;
        var last = RuleList.Items.Count == 1 ? "\n\nThis is the only rule: without one the subscription receives no messages." : "";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete rule", $"Delete rule “{row.Name}” from “{Entity.DisplayName}” in {Group.Name}?{last}")) return;
        await RunAsync("Deleting rule…", async ct =>
        {
            await Task.Run(() => ServiceBusOperations.DeleteRuleAsync(Namespace, Entity, row.Name, ct), ct);
            await LoadRulesAsync(ct);
            return $"Deleted rule {row.Name}";
        });
    }

    // ------------------------------------------------------------------ properties

    private async Task LoadPropertiesAsync(CancellationToken ct)
    {
        _properties = await Task.Run(() => ServiceBusOperations.GetPropertiesAsync(Namespace, Entity, ct), ct);
        _propertiesLoaded = true;
        ShowPropertiesForm();
    }

    private void ShowPropertiesForm()
    {
        if (_properties == null) return;
        _form = new PropertyForm(Entity.Kind, _properties, ReadOnly);
        PropertiesHost.Child = _form.View;
        PropertiesError.Visibility = Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateRuntime()
    {
        RuntimeGrid.Children.Clear();
        RuntimeGrid.RowDefinitions.Clear();
        RuntimeGrid.ColumnDefinitions.Clear();
        if (Info is not { } i) return;
        RuntimeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RuntimeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = new List<(string, string)>
        {
            ("Path", Entity.Path),
            ("Status", i.Status),
        };
        if (Entity.HasMessages)
        {
            rows.Add(("Active messages", i.ActiveCount.ToString("N0")));
            rows.Add(("Dead-letter messages", i.DeadLetterCount.ToString("N0")));
            rows.Add(("Transfer messages", i.TransferCount.ToString("N0")));
            rows.Add(("Transfer dead-letter", i.TransferDeadLetterCount.ToString("N0")));
            rows.Add(("Requires session", i.RequiresSession ? "Yes" : "No"));
        }
        else rows.Add(("Subscriptions", i.Subscriptions.Count.ToString("N0")));
        rows.Add(("Scheduled messages", i.ScheduledCount.ToString("N0")));
        if (Entity.Kind != EntityKind.Subscription) rows.Add(("Size", MessageFormatter.Size(i.SizeInBytes)));
        if (i.ForwardTo.Length > 0) rows.Add(("Forwards to", i.ForwardTo));
        if (i.UpdatedAt is { } updated) rows.Add(("Updated", MessageFormatter.Time(updated)));
        if (i.AccessedAt is { } accessed) rows.Add(("Last accessed", MessageFormatter.Time(accessed)));
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        for (var r = 0; r < rows.Count; r++)
        {
            RuntimeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = rows[r].Item1, Foreground = secondary };
            var value = new TextBlock { Text = rows[r].Item2, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(name, r);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            RuntimeGrid.Children.Add(name);
            RuntimeGrid.Children.Add(value);
        }
    }

    private async void SaveProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_form == null || _properties == null || ReadOnly) return;
        try { _form.Apply(); }
        catch (FormatException ex)
        {
            PropertiesError.Text = ex.Message;
            PropertiesError.Visibility = Visibility.Visible;
            return;
        }
        PropertiesError.Visibility = Visibility.Collapsed;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Save settings", $"Update the settings of “{Entity.DisplayName}” in {Group.Name}?", "Save")) return;
        var properties = _properties;
        await RunAsync("Saving settings…", async ct =>
        {
            await Task.Run(() => ServiceBusOperations.UpdatePropertiesAsync(Namespace, properties, ct), ct);
            await LoadPropertiesAsync(ct);
            await RefreshInfoAsync(ct);
            return "Saved settings";
        });
    }
}
