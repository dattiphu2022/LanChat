using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Xunit;
using ChatMessage = LanChat.Domain.Message;

namespace LanChat.Tests;

[CollectionDefinition("Windows UI", DisableParallelization = true)]
public sealed class WindowsUiCollection { }

[Collection("Windows UI")]
public sealed partial class WindowsUiTests
{
    [Fact]
    public Task AboutAndBannerShareDownloadAcrossClosingAndReopening() => OnUi(async host =>
    {
        var requested = Signal(); var finish = Signal(); var checks = 0; var downloads = 0;
        using var fixture = new Updates(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "api.github.com") { checks++; return Manifest(); }
            downloads++; requested.TrySetResult(true); await Wait(finish.Task, ct);
            return Payload();
        });
        using var banner = new UpdatePanel(fixture.Controller, false); host.Controls.Add(banner);
        using (var about = new AboutForm(fixture.Controller, () => true))
        {
            about.Show(host);
            var first = fixture.Controller.CheckAsync(true);
            Assert.Same(first, fixture.Controller.CheckAsync(true));
            await requested.Task;
            Assert.Equal(1, checks);
            Assert.True(fixture.Controller.State.Downloading);
            Assert.True(Button(about, "Hủy tải").Visible);
            about.Close();
        }
        Assert.True(fixture.Controller.State.Downloading);
        using (var reopened = new AboutForm(fixture.Controller, () => true))
        {
            reopened.Show(host);
            Assert.True(Button(reopened, "Hủy tải").Visible);
            Assert.False(Button(reopened, "Kiểm tra cập nhật").Enabled);
            finish.TrySetResult(true);
            await Until(() => fixture.Controller.State.Ready);
            Assert.Equal(1, downloads);
            Assert.True(Button(reopened, "Cài đặt ngay").Enabled);
            Assert.True(Button(banner, "Cài đặt ngay").Enabled);
            Assert.False(reopened.InstallRequested);
            Button(reopened, "Cài đặt ngay").PerformClick();
            Assert.True(reopened.InstallRequested);
        }
        using var installer = await fixture.Controller.PrepareInstallerAsync();
        Assert.NotNull(installer);
        Assert.Equal(Setup, File.ReadAllBytes(installer!.Path));
        Assert.Equal(1, downloads);
    });

    [Fact]
    public Task FailedOrCanceledDownloadCanRetryWithoutInstalling() => OnUi(async _ =>
    {
        var requested = Signal(); var attempt = 0;
        using var fixture = new Updates(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Manifest();
            attempt++;
            if (attempt == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (attempt == 2) { requested.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); }
            return Payload();
        });
        await fixture.Controller.CheckAsync(true);
        Assert.False(fixture.Controller.State.Busy);
        Assert.False(fixture.Controller.State.Ready);
        Assert.Contains("Chưa tải", fixture.Controller.State.Message);
        var download = fixture.Controller.DownloadAsync();
        await requested.Task;
        fixture.Controller.CancelDownload();
        await download;
        Assert.Contains("Đã hủy", fixture.Controller.State.Message);
        Assert.False(fixture.Controller.State.Ready);
        await fixture.Controller.DownloadAsync();
        Assert.True(fixture.Controller.State.Ready);
        Assert.Equal(3, attempt);
    });

    [Fact]
    public Task InstallFromAboutWaitsForItsModalAndBusyScopeToClose() => OnUi(async host =>
    {
        var directory = Temp();
        using var service = new UpdateService(directory, new Handler((request, _) =>
            Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Manifest() : Payload())));
        using var workspace = new NetworkWorkspace(new NetworkCatalog(new NodeOptions { DataDirectory = directory }), new WindowsStartup(directory), service);
        workspace.Show(host);
        var driver = Signal();
        host.BeginInvoke((Action)(async () =>
        {
            AboutForm? about = null;
            try
            {
                await Until(() => (about = System.Windows.Forms.Application.OpenForms.OfType<AboutForm>().FirstOrDefault()) is not null);
                Button(about!, "Kiểm tra cập nhật").PerformClick();
                await Until(() => Children(about!).OfType<Button>().Any(b => b.Text == "Cài đặt ngay" && b.Enabled));
                Assert.Null(workspace.PendingInstaller);
                Button(about!, "Cài đặt ngay").PerformClick();
                driver.TrySetResult(true);
            }
            catch (Exception ex) { driver.TrySetException(ex); about?.Close(); }
        }));
        var more = Button(workspace, "Thêm"); more.PerformClick();
        more.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text.StartsWith("Giới thiệu")).PerformClick();
        await driver.Task;
        await Until(() => workspace.PendingInstaller is not null);
        workspace.PendingInstaller!.Dispose();
    });

    [Fact]
    public Task InstallingPublisherWaitsForReadersAndResumesSharingAfterVerificationFailure() => OnUi(async host =>
    {
        var directory = Temp(); using var service = new UpdateService(directory);
        service.Packages.InspectForTesting = _ => "v9.12.3";
        var file = Path.Combine(directory, "fixture.exe"); File.WriteAllText(file, "fixture");
        var package = await service.Packages.ImportAsync(file); var network = Guid.NewGuid();
        await service.ConfigureAsync(new(UpdateSourceKind.Local, Package: package, Sharing: true, Networks: [network]), default);
        using var workspace = new NetworkWorkspace(new NetworkCatalog(new NodeOptions { DataDirectory = directory }, service.Packages), new WindowsStartup(directory), service);
        workspace.Show(host);
        var controller = (UpdateCoordinator)typeof(NetworkWorkspace).GetField("_updates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
        await controller.CheckAsync(true); await controller.DownloadAsync(); Assert.True(controller.State.Ready);
        using (var reader = service.Packages.OpenPackage(network, package.Id))
        {
            Button(workspace, "Cài đặt ngay").PerformClick(); await Task.Delay(50);
            Assert.Null(workspace.PendingInstaller); Assert.Contains("đang cung cấp", controller.State.Message);
        }
        string cache;
        using (var verified = await service.DownloadAsync(controller.State.Release!, new Progress<long>(), default)) cache = verified.Path;
        File.WriteAllText(cache, "bad"); File.WriteAllText(service.Packages.PackagePath(package), "bad");
        Button(workspace, "Cài đặt ngay").PerformClick(); await Until(() => !controller.State.Busy && !controller.State.Ready);
        Assert.Null(workspace.PendingInstaller); Assert.Equal(package, service.Packages.GetPackage(network));
        File.WriteAllText(service.Packages.PackagePath(package), "fixture"); await controller.DownloadAsync(); Assert.True(controller.State.Ready);
        Button(workspace, "Cài đặt ngay").PerformClick(); await Until(() => workspace.PendingInstaller is not null);
        Assert.Null(service.Packages.GetPackage(network)); workspace.PendingInstaller!.Dispose();
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public Task AboutActionsStayVisibleAtSmallSizesAndHighDpi(int dpi) => OnUi(async host =>
    {
        using var fixture = new Updates((_, _) => Task.FromResult(Manifest()));
        await fixture.Controller.CheckAsync(true);
        fixture.Controller.Notify("Đã có phiên bản mới. Đóng Giới thiệu vẫn tiếp tục tải nền; bạn có thể cài sau khi hoàn tất công việc.");
        using var about = new AboutForm(fixture.Controller, () => true, () => { });
        about.Show(host);
        using var fonts = SimulateDpi(about, dpi);
        about.MinimumSize = new System.Drawing.Size(500 * dpi / 96, 450 * dpi / 96);
        about.Size = about.MinimumSize;
        await Task.Delay(50);
        var check = Button(about, "Kiểm tra cập nhật"); var apply = Button(about, "Tải bản cập nhật");
        var donation = Button(about, "Ủng hộ phát triển"); var close = Button(about, "Đóng");
        Assert.Equal(check.PointToScreen(System.Drawing.Point.Empty).X, donation.PointToScreen(System.Drawing.Point.Empty).X);
        Assert.Equal(apply.PointToScreen(System.Drawing.Point.Empty).X, close.PointToScreen(System.Drawing.Point.Empty).X);
        Assert.All(new[] { apply, donation, close }, button => Assert.Equal(check.Size, button.Size));
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captures))
        {
            Directory.CreateDirectory(captures);
            using var image = new System.Drawing.Bitmap(about.Width, about.Height);
            about.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, about.Size));
            image.Save(Path.Combine(captures, $"about-{dpi}.png"));
        }
        foreach (var button in Children(about).OfType<Button>().Where(button => button.Visible))
        {
            var original = button.RectangleToScreen(button.ClientRectangle); var visible = original;
            for (var parent = button.Parent; parent is not null; parent = parent.Parent)
                visible = System.Drawing.Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            Assert.True(visible == original, $"Button is clipped at {dpi} DPI: {button.Text} ({original}, {visible})");
        }
    });

    [Fact]
    public Task SettingsKeepInputAfterFailureAndPreventClosingDuringSave() => OnUi(async host =>
    {
        var save = Signal(); var calls = 0;
        using var settings = new SettingsForm("Tên cũ", "Đơn vị", false, async (_, _, _) =>
        {
            calls++; await save.Task; throw new IOException("Không lưu được hồ sơ thử nghiệm");
        });
        settings.Show(host);
        var name = Children(settings).OfType<TextBox>().Single(text => text.AccessibleName == "Tên");
        name.Text = "Tên mới"; Button(settings, "Lưu").PerformClick();
        settings.Close(); Assert.False(settings.IsDisposed);
        Assert.False(Button(settings, "Lưu").Enabled);
        save.SetResult(true); await Until(() => Button(settings, "Lưu").Enabled);
        Assert.Equal(1, calls); Assert.Equal("Tên mới", name.Text);
        Assert.Contains(Children(settings).OfType<Label>(), label => label.Text.Contains("Không lưu được hồ sơ"));
    });

    [Fact]
    public Task NamePromptKeepsInvalidAndFailedInputUntilSaveSucceeds() => OnUi(async host =>
    {
        var driver = Signal(); var calls = 0;
        host.BeginInvoke((Action)(async () =>
        {
            Form? prompt = null;
            try
            {
                await Until(() => (prompt = System.Windows.Forms.Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "Tên thử nghiệm")) is not null);
                var input = Children(prompt!).OfType<TextBox>().Single();
                Assert.Equal(60, input.MaxLength);
                var enter = System.Windows.Forms.Message.Create(prompt!.Handle, 0x100, new IntPtr((int)Keys.Enter), IntPtr.Zero);
                prompt.PreProcessMessage(ref enter);
                Assert.Contains(Children(prompt).OfType<Label>(), label => label.Text.Contains("Vui lòng nhập tên"));
                Assert.Equal(0, calls);
                input.Text = "Tên mới cần giữ";
                Button(prompt, "Lưu").PerformClick();
                await Until(() => Children(prompt).OfType<Label>().Any(label => label.Text.Contains("Thử lưu lại")));
                Assert.Equal("Tên mới cần giữ", input.Text);
                Assert.NotEqual(DialogResult.OK, prompt.DialogResult);
                Button(prompt, "Lưu").PerformClick();
                driver.TrySetResult(true);
            }
            catch (Exception ex) { driver.TrySetException(ex); prompt?.Dispose(); }
        }));
        var name = SelectionForm.Prompt(host, "Tên thử nghiệm", maxLength: 60, save: _ =>
            ++calls == 1 ? Task.FromException(new IOException("Thử lưu lại")) : Task.CompletedTask);
        await driver.Task;
        Assert.Equal("Tên mới cần giữ", name); Assert.Equal(2, calls);
    });

    [Fact]
    public Task LateSearchCannotReplaceNewResultsOrUpdateClosedWindow() => OnUi(async host =>
    {
        var first = new TaskCompletionSource<SearchResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<SearchResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Signal(); var count = 0;
        using var form = new MessageSearchForm((query, _) =>
        {
            Interlocked.Increment(ref count);
            if (query == "cũ") { started.TrySetResult(true); return first.Task; }
            return second.Task;
        }, []);
        form.Show(host);
        var search = Children(form).OfType<PromptTextBox>().Single();
        var results = Children(form).OfType<ListBox>().Single();
        search.Text = "cũ"; Button(form, "Tìm kiếm").PerformClick(); await started.Task;
        search.Text = "mới"; Button(form, "Tìm kiếm").PerformClick();
        await Until(() => Volatile.Read(ref count) == 2);
        var message = Guid.NewGuid(); var conversation = Guid.NewGuid();
        second.SetResult([new(message, conversation, "kết quả mới", DateTime.UtcNow.Ticks)]);
        await Until(() => results.Items.Count == 1);
        first.SetResult([new(Guid.NewGuid(), Guid.NewGuid(), "kết quả cũ", DateTime.UtcNow.Ticks)]);
        await Task.Delay(50);
        Assert.Contains("kết quả mới", results.Items[0].ToString());
        results.SelectedIndex = 0; results.Focus();
        var key = System.Windows.Forms.Message.Create(form.Handle, 0x100, new IntPtr((int)Keys.Enter), IntPtr.Zero);
        form.PreProcessMessage(ref key);
        Assert.Equal(DialogResult.OK, form.DialogResult);
        Assert.Equal(message, form.SelectedMessage);
        Assert.Equal(conversation, form.SelectedConversation);
        form.Close();
    });

    [Fact]
    public Task ClosingSearchCancelsItsPendingRead() => OnUi(async host =>
    {
        var started = Signal(); var canceled = Signal();
        using var form = new MessageSearchForm(async (_, ct) =>
        {
            started.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { canceled.TrySetResult(true); throw; }
            return [];
        }, []);
        form.Show(host);
        Children(form).OfType<PromptTextBox>().Single().Text = "tìm";
        Button(form, "Tìm kiếm").PerformClick(); await started.Task;
        form.Close(); await canceled.Task;
    });

    [Fact]
    public Task EditingRestoresTextFilesAndReplyAfterCancelAndSuccessfulSave() => OnUi(async host =>
    {
        using var composer = new ChatInputControl(); host.Controls.Add(composer);
        var original = new ComposerDraft("Nội dung đang soạn", ["ảnh-nháp.png"], Guid.NewGuid(), null, "Trả lời tin trước");
        var message = new ChatMessage { Id = Guid.NewGuid(), Text = "Tin cần sửa" };
        composer.RestoreDraft(original); composer.SetContext(message, true);
        AssertDraft(original, composer.Draft().BeforeEdit!);
        Assert.Contains("ảnh-nháp.png", composer.Draft().AllFiles);
        var escape = System.Windows.Forms.Message.Create(composer.Handle, 0x100, new IntPtr((int)Keys.Escape), IntPtr.Zero);
        composer.PreProcessMessage(ref escape); AssertDraft(original, composer.Draft());
        composer.SetContext(message, true);
        var editing = composer.Draft(); composer.RestoreDraft(null); composer.RestoreDraft(editing);
        composer.SendRequested += _ => Task.CompletedTask;
        Button(composer, "Lưu sửa").PerformClick(); await Until(() => !composer.IsWorking);
        AssertDraft(original, composer.Draft());
    });

    [Fact]
    public Task SendFailureAndLateCompletionPreserveTheRightDraft() => OnUi(async host =>
    {
        using (var failed = new ChatInputControl())
        {
            host.Controls.Add(failed);
            Assert.False(Button(failed, "Gửi").Enabled);
            failed.RestoreDraft(new("Cần gửi lại", [], null, null, ""));
            failed.SendRequested += _ => Task.FromException(new IOException("Lỗi thử nghiệm"));
            Button(failed, "Gửi").PerformClick();
            await Until(() => !failed.IsWorking);
            Assert.Equal("Cần gửi lại", failed.Draft().Text);
            Assert.Contains(Children(failed).OfType<Label>(), label => label.Text.Contains("Lỗi thử nghiệm"));
        }
        using var composer = new ChatInputControl(); host.Controls.Add(composer);
        var finish = Signal(); composer.SendRequested += _ => finish.Task;
        composer.RestoreDraft(new("Hội thoại A", [], null, null, ""));
        Button(composer, "Gửi").PerformClick(); Assert.True(composer.IsWorking);
        composer.RestoreDraft(new("Hội thoại B", [], null, null, ""));
        finish.SetResult(true); await Until(() => !composer.IsWorking);
        Assert.Equal("Hội thoại B", composer.Draft().Text);
    });

    [Fact]
    public Task PendingAttachmentSendLeavesComposerAvailableAndFailureKeepsBothDrafts() => OnUi(async host =>
    {
        using var composer = new ChatInputControl();
        host.Controls.Add(composer);
        var editor = Children(composer).OfType<PromptTextBox>().Single();
        var attachment = Path.Combine(Temp(), "large-file.bin");
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new List<ComposerSubmission>();
        composer.SendRequested += submission =>
        {
            submitted.Add(submission);
            return submitted.Count == 1 ? first.Task : Task.CompletedTask;
        };

        composer.RestoreDraft(new("Đính kèm", [attachment], null, null, ""));
        Button(composer, "Gửi").PerformClick();
        Assert.True(composer.IsWorking);
        Assert.False(editor.ReadOnly);
        Assert.Empty(composer.Draft().Files);
        editor.Text = "Tin tiếp theo";
        Assert.True(Button(composer, "Gửi").Enabled);
        Button(composer, "Gửi").PerformClick();
        Assert.Equal(new[] { "Đính kèm", "Tin tiếp theo" }, submitted.Select(item => item.Text));

        editor.Text = "Đang soạn tin thứ ba";
        first.SetException(new IOException("Không đọc được tệp"));
        await Until(() => !composer.IsWorking);
        Assert.Equal("Đang soạn tin thứ ba", composer.Draft().Text);
        Assert.Equal(attachment, Assert.Single(composer.FailedDraftFiles));
        var recovery = Children(composer).OfType<LinkLabel>().Single(label => label.Text.Contains("Khôi phục tin chưa gửi"));
        Assert.True(recovery.CanSelect);
        editor.Clear();
        typeof(LinkLabel).GetMethod("OnLinkClicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(recovery, [new LinkLabelLinkClickedEventArgs(recovery.Links[0])]);
        Assert.Equal("Đính kèm", composer.Draft().Text);
        Assert.Equal(attachment, Assert.Single(composer.Draft().Files));
        Assert.Empty(composer.FailedDraftFiles);
    });

    [Fact]
    public Task SelectionRequiresARealItemAndReportsEmptyFilter() => OnUi(host =>
    {
        using var selection = new SelectionForm("Chọn", [new SelectionItem(Guid.NewGuid(), "Người A")]);
        selection.Show(host);
        Assert.NotNull(selection.CancelButton);
        Assert.False(Button(selection, "Chọn").Enabled);
        var list = Children(selection).OfType<ListBox>().Single(); list.SelectedIndex = 0;
        Assert.True(Button(selection, "Chọn").Enabled);
        Children(selection).OfType<PromptTextBox>().Single().Text = "không tồn tại";
        Assert.False(Button(selection, "Chọn").Enabled);
        Assert.Contains(Children(selection).OfType<Label>(), label => label.Text.Contains("Không tìm thấy"));
        var escape = System.Windows.Forms.Message.Create(selection.Handle, 0x100, new IntPtr((int)Keys.Escape), IntPtr.Zero);
        selection.PreProcessMessage(ref escape); Assert.Equal(DialogResult.Cancel, selection.DialogResult);
        return Task.CompletedTask;
    });

    [Fact]
    public Task FileLimitIsReportedWithoutDiscardingAcceptedFiles() => OnUi(host =>
    {
        using var composer = new ChatInputControl(); host.Controls.Add(composer);
        var directory = Temp(); var files = Enumerable.Range(0, 21).Select(i => Path.Combine(directory, i + ".txt")).ToArray();
        foreach (var file in files) File.WriteAllText(file, "tệp");
        composer.AddFiles(files.Append(Path.Combine(directory, "missing.txt")));
        Assert.Equal(20, composer.Draft().Files.Length);
        Assert.Contains(Children(composer).OfType<Label>(), label => label.Text.Contains("vượt giới hạn") && label.Text.Contains("không còn tồn tại"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task EmbeddedViewsUseTheForegroundOwnedDialog() => OnUi(host =>
    {
        using var embedded = new DpiForm { TopLevel = false }; host.Controls.Add(embedded); embedded.Show();
        using var dialog = new DpiForm(); dialog.Show(host);
        Assert.Same(dialog, UiInteraction.Owner(embedded));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ClosingAnOpenProgressWindowDoesNotCloseAnUnrelatedDialog() => OnUi(host =>
    {
        using var cancellation = new CancellationTokenSource();
        using var progress = new AttachmentOpenProgressForm("Tệp A", cancellation); progress.Show(host);
        Assert.Same(progress, UiInteraction.ForegroundDialog(host));
        Assert.Same(host, UiInteraction.Owner(host));
        using var other = new DpiForm(); other.Show(UiInteraction.Owner(host));
        progress.Close(); Assert.False(other.IsDisposed); Assert.Same(other, UiInteraction.Owner(host));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task SavingCanceledCopyPreservesExistingFileAndCleansTemporaryFile()
    {
        var directory = Temp(); var source = Path.Combine(directory, "source.txt"); var target = Path.Combine(directory, "target.txt");
        File.WriteAllText(source, "nội dung mới"); File.WriteAllText(target, "bản cũ");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UiInteraction.SaveCopyAsync(source, target, cancellation.Token));
        Assert.Equal("bản cũ", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(directory, ".lanchat-*.tmp"));
        await UiInteraction.SaveCopyAsync(source, target, default);
        Assert.Equal("nội dung mới", File.ReadAllText(target));
    }

    [Fact]
    public Task ChatDrawingCacheReusesGeometryAndKeepsNarrowTextWrapped() => OnUi(_ =>
    {
        Assert.Equal(100, ChatDrawingCache.ForDpi(96).Pixels(100));
        Assert.Equal(125, ChatDrawingCache.ForDpi(120).Pixels(100));
        Assert.Equal(150, ChatDrawingCache.ForDpi(144).Pixels(100));
        Assert.Equal(175, ChatDrawingCache.ForDpi(168).Pixels(100));
        Assert.Equal(200, ChatDrawingCache.ForDpi(192).Pixels(100));
        Assert.Equal(225, ChatDrawingCache.ForDpi(216).Pixels(100));
        Assert.Equal(12, ChatDrawingCache.ForDpi(120).Pixels(10));
        using var cache = new ChatDrawingCache();
        var bounds = new System.Drawing.Rectangle(10, 10, 260, 48);
        var outline = cache.RoundedOutline(bounds, 12);
        Assert.NotSame(outline, cache.RoundedOutline(bounds, 16));
        Assert.Same(outline, cache.RoundedOutline(bounds, 12));

        var text = string.Join(" ", Enumerable.Repeat("Tin nhắn tiếng Việt", 8));
        var wide = cache.Measure(text, Theme.DefaultFont, 340, 96);
        var narrow = cache.Measure(text, Theme.DefaultFont, 150, 96);
        Assert.True(narrow.Height > wide.Height);
        Assert.Equal(wide, cache.Measure(text, Theme.DefaultFont, 340, 96));
        return Task.CompletedTask;
    });

    [Fact]
    public Task MessageResizeBatchesWidthChangesAndSkipsHeightOnlyReflow() => OnUi(async host =>
    {
        using var view = new Form { ClientSize = new System.Drawing.Size(530, 430) };
        using var list = new MessageListControl();
        view.Controls.Add(list);
        view.Show(host);
        var user = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var messages = Enumerable.Range(0, 12).Select(_ => new ChatMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation, SenderUserId = user, IsRead = true,
            Text = "Nội dung dài, xuống dòng khi kéo hẹp vùng đọc.", CreatedAtTicks = DateTime.UtcNow.Ticks
        }).ToArray();
        list.Bind(conversation, messages, user, []);
        var bubble = Children(list).OfType<MessageBubbleControl>().First();
        var originalWidth = bubble.Width;
        var widthChanges = 0;
        var lastWidth = originalWidth;
        bubble.SizeChanged += (_, _) =>
        {
            if (lastWidth != bubble.Width) { lastWidth = bubble.Width; widthChanges++; }
        };

        view.ClientSize = new System.Drawing.Size(530, 490);
        await Task.Delay(150);
        Assert.Equal(0, widthChanges);
        var flow = list.Controls.OfType<FlowLayoutPanel>().Single();
        var viewportChanges = 0;
        flow.SizeChanged += (_, _) => viewportChanges++;
        for (var width = 520; width >= 400; width -= 20)
        {
            view.ClientSize = new System.Drawing.Size(width, 490);
            var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            list.BeginInvoke((Action)(() => drained.SetResult(true)));
            await drained.Task;
        }
        await Task.Delay(200);
        Assert.Equal(1, widthChanges);
        Assert.Equal(1, viewportChanges);
        Assert.Equal(list.ClientSize, flow.Size);
        Assert.True(bubble.Width < originalWidth);
    });

    [Fact]
    public Task ConversationRenderingKeepsTheSelectedHistoryCorrect() => OnUi(host =>
    {
        using var list = new MessageListControl(); host.Controls.Add(list);
        var user = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        ChatMessage[] Messages(Guid conversation) => Enumerable.Range(0, 80).Select(i => new ChatMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation, SenderUserId = user, IsRead = true,
            Text = $"Tin nhắn {i}: Nội dung trao đổi công việc, kiểm tra độ mượt khi chuyển hội thoại.",
            CreatedAtTicks = DateTime.UtcNow.AddMinutes(i).Ticks
        }).ToArray();
        var first = Messages(a); var second = Messages(b);
        double Measure(Action action) { var timer = Stopwatch.StartNew(); action(); return timer.Elapsed.TotalMilliseconds; }
        var cold = Measure(() => list.Bind(a, first, user, []));
        var controls = Children(list).OfType<MessageBubbleControl>().ToArray();
        var layouts = 0;
        foreach (var bubble in controls) bubble.Layout += (_, _) => layouts++;
        var repeated = Measure(() => list.Bind(a, first, user, []));
        Assert.Equal(0, layouts);
        var switched = Measure(() => list.Bind(b, second, user, []));
        Assert.True(controls.ToHashSet().SetEquals(Children(list).OfType<MessageBubbleControl>()));
        Assert.Equal(second.Select(m => m.Id).OrderBy(id => id), Children(list).OfType<MessageBubbleControl>().Select(c => c.MessageId).OrderBy(id => id));
        var actions = 0; Guid? replied = null;
        list.ActionRequested += (message, action) => { if (action == "reply") { actions++; replied = message.Id; } };
        var selected = Children(list).OfType<MessageBubbleControl>().Single(c => c.MessageId == second[0].Id);
        Children(selected).OfType<IconButton>().Single(button => button.Icon == UiIcon.Reply).PerformClick();
        Assert.Equal(second[0].Id, replied); Assert.Equal(1, actions);
        var returned = Measure(() => list.Bind(a, first, user, []));
        Assert.True(controls.ToHashSet().SetEquals(Children(list).OfType<MessageBubbleControl>()));
        first[0].Text = "Nội dung đã sửa sau khi chuyển hội thoại";
        var detail = new MessageDetails(first[0], [], ["👍"], 1, 1, null, "Người A") { SenderDepartment = "Đơn vị A" };
        list.Bind(a, first, user, [detail]);
        var edited = Children(list).OfType<MessageBubbleControl>().Single(c => c.MessageId == first[0].Id);
        Assert.Contains(Children(edited).OfType<RichTextBox>(), body => body.Text == first[0].Text);
        Assert.Contains(Children(edited).OfType<Label>(), label => label.Text.Contains("👍") && label.Text.Contains("Đã đọc"));
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captures))
        {
            Directory.CreateDirectory(captures);
            File.WriteAllText(Path.Combine(captures, "render-timing.json"), JsonSerializer.Serialize(new { cold, repeated, switched, returned }));
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public Task ConversationFiltersAndUnreadBadgeFitAtHighDpi(int dpi) => OnUi(host =>
    {
        using var view = new DpiForm { ClientSize = new System.Drawing.Size(290, 400) };
        using var list = new ConversationListControl();
        view.Controls.Add(list);
        view.Show(host);
        var summary = new ConversationSummary(Guid.NewGuid(), Guid.NewGuid(), "Nguyễn Văn An", "Tin chưa đọc", 0, 120, true, true, LanChat.Domain.PresenceStatus.Online);
        list.Bind([summary], null);
        using var fonts = SimulateDpi(view, dpi);
        list.ApplyPreferences(new UiPreferences { Density = UiDensity.Compact });

        var filters = Children(list).OfType<Button>().Where(button => button.Text is "Tất cả" or "Chưa đọc" or "@ Tôi" or "Đã ghim").ToArray();
        Assert.Equal(4, filters.Length);
        foreach (var button in filters)
        {
            Assert.True(button.Height >= button.Font.Height + button.Padding.Vertical, $"Filter clipped at {dpi}: {button.Text}");
            Assert.True(button.Parent.ClientRectangle.Contains(button.Bounds));
        }

        var row = Children(list).OfType<ConversationItemControl>().Single();
        var badge = Children(row).OfType<UnreadBadge>().Single();
        Assert.True(badge.Visible);
        Assert.True(badge.Height >= badge.GetPreferredSize(System.Drawing.Size.Empty).Height);
        Assert.True(badge.Parent.ClientRectangle.Contains(badge.Bounds));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReturnToLatestNeverCoversHistoryOrComposer() => OnUi(host =>
    {
        using var view = new Form { ClientSize = new System.Drawing.Size(370, 380) };
        using var chat = new ChatViewControl();
        view.Controls.Add(chat);
        view.Show(host);
        chat.SelectConversation("Hội thoại thử", true);
        chat.SetHistoryAvailable(true, returnToLatest: true);
        var link = Children(chat).OfType<IconLinkLabel>().Single(c => c.Visible && c.Text == "Quay về tin nhắn mới");
        var history = Children(chat).OfType<MessageListControl>().Single();
        var composer = Children(chat).OfType<ChatInputControl>().Single();
        var linkBounds = link.RectangleToScreen(link.ClientRectangle);
        Assert.True(history.RectangleToScreen(history.ClientRectangle).Bottom <= linkBounds.Top);
        Assert.True(linkBounds.Bottom <= composer.RectangleToScreen(composer.ClientRectangle).Top);
        Assert.True(composer.Height > 0);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AttachmentButtonsStackInsideNarrowFileCard() => OnUi(host =>
    {
        var file = new AttachmentDto(Guid.NewGuid(), Guid.NewGuid(), "Hồ sơ.txt", "text/plain", 123, new string('a', 64));
        using var item = new AttachmentItemControl(file, null, null);
        host.Controls.Add(item);
        item.FitWidth(105);
        var open = Children(item).OfType<Button>().Single(c => c.Text == "Mở");
        var save = Children(item).OfType<Button>().Single(c => c.Text == "Lưu về máy");
        Assert.True(item.ClientRectangle.Contains(open.Bounds));
        Assert.True(item.ClientRectangle.Contains(save.Bounds));
        Assert.True(open.Bottom <= save.Top);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ConversationRowsHaveOnePixelDividersAndSelectionDoesNotRebuildThem() => OnUi(host =>
    {
        using var list = new ConversationListControl(); host.Controls.Add(list);
        var first = new ConversationSummary(Guid.NewGuid(), Guid.NewGuid(), "Nguyễn Văn An", "Nội dung trao đổi công việc", 0, 0, false, false, LanChat.Domain.PresenceStatus.Online);
        var second = new ConversationSummary(Guid.NewGuid(), Guid.NewGuid(), "Trần Minh Bình", "Hẹn gặp lại", 0, 0, false, false, LanChat.Domain.PresenceStatus.Offline);
        list.Bind([first, second], first.Id);
        var rows = Children(list).OfType<ConversationItemControl>().OrderBy(c => c.Top).ToArray();
        using var rowImage = new System.Drawing.Bitmap(rows[1].Width, rows[1].Height);
        rows[1].DrawToBitmap(rowImage, rows[1].ClientRectangle);
        Assert.Equal(Theme.Border.ToArgb(), rowImage.GetPixel(rowImage.Width / 2, rowImage.Height - 1).ToArgb());
        Assert.Equal(System.Drawing.Color.White.ToArgb(), rowImage.GetPixel(rowImage.Width / 2, rowImage.Height - 2).ToArgb());
        list.SetActive(second.Id);
        Assert.Equal(Theme.Selection, rows[1].BackColor);
        Assert.Equal(System.Drawing.Color.White, rows[0].BackColor);
        Assert.All(rows, row => Assert.False(row.IsDisposed));
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captures))
        {
            Directory.CreateDirectory(captures);
            using var image = new System.Drawing.Bitmap(list.Width, list.Height);
            list.DrawToBitmap(image, list.ClientRectangle); image.Save(Path.Combine(captures, "conversation-list.png"));
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task PreparingLongHistoryYieldsAndCanBeCanceledWhenSelectionChanges() => OnUi(async host =>
    {
        using var list = new MessageListControl(); host.Controls.Add(list);
        using var cancellation = new CancellationTokenSource();
        var preparing = list.PrepareAsync(80, cancellation.Token);
        var yielded = false;
        host.BeginInvoke((Action)(() => { yielded = !preparing.IsCompleted; cancellation.Cancel(); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.True(yielded);
        await list.PrepareAsync(8, default);
        var conversation = Guid.NewGuid(); var user = Guid.NewGuid();
        var messages = Enumerable.Range(0, 8).Select(i => new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation,
            SenderUserId = user, Text = "Tin của hội thoại được chọn sau cùng", CreatedAtTicks = DateTime.UtcNow.Ticks }).ToArray();
        list.Bind(conversation, messages, user, []);
        Assert.Equal(8, Children(list).OfType<MessageBubbleControl>().Count());
        Assert.All(Children(list).OfType<MessageBubbleControl>(), bubble => Assert.Contains(messages, message => message.Id == bubble.MessageId));
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public Task AttachmentActionsStayVisibleAndBindingReusesControls(int dpi) => OnUi(async host =>
    {
        using var view = new DpiForm { ClientSize = new System.Drawing.Size(540, 620), StartPosition = FormStartPosition.CenterParent };
        using var list = new MessageListControl(); view.Controls.Add(list);
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var files = (IFileTransferService)node.Host.Services.GetService(typeof(IFileTransferService))!;
        list.ConfigureImages(new ChatImageService(files)); list.ConfigureAttachments(new AttachmentActions(files));
        var user = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var message = new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation, SenderUserId = user, Text = "Các đính kèm", CreatedAtTicks = DateTime.UtcNow.Ticks };
        AttachmentDto File(string name, string mime) => new(Guid.NewGuid(), message.Id, name, mime, 100, new string('a', 64));
        var attachments = new[] { File("Hồ sơ.txt", "text/plain"), File("Ảnh lớn.bmp", "image/bmp") with { FileSize = 30 * 1024 * 1024 } };
        var detail = new MessageDetails(message, attachments, [], 0, 0, null, "Người gửi");
        list.Bind(conversation, [message], user, [detail]); view.Show(host);
        await Task.Delay(100);
        using var fonts = SimulateDpi(view, dpi); view.ClientSize = new System.Drawing.Size(540 * dpi / 96, 620 * dpi / 96);
        await Task.Delay(80);
        // Native WM_SIZE can restore the real monitor's DPI on an oversized test window.
        // Reapply the emulated monitor after that message, then raise its layout event.
        foreach (var control in Children(view).Prepend(view))
            typeof(Control).GetField("deviceDpi", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, dpi);
        typeof(Control).GetMethod("OnDpiChangedAfterParent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(list, [EventArgs.Empty]);
        var dpiApplied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        list.BeginInvoke((Action)(() => dpiApplied.SetResult(true)));
        await dpiApplied.Task;
        var items = Children(list).OfType<AttachmentItemControl>().ToArray(); Assert.Equal(2, items.Length);
        var commands = new List<AttachmentCommand>(); list.AttachmentCommandRequested += (_, command) => commands.Add(command);
        foreach (var item in items)
        {
            foreach (var button in Children(item).OfType<Button>())
            {
                var original = button.RectangleToScreen(button.ClientRectangle); var visible = original;
                for (var parent = button.Parent; parent is not null; parent = parent.Parent)
                    visible = System.Drawing.Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
                var ancestors = new List<string>();
                for (var parent = button.Parent; parent is not null; parent = parent.Parent) ancestors.Add(parent.GetType().Name + ":" + parent.RectangleToScreen(parent.ClientRectangle));
                Assert.True(original == visible, $"Clipped {button.Text} at {dpi} (form {view.DeviceDpi}, layout {item.LayoutDpi()}): {original}, {visible} / {string.Join(" / ", ancestors)}");
            }
            Button(item, "Mở").PerformClick(); Button(item, "Lưu về máy").PerformClick();
        }
        Assert.Equal(new[] { AttachmentCommand.Open, AttachmentCommand.Save, AttachmentCommand.Open, AttachmentCommand.Save }, commands);
        list.Bind(conversation, [message], user, [detail]); Assert.Equal(items, Children(list).OfType<AttachmentItemControl>().ToArray());
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captures))
        {
            Directory.CreateDirectory(captures); using var image = new System.Drawing.Bitmap(view.Width, view.Height);
            view.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, view.Size)); image.Save(Path.Combine(captures, $"attachments-{dpi}.png"));
        }
    });

    [Fact]
    public Task DenseChatShowsEveryFileNameAndKeepsActionsOnTheFooterLine() => OnUi(host =>
    {
        using var list = new MessageListControl(); host.Controls.Add(list);
        var conversation = Guid.NewGuid(); var sender = Guid.NewGuid(); var other = Guid.NewGuid();
        ChatMessage Message(Guid author, string text) => new() { Id = Guid.NewGuid(), ConversationId = conversation,
            SenderUserId = author, Text = text, CreatedAtTicks = DateTime.UtcNow.Ticks, IsRead = true };
        var withFiles = Message(sender, "Tài liệu cuộc họp"); var shortText = Message(sender, "Đã nhận."); var reply = Message(other, "Cảm ơn bạn.");
        var files = new[] { "Báo cáo tổng hợp tháng 11.xlsx", "Biên bản họp.docx", "Phụ lục.pdf" }
            .Select(name => new AttachmentDto(Guid.NewGuid(), withFiles.Id, name, "application/octet-stream", 12500, new string('a', 64))).ToArray();
        var details = new MessageDetails(withFiles, files, [], 0, 0, null, "Nguyễn Văn An");
        list.Bind(conversation, [withFiles, shortText, reply], other, [details]);
        var bubbles = Children(list).OfType<MessageBubbleControl>().ToDictionary(b => b.MessageId);
        var items = Children(bubbles[withFiles.Id]).OfType<AttachmentItemControl>().OrderBy(i => i.Top).ToArray();
        Assert.Equal(3, items.Length);
        foreach (var item in items)
        {
            var link = Children(item).OfType<IconLinkLabel>().Single();
            var metadata = Children(item).OfType<Label>().Single(label => label is not LinkLabel);
            Assert.Contains(files, file => file.FileName == link.Text);
            Assert.True(link.Visible); Assert.True(link.Height >= link.Font.Height);
            Assert.True(link.Bottom <= metadata.Top, "Filename and size must occupy separate, visible lines.");
            Assert.True(item.Height <= item.ScalePixels(52), "A file row must not reserve the old 100px tile height.");
            Assert.All(Children(item).OfType<Button>(), button => Assert.True(link.Right <= button.Left || link.Bottom <= button.Top));
        }
        for (var i = 1; i < items.Length; i++) Assert.True(items[i].Top >= items[i - 1].Bottom);
        var fileFlow = items[0].Parent as FlowLayoutPanel; Assert.NotNull(fileFlow); Assert.False(fileFlow!.AutoScroll);
        var shortBubble = bubbles[shortText.Id]; var card = shortBubble.Controls.OfType<TableLayoutPanel>().Single();
        Assert.True(card.Width <= shortBubble.ScalePixels(300), "Short messages should not fill the maximum bubble width.");
        var state = Children(shortBubble).OfType<Label>().Single(label => label.Text.Contains(" · Đã lưu") || label.Text == DateTime.Now.ToString("HH:mm"));
        var more = Children(shortBubble).OfType<IconButton>().Single(button => button.Icon == UiIcon.More);
        var stateBounds = state.RectangleToScreen(state.ClientRectangle); var moreBounds = more.RectangleToScreen(more.ClientRectangle);
        Assert.True(stateBounds.Top < moreBounds.Bottom && moreBounds.Top < stateBounds.Bottom);
        using var bitmap = new System.Drawing.Bitmap(bubbles[reply.Id].Width, bubbles[reply.Id].Height);
        bubbles[reply.Id].DrawToBitmap(bitmap, bubbles[reply.Id].ClientRectangle);
        Assert.Equal(Theme.Border.ToArgb(), bitmap.GetPixel(bitmap.Width / 2, 0).ToArgb());
        Assert.Equal(Theme.Canvas.ToArgb(), bitmap.GetPixel(bitmap.Width / 2, 1).ToArgb());
        withFiles.IsRead = false;
        list.Bind(conversation, [withFiles, shortText, reply], other, [details with { ReadCount = 1 }]);
        Assert.Equal(items, Children(bubbles[withFiles.Id]).OfType<AttachmentItemControl>().OrderBy(i => i.Top).ToArray());
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captures))
        {
            Directory.CreateDirectory(captures); using var image = new System.Drawing.Bitmap(list.Width, list.Height);
            list.DrawToBitmap(image, list.ClientRectangle); image.Save(Path.Combine(captures, "dense-conversation.png"));
            File.WriteAllText(Path.Combine(captures, "density-metrics.json"), JsonSerializer.Serialize(new {
                dpi = list.DeviceDpi, fileRow = items[0].Height, fileMessage = bubbles[withFiles.Id].Height,
                shortMessage = shortBubble.Height, shortMessageWidth = card.Width }));
        }
        shortText.CreatedAtTicks = DateTime.UtcNow.AddDays(-2).Ticks;
        list.Bind(conversation, [withFiles, shortText, reply], other, [details]);
        Assert.Contains(new DateTime(shortText.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM"), state.Text);
        foreach (var item in items)
        {
            item.FitWidth(item.ScalePixels(220));
            var name = Children(item).OfType<IconLinkLabel>().Single();
            Assert.All(Children(item).OfType<Button>(), button =>
            {
                Assert.True(button.Bottom <= item.Height && button.Right <= item.Width);
                Assert.True(button.Top >= name.Bottom, "At narrow widths the action row must move below the filename.");
            });
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task GalleryBatchOnlyIncludesImagesFromSelectedMessage() => OnUi(async host =>
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var files = (IFileTransferService)node.Host.Services.GetService(typeof(IFileTransferService))!;
        var message = Guid.NewGuid(); var otherMessage = Guid.NewGuid();
        GalleryImage Item(Guid id) => new(new AttachmentDto(Guid.NewGuid(), id, "image.png", "image/png", 100, new string('a', 64)), "", new PersonName("A"), DateTime.UtcNow.Ticks);
        var first = Item(message); var second = Item(message); var third = Item(otherMessage);
        using var gallery = new ImageGalleryForm([first, second, third], third.File.Id, new ChatImageService(files), files);
        Assert.Equal(new[] { third.File }, gallery.SelectedMessageImages);
        using var same = new ImageGalleryForm([first, second, third], second.File.Id, new ChatImageService(files), files);
        Assert.Equal(new[] { first.File, second.File }, same.SelectedMessageImages);
    });

    [Fact]
    public Task SavingDialogWaitsForCancellationAndIgnoresCompletionAfterDisposal() => OnUi(async host =>
    {
        var started = Signal(); var release = Signal(); var canceled = false;
        using var form = new AttachmentSaveForm("Thử hủy", async (_, ct) =>
        {
            started.TrySetResult(true); using var subscription = ct.Register(() => canceled = true);
            await release.Task; ct.ThrowIfCancellationRequested(); return [];
        }, default);
        form.Show(host); await started.Task; form.Close(); Assert.False(form.IsDisposed); Assert.True(canceled);
        release.SetResult(true); await Until(() => Children(form).OfType<Button>().Any(b => b.Text == "Đóng"));
        Assert.Contains(Children(form).OfType<Label>(), label => label.Text.StartsWith("Đã hủy")); form.Close();
        var late = Signal(); var running = Signal();
        var disposed = new AttachmentSaveForm("Callback muộn", async (_, _) => { running.SetResult(true); await late.Task; return []; }, default);
        disposed.Show(host); await running.Task; disposed.Dispose(); late.SetResult(true); await Task.Delay(50);
    });

    [Fact]
    public Task MissingSavedAttachmentCanCancelOrExplicitlyRestoreWithoutLaunchingRealApps() => OnUi(async host =>
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var files = (IFileTransferService)node.Host.Services.GetService(typeof(IFileTransferService))!;
        var source = Path.Combine(node.Options.DataDirectory, "original.txt"); File.WriteAllText(source, "original");
        var file = await files.ImportLocalAsync(source); var saved = Path.Combine(Temp(), "saved.txt");
        await files.SaveLocalAsync(file, saved); File.Delete(saved);
        var actions = new AttachmentActions(files); var launched = new List<string>();
        var cancelDriver = Task.Run(async () =>
        {
            await Task.Delay(50);
            host.BeginInvoke((Action)(async () =>
            {
                await Until(() => System.Windows.Forms.Application.OpenForms.OfType<AttachmentRecoveryForm>().Any());
                Button(System.Windows.Forms.Application.OpenForms.OfType<AttachmentRecoveryForm>().Single(), "Hủy").PerformClick();
            }));
        });
        var cancel = actions.OpenAsync(host, file, default, launched.Add);
        await actions.OpenAsync(host, file, default, launched.Add); // Repeated click must not create another dialog.
        await cancel; await cancelDriver;
        Assert.Empty(launched); Assert.Equal(saved, (await files.GetLocalLocationAsync(file)).Path);
        var driver = Signal();
        host.BeginInvoke((Action)(async () =>
        {
            try
            {
                await Until(() => System.Windows.Forms.Application.OpenForms.OfType<AttachmentRecoveryForm>().Any());
                var recovery = System.Windows.Forms.Application.OpenForms.OfType<AttachmentRecoveryForm>().Single();
                Assert.Contains(Children(recovery).OfType<TextBox>(), text => text.Text.Contains("không phục hồi"));
                Button(recovery, "Tạo lại từ bản gốc").PerformClick();
                await Until(() => System.Windows.Forms.Application.OpenForms.OfType<AttachmentSaveForm>().Any());
                var progress = System.Windows.Forms.Application.OpenForms.OfType<AttachmentSaveForm>().Single();
                await Until(() => Children(progress).OfType<Button>().Any(b => b.Text == "Đóng"));
                Button(progress, "Đóng").PerformClick(); driver.TrySetResult(true);
            }
            catch (Exception ex) { driver.TrySetException(ex); }
        }));
        await actions.OpenAsync(host, file, default, launched.Add); await driver.Task;
        Assert.Equal("original", File.ReadAllText(Assert.Single(launched))); Assert.False((await files.GetLocalLocationAsync(file)).UserSelected);
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public Task ComposerGrowsForVisualLinesCapsAtEightAndShrinksWithoutLosingCaret(int dpi) => OnUi(async host =>
    {
        using var chat = new ChatViewControl(); host.Controls.Add(chat); chat.SelectConversation("A", true);
        var input = Children(chat).OfType<ChatInputControl>().Single();
        var editor = Children(input).OfType<TextBox>().Single(t => t.AccessibleName == "Nhập tin nhắn");
        using var fonts = SimulateDpi(host, dpi); host.ClientSize = new System.Drawing.Size(800 * dpi / 96, 700 * dpi / 96);
        await Task.Delay(60); var empty = input.Height; editor.Focus();
        editor.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 6).Select(i => "Dòng " + i));
        editor.SelectionStart = 4; await Task.Delay(100);
        var six = input.Height; Assert.True(six > empty, $"Expected growth: empty {empty}, six {six}"); Assert.Equal(4, editor.SelectionStart);
        editor.Text = string.Join(Environment.NewLine, Enumerable.Repeat("Dòng", 8)); await Task.Delay(80); var eight = input.Height;
        editor.Text += string.Concat(Enumerable.Repeat(Environment.NewLine + "Thêm dòng", 20)); await Task.Delay(80); Assert.Equal(eight, input.Height);
        editor.Text = ""; await Task.Delay(80); Assert.Equal(empty, input.Height);
        editor.Text = string.Join(" ", Enumerable.Repeat("Nội dung tự quấn theo chiều rộng", 10)); await Task.Delay(80);
        Assert.True(input.Height > empty); var text = editor.Text;
        chat.Width = 400; await Task.Delay(80); Assert.Equal(text, editor.Text);
        input.RestoreDraft(new("Dòng một\r\nDòng hai\r\nDòng ba\r\n", [], null, null, "")); await Task.Delay(80);
        Assert.True(input.Height > empty); input.ResetDraft(); await Task.Delay(80); Assert.Equal(empty, input.Height);
    });

    [Fact]
    public Task ComposerGrowthKeepsHistoryAnchorAndAttachmentsReachableAfterSendFailure() => OnUi(async host =>
    {
        using var chat = new ChatViewControl(); host.Controls.Add(chat); chat.SelectConversation("A", true);
        var conversation = Guid.NewGuid(); var author = Guid.NewGuid();
        var messages = Enumerable.Range(0, 40).Select(i => new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation,
            SenderUserId = author, Text = "Tin nhắn " + i, CreatedAtTicks = DateTime.UtcNow.Ticks, IsRead = true }).ToArray();
        chat.Bind(conversation, messages, author, [], false); await Task.Delay(80);
        var history = Children(chat).OfType<MessageListControl>().Single();
        var flow = history.Controls.OfType<FlowLayoutPanel>().Single(); flow.AutoScrollPosition = new System.Drawing.Point(0, 300);
        await Task.Delay(50); var anchor = flow.Controls.OfType<MessageBubbleControl>().First(b => b.Bottom > 0); var top = anchor.Top;
        var input = Children(chat).OfType<ChatInputControl>().Single();
        var editor = Children(input).OfType<TextBox>().Single(t => t.AccessibleName == "Nhập tin nhắn");
        editor.Text = string.Join("\r\n", Enumerable.Repeat("Nội dung đang soạn", 12)); await Task.Delay(100);
        Assert.Equal(top, anchor.Top); Assert.False(history.AtBottom);
        var file = Path.Combine(Temp(), "draft.txt"); File.WriteAllText(file, "draft"); input.AddFiles([file]);
        host.ClientSize = new System.Drawing.Size(660, 450); await Task.Delay(80);
        var previews = Children(input).OfType<FlowLayoutPanel>().Single(p => p.AutoScroll);
        Assert.True(previews.Height > 0); Assert.True(Button(input, "Gửi").Bottom <= Button(input, "Gửi").Parent!.ClientSize.Height);
        chat.SendRequested += _ => throw new IOException("Lỗi gửi thử nghiệm");
        var text = editor.Text; Button(input, "Gửi").PerformClick(); await Until(() => !input.IsWorking);
        Assert.Equal(text, editor.Text); Assert.Equal(new[] { file }, input.Draft().Files);
        Assert.Contains(Children(input).OfType<Label>(), label => label.Text.Contains("Lỗi gửi thử nghiệm"));
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(captures)) { Directory.CreateDirectory(captures); using var image = new System.Drawing.Bitmap(chat.Width, chat.Height); chat.DrawToBitmap(image, chat.ClientRectangle); image.Save(Path.Combine(captures, "composer-small-failed-send.png")); }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public Task UpdateSettingsLayoutKeepsFooterAccessibleAtHighDpi(int dpi) => OnUi(async host =>
    {
        using var fixture = new Updates((_, _) => Task.FromResult(Manifest()));
        using var settings = new UpdateSettingsForm(fixture.Controller, [], null); settings.Show(host);
        Children(settings).OfType<ComboBox>().Single(c => c.AccessibleName == "Nguồn cập nhật").SelectedIndex = 2;
        using var fonts = SimulateDpi(settings, dpi); settings.Size = new System.Drawing.Size(520 * dpi / 96, 450 * dpi / 96);
        await Task.Delay(60);
        foreach (var button in Children(settings).OfType<Button>().Where(b => b.Text is "Lưu" or "Hủy"))
        {
            var bounds = button.RectangleToScreen(button.ClientRectangle); var visible = bounds;
            for (var parent = button.Parent; parent is not null; parent = parent.Parent) visible = System.Drawing.Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            Assert.Equal(bounds, visible);
        }
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captures))
        { Directory.CreateDirectory(captures); using var image = new System.Drawing.Bitmap(settings.Width, settings.Height); settings.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, settings.Size)); image.Save(Path.Combine(captures, $"update-settings-{dpi}.png")); }
        Children(settings).OfType<ComboBox>().Single(c => c.AccessibleName == "Nguồn cập nhật").SelectedIndex = 1;
        Children(settings).OfType<CheckBox>().Single(c => c.Text.Contains("nâng cao")).Checked = true;
        Children(settings).OfType<CheckBox>().Single(c => c.Text.StartsWith("Máy này")).Checked = true;
        await Task.Delay(50);
        if (!string.IsNullOrWhiteSpace(captures))
        { using var image = new System.Drawing.Bitmap(settings.Width, settings.Height); settings.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, settings.Size)); image.Save(Path.Combine(captures, $"update-settings-lan-{dpi}.png")); }
        Assert.True(Button(settings, "Lưu").Visible); Assert.True(Button(settings, "Hủy").Visible);
        // Restore selection before disposal; no unsaved-changes confirmation is needed in this layout check.
    });

    [Fact]
    public Task UpdateSettingsSaveFailureKeepsSourceAndEnteredAddress() => OnUi(async host =>
    {
        using var fixture = new Updates((_, _) => Task.FromResult(Manifest()));
        using var settings = new UpdateSettingsForm(fixture.Controller, [new(Guid.NewGuid(), "Mạng offline", null, 54546)], null); settings.Show(host);
        Children(settings).OfType<ComboBox>().Single(c => c.AccessibleName == "Nguồn cập nhật").SelectedIndex = 1;
        Children(settings).OfType<CheckBox>().Single(c => c.Text.Contains("nâng cao")).Checked = true;
        var address = Children(settings).OfType<TextBox>().Single(c => c.AccessibleName == "IP máy nguồn"); address.Text = "192.168.1.20";
        Button(settings, "Lưu").PerformClick(); await Task.Delay(100);
        Assert.False(settings.IsDisposed); Assert.Equal("192.168.1.20", address.Text); Assert.True(Button(settings, "Lưu").Enabled);
        Assert.Equal(UpdateSourceKind.GitHub, fixture.Controller.Service.Settings.Kind);
        Assert.Contains(Children(settings).OfType<Label>(), label => label.Text.Contains("đang kết nối"));
    });

    [Fact]
    public Task SourceChangeIsBlockedDuringDownloadAndResetsReadyStateAfterward() => OnUi(async host =>
    {
        var started = Signal(); var finish = Signal();
        using var fixture = new Updates(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Manifest();
            started.SetResult(true); await Wait(finish.Task, ct); return Payload();
        });
        var download = fixture.Controller.CheckAsync(true); await started.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.ConfigureAsync(new()));
        finish.SetResult(true); await download; Assert.True(fixture.Controller.State.Ready);
        var service = fixture.Controller.Service; service.Packages.InspectForTesting = _ => "v1.0.0";
        var source = Path.Combine(Temp(), "old.exe"); File.WriteAllText(source, "old"); var old = await service.Packages.ImportAsync(source);
        await fixture.Controller.ConfigureAsync(new(UpdateSourceKind.Local, Package: old));
        Assert.False(fixture.Controller.State.Ready); Assert.Null(fixture.Controller.State.Release);
        Assert.Contains("máy này", fixture.Controller.SourceName);
    });

    [Theory]
    [InlineData("abc.z.exe", false)]
    [InlineData("abc.z.exe", true)]
    [InlineData("Báo.cáo.tháng.11.xlsx", false)]
    [InlineData("archive.v1.tar.gz", false)]
    [InlineData("README", false)]
    public Task NativeSaveDialogReturnsCompleteMultiDotName(string name, bool overwrite) => OnUi(async host =>
    {
        var directory = Temp(); var target = Path.Combine(directory, name); if (overwrite) File.WriteAllText(target, "previous");
        using var picker = AttachmentSaveDialog.Create(name, directory); picker.Title = "LanChat native save verification";
        using var timer = new System.Windows.Forms.Timer { Interval = 120 }; var started = Stopwatch.StartNew(); var accepted = false;
        var thread = GetCurrentThreadId();
        timer.Tick += (_, _) => EnumThreadWindows(thread, (window, _) =>
        {
            var title = new System.Text.StringBuilder(260); GetWindowText(window, title, title.Capacity);
            if (overwrite) EnumChildWindows(window, (child, __) =>
            {
                var caption = new System.Text.StringBuilder(100); GetWindowText(child, caption, caption.Capacity);
                var label = caption.ToString().Replace("&", "").Trim();
                if (label is "Yes" or "Có") PostMessage(child, 0xF5, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            if (title.ToString() == picker.Title)
            {
                if (started.Elapsed > TimeSpan.FromSeconds(8)) PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
                else { PostMessage(window, 0x111, new IntPtr(1), IntPtr.Zero); accepted = true; }
            }
            else if (overwrite && GetDlgItem(window, 6) != IntPtr.Zero) PostMessage(window, 0x111, new IntPtr(6), IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        timer.Start(); var result = picker.ShowDialog(host); timer.Stop(); Assert.True(accepted); Assert.Equal(DialogResult.OK, result);
        Assert.Equal(target, picker.FileName);
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var source = Path.Combine(node.Options.DataDirectory, name); File.WriteAllText(source, "working contents");
        var files = (IFileTransferService)node.Host.Services.GetService(typeof(IFileTransferService))!; var file = await files.ImportLocalAsync(source);
        await files.SaveLocalAsync(file, picker.FileName, overwrite); Assert.Equal(target, await files.PrepareOpenAsync(file)); Assert.Equal("working contents", File.ReadAllText(target));
    });

    private delegate bool EnumWindow(IntPtr window, IntPtr data);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static void AssertDraft(ComposerDraft expected, ComposerDraft actual)
    {
        Assert.Equal(expected.Text, actual.Text); Assert.Equal(expected.Files, actual.Files);
        Assert.Equal(expected.Reply, actual.Reply); Assert.Equal(expected.Edit, actual.Edit); Assert.Equal(expected.Context, actual.Context);
    }
    private static IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => Children(c).Prepend(c));
    private static Button Button(Control parent, string text) => Children(parent).OfType<Button>().Single(button => button.Text == text);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "LanChat.UiTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("UI did not reach the expected state."); await Task.Delay(10); }
    }
    private static async Task Wait(Task gate, CancellationToken ct)
    {
        var canceled = Signal(); using var registration = ct.Register(() => canceled.TrySetCanceled());
        await await Task.WhenAny(gate, canceled.Task); ct.ThrowIfCancellationRequested();
    }
    private static Task OnUi(Func<Form, Task> test)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SetThreadDpiAwarenessContext(new IntPtr(-4));
                System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                using var host = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new System.Drawing.Size(1000, 700) };
                using var deadline = new System.Windows.Forms.Timer { Interval = 20000 };
                deadline.Tick += (_, _) => { result.TrySetException(new TimeoutException("Windows UI test timed out.")); host.Close(); };
                host.Shown += async (_, _) =>
                {
                    deadline.Start();
                    try { await test(host); result.TrySetResult(true); }
                    catch (Exception ex) { result.TrySetException(ex); }
                    finally { deadline.Stop(); host.Close(); }
                };
                System.Windows.Forms.Application.Run(host);
            }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    // Emulate a monitor scale without changing the user's Windows display settings.
    // .NET Framework stores DeviceDpi per control; Theme also reads this value for dynamic layout.
    private static FontScope SimulateDpi(Form form, int dpi)
    {
        var controls = Children(form).Prepend(form).ToArray();
        var scale = dpi / (float)form.DeviceDpi;
        var originalFonts = controls.Select(control => control.Font).ToArray();
        var dpiField = typeof(Control).GetField("deviceDpi", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dpiField);
        form.MinimumSize = System.Drawing.Size.Empty;
        foreach (var dpiForm in controls.OfType<DpiForm>()) dpiForm.SimulatedLayoutDpi = dpi;
        foreach (var control in controls) dpiField!.SetValue(control, dpi);
        form.Scale(new System.Drawing.SizeF(scale, scale));
        var fonts = new FontScope(form);
        for (var i = 0; i < controls.Length; i++)
        {
            var font = new System.Drawing.Font(originalFonts[i].FontFamily, originalFonts[i].SizeInPoints * scale, originalFonts[i].Style);
            fonts.Items.Add(font); controls[i].Font = font;
        }
        return fonts;
    }
    private sealed class FontScope(Form form) : IDisposable
    {
        public List<System.Drawing.Font> Items { get; } = [];
        public void Dispose() { form.Dispose(); foreach (var font in Items) font.Dispose(); }
    }

    private static readonly byte[] Setup = [1, 5, 9, 13];
    private static readonly string Digest = "sha256:" + FrameworkIO.Hex(FrameworkIO.Hash(Setup)).ToLowerInvariant();
    private static HttpResponseMessage Payload() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) };
    private static HttpResponseMessage Manifest() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { tag_name = "v9.12.3", draft = false, prerelease = false,
            assets = new[] { new { name = UpdateService.AssetName, state = "uploaded", size = Setup.Length, digest = Digest,
                browser_download_url = "https://github.com/dattiphu2022/LanChat/releases/download/v9.12.3/" + UpdateService.AssetName } } }))
    };
    private sealed class Updates : IDisposable
    {
        private readonly UpdateService _service;
        public UpdateCoordinator Controller { get; }
        public Updates(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        { _service = new UpdateService(Temp(), new Handler(send)); Controller = new UpdateCoordinator(_service); }
        public void Dispose() { Controller.Dispose(); _service.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
