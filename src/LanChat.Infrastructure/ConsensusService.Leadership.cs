using System.Diagnostics;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;

namespace LanChat.Infrastructure;

public sealed partial class ConsensusService
{

    private bool CanPropose(Snapshot snapshot) => _role == NodeRole.Leader && runtime.CanLead && !_transferring &&
        _leaderTerm == snapshot.State.CurrentTerm && _lastQuorum != 0 &&
        FrameworkIO.Elapsed(_lastQuorum) < TimeSpan.FromTicks(options.HeartbeatInterval.Ticks * 3) &&
        !faults.StopHeartbeat && !faults.SimulateLeaderFailure && CommittedInTerm(snapshot);

    private static bool CommittedInTerm(Snapshot snapshot) =>
        snapshot.Log.Any(x => x.Index <= snapshot.State.CommitIndex && x.Term == snapshot.State.CurrentTerm);

    private bool FreshLeader(Snapshot snapshot) => snapshot.State.CurrentLeaderNodeId.HasValue &&
        _lastHeartbeatTicks != 0 && FrameworkIO.Elapsed(_lastContact) < options.LeaderTimeout;

    private static bool UpToDate(VoteRequest request, Snapshot snapshot) =>
        request.LastLogTerm > snapshot.LastTerm || request.LastLogTerm == snapshot.LastTerm && request.LastLogIndex >= snapshot.LastIndex;

    private readonly SemaphoreSlim _election = new(1, 1);
    private readonly SemaphoreSlim _preference = new(1, 1);
    private bool _transferring;
    private long _lastElectionAttempt;
    private int _campaignRequested;
    private (Guid Source, long Term)? _handoff;
    private int _handoffPending;

    internal async Task<VoteResponse> PreVoteAsync(VoteRequest request, Guid source, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await ReadAsync(ct);
            var config = Configuration(snapshot.Log);
            return new VoteResponse(snapshot.State.CurrentTerm, source == request.CandidateNodeId && !faults.SimulateLeaderFailure &&
                request.Term > snapshot.State.CurrentTerm && config.All.Contains(source) && config.All.Contains(_nodeId) &&
                !FreshLeader(snapshot) && !CanPropose(snapshot) && UpToDate(request, snapshot));
        }
        finally { _gate.Release(); }
    }

    public async Task<LeadershipResult> CampaignAsync(CancellationToken ct = default)
    {
        var state = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!state.CanLead || !state.Voters.Concat(state.JointVoters ?? []).Contains(_nodeId))
            return new(false, "Máy này chưa đủ điều kiện ứng cử.");
        if (state.Role == NodeRole.Leader) return new(state.HasQuorum, state.HasQuorum ? null : "Máy trung tâm đang chờ đủ phiếu.");
        if (state.LeaderReady) return await NominateAsync(_nodeId, ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _campaignRequested, 1);
        discovery.RequestDiscovery();
        return new(true, null);
    }

    public async Task<LeadershipResult> NominateAsync(Guid target, CancellationToken ct = default)
    {
        var state = await GetStatusAsync(ct).ConfigureAwait(false);
        if (state.Role == NodeRole.Leader) return await TransferAsync(target, ct).ConfigureAwait(false);
        var leader = (await store.GetPeerEndpointsAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.NodeId == state.LeaderNodeId);
        if (leader is null || !state.LeaderReady)
            return new(false, "Chưa có máy trung tâm. Máy đủ điều kiện sẽ tự ứng cử.");
        try { return await transport.CallAsync<LeadershipResult>(leader, "Nominate", target, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsNetworkError(ex)) { return new(false, "Chưa liên lạc được máy trung tâm."); }
    }

    internal async Task<LeadershipResult> NominateRemoteAsync(Guid target, Guid source, CancellationToken ct)
    {
        using var db = database.Open(ct);
        if (!db.Exists<PeerNode>("NodeId=$0", source)) return new(false, "Máy đề cử chưa có trong mạng chat.");
        return await TransferAsync(target, ct).ConfigureAwait(false);
    }

    public async Task<LeadershipResult> SetCanLeadAsync(bool enabled, CancellationToken ct = default)
    {
        await _preference.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = Path.Combine(options.DataDirectory, "leadership.json");
            var temp = file + ".tmp";
            await FrameworkIO.WriteTextAsync(temp, JsonSerializer.Serialize(enabled), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            FrameworkIO.PublishFile(temp, file);
            runtime.CanLead = enabled;
            discovery.RequestDiscovery();
            if (!enabled && _role == NodeRole.Leader)
            {
                runtime.SetReadyLeader(null, 0);
                using var deadline = new CancellationTokenSource(3100);
                try { return await TransferAsync(null, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                { return new(false, "Đã lưu lựa chọn. Các máy còn lại sẽ tự bầu."); }
                finally { await RelinquishAsync().ConfigureAwait(false); }
            }
            if (!enabled && _role == NodeRole.Candidate) await RelinquishAsync().ConfigureAwait(false);
            return new(true, null);
        }
        finally { _preference.Release(); }
    }

    private async Task RelinquishAsync(bool quorumLost = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_role == NodeRole.Follower || (quorumLost && (_role != NodeRole.Leader ||
                FrameworkIO.Elapsed(_lastQuorum == 0 ? _electedAt : _lastQuorum) < options.LeaderTimeout))) return;
            await store.WriteAsync(db =>
            {
                db.Required<ClusterState>().CurrentLeaderNodeId = null;
                return Task.FromResult(true);
            }, ct).ConfigureAwait(false);
            _role = NodeRole.Follower;
            _lastQuorum = 0;
            _remoteReady = false;
            runtime.SetReadyLeader(null, 0);
        }
        finally { _gate.Release(); }
    }

    private Task RetireIfQuorumLostAsync(CancellationToken ct) => RelinquishAsync(quorumLost: true, ct: ct);

    private async Task<LeadershipResult> TransferAsync(Guid? target, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2700);
        var token = timeout.Token;
        if (!await _proposal.WaitAsync(0, token).ConfigureAwait(false))
            return new(false, "Đang xác nhận thay đổi nhóm. Hãy thử lại.");
        try
        {
            Snapshot snapshot;
            PeerEndpoint[] candidates;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                snapshot = await ReadAsync(token);
                var configuration = Configuration(snapshot.Log);
                if (_role != NodeRole.Leader || _lastQuorum == 0 || !CommittedInTerm(snapshot) ||
                    FrameworkIO.Elapsed(_lastQuorum) >= TimeSpan.FromTicks(options.HeartbeatInterval.Ticks * 3))
                    return new(false, "Chưa có đủ phiếu để bàn giao.");
                if (target == _nodeId) return new(true, null);
                _transferring = true;
                candidates = (await store.GetPeerEndpointsAsync(token)).Where(p => configuration.All.Contains(p.NodeId) &&
                    (!target.HasValue || p.NodeId == target)).OrderByDescending(p => _match.GetValueOrDefault(p.NodeId)).ToArray();
            }
            finally { _gate.Release(); }

            var term = snapshot.State.CurrentTerm;
            var checks = await Task.WhenAll(candidates.Select(async peer =>
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                probe.CancelAfter(550);
                try
                {
                    var response = await transport.CallAsync<ConsensusStatus>(peer, "ConsensusStatus", new { }, probe.Token).ConfigureAwait(false);
                    return (Peer: peer, Eligible: response.CanLead && response.Term == term);
                }
                catch (Exception ex) when (IsNetworkError(ex)) { return (Peer: peer, Eligible: false); }
            })).ConfigureAwait(false);
            foreach (var item in checks.Where(c => c.Eligible))
            {
                bool caughtUp;
                do
                {
                    await ReplicateRoundAsync(token).ConfigureAwait(false);
                    await _gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        snapshot = await ReadAsync(token);
                        if (_role != NodeRole.Leader || snapshot.State.CurrentTerm != term)
                            return new(false, "Máy trung tâm đã thay đổi.");
                        caughtUp = _match.GetValueOrDefault(item.Peer.NodeId) == snapshot.LastIndex;
                    }
                    finally { _gate.Release(); }
                    if (!caughtUp) await Task.Delay(50, token).ConfigureAwait(false);
                } while (!caughtUp);

                LeadershipResult response;
                try
                {
                    response = await transport.CallAsync<LeadershipResult>(item.Peer, "TimeoutNow",
                        new LeadershipHandoff(term, snapshot.LastIndex, snapshot.LastTerm), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsNetworkError(ex)) { continue; }
                if (!response.Success) continue;
                while (true)
                {
                    var state = await GetStatusAsync(token).ConfigureAwait(false);
                    if (state.Term > term && state.LeaderReady)
                        return state.LeaderNodeId == item.Peer.NodeId ? new(true, null) : new(false, "Một máy khác đã được bầu làm trung tâm.");
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
            return new(false, "Chưa có máy đủ điều kiện nhận bàn giao.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(false, "Hết thời gian chờ bàn giao máy trung tâm."); }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            _transferring = false;
            _gate.Release();
            _proposal.Release();
        }
    }

    internal async Task<LeadershipResult> AcceptHandoffAsync(LeadershipHandoff request, Guid source, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await ReadAsync(ct);
            if (!runtime.CanLead || faults.SimulateLeaderFailure || snapshot.State.CurrentTerm != request.Term ||
                snapshot.State.CurrentLeaderNodeId != source || !FreshLeader(snapshot) ||
                !Configuration(snapshot.Log).All.Contains(_nodeId) || snapshot.LastIndex != request.LastIndex ||
                snapshot.LastTerm != request.LastTerm)
                return new(false, "Máy này chưa sẵn sàng nhận vai trò.");
            _handoff = (source, request.Term);
            Volatile.Write(ref _handoffPending, 1);
            return new(true, null);
        }
        finally { _gate.Release(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (_role == NodeRole.Leader) await TransferAsync(null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
    }

}
