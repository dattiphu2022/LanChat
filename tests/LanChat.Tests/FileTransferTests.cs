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
    public async Task OpenLocalFilePreservesNameAndUserEditsWithoutChangingSharedBlob()
    {
        await using var node = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, "Báo cáo tháng 9.txt"); await File.WriteAllTextAsync(source, "Nội dung gốc");
        var file = await Files(node).ImportLocalAsync(source); var path = await Files(node).PrepareOpenAsync(file);
        Assert.Equal(file.FileName, Path.GetFileName(path)); Assert.Equal(".txt", Path.GetExtension(path));
        Assert.StartsWith(Path.Combine(node.Options.DataDirectory, "Attachments", "Open"), path);
        Assert.Equal("Nội dung gốc", await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "Đã chỉnh sửa trên máy này");
        Assert.Equal(path, await Files(node).PrepareOpenAsync(file)); Assert.Equal("Đã chỉnh sửa trên máy này", await File.ReadAllTextAsync(path));
        Assert.Equal("Nội dung gốc", await File.ReadAllTextAsync((await Files(node).GetLocalPathAsync(file.Sha256))!));
    }
    [Fact]
    public async Task OpenRemoteFileAutomaticallyDownloadsAndCanReopenWithoutSource()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "Tài liệu từ xa.txt"); await File.WriteAllTextAsync(source, "Tải rồi mở một lần bấm");
        var file = await Files(a).ImportLocalAsync(source); Assert.Null(await Files(b).GetLocalPathAsync(file.Sha256));
        var path = await Files(b).PrepareOpenAsync(file); Assert.Equal(file.FileName, Path.GetFileName(path));
        Assert.Equal("Tải rồi mở một lần bấm", await File.ReadAllTextAsync(path)); Assert.NotNull(await Files(b).GetLocalPathAsync(file.Sha256));
        await a.Host.StopAsync(); Assert.Equal(path, await Files(b).PrepareOpenAsync(file));
    }
    [Fact]
    public async Task OpenCopiesSeparateIdenticallyNamedAttachmentsAndFlattenUnsafeNames()
    {
        await using var node = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, "report.txt"); await File.WriteAllTextAsync(source, "First"); var first = await Files(node).ImportLocalAsync(source);
        await File.WriteAllTextAsync(source, "Second"); var second = await Files(node).ImportLocalAsync(source);
        var firstPath = await Files(node).PrepareOpenAsync(first); var secondPath = await Files(node).PrepareOpenAsync(second);
        Assert.NotEqual(firstPath, secondPath); Assert.Equal("First", await File.ReadAllTextAsync(firstPath)); Assert.Equal("Second", await File.ReadAllTextAsync(secondPath));
        var unsafeName = first with { Id = Guid.NewGuid(), FileName = "../../report.txt" };
        var safePath = await Files(node).PrepareOpenAsync(unsafeName);
        Assert.Equal(Path.Combine(node.Options.DataDirectory, "Attachments", "Open", unsafeName.Id.ToString("N")), Path.GetDirectoryName(Path.GetFullPath(safePath)));
        Assert.Equal(".txt", Path.GetExtension(safePath));
    }
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
        // A sender with no intact copy must not prevent another cached peer from serving the original.
        await File.WriteAllTextAsync(source, "changed source");
        await File.WriteAllTextAsync((await Files(a).GetLocalPathAsync(file.Sha256))!, "damaged cache");
        Assert.False((await a.Host.Services.GetRequiredService<PeerFileTransferService>().HasFileAsync(file.Sha256, default)).Available);
        await File.WriteAllTextAsync(await Files(b).PrepareOpenAsync(received), "Receiver's local edits");
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
        await File.WriteAllTextAsync(source, "modified");
        Assert.False((await a.Host.Services.GetRequiredService<PeerFileTransferService>().HasFileAsync(metadata.Sha256, default)).Available);
        var error = await Assert.ThrowsAsync<IOException>(() => Files(b).PrepareOpenAsync(metadata));
        Assert.Contains("gửi lại tệp", error.Message);
        Assert.Null(await Files(b).GetLocalPathAsync(metadata.Sha256)); Assert.Empty(Directory.GetFiles(Path.Combine(b.Options.DataDirectory, "Attachments"), "*.part", SearchOption.AllDirectories));
        // Reimport repairs a damaged content-addressed local copy.
        await File.WriteAllTextAsync(source, "original");
        await Files(a).ImportLocalAsync(source); Assert.Equal("original", await File.ReadAllTextAsync(await Files(b).DownloadAsync(metadata)));
    }
    [Theory]
    [InlineData("modified")]
    [InlineData("Nội dung mới dài hơn sau khi gửi")]
    [InlineData(null)]
    public async Task ChangingOrDeletingOriginalAfterSendDoesNotAffectDelayedRecipient(string? replacement)
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "report.txt"); await File.WriteAllTextAsync(source, "original");
        var file = await Files(a).ImportLocalAsync(source);
        if (replacement is null) File.Delete(source); else await File.WriteAllTextAsync(source, replacement);
        var senderCopy = await Files(a).PrepareOpenAsync(file); await File.WriteAllTextAsync(senderCopy, "Sender edited in Word/Excel");
        var received = await Files(b).PrepareOpenAsync(file);
        Assert.Equal("original", await File.ReadAllTextAsync(received));
        await File.WriteAllTextAsync(received, "Receiver edits");
        Assert.Equal("original", await File.ReadAllTextAsync(await Files(b).DownloadAsync(file)));
        Assert.Equal("Receiver edits", await File.ReadAllTextAsync(await Files(b).PrepareOpenAsync(file)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedOrMissingBlobAutomaticallyRecoversFromUnchangedSource(bool delete)
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync();
        var source = Path.Combine(a.Options.DataDirectory, "report.txt"); await File.WriteAllTextAsync(source, "original");
        var file = await Files(a).ImportLocalAsync(source); var blob = (await Files(a).GetLocalPathAsync(file.Sha256))!;
        if (delete) File.Delete(blob); else await File.WriteAllTextAsync(blob, "tampered");
        // A fresh host must recover using persisted source information, without reimporting.
        await using var restarted = new TestNode(cluster, secret, "A", a.Options.DataDirectory);
        await restarted.InitializeAsync(true); await b.Store.RememberPeerAsync(await restarted.Store.GetNodeInfoAsync(), "127.0.0.1");
        Assert.Equal("original", await File.ReadAllTextAsync(await Files(b).PrepareOpenAsync(file)));
        Assert.Equal("original", await File.ReadAllTextAsync(blob));
        Assert.Empty(Directory.GetFiles(Path.Combine(a.Options.DataDirectory, "Attachments"), "*.part", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAttachmentRecoversOnlyFromUnchangedOpenCopy(bool edited)
    {
        await using var node = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, "report.txt"); await File.WriteAllTextAsync(source, "original");
        var file = await Files(node).ImportLocalAsync(source);
        await using var other = new TestNode(node.Options.ClusterId, node.Options.ClusterSecret, "B"); await other.InitializeAsync();
        await node.Store.RememberPeerAsync(await other.Store.GetNodeInfoAsync(), "127.0.0.1");
        var conversation = await node.Store.OpenDirectAsync((await other.Store.GetIdentityAsync()).UserId);
        await node.Host.Services.GetRequiredService<IDistributedStore>().SendToConversationAsync(conversation, "File", attachments: [file]);
        var open = await Files(node).PrepareOpenAsync(file); var blob = (await Files(node).GetLocalPathAsync(file.Sha256))!;
        File.Delete(blob + ".source.json"); File.Delete(source);
        await File.WriteAllTextAsync(blob, "tampered");
        if (edited) await File.WriteAllTextAsync(open, "modified");
        var availability = await node.Host.Services.GetRequiredService<PeerFileTransferService>().HasFileAsync(file.Sha256, default);
        Assert.Equal(!edited, availability.Available);
        if (!edited) Assert.Equal("original", await File.ReadAllTextAsync(await Files(node).DownloadAsync(file)));
        else Assert.Null(await Files(node).GetLocalPathAsync(file.Sha256));
        Assert.Equal(edited ? "modified" : "original", await File.ReadAllTextAsync(open));
    }
    [Fact]
    public async Task VerifiedSourceCannotBeEditedUntilTransferHandleIsClosed()
    {
        await using var node = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, "report.txt"); await File.WriteAllTextAsync(source, "original");
        var file = await Files(node).ImportLocalAsync(source); var blob = (await Files(node).GetLocalPathAsync(file.Sha256))!;
        await using (var stream = await node.Host.Services.GetRequiredService<PeerFileTransferService>().OpenVerifiedLocalAsync(file.Sha256, default))
        {
            Assert.NotNull(stream); Assert.Equal(0, stream.Position);
            Assert.Throws<IOException>(() => { using var writer = new FileStream(blob, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Assert.Equal(file.Sha256, (await Files(node).ImportLocalAsync(source)).Sha256);
        }
        await File.WriteAllTextAsync(blob, "tampered");
    }
    [Fact]
    public async Task CancellationDuringStreamingRemovesPartialDownload()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "large.bin"); await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(4 * 1024 * 1024)); var metadata = await Files(a).ImportLocalAsync(source);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Files(b).PrepareOpenAsync(metadata, new CaptureProgress(_ => cancel.Cancel()), cancel.Token));
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
