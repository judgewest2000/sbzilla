using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using WinUIEditor;

namespace Sbzilla.Views;

/// <summary>
/// Native Scintilla code editor (WinUIEdit) with JSON / XML highlighting and a Ctrl+F find bar.
/// Offsets exposed to callers are UTF-16 offsets into LF-normalised <see cref="Text"/>.
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private const string FontName = "Cascadia Mono";
    private const int FontPoints = 10;

    private readonly CodeEditorControl _control = new();
    private readonly TextBlock _placeholderBlock;
    private string? _text = "";   // null = stale, re-read from the editor on demand
    private string _language = "plaintext";
    private bool _readOnly;
    private bool _wordWrap;
    private bool _closed;

    public CodeEditor()
    {
        _placeholderBlock = new TextBlock
        {
            IsHitTestVisible = false,
            FontFamily = new FontFamily(FontName),
            FontSize = 13,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        };
        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0, 1, 1, 1),
            Child = new Grid { Children = { _control, _placeholderBlock, CreateFindBar() } },
        };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        InitEditor();
        PreviewKeyDown += OnPreviewKeyDown;
        _control.Loaded += OnControlLoaded;
    }

    public event EventHandler? TextChanged;

    public string Text
    {
        get => _text ??= Normalize(Editor.GetText(Editor.Length));
        set
        {
            value = Normalize(value);
            if (value == Text) return;
            WithWritable(() =>
            {
                Editor.SetText(value);
                Editor.EmptyUndoBuffer();
                Editor.GotoPos(0);
            });
            _text = value;
            UpdatePlaceholder();
        }
    }

    /// <summary>"json", "xml" or "plaintext".</summary>
    public string CodeLanguage
    {
        get => _language;
        set
        {
            if (value == _language) return;
            _language = value;
            ApplyLanguage();
        }
    }

    public string Placeholder
    {
        get => _placeholderBlock.Text;
        set
        {
            _placeholderBlock.Text = value;
            UpdatePlaceholder();
        }
    }

    public bool IsReadOnly
    {
        get => _readOnly;
        set { _readOnly = value; Editor.ReadOnly = value; }
    }

    public bool WordWrap
    {
        get => _wordWrap;
        set { _wordWrap = value; Editor.WrapMode = value ? Wrap.Word : Wrap.None; }
    }

    /// <summary>Sets the text and the highlighting language together, so the old language never styles the new text.</summary>
    public void SetContent(string text, string language)
    {
        CodeLanguage = language;
        Text = text;
    }

    public void FocusEditor()
    {
        _control.Focus(FocusState.Programmatic);
        Editor.Focus = true;
    }

    /// <summary>
    /// Detaches from WinUIEdit; required for the editor to be collected. WinUIEdit's events aren't visible to the
    /// XAML reference tracker, so their handlers strongly root this control in a cycle via <see cref="_control"/>.
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Editor.Modified -= OnModified;
        _control.SyntaxHighlightingApplied -= OnSyntaxHighlightingApplied;
        _control.DefaultColorsChanged -= OnDefaultColorsChanged;
        _control.Loaded -= OnControlLoaded;
        PreviewKeyDown -= OnPreviewKeyDown;
    }

    private Editor Editor => _control.Editor;

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static int Bgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private void InitEditor()
    {
        var e = Editor;
        e.EOLMode = EndOfLine.Lf;
        e.PasteConvertEndings = true;
        e.TabWidth = 2;
        e.UseTabs = false;
        e.ModEventMask = ModificationFlags.InsertText | ModificationFlags.DeleteText;
        e.Modified += OnModified;
        _control.SyntaxHighlightingApplied += OnSyntaxHighlightingApplied;
        _control.DefaultColorsChanged += OnDefaultColorsChanged;
        InitFindIndicator();
        ApplyLanguage();
    }

    private void OnControlLoaded(object sender, RoutedEventArgs args) => UpdatePlaceholder();

    private void OnSyntaxHighlightingApplied(object? sender, ElementTheme args) => ApplyFont();

    private void OnDefaultColorsChanged(object? sender, ElementTheme args)
    {
        ApplyFont();
        ApplyFindColors();
    }

    private void ApplyLanguage()
    {
        _control.HighlightingLanguage = _language is "json" or "xml" ? _language : "plaintext";
        ApplyFont();
    }

    private void ApplyFont()
    {
        for (var style = 0; style < 40; style++)
        {
            Editor.StyleSetFont(style, FontName);
            Editor.StyleSetSize(style, FontPoints);
        }
        DispatcherQueue.TryEnqueue(UpdatePlaceholder);
    }

    private void WithWritable(Action edit)
    {
        Editor.ReadOnly = false;
        try { edit(); }
        finally { Editor.ReadOnly = _readOnly; }
    }

    private void OnModified(Editor sender, ModifiedEventArgs args)
    {
        if ((args.ModificationType & (int)(ModificationFlags.InsertText | ModificationFlags.DeleteText)) == 0) return;
        _text = null;
        QueueFindRefresh();
        UpdatePlaceholder();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePlaceholder()
    {
        _placeholderBlock.Visibility = Editor.Length == 0 && !string.IsNullOrEmpty(_placeholderBlock.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_placeholderBlock.Visibility == Visibility.Collapsed || XamlRoot == null) return;
        var px = 0;
        for (var m = 0; m < 5; m++) px += Editor.GetMarginWidthN(m);
        _placeholderBlock.Margin = new Thickness((px + 4) / XamlRoot.RasterizationScale, 1, 4, 0);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleFindKey(e.Key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu))) e.Handled = true;
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
