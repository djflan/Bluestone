using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Bluestone.Desktop.Views;

/// <summary>The Display settings page. All behaviour is in <see cref="Presentation.DisplayViewModel"/>.</summary>
public partial class DisplaySettingsPage : UserControl
{
    public DisplaySettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>"#RRGGBB" as a brush; anything else draws nothing.</summary>
    public static IValueConverter HexToBrush { get; } = new FuncValueConverter<string?, IBrush?>(hex =>
        hex is not null && Color.TryParse(hex, out var colour) ? new ImmutableSolidColorBrush(colour) : null);
}
