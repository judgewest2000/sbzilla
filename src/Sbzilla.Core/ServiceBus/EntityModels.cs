namespace Sbzilla.ServiceBus;

public enum EntityKind { Queue, Topic, Subscription }

/// <summary>Where messages are peeked from or deleted in.</summary>
public enum MessageSource { Active, DeadLetter, TransferDeadLetter }

/// <summary>A queue, topic, or subscription (<see cref="Name"/> under <see cref="Topic"/>).</summary>
public sealed record EntityRef(EntityKind Kind, string Name, string? Topic = null)
{
    public static EntityRef Queue(string name) => new(EntityKind.Queue, name);
    public static EntityRef ForTopic(string name) => new(EntityKind.Topic, name);
    public static EntityRef Subscription(string topic, string name) => new(EntityKind.Subscription, name, topic);

    /// <summary>The entity path as Service Bus spells it, e.g. "orders/Subscriptions/audit".</summary>
    public string Path => Kind == EntityKind.Subscription ? $"{Topic}/Subscriptions/{Name}" : Name;

    /// <summary>Where sends go: the queue, or the topic (for a topic or one of its subscriptions).</summary>
    public string SendTarget => Kind == EntityKind.Subscription ? Topic! : Name;

    public bool HasMessages => Kind != EntityKind.Topic;

    public string DisplayName => Kind == EntityKind.Subscription ? $"{Topic}/{Name}" : Name;

    public string Key => $"{Kind}:{Path}".ToLowerInvariant();

    public static string KindLabel(EntityKind kind) => kind switch
    {
        EntityKind.Queue => "Queue",
        EntityKind.Topic => "Topic",
        _ => "Subscription",
    };

    public static string SourceLabel(MessageSource source) => source switch
    {
        MessageSource.DeadLetter => "Dead-letter",
        MessageSource.TransferDeadLetter => "Transfer dead-letter",
        _ => "Active",
    };
}

/// <summary>Runtime counts and the flags the explorer needs for one entity.</summary>
public sealed class EntityInfo
{
    public EntityRef Ref { get; init; } = new(EntityKind.Queue, "");
    public long ActiveCount { get; init; }
    public long DeadLetterCount { get; init; }
    public long ScheduledCount { get; init; }
    public long TransferCount { get; init; }
    public long TransferDeadLetterCount { get; init; }
    public long TotalCount { get; init; }
    public long SizeInBytes { get; init; }
    public bool RequiresSession { get; init; }
    public string Status { get; init; } = "";
    public string ForwardTo { get; init; } = "";
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? AccessedAt { get; init; }
    public List<EntityInfo> Subscriptions { get; init; } = new();

    public long CountFor(MessageSource source) => source switch
    {
        MessageSource.DeadLetter => DeadLetterCount,
        MessageSource.TransferDeadLetter => TransferDeadLetterCount,
        _ => ActiveCount,
    };

    /// <summary>"(active, dead-letter)" like Service Bus Explorer; topics show their subscription count.</summary>
    public string CountsLabel => Ref.Kind == EntityKind.Topic
        ? $"({Subscriptions.Count} sub{(Subscriptions.Count == 1 ? "" : "s")})"
        : $"({ActiveCount:N0}, {DeadLetterCount:N0})";
}

public sealed class NamespaceSnapshot
{
    public List<EntityInfo> Queues { get; init; } = new();
    public List<EntityInfo> Topics { get; init; } = new();

    public IEnumerable<EntityInfo> All => Queues.Concat(Topics).Concat(Topics.SelectMany(t => t.Subscriptions));

    public EntityInfo? Find(EntityRef entity) => All.FirstOrDefault(e => e.Ref.Key == entity.Key);
}

/// <summary>Thrown when something tries to change Service Bus through a namespace in a read-only group.</summary>
public sealed class ReadOnlyNamespaceException(string namespaceName)
    : InvalidOperationException($"“{namespaceName}” is in a read-only group, so Sbzilla won't change anything in it.");
