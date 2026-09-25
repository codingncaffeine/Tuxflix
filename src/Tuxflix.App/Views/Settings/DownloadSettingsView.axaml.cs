using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tuxflix.App.ViewModels;

namespace Tuxflix.App.Views.Settings;

/// <summary>The download options; its data context is a <see cref="DownloadSettingsViewModel"/> built from the shell.</summary>
public partial class DownloadSettingsView : UserControl
{
    public DownloadSettingsView()
    {
        InitializeComponent();
    }

    /// <summary>The desktop's own folder chooser (through the portal); new downloads go where it points once it proves writable.</summary>
    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DownloadSettingsViewModel options || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage) return;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where new downloads go", AllowMultiple = false });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) await options.SetFolderAsync(path);
    }
}
