using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

/// <summary>The Browser's gestures: drag an item out to a Device View, or Return / double-click to insert into the active one.</summary>
public partial class BrowserView : UserControl
{
    private const double DragThreshold = 4;
    private PointerPressedEventArgs? _press;
    private BrowserItem? _pressed;
    private Point _pressAt;

    public BrowserView()
    {
        InitializeComponent();
        ItemList.AddHandler(PointerPressedEvent, OnItemPressed, RoutingStrategies.Tunnel);
        ItemList.AddHandler(PointerMovedEvent, OnItemMoved, RoutingStrategies.Tunnel);
        ItemList.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel);
        ItemList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: BrowserItem item } && ViewModel is { } vm)
            {
                vm.InsertIntoActive(item);
                e.Handled = true;
            }
        };
        ItemList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ViewModel is { SelectedItem: { } item } vm)
            {
                vm.InsertIntoActive(item);
                e.Handled = true;
            }
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (ViewModel is not { } vm)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Enter:
                    if ((vm.SelectedItem ?? vm.Items.FirstOrDefault()) is { } item)
                    {
                        vm.SelectedItem = item;
                        vm.InsertIntoActive(item);
                    }

                    e.Handled = true;
                    break;
                case Key.Down when vm.Items.Count > 0:
                    vm.SelectedItem ??= vm.Items[0];
                    ItemList.ContainerFromItem(vm.SelectedItem)?.Focus(NavigationMethod.Directional);
                    e.Handled = true;
                    break;
                case Key.Escape when vm.SearchText.Length > 0:
                    vm.SearchText = string.Empty;
                    e.Handled = true;
                    break;
            }
        };
    }

    private BrowserViewModel? ViewModel => DataContext as BrowserViewModel;

    /// <summary>Puts the keyboard in the search field.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is Control { DataContext: BrowserItem { Definition: not null } item })
        {
            _press = e;
            _pressed = item;
            _pressAt = e.GetPosition(this);
        }
    }

    private async void OnItemMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressed?.Definition is not { } definition)
        {
            return;
        }

        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _pressAt.X) < DragThreshold && Math.Abs(at.Y - _pressAt.Y) < DragThreshold)
        {
            return;
        }

        _press = null;
        await DragDrop.DoDragDropAsync(press, ChainDrag.Insert(definition).ToTransfer(), DragDropEffects.Copy);
    }
}
