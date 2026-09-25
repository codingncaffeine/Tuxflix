using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tuxflix.App.Views;

/// <summary>
/// What a page says when it has nothing to show, or could not be loaded: a picture, a heading, a
/// line saying what to do, and a button for it (TRY AGAIN under a failure).
/// </summary>
public partial class EmptyState : UserControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<EmptyState, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Heading));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Text));

    public static readonly StyledProperty<string?> ActionTextProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(ActionText));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<EmptyState, ICommand?>(nameof(Command));

    /// <summary>A failure rather than an absence: the picture takes the accent.</summary>
    public static readonly StyledProperty<bool> IsProblemProperty =
        AvaloniaProperty.Register<EmptyState, bool>(nameof(IsProblem));

    public EmptyState() => InitializeComponent();

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? ActionText
    {
        get => GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool IsProblem
    {
        get => GetValue(IsProblemProperty);
        set => SetValue(IsProblemProperty, value);
    }

    /// <summary>The button under the words, for probes and tests.</summary>
    internal Button Action => ActionButton;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Picture is null) return;
        if (change.Property == IconProperty)
        {
            Picture.Data = Icon;
            Picture.IsVisible = Icon is not null;
        }
        else if (change.Property == HeadingProperty)
        {
            HeadingText.Text = Heading;
            HeadingText.IsVisible = !string.IsNullOrEmpty(Heading);
        }
        else if (change.Property == TextProperty)
        {
            LineText.Text = Text;
        }
        else if (change.Property == ActionTextProperty || change.Property == CommandProperty)
        {
            ActionButton.Content = ActionText;
            ActionButton.Command = Command;
            ActionButton.IsVisible = !string.IsNullOrEmpty(ActionText) && Command is not null;
        }
        else if (change.Property == IsProblemProperty)
        {
            Picture.Foreground = this.FindResource(IsProblem ? "Brush.Accent" : "Brush.Text.Disabled") as IBrush;
        }
    }
}
