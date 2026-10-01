using Avalonia;
using DownKyi.Views;

namespace DownKyi.Desktop.Tests;

public sealed class MainWindowPlacementTests
{
    [Fact]
    public void OversizedRestoredWindowIsMovedAndShrunkIntoWorkingArea()
    {
        var placement = MainWindow.ConstrainRestoredPlacement(
            new PixelPoint(162, 225),
            new Size(960, 1000),
            new Size(974, 1039),
            new PixelRect(0, 0, 1920, 1032),
            1);

        Assert.Equal(new PixelPoint(162, 0), placement.Position);
        Assert.Equal(new Size(960, 993), placement.ClientSize);
        Assert.Equal(1032, placement.Position.Y + placement.ClientSize.Height + 39);
    }

    [Fact]
    public void VisibleRestoredWindowKeepsItsSizeAndPosition()
    {
        var placement = MainWindow.ConstrainRestoredPlacement(
            new PixelPoint(162, 80),
            new Size(960, 750),
            new Size(974, 789),
            new PixelRect(0, 0, 1920, 1032),
            1);

        Assert.Equal(new PixelPoint(162, 80), placement.Position);
        Assert.Equal(new Size(960, 750), placement.ClientSize);
    }
}
