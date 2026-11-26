using Sbzilla.ServiceBus;

namespace Sbzilla.Tests;

public class EntityPropertyFieldsTests
{
    [Theory]
    [InlineData("30s", 0, 0, 0, 30)]
    [InlineData("1h 30m", 0, 1, 30, 0)]
    [InlineData("14d", 14, 0, 0, 0)]
    [InlineData("1.5h", 0, 1, 30, 0)]
    [InlineData("2.03:04:05", 2, 3, 4, 5)]
    public void ParsesDurations(string text, int days, int hours, int minutes, int seconds) =>
        Assert.Equal(new TimeSpan(days, hours, minutes, seconds), EntityPropertyFields.ParseTimeSpan(text));

    [Theory]
    [InlineData("never")]
    [InlineData("MAX")]
    public void NeverIsTheMaximum(string text) =>
        Assert.Equal(TimeSpan.MaxValue, EntityPropertyFields.ParseTimeSpan(text));

    [Theory]
    [InlineData("")]
    [InlineData("5 minutes")]
    [InlineData("10x")]
    public void RejectsBadDurations(string text) =>
        Assert.Throws<FormatException>(() => EntityPropertyFields.ParseTimeSpan(text));

    [Fact]
    public void FormattedDurationsParseBack()
    {
        TimeSpan[] values = [TimeSpan.Zero, TimeSpan.FromSeconds(45), new(1, 2, 30, 0), TimeSpan.FromMilliseconds(1500), TimeSpan.MaxValue];

        var formatted = values.Select(EntityPropertyFields.FormatTimeSpan).ToList();
        var parsed = formatted.Select(EntityPropertyFields.ParseTimeSpan);

        Assert.Equal(["0s", "45s", "1d 2h 30m", "1s 500ms", "never"], formatted);
        Assert.Equal(values, parsed);
    }
}
