using System.Text.Json;
using System.Collections.Concurrent;
using LanChat.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed class ReplicationBackgroundService(IDistributedStore store, ChatStore chat, IClusterTransport transport, IConsensusService consensus,
    NodeOptions options, FaultInjection faults, NodeRuntimeState runtime, ILogger<ReplicationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan ActiveReconciliationInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan QuietReconciliationInterval = TimeSpan.FromSeconds(12);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _leaderGate = new();
    private Guid? _newLeader;
    private CancellationTokenSource? _normalRound;
    private long _lastPeerRevision;
    private readonly ConcurrentDictionary<Guid, (string? Local, string? Remote)> _groupHistoryRevisions = new();
    private void OnLeaderReady(Guid node, long term)
    {
        lock (_leaderGate)
        {
            _newLeader = node;
            _normalRound?.Cancel();
        }
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
    public Task SynchronizeAsync(CancellationToken ct) => SynchronizeAsync(null, ct);
    internal async Task<bool> SynchronizeAsync(Guid? preferred, CancellationToken ct, bool activeOnly = false)
    {
        if (faults.PauseReplication) return false;
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var peers = activeOnly ? chat.GetActivePeerEndpoints(ct) : await store.GetPeerEndpointsAsync(ct);
            if (activeOnly && peers.Length == 0) return false;
            var pending = await store.GetOutboxAsync(ct);
            if (peers.Length == 0)
            {
                if (pending.Length > 0)
                    await store.RecordOutboxResultAsync(pending.Select(x => x.Id).ToArray(), false, ct);
                return false;
            }

            var self = await chat.GetNodeInfoAsync(ct);
            var leader = preferred;
            if (leader is null && peers.Length > 1)
            {
                var state = await consensus.GetStatusAsync(ct);
                leader = state.PairCoordinatorNodeId ?? state.LeaderNodeId;
            }

            var pendingIds = new HashSet<Guid>(pending.Select(x => x.Id));
            var acknowledged = new HashSet<Guid>();
            var synced = 0;
            var changed = 0;
            async Task SyncPeer(PeerEndpoint peer, CancellationToken token)
            {
                try
                {
                    if (pending.Length > 0)
                    {
                        var ack = await transport.CallAsync<EventAck>(peer, "EventPush", new EventBatch(pending), token);
                        lock (acknowledged) foreach (var id in ack.EventIds) if (pendingIds.Contains(id)) acknowledged.Add(id);
                    }
                    if (await ReconcilePeerAsync(peer, self, token)) Interlocked.Increment(ref changed);
                    Interlocked.Increment(ref synced);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (NetworkFailure(ex))
                { logger.LogDebug("Replication peer unavailable {Node}: {Type}", peer.NodeId, ex.GetType().Name); }
            }

            var first = preferred.HasValue ? peers.FirstOrDefault(peer => peer.NodeId == preferred) : null;
            if (first is not null)
            {
                logger.LogInformation("Immediate sync started with leader {Node}", first.NodeId);
                await SyncPeer(first, ct);
            }

            await FrameworkIO.ForEachAsync(peers.Where(peer => peer.NodeId != first?.NodeId)
                .OrderByDescending(peer => peer.NodeId == leader), 4, ct, SyncPeer);
            if (acknowledged.Count > 0) await store.RecordOutboxResultAsync(acknowledged.ToArray(), true, ct);
            var failed = pendingIds.Except(acknowledged).ToArray();
            if (failed.Length > 0) await store.RecordOutboxResultAsync(failed, false, ct);
            if (synced > 0) faults.LastSync = DateTimeOffset.UtcNow;
            return synced == peers.Length && pending.Length == 0 && changed == 0;
        }
        finally { _sync.Release(); }
    }
    private static bool NetworkFailure(Exception ex) => ex is ObjectDisposedException or IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or JsonException or System.Security.Authentication.AuthenticationException;
    public async Task ReconcilePeerAsync(PeerEndpoint peer, CancellationToken ct) =>
        await ReconcilePeerAsync(peer, await chat.GetNodeInfoAsync(ct), ct);

    private async Task<bool> ReconcilePeerAsync(PeerEndpoint peer, NodeInfoDto self, CancellationToken ct)
    {
        var remote = await transport.CallAsync<ReplicationDigest>(peer, "Digest", self, ct);
        var localRevision = chat.Revision;
        var localDigest = await store.GetDigestAsync(ct);
        var transferred = false;
        var revision = (Local: localDigest.GroupHistoryRevision, Remote: remote.GroupHistoryRevision);
        var replayHistory = remote.SupportsGroupHistory &&
            (!_groupHistoryRevisions.TryGetValue(peer.NodeId, out var previous) || previous != revision);
        var local = localDigest.Cursors.ToDictionary(x => x.OriginNodeId, x => x.LastOriginSequence);
        foreach (var origin in remote.Cursors)
        {
            var cursor = replayHistory ? 0 : local.GetValueOrDefault(origin.OriginNodeId);
            while (cursor < origin.LastOriginSequence)
            {
                transferred = true;
                var batch = await transport.CallAsync<EventBatch>(peer, "EventRange",
                    new EventRangeRequest(origin.OriginNodeId, cursor) { IncludeGroupHistory = remote.SupportsGroupHistory }, ct);
                if (batch.Events.Length == 0) break;
                if (batch.Events.Any(item => item.OriginNodeId != origin.OriginNodeId || item.OriginSequence <= cursor))
                    throw new InvalidDataException("Trang lịch sử không khớp vị trí đồng bộ.");
                await store.ImportEventsAsync(batch, ct);
                var next = batch.Events.Last().OriginSequence;
                if (next <= cursor) break; cursor = next;
            }
        }
        var ours = localRevision == chat.Revision ? localDigest.Cursors : (await store.GetDigestAsync(ct)).Cursors;
        foreach (var origin in ours)
        {
            var after = replayHistory ? 0 : remote.Cursors.FirstOrDefault(x => x.OriginNodeId == origin.OriginNodeId)?.LastOriginSequence ?? 0;
            while (after < origin.LastOriginSequence)
            {
                transferred = true;
                var batch = await store.GetEventRangeAsync(new(origin.OriginNodeId, after), ct);
                if (batch.Events.Length == 0) break;
                if (remote.SupportsGroupHistory) batch = await chat.AddGroupHistoryAsync(batch, peer.NodeId, ct);
                var ack = await transport.CallAsync<EventAck>(peer, "EventPush", batch, ct);
                if (ack.EventIds.Length != batch.Events.Length) break;
                after = batch.Events[batch.Events.Length - 1].OriginSequence;
                await store.RecordOutboxResultAsync(ack.EventIds, true, ct);
            }
        }
        if (remote.SupportsGroupHistory) _groupHistoryRevisions[peer.NodeId] = revision;
        return transferred;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastReconcile = DateTimeOffset.MinValue;
        var lastActiveReconcile = DateTimeOffset.MinValue;
        var activeInterval = ActiveReconciliationInterval;
        var outboxRevision = -1L;
        var nextOutboxRetry = long.MaxValue;
        runtime.LeaderReady += OnLeaderReady;
        try
        {
            await chat.RepairGroupHistoryAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!faults.PauseReplication)
                    {
                        Guid? preferred;
                        using var round = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        lock (_leaderGate)
                        {
                            preferred = _newLeader; _newLeader = null;
                            _normalRound = round;
                        }
                        try
                        {
                            var peerRevision = chat.PresenceRevision;
                            var contentRevision = chat.Revision;
                            var now = DateTimeOffset.UtcNow;
                            if (outboxRevision != contentRevision)
                            {
                                nextOutboxRetry = chat.NextOutboxRetryTicks(round.Token);
                                outboxRevision = contentRevision;
                            }
                            var fullRound = preferred.HasValue || peerRevision != _lastPeerRevision ||
                                nextOutboxRetry <= now.UtcDateTime.Ticks || now - lastReconcile >= options.ReconciliationInterval;
                            if (fullRound || now - lastActiveReconcile >= activeInterval)
                            {
                                // Keep pull available for one-way links, but poll a quiet
                                // healthy network less often. Pending sends stay on the fast path.
                                var quiet = await SynchronizeAsync(preferred, round.Token, activeOnly: !fullRound);
                                activeInterval = quiet ? QuietReconciliationInterval : ActiveReconciliationInterval;
                                lastActiveReconcile = DateTimeOffset.UtcNow;
                                if (fullRound)
                                {
                                    _lastPeerRevision = peerRevision;
                                    lastReconcile = lastActiveReconcile;
                                }
                            }
                        }
                        catch (OperationCanceledException) when (round.IsCancellationRequested && !stoppingToken.IsCancellationRequested) { }
                        finally { lock (_leaderGate) _normalRound = null; }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Replication cycle failed"); }
                await _wake.WaitAsync(TimeSpan.FromMilliseconds(750), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { runtime.LeaderReady -= OnLeaderReady; }
    }
}
