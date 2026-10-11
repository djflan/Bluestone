using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Bluestone.Desktop.Views;

/// <summary>The plugins view (Window ▸ Plugins…). All behaviour is in <see cref="Presentation.PluginsViewModel"/>.</summary>
public partial class PluginsWindow : Window
{
    public PluginsWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
