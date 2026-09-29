using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Presentation;
using DownKyi.Views;

namespace DownKyi.Desktop.Tests;

public sealed class SectionNavigationVisualStateTests
{
    [AvaloniaFact]
    public Task SectionMenusShareOneRoundedSelectedStateAcrossThemes()
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
                    var listTheme = GetTheme(application, "LeftTabHeaderStyle", theme);
                    var itemTheme = GetTheme(application, "LeftTabHeaderItemStyle", theme);

                    AssertTargetViewUsesSharedThemes(new ViewToolbox(), listTheme, itemTheme);
                    AssertTargetViewUsesSharedThemes(new ViewDownloadManager(), listTheme, itemTheme);
                    AssertTargetViewUsesSharedThemes(new ViewSettings(), listTheme, itemTheme);
                    AssertSelectedState(application, theme, listTheme, itemTheme);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertTargetViewUsesSharedThemes(
        UserControl view,
        ControlTheme listTheme,
        ControlTheme itemTheme)
    {
        var navigation = Assert.IsType<ListBox>(view.FindControl<ListBox>("NameLeftTabHeaders"));
        Assert.Same(listTheme, navigation.Theme);
        Assert.Same(itemTheme, navigation.ItemContainerTheme);
    }

    private static void AssertSelectedState(
        Avalonia.Application application,
        ThemeVariant theme,
        ControlTheme listTheme,
        ControlTheme itemTheme)
    {
        var navigation = new ListBox
        {
            Width = 200,
            Height = 160,
            Theme = listTheme,
            ItemContainerTheme = itemTheme,
            ItemsSource = new[]
            {
                new TabHeader { Id = 0, Title = "First" },
                new TabHeader { Id = 1, Title = "Second" }
            },
            SelectedIndex = 1
        };
        var window = new Window
        {
            Content = navigation,
            Width = 300,
            Height = 240
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var items = navigation
                .GetVisualDescendants()
                .OfType<ListBoxItem>()
                .ToArray();
            Assert.Equal(2, items.Length);
            var unselected = Assert.Single(items, item => !item.IsSelected);
            var selected = Assert.Single(items, item => item.IsSelected);
            var unselectedBackground = FindTemplateBorder(unselected, "SelectionBackground");
            var selectedBackground = FindTemplateBorder(selected, "SelectionBackground");
            var unselectedIndicator = FindTemplateBorder(unselected, "SelectionIndicator");
            var selectedIndicator = FindTemplateBorder(selected, "SelectionIndicator");

            Assert.Equal(new Thickness(8, 2), selected.Margin);
            Assert.Equal(44, selected.MinHeight);
            Assert.Equal(new CornerRadius(8), selectedBackground.CornerRadius);
            Assert.Equal(Colors.Transparent, SolidColor(unselectedBackground.Background));
            Assert.Equal(
                ResourceColor(application, "BrushPrimaryTranslucent3", theme),
                SolidColor(selectedBackground.Background));
            Assert.Equal(0, unselectedIndicator.Opacity);
            Assert.Equal(1, selectedIndicator.Opacity);
            Assert.Equal(
                ResourceColor(application, "BrushPrimary", theme),
                SolidColor(selectedIndicator.Background));

            var clickPoint = unselected.TranslatePoint(
                new Point(unselected.Bounds.Width - 4, unselected.Bounds.Height / 2),
                window);
            Assert.NotNull(clickPoint);
            window.MouseMove(clickPoint.Value);
            window.MouseDown(clickPoint.Value, MouseButton.Left);
            window.MouseUp(clickPoint.Value, MouseButton.Left);
            window.UpdateLayout();
            Assert.Equal(0, navigation.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    private static ControlTheme GetTheme(
        Avalonia.Application application,
        string key,
        ThemeVariant theme)
    {
        Assert.True(application.TryGetResource(key, theme, out var value));
        return Assert.IsType<ControlTheme>(value);
    }

    private static Color ResourceColor(
        Avalonia.Application application,
        string key,
        ThemeVariant theme)
    {
        Assert.True(application.TryGetResource(key, theme, out var value));
        return SolidColor(value);
    }

    private static Color SolidColor(object? value)
    {
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Border FindTemplateBorder(Control item, string name)
    {
        return Assert.Single(
            item.GetVisualDescendants().OfType<Border>(),
            border => border.Name == name);
    }
}
