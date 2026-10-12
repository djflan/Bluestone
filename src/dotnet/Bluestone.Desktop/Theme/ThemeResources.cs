using Avalonia.Styling;

namespace Bluestone.Desktop.Theme;

/// <summary>Looks up design tokens and icons from code, for converters and windows built in code.</summary>
internal static class ThemeResources
{
    public static object? Find(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant ?? ThemeVariant.Dark, out var value) ? value : null;
}
