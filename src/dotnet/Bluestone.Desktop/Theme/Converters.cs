using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Theme;

/// <summary>Maps a track's colour index to its palette brush.</summary>
public sealed class TrackBrushConverter : IValueConverter
{
    public static readonly TrackBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Palette.Track(value is int index ? index : 0));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"TRACK" or "TRACKS" for the inspector header.</summary>
public sealed class SelectionHeaderConverter : IValueConverter
{
    public static readonly SelectionHeaderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "TRACKS" : "TRACK";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DeleteLabelConverter : IValueConverter
{
    public static readonly DeleteLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "Delete Tracks" : "Delete Track";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A track colour mixed into the panel background, for icon tiles and subtle fills.</summary>
public sealed class TrackTintConverter : IValueConverter
{
    public static readonly TrackTintConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var amount = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 0.3;
        return new SolidColorBrush(Palette.Mix(Palette.Panel, Palette.Track(value is int index ? index : 0), amount));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when an enum value equals the parameter (by name), e.g. for tabs bound to an enum.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A left margin of the given width, for indenting a group's members in the track list.</summary>
public sealed class IndentConverter : IValueConverter
{
    public static readonly IndentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => new Avalonia.Thickness(value is double width ? width : 0, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A device's signal role as its accent colour (Signal.* tokens), for the panel stripe and knob arcs.</summary>
public sealed class DeviceRoleColorConverter : IValueConverter
{
    public static readonly DeviceRoleColorConverter Instance = new();

    public static Color ColorOf(DeviceRole role) => role switch
    {
        DeviceRole.Note => Palette.SignalNote,
        DeviceRole.Instrument => Palette.SignalInstrument,
        DeviceRole.Audio => Palette.SignalAudio,
        DeviceRole.Unavailable => Palette.Record,
        _ => Palette.SignalOther,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value is DeviceRole role ? ColorOf(role) : Palette.SignalOther;
        return targetType == typeof(Color) ? color : new SolidColorBrush(color);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A device panel's fixed width class as pixels, from the DevicePanel.*Width tokens.</summary>
public sealed class DevicePanelWidthConverter : IValueConverter
{
    public static readonly DevicePanelWidthConverter Instance = new();

    public static double WidthOf(DevicePanelSize size)
    {
        var key = $"DevicePanel.{size}Width";
        return ThemeResources.Find(key) is double width ? width : size switch
        {
            DevicePanelSize.Collapsed => 30,
            DevicePanelSize.Small => 152,
            DevicePanelSize.Medium => 216,
            _ => 280,
        };
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DevicePanelSize size ? WidthOf(size) : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A workspace view kind as its rail and tab icon.</summary>
public sealed class WorkspaceIconConverter : IValueConverter
{
    public static readonly WorkspaceIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            WorkspaceViewKind.Inspector => "Icon.Inspector",
            WorkspaceViewKind.Browser => "Icon.Browser",
            _ => "Icon.Devices",
        };
        return ThemeResources.Find(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
