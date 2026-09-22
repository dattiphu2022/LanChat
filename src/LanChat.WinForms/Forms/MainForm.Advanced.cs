using LanChat.Application;
using LanChat.Domain;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

public sealed partial class MainForm
{
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
            foreach (var path in submission.Files) attachments.Add(await Files.ImportLocalAsync(path, _lifetime.Token));
            await Distributed.SendToConversationAsync(conversation, submission.Text, submission.ReplyTo, attachments: attachments.ToArray(), ct: _lifetime.Token);
        }
        await RefreshAsync();
    }
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
                using (var select = new SelectionForm("Chuyển tiếp đến", conversations.Select(x => new SelectionItem(x.Id, x.DisplayName))))
                {
                    if (select.ShowDialog(this) != DialogResult.OK || select.SelectedId is not { } destination) return;
                    var details = await Distributed.GetMessageDetailsAsync(message.Id, _lifetime.Token);
                    await Distributed.SendToConversationAsync(destination, message.Text, forwardedFrom: message.Id,
                        attachments: details.Attachments.Select(x => x with { Id = Guid.CreateVersion7(), MessageId = Guid.Empty }).ToArray(), ct: _lifetime.Token);
                }
                break;
            default: if (action.StartsWith("react:", StringComparison.Ordinal)) await Distributed.ReactAsync(message.Id, action[6..], true, _lifetime.Token); break;
        }
        await RefreshAsync();
    }
    private async Task OpenAttachmentAsync(AttachmentDto file)
    {
        using var viewer = new AttachmentForm(file, Files); viewer.ShowDialog(this); await Task.CompletedTask;
    }
    private async Task SearchMessagesAsync()
    {
        using var form = new MessageSearchForm(Distributed, await _store.GetConversationsAsync(ct: _lifetime.Token));
        if (form.ShowDialog(this) == DialogResult.OK && form.SelectedConversation is { } id)
        {
            var conversations = await _store.GetConversationsAsync(ct: _lifetime.Token); var selected = conversations.FirstOrDefault(x => x.Id == id);
            if (selected is not null) { await SelectConversationAsync(id, selected.OtherUserId); _searchAnchor = form.SelectedMessage; _anchorScrolled = false; await RefreshAsync(); }
        }
    }
    private async Task OpenFilesAsync()
    {
        var attachments = await Distributed.GetAttachmentsAsync(ct: _lifetime.Token);
        using var select = new SelectionForm("Tệp trong các cuộc trò chuyện", attachments.Select(x => new SelectionItem(x.Id, $"{x.FileName} · {x.FileSize / 1024d:N1} KB")));
        if (select.ShowDialog(this) == DialogResult.OK && select.SelectedId is { } id) await OpenAttachmentAsync(attachments.Single(x => x.Id == id));
    }
}
