using System.Text.Json;
using LanChat.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed class ReplicationBackgroundService(IDistributedStore store, IClusterTransport transport, IConsensusService consensus,
    NodeOptions options, FaultInjection faults, ILogger<ReplicationBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    public async Task SynchronizeAsync(CancellationToken ct)
    {
        if (faults.PauseReplication || !await _sync.WaitAsync(0, ct)) return;
        try
        {
            var peers = await store.GetPeerEndpointsAsync(ct);
            var leader = (await consensus.GetStatusAsync(ct)).LeaderNodeId;
            var pending = await store.GetOutboxAsync(ct); var acknowledged = new HashSet<Guid>();
            var synced = 0;
            await Parallel.ForEachAsync(peers.OrderByDescending(x => x.NodeId == leader), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (peer, token) =>
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
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or JsonException or System.Security.Authentication.AuthenticationException)
                { logger.LogDebug("Replication peer unavailable {Node}: {Type}", peer.NodeId, ex.GetType().Name); }
            });
            if (acknowledged.Count > 0) await store.RecordOutboxResultAsync(acknowledged.ToArray(), true, ct);
            var failed = pending.Select(x => x.Id).Except(acknowledged).ToArray();
            if (failed.Length > 0) await store.RecordOutboxResultAsync(failed, false, ct);
            if (synced > 0) faults.LastSync = DateTimeOffset.UtcNow;
        }
        finally { _sync.Release(); }
    }
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
                after = batch.Events[^1].OriginSequence;
                await store.RecordOutboxResultAsync(ack.EventIds, true, ct);
            }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastReconcile = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!faults.PauseReplication && ((await store.GetOutboxAsync(stoppingToken)).Length > 0 || DateTimeOffset.UtcNow - lastReconcile >= options.ReconciliationInterval))
                    { await SynchronizeAsync(stoppingToken); lastReconcile = DateTimeOffset.UtcNow; }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Replication cycle failed"); }
                await Task.Delay(750, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
