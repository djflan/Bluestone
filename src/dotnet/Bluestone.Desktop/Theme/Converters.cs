using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Bluestone.Audio;
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

/// <summary>An audio setting's choice in words: a sample rate ("Automatic", "48,000 Hz") or a block size ("256 samples").</summary>
public sealed class AudioChoiceConverter(Func<int, string> label) : IValueConverter
{
    public static readonly AudioChoiceConverter Rate = new(AudioViewModel.RateLabel);

    public static readonly AudioChoiceConverter Block = new(AudioViewModel.BlockLabel);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int choice ? label(choice) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A source's colour: its track's (an index of 0 or more), or the neutral signal colour for a rack or nothing (-1).</summary>
public sealed class SourceAccentConverter : IValueConverter
{
    public static readonly SourceAccentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(value is int index and >= 0 ? Palette.Track(index) : Palette.SignalOther);

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

/// <summary>Friendly labels for the mixer meter's global display mode.</summary>
public sealed class MixerMeterModeLabelConverter : IValueConverter
{
    public static readonly MixerMeterModeLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MixerMeterMode mode
            ? mode switch
            {
                MixerMeterMode.PeakRms => "Peak + RMS",
                MixerMeterMode.Vu => "VU",
                MixerMeterMode.K20 => "K-20",
                MixerMeterMode.K14 => "K-14",
                MixerMeterMode.K12 => "K-12",
                MixerMeterMode.LufsS => "LUFS-S",
                _ => "Peak + RMS",
            }
            : "Peak + RMS";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
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
            WorkspaceViewKind.Mixer => "Icon.Mixer",
            _ => "Icon.Devices",
        };
        return ThemeResources.Find(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The icon for a Settings page.</summary>
public sealed class SettingsIconConverter : IValueConverter
{
    public static readonly SettingsIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemeResources.Find(value is SettingsPage.Plugins ? "Icon.Plug" : "Icon.Speaker");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A track's colour index as its palette colour (not a brush), for drawn controls with a colour property.</summary>
public sealed class TrackColorConverter : IValueConverter
{
    public static readonly TrackColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Palette.Track(value is int index ? index : 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The mixer meter's unit for the console footer, so a gain setting (dB) is never read as a level.</summary>
public sealed class MixerMeterUnitConverter : IValueConverter
{
    public static readonly MixerMeterUnitConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            MixerMeterMode.Vu => "meter VU",
            MixerMeterMode.K20 => "meter K-20 dB",
            MixerMeterMode.K14 => "meter K-14 dB",
            MixerMeterMode.K12 => "meter K-12 dB",
            MixerMeterMode.LufsS => "meter LUFS",
            _ => "meter dBFS",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when a number is greater than the parameter, e.g. a name long enough to need the smaller, wrapping style.</summary>
public sealed class GreaterThanConverter : IValueConverter
{
    public static readonly GreaterThanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int number && parameter is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && number > limit;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The one-line cue a folded mixer strip shows instead of its insert stack and sends: "2 devices · 1 send ›". Values are
/// the insert count and, for a track strip, the send count and whether it has an audio strip (a MIDI-only strip has no
/// sends); Master passes only its insert count.
/// </summary>
public sealed class MixerProcessingCueConverter : IMultiValueConverter
{
    public static readonly MixerProcessingCueConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var devices = values.Count > 0 && values[0] is int d ? d : 0;
        var text = Count(devices, "device", "No");
        if (values.Count > 1 && !(values.Count > 2 && values[2] is false))
        {
            text += " · " + Count(values[1] is int s ? s : 0, "send", "no");
        }

        return text + " ›";

        static string Count(int count, string noun, string none) => count switch
        {
            0 => $"{none} {noun}s",
            1 => $"1 {noun}",
            _ => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}s"),
        };
    }
}

/// <summary>
/// A formatted level without its trailing unit ("-21.0 dB" to "-21.0"), for a cell too narrow for the unit; the unit
/// stays in the control's tooltip and spoken value.
/// </summary>
public sealed class MixerUnitlessConverter : IValueConverter
{
    public static readonly MixerUnitlessConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text.LastIndexOf(' ') is var space and > 0 ? text[..space] : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True while a strip's inserts leave a free row in its insert well, for the well's "+" add row.</summary>
public sealed class InsertWellHasRoomConverter : IValueConverter
{
    public static readonly InsertWellHasRoomConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count < MixerConsoleViewModel.VisibleInsertRows;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
