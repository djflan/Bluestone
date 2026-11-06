using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;

namespace Bluestone.Tests.Ui;

public sealed class BrowserViewTests
{
    [Theory]
    [InlineData(1100, 720)]
    [InlineData(1440, 900)]
    public Task TheBrowser_ItemAndFooterDotsAlignWithTheSearchField(double width, double height) => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        app.Window.Width = width;
        app.Window.Height = height;
        app.ViewModel.Workspace.Browser.ShowCommand.Execute(null);
        app.Settle();

        var browser = app.Find<Panel>("BrowserDock");
        var searchField = AppUnderTest.Within<Border>(browser, border => border.Classes.Contains("searchField"));
        var itemList = AppUnderTest.Within<ListBox>(browser, list => list.Name == "ItemList");
        var firstItem = itemList.ContainerFromIndex(0) as ListBoxItem ?? throw new InvalidOperationException("The first Browser item is not realized.");
        var itemDot = AppUnderTest.Within<Ellipse>(firstItem, ellipse => ellipse.Classes.Contains("roleDot"));
        var footer = AppUnderTest.Within<Border>(browser, border => border.Classes.Contains("browserFooter"));
        var footerDot = AppUnderTest.Within<Ellipse>(footer, ellipse => ellipse.Classes.Contains("targetDot"));

        var fieldLeft = app.InWindow(searchField, new Point(0, 0)).X;
        var itemDotLeft = app.InWindow(itemDot, new Point(0, 0)).X;
        var footerDotLeft = app.InWindow(footerDot, new Point(0, 0)).X;
        Assert.InRange(Math.Abs(fieldLeft - itemDotLeft), 0, 1);
        Assert.InRange(Math.Abs(fieldLeft - footerDotLeft), 0, 1);
        Assert.Equal(itemDot.Bounds.Width, footerDot.Bounds.Width);
        Assert.Equal(itemDot.Bounds.Height, footerDot.Bounds.Height);
        Assert.True(File.Exists(app.SaveFrame($"browser-dot-alignment-{(int)width}")));
    });
}
