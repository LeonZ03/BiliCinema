using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using DownKyi.Views.Toolbox;

namespace DownKyi.Desktop.Tests;

public sealed class ToolboxResponsiveLayoutTests
{
    [AvaloniaFact]
    public Task PrimaryActionsStayVisibleAtToolboxContentWidth()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertExtractMediaActionIsVisible();
                    AssertDelogoControlsDoNotOverlap();
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertExtractMediaActionIsVisible()
    {
        var view = new ViewExtractMedia();
        var window = CreateToolboxContentWindow(view);

        try
        {
            window.Show();
            window.UpdateLayout();

            var selectButton = Assert.IsType<Button>(
                view.FindControl<Button>("NameSelectVideoButton"));
            var buttonOrigin = Assert.NotNull(selectButton.TranslatePoint(default, window));

            Assert.True(buttonOrigin.X >= 0);
            Assert.True(buttonOrigin.X + selectButton.Bounds.Width <= window.ClientSize.Width);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertDelogoControlsDoNotOverlap()
    {
        var view = new ViewDelogo();
        var window = CreateToolboxContentWindow(view);

        try
        {
            window.Show();
            window.UpdateLayout();

            var yInput = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameLogoY"));
            var delogoButton = Assert.IsType<Button>(view.FindControl<Button>("NameDelogoButton"));
            var yOrigin = Assert.NotNull(yInput.TranslatePoint(default, window));
            var buttonOrigin = Assert.NotNull(delogoButton.TranslatePoint(default, window));

            Assert.True(yOrigin.X + yInput.Bounds.Width <= buttonOrigin.X);
            Assert.True(buttonOrigin.X + delogoButton.Bounds.Width <= window.ClientSize.Width);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateToolboxContentWindow(UserControl view) => new()
    {
        Content = view,
        Width = 800,
        Height = 630,
        CanResize = false
    };
}
