using System.Security.Cryptography;
using LanChat.Application;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class FileTransferTests
{
    private static IFileTransferService Files(TestNode n) => n.Host.Services.GetRequiredService<IFileTransferService>();
    [Fact]
    public async Task MetadataReplicatesButBytesOnlyDownloadOnRequestAndCachedPeerCanServeThem()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var c = new TestNode(cluster, secret, "C");
        await a.InitializeAsync(true); await b.InitializeAsync(true); await c.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1"); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "tài liệu.txt"); var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17); await File.WriteAllBytesAsync(source, bytes);
        var file = await Files(a).ImportLocalAsync(source); var conversation = await a.Store.OpenDirectAsync((await b.Store.GetIdentityAsync()).UserId);
        var storeA = a.Host.Services.GetRequiredService<IDistributedStore>(); var storeB = b.Host.Services.GetRequiredService<IDistributedStore>();
        await storeA.SendToConversationAsync(conversation, "Tệp kiểm thử", attachments: [file]);
        await TestNode.WaitUntilAsync(async () => (await storeB.GetAttachmentsAsync()).Length == 1);
        var received = Assert.Single(await storeB.GetAttachmentsAsync()); Assert.Equal(file.Sha256, received.Sha256);
        Assert.Null(await Files(b).GetLocalPathAsync(file.Sha256));
        var progress = new CaptureProgress(); var path = await Files(b).DownloadAsync(received, progress);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.Equal(bytes.Length, progress.Last!.Completed);
        // C knows only B; file data can be fetched without the original sender or a leader.
        await c.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var copy = await Files(c).DownloadAsync(received); Assert.Equal(bytes, await File.ReadAllBytesAsync(copy));
    }
    [Fact]
    public async Task CorruptSourceIsRejectedAndTemporaryFileIsRemoved()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "source.txt"); await File.WriteAllTextAsync(source, "original"); var metadata = await Files(a).ImportLocalAsync(source);
        await File.WriteAllTextAsync((await Files(a).GetLocalPathAsync(metadata.Sha256))!, "tampered");
        await Assert.ThrowsAsync<IOException>(() => Files(b).DownloadAsync(metadata));
        Assert.Null(await Files(b).GetLocalPathAsync(metadata.Sha256)); Assert.Empty(Directory.GetFiles(Path.Combine(b.Options.DataDirectory, "Attachments"), "*.part", SearchOption.AllDirectories));
        // Reimport repairs a damaged content-addressed local copy.
        await Files(a).ImportLocalAsync(source); Assert.Equal("original", await File.ReadAllTextAsync(await Files(b).DownloadAsync(metadata)));
    }
    [Fact]
    public async Task CancellationDuringStreamingRemovesPartialDownload()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "large.bin"); await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(4 * 1024 * 1024)); var metadata = await Files(a).ImportLocalAsync(source);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Files(b).DownloadAsync(metadata, new CaptureProgress(_ => cancel.Cancel()), cancel.Token));
        Assert.Null(await Files(b).GetLocalPathAsync(metadata.Sha256)); Assert.Empty(Directory.GetFiles(Path.Combine(b.Options.DataDirectory, "Attachments"), "*.part", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task HashPathTraversalAndMissingSourceAreRejected()
    {
        await using var node = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "A"); await node.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Files(node).GetLocalPathAsync("../../cluster.json"));
        await Assert.ThrowsAsync<IOException>(() => Files(node).DownloadAsync(new(Guid.NewGuid(), Guid.NewGuid(), "missing.txt", "text/plain", 10, new string('a', 64))));
    }
    private sealed class CaptureProgress(Action<FileProgress>? action = null) : IProgress<FileProgress>
    {
        public FileProgress? Last { get; private set; }
        public void Report(FileProgress value) { Last = value; action?.Invoke(value); }
    }
}
