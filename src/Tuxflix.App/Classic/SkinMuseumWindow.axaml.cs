using Avalonia.Controls;

namespace Tuxflix.App.Classic;

/// <summary>The Winamp Skin Museum in a window of its own, beside the compact player.</summary>
public partial class SkinMuseumWindow : Window
{
    private readonly SkinMuseumViewModel? _model;
    private bool _started;

    public SkinMuseumWindow()
    {
        InitializeComponent();
    }

    public SkinMuseumWindow(SkinMuseumViewModel model)
        : this()
    {
        _model = model;
        DataContext = model;
        Opened += OnOpened;
    }

    // Opened comes again on every show: the museum is read once.
    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (_started || _model is null) return;
        _started = true;
        _ = _model.StartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _model?.Dispose();
    }
}
