using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Views.Settings;

namespace DownKyi.Desktop.Tests;

public sealed class SettingsComboBoxLayoutTests
{
    private static readonly string[] AriaFileAllocations = ["PREALLOC"];
    private static readonly VideoParseType[] VideoParseTypes =
    [
        new()
        {
            Name = "WebPage(解析慢、不易风控)",
            Id = 1
        }
    ];

    [AvaloniaFact]
    public Task DescriptiveChoicesCanGrowBeyondTheirBaselineWidth()
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
                    AssertChoiceGrows(
                        CreateVisibleAriaSettings(),
                        "NameAriaFileAllocations",
                        AriaFileAllocations,
                        100);
                    AssertChoiceGrows(
                        new ViewVideo(),
                        "NameVideoParseTypeList",
                        VideoParseTypes,
                        200);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static AriaDownloaderSettingsView CreateVisibleAriaSettings()
    {
        var view = new AriaDownloaderSettingsView();
        var content = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("NameAria"));
        content.IsVisible = true;
        return view;
    }

    private static void AssertChoiceGrows(
        UserControl view,
        string controlName,
        object itemsSource,
        double baselineWidth)
    {
        var comboBox = Assert.IsType<ComboBox>(view.FindControl<ComboBox>(controlName));
        comboBox.ItemsSource = (System.Collections.IEnumerable)itemsSource;
        comboBox.SelectedIndex = 0;
        var window = new Window
        {
            Content = view,
            Width = 840,
            Height = 620
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.True(double.IsNaN(comboBox.Width));
            Assert.Equal(baselineWidth, comboBox.MinWidth);
            Assert.True(comboBox.Bounds.Width > baselineWidth);
        }
        finally
        {
            window.Close();
        }
    }
}
