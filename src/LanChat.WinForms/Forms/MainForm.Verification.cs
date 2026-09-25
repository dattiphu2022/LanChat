#if DEBUG
using System.Drawing.Imaging;
using System.Text.Json;
using LanChat.Application;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

public sealed partial class MainForm
{
    // Explicit debug-only integration harness: uses two separate processes and isolated data directories.
    internal async Task VerifyUiAsync(string reportPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        var captures = new List<string>();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            IReadOnlyList<UserSummary> contacts;
            do { await Task.Delay(200, deadline.Token); contacts = await _store.GetUsersAsync(ct: deadline.Token); } while (contacts.Count == 0);
            var other = contacts[0]; var profile = await _store.GetProfileAsync(deadline.Token);
            var conversation = await _store.OpenDirectAsync(other.UserId, deadline.Token);
            await SelectConversationAsync(conversation, other.UserId);
            var text = $"Xin chào từ {profile.DisplayName} 👋\nTin nhắn nhiều dòng qua TCP, không có server riêng.";
            await _chat.SubmitForVerificationAsync(text);
            IReadOnlyList<Domain.Message> messages;
            do
            {
                await Task.Delay(200, deadline.Token); messages = await _store.GetMessagesAsync(conversation, ct: deadline.Token);
            } while (messages.Count < 2 || await _store.GetPendingCountAsync(deadline.Token) != 0);
            foreach (var size in new[] { new Size(1240, 800), new Size(1020, 650) })
            {
                ClientSize = size; await RefreshAsync(); await Task.Delay(350, deadline.Token);
                var path = Path.ChangeExtension(reportPath, $"{size.Width}.png"); CaptureForm(this, path); captures.Add(path);
                static object Describe(Control c) => new { Type = c.GetType().Name, Bounds = c.Bounds.ToString(), Minimum = c.MinimumSize.ToString(), c.AutoSize, Dock = c.Dock.ToString(), Children = c.Controls.Cast<Control>().Select(Describe).ToArray() };
                await File.WriteAllTextAsync(Path.ChangeExtension(reportPath, $"{size.Width}.layout.json"), JsonSerializer.Serialize(Describe(this), new JsonSerializerOptions { WriteIndented = true }));
                VerifyVisibleControls();
            }
            var own = messages.Single(m => m.SenderUserId == profile.Id); var theirs = messages.Single(m => m.SenderUserId != profile.Id);
            await MessageActionAsync(own, "edit"); await _chat.SubmitForVerificationAsync("Nội dung đã sửa từ " + profile.DisplayName);
            await MessageActionAsync(theirs, "react:👍"); await MessageActionAsync(theirs, "reply");
            var textFile = Path.ChangeExtension(reportPath, "attachment.txt"); await File.WriteAllTextAsync(textFile, "Tệp thử nghiệm của " + profile.DisplayName, deadline.Token);
            var imageFile = Path.ChangeExtension(reportPath, "attachment.png");
            var portraitFile = Path.ChangeExtension(reportPath, "portrait.png");
            using (var bitmap = new Bitmap(800, 450)) { using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.CornflowerBlue); graphics.DrawString(profile.DisplayName + " · Ảnh ngang", Font, Brushes.White, new PointF(200, 200)); bitmap.Save(imageFile, ImageFormat.Png); }
            using (var bitmap = new Bitmap(450, 800)) { using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.SeaGreen); graphics.DrawString(profile.DisplayName + " · Ảnh dọc", Font, Brushes.White, new PointF(100, 350)); bitmap.Save(portraitFile, ImageFormat.Png); }
            ClientSize = new Size(1240, 800); _chat.DropForVerification([textFile, imageFile, portraitFile]); await Task.Delay(200, deadline.Token);
            var previewPath = Path.ChangeExtension(reportPath, "drop-preview.png"); CaptureForm(this, previewPath); captures.Add(previewPath);
            await _chat.SubmitForVerificationAsync("Bộ ảnh của " + profile.DisplayName + "\nChú thích chung cho ảnh ngang và ảnh dọc.");
            // The recipient must still receive the sent snapshot after originals change or disappear.
            await File.WriteAllTextAsync(textFile, "Nội dung đã sửa sau khi gửi", deadline.Token);
            File.Delete(imageFile); File.Delete(portraitFile);
            AttachmentDto[] attachments;
            do { await Task.Delay(200, deadline.Token); messages = await _store.GetMessagesAsync(conversation, ct: deadline.Token); attachments = await Distributed.GetAttachmentsAsync(conversation, deadline.Token); }
            while (messages.Count != 4 || attachments.Length != 6 || messages.Count(m => m.IsEdited) != 2);
            static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => Descendants(c).Prepend(c));
            var inlineImagesLoaded = 0;
            await RefreshAsync();
            foreach (var message in messages.Where(m => attachments.Any(f => f.MessageId == m.Id)))
            {
                _chat.FocusMessage(message.Id); await Task.Delay(300, deadline.Token);
            }
            do { await Task.Delay(200, deadline.Token); inlineImagesLoaded = Descendants(this).OfType<ImageThumbnailControl>().Count(c => c.HasImage); } while (inlineImagesLoaded != 4);
            foreach (var file in attachments.Where(ChatImageService.IsImage))
                if (await Files.GetLocalPathAsync(file.Sha256, deadline.Token) is null) throw new InvalidOperationException("Ảnh inline chưa tự tải về local.");
            foreach (var size in new[] { new Size(1240, 800), new Size(1020, 650) })
            {
                ClientSize = size; await RefreshAsync(); _chat.FocusMessage(messages.Last(m => attachments.Any(f => f.MessageId == m.Id)).Id);
                do { await Task.Delay(200, deadline.Token); inlineImagesLoaded = Descendants(this).OfType<ImageThumbnailControl>().Count(c => c.HasImage); } while (inlineImagesLoaded < 2);
                var path = Path.ChangeExtension(reportPath, $"inline-images-{size.Width}.png"); CaptureForm(this, path); captures.Add(path);
            }
            var openedPaths = new List<string>();
            var verifiedGalleries = 0;
            _attachmentLaunchForVerification = path => openedPaths.Add(path);
            _galleryForVerification = async (items, selected) =>
            {
                if (items.Length != 4 || items.Any(x => !x.Caption.Contains(x.Sender))) throw new InvalidOperationException("Album thiếu ảnh hoặc chú thích không đúng người gửi.");
                using var gallery = new ImageGalleryForm(items, selected, _images, Files);
                gallery.Show(this); await gallery.VerifyAsync();
                if (verifiedGalleries == 0)
                {
                    foreach (var size in new[] { new Size(1080, 800), new Size(780, 580) })
                    {
                        gallery.ClientSize = size; await Task.Delay(300, deadline.Token); gallery.VerifyLayout();
                        var path = Path.ChangeExtension(reportPath, $"gallery-{size.Width}.png"); CaptureForm(gallery, path); captures.Add(path);
                    }
                }
                verifiedGalleries++; gallery.Close();
            };
            foreach (var attachment in attachments)
            {
                // Exercise the same click handler; capture the OS handoff without launching external user apps.
                await OpenAttachmentAsync(attachment);
                var path = ChatImageService.IsImage(attachment) ? await Files.DownloadAsync(attachment, ct: deadline.Token)
                    : openedPaths.LastOrDefault() ?? throw new InvalidOperationException("Chưa chuyển tệp sang thao tác mở.");
                if (!ChatImageService.IsImage(attachment) && Path.GetFileName(path) != attachment.FileName) throw new InvalidDataException("Tệp mở bị mất tên hoặc phần mở rộng.");
                await using var input = File.OpenRead(path);
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, deadline.Token));
                if (!hash.Equals(attachment.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Tệp nhận không khớp SHA256.");
            }
            await OpenAttachmentAsync(attachments.First(x => !ChatImageService.IsImage(x)));
            if (openedPaths.Count != 3 || openedPaths[0] != openedPaths[^1]) throw new InvalidOperationException("Bấm lại chưa tái sử dụng tệp local.");
            _attachmentLaunchForVerification = null; _galleryForVerification = null;
            if (profile.DisplayName.EndsWith("A", StringComparison.Ordinal))
            {
                var command = new ControlCommand(Guid.CreateVersion7(), Domain.ControlCommandType.GroupCreated, profile.Id, Guid.CreateVersion7(), "Nhóm thử nghiệm LAN", Members: [profile.Id, other.UserId]);
                ControlResult result;
                do { await Task.Delay(500, deadline.Token); result = await Consensus.ProposeAsync(command, deadline.Token); } while (!result.Success);
            }
            GroupSummary[] groups;
            do { await Task.Delay(250, deadline.Token); groups = await Distributed.GetGroupsAsync(deadline.Token); } while (groups.Length == 0);
            var group = groups[0]; await SelectConversationAsync(group.Id, Guid.Empty); await _chat.SubmitForVerificationAsync("Xin chào nhóm từ " + profile.DisplayName);
            IReadOnlyList<Domain.Message> groupMessages;
            do { await Task.Delay(200, deadline.Token); groupMessages = await _store.GetMessagesAsync(group.Id, ct: deadline.Token); } while (groupMessages.Count < 3);
            await RefreshAsync(); await Task.Delay(300, deadline.Token);
            var groupPath = Path.ChangeExtension(reportPath, "group-chat.png"); CaptureForm(this, groupPath); captures.Add(groupPath);
            using (var manager = ActivatorUtilities.CreateInstance<GroupsForm>(_services))
            { manager.Show(this); await Task.Delay(500, deadline.Token); var path = Path.ChangeExtension(reportPath, "groups.png"); CaptureForm(manager, path); captures.Add(path); manager.Close(); }
            using (var settings = ActivatorUtilities.CreateInstance<SettingsForm>(_services))
            {
                settings.Show(this); await Task.Delay(350, deadline.Token);
                var path = Path.ChangeExtension(reportPath, "settings.png"); CaptureForm(settings, path); captures.Add(path); settings.Close();
            }
            using (var diagnostics = ActivatorUtilities.CreateInstance<ClusterDiagnosticsForm>(_services))
            {
                diagnostics.Show(this); await Task.Delay(350, deadline.Token);
                var path = Path.ChangeExtension(reportPath, "diagnostics.png"); CaptureForm(diagnostics, path); captures.Add(path); diagnostics.Close();
            }
            var identity = await _store.GetIdentityAsync(deadline.Token);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Success = true, identity.NodeId, identity.UserId,
                Messages = messages.Select(x => new { x.Id, x.Text, x.Status, x.IsEdited, x.ReplyToMessageId }).ToArray(),
                GroupId = group.Id, GroupMessageIds = groupMessages.Select(x => x.Id).ToArray(), VerifiedFiles = attachments.Length, VerifiedOpenRequests = openedPaths.Count,
                VerifiedGalleries = verifiedGalleries, InlineImagesLoaded = inlineImagesLoaded, VerifiedSourceChanges = 3,
                Consensus = await Consensus.GetStatusAsync(deadline.Token), Captures = captures }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
            var partnerReport = Path.Combine(Path.GetDirectoryName(reportPath)!, profile.DisplayName.EndsWith("A", StringComparison.Ordinal) ? "B.json" : "A.json");
            while (!File.Exists(partnerReport)) await Task.Delay(200, deadline.Token);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Success = false, Error = ex.ToString(), Captures = captures }));
        }
        finally { Close(); }
    }
    private void VerifyVisibleControls()
    {
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => Descendants(c).Prepend(c));
        var controls = Descendants(this).ToArray();
        if (controls.OfType<MessageBubbleControl>().Count() < 2) throw new InvalidOperationException("UI chưa render đủ tin nhắn.");
        foreach (var control in controls.Where(c => c is NavigationItemControl or MessageBubbleControl or ChatInputControl))
        {
            var original = control.RectangleToScreen(control.ClientRectangle); var visible = original;
            for (var parent = control.Parent; parent is not null; parent = parent.Parent)
                visible = Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            if (!control.Visible || original.Width < 80 || original.Height < 25 || visible.Width < original.Width * 0.9 || visible.Height < original.Height * 0.9)
                throw new InvalidOperationException($"Control bị khuất: {control.GetType().Name}, bounds={original}, visible={visible}");
        }
    }
    private static void CaptureForm(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, form.ClientRectangle with { Width = form.Width, Height = form.Height });
        bitmap.Save(path, ImageFormat.Png);
    }
}
#endif
