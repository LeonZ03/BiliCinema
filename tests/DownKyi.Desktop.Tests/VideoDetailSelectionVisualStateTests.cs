using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Presentation;
using DownKyi.Views;

namespace DownKyi.Desktop.Tests;

public sealed class VideoDetailSelectionVisualStateTests
{
    [AvaloniaFact]
    public Task ChoiceCellsKeepCellCueUntilComboBoxOwnsFocus()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            var dataGridStyles = new StyleInclude(
                new Uri("avares://DownKyi.Desktop.Tests/"))
            {
                Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
            };
            application.Styles.Insert(0, fluentTheme);
            application.Styles.Insert(1, dataGridStyles);

            var quality = new VideoQuality
            {
                Quality = 80,
                QualityFormat = "1080P",
                VideoCodecList = ["AVC"],
                SelectedVideoCodec = "AVC"
            };
            var page = new VideoPage
            {
                Order = 1,
                Name = "Focus fixture",
                Duration = "01:00",
                AudioQualityFormatList = new ObservableCollection<string>(["192K"]),
                AudioQualityFormat = "192K",
                VideoQualityList = [quality],
                VideoQuality = quality
            };
            var view = new VideoDetailSelectionView();
            var dataGrid = Assert.IsType<DataGrid>(
                view.FindControl<DataGrid>("NameVideoPages"));
            dataGrid.AutoGenerateColumns = false;
            dataGrid.ItemsSource = new[] { page };
            var window = new Window
            {
                Content = view,
                Width = 1400,
                Height = 620
            };

            try
            {
                window.Show();
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    window.UpdateLayout();

                    var cells = dataGrid
                        .GetVisualDescendants()
                        .OfType<DataGridCell>()
                        .ToArray();
                    var choiceCells = cells
                        .Where(cell => cell.GetVisualDescendants().OfType<ComboBox>().Any())
                        .ToArray();
                    Assert.Equal(3, choiceCells.Length);

                    var textCell = Assert.Single(
                        cells,
                        cell => cell
                            .GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Any(text => text.Text == page.Name));
                    Assert.True(textCell.Focus());
                    window.UpdateLayout();
                    Assert.True(FindCellFocusVisual(textCell).IsVisible);

                    foreach (var choiceCell in choiceCells)
                    {
                        Assert.True(choiceCell.Focus());
                        window.UpdateLayout();
                        Assert.True(choiceCell.IsFocused);
                        Assert.True(FindCellFocusVisual(choiceCell).IsVisible);

                        var comboBox = Assert.Single(
                            choiceCell.GetVisualDescendants().OfType<ComboBox>());
                        Assert.True(comboBox.Focus());
                        window.UpdateLayout();
                        Assert.False(choiceCell.IsFocused);
                        Assert.True(choiceCell.IsKeyboardFocusWithin);
                        Assert.True(comboBox.IsKeyboardFocusWithin);
                        Assert.False(FindCellFocusVisual(choiceCell).IsVisible);
                    }
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                window.Close();
                application.Styles.Remove(dataGridStyles);
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static Grid FindCellFocusVisual(DataGridCell cell)
    {
        return Assert.Single(
            cell.GetVisualDescendants().OfType<Grid>(),
            grid => grid.Name == "FocusVisual");
    }
}
