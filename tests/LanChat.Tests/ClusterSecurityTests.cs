using System.Security.Cryptography;
using LanChat.Application;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class ClusterSecurityTests
{
    [Fact]
    public async Task InvitationPersistsProtectedCredentialsPortsAndCreatorIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LanChat.Tests", Guid.NewGuid().ToString("N"));
        var options = new NodeOptions { DataDirectory = directory, ServicePort = 54901, DiscoveryPort = 54902 };
        var invitation = ClusterConfigurationStore.CreateInvitation(); var parsed = ClusterConfigurationStore.ParseInvitation(invitation);
        await ClusterConfigurationStore.SaveAsync(options, invitation, creator: true);
        var json = await FrameworkIO.ReadTextAsync(Path.Combine(directory, "cluster.json")); Assert.DoesNotContain(parsed.Secret, json); Assert.DoesNotContain(parsed.TlsCertificatePfx!, json);
        var loaded = new NodeOptions { DataDirectory = directory }; Assert.True(await ClusterConfigurationStore.LoadAsync(loaded));
        Assert.Equal(parsed.BootstrapNodeId, loaded.InitialNodeId); Assert.Equal(options.ClusterSecret, loaded.ClusterSecret); Assert.Equal(options.TlsCertificatePfx, loaded.TlsCertificatePfx);
        Assert.Equal(54901, loaded.ServicePort); Assert.Equal(54902, loaded.DiscoveryPort);
        loaded.OverrideServicePort = true; loaded.ServicePort = 54903; await ClusterConfigurationStore.LoadAsync(loaded); Assert.Equal(54903, loaded.ServicePort);
    }
    [Fact]
    public async Task SameCertificateWithWrongSecretCannotAuthenticate()
    {
        var cluster = Guid.NewGuid();
        await using var a = new TestNode(cluster, FrameworkIO.RandomBytes(32), "A"); await using var b = new TestNode(cluster, FrameworkIO.RandomBytes(32), "B");
        await a.InitializeAsync(); await b.InitializeAsync(true);
        await Assert.ThrowsAnyAsync<IOException>(() => a.Host.Services.GetRequiredService<IPeerTransport>().GetNodeInfoAsync("127.0.0.1", b.Options.ServicePort, CancellationToken.None));
        Assert.Empty(await b.Store.GetUsersAsync());
    }
    [Fact]
    public async Task DatabaseCannotJoinADifferentClusterAndUncStorageIsRejected()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); string directory;
        await using (var node = new TestNode(cluster, secret, "A")) { await node.InitializeAsync(); directory = node.Options.DataDirectory; }
        await using var wrong = new TestNode(Guid.NewGuid(), secret, "Wrong", directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.InitializeAsync());
        Assert.Throws<ArgumentException>(() => ServiceRegistration.ValidateDataDirectory(@"\\server\share\LanChat"));
    }
}
