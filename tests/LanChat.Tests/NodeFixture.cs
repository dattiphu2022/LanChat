using System.Net;
using System.Net.Sockets;
using LanChat.Application;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LanChat.Tests;

internal sealed class TestNode : IAsyncDisposable
{
    // Test-only credential fixture; production state is always scoped to a DI host and its data directory.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]> Certificates = new();
    public NodeOptions Options { get; }
    public IHost Host { get; }
    public IChatStore Store => Host.Services.GetRequiredService<IChatStore>();
    public TestNode(Guid cluster, byte[] secret, string? name = null, string? directory = null, DiscoverySockets? discovery = null,
        Func<long>? utcSeconds = null)
    {
        Options = new NodeOptions { ClusterId = cluster, ClusterSecret = secret,
            TlsCertificatePfx = Certificates.GetOrAdd(cluster, ClusterTls.CreateCertificate),
            ServicePort = FreeTcpPort(), DiscoveryPort = FreeUdpPort(),
            DataDirectory = directory ?? Path.Combine(Path.GetTempPath(), "LanChat.Tests", Guid.NewGuid().ToString("N")) };
        if (name is not null) Options.InitialDisplayName = name;
        Host = ServiceRegistration.BuildHost(Options, services =>
        {
            if (discovery is not null) services.AddSingleton(discovery);
            if (utcSeconds is not null) services.AddSingleton(new WireProtocol(Options, utcSeconds));
        });
    }
    public async Task InitializeAsync(bool startNetwork = false)
    { await Store.InitializeAsync(); if (startNetwork) await Host.StartAsync(); }
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(8000); await Host.StopAsync(timeout.Token); Host.Dispose();
        // Keep isolated test databases and logs for inspection; never delete a user's data folder.
    }
    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    public static int FreeUdpPort() { using var client = new UdpClient(0); return ((IPEndPoint)client.Client.LocalEndPoint!).Port; }
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await condition()) await Task.Delay(100, timeout.Token);
    }
}
