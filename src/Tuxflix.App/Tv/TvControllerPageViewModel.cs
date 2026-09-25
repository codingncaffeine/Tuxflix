using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Tv;

/// <summary>
/// What each controller button does, and a press to change it: choose an action, then press the
/// button that should do it. The map is saved with the settings at once.
/// </summary>
public sealed partial class TvControllerPageViewModel : PageViewModel
{
    /// <summary>How long a row waits for a press before it gives up and changes nothing.</summary>
    public static readonly TimeSpan ListenFor = TimeSpan.FromSeconds(8);

    private readonly ShellViewModel _shell;
    private DispatcherTimer? _listening;

    public TvControllerPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Rows = [.. GamepadMap.Actions.Select(action => new ControllerRowViewModel(this, action))];
        Refresh(Map);
        Status = Describe();
    }

    public override string Title => "Controller";

    public override bool ShowsRail => false;

    public ObservableCollection<ControllerRowViewModel> Rows { get; }

    /// <summary>Which controllers are connected, or why none can be.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; }

    /// <summary>What the last change did, or what the page is waiting for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    /// <summary>The row waiting for a button, if one is.</summary>
    public ControllerRowViewModel? Waiting { get; private set; }

    /// <summary>The map in use: the controller thread's, or the saved one when controllers are not read.</summary>
    internal GamepadMap Map => _shell.Gamepads?.Map ?? GamepadMap.FromSettings(_shell.Settings.Tv.Gamepad);

    protected override Task LoadAsync(CancellationToken cancellation)
    {
        if (_shell.Gamepads is { } pads) pads.PadsChanged += OnPadsChanged;
        Status = Describe();
        return Task.CompletedTask;
    }

    public override void Deactivate()
    {
        StopListening(null);
        if (_shell.Gamepads is { } pads) pads.PadsChanged -= OnPadsChanged;
        base.Deactivate();
    }

    /// <summary>The next button pressed will do <paramref name="row"/>'s action.</summary>
    internal void Listen(ControllerRowViewModel row)
    {
        if (_shell.Gamepads is not { Unavailable: null } pads)
        {
            Notice = "There is no controller to listen to. " + Describe();
            return;
        }

        StopListening(null);
        Waiting = row;
        row.IsWaiting = true;
        Notice = $"Press the button that should do {GamepadMap.Title(row.Action)}.";
        pads.CaptureNext(button => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(Waiting, row)) Assign(row.Action, button);
        }));
        _listening = new DispatcherTimer { Interval = ListenFor };
        _listening.Tick += (_, _) => StopListening("Nothing was pressed; the buttons stay as they were.");
        _listening.Start();
    }

    /// <summary>Gives <paramref name="button"/> the <paramref name="action"/>, keeps it, and says what changed.</summary>
    internal void Assign(TvAction action, PadButton button)
    {
        StopListening(null);
        var before = Map;
        var map = before.Assign(action, button);
        Use(map, map.ToSettings());

        // A button that was the only one for its old action swaps with the action's previous button.
        Notice = before.ActionFor(button) is { } old && old != action && before.ButtonsFor(action) is [var previous, ..] && map.ActionFor(previous) == old
            ? $"{GamepadMap.Name(button)} now does {GamepadMap.Title(action)}, and {GamepadMap.Name(previous)} does {GamepadMap.Title(old)}."
            : $"{GamepadMap.Name(button)} now does {GamepadMap.Title(action)}.";
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        StopListening(null);
        Use(GamepadMap.Standard, []);
        Notice = "The buttons are back to the standard layout.";
    }

    private void Use(GamepadMap map, Dictionary<string, string> saved)
    {
        if (_shell.Gamepads is { } pads) pads.Map = map;
        _shell.Settings.Tv.Gamepad = saved;
        _shell.SaveSettings();
        Refresh(map);
    }

    private void StopListening(string? notice)
    {
        _listening?.Stop();
        _listening = null;
        _shell.Gamepads?.CancelCapture();
        if (Waiting is { } row) row.IsWaiting = false;
        Waiting = null;
        if (notice is not null) Notice = notice;
    }

    private void Refresh(GamepadMap map)
    {
        foreach (var row in Rows) row.Buttons = string.Join("  ·  ", map.ButtonsFor(row.Action).Select(GamepadMap.Name));
    }

    private void OnPadsChanged(IReadOnlyList<string> pads) => Dispatcher.UIThread.Post(() => Status = Describe());

    private string Describe() => DescribePads(_shell.Gamepads);

    /// <summary>Which controllers are connected, or why none can be, in a sentence.</summary>
    internal static string DescribePads(GamepadInput? pads) => pads switch
    {
        null => "Controllers are not read in this run; the keyboard works.",
        { Unavailable: { } why } => $"Controllers need SDL3, and {why}. The keyboard works: arrows, Enter, Escape.",
        { Pads.Count: 0 } => "No controller is connected. Plug one in or pair it; it is found at once.",
        { Pads: var names } => "Connected: " + string.Join(", ", names),
    };
}

/// <summary>One action in the map editor, with the buttons that do it.</summary>
public sealed partial class ControllerRowViewModel(TvControllerPageViewModel page, TvAction action) : ObservableObject
{
    public TvAction Action { get; } = action;

    public string Title { get; } = GamepadMap.Title(action);

    public string Description { get; } = GamepadMap.Describe(action);

    [ObservableProperty]
    public partial string Buttons { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsWaiting { get; set; }

    [RelayCommand]
    private void Assign() => page.Listen(this);
}
