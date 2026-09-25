using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Tv.Pages;

/// <summary>
/// Search in the TV interface: the on-screen keyboard types into the window's search text, which
/// searches as it changes (the same pause-then-search the desktop's box uses). A keyboard typed
/// on works too; X deletes and Y types a space while a key has focus.
/// </summary>
public partial class TvSearchPage : UserControl, ITvPage
{
    private ShellViewModel? _shell;
    private bool _syncing;

    public TvSearchPage()
    {
        InitializeComponent();
        Keyboard.PropertyChanged += (_, e) =>
        {
            if (e.Property == OnScreenKeyboard.TextProperty && !_syncing && _shell is not null) _shell.SearchText = Keyboard.Text;
        };
        Keyboard.Done += () => FocusResults();
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Bubble);
    }

    /// <summary>The keyboard, for tests and captures.</summary>
    internal OnScreenKeyboard Keys => Keyboard;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _shell = this.FindAncestorOfType<TvShell>()?.Model?.Shell;
        if (_shell is null) return;
        _shell.PropertyChanged += OnShellChanged;
        Show(_shell.SearchText);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_shell is not null) _shell.PropertyChanged -= OnShellChanged;
        _shell = null;
        base.OnDetachedFromVisualTree(e);
    }

    public Control? InitialFocus() => Keyboard.KeyFor("a");

    public bool Handle(TvInput input)
    {
        if (input.Phase == TvPhase.Release || FocusEngine.Focused(Keyboard) is null) return false;
        switch (input.Action)
        {
            case TvAction.Play:
                Keyboard.Delete();
                return true;
            case TvAction.Search:
                Keyboard.Type(" ");
                return true;
            case TvAction.Back when input.Phase == TvPhase.Press && Keyboard.Text.Length > 0:
                // Back takes letters away first, as a TV keyboard's back does; then it leaves.
                Keyboard.Delete();
                return true;
            default:
                return false;
        }
    }

    public bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Back when Keyboard.Text.Length > 0:
                Keyboard.Delete();
                return true;

            // Typed as text; a focused key must not also be pressed by it.
            case Key.Space:
                return true;
            default:
                return false;
        }
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        Keyboard.Text += e.Text;
        e.Handled = true;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SearchText) && _shell is not null) Show(_shell.SearchText);
    }

    private void Show(string text)
    {
        _syncing = true;
        Keyboard.Text = text;
        _syncing = false;
        Query.Text = text;
        Placeholder.IsVisible = text.Length == 0;
    }

    /// <summary>DONE: over to what was found, if anything was.</summary>
    private void FocusResults()
    {
        var shell = this.FindAncestorOfType<TvShell>();
        var first = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("tile") && b.IsEffectivelyVisible);
        if (first is not null) first.Focus(NavigationMethod.Directional);
        else shell?.Focus();
    }
}
