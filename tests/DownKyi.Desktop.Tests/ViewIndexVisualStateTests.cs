using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Views;

namespace DownKyi.Desktop.Tests;

public sealed class ViewIndexVisualStateTests
{
    [AvaloniaFact]
    public Task SearchInputKeepsOneRoundedFocusSurfaceAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);
            var view = new ViewIndex();
            var window = new Window
            {
                Content = view,
                Width = 840,
                Height = 620
            };

            try
            {
                window.Show();
                var searchBorder = Assert.IsType<Border>(
                    view.FindControl<Border>("IndexSearchBorder"));
                var input = Assert.IsType<TextBox>(
                    view.FindControl<TextBox>("NameInputUrl"));

                Assert.Equal(new Thickness(2), searchBorder.BorderThickness);
                Assert.True(input.Focus());
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    window.UpdateLayout();

                    var innerBorder = Assert.Single(
                        input.GetVisualDescendants().OfType<Border>(),
                        border => border.Name == "PART_BorderElement");
                    Assert.True(searchBorder.IsKeyboardFocusWithin);
                    Assert.Equal(new CornerRadius(20), searchBorder.CornerRadius);
                    Assert.Equal(default, innerBorder.BorderThickness);
                    Assert.Null(innerBorder.BorderBrush);
                    Assert.Null(innerBorder.Background);
                    Assert.Equal(new Thickness(2), searchBorder.BorderThickness);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                window.Close();
                application.Styles.Remove(fluentTheme);
            }
        });
    }
}
