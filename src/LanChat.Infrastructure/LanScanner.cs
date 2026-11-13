using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using LanChat.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed class LanScanner(ILocalNetworkInfoProvider networks, IPeerTransport transport, IChatStore store,
    IDistributedStore distributed, NodeOptions options, PeerDiscoveryBackgroundService discovery) : ILanScanner, IDisposable
{
    private sealed class ScanSession
    {
        public readonly CancellationTokenSource Stop = new();
        public Task<IReadOnlyList<DiscoveredPeerDto>> Task = null!;
        public readonly ConcurrentDictionary<Guid, DiscoveredPeerDto> Found = new();
        public int ManualUsers;
        public volatile bool CheckingUdp = true;
        public int Checked;
        public int Users;
    }

    private const int AutomaticLimit = 4096;
    private readonly object _gate = new();
    private ScanSession? _active;

    public Task<IReadOnlyList<DiscoveredPeerDto>> ScanAsync(CancellationToken cancellationToken = default,
        IProgress<LanScanProgress>? progress = null) => JoinAsync(true, progress, cancellationToken);

    internal Task<IReadOnlyList<DiscoveredPeerDto>> RefreshAsync(CancellationToken ct) => JoinAsync(false, null, ct);

    private async Task<IReadOnlyList<DiscoveredPeerDto>> JoinAsync(bool manual, IProgress<LanScanProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ScanSession scan;
        lock (_gate)
        {
            if (_active is null || _active.Task.IsCompleted || _active.Stop.IsCancellationRequested)
            {
                scan = new ScanSession { ManualUsers = manual ? 1 : 0 };
                scan.Task = Task.Run(() => RunAsync(scan), scan.Stop.Token);
                _active = scan;
            }
            else { scan = _active; if (manual) scan.ManualUsers++; }
            scan.Users++;
        }
        try
        {
            if (manual) discovery.RequestDiscovery();
            while (!scan.Task.IsCompleted)
            {
                progress?.Report(new LanScanProgress(Volatile.Read(ref scan.Checked), scan.Found.Count, scan.CheckingUdp));
                await Task.WhenAny(scan.Task, Task.Delay(100, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            var result = await scan.Task.ConfigureAwait(false);
            progress?.Report(new LanScanProgress(scan.Checked, result.Count, false, scan.Checked));
            return result;
        }
        finally
        {
            lock (_gate)
            {
                if (manual) scan.ManualUsers--;
                if (--scan.Users == 0)
                {
                    if (_active == scan) _active = null;
                    scan.Stop.Cancel();
                    _ = scan.Task.ContinueWith(t => { _ = t.Exception; scan.Stop.Dispose(); }, TaskScheduler.Default);
                }
            }
        }
    }

    private async Task<IReadOnlyList<DiscoveredPeerDto>> RunAsync(ScanSession scan)
    {
        var ct = scan.Stop.Token;
        var started = Stopwatch.GetTimestamp();
        discovery.RequestDiscovery();
        await Task.Delay(750, ct).ConfigureAwait(false);
        foreach (var p in discovery.PeersSince(started)) scan.Found[p.NodeId] = p;
        scan.CheckingUdp = false;

        var own = await store.GetNodeInfoAsync(ct);
        var ranges = networks.GetNetworks();
        var local = new HashSet<string>(ranges.Select(r => r.Address));
        var known = await distributed.GetPeerEndpointsAsync(ct);
        if (scan.Found.Count > 0 && known.All(p => scan.Found.ContainsKey(p.NodeId)) && Volatile.Read(ref scan.ManualUsers) == 0)
            return scan.Found.Values.ToArray();

        var endpoints = known.Where(p => !scan.Found.ContainsKey(p.NodeId) && IPAddress.TryParse(p.Address, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetwork && (IPAddress.IsLoopback(ip) ||
            ranges.Any(r => NetworkRangeCalculator.Contains(r, p.Address)))).Select(p => (p.Address, p.Port)).Distinct().ToArray();
        var checkedEndpoints = new HashSet<(string Address, int Port)>(endpoints);
        checkedEndpoints.UnionWith(scan.Found.Values.Select(p => (p.IPAddress, p.ServicePort)));
        var subnets = ranges.OrderByDescending(r => !string.IsNullOrEmpty(r.Gateway))
            .ThenByDescending(r => r.PrefixLength).GroupBy(r => (r.Network, r.PrefixLength)).Select(g => g.First());
        var ports = new[] { options.ServicePort, NodeOptions.DefaultServicePort, ClusterPorts.Alternate(options.ClusterId) }.Distinct().ToArray();

        IEnumerable<string> FallbackAddresses()
        {
            var previous = new List<NetworkInfo>();
            foreach (var subnet in subnets)
            {
                foreach (var ip in NetworkRangeCalculator.Hosts(subnet))
                    if (!local.Contains(ip) && !previous.Any(r => NetworkRangeCalculator.ContainsHost(r, ip)))
                        yield return ip;
                previous.Add(subnet);
            }
        }

        IEnumerable<(string Address, int Port)> Candidates()
        {
            var addressBudget = 0;
            foreach (var endpoint in endpoints) yield return endpoint;
            foreach (var address in FallbackAddresses())
            {
                if (Volatile.Read(ref scan.ManualUsers) == 0 && addressBudget >= AutomaticLimit) yield break;
                addressBudget++;
                foreach (var port in ports)
                    if (!checkedEndpoints.Contains((address, port)))
                        yield return (address, port);
            }
        }
        await FrameworkIO.ForEachAsync(Candidates(), 64, ct, async (endpoint, token) =>
        {
            try
            {
                var info = transport is PeerTransport peerTransport
                    ? await peerTransport.DiscoverAsync(endpoint.Address, endpoint.Port, own, token).ConfigureAwait(false)
                    : await transport.GetNodeInfoAsync(endpoint.Address, endpoint.Port, token).ConfigureAwait(false);
                if (info.NodeId == own.NodeId || info.User is null || info.User.Id == own.User.Id) return;
                await store.RememberPeerAsync(info, endpoint.Address, token).ConfigureAwait(false);
                scan.Found[info.NodeId] = ToDiscovered(info, endpoint.Address, options.ClusterId);
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or InvalidDataException or SocketException or
                OperationCanceledException or ObjectDisposedException or JsonException or FormatException or AuthenticationException) { }
            finally { Interlocked.Increment(ref scan.Checked); }
        }).ConfigureAwait(false);
        foreach (var p in discovery.PeersSince(started)) scan.Found[p.NodeId] = p;
        return scan.Found.Values.ToArray();
    }

    internal static DiscoveredPeerDto ToDiscovered(NodeInfoDto info, string address, Guid cluster) =>
        new(info.NodeId, info.User.Id, info.User.DisplayName, info.DeviceName, address,
            info.ServicePort, info.Role, info.Term, info.ProtocolVersion, info.ApplicationVersion, cluster);

    public void Dispose()
    {
        lock (_gate) _active?.Stop.Cancel();
    }
}

public sealed class FallbackScanBackgroundService(LanScanner scanner, PeerDiscoveryBackgroundService discovery,
    Microsoft.Extensions.Logging.ILogger<FallbackScanBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        void Changed(object? sender, EventArgs args)
        {
            discovery.RequestDiscovery();
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
        NetworkChange.NetworkAddressChanged += Changed;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await scanner.RefreshAsync(ct).ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "Fallback discovery failed"); }
                await _wake.WaitAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { NetworkChange.NetworkAddressChanged -= Changed; }
    }
}
