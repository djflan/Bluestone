using Bluestone.Presentation;

namespace Bluestone.Tests.Ui;

/// <summary>A theme file for a test (ADR 0053), in the test's own folder, never the musician's settings folder.</summary>
internal static class TestThemes
{
    /// <summary>The shipped theme, as the headless app applied it at startup.</summary>
    public static ThemeDefinition Shipped =>
        Desktop.App.AppliedTheme ?? throw new InvalidOperationException("The headless app applies the shipped theme at startup.");

    /// <summary>
    /// A store for the shipped theme in <paramref name="directory"/>/themes, used by <paramref name="display"/> as the app uses
    /// the real one: its gradients and MIDI color come from the file, and every edit to them is saved there.
    /// </summary>
    public static ThemeStore Use(DisplayViewModel display, string directory)
    {
        var store = new ThemeStore(Path.Combine(directory, "themes"), Shipped);
        display.UseTheme(store, store.Load().Theme);
        return store;
    }
}
