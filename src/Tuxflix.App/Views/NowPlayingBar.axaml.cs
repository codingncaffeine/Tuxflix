using Avalonia.Controls;
using Avalonia.Interactivity;
using Tuxflix.App.Music;

namespace Tuxflix.App.Views;

public partial class NowPlayingBar : UserControl
{
    public NowPlayingBar()
    {
        InitializeComponent();

        // The seek bar follows playback except while the listener holds it; letting go seeks.
        Seek.AddHandler(PointerPressedEvent, (_, _) => { if (Model is { } m) m.IsScrubbing = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerReleasedEvent, (_, _) => EndScrub(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Seek.AddHandler(PointerCaptureLostEvent, (_, _) => EndScrub(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private MusicPlayer? Model => DataContext as MusicPlayer;

    private void EndScrub()
    {
        if (Model is not { IsScrubbing: true } model) return;
        model.IsScrubbing = false;
        model.SeekTo(Seek.Value);
    }
}
