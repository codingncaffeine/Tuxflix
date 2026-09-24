using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Pages;

public partial class NowPlayingPage : UserControl
{
    private TopLevel? _top;

    public NowPlayingPage()
    {
        InitializeComponent();
        Seek.AddHandler(PointerPressedEvent, (_, _) => { if (Model?.Music is { } m) m.IsScrubbing = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerReleasedEvent, (_, _) => EndScrub(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerCaptureLostEvent, (_, _) => EndScrub(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private NowPlayingPageViewModel? Model => DataContext as NowPlayingPageViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnKey);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // Typing in a text box keeps its keys.
        if (Model?.Music is not { } music || e.Source is TextBox || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Space or Key.K:
                music.TogglePauseCommand.Execute(null);
                break;
            case Key.N:
                music.NextCommand.Execute(null);
                break;
            case Key.P:
                music.PreviousCommand.Execute(null);
                break;
            case Key.S:
                music.ToggleShuffleCommand.Execute(null);
                break;
            case Key.R:
                music.CycleRepeatCommand.Execute(null);
                break;
            case Key.M:
                music.ToggleMuteCommand.Execute(null);
                break;
            case Key.Left or Key.J:
                music.SeekTo(Math.Max(0, music.Position - 10));
                break;
            case Key.Right or Key.L:
                music.SeekTo(music.Position + 10);
                break;
            case Key.Up:
                music.ChangeVolume(5);
                break;
            case Key.Down:
                music.ChangeVolume(-5);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void EndScrub()
    {
        if (Model?.Music is not { IsScrubbing: true } music) return;
        music.IsScrubbing = false;
        music.SeekTo(Seek.Value);
    }
}
