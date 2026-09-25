using System.Globalization;
using Avalonia.Data.Converters;

namespace Tuxflix.App.Views;

/// <summary>Turns an icon upside down while a flag is set: an ascending sort arrow pointing down for descending.</summary>
public static class Flip
{
    public static readonly IValueConverter Converter = new FuncValueConverter<bool, double>(on => on ? -1 : 1);
}
