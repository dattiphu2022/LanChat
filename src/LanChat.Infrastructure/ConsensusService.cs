using System.Diagnostics;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed partial class ConsensusService(ChatStore store, IDbContextFactory<ChatDbContext> factory, IClusterTransport transport,
    NodeOptions options, FaultInjection faults, NodeRuntimeState runtime, PeerDiscoveryBackgroundService discovery, ILogger<ConsensusService> logger) : BackgroundService, IConsensusService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly SemaphoreSlim _proposal = new(1, 1);
    private readonly Dictionary<Guid, long> _next = [];
    private readonly Dictionary<Guid, long> _match = [];
    private NodeRole _role { get => runtime.Role; set => runtime.Role = value; }
    private long _lastContact = Stopwatch.GetTimestamp();
    private long _lastQuorum;
    private long _lastHeartbeatTicks;
    private long _leaderTerm;
    private int _electionJitter = Random.Shared.Next(500, 1501);
    private Guid _nodeId;

    private sealed record Snapshot(ClusterState State, LogEntryDto[] Log)
    {
        public long LastIndex => Log.LastOrDefault()?.Index ?? 0;
        public long LastTerm => Log.LastOrDefault()?.Term ?? 0;
    }
    private sealed record Membership(Guid[] Old, Guid[]? New)
    {
        public Guid[] All => Old.Concat(New ?? []).Distinct().ToArray();
        public bool Quorum(IEnumerable<Guid> voters)
        {
            var set = voters.ToHashSet();
            return Old.Count(set.Contains) > Old.Length / 2 && (New is null || New.Count(set.Contains) > New.Length / 2);
        }
    }
    private Membership Configuration(LogEntryDto[] log)
    {
        var config = log.LastOrDefault(x => x.Command.Type is ControlCommandType.JointMembership or ControlCommandType.FinalMembership)?.Command;
        return config is null ? new([options.BootstrapNodeId], null) : config.Type == ControlCommandType.JointMembership
            ? new(config.OldVoters!, config.NewVoters!) : new(config.NewVoters!, null);
    }
    private async Task<Snapshot> ReadAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var state = await db.Cluster.AsNoTracking().SingleAsync(ct);
        var log = await db.ControlLog.AsNoTracking().OrderBy(x => x.Index).ToArrayAsync(ct);
        return new(state, log.Select(x => new LogEntryDto(x.Id, x.Index, x.Term, JsonSerializer.Deserialize<ControlCommand>(x.CommandJson)!)).ToArray());
    }
    private void Contact()
    { _lastContact = Stopwatch.GetTimestamp(); _lastHeartbeatTicks = DateTime.UtcNow.Ticks; _electionJitter = Random.Shared.Next(500, 1501); }
    public async Task<ConsensusStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAsync(ct); var config = Configuration(state.Log);
            var quorum = _role == NodeRole.Leader && _leaderTerm == state.State.CurrentTerm && _lastQuorum != 0 &&
                Stopwatch.GetElapsedTime(_lastQuorum) < options.HeartbeatInterval * 3 && !faults.StopHeartbeat && !faults.SimulateLeaderFailure &&
                state.Log.Any(x => x.Index <= state.State.CommitIndex && x.Term == state.State.CurrentTerm);
            return new(_role, state.State.CurrentTerm, state.State.CurrentLeaderNodeId, quorum, config.Old, config.New,
                state.State.CommitIndex, _lastHeartbeatTicks, quorum ? "Leader · kết nối đầy đủ" : _role == NodeRole.Candidate ? "Đang kết nối lại…" :
                state.State.CurrentLeaderNodeId.HasValue && Stopwatch.GetElapsedTime(_lastContact) < options.LeaderTimeout ? "Đã kết nối mạng chat" : "Kết nối hạn chế · chat vẫn được lưu");
        }
        finally { _gate.Release(); }
    }
    public async Task<VoteResponse> VoteAsync(VoteRequest request, Guid sourceNodeId, CancellationToken ct)
    {
        if (sourceNodeId != request.CandidateNodeId || faults.SimulateLeaderFailure) throw new InvalidDataException("VoteRequest không hợp lệ.");
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await ReadAsync(ct); var config = Configuration(snapshot.Log);
            if (!config.All.Contains(sourceNodeId)) return new(snapshot.State.CurrentTerm, false);
            return await store.WriteAsync(async db =>
            {
                var state = await db.Cluster.SingleAsync(ct);
                if (request.Term > state.CurrentTerm)
                { state.CurrentTerm = request.Term; state.VotedForNodeId = null; state.CurrentLeaderNodeId = null; _role = NodeRole.Follower; _lastQuorum = 0; }
                var upToDate = request.LastLogTerm > snapshot.LastTerm || request.LastLogTerm == snapshot.LastTerm && request.LastLogIndex >= snapshot.LastIndex;
                var granted = request.Term == state.CurrentTerm && upToDate && (state.VotedForNodeId is null || state.VotedForNodeId == sourceNodeId);
                if (granted) { state.VotedForNodeId = sourceNodeId; Contact(); }
                return new VoteResponse(state.CurrentTerm, granted);
            }, ct);
        }
        finally { _gate.Release(); }
    }
    public async Task<AppendResponse> AppendAsync(AppendRequest request, Guid sourceNodeId, CancellationToken ct)
    {
        if (sourceNodeId != request.LeaderNodeId || request.Entries.Length > 100 || faults.SimulateLeaderFailure) throw new InvalidDataException("AppendRequest không hợp lệ.");
        await _gate.WaitAsync(ct);
        try
        {
            return await store.WriteAsync(async db =>
            {
                var state = await db.Cluster.SingleAsync(ct);
                if (request.Term < state.CurrentTerm) return new AppendResponse(state.CurrentTerm, false, await db.ControlLog.MaxAsync(x => (long?)x.Index, ct) ?? 0);
                if (request.Term > state.CurrentTerm) { state.CurrentTerm = request.Term; state.VotedForNodeId = null; }
                state.CurrentLeaderNodeId = sourceNodeId; _role = NodeRole.Follower; _lastQuorum = 0; Contact();
                var previous = request.PreviousIndex == 0 ? null : await db.ControlLog.SingleOrDefaultAsync(x => x.Index == request.PreviousIndex, ct);
                if (request.PreviousIndex > 0 && (previous is null || previous.Term != request.PreviousTerm))
                    return new AppendResponse(state.CurrentTerm, false, Math.Min(request.PreviousIndex - 1, await db.ControlLog.MaxAsync(x => (long?)x.Index, ct) ?? 0));
                var expected = request.PreviousIndex + 1;
                foreach (var entry in request.Entries)
                {
                    if (entry.Index != expected++ || entry.Term > request.Term || entry.Id == Guid.Empty) throw new InvalidDataException("Nhật ký không liên tục.");
                    var existing = await db.ControlLog.SingleOrDefaultAsync(x => x.Index == entry.Index, ct);
                    var json = JsonSerializer.Serialize(entry.Command);
                    if (existing is not null && (existing.Term != entry.Term || existing.Id != entry.Id || existing.CommandJson != json))
                    {
                        if (entry.Index <= state.CommitIndex) throw new InvalidDataException("Không được ghi đè nhật ký đã commit.");
                        await db.ControlLog.Where(x => x.Index >= entry.Index).ExecuteDeleteAsync(ct); existing = null;
                    }
                    if (existing is null)
                    { db.ControlLog.Add(new ControlLogEntry { Id = entry.Id, Index = entry.Index, Term = entry.Term, CommandJson = json }); await db.SaveChangesAsync(ct); }
                }
                var match = request.PreviousIndex + request.Entries.Length;
                state.CommitIndex = Math.Max(state.CommitIndex, Math.Min(request.LeaderCommit, match));
                await ApplyCommittedAsync(db, state, ct);
                return new AppendResponse(state.CurrentTerm, true, match);
            }, ct);
        }
        finally { _gate.Release(); }
    }
    private async Task StepDownAsync(long term, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await store.WriteAsync(async db =>
            {
                var state = await db.Cluster.SingleAsync(ct);
                if (term > state.CurrentTerm) { state.CurrentTerm = term; state.VotedForNodeId = null; state.CurrentLeaderNodeId = null; }
                _role = NodeRole.Follower; _lastQuorum = 0; Contact(); return true;
            }, ct);
        }
        finally { _gate.Release(); }
    }
    private async Task ElectAsync(CancellationToken ct)
    {
        Snapshot snapshot;
        await _gate.WaitAsync(ct);
        try
        {
            snapshot = await ReadAsync(ct);
            if (!Configuration(snapshot.Log).All.Contains(_nodeId)) { Contact(); return; }
            await store.WriteAsync(async db =>
            {
                var state = await db.Cluster.SingleAsync(ct); state.CurrentTerm++; state.VotedForNodeId = _nodeId; state.CurrentLeaderNodeId = null;
                _role = NodeRole.Candidate; Contact(); return true;
            }, ct);
            snapshot = await ReadAsync(ct);
        }
        finally { _gate.Release(); }
        logger.LogInformation("Election term {Term} on {Node}", snapshot.State.CurrentTerm, _nodeId);
        var votes = new List<Guid> { _nodeId }; var configuration = Configuration(snapshot.Log);
        var endpoints = await store.GetPeerEndpointsAsync(ct);
        var responses = await Task.WhenAll(endpoints.Where(p => configuration.All.Contains(p.NodeId)).Select(async peer =>
        {
            try { return (peer.NodeId, Response: await transport.CallAsync<VoteResponse>(peer, "Vote", new VoteRequest(snapshot.State.CurrentTerm, _nodeId, snapshot.LastIndex, snapshot.LastTerm), ct)); }
            catch (Exception ex) when (IsNetworkError(ex)) { return (peer.NodeId, Response: (VoteResponse?)null); }
        }));
        var higher = responses.Where(x => x.Response is not null).Select(x => x.Response!.Term).DefaultIfEmpty(0).Max();
        if (higher > snapshot.State.CurrentTerm) { await StepDownAsync(higher, ct); return; }
        votes.AddRange(responses.Where(x => x.Response?.Granted == true && x.Response.Term == snapshot.State.CurrentTerm).Select(x => x.NodeId));
        await _gate.WaitAsync(ct);
        try
        {
            var latest = await ReadAsync(ct);
            if (_role != NodeRole.Candidate || latest.State.CurrentTerm != snapshot.State.CurrentTerm || !configuration.Quorum(votes)) return;
            await store.WriteAsync(async db =>
            {
                var state = await db.Cluster.SingleAsync(ct); state.CurrentLeaderNodeId = _nodeId;
                _role = NodeRole.Leader; _leaderTerm = state.CurrentTerm; _lastQuorum = 0; _next.Clear(); _match.Clear();
                db.ControlLog.Add(new ControlLogEntry { Id = Guid.CreateVersion7(), Index = latest.LastIndex + 1, Term = state.CurrentTerm,
                    CommandJson = JsonSerializer.Serialize(new ControlCommand(Guid.CreateVersion7(), ControlCommandType.NoOp, Guid.Empty, Guid.Empty)) }); return true;
            }, ct);
            logger.LogInformation("Leader elected: {Node}, term {Term}", _nodeId, snapshot.State.CurrentTerm);
        }
        finally { _gate.Release(); }
    }
    private async Task ReplicateRoundAsync(CancellationToken ct)
    {
        if (!await _round.WaitAsync(0, ct)) return;
        try
        {
            Snapshot snapshot; Dictionary<Guid, long> nextIndexes;
            await _gate.WaitAsync(ct);
            try { snapshot = await ReadAsync(ct); if (_role != NodeRole.Leader || faults.StopHeartbeat || faults.SimulateLeaderFailure) return; nextIndexes = new(_next); }
            finally { _gate.Release(); }
            var peers = await store.GetPeerEndpointsAsync(ct);
            var replies = await Task.WhenAll(peers.Select(async peer =>
            {
                // Each request is independent; short RPC deadline avoids a dead voter blocking heartbeats for the others.
                var next = nextIndexes.GetValueOrDefault(peer.NodeId, 1);
                var previous = snapshot.Log.FirstOrDefault(x => x.Index == next - 1);
                var entries = new List<LogEntryDto>(); var bytes = 0;
                foreach (var entry in snapshot.Log.Where(x => x.Index >= next).Take(100))
                { var length = JsonSerializer.SerializeToUtf8Bytes(entry).Length; if (entries.Count > 0 && bytes + length > 500000) break; entries.Add(entry); bytes += length; }
                var request = new AppendRequest(snapshot.State.CurrentTerm, _nodeId, next - 1, previous?.Term ?? 0,
                    entries.ToArray(), snapshot.State.CommitIndex);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(750);
                    var response = await transport.CallAsync<AppendResponse>(peer, "Append", request, timeout.Token);
                    return (Node: peer.NodeId, Response: (AppendResponse?)response);
                }
                catch (Exception ex) when (IsNetworkError(ex)) { return (Node: peer.NodeId, Response: (AppendResponse?)null); }
            }));
            var responses = replies.Where(x => x.Response is not null).Select(x => (x.Node, Response: x.Response!)).ToArray();
            if (responses.Any(x => x.Response.Term > snapshot.State.CurrentTerm))
            { await StepDownAsync(responses.Max(x => x.Response.Term), ct); return; }
            await _gate.WaitAsync(ct);
            try
            {
                var latest = await ReadAsync(ct);
                if (_role != NodeRole.Leader || latest.State.CurrentTerm != snapshot.State.CurrentTerm) return;
                _match[_nodeId] = latest.LastIndex;
                foreach (var response in responses.Where(x => x.Response.Term == latest.State.CurrentTerm))
                {
                    if (response.Response.Success) { _match[response.Node] = response.Response.MatchIndex; _next[response.Node] = response.Response.MatchIndex + 1; }
                    else _next[response.Node] = Math.Max(1, Math.Min(_next.GetValueOrDefault(response.Node, 1) - 1, response.Response.MatchIndex + 1));
                }
                var config = Configuration(latest.Log);
                if (config.Quorum(responses.Where(x => x.Response.Success && x.Response.Term == latest.State.CurrentTerm).Select(x => x.Node).Append(_nodeId)))
                { _lastQuorum = Stopwatch.GetTimestamp(); Contact(); }
                var commit = latest.Log.Where(e => e.Index > latest.State.CommitIndex && e.Term == latest.State.CurrentTerm &&
                    config.Quorum(_match.Where(x => x.Value >= e.Index).Select(x => x.Key))).Select(x => x.Index).DefaultIfEmpty(latest.State.CommitIndex).Max();
                if (commit > latest.State.CommitIndex)
                    await store.WriteAsync(async db => { var s = await db.Cluster.SingleAsync(ct); s.CommitIndex = commit; await ApplyCommittedAsync(db, s, ct); return true; }, ct);
                latest = await ReadAsync(ct);
                if (latest.LastIndex != latest.State.CommitIndex) return;
                if (config.New is not null)
                    await AppendLocalAsync(new(Guid.CreateVersion7(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: config.New), latest, ct);
                else
                {
                    var learner = peers.FirstOrDefault(x => !config.All.Contains(x.NodeId) && _match.GetValueOrDefault(x.NodeId) >= latest.LastIndex &&
                        responses.Any(r => r.Node == x.NodeId && r.Response.Success));
                    if (learner is not null)
                        await AppendLocalAsync(new(Guid.CreateVersion7(), ControlCommandType.JointMembership, Guid.Empty, Guid.Empty,
                            OldVoters: config.Old, NewVoters: config.Old.Append(learner.NodeId).ToArray()), latest, ct);
                }
            }
            finally { _gate.Release(); }
        }
        finally { _round.Release(); }
    }
    private async Task AppendLocalAsync(ControlCommand command, Snapshot snapshot, CancellationToken ct) => await store.WriteAsync(db =>
    {
        db.ControlLog.Add(new ControlLogEntry { Id = command.Id, Index = snapshot.LastIndex + 1, Term = snapshot.State.CurrentTerm, CommandJson = JsonSerializer.Serialize(command) });
        return Task.FromResult(true);
    }, ct);
    public async Task<ControlResult> ProposeAsync(ControlCommand command, CancellationToken ct = default)
    {
        var local = await store.GetIdentityAsync(ct);
        if (command.ActorUserId != local.UserId) return new(false, "Sai người thực hiện thao tác.");
        var status = await GetStatusAsync(ct);
        if (status.Role != NodeRole.Leader)
        {
            var peer = (await store.GetPeerEndpointsAsync(ct)).FirstOrDefault(x => x.NodeId == status.LeaderNodeId);
            if (peer is null) return new(false, "Không đủ số node để thực hiện thay đổi cấu trúc.");
            try { return await transport.CallAsync<ControlResult>(peer, "ControlProposal", command, ct); }
            catch (Exception ex) when (IsNetworkError(ex)) { return new(false, "Chưa xác nhận được thay đổi. Kiểm tra lại khi kết nối phục hồi."); }
        }
        return await ProposeLocalAsync(command, ct);
    }
    internal async Task<ControlResult> ProposeRemoteAsync(ControlCommand command, Guid sourceNode, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Peers.AnyAsync(x => x.NodeId == sourceNode && x.UserId == command.ActorUserId, ct)) return new(false, "Không xác thực được người yêu cầu.");
        return await ProposeLocalAsync(command, ct);
    }
    private async Task<ControlResult> ProposeLocalAsync(ControlCommand command, CancellationToken ct)
    {
        if (command.Type is ControlCommandType.NoOp or ControlCommandType.JointMembership or ControlCommandType.FinalMembership) return new(false, "Thao tác nội bộ không được gọi từ giao diện.");
        await _proposal.WaitAsync(ct);
        try
        {
            if (!(await GetStatusAsync(ct)).HasQuorum) return new(false, "Không đủ số node để thực hiện thay đổi cấu trúc.");
            long index;
            await _gate.WaitAsync(ct);
            try
            {
                var snapshot = await ReadAsync(ct);
                var existing = snapshot.Log.FirstOrDefault(x => x.Command.Id == command.Id);
                if (existing is not null) return new(existing.Index <= snapshot.State.CommitIndex, existing.Index <= snapshot.State.CommitIndex ? null : "Thao tác đang chờ xác nhận.", existing.Index);
                if (snapshot.LastIndex != snapshot.State.CommitIndex) return new(false, "Đang đồng bộ cấu trúc mạng chat. Vui lòng thử lại sau vài giây.");
                var error = await ValidateGroupCommandAsync(command, ct);
                if (error is not null) return new(false, error);
                command = command with { CreatedAtTicks = DateTime.UtcNow.Ticks };
                index = snapshot.LastIndex + 1; await AppendLocalAsync(command, snapshot, ct);
            }
            finally { _gate.Release(); }
            var deadline = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(5))
            {
                await ReplicateRoundAsync(ct); var state = await ReadAsync(ct);
                if (state.State.CommitIndex >= index && state.Log.Any(x => x.Index == index && x.Command.Id == command.Id))
                    return new(true, null, index);
                if (_role != NodeRole.Leader) break;
                await Task.Delay(100, ct);
            }
            return new(false, "Thay đổi chưa được xác nhận bởi quorum; kiểm tra lại danh sách sau khi kết nối phục hồi.", index);
        }
        finally { _proposal.Release(); }
    }
    private static bool IsNetworkError(Exception ex) => ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or JsonException or System.Security.Authentication.AuthenticationException;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _nodeId = (await store.GetIdentityAsync(stoppingToken)).NodeId;
        if (options.BootstrapNodeId == Guid.Empty) { logger.LogWarning("Consensus disabled until cluster upgrade selects bootstrap identity"); return; }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!faults.SimulateLeaderFailure)
                {
                    if (_role == NodeRole.Leader) await ReplicateRoundAsync(stoppingToken);
                    else if (Stopwatch.GetElapsedTime(_lastContact) > options.LeaderTimeout + TimeSpan.FromMilliseconds(_electionJitter) ||
                             _nodeId == options.BootstrapNodeId && (await ReadAsync(stoppingToken)).Log.Length == 0 && Stopwatch.GetElapsedTime(_lastContact).TotalMilliseconds > _electionJitter)
                    {
                        // Probe first; a successful NodeInfo response alone is not a leader heartbeat.
                        var snapshot = await ReadAsync(stoppingToken);
                        var leader = (await store.GetPeerEndpointsAsync(stoppingToken)).FirstOrDefault(x => x.NodeId == snapshot.State.CurrentLeaderNodeId);
                        if (leader is not null)
                        {
                            try
                            {
                                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); timeout.CancelAfter(600);
                                var probe = await transport.CallAsync<ConsensusStatus>(leader, "ConsensusStatus", new { }, timeout.Token);
                                if (probe.Role == NodeRole.Leader && probe.HasQuorum && probe.Term >= snapshot.State.CurrentTerm) { Contact(); continue; }
                            }
                            catch (Exception ex) when (IsNetworkError(ex)) { }
                        }
                        discovery.RequestDiscovery();
                        await Task.Delay(Random.Shared.Next(500, 1501), stoppingToken);
                        if (Stopwatch.GetElapsedTime(_lastContact) >= options.LeaderTimeout) await ElectAsync(stoppingToken);
                        else if (_nodeId == options.BootstrapNodeId && snapshot.Log.Length == 0) await ElectAsync(stoppingToken);
                    }
                }
                await Task.Delay(options.HeartbeatInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogCritical(ex, "Consensus service stopped unexpectedly"); throw; }
    }
}
