using Avalonia.Controls;
using Avalonia.Input;

namespace Bluestone.Desktop.Views;

/// <summary>The Settings surface. All behaviour is in <see cref="Presentation.SettingsViewModel"/>; Escape closes it.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && DataContext is Presentation.SettingsViewModel settings)
            {
                settings.Close();
                e.Handled = true;
            }
        };
    }
}
