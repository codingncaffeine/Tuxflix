using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Tuxflix.App.Tv;

/// <summary>
/// A keyboard on the screen, for a remote or a controller: the D-pad moves between keys, A types
/// one, X deletes and Y types a space (Steam Big Picture's buttons), and DONE says the typing is
/// finished. What has been typed is <see cref="Text"/>.
/// </summary>
public partial class OnScreenKeyboard : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<OnScreenKeyboard, string>(nameof(Text), string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>The keys, in order; the letters are typed small unless the capitals key is on.</summary>
    public const string Layout = "abcdefghijklmnopqrstuvwxyz0123456789-'.,&!";

    private readonly List<Button> _letters = [];
    private bool _capitals;

    public OnScreenKeyboard()
    {
        InitializeComponent();
        foreach (var key in Layout)
        {
            var button = new Button
            {
                Theme = Application.Current?.FindResource("TvKey") as Avalonia.Styling.ControlTheme,
                Content = key.ToString(),
                Tag = key.ToString(),
                Margin = new Thickness(4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            button.Click += (_, _) => Type((string)button.Tag!);
            if (char.IsLetter(key)) _letters.Add(button);
            Keys.Items.Add(button);
        }
    }

    /// <summary>The typing is finished (DONE).</summary>
    public event Action? Done;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The key for <paramref name="text"/>, as it is labelled; for tests and for focus.</summary>
    public Button? KeyFor(string text) => Keys.Items.OfType<Button>().FirstOrDefault(b => string.Equals((string?)b.Tag, text, StringComparison.OrdinalIgnoreCase));

    public void Type(string text) => Text += _capitals ? text.ToUpperInvariant() : text;

    public void Delete()
    {
        if (Text.Length > 0) Text = Text[..^1];
    }

    public void Clear() => Text = string.Empty;

    private void OnSpace(object? sender, RoutedEventArgs e) => Text += " ";

    private void OnDelete(object? sender, RoutedEventArgs e) => Delete();

    private void OnClear(object? sender, RoutedEventArgs e) => Clear();

    private void OnDone(object? sender, RoutedEventArgs e) => Done?.Invoke();

    private void OnShift(object? sender, RoutedEventArgs e)
    {
        _capitals = !_capitals;
        ShiftLabel.Text = _capitals ? "abc" : "ABC";
        foreach (var letter in _letters)
        {
            var key = (string)letter.Tag!;
            letter.Content = _capitals ? key.ToUpperInvariant() : key;
        }
    }
}
