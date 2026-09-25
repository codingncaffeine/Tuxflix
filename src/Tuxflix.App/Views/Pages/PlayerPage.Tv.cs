using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tuxflix.App.Tv;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Pages;

/// <summary>
/// The player from a controller or a remote, the way TV players answer one: with the controls
/// hidden, A plays or pauses, left and right seek, up or down bring the controls up with focus on
/// play; with the controls up the D-pad moves between them and A presses them. A takes the skip
/// or next-episode offer while one shows, as Y always does. The triggers scrub: the bar and its
/// preview move while one is held, and the player seeks when it is let go. The shoulders go a
/// chapter back or on; X opens audio and subtitles, Start the playback settings, B hides the
/// controls and then stops. In the TV interface the controls are drawn half as large again.
/// </summary>
public partial class PlayerPage : ITvPage
{
    /// <summary>How much larger the controls are drawn in the TV interface.</summary>
    internal const double TvScale = 1.5;

    /// <summary>A pulled trigger commits its seek this long after it is let go, so a second pull continues the first.</summary>
    private static readonly TimeSpan ScrubCommitAfter = TimeSpan.FromMilliseconds(450);

    private DispatcherTimer? _scrubCommit;
    private double _scrubTarget;
    private int _scrubSteps;
    private bool _tvLayout;

    /// <summary>The controls are showing.</summary>
    internal bool ControlsShown => !Overlay.Classes.Contains("hidden");

    /// <summary>Where a held trigger has taken the bar; for tests.</summary>
    internal double ScrubTarget => _scrubTarget;

    /// <summary>In the TV interface the controls, and the skip and up-next offers, grow for the distance.</summary>
    private void UseTvLayout()
    {
        if (_tvLayout || this.FindAncestorOfType<TvShell>() is null || Overlay.Parent is not Panel stage) return;
        _tvLayout = true;
        var index = stage.Children.IndexOf(Overlay);
        stage.Children.RemoveAt(index);
        stage.Children.Insert(index, new LayoutTransformControl { LayoutTransform = new ScaleTransform(TvScale, TvScale), Child = Overlay });
        foreach (var offer in stage.Children.Where(c => c.Classes.Contains("skip") || c.Classes.Contains("upnext")))
        {
            offer.RenderTransformOrigin = new RelativePoint(1, 1, RelativeUnit.Relative);
            offer.RenderTransform = new ScaleTransform(TvScale, TvScale);
            offer.Margin = new Thickness(0, 0, offer.Margin.Right * TvScale, offer.Margin.Bottom * TvScale);
        }

        _idle.Interval = TimeSpan.FromSeconds(5);
    }

    bool ITvInputTarget.Handle(TvInput input) => HandleTv(input);

    /// <summary>The picture, not a control: the controls rest hidden until the D-pad asks for them.</summary>
    Control? ITvPage.InitialFocus() => this;

    /// <summary>Carries out a controller's action in the player; false leaves it to the general handling.</summary>
    internal bool HandleTv(TvInput input)
    {
        if (Model is not { } model) return false;
        if (input.Action is TvAction.SeekBack or TvAction.SeekForward)
        {
            Scrub(model, input);
            return true;
        }

        if (input.Phase == TvPhase.Release) return true;
        var inControls = ControlsShown && FocusEngine.Focused(Overlay) is not null;
        switch (input.Action)
        {
            case TvAction.Select when model.ShowsUpNext:
                model.PlayNextCommand.Execute(null);
                return true;
            case TvAction.Select when model.ShowsSkip:
                model.SkipCommand.Execute(null);
                return true;
            case TvAction.Select when inControls:
                ShowControls();
                return false;
            case TvAction.Select:
                if (input.Phase == TvPhase.Press) model.TogglePauseCommand.Execute(null);
                ShowControls();
                return true;
            case TvAction.Left or TvAction.Right when !inControls:
                model.SeekBy(input.Action == TvAction.Left ? -10 : 30);
                ShowControls();
                return true;
            case TvAction.Up or TvAction.Down when !inControls:
                ShowControls();
                if (PlayPauseButton() is { } play) play.Focus(NavigationMethod.Directional);
                return true;
            case TvAction.Up or TvAction.Down or TvAction.Left or TvAction.Right:
                ShowControls();
                return false;
            case TvAction.Back when ControlsShown:
                HideControlsNow();
                return true;
            case TvAction.Back:
                model.LeaveCommand.Execute(null);
                return true;
            case TvAction.Play:
                OpenTracks();
                return true;
            case TvAction.Search when model.ShowsUpNext:
                model.PlayNextCommand.Execute(null);
                return true;
            case TvAction.Search when model.ShowsSkip:
                model.SkipCommand.Execute(null);
                return true;
            case TvAction.Search:
                if (ControlsShown) HideControlsNow();
                else ShowControls();
                return true;
            case TvAction.Menu:
                Enlarge(OpenSettings());
                return true;
            case TvAction.PageLeft or TvAction.PageRight:
                Chapter(model, input.Action == TvAction.PageRight ? 1 : -1);
                ShowControls();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Audio and subtitles in one menu, each track a choice, for a controller.</summary>
    internal MenuFlyout? OpenTracks()
    {
        if (Model is not { } model) return null;
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        menu.Items.Add(Tracks("Audio", model.AudioTracks));
        menu.Items.Add(Tracks("Subtitles", model.SubtitleTracks));
        KeepControlsWhileOpen(menu);
        ShowControls();
        menu.ShowAt(SettingsButton);
        Enlarge(menu);
        return menu;
    }

    private static MenuItem Tracks(string heading, IReadOnlyList<TrackOption> options)
    {
        var chosen = options.FirstOrDefault(o => o.IsSelected)?.Label;
        var item = new MenuItem { Header = chosen is null ? heading : $"{heading}: {chosen}", IsEnabled = options.Count > 0 };
        foreach (var option in options)
        {
            item.Items.Add(new MenuItem
            {
                Header = option.Label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = option.IsSelected,
                Command = option.ChooseCommand,
            });
        }

        return item;
    }

    /// <summary>Menus in the TV interface are read from across the room.</summary>
    private void Enlarge(MenuFlyout? menu)
    {
        if (menu is null || !_tvLayout) return;
        foreach (var item in menu.Items.OfType<MenuItem>()) Enlarge(item);
    }

    private static void Enlarge(MenuItem item)
    {
        item.FontSize = 22;
        item.Padding = new Thickness(18, 12);
        foreach (var child in item.Items.OfType<MenuItem>()) Enlarge(child);
    }

    private Button? PlayPauseButton() =>
        BottomBar.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Model is { } model && ReferenceEquals(b.Command, model.TogglePauseCommand));

    private void HideControlsNow()
    {
        Focus(NavigationMethod.Directional);
        Overlay.Classes.Add("hidden");
        Cursor = new Cursor(StandardCursorType.None);
    }

    /// <summary>The start of the next chapter, or of this one (or the one before, near its start), as a remote's skip does.</summary>
    private static void Chapter(PlayerPageViewModel model, int direction)
    {
        var starts = model.Chapters.Select(c => c.Chapter.StartTimeOffset / 1000.0).Order().ToList();
        if (starts.Count < 2)
        {
            model.SeekBy(direction * 60);
            return;
        }

        var now = model.Position;
        var target = direction > 0
            ? starts.FirstOrDefault(s => s > now + 1, -1)
            : starts.LastOrDefault(s => s < now - 3, 0);
        if (target >= 0) model.SeekTo(target);
    }

    /// <summary>
    /// A trigger held moves the bar, faster the longer and deeper it is held, with the preview of
    /// where it points; let go, and a moment later the player seeks there.
    /// </summary>
    private void Scrub(PlayerPageViewModel model, TvInput input)
    {
        var forward = input.Action == TvAction.SeekForward;
        if (input.Phase == TvPhase.Release)
        {
            _scrubCommit ??= new DispatcherTimer(ScrubCommitAfter, DispatcherPriority.Input, (_, _) => CommitScrub());
            _scrubCommit.Stop();
            _scrubCommit.Start();
            return;
        }

        _scrubCommit?.Stop();
        if (!model.IsScrubbing)
        {
            model.IsScrubbing = true;
            _scrubTarget = model.Position;
            _scrubSteps = 0;
        }

        _scrubSteps++;
        var step = (_scrubSteps < 4 ? 10 : _scrubSteps < 10 ? 30 : 60) * Math.Clamp(0.5 + input.Strength, 0.85, 1.5);
        _scrubTarget = Math.Clamp(_scrubTarget + (forward ? step : -step), 0, model.SeekMaximum);
        model.SeekValue = _scrubTarget;
        ShowControls();
        if (model.Previews is { } previews && Seek.GetVisualDescendants().OfType<Track>().FirstOrDefault() is { } track)
        {
            previews.Show(_scrubTarget);
            PlacePreview(track, _scrubTarget / Math.Max(1, model.SeekMaximum));
        }
    }

    private void CommitScrub()
    {
        _scrubCommit?.Stop();
        if (Model is not { IsScrubbing: true } model) return;
        model.IsScrubbing = false;
        model.Previews?.Hide();
        model.SeekTo(_scrubTarget);
        _scrubSteps = 0;
    }
}
