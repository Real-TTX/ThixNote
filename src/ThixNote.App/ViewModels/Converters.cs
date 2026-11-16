using Avalonia;
using Avalonia.Data.Converters;

namespace ThixNote.App.ViewModels;

public static class Converters
{
    public static readonly FuncValueConverter<bool, double> TitleFontSize = new(isCompact => isCompact ? 24 : 34);

    public static readonly FuncValueConverter<bool, Thickness> EditorMargin =
        new(isCompact => isCompact ? new Thickness(20, 14, 20, 24) : new Thickness(56, 26, 56, 40));
}
