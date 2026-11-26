using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sbzilla.Models;
using Sbzilla.Services;
using Sbzilla.ServiceBus;

namespace Sbzilla.Views;

/// <summary>Code-built ContentDialogs used across the app.</summary>
public static class Dialogs
{
    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static ContentDialog Create(XamlRoot root, string title, object content, string primary, string? secondary = null) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        SecondaryButtonText = secondary ?? "",
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
    };

    /// <summary>WinUI allows one ContentDialog at a time; dialogs that can pop up unprompted (updates, files opened from Explorer) wait their turn.</summary>
    public static async Task WhenNoDialogOpenAsync(XamlRoot root)
    {
        while (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.Child is ContentDialog))
            await Task.Delay(250);
    }

    public static async Task ShowErrorAsync(XamlRoot root, string title, string message)
    {
        await WhenNoDialogOpenAsync(root);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm = "Delete")
    {
        await WhenNoDialogOpenAsync(root);
        var dialog = Create(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, confirm);
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>For irreversible operations: the confirm button stays disabled until <paramref name="expected"/> is typed exactly.</summary>
    public static async Task<bool> ConfirmTypedAsync(XamlRoot root, string title, string message, string expected, string confirm = "Delete")
    {
        await WhenNoDialogOpenAsync(root);
        var box = new TextBox { Header = $"Type “{expected}” to confirm", IsSpellCheckEnabled = false };
        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dialog = Create(root, title, panel, confirm);
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.IsPrimaryButtonEnabled = false;
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim() == expected;
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Asks for a single line of text; returns null if cancelled.</summary>
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string header, string text = "", string confirm = "OK")
    {
        await WhenNoDialogOpenAsync(root);
        var box = new TextBox { Header = header, Text = text, Width = 420, IsSpellCheckEnabled = false };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dialog = Create(root, title, box, confirm);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    // ------------------------------------------------------------------ groups

    /// <summary>Edits a group's name, colour and read-only flag in place. Returns false if cancelled.</summary>
    public static async Task<bool> EditGroupAsync(XamlRoot root, NamespaceGroup group, bool isNew)
    {
        var nameBox = new TextBox { Header = "Name", Text = group.Name, PlaceholderText = "e.g. Production" };
        var color = GroupColors.Parse(group.Color);

        var preview = new Border { Height = 36, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 0, 10, 0) };
        var previewText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        preview.Child = previewText;

        var picker = new ColorPicker
        {
            Color = color,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            Visibility = Visibility.Collapsed,
        };

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void UpdatePreview()
        {
            preview.Background = new SolidColorBrush(color);
            previewText.Foreground = new SolidColorBrush(GroupColors.ContrastText(color));
            previewText.Text = $"{(nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "Group")} · tabs use this colour";
            foreach (var b in swatches.Children.OfType<Button>())
                b.BorderBrush = new SolidColorBrush(GroupColors.ToHex(color) == (string)b.Tag ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent);
        }
        foreach (var hex in GroupColors.Presets)
        {
            var swatch = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), Tag = hex,
                Background = GroupColors.Brush(hex), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(14),
            };
            ToolTipService.SetToolTip(swatch, hex);
            swatch.Click += (_, _) => { color = GroupColors.Parse(hex); picker.Color = color; UpdatePreview(); };
            swatches.Children.Add(swatch);
        }
        var customButton = new HyperlinkButton { Content = "Custom colour…", Padding = new Thickness(4, 0, 4, 0) };
        customButton.Click += (_, _) => picker.Visibility = picker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        swatches.Children.Add(customButton);
        picker.ColorChanged += (_, args) => { color = Windows.UI.Color.FromArgb(255, args.NewColor.R, args.NewColor.G, args.NewColor.B); UpdatePreview(); };
        nameBox.TextChanged += (_, _) => UpdatePreview();

        var readOnly = new ToggleSwitch
        {
            Header = "Read-only",
            IsOn = group.IsReadOnly,
            OnContent = "Browse and peek only — no sending, purging, deleting or editing",
            OffContent = "Off",
        };

        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Colour" });
        panel.Children.Add(swatches);
        panel.Children.Add(picker);
        panel.Children.Add(preview);
        panel.Children.Add(readOnly);
        UpdatePreview();
        nameBox.Loaded += (_, _) => { nameBox.Focus(FocusState.Programmatic); nameBox.SelectAll(); };

        var dialog = Create(root, isNew ? "New group" : "Edit group", new ScrollViewer { Content = panel }, isNew ? "Create" : "Save");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        group.Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "New Group";
        group.Color = GroupColors.ToHex(color);
        group.IsReadOnly = readOnly.IsOn;
        return true;
    }

    // ------------------------------------------------------------------ namespaces

    private static readonly AuthMode[] AuthModes = Enum.GetValues<AuthMode>();

    /// <summary>Edits a copy of <paramref name="ns"/>. Returns the edited copy and chosen group, or null if cancelled.</summary>
    public static async Task<(NamespaceConnection Namespace, NamespaceGroup Group)?> EditNamespaceAsync(
        XamlRoot root, NamespaceConnection ns, IReadOnlyList<NamespaceGroup> groups, NamespaceGroup group, bool isNew)
    {
        var s = ns.Clone();

        var groupBox = new ComboBox { Header = "Group", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var g in groups)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            item.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = GroupColors.Brush(g.Color), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = g.LabelWithReadOnly });
            groupBox.Items.Add(item);
        }
        groupBox.SelectedIndex = Math.Max(0, groups.ToList().IndexOf(group));

        var nameBox = new TextBox { Header = "Display name (optional)", Text = s.Name, PlaceholderText = "Defaults to the namespace name" };
        var authBox = new ComboBox { Header = "Authentication", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var mode in AuthModes) authBox.Items.Add(NamespaceConnection.AuthLabel(mode));
        authBox.SelectedIndex = Array.IndexOf(AuthModes, s.AuthMode);
        var connectionStringBox = new TextBox
        {
            Header = "Connection string",
            Text = s.ConnectionString,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            FontFamily = (FontFamily)Application.Current.Resources["CodeFont"],
            IsSpellCheckEnabled = false,
            PlaceholderText = "Endpoint=sb://mynamespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=…",
        };
        var namespaceBox = new TextBox { Header = "Namespace", Text = s.Namespace, PlaceholderText = "mynamespace or mynamespace.servicebus.windows.net" };
        var tenantBox = new TextBox { Header = "Tenant ID", Text = s.TenantId, PlaceholderText = "Optional for interactive sign-in" };
        var clientBox = new TextBox { Header = "Application (client) ID", Text = s.ClientId };
        var secretBox = new PasswordBox { Header = "Client secret", Password = s.ClientSecret };
        var webSocketsBox = new CheckBox { Content = "Use AMQP over WebSockets (port 443 — for networks that block 5671)", IsChecked = s.UseWebSockets };
        var hint = new TextBlock { Foreground = Secondary, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var testButton = new Button { Content = "Test connection" };
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true };
        var testRow = new Grid { ColumnSpacing = 12 };
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(testResult, 1);
        testRow.Children.Add(testButton);
        testRow.Children.Add(testResult);

        AuthMode Mode() => authBox.SelectedIndex >= 0 ? AuthModes[authBox.SelectedIndex] : AuthMode.ConnectionString;

        void UpdateFields()
        {
            var mode = Mode();
            Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
            connectionStringBox.Visibility = Show(mode == AuthMode.ConnectionString);
            namespaceBox.Visibility = Show(mode != AuthMode.ConnectionString);
            tenantBox.Visibility = Show(mode is AuthMode.EntraInteractive or AuthMode.EntraServicePrincipal);
            tenantBox.PlaceholderText = mode == AuthMode.EntraServicePrincipal ? "Directory (tenant) ID" : "Optional for interactive sign-in";
            clientBox.Visibility = secretBox.Visibility = Show(mode == AuthMode.EntraServicePrincipal);
            hint.Text = mode switch
            {
                AuthMode.ConnectionString => "A namespace (or entity-scoped) shared access connection string. Browsing needs Manage rights; entity-scoped strings show just that entity.",
                AuthMode.EntraInteractive => "Signs in through the Windows account broker, with MFA if required. You need an Azure Service Bus Data Owner/Sender/Receiver role, plus Contributor to browse and edit entities.",
                AuthMode.EntraDefault => "Uses the first available of: environment variables, managed identity, Visual Studio, Azure CLI (az login), Azure PowerShell, Azure Developer CLI.",
                AuthMode.EntraServicePrincipal => "An app registration with a client secret, granted Service Bus data roles on the namespace.",
                _ => "",
            };
        }

        NamespaceConnection Read()
        {
            var r = s.Clone();
            r.Name = nameBox.Text.Trim();
            r.AuthMode = Mode();
            r.ConnectionString = connectionStringBox.Text.Trim();
            r.Namespace = namespaceBox.Text.Trim();
            r.TenantId = tenantBox.Text.Trim();
            r.ClientId = clientBox.Text.Trim();
            r.ClientSecret = secretBox.Password;
            r.UseWebSockets = webSocketsBox.IsChecked == true;
            return r;
        }

        string? Missing(NamespaceConnection r) => r.AuthMode switch
        {
            AuthMode.ConnectionString when r.ConnectionString.Length == 0 => "Enter a connection string.",
            AuthMode.ConnectionString when r.ResolvedNamespace.Length == 0 => "The connection string has no Endpoint=sb://… part.",
            not AuthMode.ConnectionString when r.ResolvedNamespace.Length == 0 => "Enter a namespace.",
            AuthMode.EntraServicePrincipal when r.TenantId.Length == 0 || r.ClientId.Length == 0 || r.ClientSecret.Length == 0 =>
                "Enter the tenant ID, client ID and client secret.",
            _ => null,
        };

        void ShowError(string text)
        {
            testResult.Text = text;
            testResult.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }

        authBox.SelectionChanged += (_, _) => UpdateFields();
        CancellationTokenSource? testCts = null;
        testButton.Click += async (_, _) =>
        {
            var candidate = Read();
            if (Missing(candidate) is { } missing) { ShowError(missing); return; }
            testCts?.Cancel();
            var cts = testCts = new CancellationTokenSource();
            testButton.IsEnabled = false;
            testResult.Foreground = Secondary;
            testResult.Text = "Connecting…";
            try
            {
                var snapshot = await Task.Run(() => ServiceBusOperations.LoadAsync(candidate, cts.Token));
                var subs = snapshot.Topics.Sum(t => t.Subscriptions.Count);
                testResult.Text = $"Connected to {candidate.ResolvedNamespace}: {snapshot.Queues.Count} queue(s), {snapshot.Topics.Count} topic(s), {subs} subscription(s).";
                testResult.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            }
            catch (Exception ex) when (!cts.IsCancellationRequested) { ShowError(ex.Message); }
            catch (OperationCanceledException) { }
            finally
            {
                ServiceBusClients.Reset(candidate.Id);
                testButton.IsEnabled = true;
            }
        };

        var panel = new StackPanel { Spacing = 12, Width = 500 };
        panel.Children.Add(groupBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(authBox);
        panel.Children.Add(hint);
        panel.Children.Add(connectionStringBox);
        panel.Children.Add(namespaceBox);
        panel.Children.Add(tenantBox);
        panel.Children.Add(clientBox);
        panel.Children.Add(secretBox);
        panel.Children.Add(webSocketsBox);
        panel.Children.Add(testRow);
        UpdateFields();

        var dialog = Create(root, isNew ? "New namespace" : "Edit namespace", new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 16, 0) }, isNew ? "Add" : "Save");
        dialog.Resources["ContentDialogMaxWidth"] = 700.0;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (Missing(Read()) is not { } missing) return;
            args.Cancel = true;
            ShowError(missing);
        };
        var result = await dialog.ShowAsync();
        testCts?.Cancel();
        if (result != ContentDialogResult.Primary) return null;
        return (Read(), groups[Math.Max(0, groupBox.SelectedIndex)]);
    }

    // ------------------------------------------------------------------ about & updates

    /// <summary>Shows About. Returns an update if the user found one via "Check for updates".</summary>
    public static async Task<UpdateInfo?> ShowAboutAsync(XamlRoot root)
    {
        var version = UpdateService.CurrentVersion.ToString(3);

        var art = new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AboutArt.png"))),
            Width = 200, Height = 200,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var shortcuts = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string Keys, string Action)[] keys =
        [
            ("F5", "Refresh messages and counts"),
            ("Ctrl+N", "Send a message to the current entity"),
            ("Ctrl+Shift+F", "Search namespaces and entities"),
            ("Ctrl+F", "Filter the peeked messages"),
            ("Ctrl+W", "Close tab"),
            ("Ctrl+C", "Copy selected messages as JSON"),
            ("Delete", "Delete selected messages"),
        ];
        for (var i = 0; i < keys.Length; i++)
        {
            shortcuts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = keys[i].Keys, FontFamily = (FontFamily)Application.Current.Resources["CodeFont"] };
            var a = new TextBlock { Text = keys[i].Action, Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, i); Grid.SetRow(a, i); Grid.SetColumn(a, 1);
            shortcuts.Children.Add(k); shortcuts.Children.Add(a);
        }

        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(art);
        panel.Children.Add(new TextBlock
        {
            Text = "Sbzilla", HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock { Text = $"Version {version}", Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "A lightweight Azure Service Bus explorer — colour-coded namespace groups, Entra ID sign-in, peek, send, resubmit and purge.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock { Text = "Keyboard shortcuts", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(shortcuts);
        panel.Children.Add(new TextBlock
        {
            Text = "Copyright (c) 2026 James West. Released under the MIT License.",
            Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(new HyperlinkButton
        {
            Content = $"github.com/{UpdateService.Repo}",
            NavigateUri = new Uri($"https://github.com/{UpdateService.Repo}"),
            Padding = new Thickness(0),
        });

        var about = new ScrollViewer { Content = panel };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = about,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        // WinUI shows one ContentDialog at a time, so release notes replace the About content rather than stacking.
        var notesButton = new Button { Content = "Release notes" };
        notesButton.Click += (_, _) => dialog.Content = BuildReleaseNotes(() => dialog.Content = about);

        UpdateInfo? found = null;
        var checkResult = new TextBlock { Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
        var checkButton = new Button { Content = "Check for updates" };
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            checkResult.Text = "Checking…";
            try
            {
                found = await UpdateService.CheckAsync(CancellationToken.None);
                if (found is null) checkResult.Text = "You're up to date.";
                else dialog.Hide();
            }
            catch (Exception ex) { checkResult.Text = $"Couldn't check for updates: {ex.Message}"; }
            finally { checkButton.IsEnabled = true; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(checkButton);
        buttons.Children.Add(notesButton);
        panel.Children.Add(buttons);
        panel.Children.Add(checkResult);

        await dialog.ShowAsync();
        return found;
    }

    /// <summary>Copies CHANGELOG.md wherever the user picks; returns a status line (shown inline because only one ContentDialog can be open).</summary>
    private static async Task<string> ExportReleaseNotesAsync(string changelogPath)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "Sbzilla release notes" };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Handle);
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return "";
        try
        {
            File.Copy(changelogPath, file.Path, overwrite: true);
            return $"Saved to {file.Path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>What changed in each version, read from the CHANGELOG.md shipped next to the exe.</summary>
    private static FrameworkElement BuildReleaseNotes(Action back)
    {
        var panel = new StackPanel { Spacing = 4, Width = 420 };
        var backButton = new HyperlinkButton { Content = "← Back to About", Padding = new Thickness(0) };
        backButton.Click += (_, _) => back();
        panel.Children.Add(backButton);
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock { Text = "Release notes", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        var changelogPath = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        var exportStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Secondary, Visibility = Visibility.Collapsed };
        var exportButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new FontIcon { Glyph = "\uEDE1", FontSize = 14 }, new TextBlock { Text = "Export…" } },
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(exportButton, "Save the full release notes as a Markdown file");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(exportButton, "Export release notes");
        exportButton.Click += async (_, _) =>
        {
            exportStatus.Text = await ExportReleaseNotesAsync(changelogPath);
            exportStatus.Visibility = exportStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        Grid.SetColumn(exportButton, 1);
        titleRow.Children.Add(exportButton);
        panel.Children.Add(titleRow);
        panel.Children.Add(exportStatus);

        IReadOnlyList<ReleaseNote> releases;
        try { releases = ReleaseNotes.Parse(File.ReadAllText(changelogPath)); }
        catch (IOException) { releases = []; }
        if (releases.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Release notes aren't available.", Foreground = Secondary });
            exportButton.IsEnabled = false;
        }

        var current = UpdateService.CurrentVersion.ToString(3);
        var code = (FontFamily)Application.Current.Resources["CodeFont"];
        foreach (var release in releases)
        {
            var heading = new TextBlock { Margin = new Thickness(0, 12, 0, 2), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            heading.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"Version {release.Version}" });
            if (release.Date is not null)
                heading.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"   {release.Date}", Foreground = Secondary, FontWeight = Microsoft.UI.Text.FontWeights.Normal });
            if (release.Version == current)
                heading.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "   (this version)", Foreground = Secondary, FontWeight = Microsoft.UI.Text.FontWeights.Normal });
            panel.Children.Add(heading);

            foreach (var item in release.Items)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12 + item.Level * 16, 0, 0, 0) };
                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = item.Level == 0 ? "•  " : "◦  " });
                foreach (var span in item.Spans)
                {
                    var run = new Microsoft.UI.Xaml.Documents.Run { Text = span.Text };
                    switch (span.Style)
                    {
                        case NoteStyle.Bold: run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; break;
                        case NoteStyle.Italic: run.FontStyle = Windows.UI.Text.FontStyle.Italic; break;
                        case NoteStyle.Code: run.FontFamily = code; break;
                    }
                    line.Inlines.Add(run);
                }
                panel.Children.Add(line);
            }
        }
        return new ScrollViewer { Content = panel };
    }

    /// <summary>Asks to install an update; on accept downloads it, launches the installer and exits the app.</summary>
    public static async Task OfferUpdateAsync(XamlRoot root, UpdateInfo update)
    {
        await WhenNoDialogOpenAsync(root);
        var message = new TextBlock
        {
            Text = $"Sbzilla {update.Version} is available (you have {UpdateService.CurrentVersion.ToString(3)}). Update now? Sbzilla will restart.",
            TextWrapping = TextWrapping.Wrap,
        };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(message);
        panel.Children.Add(progress);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Update available",
            Content = panel,
            PrimaryButtonText = "Update now",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            message.Text = $"Downloading Sbzilla {update.Version}…";
            try
            {
                var installer = await UpdateService.DownloadAsync(update, CancellationToken.None);
                UpdateService.StartInstaller(installer);
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                progress.Visibility = Visibility.Collapsed;
                message.Text = $"Update failed: {ex.Message}";
            }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
}
