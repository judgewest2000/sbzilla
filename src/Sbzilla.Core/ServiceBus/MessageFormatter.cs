using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Azure.Messaging.ServiceBus;

namespace Sbzilla.ServiceBus;

public enum BodyKind { Empty, Text, Json, Xml, Binary }

/// <summary>Turns message bodies and properties into readable text.</summary>
public static class MessageFormatter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The body as text: JSON and XML are indented, and anything that isn't text is shown as a hex dump.</summary>
    public static (string Text, BodyKind Kind) BodyText(BinaryData body, string? contentType)
    {
        var bytes = body.ToMemory().Span;
        if (bytes.Length == 0) return ("", BodyKind.Empty);
        if (!TryDecodeText(bytes, out var text)) return (HexDump(bytes), BodyKind.Binary);

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || (contentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try { return (JsonNode.Parse(text)?.ToJsonString(Indented) ?? text, BodyKind.Json); }
            catch (JsonException) { }
        }
        if (trimmed.StartsWith('<'))
        {
            try { return (XDocument.Parse(text).ToString(), BodyKind.Xml); }
            catch (System.Xml.XmlException) { }
        }
        return (text, BodyKind.Text);
    }

    /// <summary>Strict UTF-8 (with or without a BOM); text with control characters other than whitespace counts as binary.</summary>
    internal static bool TryDecodeText(ReadOnlySpan<byte> bytes, out string text)
    {
        text = "";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return false; }
        foreach (var ch in text)
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t') return false;
        return true;
    }

    internal static string HexDump(ReadOnlySpan<byte> bytes, int maxBytes = 64 * 1024)
    {
        var sb = new StringBuilder();
        var shown = Math.Min(bytes.Length, maxBytes);
        for (var offset = 0; offset < shown; offset += 16)
        {
            var line = bytes.Slice(offset, Math.Min(16, shown - offset));
            sb.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++) sb.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
            sb.Append(' ');
            foreach (var b in line) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            sb.AppendLine();
        }
        if (bytes.Length > shown) sb.AppendLine($"… {bytes.Length - shown:N0} more bytes");
        return sb.ToString();
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):N2} GB",
    };

    public static string Time(DateTimeOffset value) =>
        value == default || value == DateTimeOffset.MaxValue ? "" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>System properties (for the details pane), in the order Service Bus Explorer shows them.</summary>
    public static List<KeyValuePair<string, string>> SystemProperties(ServiceBusReceivedMessage m)
    {
        var list = new List<KeyValuePair<string, string>>();
        void Add(string name, object? value)
        {
            var text = value switch
            {
                null => "",
                DateTimeOffset d => Time(d),
                TimeSpan t => EntityPropertyFields.FormatTimeSpan(t),
                IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
                _ => value.ToString() ?? "",
            };
            if (text.Length > 0) list.Add(new(name, text));
        }
        Add("MessageId", m.MessageId);
        Add("SequenceNumber", m.SequenceNumber);
        Add("EnqueuedTime", m.EnqueuedTime);
        Add("ScheduledEnqueueTime", m.ScheduledEnqueueTime);
        Add("ExpiresAt", m.ExpiresAt);
        Add("TimeToLive", m.TimeToLive);
        Add("DeliveryCount", m.DeliveryCount);
        Add("State", m.State);
        Add("Subject", m.Subject);
        Add("ContentType", m.ContentType);
        Add("CorrelationId", m.CorrelationId);
        Add("SessionId", m.SessionId);
        Add("PartitionKey", m.PartitionKey);
        Add("TransactionPartitionKey", m.TransactionPartitionKey);
        Add("To", m.To);
        Add("ReplyTo", m.ReplyTo);
        Add("ReplyToSessionId", m.ReplyToSessionId);
        Add("DeadLetterSource", m.DeadLetterSource);
        Add("DeadLetterReason", m.DeadLetterReason);
        Add("DeadLetterErrorDescription", m.DeadLetterErrorDescription);
        Add("EnqueuedSequenceNumber", m.EnqueuedSequenceNumber);
        Add("BodySize", Size(m.Body.ToMemory().Length));
        return list;
    }

    public static string PropertyValue(object? value) => value switch
    {
        null => "(null)",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Messages as a JSON array (system properties, application properties and body) for saving or copying.</summary>
    public static string ToJson(IEnumerable<ServiceBusReceivedMessage> messages)
    {
        var array = new JsonArray();
        foreach (var m in messages)
        {
            var system = new JsonObject();
            foreach (var (k, v) in SystemProperties(m)) system[k] = v;
            var app = new JsonObject();
            foreach (var (k, v) in m.ApplicationProperties) app[k] = PropertyValue(v);
            var (text, kind) = BodyText(m.Body, m.ContentType);
            JsonNode? body = kind switch
            {
                BodyKind.Json => JsonNode.Parse(text),
                BodyKind.Binary => Convert.ToBase64String(m.Body.ToArray()),
                _ => text,
            };
            array.Add(new JsonObject
            {
                ["systemProperties"] = system,
                ["applicationProperties"] = app,
                ["bodyEncoding"] = kind == BodyKind.Binary ? "base64" : "text",
                ["body"] = body,
            });
        }
        return array.ToJsonString(Indented);
    }

    /// <summary>Text for client-side filtering of peeked messages.</summary>
    public static string SearchText(ServiceBusReceivedMessage m) => string.Join('\n',
        m.MessageId, m.Subject, m.CorrelationId, m.SessionId, m.ContentType, m.DeadLetterReason, m.DeadLetterErrorDescription,
        string.Join('\n', m.ApplicationProperties.Select(p => $"{p.Key}={PropertyValue(p.Value)}")),
        TryDecodeText(m.Body.ToMemory().Span, out var t) ? t : "");
}
