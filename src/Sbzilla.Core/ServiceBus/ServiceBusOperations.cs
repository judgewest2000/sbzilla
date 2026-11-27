using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Sbzilla.Models;

namespace Sbzilla.ServiceBus;

/// <summary>Everything the explorer does against Service Bus: browsing, peeking, sending, deleting and administration.</summary>
public static class ServiceBusOperations
{
    private const int MaxParallelTopicLoads = 8;
    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AcceptSessionWait = TimeSpan.FromSeconds(4);

    /// <summary>Second line of defence behind the UI: nothing that changes Service Bus runs for a read-only group's namespace.</summary>
    private static void EnsureWritable(NamespaceConnection c)
    {
        if (c.IsReadOnly) throw new ReadOnlyNamespaceException(c.DisplayName);
    }

    // ------------------------------------------------------------------ browsing

    /// <summary>Every queue, topic and subscription with their message counts.</summary>
    public static async Task<NamespaceSnapshot> LoadAsync(NamespaceConnection c, CancellationToken ct)
    {
        if (c.ResolvedEntityPath.Length > 0) return await LoadScopedAsync(c, c.ResolvedEntityPath, ct);

        var admin = ServiceBusClients.Admin(c);
        var queuesTask = ToListAsync(admin.GetQueuesAsync(ct), ct);
        var queueRuntimeTask = ToListAsync(admin.GetQueuesRuntimePropertiesAsync(ct), ct);
        var topicRuntimeTask = ToListAsync(admin.GetTopicsRuntimePropertiesAsync(ct), ct);
        await Task.WhenAll(queuesTask, queueRuntimeTask, topicRuntimeTask);

        var queueProps = queuesTask.Result.ToDictionary(q => q.Name, StringComparer.OrdinalIgnoreCase);
        var queues = queueRuntimeTask.Result
            .Select(r => Map(r, queueProps.GetValueOrDefault(r.Name)))
            .OrderBy(q => q.Ref.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var gate = new SemaphoreSlim(MaxParallelTopicLoads);
        var topics = await Task.WhenAll(topicRuntimeTask.Result.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try { return Map(t, await LoadSubscriptionsAsync(admin, t.Name, ct)); }
            finally { gate.Release(); }
        }));
        return new NamespaceSnapshot
        {
            Queues = queues,
            Topics = topics.OrderBy(t => t.Ref.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>Fresh counts for one entity (a topic includes its subscriptions).</summary>
    public static async Task<EntityInfo> GetInfoAsync(NamespaceConnection c, EntityRef entity, CancellationToken ct)
    {
        var admin = ServiceBusClients.Admin(c);
        switch (entity.Kind)
        {
            case EntityKind.Queue:
                var qr = admin.GetQueueRuntimePropertiesAsync(entity.Name, ct);
                var qp = admin.GetQueueAsync(entity.Name, ct);
                await Task.WhenAll(qr, qp);
                return Map(qr.Result.Value, qp.Result.Value);
            case EntityKind.Topic:
                var tr = await admin.GetTopicRuntimePropertiesAsync(entity.Name, ct);
                return Map(tr.Value, await LoadSubscriptionsAsync(admin, entity.Name, ct));
            default:
                var sr = admin.GetSubscriptionRuntimePropertiesAsync(entity.Topic, entity.Name, ct);
                var sp = admin.GetSubscriptionAsync(entity.Topic, entity.Name, ct);
                await Task.WhenAll(sr, sp);
                return Map(sr.Result.Value, sp.Result.Value);
        }
    }

    /// <summary>An entity-scoped connection string can only see its own entity, and maybe not even its counts.</summary>
    private static async Task<NamespaceSnapshot> LoadScopedAsync(NamespaceConnection c, string path, CancellationToken ct)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[1].Equals("Subscriptions", StringComparison.OrdinalIgnoreCase))
        {
            var sub = await TryGetInfoAsync(c, EntityRef.Subscription(parts[0], parts[2]), ct);
            return new NamespaceSnapshot { Topics = [new EntityInfo { Ref = EntityRef.ForTopic(parts[0]), Subscriptions = [sub] }] };
        }
        try
        {
            return new NamespaceSnapshot { Queues = [await GetInfoAsync(c, EntityRef.Queue(path), ct)] };
        }
        catch (Exception ex) when (ex is RequestFailedException or UnauthorizedAccessException)
        {
            try { return new NamespaceSnapshot { Topics = [await GetInfoAsync(c, EntityRef.ForTopic(path), ct)] }; }
            catch (Exception ex2) when (ex2 is RequestFailedException or UnauthorizedAccessException)
            {
                // No Manage right to read counts: assume a queue, which can still be peeked, sent to and received from.
                return new NamespaceSnapshot { Queues = [new EntityInfo { Ref = EntityRef.Queue(path), Status = "unknown" }] };
            }
        }
    }

    private static async Task<EntityInfo> TryGetInfoAsync(NamespaceConnection c, EntityRef entity, CancellationToken ct)
    {
        try { return await GetInfoAsync(c, entity, ct); }
        catch (Exception ex) when (ex is RequestFailedException or UnauthorizedAccessException) { return new EntityInfo { Ref = entity, Status = "unknown" }; }
    }

    private static async Task<List<EntityInfo>> LoadSubscriptionsAsync(ServiceBusAdministrationClient admin, string topic, CancellationToken ct)
    {
        var runtime = ToListAsync(admin.GetSubscriptionsRuntimePropertiesAsync(topic, ct), ct);
        var props = ToListAsync(admin.GetSubscriptionsAsync(topic, ct), ct);
        await Task.WhenAll(runtime, props);
        var byName = props.Result.ToDictionary(s => s.SubscriptionName, StringComparer.OrdinalIgnoreCase);
        return runtime.Result
            .Select(r => Map(r, byName.GetValueOrDefault(r.SubscriptionName)))
            .OrderBy(s => s.Ref.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static EntityInfo Map(QueueRuntimeProperties r, QueueProperties? p) => new()
    {
        Ref = EntityRef.Queue(r.Name),
        ActiveCount = r.ActiveMessageCount,
        DeadLetterCount = r.DeadLetterMessageCount,
        ScheduledCount = r.ScheduledMessageCount,
        TransferCount = r.TransferMessageCount,
        TransferDeadLetterCount = r.TransferDeadLetterMessageCount,
        TotalCount = r.TotalMessageCount,
        SizeInBytes = r.SizeInBytes,
        RequiresSession = p?.RequiresSession ?? false,
        Status = p?.Status.ToString() ?? "",
        ForwardTo = p?.ForwardTo ?? "",
        UpdatedAt = r.UpdatedAt,
        AccessedAt = r.AccessedAt,
    };

    private static EntityInfo Map(TopicRuntimeProperties r, List<EntityInfo> subscriptions) => new()
    {
        Ref = EntityRef.ForTopic(r.Name),
        ScheduledCount = r.ScheduledMessageCount,
        SizeInBytes = r.SizeInBytes,
        UpdatedAt = r.UpdatedAt,
        AccessedAt = r.AccessedAt,
        Subscriptions = subscriptions,
    };

    private static EntityInfo Map(SubscriptionRuntimeProperties r, SubscriptionProperties? p) => new()
    {
        Ref = EntityRef.Subscription(r.TopicName, r.SubscriptionName),
        ActiveCount = r.ActiveMessageCount,
        DeadLetterCount = r.DeadLetterMessageCount,
        TransferCount = r.TransferMessageCount,
        TransferDeadLetterCount = r.TransferDeadLetterMessageCount,
        TotalCount = r.TotalMessageCount,
        RequiresSession = p?.RequiresSession ?? false,
        Status = p?.Status.ToString() ?? "",
        ForwardTo = p?.ForwardTo ?? "",
        UpdatedAt = r.UpdatedAt,
        AccessedAt = r.AccessedAt,
    };

    private static async Task<List<T>> ToListAsync<T>(AsyncPageable<T> pages, CancellationToken ct) where T : notnull
    {
        var list = new List<T>();
        await foreach (var item in pages.WithCancellation(ct)) list.Add(item);
        return list;
    }

    // ------------------------------------------------------------------ administration

    /// <summary>QueueProperties, TopicProperties or SubscriptionProperties.</summary>
    public static async Task<object> GetPropertiesAsync(NamespaceConnection c, EntityRef entity, CancellationToken ct)
    {
        var admin = ServiceBusClients.Admin(c);
        return entity.Kind switch
        {
            EntityKind.Queue => (await admin.GetQueueAsync(entity.Name, ct)).Value,
            EntityKind.Topic => (await admin.GetTopicAsync(entity.Name, ct)).Value,
            _ => (await admin.GetSubscriptionAsync(entity.Topic, entity.Name, ct)).Value,
        };
    }

    public static async Task UpdatePropertiesAsync(NamespaceConnection c, object properties, CancellationToken ct)
    {
        EnsureWritable(c);
        var admin = ServiceBusClients.Admin(c);
        switch (properties)
        {
            case QueueProperties q: await admin.UpdateQueueAsync(q, ct); break;
            case TopicProperties t: await admin.UpdateTopicAsync(t, ct); break;
            case SubscriptionProperties s: await admin.UpdateSubscriptionAsync(s, ct); break;
            default: throw new ArgumentException("Unknown entity properties.", nameof(properties));
        }
    }

    /// <summary>Creates from CreateQueueOptions, CreateTopicOptions or CreateSubscriptionOptions (optionally with a first rule).</summary>
    public static async Task<EntityRef> CreateAsync(NamespaceConnection c, object options, CreateRuleOptions? rule, CancellationToken ct)
    {
            EnsureWritable(c);
        var admin = ServiceBusClients.Admin(c);
        switch (options)
        {
            case CreateQueueOptions q:
                await admin.CreateQueueAsync(q, ct);
                return EntityRef.Queue(q.Name);
            case CreateTopicOptions t:
                await admin.CreateTopicAsync(t, ct);
                return EntityRef.ForTopic(t.Name);
            case CreateSubscriptionOptions s:
                if (rule != null) await admin.CreateSubscriptionAsync(s, rule, ct);
                else await admin.CreateSubscriptionAsync(s, ct);
                return EntityRef.Subscription(s.TopicName, s.SubscriptionName);
            default:
                throw new ArgumentException("Unknown entity options.", nameof(options));
        }
    }

    public static async Task DeleteEntityAsync(NamespaceConnection c, EntityRef entity, CancellationToken ct)
    {
        EnsureWritable(c);
        var admin = ServiceBusClients.Admin(c);
        switch (entity.Kind)
        {
            case EntityKind.Queue: await admin.DeleteQueueAsync(entity.Name, ct); break;
            case EntityKind.Topic: await admin.DeleteTopicAsync(entity.Name, ct); break;
            default: await admin.DeleteSubscriptionAsync(entity.Topic, entity.Name, ct); break;
        }
    }

    public static async Task<List<RuleProperties>> GetRulesAsync(NamespaceConnection c, EntityRef subscription, CancellationToken ct) =>
        await ToListAsync(ServiceBusClients.Admin(c).GetRulesAsync(subscription.Topic, subscription.Name, ct), ct);

    public static async Task CreateRuleAsync(NamespaceConnection c, EntityRef subscription, CreateRuleOptions rule, CancellationToken ct)
    {
        EnsureWritable(c);
        await ServiceBusClients.Admin(c).CreateRuleAsync(subscription.Topic, subscription.Name, rule, ct);
    }

    public static async Task DeleteRuleAsync(NamespaceConnection c, EntityRef subscription, string ruleName, CancellationToken ct)
    {
        EnsureWritable(c);
        await ServiceBusClients.Admin(c).DeleteRuleAsync(subscription.Topic, subscription.Name, ruleName, ct);
    }

    // ------------------------------------------------------------------ messages

    private static ServiceBusReceiverOptions ReceiverOptions(MessageSource source, ServiceBusReceiveMode mode) => new()
    {
        SubQueue = source switch
        {
            MessageSource.DeadLetter => SubQueue.DeadLetter,
            MessageSource.TransferDeadLetter => SubQueue.TransferDeadLetter,
            _ => SubQueue.None,
        },
        ReceiveMode = mode,
        PrefetchCount = 0,
    };

    private static ServiceBusReceiver CreateReceiver(ServiceBusClient client, EntityRef entity, ServiceBusReceiverOptions options) =>
        entity.Kind == EntityKind.Subscription
            ? client.CreateReceiver(entity.Topic, entity.Name, options)
            : client.CreateReceiver(entity.Name, options);

    /// <summary>Only the active queue of a session-enabled entity needs session receivers; dead-letter queues never do.</summary>
    private static bool UsesSessions(bool requiresSession, MessageSource source) => requiresSession && source == MessageSource.Active;

    private static Task<ServiceBusSessionReceiver> AcceptSessionAsync(ServiceBusClient client, EntityRef entity, string sessionId,
        ServiceBusReceiveMode mode, CancellationToken ct)
    {
        var options = new ServiceBusSessionReceiverOptions { ReceiveMode = mode, PrefetchCount = 0 };
        return entity.Kind == EntityKind.Subscription
            ? client.AcceptSessionAsync(entity.Topic, entity.Name, sessionId, options, ct)
            : client.AcceptSessionAsync(entity.Name, sessionId, options, ct);
    }

    /// <summary>The next unlocked session, or null once none are left (Service Bus has no way to list sessions).</summary>
    private static async Task<ServiceBusSessionReceiver?> AcceptNextSessionAsync(ServiceBusClient client, EntityRef entity,
        ServiceBusReceiveMode mode, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AcceptSessionWait);
        var options = new ServiceBusSessionReceiverOptions { ReceiveMode = mode, PrefetchCount = 0 };
        try
        {
            return entity.Kind == EntityKind.Subscription
                ? await client.AcceptNextSessionAsync(entity.Topic, entity.Name, options, timeout.Token)
                : await client.AcceptNextSessionAsync(entity.Name, options, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.ServiceTimeout) { return null; }
    }

    /// <summary>
    /// Peeks up to <paramref name="max"/> messages without locking them. For a session-enabled entity's active queue,
    /// peeks the given session, or (when <paramref name="sessionId"/> is empty) each session that isn't locked by a consumer.
    /// </summary>
    public static async Task<List<ServiceBusReceivedMessage>> PeekAsync(NamespaceConnection c, EntityRef entity, MessageSource source,
        int max, long? fromSequenceNumber, bool requiresSession, string? sessionId, CancellationToken ct)
    {
        var client = ServiceBusClients.Client(c);
        if (!UsesSessions(requiresSession, source))
        {
            await using var receiver = CreateReceiver(client, entity, ReceiverOptions(source, ServiceBusReceiveMode.PeekLock));
            return await PeekFromAsync(receiver, max, fromSequenceNumber, ct);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await using var session = await AcceptSessionAsync(client, entity, sessionId.Trim(), ServiceBusReceiveMode.PeekLock, ct);
            return await PeekFromAsync(session, max, fromSequenceNumber, ct);
        }

        // Accepted sessions stay open until the end so AcceptNextSession moves on to a different one.
        var sessions = new List<ServiceBusSessionReceiver>();
        var result = new List<ServiceBusReceivedMessage>();
        try
        {
            while (result.Count < max && sessions.Count < 100 && await AcceptNextSessionAsync(client, entity, ServiceBusReceiveMode.PeekLock, ct) is { } session)
            {
                sessions.Add(session);
                result.AddRange(await PeekFromAsync(session, max - result.Count, fromSequenceNumber, ct));
            }
        }
        finally
        {
            foreach (var s in sessions) await s.DisposeAsync();
        }
        return result.OrderBy(m => m.SequenceNumber).ToList();
    }

    private static async Task<List<ServiceBusReceivedMessage>> PeekFromAsync(ServiceBusReceiver receiver, int max, long? fromSequenceNumber, CancellationToken ct)
    {
        var result = new List<ServiceBusReceivedMessage>();
        var next = fromSequenceNumber;
        while (result.Count < max)
        {
            var batch = await receiver.PeekMessagesAsync(Math.Min(250, max - result.Count), next, ct);
            if (batch.Count == 0) break;
            result.AddRange(batch);
            next = batch[^1].SequenceNumber + 1;
        }
        return result;
    }

    /// <summary>
    /// Peeks the newest <paramref name="max"/> messages (those below <paramref name="beforeSequenceNumber"/> when given), in ascending
    /// sequence order. Service Bus can only peek forwards, so this locates the end of the queue by searching on sequence number and then
    /// reads backwards in windows. For a session-enabled entity's active queue without a session ID, every available session is searched.
    /// </summary>
    public static async Task<List<ServiceBusReceivedMessage>> PeekLatestAsync(NamespaceConnection c, EntityRef entity, MessageSource source,
        int max, long? beforeSequenceNumber, bool requiresSession, string? sessionId, CancellationToken ct)
    {
        var client = ServiceBusClients.Client(c);
        if (!UsesSessions(requiresSession, source))
        {
            await using var receiver = CreateReceiver(client, entity, ReceiverOptions(source, ServiceBusReceiveMode.PeekLock));
            return await PeekLatestFromAsync(receiver, max, beforeSequenceNumber, ct);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await using var session = await AcceptSessionAsync(client, entity, sessionId.Trim(), ServiceBusReceiveMode.PeekLock, ct);
            return await PeekLatestFromAsync(session, max, beforeSequenceNumber, ct);
        }

        // The newest messages could be in any session, so all of them (up to 100) have to be searched.
        var sessions = new List<ServiceBusSessionReceiver>();
        var result = new List<ServiceBusReceivedMessage>();
        try
        {
            while (sessions.Count < 100 && await AcceptNextSessionAsync(client, entity, ServiceBusReceiveMode.PeekLock, ct) is { } session)
            {
                sessions.Add(session);
                result.AddRange(await PeekLatestFromAsync(session, max, beforeSequenceNumber, ct));
            }
        }
        finally
        {
            foreach (var s in sessions) await s.DisposeAsync();
        }
        return result.OrderBy(m => m.SequenceNumber).TakeLast(max).ToList();
    }

    private static async Task<List<ServiceBusReceivedMessage>> PeekLatestFromAsync(ServiceBusReceiver receiver, int max, long? beforeSequenceNumber, CancellationToken ct)
    {
        var first = await PeekOneAsync(receiver, null, ct);
        if (first == null) return [];
        var low = first.SequenceNumber;
        var end = beforeSequenceNumber ?? await FindEndAsync(receiver, low, max, ct);

        // Read windows [from, start) backwards until there are enough; sequence numbers have gaps, so size each window by the density seen so far.
        var result = new List<ServiceBusReceivedMessage>();
        var start = end;
        var width = (long)Math.Max(1, max);
        while (result.Count < max && start > low)
        {
            var from = start - low > width ? start - width : low;
            var window = await PeekRangeAsync(receiver, from, start, ct);
            result.InsertRange(0, window);
            var length = start - from;
            start = from;
            var needed = max - result.Count;
            width = window.Count == 0
                ? Math.Min(length, long.MaxValue / 4) * 2
                : Math.Max(1, (long)Math.Ceiling(needed * (double)length / window.Count * 1.25));
        }
        return result.TakeLast(max).ToList();
    }

    /// <summary>Returns one past the highest sequence number in the receiver, given a known message at <paramref name="low"/>.</summary>
    private static async Task<long> FindEndAsync(ServiceBusReceiver receiver, long low, int step, CancellationToken ct)
    {
        // Gallop forwards to find a sequence number with nothing at or after it...
        var last = low;
        var jump = (long)Math.Max(1, step);
        long high;
        while (true)
        {
            if (jump > long.MaxValue - last) { high = long.MaxValue; break; }
            var probe = await PeekOneAsync(receiver, last + jump, ct);
            if (probe == null) { high = last + jump; break; }
            last = probe.SequenceNumber;
            jump = Math.Min(jump, long.MaxValue / 4) * 2;
        }

        // ...then binary search between the last message found and that point.
        while (high - last > 1)
        {
            var mid = last + (high - last) / 2;
            var probe = await PeekOneAsync(receiver, mid, ct);
            if (probe == null) high = mid;
            else last = probe.SequenceNumber;
        }
        return last + 1;
    }

    private static async Task<ServiceBusReceivedMessage?> PeekOneAsync(ServiceBusReceiver receiver, long? fromSequenceNumber, CancellationToken ct) =>
        (await receiver.PeekMessagesAsync(1, fromSequenceNumber, ct)).FirstOrDefault();

    /// <summary>Peeks every message with a sequence number in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    private static async Task<List<ServiceBusReceivedMessage>> PeekRangeAsync(ServiceBusReceiver receiver, long from, long to, CancellationToken ct)
    {
        var result = new List<ServiceBusReceivedMessage>();
        var next = from;
        while (next < to)
        {
            var batch = await receiver.PeekMessagesAsync(250, next, ct);
            if (batch.Count == 0) break;
            var inRange = batch.Where(m => m.SequenceNumber < to).ToList();
            result.AddRange(inRange);
            if (inRange.Count < batch.Count) break;
            next = batch[^1].SequenceNumber + 1;
        }
        return result;
    }

    /// <summary>Deletes every message in the queue (or dead-letter queue). Returns how many were removed.</summary>
    public static async Task<int> PurgeAsync(NamespaceConnection c, EntityRef entity, MessageSource source, bool requiresSession,
        IProgress<int>? progress, CancellationToken ct)
    {
        EnsureWritable(c);
        var client = ServiceBusClients.Client(c);
        var total = 0;
        if (!UsesSessions(requiresSession, source))
        {
            await using var receiver = CreateReceiver(client, entity, ReceiverOptions(source, ServiceBusReceiveMode.ReceiveAndDelete));
            total = await DrainAsync(receiver, progress, 0, ct);
            return total;
        }

        while (await AcceptNextSessionAsync(client, entity, ServiceBusReceiveMode.ReceiveAndDelete, ct) is { } session)
        {
            await using (session) total = await DrainAsync(session, progress, total, ct);
        }
        return total;
    }

    private static async Task<int> DrainAsync(ServiceBusReceiver receiver, IProgress<int>? progress, int total, CancellationToken ct)
    {
        while (true)
        {
            var removed = (await receiver.ReceiveMessagesAsync(500, ReceiveWait, ct)).Count;
            if (removed == 0) return total;
            total += removed;
            progress?.Report(total);
        }
    }

    /// <summary>A message picked for deletion: its sequence number, and its session (for session-enabled entities).</summary>
    public readonly record struct MessageKey(long SequenceNumber, string? SessionId);

    /// <summary>
    /// Deletes specific messages. Service Bus can only remove a message by receiving it, so this receives (locks) messages
    /// until it has found every target, completes the targets and abandons the rest. Abandoning counts as a delivery,
    /// which for an active queue can dead-letter messages already at their max delivery count.
    /// </summary>
    public static async Task<int> DeleteAsync(NamespaceConnection c, EntityRef entity, MessageSource source, bool requiresSession,
        IReadOnlyCollection<MessageKey> targets, IProgress<int>? progress, CancellationToken ct)
    {
        EnsureWritable(c);
        var client = ServiceBusClients.Client(c);
        if (!UsesSessions(requiresSession, source))
        {
            await using var receiver = CreateReceiver(client, entity, ReceiverOptions(source, ServiceBusReceiveMode.PeekLock));
            return await CompleteMatchingAsync(receiver, targets.Select(t => t.SequenceNumber), progress, 0, ct);
        }

        var removed = 0;
        foreach (var group in targets.GroupBy(t => t.SessionId ?? ""))
        {
            await using var session = await AcceptSessionAsync(client, entity, group.Key, ServiceBusReceiveMode.PeekLock, ct);
            removed = await CompleteMatchingAsync(session, group.Select(t => t.SequenceNumber), progress, removed, ct);
        }
        return removed;
    }

    private static async Task<int> CompleteMatchingAsync(ServiceBusReceiver receiver, IEnumerable<long> sequenceNumbers,
        IProgress<int>? progress, int removed, CancellationToken ct)
    {
        var remaining = sequenceNumbers.ToHashSet();
        var held = new List<ServiceBusReceivedMessage>();
        try
        {
            var emptyPolls = 0;
            while (remaining.Count > 0 && emptyPolls < 2 && held.Count < 20_000)
            {
                var batch = await receiver.ReceiveMessagesAsync(250, ReceiveWait, ct);
                if (batch.Count == 0) { emptyPolls++; continue; }
                emptyPolls = 0;
                foreach (var m in batch)
                {
                    if (remaining.Remove(m.SequenceNumber))
                    {
                        await receiver.CompleteMessageAsync(m, ct);
                        progress?.Report(++removed);
                    }
                    else held.Add(m);
                }
            }
        }
        finally
        {
            foreach (var m in held)
            {
                try { await receiver.AbandonMessageAsync(m, cancellationToken: CancellationToken.None); }
                catch (ServiceBusException) { }   // lock expired: the message is released anyway
            }
        }
        return removed;
    }

    /// <summary>
    /// Sends copies of (usually dead-lettered) messages back to the entity's send target, optionally removing the originals.
    /// For a subscription the copies go to its topic, so every subscription whose rules match receives them.
    /// </summary>
    public static async Task<int> ResubmitAsync(NamespaceConnection c, EntityRef entity, MessageSource source, bool requiresSession,
        IReadOnlyList<ServiceBusReceivedMessage> messages, bool removeOriginals, IProgress<int>? progress, CancellationToken ct)
    {
                EnsureWritable(c);
        await using var sender = ServiceBusClients.Client(c).CreateSender(entity.SendTarget);
        var sent = 0;
        foreach (var chunk in messages.Chunk(100))
        {
            await SendBatchesAsync(sender, chunk.Select(MessageDrafts.CloneForResubmit), ct);
            sent += chunk.Length;
            progress?.Report(sent);
        }
        if (removeOriginals)
            await DeleteAsync(c, entity, source, requiresSession, messages.Select(m => new MessageKey(m.SequenceNumber, m.SessionId)).ToList(), null, ct);
        return sent;
    }

    /// <summary>Sends <paramref name="copies"/> messages built from <paramref name="draft"/> to a queue or topic.</summary>
    public static async Task<int> SendAsync(NamespaceConnection c, string queueOrTopic, MessageDraft draft, int copies,
        IProgress<int>? progress, CancellationToken ct)
    {
        EnsureWritable(c);
        await using var sender = ServiceBusClients.Client(c).CreateSender(queueOrTopic);
        var sent = 0;
        for (var start = 1; start <= copies; start += 100)
        {
            var count = Math.Min(100, copies - start + 1);
            var messages = Enumerable.Range(start, count).Select(draft.ToMessage).ToList();
            if (draft.ScheduledEnqueueTime is { } when)
                await sender.ScheduleMessagesAsync(messages, when, ct);
            else
                await SendBatchesAsync(sender, messages, ct);
            sent += count;
            progress?.Report(sent);
        }
        return sent;
    }

    private static async Task SendBatchesAsync(ServiceBusSender sender, IEnumerable<ServiceBusMessage> messages, CancellationToken ct)
    {
        var batch = await sender.CreateMessageBatchAsync(ct);
        try
        {
            foreach (var m in messages)
            {
                if (batch.TryAddMessage(m)) continue;
                if (batch.Count == 0) throw new InvalidOperationException("The message is larger than the namespace allows.");
                await sender.SendMessagesAsync(batch, ct);
                batch.Dispose();
                batch = await sender.CreateMessageBatchAsync(ct);
                if (!batch.TryAddMessage(m)) throw new InvalidOperationException("The message is larger than the namespace allows.");
            }
            if (batch.Count > 0) await sender.SendMessagesAsync(batch, ct);
        }
        finally { batch.Dispose(); }
    }
}
