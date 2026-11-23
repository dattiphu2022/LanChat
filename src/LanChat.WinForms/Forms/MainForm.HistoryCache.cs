using LanChat.Application;
using LanChat.Domain;
using LanChat.WinForms.Controls;
using Message = LanChat.Domain.Message;

namespace LanChat.WinForms.Forms;

public sealed partial class MainForm
{
    // Each network owns its cache. Bound both the number of conversations and
    // retained messages so long sessions cannot retain unlimited history.
    private readonly Dictionary<Guid, HistorySnapshot> _historyCache = [];
    private readonly LinkedList<Guid> _historyRecency = new();
    private long _boundRevision = -1;
    private MentionCandidate[] _mentionChoices = [];
    private Guid[] _mentionAudience = [];
    private sealed record HistorySnapshot(Message[] Messages, MessageDetails[] Details, bool More,
        long Revision, MentionCandidate[] Choices, Guid[] Audience);

    private void RememberHistory(Guid id, long revision, (Message[] Messages, MessageDetails[] Details, bool More) data)
    {
        var messages = data.Messages.Length <= 400 ? data.Messages : data.Messages.Skip(data.Messages.Length - 400).ToArray();
        var ids = new HashSet<Guid>(messages.Select(m => m.Id));
        _historyCache[id] = new(messages, data.Details.Where(d => ids.Contains(d.Message.Id)).ToArray(),
            data.Messages.Length > messages.Length || _hasOlder && (data.More || _olderMessages.Length > 0),
            revision, _mentionChoices, _mentionAudience);
        _historyRecency.Remove(id); _historyRecency.AddLast(id);
        // Giới hạn số lượng hội thoại trong cache để tránh RAM cao
        while (_historyRecency.Count > 12)
        {
            _historyCache.Remove(_historyRecency.First!.Value);
            _historyRecency.RemoveFirst();
            // Delay để tránh chiếm CPU cao khi dọn dẹp cache
            System.Threading.Thread.Sleep(5);
        }
    }

    private bool RestoreHistory(Guid id)
    {
        if (!_historyCache.TryGetValue(id, out var snapshot)) return false;
        _historyRecency.Remove(id); _historyRecency.AddLast(id);
        _olderMessages = snapshot.Messages.Length > PageSize ? snapshot.Messages : [];
        _hasOlder = snapshot.More;
        _mentionChoices = snapshot.Choices; _mentionAudience = snapshot.Audience;
        _chat.ConfigureMentions(_mentionChoices, _mentionAudience);
        BindHistory(id, null, _olderMessages, (snapshot.Messages, snapshot.Details, snapshot.More));
        _boundRevision = snapshot.Revision;
        return true;
    }
}
