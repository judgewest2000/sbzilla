using System.Globalization;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sbzilla.ServiceBus;

namespace Sbzilla.Views;

/// <summary>Dialogs for sending messages and creating entities and rules.</summary>
public static class MessageDialogs
{
    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private static Brush Critical => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
    private static FontFamily Code => (FontFamily)Application.Current.Resources["CodeFont"];

    private static readonly string[] ContentTypes = ["application/json", "text/plain", "application/xml", "application/octet-stream"];

    private static ContentDialog Create(XamlRoot root, string title, object content, string primary) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = new ScrollViewer { Content = content, Padding = new Thickness(0, 0, 16, 0) },
        PrimaryButtonText = primary,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
        Resources = { ["ContentDialogMaxWidth"] = 900.0 },
    };

    private static Grid TwoColumns(params FrameworkElement[] items)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < items.Length; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(items[i], i / 2);
            Grid.SetColumn(items[i], i % 2);
            grid.Children.Add(items[i]);
        }
        return grid;
    }

    private static TextBox Field(string header, string text, string placeholder = "") =>
        new() { Header = header, Text = text, PlaceholderText = placeholder, IsSpellCheckEnabled = false };

    private static TextBlock ErrorText() => new() { Foreground = Critical, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    private static void ShowError(TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ send

    /// <summary>Edits a message to send to <paramref name="target"/>. Returns the draft and number of copies, or null if cancelled.</summary>
    public static async Task<(MessageDraft Draft, int Copies)?> SendAsync(XamlRoot root, string target, MessageDraft initial, bool requiresSession)
    {
        await Dialogs.WhenNoDialogOpenAsync(root);
        var body = new TextBox
        {
            Header = "Body",
            Text = initial.Body,
            Style = (Style)Application.Current.Resources["CodeTextBoxStyle"],
            Height = 220,
            PlaceholderText = "{ \"hello\": \"world\" }   ·   {n} is replaced with the copy number and {guid} with a new GUID",
        };
        var loadButton = new HyperlinkButton { Content = "Load body from file…", Padding = new Thickness(0) };
        var formatButton = new HyperlinkButton { Content = "Format JSON / XML", Padding = new Thickness(0) };
        var bodyLinks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        bodyLinks.Children.Add(loadButton);
        bodyLinks.Children.Add(formatButton);

        var contentType = new ComboBox { Header = "Content type", IsEditable = true, Text = initial.ContentType, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var ct in ContentTypes) contentType.Items.Add(ct);
        var messageId = Field("Message ID", initial.MessageId, "Empty = new GUID per message");
        var subject = Field("Subject (label)", initial.Subject);
        var correlationId = Field("Correlation ID", initial.CorrelationId);
        var sessionId = Field(requiresSession ? "Session ID (required)" : "Session ID", initial.SessionId);
        var partitionKey = Field("Partition key", initial.PartitionKey, "Ignored when a session ID is set");
        var to = Field("To", initial.To);
        var replyTo = Field("Reply to", initial.ReplyTo);
        var replyToSession = Field("Reply to session ID", initial.ReplyToSessionId);
        var ttl = Field("Time to live", initial.TimeToLive is { } t ? EntityPropertyFields.FormatTimeSpan(t) : "", "Entity default — e.g. 1h, 7d");
        var scheduled = Field("Scheduled enqueue time (local)", initial.ScheduledEnqueueTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            "Send now — or e.g. 2026-01-31 09:00, +10m");
        var properties = new TextBox
        {
            Header = "Application properties",
            Text = initial.ApplicationProperties,
            Style = (Style)Application.Current.Resources["CodeTextBoxStyle"],
            Height = 110,
            PlaceholderText = "name = value  (one per line: numbers, true/false and \"quoted strings\" keep their type)",
        };
        var copies = new NumberBox
        {
            Header = "Copies", Value = 1, Minimum = 1, Maximum = 100_000, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140,
        };
        var error = ErrorText();

        loadButton.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Handle);
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            try
            {
                body.Text = await File.ReadAllTextAsync(file.Path);
                var ext = Path.GetExtension(file.Path).ToLowerInvariant();
                contentType.Text = ext switch { ".json" => "application/json", ".xml" => "application/xml", ".txt" => "text/plain", _ => contentType.Text };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(error, ex.Message); }
        };
        formatButton.Click += (_, _) =>
        {
            var (text, _) = MessageFormatter.BodyText(BinaryData.FromString(body.Text), contentType.Text);
            body.Text = text;
        };

        var panel = new StackPanel { Spacing = 10, Width = 760 };
        panel.Children.Add(new TextBlock { Text = $"To {target}", Foreground = Secondary });
        panel.Children.Add(body);
        panel.Children.Add(bodyLinks);
        panel.Children.Add(TwoColumns(contentType, messageId, subject, correlationId, sessionId, partitionKey, to, replyTo, replyToSession, ttl, scheduled, copies));
        panel.Children.Add(properties);
        panel.Children.Add(error);
        body.Loaded += (_, _) => body.Focus(FocusState.Programmatic);

        MessageDraft? draft = null;
        var dialog = Create(root, "Send message", panel, "Send");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var d = new MessageDraft
                {
                    Body = body.Text,
                    ContentType = (contentType.Text ?? "").Trim(),
                    MessageId = messageId.Text.Trim(),
                    Subject = subject.Text.Trim(),
                    CorrelationId = correlationId.Text.Trim(),
                    SessionId = sessionId.Text.Trim(),
                    PartitionKey = partitionKey.Text.Trim(),
                    To = to.Text.Trim(),
                    ReplyTo = replyTo.Text.Trim(),
                    ReplyToSessionId = replyToSession.Text.Trim(),
                    TimeToLive = ttl.Text.Trim().Length == 0 ? null : EntityPropertyFields.ParseTimeSpan(ttl.Text),
                    ScheduledEnqueueTime = ParseSchedule(scheduled.Text),
                    ApplicationProperties = properties.Text,
                };
                MessageDrafts.ParseProperties(d.ApplicationProperties);
                if (requiresSession && d.SessionId.Length == 0) throw new FormatException("This entity requires sessions: enter a session ID.");
                draft = d;
            }
            catch (FormatException ex)
            {
                args.Cancel = true;
                ShowError(error, ex.Message);
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || draft == null) return null;
        return (draft, double.IsNaN(copies.Value) ? 1 : (int)copies.Value);
    }

    /// <summary>Empty → send now; "+10m" → relative; otherwise a local date/time.</summary>
    private static DateTimeOffset? ParseSchedule(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith('+')) return DateTimeOffset.Now + EntityPropertyFields.ParseTimeSpan(s[1..]);
        if (DateTimeOffset.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var at)) return at;
        throw new FormatException($"\"{text}\" isn't a date/time. Use e.g. 2026-01-31 09:00 or +10m.");
    }

    // ------------------------------------------------------------------ resubmit

    /// <summary>Confirms a resubmit. Returns whether to remove the originals, or null if cancelled.</summary>
    public static async Task<bool?> ConfirmResubmitAsync(XamlRoot root, string groupName, EntityRef entity, MessageSource source, int count)
    {
        await Dialogs.WhenNoDialogOpenAsync(root);
        var fromDeadLetter = source != MessageSource.Active;
        var panel = new StackPanel { Spacing = 12, Width = 460 };
        var target = entity.Kind == EntityKind.Subscription ? $"topic “{entity.Topic}”" : $"queue “{entity.Name}”";
        panel.Children.Add(new TextBlock
        {
            Text = $"Send copies of {count} message{(count == 1 ? "" : "s")} from {EntityRef.SourceLabel(source).ToLowerInvariant()} of “{entity.DisplayName}” to {target} in {groupName}?",
            TextWrapping = TextWrapping.Wrap,
        });
        if (entity.Kind == EntityKind.Subscription)
            panel.Children.Add(new TextBlock
            {
                Text = "Messages are sent to the topic, so every subscription whose rules match will receive a copy — not just this one.",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            });
        var remove = new CheckBox
        {
            Content = fromDeadLetter ? "Remove the originals from the dead-letter queue" : "Remove the originals (otherwise they are duplicated)",
            IsChecked = fromDeadLetter,
        };
        panel.Children.Add(remove);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Resubmit messages",
            Content = panel,
            PrimaryButtonText = "Resubmit",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? remove.IsChecked == true : null;
    }

    // ------------------------------------------------------------------ entities

    /// <summary>Asks for a new entity's name, settings and (for subscriptions) first rule. Returns the Create…Options, or null.</summary>
    public static async Task<(object Options, CreateRuleOptions? Rule)?> CreateEntityAsync(XamlRoot root, EntityKind kind, string? topic, string groupName)
    {
        await Dialogs.WhenNoDialogOpenAsync(root);
        var label = EntityRef.KindLabel(kind).ToLowerInvariant();
        var name = Field("Name", "", kind == EntityKind.Subscription ? "e.g. audit" : "e.g. orders");
        object options = kind switch
        {
            EntityKind.Queue => new CreateQueueOptions("placeholder"),
            EntityKind.Topic => new CreateTopicOptions("placeholder"),
            _ => new CreateSubscriptionOptions(topic!, "placeholder"),
        };
        var form = new PropertyForm(kind, options, readOnly: false);
        var filter = new TextBox
        {
            Header = "Default rule filter (SQL, optional)",
            Style = (Style)Application.Current.Resources["CodeTextBoxStyle"],
            Height = 60,
            PlaceholderText = "Empty = all messages (1=1) · e.g. sys.Label = 'Order' AND region = 'EU'",
        };
        var error = ErrorText();

        var panel = new StackPanel { Spacing = 12, Width = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = topic is null ? $"In {groupName}" : $"On topic “{topic}” in {groupName}",
            Foreground = Secondary,
        });
        panel.Children.Add(name);
        if (kind == EntityKind.Subscription) panel.Children.Add(filter);
        panel.Children.Add(new TextBlock { Text = "Settings", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(form.View);
        panel.Children.Add(error);
        name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);

        CreateRuleOptions? rule = null;
        var dialog = Create(root, $"New {label}", panel, "Create");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var n = name.Text.Trim();
                if (n.Length == 0) throw new FormatException("Enter a name.");
                form.Apply();
                switch (options)
                {
                    case CreateQueueOptions q: q.Name = n; break;
                    case CreateTopicOptions t: t.Name = n; break;
                    case CreateSubscriptionOptions s: s.SubscriptionName = n; break;
                }
                rule = filter.Text.Trim().Length > 0
                    ? new CreateRuleOptions(RuleProperties.DefaultRuleName, new SqlRuleFilter(filter.Text.Trim()))
                    : null;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                args.Cancel = true;
                ShowError(error, ex.Message);
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? (options, rule) : null;
    }

    // ------------------------------------------------------------------ rules

    private static readonly string[] FilterKinds = ["SQL filter", "Correlation filter", "True (all messages)", "False (no messages)"];

    /// <summary>Asks for a new subscription rule. Returns null if cancelled.</summary>
    public static async Task<CreateRuleOptions?> AddRuleAsync(XamlRoot root, EntityRef subscription)
    {
        await Dialogs.WhenNoDialogOpenAsync(root);
        var name = Field("Rule name", "");
        var kind = new ComboBox { Header = "Filter type", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var k in FilterKinds) kind.Items.Add(k);
        kind.SelectedIndex = 0;
        var sql = new TextBox
        {
            Header = "SQL filter",
            Style = (Style)Application.Current.Resources["CodeTextBoxStyle"],
            Height = 80,
            PlaceholderText = "e.g. sys.Label = 'Order' AND amount > 100",
        };
        var correlationId = Field("Correlation ID", "");
        var messageId = Field("Message ID", "");
        var subject = Field("Subject (label)", "");
        var to = Field("To", "");
        var replyTo = Field("Reply to", "");
        var sessionId = Field("Session ID", "");
        var replyToSession = Field("Reply to session ID", "");
        var contentType = Field("Content type", "");
        var correlationProps = new TextBox
        {
            Header = "Application properties to match",
            Style = (Style)Application.Current.Resources["CodeTextBoxStyle"],
            Height = 70,
            PlaceholderText = "name = value (one per line)",
        };
        var correlation = new StackPanel { Spacing = 10 };
        correlation.Children.Add(TwoColumns(correlationId, messageId, subject, to, replyTo, sessionId, replyToSession, contentType));
        correlation.Children.Add(correlationProps);
        var action = new TextBox
        {
            Header = "Action (SQL, optional)",
            Style = (Style)Application.Current.Resources["CodeTextBoxStyle"],
            Height = 60,
            PlaceholderText = "e.g. SET priority = 'high'",
        };
        var error = ErrorText();

        void UpdateFields()
        {
            sql.Visibility = kind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            correlation.Visibility = kind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        kind.SelectionChanged += (_, _) => UpdateFields();
        UpdateFields();

        var panel = new StackPanel { Spacing = 12, Width = 560 };
        panel.Children.Add(new TextBlock { Text = $"On subscription “{subscription.DisplayName}”", Foreground = Secondary });
        panel.Children.Add(name);
        panel.Children.Add(kind);
        panel.Children.Add(sql);
        panel.Children.Add(correlation);
        panel.Children.Add(action);
        panel.Children.Add(error);
        name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);

        CreateRuleOptions? result = null;
        var dialog = Create(root, "Add rule", panel, "Add");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var n = name.Text.Trim();
                if (n.Length == 0) throw new FormatException("Enter a rule name.");
                RuleFilter filter;
                switch (kind.SelectedIndex)
                {
                    case 0:
                        if (sql.Text.Trim().Length == 0) throw new FormatException("Enter a SQL filter expression.");
                        filter = new SqlRuleFilter(sql.Text.Trim());
                        break;
                    case 1:
                        static string? V(TextBox b) => b.Text.Trim().Length > 0 ? b.Text.Trim() : null;
                        var c = new CorrelationRuleFilter
                        {
                            CorrelationId = V(correlationId), MessageId = V(messageId), Subject = V(subject), To = V(to),
                            ReplyTo = V(replyTo), SessionId = V(sessionId), ReplyToSessionId = V(replyToSession), ContentType = V(contentType),
                        };
                        foreach (var (key, value) in MessageDrafts.ParseProperties(correlationProps.Text)) c.ApplicationProperties[key] = value;
                        filter = c;
                        break;
                    case 2: filter = new TrueRuleFilter(); break;
                    default: filter = new FalseRuleFilter(); break;
                }
                var options = new CreateRuleOptions(n, filter);
                if (action.Text.Trim().Length > 0) options.Action = new SqlRuleAction(action.Text.Trim());
                result = options;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                args.Cancel = true;
                ShowError(error, ex.Message);
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }
}
