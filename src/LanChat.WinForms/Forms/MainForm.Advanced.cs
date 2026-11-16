using LanChat.Application;
using LanChat.Domain;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.ComponentModel;

namespace LanChat.WinForms.Forms;

public sealed partial class MainForm
{
    private readonly HashSet<Guid> _openingAttachments = [];
    private readonly ChatImageService _images;
#if DEBUG
    private Action<string>? _attachmentLaunchForVerification;
    private Func<GalleryImage[], Guid, Task>? _galleryForVerification;
#endif
    private IDistributedStore Distributed => _services.GetRequiredService<IDistributedStore>();
    private IConsensusService Consensus => _services.GetRequiredService<IConsensusService>();
    private IFileTransferService Files => _services.GetRequiredService<IFileTransferService>();
    private async Task SendSubmissionAsync(ComposerSubmission submission)
    {
        if (_activeConversation is not { } conversation) throw new InvalidOperationException("Hãy chọn cuộc trò chuyện.");
        if (submission.EditId is { } edit) await Distributed.EditMessageAsync(edit, submission.Text, _lifetime.Token);
        else
        {
            var attachments = new List<AttachmentDto>();
            var preparingFolder = false;
            try
            {
                foreach (var path in submission.Files)
                {
                    if (Directory.Exists(path))
                    {
                        preparingFolder = true;
                        var progress = new Progress<string>(text => { if (preparingFolder && !IsDisposed) _scanState.Text = text; });
                        attachments.Add(await Files.ImportFolderAsync(path, progress, _lifetime.Token));
                    }
                    else attachments.Add(await Files.ImportLocalAsync(path, _lifetime.Token));
                }
            }
            finally
            {
                if (preparingFolder && !IsDisposed) _scanState.Text = "";
                preparingFolder = false;
            }
            await Distributed.SendToConversationAsync(conversation, submission.Text, submission.ReplyTo, attachments: attachments.ToArray(), ct: _lifetime.Token);
        }
        if (_activeConversation != conversation)
        {
            if (_drafts.TryGetValue(conversation, out var draft) && MatchesSubmission(draft, submission)) _drafts.Remove(conversation);
            return;
        }
        if (MatchesSubmission(_chat.Draft(), submission)) _chat.ResetDraft();
        try
        {
            await RefreshAsync();
            if (_activeConversation == conversation && !_searchAnchor.HasValue) _chat.ScrollToEnd();
        }
        catch (Exception ex) { if (!IsDisposed && !_lifetime.IsCancellationRequested) _status.Text = UiText.HideIpAddresses(ex.Message); }
    }
    private static bool MatchesSubmission(ComposerDraft draft, ComposerSubmission submission) =>
        draft.Text == submission.Text && draft.Reply == submission.ReplyTo && draft.Edit == submission.EditId && draft.Files.SequenceEqual(submission.Files);
    private async Task MessageActionAsync(LanChat.Domain.Message message, string action)
    {
        switch (action)
        {
            case "reply": _chat.SetContext(message, false); break;
            case "edit": _chat.SetContext(message, true); break;
            case "recall": await Distributed.RecallMessageAsync(message.Id, _lifetime.Token); break;
            case "delete": await Distributed.DeleteLocalAsync(message.Id, _lifetime.Token); break;
            case "unreact": foreach (var emoji in new[] { "👍", "❤️", "😂", "😮", "😢" }) await Distributed.ReactAsync(message.Id, emoji, false, _lifetime.Token); break;
            case "forward":
                var conversations = await _store.GetConversationsAsync(ct: _lifetime.Token);
                using (var select = new SelectionForm("Chuyển tiếp đến", conversations.Select(x => new SelectionItem(x.Id, x.DisplayName, x.Department))))
                {
                    if (select.ShowDialog(this) != DialogResult.OK || select.SelectedId is not { } destination) return;
                    var details = await Distributed.GetMessageDetailsAsync(message.Id, _lifetime.Token);
                    await Distributed.SendToConversationAsync(destination, message.Text, forwardedFrom: message.Id,
                        attachments: details.Attachments.Select(x => x with { Id = LanChat.Domain.AppIdentity.NewId(), MessageId = Guid.Empty }).ToArray(), ct: _lifetime.Token);
                }
                break;
            default: if (action.StartsWith("react:", StringComparison.Ordinal)) await Distributed.ReactAsync(message.Id, action.Substring(6), true, _lifetime.Token); break;
        }
        await RefreshAsync();
    }
    private async Task OpenAttachmentAsync(AttachmentDto file)
    {
        if (ChatImageService.IsImage(file)) { await OpenImageGalleryAsync(file); return; }
        if (!_openingAttachments.Add(file.Id)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        using var progressForm = new AttachmentOpenProgressForm(file.FileName, cancellation, file.MimeType == FolderAttachment.MimeType);
        try
        {
            var prepare = Files.PrepareOpenAsync(file, new Progress<FileProgress>(progressForm.Report), cancellation.Token);
            // Cached files open directly; display progress only when preparation takes noticeable time.
            if (await Task.WhenAny(prepare, Task.Delay(350, cancellation.Token)) != prepare && !cancellation.IsCancellationRequested)
                progressForm.Show(this);
            var path = await prepare;
            cancellation.Token.ThrowIfCancellationRequested();
#if DEBUG
            if (_attachmentLaunchForVerification is not null) _attachmentLaunchForVerification(path);
            else
#endif
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open", WorkingDirectory = Path.GetDirectoryName(path) });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1155 or 31)
        { throw new InvalidOperationException("Windows chưa có ứng dụng mặc định để mở loại tệp này. Hãy cài hoặc chọn ứng dụng phù hợp rồi bấm lại tệp.", ex); }
        finally { progressForm.Close(); _openingAttachments.Remove(file.Id); }
    }
    private async Task<GalleryImage[]> GetGalleryAsync(AttachmentDto selected)
    {
        var detail = await Distributed.GetMessageDetailsAsync(selected.MessageId, _lifetime.Token);
        var files = (await Distributed.GetAttachmentsAsync(detail.Message.ConversationId, _lifetime.Token)).Where(ChatImageService.IsImage).ToLookup(x => x.MessageId);
        var messages = await Distributed.GetMessageDetailsBatchAsync(files.Select(g => g.Key).ToArray(), _lifetime.Token);
        return messages.OrderBy(x => x.Message.PhysicalTicks).ThenBy(x => x.Message.LogicalCounter).ThenBy(x => x.Message.OriginNodeId).ThenBy(x => x.Message.Id)
            .SelectMany(m => files[m.Message.Id].OrderBy(x => x.Id).Select(f =>
                new GalleryImage(f, m.Message.Text, new PersonName(m.SenderName ?? "", m.SenderDepartment), m.Message.CreatedAtTicks))).ToArray();
    }
    private async Task OpenImageGalleryAsync(AttachmentDto file)
    {
        var items = await GetGalleryAsync(file);
        if (!items.Any(x => x.File.Id == file.Id)) throw new InvalidOperationException("Ảnh đã bị thu hồi hoặc không còn trong cuộc trò chuyện.");
#if DEBUG
        if (_galleryForVerification is not null) { await _galleryForVerification(items, file.Id); return; }
#endif
        using var gallery = new ImageGalleryForm(items, file.Id, _images, Files); gallery.ShowDialog(this);
    }
    private async Task SearchMessagesAsync()
    {
        using var form = new MessageSearchForm(Distributed, await _store.GetConversationsAsync(ct: _lifetime.Token));
        if (form.ShowDialog(this) == DialogResult.OK && form.SelectedConversation is { } id)
        {
            var conversations = await _store.GetConversationsAsync(ct: _lifetime.Token); var selected = conversations.FirstOrDefault(x => x.Id == id);
            if (selected is not null) { await SelectConversationAsync(id); _searchAnchor = form.SelectedMessage; _anchorScrolled = false; await RefreshAsync(); }
        }
    }
    private async Task OpenFilesAsync()
    {
        var attachments = await Distributed.GetAttachmentsAsync(ct: _lifetime.Token);
        using var select = new SelectionForm("Tệp và thư mục trong các cuộc trò chuyện", attachments.Select(x => new SelectionItem(x.Id,
            x.MimeType == FolderAttachment.MimeType ? $"{x.FileName} · Thư mục" : $"{x.FileName} · {x.FileSize / 1024d:N1} KB")));
        if (select.ShowDialog(this) == DialogResult.OK && select.SelectedId is { } id)
        {
            var attachment = attachments.Single(x => x.Id == id);
            if (attachment.MimeType == FolderAttachment.MimeType) await OpenAttachmentAsync(attachment);
            else { using var viewer = new AttachmentForm(attachment, Files); viewer.ShowDialog(this); }
        }
    }
}
