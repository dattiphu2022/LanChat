using System.Net.Http;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using LanChat.WinForms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class LanUpdateTests
{
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "LanChat.UpdateTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private static async Task<UpdatePackage> Import(LocalUpdatePackages packages, string contents = "installer", string version = "v9.12.3")
    {
        packages.InspectForTesting = _ => version;
        var source = Path.Combine(Temp(), "LanChat.test.setup.exe"); File.WriteAllText(source, contents);
        return await packages.ImportAsync(source);
    }
    private static PeerEndpoint Peer(TestNode node, NodeIdentity identity) => new(identity.NodeId, "127.0.0.1", node.Options.ServicePort);

    [Fact]
    public async Task LanUsesPinnedIdentitySignedManifestAndSharedCacheWithoutHttp()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); var directory = Temp(); var packages = new LocalUpdatePackages(directory);
        var package = await Import(packages); packages.Configure(package, [cluster]);
        await using var source = new TestNode(cluster, secret, "Source", updatePackages: packages); await source.InitializeAsync(true);
        await using var receiver = new TestNode(cluster, secret, "Receiver"); await receiver.InitializeAsync(true);
        var identity = await source.Store.GetIdentityAsync(); var peer = Peer(source, identity);
        var client = receiver.Host.Services.GetRequiredService<ILanUpdateClient>();
        var manifest = await client.CheckAsync(peer); Assert.Equal(package, manifest.Package); Assert.NotNull(manifest.Signature);
        using var updates = new UpdateService(Temp(), new NoHttp()); updates.ResolveNetwork = id => id == cluster ? receiver.Host.Services : null;
        await updates.ConfigureAsync(new(UpdateSourceKind.Lan, cluster, identity.NodeId, peer.Address, peer.Port, "Source"), default);
        var release = (await updates.CheckAsync(true, default)).Release; Assert.NotNull(release);
        using (var setup = await updates.DownloadAsync(release!, new Progress<long>(), default)) Assert.Equal("installer", File.ReadAllText(setup.Path));
        await source.Host.StopAsync();
        using var offline = await updates.DownloadAsync(release!, new Progress<long>(), default); Assert.Equal("installer", File.ReadAllText(offline.Path));
        Assert.Equal(0, packages.ActiveTransfers);
    }

    [Fact]
    public async Task ReplacingOrDisablingPackageDoesNotBreakAnExistingLease()
    {
        var packages = new LocalUpdatePackages(Temp()); var cluster = Guid.NewGuid(); var first = await Import(packages, "first"); packages.Configure(first, [cluster]);
        using (var lease = packages.OpenPackage(cluster, first.Id))
        {
            Assert.False(packages.TryPauseForInstall());
            var second = await Import(packages, "second", "v9.12.4"); packages.Configure(second, [cluster]);
            Assert.Throws<IOException>(() => packages.OpenPackage(cluster, first.Id));
            packages.Configure(second, []); Assert.Null(packages.GetPackage(cluster));
            using var reader = new StreamReader(lease); Assert.Equal("first", await reader.ReadToEndAsync());
        }
        Assert.Equal(0, packages.ActiveTransfers); Assert.True(packages.TryPauseForInstall()); packages.Resume();
    }

    [Fact]
    public async Task ServerLimitsTransfersAndPausesNewReadsBeforeInstalling()
    {
        var packages = new LocalUpdatePackages(Temp()); var cluster = Guid.NewGuid(); var package = await Import(packages); packages.Configure(package, [cluster]);
        var leases = Enumerable.Range(0, 4).Select(_ => packages.OpenPackage(cluster, package.Id)).ToArray();
        try { Assert.Equal(4, packages.ActiveTransfers); Assert.Throws<IOException>(() => packages.OpenPackage(cluster, package.Id)); Assert.False(packages.TryPauseForInstall()); }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Assert.True(packages.TryPauseForInstall()); Assert.Null(packages.GetPackage(cluster)); Assert.Throws<IOException>(() => packages.OpenPackage(cluster, package.Id));
        packages.Resume(); using var retry = packages.OpenPackage(cluster, package.Id);
    }

    [Fact]
    public async Task CancelImportPreservesPublishedPackageAndRemovesPartials()
    {
        var root = Temp(); var packages = new LocalUpdatePackages(root); var cluster = Guid.NewGuid(); var package = await Import(packages); packages.Configure(package, [cluster]);
        var source = Path.Combine(Temp(), "large.exe"); File.WriteAllBytes(source, FrameworkIO.RandomBytes(1024 * 1024)); using var canceled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => packages.ImportAsync(source, new ProgressNow<FileProgress>(_ => canceled.Cancel()), canceled.Token));
        Assert.Equal(package, packages.GetPackage(cluster)); Assert.False(packages.Preparing);
        Assert.Empty(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories)); Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task LocalSourcePersistsSharesIndependentlyAndDoesNotDowngrade()
    {
        var root = Temp(); var cluster = Guid.NewGuid();
        using (var updates = new UpdateService(root, new NoHttp()))
        {
            var package = await Import(updates.Packages);
            await updates.ConfigureAsync(new(UpdateSourceKind.Local, Package: package, Sharing: true, Networks: [cluster]), default);
            var release = (await updates.CheckAsync(false, default)).Release; Assert.NotNull(release);
            using var setup = await updates.DownloadAsync(release!, new Progress<long>(), default); Assert.Equal("installer", File.ReadAllText(setup.Path));
        }
        using (var restart = new UpdateService(root, new NoHttp()))
        {
            Assert.Equal(UpdateSourceKind.Local, restart.Settings.Kind); Assert.True(restart.Settings.Sharing); Assert.NotNull(restart.Packages.GetPackage(cluster));
            var old = await Import(restart.Packages, version: "v1.0.0"); await restart.ConfigureAsync(new(UpdateSourceKind.Local, Package: old), default);
            Assert.Null((await restart.CheckAsync(true, default)).Release);
            Assert.Null(restart.Packages.GetPackage(cluster));
            await restart.ConfigureAsync(new(UpdateSourceKind.GitHub, Package: old, Sharing: true, Networks: [cluster]), default);
            Assert.Equal(old, restart.Packages.GetPackage(cluster));
            File.Delete(restart.Packages.PackagePath(old));
            await restart.ConfigureAsync(new(UpdateSourceKind.GitHub, Package: old, Sharing: false), default);
            Assert.Null(restart.Packages.GetPackage(cluster));
        }
    }

    [Fact]
    public async Task SwitchingSourceInvalidatesOldCandidateEvenWhenItsInstallerIsCached()
    {
        using var updates = new UpdateService(Temp(), new NoHttp()); var package = await Import(updates.Packages);
        await updates.ConfigureAsync(new(UpdateSourceKind.Local, Package: package), default);
        var old = (await updates.CheckAsync(true, default)).Release!; using (await updates.DownloadAsync(old, new Progress<long>(), default)) { }
        await updates.ConfigureAsync(new(UpdateSourceKind.GitHub), default);
        await Assert.ThrowsAsync<IOException>(() => updates.DownloadAsync(old, new Progress<long>(), default));
    }

    [Fact]
    public async Task CorruptSnapshotFailsVerificationAndLeavesNoReadyInstaller()
    {
        using var updates = new UpdateService(Temp(), new NoHttp()); var package = await Import(updates.Packages);
        await updates.ConfigureAsync(new(UpdateSourceKind.Local, Package: package), default); var release = (await updates.CheckAsync(true, default)).Release!;
        File.WriteAllText(updates.Packages.PackagePath(package), "corrupted");
        await Assert.ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(release, new Progress<long>(), default)); Assert.False(updates.HasDownloaded(release));
    }

    [Fact]
    public async Task ChangedNodeAndTamperedSignatureAreRejected()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); var packages = new LocalUpdatePackages(Temp()); var package = await Import(packages); packages.Configure(package, [cluster]);
        await using var source = new TestNode(cluster, secret, "Source", updatePackages: packages); await source.InitializeAsync(true);
        await using var receiver = new TestNode(cluster, secret, "Receiver"); await receiver.InitializeAsync();
        var identity = await source.Store.GetIdentityAsync(); var peer = Peer(source, identity); var client = receiver.Host.Services.GetRequiredService<ILanUpdateClient>();
        var manifest = await client.CheckAsync(peer);
        var store = receiver.Host.Services.GetRequiredService<ChatStore>();
        Assert.Throws<InvalidDataException>(() => store.VerifyUpdate(manifest with { Package = package with { Size = 123 } }, identity.NodeId, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(peer with { NodeId = Guid.NewGuid() }));
        var newer = await Import(packages, "new", "v9.12.4"); packages.Configure(newer, [cluster]);
        await Assert.ThrowsAsync<IOException>(() => client.OpenAsync(peer, package));
    }

    [Fact]
    public async Task InvalidSavedConfigurationDoesNotFallBackToGitHub()
    {
        var root = Temp(); Directory.CreateDirectory(Path.Combine(root, "Updates")); File.WriteAllText(Path.Combine(root, "Updates", "settings.json"), "broken json");
        using var updates = new UpdateService(root, new NoHttp()); Assert.NotNull(updates.SettingsError);
        await Assert.ThrowsAsync<IOException>(() => updates.CheckAsync(false, default));
    }

    [Fact]
    public async Task SharingIsRestrictedToAllowedChatNetworksAndRejectsInvalidInstallers()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); var packages = new LocalUpdatePackages(Temp());
        var invalid = Path.Combine(Temp(), "not-an-installer.exe"); File.WriteAllText(invalid, "plain text");
        await Assert.ThrowsAsync<InvalidDataException>(() => packages.ImportAsync(invalid)); Assert.False(packages.Preparing);
        var package = await Import(packages); packages.Configure(package, [Guid.NewGuid()]);
        await using var source = new TestNode(cluster, secret, "Source", updatePackages: packages); await source.InitializeAsync(true);
        await using var receiver = new TestNode(cluster, secret, "Receiver"); await receiver.InitializeAsync();
        var identity = await source.Store.GetIdentityAsync(); var client = receiver.Host.Services.GetRequiredService<ILanUpdateClient>();
        var missing = await Assert.ThrowsAsync<IOException>(() => client.CheckAsync(Peer(source, identity))); Assert.Contains("mạng này", missing.Message);
        packages.Configure(package, [cluster]); Assert.NotNull(await client.CheckAsync(Peer(source, identity)));
        await using var stranger = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "Other network"); await stranger.InitializeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => stranger.Host.Services.GetRequiredService<ILanUpdateClient>().CheckAsync(Peer(source, identity), deadline.Token));
    }

    [Fact]
    public async Task ClosingLanDownloadReleasesServerLeaseAndAllowsRetry()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); var packages = new LocalUpdatePackages(Temp());
        var package = await Import(packages, new string('a', 16 * 1024 * 1024)); packages.Configure(package, [cluster]);
        await using var source = new TestNode(cluster, secret, "Source", updatePackages: packages); await source.InitializeAsync(true);
        await using var receiver = new TestNode(cluster, secret, "Receiver"); await receiver.InitializeAsync();
        var peer = Peer(source, await source.Store.GetIdentityAsync()); var client = receiver.Host.Services.GetRequiredService<ILanUpdateClient>();
        using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var download = await client.OpenAsync(peer, package, canceled.Token))
        { Assert.Equal(1, packages.ActiveTransfersFor(cluster)); canceled.Cancel(); }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (packages.ActiveTransfers > 0 && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
        Assert.Equal(0, packages.ActiveTransfers); Assert.True(packages.TryPauseForInstall()); packages.Resume();
        using var retry = await client.OpenAsync(peer, package); Assert.True(retry.CanRead);
    }

    [Fact]
    public async Task FailedConfigurationPublishKeepsPreviousSharingAndSource()
    {
        var root = Temp(); using var updates = new UpdateService(root, new NoHttp()); var cluster = Guid.NewGuid();
        var package = await Import(updates.Packages); await updates.ConfigureAsync(new(UpdateSourceKind.Local, Package: package, Sharing: true, Networks: [cluster]), default);
        var file = Path.Combine(root, "Updates", "settings.json"); File.Move(file, file + ".previous"); Directory.CreateDirectory(file);
        await Assert.ThrowsAnyAsync<IOException>(() => updates.ConfigureAsync(new(UpdateSourceKind.GitHub), default));
        Assert.Equal(UpdateSourceKind.Local, updates.Settings.Kind); Assert.Equal(package, updates.Packages.GetPackage(cluster));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "Updates"), "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentDisposalReleasesTransferExactlyOnce()
    {
        var packages = new LocalUpdatePackages(Temp()); var cluster = Guid.NewGuid(); var package = await Import(packages); packages.Configure(package, [cluster]);
        var lease = packages.OpenPackage(cluster, package.Id); Parallel.For(0, 100, _ => lease.Dispose());
        Assert.Equal(0, packages.ActiveTransfers); Assert.Equal(0, packages.ActiveTransfersFor(cluster));
        Assert.True(packages.TryPauseNetwork(cluster)); Assert.Null(packages.GetPackage(cluster));
        Assert.Throws<IOException>(() => packages.OpenPackage(cluster, package.Id)); packages.ResumeNetwork(cluster);
        using var retry = packages.OpenPackage(cluster, package.Id); Assert.False(packages.TryPauseNetwork(cluster));
    }
    private sealed class NoHttp : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("LAN/local must never call HTTP."); }
    private sealed class ProgressNow<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
