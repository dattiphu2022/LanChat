using System.Text.Json;
using LanChat.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed class ReplicationBackgroundService(IDistributedStore store, IChatStore chat, IClusterTransport transport, IConsensusService consensus,
    NodeOptions options, FaultInjection faults, NodeRuntimeState runtime, ILogger<ReplicationBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _leaderGate = new();
    private Guid? _newLeader;
    private CancellationTokenSource? _normalRound;
    private long _lastPeerRevision;
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
    private async Task SynchronizeAsync(Guid? preferred, CancellationToken ct)
    {
        if (faults.PauseReplication) return;
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var peers = await store.GetPeerEndpointsAsync(ct);
            var leader = preferred ?? (await consensus.GetStatusAsync(ct)).LeaderNodeId;
            if (preferred.HasValue && peers.FirstOrDefault(p => p.NodeId == preferred) is { } center)
            {
                logger.LogInformation("Immediate sync started with leader {Node}", center.NodeId);
                try { await ReconcilePeerAsync(center, ct).ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested && NetworkFailure(ex))
                { logger.LogDebug("Immediate sync with leader deferred: {Error}", ex.GetType().Name); }
            }
            var pending = await store.GetOutboxAsync(ct); var acknowledged = new HashSet<Guid>();
            var synced = 0;
            await FrameworkIO.ForEachAsync(peers.OrderByDescending(x => x.NodeId == leader), 4, ct, async (peer, token) =>
            {
                try
                {
                    if (pending.Length > 0)
                    {
                        var ack = await transport.CallAsync<EventAck>(peer, "EventPush", new EventBatch(pending), ct);
                        lock (acknowledged) foreach (var id in ack.EventIds) if (pending.Any(x => x.Id == id)) acknowledged.Add(id);
                    }
                    await ReconcilePeerAsync(peer, ct);
                    Interlocked.Increment(ref synced);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (NetworkFailure(ex))
                { logger.LogDebug("Replication peer unavailable {Node}: {Type}", peer.NodeId, ex.GetType().Name); }
            });
            if (acknowledged.Count > 0) await store.RecordOutboxResultAsync(acknowledged.ToArray(), true, ct);
            var failed = pending.Select(x => x.Id).Except(acknowledged).ToArray();
            if (failed.Length > 0) await store.RecordOutboxResultAsync(failed, false, ct);
            if (synced > 0) faults.LastSync = DateTimeOffset.UtcNow;
        }
        finally { _sync.Release(); }
    }
    private static bool NetworkFailure(Exception ex) => ex is ObjectDisposedException or IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or JsonException or System.Security.Authentication.AuthenticationException;
    public async Task ReconcilePeerAsync(PeerEndpoint peer, CancellationToken ct)
    {
        var remote = await transport.CallAsync<ReplicationDigest>(peer, "Digest", new { }, ct);
        var local = (await store.GetDigestAsync(ct)).Cursors.ToDictionary(x => x.OriginNodeId, x => x.LastOriginSequence);
        foreach (var origin in remote.Cursors)
        {
            var cursor = local.GetValueOrDefault(origin.OriginNodeId);
            while (cursor < origin.LastOriginSequence)
            {
                var batch = await transport.CallAsync<EventBatch>(peer, "EventRange", new EventRangeRequest(origin.OriginNodeId, cursor), ct);
                if (batch.Events.Length == 0) break;
                await store.ImportEventsAsync(batch, ct);
                var next = (await store.GetDigestAsync(ct)).Cursors.FirstOrDefault(x => x.OriginNodeId == origin.OriginNodeId)?.LastOriginSequence ?? 0;
                if (next <= cursor) break; cursor = next;
            }
        }
        var ours = (await store.GetDigestAsync(ct)).Cursors;
        foreach (var origin in ours)
        {
            var after = remote.Cursors.FirstOrDefault(x => x.OriginNodeId == origin.OriginNodeId)?.LastOriginSequence ?? 0;
            while (after < origin.LastOriginSequence)
            {
                var batch = await store.GetEventRangeAsync(new(origin.OriginNodeId, after), ct);
                if (batch.Events.Length == 0) break;
                var ack = await transport.CallAsync<EventAck>(peer, "EventPush", batch, ct);
                if (ack.EventIds.Length != batch.Events.Length) break;
                after = batch.Events[batch.Events.Length - 1].OriginSequence;
                await store.RecordOutboxResultAsync(ack.EventIds, true, ct);
            }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastReconcile = DateTimeOffset.MinValue;
        runtime.LeaderReady += OnLeaderReady;
        try
        {
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
                            if (preferred.HasValue || peerRevision != _lastPeerRevision || (await store.GetOutboxAsync(round.Token)).Length > 0 || DateTimeOffset.UtcNow - lastReconcile >= options.ReconciliationInterval)
                            {
                                await SynchronizeAsync(preferred, round.Token);
                                _lastPeerRevision = peerRevision;
                                lastReconcile = DateTimeOffset.UtcNow;
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
