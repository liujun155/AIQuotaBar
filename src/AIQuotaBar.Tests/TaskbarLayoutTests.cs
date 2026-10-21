using AIQuotaBar.App.Interop;

namespace AIQuotaBar.Tests;

public class TaskbarLayoutTests
{
    [Theory]
    [InlineData(1.0, 108, 34, 6)]
    [InlineData(1.25, 135, 42, 8)]
    [InlineData(1.5, 162, 51, 9)]
    [InlineData(2.0, 216, 68, 12)]
    public void AnchorsToActualTrayAndScalesSize(double scale, int width, int height, int gap)
    {
        var bar = new PixelRect(0, 2000, 3840, 2096);
        var tray = new PixelRect(3300, 2000, 3840, 2096);
        var result = TaskbarLayout.Place(bar, tray, 108, 34, scale);
        Assert.Equal(tray.Left - gap, result.Right);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(bar.Top + (bar.Height - height) / 2, result.Top);
    }

    [Fact]
    public void PreservesNegativeMonitorCoordinates()
    {
        var result = TaskbarLayout.Place(new(-1920, -48, 0, 0), new PixelRect(-320, -48, 0, 0), 108, 34, 1);
        Assert.Equal(new PixelRect(-434, -41, -326, -7), result);
    }

    [Fact]
    public void VerticalTaskbarPlacesAboveTrayAndClipsToAvailableWidth()
    {
        var result = TaskbarLayout.Place(new(1920, 0, 1980, 1080), new PixelRect(1920, 900, 1980, 1080), 108, 34, 1);
        Assert.Equal(new PixelRect(1920, 860, 1980, 894), result);
    }

    [Fact]
    public void MissingTrayStaysInsideTaskbar()
    {
        var result = TaskbarLayout.Place(new(2000, 100, 2200, 140), null, 108, 34, 2);
        Assert.Equal(new PixelRect(2000, 100, 2200, 140), result);
    }

    [Fact]
    public void PopupOnNegativeMonitorStaysWithinWorkArea()
    {
        var result = TaskbarLayout.PlacePopup(new(-120, 1039, -12, 1073), new(-1920, 0, 0, 1032), 330, 370, 8);
        Assert.Equal(new PixelRect(-330, 661, 0, 1031), result);
    }

    [Fact]
    public void PopupForTopTaskbarOpensBelowIt()
    {
        var result = TaskbarLayout.PlacePopup(new(1500, 7, 1608, 41), new(0, 48, 1920, 1080), 330, 370, 8);
        Assert.Equal(49, result.Top);
        Assert.Equal(370, result.Height);
    }
}
