using LanChat.Application;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class LocalAttachmentTests
{
    private static PeerFileTransferService Files(TestNode node) => node.Host.Services.GetRequiredService<PeerFileTransferService>();
    private static string Destination() { var p = Path.Combine(Path.GetTempPath(), "LanChat.SavedTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private static async Task<AttachmentDto> Import(TestNode node, string name = "report.txt", string content = "original")
    { var p = Path.Combine(node.Options.DataDirectory, name); await FrameworkIO.WriteTextAsync(p, content); return await Files(node).ImportLocalAsync(p); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstSaveMovesEditedWorkingCopyAndRestartOpensSavedLocation(bool copy)
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); service.ForceLocalCopy = copy;
        var file = await Import(node); var working = await service.PrepareOpenAsync(file);
        await FrameworkIO.WriteTextAsync(working, "local edits");
        var target = Path.Combine(Destination(), "saved.txt");
        Assert.Equal(target, await service.SaveLocalAsync(file, target)); Assert.False(File.Exists(working));
        Assert.Equal("local edits", await FrameworkIO.ReadTextAsync(target));
        Assert.Equal("original", await FrameworkIO.ReadTextAsync(Path.Combine(node.Options.DataDirectory, "report.txt")));
        Assert.Equal("original", await FrameworkIO.ReadTextAsync(await service.DownloadAsync(file)));
        await using var restarted = new TestNode(node.Options.ClusterId, node.Options.ClusterSecret, "A", node.Options.DataDirectory);
        await restarted.InitializeAsync();
        Assert.Equal(target, await Files(restarted).PrepareOpenAsync(file));
        Assert.Equal(new LocalAttachmentLocation(target, true, true), await Files(restarted).GetLocalLocationAsync(file));
        Assert.True(Files(restarted).IsPrepared(file)); Assert.False(File.Exists(working));
    }

    [Fact]
    public async Task LaterSaveCopiesLatestEditsKeepsPreviousAndSameDestinationDoesNothing()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var file = await Import(node); var first = Path.Combine(Destination(), "one.txt");
        await service.SaveLocalAsync(file, first); await FrameworkIO.WriteTextAsync(first, "edited outside");
        var second = Path.Combine(Destination(), "two.txt"); await service.SaveLocalAsync(file, second);
        Assert.Equal("edited outside", await FrameworkIO.ReadTextAsync(first)); Assert.Equal("edited outside", await FrameworkIO.ReadTextAsync(second));
        Assert.Equal(second, await service.PrepareOpenAsync(file)); Assert.Equal(second, await service.SaveLocalAsync(file, second));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(second)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderSaveKeepsEditedTreeNewFilesAndEmptyDirectories(bool copy)
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(Destination(), "Folder"); Directory.CreateDirectory(Path.Combine(source, "Empty"));
        await FrameworkIO.WriteTextAsync(Path.Combine(source, "a.txt"), "original");
        var service = Files(node); service.ForceLocalCopy = copy; var file = await service.ImportFolderAsync(source);
        var working = await service.PrepareOpenAsync(file); await FrameworkIO.WriteTextAsync(Path.Combine(working, "a.txt"), "edits");
        await FrameworkIO.WriteTextAsync(Path.Combine(working, "new.txt"), "new");
        var target = Path.Combine(Destination(), "Folder"); await service.SaveLocalAsync(file, target);
        Assert.False(Directory.Exists(working)); Assert.True(Directory.Exists(Path.Combine(target, "Empty")));
        Assert.Equal("edits", await FrameworkIO.ReadTextAsync(Path.Combine(target, "a.txt")));
        Assert.Equal("new", await FrameworkIO.ReadTextAsync(Path.Combine(target, "new.txt")));
        Assert.Equal("original", await FrameworkIO.ReadTextAsync(Path.Combine(source, "a.txt")));
        var later = Path.Combine(Destination(), "Folder"); await service.SaveLocalAsync(file, later);
        Assert.True(Directory.Exists(target)); Assert.Equal(later, await service.PrepareOpenAsync(file));
    }

    [Theory]
    [InlineData("intent", false)]
    [InlineData("published", true)]
    public async Task MoveJournalRecoversAfterInterruptedSave(string checkpoint, bool moved)
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var file = await Import(node); var working = await service.PrepareOpenAsync(file);
        await FrameworkIO.WriteTextAsync(working, "edits"); var target = Path.Combine(Destination(), "saved.txt");
        service.LocalSaveCheckpoint = step => { if (step == checkpoint) throw new IOException("simulated interruption"); };
        await Assert.ThrowsAsync<IOException>(() => service.SaveLocalAsync(file, target));
        await using var restart = new TestNode(node.Options.ClusterId, node.Options.ClusterSecret, "A", node.Options.DataDirectory); await restart.InitializeAsync();
        var opened = await Files(restart).PrepareOpenAsync(file); Assert.Equal(moved ? target : working, opened);
        Assert.Equal("edits", await FrameworkIO.ReadTextAsync(opened));
    }

    [Fact]
    public async Task MissingExternalLocationIsNotSilentlyRecreatedAndCanRelinkOrRestoreOriginal()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var file = await Import(node); var working = await service.PrepareOpenAsync(file);
        var target = Path.Combine(Destination(), "saved.txt"); await service.SaveLocalAsync(file, target); File.Delete(target);
        Assert.True(service.IsPrepared(file)); Assert.False((await service.GetLocalLocationAsync(file)).Exists);
        await Assert.ThrowsAsync<LocalAttachmentMissingException>(() => service.PrepareOpenAsync(file)); Assert.False(File.Exists(working));
        var located = Path.Combine(Destination(), "found.txt"); await FrameworkIO.WriteTextAsync(located, "found edits");
        await service.RelinkLocalAsync(file, located); Assert.Equal(located, await service.PrepareOpenAsync(file));
        await Assert.ThrowsAsync<IOException>(() => service.RestoreLocalAsync(file)); File.Delete(located);
        Assert.Equal(working, await service.RestoreLocalAsync(file)); Assert.Equal("original", await FrameworkIO.ReadTextAsync(working));
        Assert.False((await service.GetLocalLocationAsync(file)).UserSelected);
    }

    [Fact]
    public async Task LockedSourceAndMetadataFailureKeepOldLocationAndAllowRetry()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var file = await Import(node); var working = await service.PrepareOpenAsync(file); var target = Path.Combine(Destination(), "saved.txt");
        using (var locked = new FileStream(working, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => service.SaveLocalAsync(file, target));
        Assert.Equal(working, await service.PrepareOpenAsync(file)); Assert.False(File.Exists(target));
        var metadata = Path.Combine(node.Options.DataDirectory, "Attachments", "Locations"); await FrameworkIO.WriteTextAsync(metadata, "blocks directory");
        await Assert.ThrowsAsync<IOException>(() => service.SaveLocalAsync(file, target)); Assert.True(File.Exists(working)); Assert.False(File.Exists(target));
        File.Delete(metadata); await service.SaveLocalAsync(file, target); Assert.Equal(target, await service.PrepareOpenAsync(file));
    }

    [Fact]
    public async Task CancelCopyPreservesWorkingCopyAndExistingTargetAndRemovesTemporaryFiles()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); service.ForceLocalCopy = true; var source = Path.Combine(node.Options.DataDirectory, "large.bin");
        await FrameworkIO.WriteBytesAsync(source, FrameworkIO.RandomBytes(2 * 1024 * 1024)); var file = await service.ImportLocalAsync(source);
        var working = await service.PrepareOpenAsync(file); var target = Path.Combine(Destination(), "old.bin"); await FrameworkIO.WriteTextAsync(target, "old");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveLocalAsync(file, target, true, new ImmediateProgress<FileProgress>(_ => cancel.Cancel()), cancel.Token));
        Assert.Equal("old", await FrameworkIO.ReadTextAsync(target)); Assert.Equal(working, await service.PrepareOpenAsync(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task BatchContinuesAfterItemFailureRenamesCollisionsAndSkipsItsOwnPath()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var message = Guid.NewGuid(); var first = (await Import(node)) with { MessageId = message };
        var second = (await Import(node, content: "second")) with { MessageId = message };
        var third = (await Import(node, "picture.png", "image working contents")) with { MessageId = message, MimeType = "image/png" };
        var bad = first with { Id = Guid.NewGuid(), Sha256 = new string('f', 64), FileKey = Convert.ToBase64String(FrameworkIO.RandomBytes(32)) };
        var directory = Destination(); await FrameworkIO.WriteTextAsync(Path.Combine(directory, "report.txt"), "existing");
        var batch = new LocalAttachmentBatchSaver(service); var results = await batch.SaveAsync([first, bad, second, third], directory);
        Assert.NotNull(results[1].Error); Assert.Equal(3, results.Count(r => r.Path is not null));
        Assert.Equal(Path.Combine(directory, "report (2).txt"), results[0].Path); Assert.Equal(Path.Combine(directory, "report (3).txt"), results[2].Path);
        var again = await batch.SaveAsync([first, second, third], directory); Assert.All(again, r => Assert.Null(r.Error)); Assert.Equal(4, Directory.GetFiles(directory).Length);
        await Assert.ThrowsAsync<ArgumentException>(() => batch.SaveAsync([first, second with { MessageId = Guid.NewGuid() }], directory));
    }

    [Fact]
    public async Task CancelBatchRetainsCompletedItemsAndReportsRemainingAsCanceled()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var message = Guid.NewGuid(); var first = (await Import(node)) with { MessageId = message };
        var second = first with { Id = Guid.NewGuid() }; using var cancel = new CancellationTokenSource();
        service.LocalSaveCheckpoint = step => { if (step == "mapped") cancel.Cancel(); };
        var results = await new LocalAttachmentBatchSaver(service).SaveAsync([first, second], Destination(), ct: cancel.Token);
        Assert.True(File.Exists(results[0].Path)); Assert.True(results[1].Canceled); Assert.Equal(results[0].Path, await service.PrepareOpenAsync(first));
    }

    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

    [Fact]
    public async Task ConcurrentOpenWaitsForSaveAndUsesCommittedLocation()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var file = await Import(node); var working = await service.PrepareOpenAsync(file);
        var target = Path.Combine(Destination(), "saved.txt");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        service.LocalSaveCheckpoint = step => { if (step == "intent") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); } };
        var save = Task.Run(() => service.SaveLocalAsync(file, target));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); var open = service.PrepareOpenAsync(file); Assert.False(open.IsCompleted);
        release.Set(); Assert.Equal(target, await save); Assert.Equal(target, await open); Assert.False(File.Exists(working));
    }

    [Fact]
    public async Task CopyMetadataFailureRetainsOldPathAndRetryDoesNotLoseEdits()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); service.ForceLocalCopy = true; var file = await Import(node); var working = await service.PrepareOpenAsync(file);
        await FrameworkIO.WriteTextAsync(working, "edits"); var target = Path.Combine(Destination(), "saved.txt");
        var metadataDirectory = Path.Combine(node.Options.DataDirectory, "Attachments", "Locations");
        service.LocalSaveCheckpoint = step => { if (step == "published") File.WriteAllText(metadataDirectory, "blocks metadata"); };
        await Assert.ThrowsAsync<IOException>(() => service.SaveLocalAsync(file, target));
        Assert.Equal(working, await service.PrepareOpenAsync(file)); Assert.True(File.Exists(target)); Assert.Equal("edits", File.ReadAllText(working));
        File.Delete(metadataDirectory); service.LocalSaveCheckpoint = null;
        await service.SaveLocalAsync(file, target, true); Assert.Equal(target, await service.PrepareOpenAsync(file)); Assert.False(File.Exists(working));
    }

    [Fact]
    public async Task BatchIncludesFoldersEmptyDirectoriesAndImagesWithCollidingNames()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var message = Guid.NewGuid(); var root = Path.Combine(Destination(), "report.txt"); Directory.CreateDirectory(Path.Combine(root, "Empty"));
        var folder = (await service.ImportFolderAsync(root)) with { MessageId = message };
        var document = (await Import(node)) with { MessageId = message }; var image = (await Import(node, "report.png")) with { MessageId = message, MimeType = "image/png" };
        var target = Destination(); var results = await new LocalAttachmentBatchSaver(service).SaveAsync([folder, document, image], target);
        Assert.All(results, r => Assert.Null(r.Error)); Assert.Equal(Path.Combine(target, "report (2).txt"), results[1].Path);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(results[0].Path!, "Empty")));
        foreach (var result in results) Assert.Equal(result.Path, await service.PrepareOpenAsync(result.File));
    }

    [Fact]
    public async Task BackgroundDownloadDoesNotRecreateWorkingCopyAfterExternalFileDisappears()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        await using var other = new TestNode(node.Options.ClusterId, node.Options.ClusterSecret, "B"); await other.InitializeAsync();
        await node.Store.RememberPeerAsync(await other.Store.GetNodeInfoAsync(), "127.0.0.1");
        var service = Files(node); var file = await Import(node); var conversation = await node.Store.OpenDirectAsync((await other.Store.GetIdentityAsync()).UserId);
        await node.Host.Services.GetRequiredService<IDistributedStore>().SendToConversationAsync(conversation, "File", attachments: [file]);
        var working = await service.PrepareOpenAsync(file); var target = Path.Combine(Destination(), "saved.txt"); await service.SaveLocalAsync(file, target); File.Delete(target);
        await node.Host.StartAsync(); await Task.Delay(1000); Assert.False(File.Exists(working));
        await Assert.ThrowsAsync<LocalAttachmentMissingException>(() => service.PrepareOpenAsync(file));
        Assert.Equal(target, (await service.GetLocalLocationAsync(file)).Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataLockedAtCommitNeverReportsSuccessAndRecoversActualLocation(bool subsequentSave)
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var file = await Import(node); var working = await service.PrepareOpenAsync(file);
        if (subsequentSave) { working = Path.Combine(Destination(), "first.txt"); await service.SaveLocalAsync(file, working); }
        File.WriteAllText(working, "edits"); var target = Path.Combine(Destination(), "next.txt");
        var metadata = Path.Combine(node.Options.DataDirectory, "Attachments", "Locations", file.Id.ToString("N") + ".json");
        FileStream? locked = null;
        service.LocalSaveCheckpoint = step => { if (step == "published") locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read); };
        try { await Assert.ThrowsAsync<IOException>(() => service.SaveLocalAsync(file, target)); }
        finally { locked?.Dispose(); service.LocalSaveCheckpoint = null; }
        Assert.Equal("edits", File.ReadAllText(target));
        Assert.Equal(subsequentSave ? working : target, await service.PrepareOpenAsync(file));
        Assert.Equal(subsequentSave, File.Exists(working));
        if (subsequentSave) { await service.SaveLocalAsync(file, target, true); Assert.Equal(target, await service.PrepareOpenAsync(file)); Assert.True(File.Exists(working)); }
    }

    [Fact]
    public async Task CancelAfterMoveIntentKeepsSourceAndAllowsRecovery()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = Files(node); var file = await Import(node); var working = await service.PrepareOpenAsync(file);
        var target = Path.Combine(Destination(), "saved.txt"); using var cancel = new CancellationTokenSource();
        service.LocalSaveCheckpoint = step => { if (step == "intent") cancel.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveLocalAsync(file, target, ct: cancel.Token));
        service.LocalSaveCheckpoint = null; Assert.Equal(working, await service.PrepareOpenAsync(file)); Assert.False(File.Exists(target));
        await service.SaveLocalAsync(file, target); Assert.Equal(target, await service.PrepareOpenAsync(file));
    }
}
