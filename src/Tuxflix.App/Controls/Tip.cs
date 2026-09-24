using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace Tuxflix.App.Controls;

/// <summary>A tooltip's content: what the control does, and the keys that do it.</summary>
public sealed record TipContent(string Text, string? Shortcut)
{
    public bool HasKeys => !string.IsNullOrWhiteSpace(Shortcut);

    /// <summary>"Ctrl+K" as ["Ctrl", "K"], one keycap each.</summary>
    public IReadOnlyList<string> Keys =>
        Shortcut?.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public override string ToString() => HasKeys ? $"{Text} ({Shortcut})" : Text;
}

/// <summary>
/// <c>ui:Tip.Text="Back"</c> and <c>ui:Tip.Keys="Alt+Left"</c>: one place that gives a control its
/// tooltip, its shortcut hint and its accessible name together, so none of the three can be
/// forgotten while the others are there.
/// </summary>
public static class Tip
{
    /// <summary>How long the pointer rests before a tip appears, in milliseconds.</summary>
    public const int Delay = 450;

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(Tip));

    public static readonly AttachedProperty<string?> KeysProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Keys", typeof(Tip));

    static Tip()
    {
        TextProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
        KeysProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
    }

    public static string? GetText(Control control) => control.GetValue(TextProperty);

    public static void SetText(Control control, string? value) => control.SetValue(TextProperty, value);

    public static string? GetKeys(Control control) => control.GetValue(KeysProperty);

    public static void SetKeys(Control control, string? value) => control.SetValue(KeysProperty, value);

    private static void Apply(Control control)
    {
        var text = GetText(control);
        if (string.IsNullOrWhiteSpace(text))
        {
            ToolTip.SetTip(control, null);
            return;
        }

        ToolTip.SetTip(control, new TipContent(text, GetKeys(control)));
        ToolTip.SetShowDelay(control, Delay);
        AutomationProperties.SetName(control, text);
    }
}
