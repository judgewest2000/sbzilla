using Sbzilla.ServiceBus;

namespace Sbzilla.Tests;

public class MessageFormatterTests
{
    [Fact]
    public void IndentsJson()
    {
        var (text, kind) = MessageFormatter.BodyText(BinaryData.FromString("{\"a\":1}"), null);

        Assert.Equal(BodyKind.Json, kind);
        Assert.Equal("{\n  \"a\": 1\n}", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void InvalidJsonFallsBackToText()
    {
        var (text, kind) = MessageFormatter.BodyText(BinaryData.FromString("{not json"), "application/json");

        Assert.Equal((BodyKind.Text, "{not json"), (kind, text));
    }

    [Fact]
    public void BinaryBodiesAreHexDumped()
    {
        var (text, kind) = MessageFormatter.BodyText(BinaryData.FromBytes([0x00, 0x41, 0xFF]), null);

        Assert.Equal(BodyKind.Binary, kind);
        Assert.StartsWith("00000000  00 41 FF", text);
        Assert.EndsWith(".A.", text.TrimEnd());
    }

    [Fact]
    public void EmptyBody() =>
        Assert.Equal(BodyKind.Empty, MessageFormatter.BodyText(BinaryData.Empty, null).Kind);
}
