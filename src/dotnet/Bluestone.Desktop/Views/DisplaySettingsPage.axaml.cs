using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Desktop.Controls;

namespace Bluestone.Desktop.Views;

/// <summary>The Display settings page. All behaviour is in <see cref="Presentation.DisplayViewModel"/>.</summary>
public partial class DisplaySettingsPage : UserControl
{
    public DisplaySettingsPage()
    {
        InitializeComponent();

        // The gradient's actions are text keys, which are toggles; as actions they spring back after each press.
        AddHandler(ToggleButton.IsCheckedChangedEvent, (_, e) =>
        {
            if (e.Source is TextKey { IsChecked: true } key && key.Classes.Contains("action"))
            {
                key.IsChecked = false;
            }
        });
    }

    /// <summary>"#RRGGBB" as a brush; anything else draws nothing.</summary>
    public static IValueConverter HexToBrush { get; } = new FuncValueConverter<string?, IBrush?>(hex =>
        hex is not null && Color.TryParse(hex, out var colour) ? new ImmutableSolidColorBrush(colour) : null);
}
