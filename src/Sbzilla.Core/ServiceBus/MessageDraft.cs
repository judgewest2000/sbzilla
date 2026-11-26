using System.Globalization;
using Azure.Messaging.ServiceBus;

namespace Sbzilla.ServiceBus;

/// <summary>What the Send message dialog edits. <see cref="ToMessage"/> builds the n-th copy.</summary>
public sealed class MessageDraft
{
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "application/json";

    /// <summary>"{n}" becomes the copy number and "{guid}" a new GUID, so several copies get distinct ids.</summary>
    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string PartitionKey { get; set; } = "";
    public string To { get; set; } = "";
    public string ReplyTo { get; set; } = "";
    public string ReplyToSessionId { get; set; } = "";
    public TimeSpan? TimeToLive { get; set; }
    public DateTimeOffset? ScheduledEnqueueTime { get; set; }

    /// <summary>One "name = value" per line; see <see cref="MessageDrafts.ParseProperties"/>.</summary>
    public string ApplicationProperties { get; set; } = "";

    public ServiceBusMessage ToMessage(int copyNumber)
    {
        string Expand(string s) => s
            .Replace("{n}", copyNumber.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{guid}", Guid.NewGuid().ToString(), StringComparison.OrdinalIgnoreCase);

        var m = new ServiceBusMessage(BinaryData.FromString(Expand(Body)));
        if (ContentType.Trim().Length > 0) m.ContentType = ContentType.Trim();
        if (MessageId.Trim().Length > 0) m.MessageId = Expand(MessageId.Trim());
        if (Subject.Trim().Length > 0) m.Subject = Expand(Subject.Trim());
        if (CorrelationId.Trim().Length > 0) m.CorrelationId = Expand(CorrelationId.Trim());
        if (SessionId.Trim().Length > 0) m.SessionId = Expand(SessionId.Trim());
        // A partition key must match the session id when both are set, so it's only applied on its own.
        if (PartitionKey.Trim().Length > 0 && SessionId.Trim().Length == 0) m.PartitionKey = Expand(PartitionKey.Trim());
        if (To.Trim().Length > 0) m.To = To.Trim();
        if (ReplyTo.Trim().Length > 0) m.ReplyTo = ReplyTo.Trim();
        if (ReplyToSessionId.Trim().Length > 0) m.ReplyToSessionId = ReplyToSessionId.Trim();
        if (TimeToLive is { } ttl) m.TimeToLive = ttl;
        foreach (var (key, value) in MessageDrafts.ParseProperties(ApplicationProperties))
            m.ApplicationProperties[key] = value is string s ? Expand(s) : value;
        return m;
    }
}

public static class MessageDrafts
{
    /// <summary>Broker-set application properties that shouldn't be copied onto a resubmitted message.</summary>
    private static readonly string[] DeadLetterProperties = ["DeadLetterReason", "DeadLetterErrorDescription"];

    /// <summary>A draft pre-filled from a received message (for "Edit and resend").</summary>
    public static MessageDraft FromReceived(ServiceBusReceivedMessage m) => new()
    {
        Body = MessageFormatter.BodyText(m.Body, m.ContentType).Text,
        ContentType = m.ContentType ?? "",
        MessageId = m.MessageId ?? "",
        Subject = m.Subject ?? "",
        CorrelationId = m.CorrelationId ?? "",
        SessionId = m.SessionId ?? "",
        PartitionKey = m.SessionId == null ? m.PartitionKey ?? "" : "",
        To = m.To ?? "",
        ReplyTo = m.ReplyTo ?? "",
        ReplyToSessionId = m.ReplyToSessionId ?? "",
        ApplicationProperties = FormatProperties(m.ApplicationProperties.Where(p => !DeadLetterProperties.Contains(p.Key))),
    };

    /// <summary>An exact copy of the message (same body, id and properties) minus the dead-letter bookkeeping.</summary>
    public static ServiceBusMessage CloneForResubmit(ServiceBusReceivedMessage m)
    {
        var copy = new ServiceBusMessage(m);
        foreach (var key in DeadLetterProperties) copy.ApplicationProperties.Remove(key);
        return copy;
    }

    /// <summary>
    /// Parses "name = value" lines. Values are typed: integers become long, numbers double, true/false bool,
    /// and anything in double quotes (or anything else) a string. Blank lines and lines starting with # are ignored.
    /// </summary>
    public static List<KeyValuePair<string, object>> ParseProperties(string? text)
    {
        var result = new List<KeyValuePair<string, object>>();
        var lines = (text ?? "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOfAny(['=', ':']);
            if (eq <= 0) throw new FormatException($"Line {i + 1}: expected \"name = value\".");
            var name = line[..eq].Trim();
            if (result.Any(p => p.Key == name)) throw new FormatException($"Line {i + 1}: \"{name}\" is set twice.");
            result.Add(new(name, ParseValue(line[(eq + 1)..].Trim())));
        }
        return result;
    }

    internal static object ParseValue(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') return raw[1..^1];
        if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && raw.Any(char.IsDigit)) return d;
        if (bool.TryParse(raw, out var b)) return b;
        return raw;
    }

    /// <summary>The inverse of <see cref="ParseProperties"/>: strings that would read back as another type are quoted.</summary>
    public static string FormatProperties(IEnumerable<KeyValuePair<string, object>> properties) =>
        string.Join("\r\n", properties.Select(p => $"{p.Key} = {FormatValue(p.Value)}"));

    private static string FormatValue(object? value) => value switch
    {
        null => "\"\"",
        string s => ParseValue(s) is string && s == s.Trim() ? s : $"\"{s}\"",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
