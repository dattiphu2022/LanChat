using System.Security.Cryptography;
using LanChat.Application;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class FileTransferTests
{
    private static IFileTransferService Files(TestNode n) => n.Host.Services.GetRequiredService<IFileTransferService>();
    private static string PreparedPath(TestNode node, AttachmentDto file) =>
        Path.Combine(node.Options.DataDirectory, "Attachments", "Open", file.Id.ToString("N"), file.FileName);

    [Fact]
    public async Task FolderTransfersOverTlsAndRestoresNestedFilesAndEmptyDirectories()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(true);
        await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(Path.GetDirectoryName(a.Options.DataDirectory)!, Guid.NewGuid().ToString("N"), "Hồ sơ");
        Directory.CreateDirectory(Path.Combine(source, "Phụ lục", "Rỗng"));
        var bytes = FrameworkIO.RandomBytes(32_000);
        var file = Path.Combine(source, "Phụ lục", "Kết quả.bin");
        await FrameworkIO.WriteBytesAsync(file, bytes);
        await FrameworkIO.WriteTextAsync(Path.Combine(source, "Báo cáo.txt"), "Bản gốc");
        var folder = await Files(a).ImportFolderAsync(source);
        Assert.Equal(FolderAttachment.MimeType, folder.MimeType);
        await FrameworkIO.WriteBytesAsync(file, [1, 2, 3]);
        Assert.Null(await Files(b).GetLocalPathAsync(folder.Sha256));

        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var conversation = await a.Store.OpenDirectAsync((await b.Store.GetIdentityAsync()).UserId);
        var storeA = a.Host.Services.GetRequiredService<IDistributedStore>();
        var storeB = b.Host.Services.GetRequiredService<IDistributedStore>();
        await storeA.SendToConversationAsync(conversation, "", attachments: [folder]);
        await storeB.ImportEventsAsync(await storeA.GetEventRangeAsync(new((await a.Store.GetIdentityAsync()).NodeId, 0)));
        var received = Assert.Single(await storeB.GetAttachmentsAsync(conversation));
        Assert.Equal("Hồ sơ", received.FileName);
        var opened = PreparedPath(b, received);
        await TestNode.WaitUntilAsync(() => Task.FromResult(Directory.Exists(opened) && Directory.Exists(PreparedPath(a, folder))));
        Assert.Equal("Hồ sơ", Path.GetFileName(opened));
        Assert.Equal(bytes, await FrameworkIO.ReadBytesAsync(Path.Combine(opened, "Phụ lục", "Kết quả.bin")));
        Assert.Equal("Bản gốc", await FrameworkIO.ReadTextAsync(Path.Combine(opened, "Báo cáo.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(opened, "Phụ lục", "Rỗng")));
        await a.Host.StopAsync();
        Assert.Equal(opened, await Files(b).PrepareOpenAsync(received));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData("x:stream")]
    [InlineData("AUX.txt")]
    [InlineData("missing/child.txt")]
    public void FolderManifestRejectsInvalidWindowsPaths(string path)
    {
        var manifest = new FolderManifest("Folder", [], [new(path, 0, new string('a', 64))]);
        Assert.Throws<InvalidDataException>(() => PeerFileTransferService.ValidateManifest(manifest, "Folder"));
    }
    [Fact]
    public async Task OpenLocalFilePreservesNameAndUserEditsWithoutChangingSharedBlob()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, "Báo cáo tháng 9.txt"); await FrameworkIO.WriteTextAsync(source, "Nội dung gốc");
        var file = await Files(node).ImportLocalAsync(source); var path = await Files(node).PrepareOpenAsync(file);
        Assert.Equal(file.FileName, Path.GetFileName(path)); Assert.Equal(".txt", Path.GetExtension(path));
        Assert.StartsWith(Path.Combine(node.Options.DataDirectory, "Attachments", "Open"), path);
        Assert.Equal("Nội dung gốc", await FrameworkIO.ReadTextAsync(path));
        await FrameworkIO.WriteTextAsync(path, "Đã chỉnh sửa trên máy này");
        Assert.Equal(path, await Files(node).PrepareOpenAsync(file)); Assert.Equal("Đã chỉnh sửa trên máy này", await FrameworkIO.ReadTextAsync(path));
        Assert.Equal("Nội dung gốc", await FrameworkIO.ReadTextAsync((await Files(node).GetLocalPathAsync(file.Sha256))!));
    }
    [Fact]
    public async Task OpenRemoteFileAutomaticallyDownloadsAndCanReopenWithoutSource()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "Tài liệu từ xa.txt"); await FrameworkIO.WriteTextAsync(source, "Tải rồi mở một lần bấm");
        var file = await Files(a).ImportLocalAsync(source); Assert.Null(await Files(b).GetLocalPathAsync(file.Sha256));
        var path = await Files(b).PrepareOpenAsync(file); Assert.Equal(file.FileName, Path.GetFileName(path));
        Assert.Equal("Tải rồi mở một lần bấm", await FrameworkIO.ReadTextAsync(path)); Assert.NotNull(await Files(b).GetLocalPathAsync(file.Sha256));
        await a.Host.StopAsync(); Assert.Equal(path, await Files(b).PrepareOpenAsync(file));
    }
    [Fact]
    public async Task OpenCopiesSeparateIdenticallyNamedAttachmentsAndFlattenUnsafeNames()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, "report.txt"); await FrameworkIO.WriteTextAsync(source, "First"); var first = await Files(node).ImportLocalAsync(source);
        await FrameworkIO.WriteTextAsync(source, "Second"); var second = await Files(node).ImportLocalAsync(source);
        var firstPath = await Files(node).PrepareOpenAsync(first); var secondPath = await Files(node).PrepareOpenAsync(second);
        Assert.NotEqual(firstPath, secondPath); Assert.Equal("First", await FrameworkIO.ReadTextAsync(firstPath)); Assert.Equal("Second", await FrameworkIO.ReadTextAsync(secondPath));
        var unsafeName = first with { Id = Guid.NewGuid(), FileName = "../../report.txt" };
        var safePath = await Files(node).PrepareOpenAsync(unsafeName);
        Assert.Equal(Path.Combine(node.Options.DataDirectory, "Attachments", "Open", unsafeName.Id.ToString("N")), Path.GetDirectoryName(Path.GetFullPath(safePath)));
        Assert.Equal(".txt", Path.GetExtension(safePath));
    }
    [Fact]
    public async Task AttachmentsPrepareAutomaticallyOnSendAndReceiveAndCachedPeerCanServeThem()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var c = new TestNode(cluster, secret, "C");
        await a.InitializeAsync(true); await b.InitializeAsync(true); await c.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1"); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "tài liệu.txt"); var bytes = FrameworkIO.RandomBytes(2 * 1024 * 1024 + 17); await FrameworkIO.WriteBytesAsync(source, bytes);
        var file = await Files(a).ImportLocalAsync(source); var conversation = await a.Store.OpenDirectAsync((await b.Store.GetIdentityAsync()).UserId);
        var storeA = a.Host.Services.GetRequiredService<IDistributedStore>(); var storeB = b.Host.Services.GetRequiredService<IDistributedStore>();
        await storeA.SendToConversationAsync(conversation, "Tệp kiểm thử", attachments: [file]);
        await TestNode.WaitUntilAsync(async () => (await storeB.GetAttachmentsAsync()).Length == 1);
        var received = Assert.Single(await storeB.GetAttachmentsAsync()); Assert.Equal(file.Sha256, received.Sha256);
        await TestNode.WaitUntilAsync(() => Task.FromResult(File.Exists(PreparedPath(b, received)) && File.Exists(PreparedPath(a, file))));
        Assert.Equal(bytes, await FrameworkIO.ReadBytesAsync(PreparedPath(b, received)));
        var progress = new CaptureProgress(); var path = await Files(b).DownloadAsync(received, progress);
        Assert.Equal(bytes, await FrameworkIO.ReadBytesAsync(path)); Assert.Equal(bytes.Length, progress.Last!.Completed);
        // A sender with no intact copy must not prevent another cached peer from serving the original.
        await FrameworkIO.WriteTextAsync(source, "changed source");
        await FrameworkIO.WriteTextAsync(PreparedPath(a, file), "Sender's working copy changed");
        await FrameworkIO.WriteTextAsync((await Files(a).GetLocalPathAsync(file.Sha256))!, "damaged cache");
        Assert.False((await a.Host.Services.GetRequiredService<PeerFileTransferService>().HasFileAsync(file.Sha256, default)).Available);
        await FrameworkIO.WriteTextAsync(await Files(b).PrepareOpenAsync(received), "Receiver's local edits");
        // B can forward the file key to C in a new encrypted conversation.
        await c.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var recipient = await c.Store.GetNodeInfoAsync();
        await b.Store.RememberPeerAsync(recipient, "127.0.0.1");
        var forwarded = await b.Store.OpenDirectAsync(recipient.User.Id);
        await storeB.SendToConversationAsync(forwarded, "Chuyển tiếp tệp", attachments: [received with { Id = Guid.NewGuid() }]);
        var storeC = c.Host.Services.GetRequiredService<IDistributedStore>();
        await storeC.ImportEventsAsync(await storeB.GetEventRangeAsync(new((await b.Store.GetIdentityAsync()).NodeId, 0)));
        var copy = await Files(c).DownloadAsync(Assert.Single(await storeC.GetAttachmentsAsync(forwarded)));
        Assert.Equal(bytes, await FrameworkIO.ReadBytesAsync(copy));
    }

    [Fact]
    public async Task ForwardedFileUsesCachedOriginalAfterSenderGoesOffline()
    {
        await AssertForwardedAttachmentAvailableAsync(folder: false, prepared: true);
    }

    [Fact]
    public async Task ForwardedFolderUsesCachedOriginalAfterSenderGoesOffline()
    {
        await AssertForwardedAttachmentAvailableAsync(folder: true, prepared: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardingWaitsForFileAndFolderDataAndDoesNotSendWhenUnavailable(bool folder)
    {
        await AssertForwardedAttachmentAvailableAsync(folder, prepared: false);
    }

    private static async Task AssertForwardedAttachmentAvailableAsync(bool folder, bool prepared)
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "Original");
        await using var b = new TestNode(cluster, secret, "Forwarder");
        await using var c = new TestNode(cluster, secret, "Recipient");
        await a.InitializeAsync(true); await b.InitializeAsync(); await c.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        await b.Store.RememberPeerAsync(await c.Store.GetNodeInfoAsync(), "127.0.0.1");
        await c.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");

        var cached = Path.Combine(b.Options.DataDirectory, "cached.txt");
        await FrameworkIO.WriteTextAsync(cached, "Bản gốc cần chuyển tiếp");
        if (prepared) await Files(b).ImportLocalAsync(cached);
        var source = Path.Combine(Path.GetDirectoryName(a.Options.DataDirectory)!, Guid.NewGuid().ToString("N"), "Hồ sơ chuyển tiếp");
        Directory.CreateDirectory(Path.Combine(source, "Tài liệu", "Rỗng"));
        var sourceFile = Path.Combine(source, "Tài liệu", "Báo cáo.txt");
        await FrameworkIO.WriteTextAsync(sourceFile, "Bản gốc cần chuyển tiếp");
        await FrameworkIO.WriteTextAsync(Path.Combine(source, "Bản sao.txt"), "Bản gốc cần chuyển tiếp");
        var file = folder ? await Files(a).ImportFolderAsync(source) : await Files(a).ImportLocalAsync(sourceFile);
        var originalConversation = await a.Store.OpenDirectAsync((await b.Store.GetIdentityAsync()).UserId);
        var storeA = a.Host.Services.GetRequiredService<IDistributedStore>();
        var storeB = b.Host.Services.GetRequiredService<IDistributedStore>();
        var original = await storeA.SendToConversationAsync(originalConversation, "", attachments: [file]);
        await storeB.ImportEventsAsync(await storeA.GetEventRangeAsync(new((await a.Store.GetIdentityAsync()).NodeId, 0)));
        var received = Assert.Single(await storeB.GetAttachmentsAsync(originalConversation));
        if (prepared)
        {
            var originalCopy = await Files(b).PrepareOpenAsync(received);
            var originalDocument = folder ? Path.Combine(originalCopy, "Tài liệu", "Báo cáo.txt") : originalCopy;
            await FrameworkIO.WriteTextAsync(originalDocument, "Chỉnh sửa cục bộ của người chuyển tiếp");
            await a.Host.StopAsync();
        }
        var destination = await b.Store.OpenDirectAsync((await c.Store.GetIdentityAsync()).UserId);
        var chat = b.Host.Services.GetRequiredService<IChatService>();
        if (!prepared)
        {
            var sourceNode = (await a.Store.GetIdentityAsync()).NodeId;
            var faults = b.Host.Services.GetRequiredService<FaultInjection>();
            lock (faults.BlockedNodes) faults.BlockedNodes.Add(sourceNode);
            await Assert.ThrowsAsync<IOException>(() => chat.ForwardAsync(original.Id, destination));
            Assert.Empty(await b.Store.GetMessagesAsync(destination));
            lock (faults.BlockedNodes) faults.BlockedNodes.Clear();
            using var cancel = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.ForwardAsync(original.Id, destination,
                new CaptureProgress(p => { if (p.Phase == AttachmentPhase.Downloading && p.Completed > 0) cancel.Cancel(); }), cancel.Token));
            Assert.Empty(await b.Store.GetMessagesAsync(destination));
        }
        var forwarded = await chat.ForwardAsync(original.Id, destination);
        await b.Host.StartAsync();
        if (!prepared) await a.Host.StopAsync();

        var storeC = c.Host.Services.GetRequiredService<IDistributedStore>();
        await storeC.ImportEventsAsync(await storeB.GetEventRangeAsync(new((await b.Store.GetIdentityAsync()).NodeId, 0)));
        var copy = Assert.Single(await storeC.GetAttachmentsAsync(destination));
        Assert.NotEqual(received.Id, copy.Id);
        Assert.NotEqual(received.FileKey, copy.FileKey);
        Assert.Equal(original.Id, (await storeC.GetMessageDetailsAsync(forwarded.Id)).Message.ForwardedFromMessageId);
        var opened = await Files(c).PrepareOpenAsync(copy);
        var document = folder ? Path.Combine(opened, "Tài liệu", "Báo cáo.txt") : opened;
        Assert.Equal("Bản gốc cần chuyển tiếp", await FrameworkIO.ReadTextAsync(document));
        if (folder)
        {
            Assert.Equal("Bản gốc cần chuyển tiếp", await FrameworkIO.ReadTextAsync(Path.Combine(opened, "Bản sao.txt")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(opened, "Tài liệu", "Rỗng")));
        }
    }

    [Fact]
    public async Task MissingAttachmentsRetryAfterRestartAndPrepareWholeFolderAndLargeImage()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");

        var source = Path.Combine(Path.GetDirectoryName(a.Options.DataDirectory)!, Guid.NewGuid().ToString("N"), "Hồ sơ tự tải");
        Directory.CreateDirectory(Path.Combine(source, "Phụ lục", "Rỗng"));
        await FrameworkIO.WriteTextAsync(Path.Combine(source, "Phụ lục", "Báo cáo.txt"), "Bản gốc trong thư mục");
        var folder = await Files(a).ImportFolderAsync(source);
        var imagePath = Path.Combine(a.Options.DataDirectory, "Ảnh lớn.bmp");
        using (var bitmap = new System.Drawing.Bitmap(3200, 2400)) bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Bmp);
        var image = await Files(a).ImportLocalAsync(imagePath);
        Assert.True(image.FileSize > 20L * 1024 * 1024);

        var conversation = await a.Store.OpenDirectAsync((await b.Store.GetIdentityAsync()).UserId);
        var storeA = a.Host.Services.GetRequiredService<IDistributedStore>();
        var storeB = b.Host.Services.GetRequiredService<IDistributedStore>();
        await storeA.SendToConversationAsync(conversation, "Tự tải cả thư mục và ảnh ngoài vùng xem", attachments: [folder, image]);
        await storeB.ImportEventsAsync(await storeA.GetEventRangeAsync(new((await a.Store.GetIdentityAsync()).NodeId, 0)));
        await b.Host.StartAsync();
        await TestNode.WaitUntilAsync(() => Task.FromResult(Directory.Exists(Path.Combine(b.Options.DataDirectory, "Attachments"))));
        await b.Host.StopAsync();
        Assert.False(Directory.Exists(PreparedPath(b, folder)));
        Assert.False(File.Exists(PreparedPath(b, image)));
        Assert.Empty(Directory.GetFiles(Path.Combine(b.Options.DataDirectory, "Attachments"), "*.part", SearchOption.AllDirectories));

        await using var restarted = new TestNode(cluster, secret, "B", b.Options.DataDirectory);
        await restarted.InitializeAsync(true);
        await a.Store.RememberPeerAsync(await restarted.Store.GetNodeInfoAsync(), "127.0.0.1");
        await a.Host.StartAsync();
        var openedFolder = PreparedPath(restarted, folder);
        var openedImage = PreparedPath(restarted, image);
        await TestNode.WaitUntilAsync(() => Task.FromResult(Directory.Exists(openedFolder) && File.Exists(openedImage)), 25);
        await a.Host.StopAsync();
        Assert.Equal("Bản gốc trong thư mục", await FrameworkIO.ReadTextAsync(Path.Combine(openedFolder, "Phụ lục", "Báo cáo.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(openedFolder, "Phụ lục", "Rỗng")));
        Assert.Equal(image.FileSize, new FileInfo(openedImage).Length);
        Assert.NotNull(await Files(restarted).GetLocalPathAsync(image.Sha256));
        Assert.Equal(openedFolder, await Files(restarted).PrepareOpenAsync(folder));
        Assert.Equal(openedImage, await Files(restarted).PrepareOpenAsync(image));
    }

    [Fact]
    public async Task AutomaticDownloadsSkipRecalledFilesAndPrivateCiphertextOnRelay()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        await using var relay = new TestNode(cluster, secret, "Relay");
        await a.InitializeAsync(); await b.InitializeAsync(); await relay.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        await relay.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");

        var source = Path.Combine(a.Options.DataDirectory, "Document.txt");
        await FrameworkIO.WriteTextAsync(source, "Giữ lại");
        var kept = await Files(a).ImportLocalAsync(source);
        await FrameworkIO.WriteTextAsync(source, "Đã thu hồi");
        var recalled = await Files(a).ImportLocalAsync(source);
        var storeA = a.Host.Services.GetRequiredService<IDistributedStore>();
        var conversation = await a.Store.OpenDirectAsync((await b.Store.GetIdentityAsync()).UserId);
        await storeA.SendToConversationAsync(conversation, "Tệp được phép nhận", attachments: [kept]);
        var message = await storeA.SendToConversationAsync(conversation, "Thu hồi trước khi tải", attachments: [recalled]);
        await storeA.RecallMessageAsync(message.Id);
        var events = await storeA.GetEventRangeAsync(new((await a.Store.GetIdentityAsync()).NodeId, 0));
        foreach (var node in new[] { b, relay })
            await node.Host.Services.GetRequiredService<IDistributedStore>().ImportEventsAsync(events);
        foreach (var node in new[] { a, b, relay }) await node.Host.StartAsync();

        await TestNode.WaitUntilAsync(() => Task.FromResult(File.Exists(PreparedPath(b, kept))));
        Assert.Equal("Giữ lại", await FrameworkIO.ReadTextAsync(PreparedPath(b, kept)));
        Assert.False(File.Exists(PreparedPath(b, recalled)));
        Assert.Null(await Files(b).GetLocalPathAsync(recalled.Sha256));
        Assert.Empty(await relay.Host.Services.GetRequiredService<IDistributedStore>().GetAttachmentsAsync());
        Assert.False(Directory.Exists(Path.Combine(relay.Options.DataDirectory, "Attachments")));
    }
    [Fact]
    public async Task CorruptSourceIsRejectedAndTemporaryFileIsRemoved()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "source.txt"); await FrameworkIO.WriteTextAsync(source, "original"); var metadata = await Files(a).ImportLocalAsync(source);
        await FrameworkIO.WriteTextAsync((await Files(a).GetLocalPathAsync(metadata.Sha256))!, "tampered");
        await FrameworkIO.WriteTextAsync(source, "modified");
        Assert.False((await a.Host.Services.GetRequiredService<PeerFileTransferService>().HasFileAsync(metadata.Sha256, default)).Available);
        var error = await Assert.ThrowsAsync<IOException>(() => Files(b).PrepareOpenAsync(metadata));
        Assert.Contains("gửi lại tệp", error.Message);
        Assert.Null(await Files(b).GetLocalPathAsync(metadata.Sha256)); Assert.Empty(Directory.GetFiles(Path.Combine(b.Options.DataDirectory, "Attachments"), "*.part", SearchOption.AllDirectories));
        // Reimport repairs a damaged content-addressed local copy.
        await FrameworkIO.WriteTextAsync(source, "original");
        await Files(a).ImportLocalAsync(source); Assert.Equal("original", await FrameworkIO.ReadTextAsync(await Files(b).DownloadAsync(metadata)));
    }
    [Theory]
    [InlineData("modified")]
    [InlineData("Nội dung mới dài hơn sau khi gửi")]
    [InlineData(null)]
    public async Task ChangingOrDeletingOriginalAfterSendDoesNotAffectDelayedRecipient(string? replacement)
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "report.txt"); await FrameworkIO.WriteTextAsync(source, "original");
        var file = await Files(a).ImportLocalAsync(source);
        if (replacement is null) File.Delete(source); else await FrameworkIO.WriteTextAsync(source, replacement);
        var senderCopy = await Files(a).PrepareOpenAsync(file); await FrameworkIO.WriteTextAsync(senderCopy, "Sender edited in Word/Excel");
        var received = await Files(b).PrepareOpenAsync(file);
        Assert.Equal("original", await FrameworkIO.ReadTextAsync(received));
        await FrameworkIO.WriteTextAsync(received, "Receiver edits");
        Assert.Equal("original", await FrameworkIO.ReadTextAsync(await Files(b).DownloadAsync(file)));
        Assert.Equal("Receiver edits", await FrameworkIO.ReadTextAsync(await Files(b).PrepareOpenAsync(file)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedOrMissingBlobAutomaticallyRecoversFromUnchangedSource(bool delete)
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync();
        var source = Path.Combine(a.Options.DataDirectory, "report.txt"); await FrameworkIO.WriteTextAsync(source, "original");
        var file = await Files(a).ImportLocalAsync(source); var blob = (await Files(a).GetLocalPathAsync(file.Sha256))!;
        if (delete) File.Delete(blob); else await FrameworkIO.WriteTextAsync(blob, "tampered");
        // A fresh host must recover using persisted source information, without reimporting.
        await using var restarted = new TestNode(cluster, secret, "A", a.Options.DataDirectory);
        await restarted.InitializeAsync(true); await b.Store.RememberPeerAsync(await restarted.Store.GetNodeInfoAsync(), "127.0.0.1");
        Assert.Equal("original", await FrameworkIO.ReadTextAsync(await Files(b).PrepareOpenAsync(file)));
        Assert.Equal("original", await FrameworkIO.ReadTextAsync(blob));
        Assert.Empty(Directory.GetFiles(Path.Combine(a.Options.DataDirectory, "Attachments"), "*.part", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAttachmentRecoversOnlyFromUnchangedOpenCopy(bool edited)
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, "report.txt"); await FrameworkIO.WriteTextAsync(source, "original");
        var file = await Files(node).ImportLocalAsync(source);
        await using var other = new TestNode(node.Options.ClusterId, node.Options.ClusterSecret, "B"); await other.InitializeAsync();
        await node.Store.RememberPeerAsync(await other.Store.GetNodeInfoAsync(), "127.0.0.1");
        var conversation = await node.Store.OpenDirectAsync((await other.Store.GetIdentityAsync()).UserId);
        await node.Host.Services.GetRequiredService<IDistributedStore>().SendToConversationAsync(conversation, "File", attachments: [file]);
        var open = await Files(node).PrepareOpenAsync(file); var blob = (await Files(node).GetLocalPathAsync(file.Sha256))!;
        File.Delete(blob + ".source.json"); File.Delete(source);
        await FrameworkIO.WriteTextAsync(blob, "tampered");
        if (edited) await FrameworkIO.WriteTextAsync(open, "modified");
        var availability = await node.Host.Services.GetRequiredService<PeerFileTransferService>().HasFileAsync(file.Sha256, default);
        Assert.Equal(!edited, availability.Available);
        if (!edited) Assert.Equal("original", await FrameworkIO.ReadTextAsync(await Files(node).DownloadAsync(file)));
        else Assert.Null(await Files(node).GetLocalPathAsync(file.Sha256));
        Assert.Equal(edited ? "modified" : "original", await FrameworkIO.ReadTextAsync(open));
    }
    [Fact]
    public async Task VerifiedSourceCannotBeEditedUntilTransferHandleIsClosed()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, "report.txt"); await FrameworkIO.WriteTextAsync(source, "original");
        var file = await Files(node).ImportLocalAsync(source); var blob = (await Files(node).GetLocalPathAsync(file.Sha256))!;
        using (var stream = await node.Host.Services.GetRequiredService<PeerFileTransferService>().OpenVerifiedLocalAsync(file.Sha256, default))
        {
            Assert.NotNull(stream); Assert.Equal(0, stream.Position);
            Assert.Throws<IOException>(() => { using var writer = new FileStream(blob, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Assert.Equal(file.Sha256, (await Files(node).ImportLocalAsync(source)).Sha256);
        }
        await FrameworkIO.WriteTextAsync(blob, "tampered");
    }
    [Fact]
    public async Task CancellationDuringStreamingRemovesPartialDownload()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(true); await b.InitializeAsync(); await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        var source = Path.Combine(a.Options.DataDirectory, "large.bin"); await FrameworkIO.WriteBytesAsync(source, FrameworkIO.RandomBytes(4 * 1024 * 1024)); var metadata = await Files(a).ImportLocalAsync(source);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Files(b).PrepareOpenAsync(metadata, new CaptureProgress(p => { if (p.Phase == AttachmentPhase.Downloading && p.Completed > 0) cancel.Cancel(); }), cancel.Token));
        Assert.Null(await Files(b).GetLocalPathAsync(metadata.Sha256)); Assert.Empty(Directory.GetFiles(Path.Combine(b.Options.DataDirectory, "Attachments"), "*.part", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task HashPathTraversalAndMissingSourceAreRejected()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Files(node).GetLocalPathAsync("../../cluster.json"));
        var metadata = new AttachmentDto(Guid.NewGuid(), Guid.NewGuid(), "missing.txt", "text/plain", 10, new string('a', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => Files(node).DownloadAsync(metadata));
        await Assert.ThrowsAsync<IOException>(() => Files(node).DownloadAsync(metadata with { FileKey = Convert.ToBase64String(FrameworkIO.RandomBytes(32)) }));
    }
    private sealed class CaptureProgress(Action<FileProgress>? action = null) : IProgress<FileProgress>
    {
        public FileProgress? Last { get; private set; }
        public void Report(FileProgress value) { Last = value; action?.Invoke(value); }
    }
}
