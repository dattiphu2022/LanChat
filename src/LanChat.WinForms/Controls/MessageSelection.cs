using System.Text;
using LanChat.Domain;
using QRCoder;
using Message = LanChat.Domain.Message;

namespace LanChat.WinForms.Controls;

internal sealed record QrText(string Text, int Count, int Bytes, string? Error)
{
    internal const int MaximumBytes = 500, MaximumVersion = 18;
    internal bool CanExport => Count > 0 && Text.Length > 0 && Error is null;
    internal static QrText From(IEnumerable<Message> messages)
    {
        var selected = messages.Where(m => !m.IsRecalled).OrderBy(m => m.CreatedAtTicks).ThenBy(m => m.Id).ToArray();
        var text = string.Join("\n\n", selected.Select(m => m.Text));
        try
        {
            var bytes = new UTF8Encoding(false, true).GetByteCount(text);
            return new(text, selected.Length, bytes, bytes > MaximumBytes ? "Vượt giới hạn 500 byte UTF-8. Hãy bỏ chọn bớt tin; nội dung không được cắt tự động." : null);
        }
        catch (EncoderFallbackException) { return new(text, selected.Length, -1, "Văn bản chứa ký tự Unicode không hợp lệ; không thể xuất nguyên nội dung sang UTF-8."); }
    }
    internal QRCodeData CreateCode()
    {
        if (!CanExport) throw new InvalidOperationException(Error ?? "Hãy chọn tin có văn bản để xuất QR.");
        var code = QRCodeGenerator.GenerateQrCode(Text, QRCodeGenerator.ECCLevel.M, forceUtf8: true,
            utf8BOM: false, eciMode: QRCodeGenerator.EciMode.Utf8);
        if (code.Version <= MaximumVersion) return code;
        code.Dispose(); throw new InvalidOperationException("Văn bản không vừa mã QR tối đa Version 18.");
    }
}

internal sealed class MessageSelection
{
    private readonly Dictionary<Guid, Message> _messages = [];
    private readonly HashSet<Guid> _selected = [];
    internal bool Active { get; private set; }
    internal int Available => _messages.Count;
    internal bool IsSelected(Guid id) => _selected.Contains(id);
    internal bool CanSelect(Guid id) => _messages.TryGetValue(id, out var message) && !message.IsRecalled;
    internal QrText Text => QrText.From(_selected.Where(_messages.ContainsKey).Select(id => _messages[id]));
    internal void Bind(IEnumerable<Message> messages, bool changedConversation)
    {
        if (changedConversation) End();
        _messages.Clear(); foreach (var message in messages) _messages[message.Id] = message;
        _selected.RemoveWhere(id => !CanSelect(id));
    }
    internal void Begin() => Active = true;
    internal void End() { Active = false; _selected.Clear(); }
    internal void Clear() => _selected.Clear();
    internal void All() { if (Active) foreach (var pair in _messages) if (CanSelect(pair.Key)) _selected.Add(pair.Key); }
    internal void Select(Guid id, bool selected)
    { if (Active && CanSelect(id)) { if (selected) _selected.Add(id); else _selected.Remove(id); } }
}
