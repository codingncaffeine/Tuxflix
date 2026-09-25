using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Tuxflix.App.Controls;

/// <summary>
/// Gives a control the keyboard focus, with its text selected, when a flag turns on:
/// <c>ui:FocusWhen.On="{Binding IsRenaming}"</c> puts the cursor in a name field as it appears.
/// </summary>
public static class FocusWhen
{
    public static readonly AttachedProperty<bool> OnProperty = AvaloniaProperty.RegisterAttached<Control, bool>("On", typeof(FocusWhen));

    static FocusWhen()
    {
        OnProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true) return;

            // After the layout that shows it: a control still hidden cannot take the focus.
            Dispatcher.UIThread.Post(() =>
            {
                control.Focus();
                if (control is TextBox box) box.SelectAll();
            }, DispatcherPriority.Loaded);
        });
    }

    public static bool GetOn(Control control) => control?.GetValue(OnProperty) ?? false;

    public static void SetOn(Control control, bool value) => control?.SetValue(OnProperty, value);
}
