using System.Diagnostics;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed partial class ConsensusService(ChatStore store, ChatDatabase database, IClusterTransport transport,
    NodeOptions options, FaultInjection faults, NodeRuntimeState runtime, PeerDiscoveryBackgroundService discovery, ILogger<ConsensusService> logger) : BackgroundService, IConsensusService
{
    private static class Jitter
    {
        private static readonly Random Random = new();
        public static int Next(int min, int max) { lock (Random) return Random.Next(min, max); }
    }
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
    private long _electedAt;
    private bool _remoteReady;
    private readonly Stopwatch _heartbeatSchedule = Stopwatch.StartNew();
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
            var set = new HashSet<Guid>(voters);
            return Old.Count(set.Contains) > Old.Length / 2 && (New is null || New.Count(set.Contains) > New.Length / 2);
        }
    }
    private Membership Configuration(LogEntryDto[] log)
    {
        var config = log.LastOrDefault(x => x.Command.Type is ControlCommandType.JointMembership or ControlCommandType.FinalMembership)?.Command;
        return config is null ? new([options.BootstrapNodeId], null) : config.Type == ControlCommandType.JointMembership
            ? new(config.OldVoters!, config.NewVoters!) : new(config.NewVoters!, null);
    }
    private Task<Snapshot> ReadAsync(CancellationToken ct)
    {
        using var db = database.Open(ct);
        var state = db.Required<ClusterState>();
        var log = db.Rows<ControlLogEntry>("1 ORDER BY \"Index\"");
        return Task.FromResult(new Snapshot(state, log.Select(x => new LogEntryDto(x.Id, x.Index, x.Term, JsonSerializer.Deserialize<ControlCommand>(x.CommandJson)!)).ToArray()));
    }
    private void Contact()
    { Interlocked.Exchange(ref _lastContact, Stopwatch.GetTimestamp()); _lastHeartbeatTicks = DateTime.UtcNow.Ticks; }
    public async Task<ConsensusStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAsync(ct); var config = Configuration(state.Log);
            var quorum = CanPropose(state);
            var ready = quorum || (_role == NodeRole.Follower && _remoteReady && FreshLeader(state));
            return new(_role, state.State.CurrentTerm, state.State.CurrentLeaderNodeId, quorum, config.Old, config.New,
                state.State.CommitIndex, _lastHeartbeatTicks, quorum ? "Máy này đang điều phối" : ready ? "Đã kết nối mạng chat" :
                _role == NodeRole.Candidate ? "Đang bầu máy trung tâm…" : "Đang chờ máy trung tâm · tin nhắn vẫn được lưu", runtime.CanLead, ready);
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
            if (!config.All.Contains(sourceNodeId) || !config.All.Contains((await store.GetIdentityAsync(ct)).NodeId)) return new(snapshot.State.CurrentTerm, false);
            var authorizedTransfer = request.Term == snapshot.State.CurrentTerm + 1 && request.TransferFrom.HasValue &&
                request.TransferFrom == snapshot.State.CurrentLeaderNodeId && request.TransferFrom != sourceNodeId;
            if (!authorizedTransfer && (FreshLeader(snapshot) || CanPropose(snapshot))) return new(snapshot.State.CurrentTerm, false);
            return await store.WriteAsync(async db =>
            {
                var state = db.Required<ClusterState>();
                if (request.Term > state.CurrentTerm)
                { state.CurrentTerm = request.Term; state.VotedForNodeId = null; state.CurrentLeaderNodeId = null; _role = NodeRole.Follower; _lastQuorum = 0; _remoteReady = false; runtime.SetReadyLeader(null, request.Term); }
                var upToDate = UpToDate(request, snapshot);
                var granted = request.Term == state.CurrentTerm && upToDate && (state.VotedForNodeId is null || state.VotedForNodeId == sourceNodeId);
                if (granted) state.VotedForNodeId = sourceNodeId;
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
            var response = await store.WriteAsync(async db =>
            {
                var state = db.Required<ClusterState>();
                if (request.Term < state.CurrentTerm) return new AppendResponse(state.CurrentTerm, false, db.Scalar("SELECT COALESCE(MAX(\"Index\"),0) FROM ControlLog"));
                if (request.Term > state.CurrentTerm) { state.CurrentTerm = request.Term; state.VotedForNodeId = null; }
                state.CurrentLeaderNodeId = sourceNodeId;
                var previous = request.PreviousIndex == 0 ? null : db.One<ControlLogEntry>("\"Index\"=$0", request.PreviousIndex);
                if (request.PreviousIndex > 0 && (previous is null || previous.Term != request.PreviousTerm))
                    return new AppendResponse(state.CurrentTerm, false, Math.Min(request.PreviousIndex - 1, db.Scalar("SELECT COALESCE(MAX(\"Index\"),0) FROM ControlLog")));
                var expected = request.PreviousIndex + 1;
                foreach (var entry in request.Entries)
                {
                    if (entry.Index != expected++ || entry.Term > request.Term || entry.Id == Guid.Empty) throw new InvalidDataException("Nhật ký không liên tục.");
                    var existing = db.One<ControlLogEntry>("\"Index\"=$0", entry.Index);
                    var json = JsonSerializer.Serialize(entry.Command);
                    if (existing is not null && (existing.Term != entry.Term || existing.Id != entry.Id || JsonSerializer.Serialize(JsonSerializer.Deserialize<ControlCommand>(existing.CommandJson)) != json))
                    {
                        if (entry.Index <= state.CommitIndex) throw new InvalidDataException("Không được ghi đè nhật ký đã commit.");
                        db.Delete<ControlLogEntry>("\"Index\">=$0", entry.Index); existing = null;
                    }
                    if (existing is null)
                    { db.Insert(new ControlLogEntry { Id = entry.Id, Index = entry.Index, Term = entry.Term, CommandJson = json }); db.SaveChanges(); }
                }
                var match = request.PreviousIndex + request.Entries.Length;
                state.CommitIndex = Math.Max(state.CommitIndex, Math.Min(request.LeaderCommit, match));
                await ApplyCommittedAsync(db, state, ct);
                return new AppendResponse(state.CurrentTerm, true, match);
            }, ct);
            if (response.Term == request.Term)
            {
                _role = NodeRole.Follower; _lastQuorum = 0; Contact();
                _remoteReady = response.Success && request.LeaderReady && CommittedInTerm(await ReadAsync(ct));
                runtime.SetReadyLeader(_remoteReady ? sourceNodeId : (Guid?)null, request.Term);
            }
            return response;
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
                var state = db.Required<ClusterState>();
                if (term <= state.CurrentTerm) return false;
                state.CurrentTerm = term; state.VotedForNodeId = null; state.CurrentLeaderNodeId = null;
                _role = NodeRole.Follower; _lastQuorum = 0; _remoteReady = false; runtime.SetReadyLeader(null, term); return true;
            }, ct);
        }
        finally { _gate.Release(); }
    }
    private async Task ElectAsync(CancellationToken ct)
    {
        if (!await _election.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            Snapshot snapshot;
            Membership config;
            (Guid Source, long Term)? handoff;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                snapshot = await ReadAsync(ct);
                config = Configuration(snapshot.Log);
                handoff = _handoff;
                _handoff = null;
                Volatile.Write(ref _handoffPending, 0);
                if (handoff?.Term != snapshot.State.CurrentTerm || handoff?.Source != snapshot.State.CurrentLeaderNodeId) handoff = null;
                if (_role == NodeRole.Leader || !runtime.CanLead || !config.All.Contains(_nodeId) || (handoff is null && FreshLeader(snapshot))) return;
            }
            finally { _gate.Release(); }

            var endpoints = (await store.GetPeerEndpointsAsync(ct)).Where(p => config.All.Contains(p.NodeId)).ToArray();
            var request = new VoteRequest(snapshot.State.CurrentTerm + 1, _nodeId, snapshot.LastIndex, snapshot.LastTerm, handoff?.Source);
            if (handoff is null)
            {
                logger.LogInformation("PreVote started for term {Term} on {Node}", request.Term, _nodeId);
                var preliminary = await RequestVotesAsync(endpoints, "PreVote", request, ct);
                var higher = preliminary.Select(v => v.Response?.Term ?? 0).DefaultIfEmpty().Max();
                if (higher > snapshot.State.CurrentTerm) { await StepDownAsync(higher, ct); return; }
                if (!config.Quorum(preliminary.Where(x => x.Response?.Granted == true).Select(x => x.NodeId).Append(_nodeId))) return;
                if (Interlocked.Exchange(ref _campaignRequested, 0) == 0 && endpoints.Length > 0)
                    await Task.Delay(Jitter.Next(25, 300), ct).ConfigureAwait(false);
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var latest = await ReadAsync(ct);
                if (!runtime.CanLead || _role == NodeRole.Leader || latest.State.CurrentTerm != snapshot.State.CurrentTerm ||
                    latest.LastIndex != snapshot.LastIndex || latest.LastTerm != snapshot.LastTerm ||
                    (handoff is null && FreshLeader(latest))) return;
                await store.WriteAsync(db =>
                {
                    var state = db.Required<ClusterState>();
                    state.CurrentTerm = request.Term; state.VotedForNodeId = _nodeId; state.CurrentLeaderNodeId = null;
                    return Task.FromResult(true);
                }, ct).ConfigureAwait(false);
                _role = NodeRole.Candidate; _remoteReady = false;
                runtime.SetReadyLeader(null, request.Term);
            }
            finally { _gate.Release(); }

            var responses = await RequestVotesAsync(endpoints, "Vote", request, ct);
            var observedTerm = responses.Select(v => v.Response?.Term ?? 0).DefaultIfEmpty().Max();
            if (observedTerm > request.Term) { await StepDownAsync(observedTerm, ct); return; }
            var votes = responses.Where(x => x.Response?.Granted == true && x.Response.Term == request.Term).Select(x => x.NodeId).Append(_nodeId);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var latest = await ReadAsync(ct);
                if (!runtime.CanLead || _role != NodeRole.Candidate || latest.State.CurrentTerm != request.Term || !config.Quorum(votes)) return;
                await store.WriteAsync(db =>
                {
                    db.Required<ClusterState>().CurrentLeaderNodeId = _nodeId;
                    var id = AppIdentity.NewId();
                    db.Insert(new ControlLogEntry { Id = id, Index = latest.LastIndex + 1, Term = request.Term,
                        CommandJson = JsonSerializer.Serialize(new ControlCommand(id, ControlCommandType.NoOp, Guid.Empty, Guid.Empty)) });
                    return Task.FromResult(true);
                }, ct).ConfigureAwait(false);
                _role = NodeRole.Leader; _leaderTerm = request.Term; _electedAt = Stopwatch.GetTimestamp(); _lastQuorum = 0; _next.Clear(); _match.Clear();
                _heartbeatSchedule.Restart();
                logger.LogInformation("Leader elected: {Node}, term {Term}", _nodeId, request.Term);
            }
            finally { _gate.Release(); }
            await ReplicateRoundAsync(ct).ConfigureAwait(false);
            _heartbeatSchedule.Restart();
        }
        finally { _election.Release(); }
    }

    private Task<(Guid NodeId, VoteResponse? Response)[]> RequestVotesAsync(PeerEndpoint[] endpoints, string message, VoteRequest request, CancellationToken ct) =>
        Task.WhenAll(endpoints.Select(async peer =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(650);
            try { return (peer.NodeId, (VoteResponse?)await transport.CallAsync<VoteResponse>(peer, message, request, timeout.Token).ConfigureAwait(false)); }
            catch (Exception ex) when (IsNetworkError(ex)) { return (peer.NodeId, (VoteResponse?)null); }
        }));

    private async Task ReplicateRoundAsync(CancellationToken ct)
    {
        if (!await _round.WaitAsync(0, ct)) return;
        try
        {
            Snapshot snapshot; Dictionary<Guid, long> nextIndexes; bool ready;
            await _gate.WaitAsync(ct);
            try { snapshot = await ReadAsync(ct); if (_role != NodeRole.Leader || faults.StopHeartbeat || faults.SimulateLeaderFailure) return; nextIndexes = new(_next); ready = CanPropose(snapshot); }
            finally { _gate.Release(); }
            var peers = await store.GetPeerEndpointsAsync(ct);
            var replies = await Task.WhenAll(peers.Select(async peer =>
            {
                // Each request is independent; short RPC deadline avoids a dead voter blocking heartbeats for the others.
                var next = nextIndexes.GetValueOrDefault(peer.NodeId, Math.Max(1, snapshot.LastIndex));
                var previous = snapshot.Log.FirstOrDefault(x => x.Index == next - 1);
                var entries = new List<LogEntryDto>(); var bytes = 0;
                foreach (var entry in snapshot.Log.Where(x => x.Index >= next).Take(100))
                { var length = JsonSerializer.SerializeToUtf8Bytes(entry).Length; if (entries.Count > 0 && bytes + length > 500000) break; entries.Add(entry); bytes += length; }
                var request = new AppendRequest(snapshot.State.CurrentTerm, _nodeId, next - 1, previous?.Term ?? 0,
                    entries.ToArray(), snapshot.State.CommitIndex, ready);
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
                    else _next[response.Node] = Math.Max(1, Math.Min(nextIndexes.GetValueOrDefault(response.Node, Math.Max(1, snapshot.LastIndex)) - 1, response.Response.MatchIndex + 1));
                }
                var config = Configuration(latest.Log);
                if (config.Quorum(responses.Where(x => x.Response.Success && x.Response.Term == latest.State.CurrentTerm).Select(x => x.Node).Append(_nodeId)))
                { _lastQuorum = Stopwatch.GetTimestamp(); Contact(); }
                var commit = latest.Log.Where(e => e.Index > latest.State.CommitIndex && e.Term == latest.State.CurrentTerm &&
                    config.Quorum(_match.Where(x => x.Value >= e.Index).Select(x => x.Key))).Select(x => x.Index).DefaultIfEmpty(latest.State.CommitIndex).Max();
                if (commit > latest.State.CommitIndex)
                    await store.WriteAsync(async db => { var s = db.Required<ClusterState>(); s.CommitIndex = commit; await ApplyCommittedAsync(db, s, ct); return true; }, ct);
                latest = await ReadAsync(ct);
                runtime.SetReadyLeader(CanPropose(latest) ? _nodeId : (Guid?)null, latest.State.CurrentTerm);
                if (_transferring || !CanPropose(latest) || latest.LastIndex != latest.State.CommitIndex) return;
                if (config.New is not null)
                    await AppendLocalAsync(new(LanChat.Domain.AppIdentity.NewId(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: config.New), latest, ct);
                else
                {
                    var learner = peers.FirstOrDefault(x => !config.All.Contains(x.NodeId) && _match.GetValueOrDefault(x.NodeId) >= latest.LastIndex &&
                        responses.Any(r => r.Node == x.NodeId && r.Response.Success));
                    if (learner is not null)
                        await AppendLocalAsync(new(LanChat.Domain.AppIdentity.NewId(), ControlCommandType.JointMembership, Guid.Empty, Guid.Empty,
                            OldVoters: config.Old, NewVoters: config.Old.Append(learner.NodeId).ToArray()), latest, ct);
                }
            }
            finally { _gate.Release(); }
        }
        finally { _round.Release(); }
    }
    private async Task AppendLocalAsync(ControlCommand command, Snapshot snapshot, CancellationToken ct) => await store.WriteAsync(db =>
    {
        if (command.Type >= ControlCommandType.GroupCreated)
            store.VerifyGroupCommand(db, command, db.One<Group>("Id=$0", command.GroupId));
        db.Insert(new ControlLogEntry { Id = command.Id, Index = snapshot.LastIndex + 1, Term = snapshot.State.CurrentTerm, CommandJson = JsonSerializer.Serialize(command) });
        return Task.FromResult(true);
    }, ct);
    public async Task<ControlResult> ProposeAsync(ControlCommand command, CancellationToken ct = default)
    {
        var local = await store.GetIdentityAsync(ct);
        if (command.ActorUserId != local.UserId) return new(false, "Sai người thực hiện thao tác.");
        var status = await GetStatusAsync(ct);
        if (!status.LeaderReady) return new(false, "Máy trung tâm chưa sẵn sàng. Tin nhắn vẫn được đồng bộ.");
        command = store.SignGroupCommand(command, local.NodeId, ct);
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
        using var db = database.Open(ct);
        if (command.OriginNodeId != sourceNode || !db.Exists<PeerNode>("NodeId=$0 AND UserId=$1", sourceNode, command.ActorUserId)) return new(false, "Không xác thực được người yêu cầu.");
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
                if (!CanPropose(snapshot)) return new(false, "Máy trung tâm đang thay đổi.");
                var existing = snapshot.Log.FirstOrDefault(x => x.Command.Id == command.Id);
                if (existing is not null) return new(existing.Index <= snapshot.State.CommitIndex, existing.Index <= snapshot.State.CommitIndex ? null : "Thao tác đang chờ xác nhận.", existing.Index);
                if (snapshot.LastIndex != snapshot.State.CommitIndex) return new(false, "Đang đồng bộ cấu trúc mạng chat. Vui lòng thử lại sau vài giây.");
                var error = await ValidateGroupCommandAsync(command, ct);
                if (error is not null) return new(false, error);
                index = snapshot.LastIndex + 1; await AppendLocalAsync(command, snapshot, ct);
            }
            finally { _gate.Release(); }
            var deadline = Stopwatch.GetTimestamp();
            while (FrameworkIO.Elapsed(deadline) < TimeSpan.FromSeconds(5))
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
    private static bool IsNetworkError(Exception ex) => ex is ObjectDisposedException or IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or JsonException or System.Security.Authentication.AuthenticationException;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _nodeId = (await store.GetIdentityAsync(stoppingToken)).NodeId;
        if (options.BootstrapNodeId == Guid.Empty) { logger.LogWarning("Consensus disabled until cluster upgrade selects bootstrap identity"); return; }
        var preference = Path.Combine(options.DataDirectory, "leadership.json");
        try { if (File.Exists(preference)) runtime.CanLead = JsonSerializer.Deserialize<bool>(File.ReadAllText(preference)); }
        catch (Exception ex) when (ex is IOException or JsonException)
        { logger.LogWarning("Invalid local leadership preference; automatic candidacy disabled"); runtime.CanLead = false; }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!faults.SimulateLeaderFailure)
                {
                    if (_role == NodeRole.Leader && _heartbeatSchedule.Elapsed >= options.HeartbeatInterval)
                    {
                        _heartbeatSchedule.Restart();
                        await ReplicateRoundAsync(stoppingToken).ConfigureAwait(false);
                        await RetireIfQuorumLostAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else if (_role != NodeRole.Leader && runtime.CanLead)
                    {
                        var elapsed = FrameworkIO.Elapsed(Interlocked.Read(ref _lastContact));
                        var bootstrap = _nodeId == options.BootstrapNodeId && elapsed.TotalMilliseconds >= 150 &&
                            _lastElectionAttempt == 0 && (await ReadAsync(stoppingToken)).LastIndex == 0;
                        var retryDue = _lastElectionAttempt == 0 || FrameworkIO.Elapsed(_lastElectionAttempt) >= TimeSpan.FromMilliseconds(700);
                        if (bootstrap || Volatile.Read(ref _handoffPending) != 0 || (retryDue && elapsed >= options.LeaderTimeout))
                        {
                            _lastElectionAttempt = Stopwatch.GetTimestamp();
                            await ElectAsync(stoppingToken).ConfigureAwait(false);
                        }
                    }
                }
                await Task.Delay(50, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogCritical(ex, "Consensus service stopped unexpectedly"); throw; }
    }
}
