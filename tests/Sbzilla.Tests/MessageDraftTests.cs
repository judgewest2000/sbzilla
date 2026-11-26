using Sbzilla.ServiceBus;

namespace Sbzilla.Tests;

public class MessageDraftTests
{
    [Fact]
    public void ParsesTypedApplicationProperties()
    {
        var props = MessageDrafts.ParseProperties("# comment\r\ncount = 42\nratio=1.5\nflag: true\nname = \"123\"\n\nplain = hello world");

        Assert.Equal(
            [("count", (object)42L), ("ratio", 1.5), ("flag", true), ("name", "123"), ("plain", "hello world")],
            props.Select(p => (p.Key, p.Value)));
    }

    [Theory]
    [InlineData("no separator")]
    [InlineData("a = 1\na = 2")]
    public void RejectsBadPropertyLines(string text) =>
        Assert.Throws<FormatException>(() => MessageDrafts.ParseProperties(text));

    [Fact]
    public void FormattedPropertiesReadBackAsTheSameValues()
    {
        KeyValuePair<string, object>[] original = [new("id", "42"), new("n", 42L), new("on", false), new("text", "hi")];

        var roundTripped = MessageDrafts.ParseProperties(MessageDrafts.FormatProperties(original));

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void ExpandsCopyNumberPlaceholders()
    {
        var draft = new MessageDraft { Body = "{\"n\": {n}}", MessageId = "order-{n}", ApplicationProperties = "copy = item {n}" };

        var message = draft.ToMessage(3);

        Assert.Equal("{\"n\": 3}", message.Body.ToString());
        Assert.Equal("order-3", message.MessageId);
        Assert.Equal("item 3", message.ApplicationProperties["copy"]);
    }

    [Fact]
    public void PartitionKeyIsIgnoredWhenASessionIsSet()
    {
        var draft = new MessageDraft { SessionId = "s1", PartitionKey = "other" };

        var message = draft.ToMessage(1);

        Assert.Equal("s1", message.SessionId);
        Assert.Null(message.PartitionKey);
    }
}
