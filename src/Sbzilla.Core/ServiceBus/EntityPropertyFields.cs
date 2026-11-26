using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Azure.Messaging.ServiceBus.Administration;

namespace Sbzilla.ServiceBus;

/// <summary>
/// One editable setting of a queue, topic or subscription. The same property names exist on the Create…Options classes
/// and the …Properties classes, so one list drives both the "New" and "Edit" forms; on …Properties, settings that can't be
/// changed after creation have no setter and show as read-only.
/// </summary>
public sealed record PropertyField(string Name, string Label, string Hint = "");

public static partial class EntityPropertyFields
{
    private static readonly PropertyField[] QueueFields =
    [
        new("MaxSizeInMegabytes", "Max size (MB)"),
        new("LockDuration", "Lock duration", "e.g. 30s, 5m (max 5 minutes)"),
        new("MaxDeliveryCount", "Max delivery count", "Deliveries before a message is dead-lettered"),
        new("DefaultMessageTimeToLive", "Default message time to live", "e.g. 14d, 1h, or never"),
        new("AutoDeleteOnIdle", "Auto-delete on idle", "e.g. 7d, or never"),
        new("DuplicateDetectionHistoryTimeWindow", "Duplicate detection window", "e.g. 10m"),
        new("ForwardTo", "Forward messages to", "Queue or topic name"),
        new("ForwardDeadLetteredMessagesTo", "Forward dead-lettered messages to", "Queue or topic name"),
        new("UserMetadata", "User metadata"),
        new("Status", "Status"),
        new("RequiresSession", "Requires session"),
        new("RequiresDuplicateDetection", "Requires duplicate detection"),
        new("EnablePartitioning", "Enable partitioning"),
        new("DeadLetteringOnMessageExpiration", "Dead-letter expired messages"),
        new("EnableBatchedOperations", "Enable batched operations"),
    ];

    private static readonly PropertyField[] TopicFields =
    [
        new("MaxSizeInMegabytes", "Max size (MB)"),
        new("DefaultMessageTimeToLive", "Default message time to live", "e.g. 14d, 1h, or never"),
        new("AutoDeleteOnIdle", "Auto-delete on idle", "e.g. 7d, or never"),
        new("DuplicateDetectionHistoryTimeWindow", "Duplicate detection window", "e.g. 10m"),
        new("UserMetadata", "User metadata"),
        new("Status", "Status"),
        new("RequiresDuplicateDetection", "Requires duplicate detection"),
        new("EnablePartitioning", "Enable partitioning"),
        new("SupportOrdering", "Support ordering"),
        new("EnableBatchedOperations", "Enable batched operations"),
    ];

    private static readonly PropertyField[] SubscriptionFields =
    [
        new("LockDuration", "Lock duration", "e.g. 30s, 5m (max 5 minutes)"),
        new("MaxDeliveryCount", "Max delivery count", "Deliveries before a message is dead-lettered"),
        new("DefaultMessageTimeToLive", "Default message time to live", "e.g. 14d, 1h, or never"),
        new("AutoDeleteOnIdle", "Auto-delete on idle", "e.g. 7d, or never"),
        new("ForwardTo", "Forward messages to", "Queue or topic name"),
        new("ForwardDeadLetteredMessagesTo", "Forward dead-lettered messages to", "Queue or topic name"),
        new("UserMetadata", "User metadata"),
        new("Status", "Status"),
        new("RequiresSession", "Requires session"),
        new("DeadLetteringOnMessageExpiration", "Dead-letter expired messages"),
        new("EnableDeadLetteringOnFilterEvaluationExceptions", "Dead-letter on filter evaluation errors"),
        new("EnableBatchedOperations", "Enable batched operations"),
    ];

    public static IReadOnlyList<PropertyField> For(EntityKind kind) => kind switch
    {
        EntityKind.Queue => QueueFields,
        EntityKind.Topic => TopicFields,
        _ => SubscriptionFields,
    };

    /// <summary>The fields that exist on <paramref name="target"/>'s type, with their reflection info.</summary>
    public static IEnumerable<(PropertyField Field, PropertyInfo Property)> Bind(EntityKind kind, object target) =>
        For(kind)
            .Select(f => (f, target.GetType().GetProperty(f.Name, BindingFlags.Public | BindingFlags.Instance)))
            .Where(x => x.Item2 != null)
            .Select(x => (x.f, x.Item2!));

    public static string Format(object? value) => value switch
    {
        null => "",
        TimeSpan t => FormatTimeSpan(t),
        EntityStatus s => s.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>"never" for the maximum (Service Bus's "no limit"), else the largest whole units, e.g. "14d", "1h 30m", "45s".</summary>
    public static string FormatTimeSpan(TimeSpan t)
    {
        if (t >= TimeSpan.FromDays(3650)) return "never";
        if (t == TimeSpan.Zero) return "0s";
        var parts = new List<string>();
        if (t.Days > 0) parts.Add($"{t.Days}d");
        if (t.Hours > 0) parts.Add($"{t.Hours}h");
        if (t.Minutes > 0) parts.Add($"{t.Minutes}m");
        if (t.Seconds > 0) parts.Add($"{t.Seconds}s");
        if (t.Milliseconds > 0) parts.Add($"{t.Milliseconds}ms");
        return string.Join(' ', parts);
    }

    /// <summary>Accepts "never"/"max", unit shorthand ("1d 2h", "90s", "500ms"), or .NET's d.hh:mm:ss.</summary>
    public static TimeSpan ParseTimeSpan(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) throw new FormatException("Enter a duration, e.g. 30s, 5m, 14d or never.");
        if (s.Equals("never", StringComparison.OrdinalIgnoreCase) || s.Equals("max", StringComparison.OrdinalIgnoreCase)
            || s == "∞" || s.Equals("infinite", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.MaxValue;

        var matches = DurationPart().Matches(s);
        if (matches.Count > 0 && DurationPart().Replace(s, "").Trim().Length == 0)
        {
            var total = TimeSpan.Zero;
            foreach (Match m in matches)
            {
                var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                total += m.Groups["u"].Value.ToLowerInvariant() switch
                {
                    "ms" => TimeSpan.FromMilliseconds(n),
                    "s" => TimeSpan.FromSeconds(n),
                    "m" => TimeSpan.FromMinutes(n),
                    "h" => TimeSpan.FromHours(n),
                    _ => TimeSpan.FromDays(n),
                };
            }
            return total;
        }
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new FormatException($"\"{text}\" isn't a duration. Use e.g. 30s, 5m, 1h 30m, 14d or never.");
    }

    /// <summary>Parses text from the form into the property's type (not used for bools and enums, which have their own controls).</summary>
    public static object? Parse(Type type, string text)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return text.Trim().Length == 0 ? null : text.Trim();
        if (t == typeof(TimeSpan)) return ParseTimeSpan(text);
        if (t == typeof(int)) return int.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (t == typeof(long)) return long.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        throw new NotSupportedException($"Can't edit a {t.Name}.");
    }

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<u>ms|d|h|m|s)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();

    /// <summary>A rule's filter, as one line of text.</summary>
    public static string Describe(RuleFilter? filter) => filter switch
    {
        null => "",
        TrueRuleFilter => "TRUE (all messages)",
        FalseRuleFilter => "FALSE (no messages)",
        SqlRuleFilter sql => sql.SqlExpression,
        CorrelationRuleFilter c => DescribeCorrelation(c),
        _ => filter.ToString() ?? "",
    };

    public static string Describe(RuleAction? action) => action is SqlRuleAction sql ? sql.SqlExpression : "";

    private static string DescribeCorrelation(CorrelationRuleFilter c)
    {
        var parts = new List<string>();
        void Add(string name, string? value) { if (!string.IsNullOrEmpty(value)) parts.Add($"{name} = '{value}'"); }
        Add("CorrelationId", c.CorrelationId);
        Add("MessageId", c.MessageId);
        Add("Subject", c.Subject);
        Add("To", c.To);
        Add("ReplyTo", c.ReplyTo);
        Add("SessionId", c.SessionId);
        Add("ReplyToSessionId", c.ReplyToSessionId);
        Add("ContentType", c.ContentType);
        foreach (var (k, v) in c.ApplicationProperties) parts.Add($"{k} = '{v}'");
        return "Correlation: " + (parts.Count == 0 ? "(empty)" : string.Join(" AND ", parts));
    }
}
