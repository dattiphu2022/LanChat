using LanChat.Application;
using LanChat.Domain;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.ComponentModel;

namespace LanChat.WinForms.Forms;

public sealed partial class MainForm
{
    private readonly AttachmentActions _attachmentActions;
    private readonly HashSet<Guid> _forwardingMessages = [];
    private readonly HashSet<string> _openingDialogs = [];
    private readonly ChatImageService _images;
#if DEBUG
    private Action<string>? _attachmentLaunchForVerification;
    private Func<GalleryImage[], Guid, Task>? _galleryForVerification;
#endif
    private IDistributedStore Distributed => _services.GetRequiredService<IDistributedStore>();
    private IConsensusService Consensus => _services.GetRequiredService<IConsensusService>();
    private IFileTransferService Files => _services.GetRequiredService<IFileTransferService>();
    private readonly SemaphoreSlim _attachmentImports = new(1);
    private readonly SemaphoreSlim _messageWrites = new(1);

    private async Task SendSubmissionAsync(ComposerSubmission submission)
    {
        if (_activeConversation is not { } conversation) throw new InvalidOperationException("Hãy chọn cuộc trò chuyện.");
        var ct = _lifetime.Token;
        AttachmentDto[] attachments = [];
        if (submission.Files.Length > 0)
        {
            await _attachmentImports.WaitAsync(ct);
            var importing = true;
            string? shownProgress = null;
            var progress = new Progress<string>(text =>
            {
                if (!importing || IsDisposed || ct.IsCancellationRequested || _activeConversation != conversation) return;
                _scanState.Text = shownProgress = text;
            });
            try
            {
                attachments = await Task.Run(async () =>
                {
                    var files = new List<AttachmentDto>(submission.Files.Length);
                    foreach (var path in submission.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        files.Add(Directory.Exists(path)
                            ? await Files.ImportFolderAsync(path, progress, ct).ConfigureAwait(false)
                            : await Files.ImportLocalAsync(path, ct).ConfigureAwait(false));
                    }
                    return files.ToArray();
                }, ct);
            }
            finally
            {
                importing = false;
                if (!IsDisposed && shownProgress is not null && _scanState.Text == shownProgress) _scanState.Text = "";
                _attachmentImports.Release();
            }
        }

        await _messageWrites.WaitAsync(ct);
        try
        {
            await Task.Run(async () =>
            {
                if (submission.EditId is { } edit)
                    await Distributed.EditMessageAsync(edit, submission.Text, ct, submission.Mentions).ConfigureAwait(false);
                else
                    await Distributed.SendToConversationAsync(conversation, submission.Text, submission.ReplyTo,
                        attachments: attachments, ct: ct, mentions: submission.Mentions).ConfigureAwait(false);
            }, ct);
        }
        finally { _messageWrites.Release(); }

        if (IsDisposed || ct.IsCancellationRequested || _activeConversation != conversation) return;
        try
        {
            await RefreshAsync();
            if (!IsDisposed && _activeConversation == conversation && !_searchAnchor.HasValue && !_jumpedInLatest) _chat.ScrollToEnd();
        }
        catch (Exception ex) { if (!IsDisposed && !ct.IsCancellationRequested) { _scanState.Text = UiText.HideIpAddresses(ex.Message); _scanState.ToolTipText = _scanState.Text; } }
    }
    private async Task MessageActionAsync(LanChat.Domain.Message message, string action)
    {
        switch (action)
        {
            case "save-all":
                await SaveMessageAttachmentsAsync(message.Id); return;
            case "reply": _chat.SetContext(message, false); break;
            case "edit": _chat.SetContext(message, true, (await Distributed.GetMessageDetailsAsync(message.Id, _lifetime.Token)).Mentions); break;
            case "recall": await Distributed.RecallMessageAsync(message.Id, _lifetime.Token); break;
            case "delete": await Distributed.DeleteLocalAsync(message.Id, _lifetime.Token); break;
            case "unreact": foreach (var emoji in new[] { "👍", "❤️", "😂", "😮", "😢" }) await Distributed.ReactAsync(message.Id, emoji, false, _lifetime.Token); break;
            case "forward": await RunDialogAsync("forward", () => ForwardMessagesAsync([message.Id])); break;
            default: if (action.StartsWith("react:", StringComparison.Ordinal)) await Distributed.ReactAsync(message.Id, action.Substring(6), true, _lifetime.Token); break;
        }
        await RefreshAsync();
    }

    private async Task ForwardMessagesAsync(Guid[] messageIds)
    {
        if (messageIds.Length == 0 || messageIds.Any(_forwardingMessages.Contains)) return;
        foreach (var id in messageIds) _forwardingMessages.Add(id);
        try
        {
            var contacts = await _store.GetUsersAsync(ct: _lifetime.Token);
            var conversations = await _store.GetConversationsAsync(ct: _lifetime.Token);
            var groups = await Distributed.GetGroupsAsync(_lifetime.Token);
            var destinations = conversations.Concat(groups.Where(g => !conversations.Any(c => c.Id == g.Id)).Select(g =>
                new ConversationSummary(g.Id, Guid.Empty, g.Name, "", 0, 0, false, false, PresenceStatus.Unknown, ConversationKind.Group))).ToArray();
            using var select = new ForwardRecipientsForm(contacts, destinations);
            if (UiInteraction.ShowDialog(select, this) != DialogResult.OK) return;
            var requests = messageIds.SelectMany(id => select.Selected.Select(destination => new ForwardRequest(id, destination))).ToArray();
            using var form = new ForwardBatchForm(_services.GetRequiredService<IChatService>(), requests, _lifetime.Token);
            UiInteraction.ShowDialog(form, this);
        }
        finally { foreach (var id in messageIds) _forwardingMessages.Remove(id); }
    }

    private async Task OpenAttachmentAsync(AttachmentDto file)
    {
        if (ChatImageService.IsImage(file)) { await RunDialogAsync("gallery", () => OpenImageGalleryAsync(file)); return; }
        await OpenExternalAttachmentAsync(file);
    }
    private Task OpenExternalAttachmentAsync(AttachmentDto file) => _attachmentActions.OpenAsync(this, file, _lifetime.Token
#if DEBUG
        , _attachmentLaunchForVerification
#endif
    );
    private Task AttachmentCommandAsync(AttachmentDto file, AttachmentCommand command) => command switch
    {
        AttachmentCommand.Open => OpenExternalAttachmentAsync(file),
        AttachmentCommand.Save => _attachmentActions.SaveAsync(this, file, _lifetime.Token),
        AttachmentCommand.SaveMessageImages => SaveMessageAttachmentsAsync(file.MessageId, true),
        _ => SaveMessageAttachmentsAsync(file.MessageId)
    };
    private async Task SaveMessageAttachmentsAsync(Guid messageId, bool imagesOnly = false)
    {
        var detail = await Distributed.GetMessageDetailsAsync(messageId, _lifetime.Token);
        var attachments = (await Distributed.GetAttachmentsAsync(detail.Message.ConversationId, _lifetime.Token))
            .Where(f => f.MessageId == messageId && (!imagesOnly || ChatImageService.IsImage(f))).ToArray();
        if (attachments.Length == 0) throw new InvalidOperationException("Tin nhắn không còn đính kèm có thể lưu.");
        await _attachmentActions.SaveBatchAsync(this, attachments, _lifetime.Token);
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
    private async Task RunDialogAsync(string key, Func<Task> action)
    {
        if (!_openingDialogs.Add(key)) return;
        _userOperations++;
        try { await action(); }
        finally { _openingDialogs.Remove(key); _userOperations--; DialogOperationCompleted?.Invoke(); }
    }
    private async Task OpenImageGalleryAsync(AttachmentDto file)
    {
        var items = await GetGalleryAsync(file);
        if (!items.Any(x => x.File.Id == file.Id)) throw new InvalidOperationException("Ảnh đã bị thu hồi hoặc không còn trong cuộc trò chuyện.");
#if DEBUG
        if (_galleryForVerification is not null) { await _galleryForVerification(items, file.Id); return; }
#endif
        using var gallery = new ImageGalleryForm(items, file.Id, _images, Files, _attachmentActions); UiInteraction.ShowDialog(gallery, this);
    }
    private async Task SearchMessagesAsync()
    {
        using var form = new MessageSearchForm(Distributed, await _store.GetConversationsAsync(ct: _lifetime.Token));
        if (UiInteraction.ShowDialog(form, this) == DialogResult.OK && form.SelectedConversation is { } id)
        {
            var conversations = await _store.GetConversationsAsync(ct: _lifetime.Token); var selected = conversations.FirstOrDefault(x => x.Id == id);
            if (selected is not null && form.SelectedMessage is Guid message)
            {
                await SelectConversationAsync(id);
                await JumpToMessageAsync(id, message);
            }
        }
    }
    private TransferCenterForm? _transferCenter;
    private Task OpenFilesAsync()
    {
        if (_transferCenter is { IsDisposed: false } existing) { existing.Show(); existing.Activate(); return Task.CompletedTask; }
        _transferCenter = new TransferCenterForm(_attachmentActions, _lifetime.Token, ct => Task.Run(async () =>
        {
            var attachments = await Distributed.GetAttachmentsAsync(ct: ct);
            var details = await Distributed.GetMessageDetailsBatchAsync(attachments.Select(f => f.MessageId).Distinct().ToArray(), ct);
            var names = (await _store.GetConversationsAsync(ct: ct)).ToDictionary(c => c.Id, c => new PersonName(c.DisplayName, c.Department).ToString());
            var messages = details.ToDictionary(d => d.Message.Id, d => d.Message.ConversationId);
            return attachments.Where(f => messages.ContainsKey(f.MessageId)).Select(f =>
                new TransferEntry(f, names.TryGetValue(messages[f.MessageId], out var name) ? name : "Hội thoại")).ToArray();
        }, ct), file => RunUiAsync(() => OpenAttachmentAsync(file), userOperation: true));
        _transferCenter.Show(TopLevelControl as Form ?? this);
        return Task.CompletedTask;
    }
}
