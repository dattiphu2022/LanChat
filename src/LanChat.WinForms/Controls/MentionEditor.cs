using System.Globalization;
using LanChat.Application;

namespace LanChat.WinForms.Controls;

internal sealed record MentionCandidate(Guid UserId, string Name, string? Department, string Device, bool All = false)
{
    public override string ToString() => All ? "@All · Tất cả thành viên" : new PersonName(Name, Department) + (Device.Length > 0 ? " · " + Device : "");
}

/// <summary>Offsets always refer to the unformatted UTF-16 composer text.</summary>
internal sealed class MentionDraft
{
    private string _text = "";
    private MessageMention[] _values = [];
    private readonly List<(string Text, MessageMention[] Values)> _history = [];
    internal MessageMention[] Values => _values.Select(m => m with { UserIds = m.UserIds.ToArray() }).ToArray();
    internal void Reset(string text, MessageMention[]? values)
    {
        _text = text; _values = values ?? []; _history.Clear();
    }
    internal void Change(string text, bool undo = false)
    {
        if (text == _text) return;
        var restore = undo ? _history.FindLastIndex(item => item.Text == text) : -1;
        var snapshot = restore >= 0 ? _history[restore].Values : null;
        _history.Add((_text, Values)); if (_history.Count > 100) _history.RemoveAt(0);
        if (snapshot is not null) { _text = text; _values = snapshot; return; }
        var prefix = 0;
        while (prefix < _text.Length && prefix < text.Length && _text[prefix] == text[prefix]) prefix++;
        var suffix = 0;
        while (suffix < _text.Length - prefix && suffix < text.Length - prefix && _text[_text.Length - suffix - 1] == text[text.Length - suffix - 1]) suffix++;
        var oldEnd = _text.Length - suffix;
        var shift = text.Length - _text.Length;
        _values = _values.Where(m => m.Start + m.Length <= prefix || m.Start >= oldEnd)
            .Select(m => m.Start >= oldEnd ? m with { Start = m.Start + shift } : m).ToArray();
        _text = text;
    }
    internal void Add(MessageMention mention) => _values = _values.Append(mention).OrderBy(m => m.Start).ToArray();
    internal static bool Matches(string name, string query)
    {
        static string Fold(string value) => new string(value.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Replace('đ', 'd').Replace('Đ', 'D');
        return Fold(name).IndexOf(Fold(query), StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

public sealed partial class ChatInputControl
{
    private readonly MentionDraft _mentions = new();
    private MentionCandidate[] _mentionCandidates = [];
    private Guid[] _mentionAudience = [];
    private readonly MentionPopup _mentionPopup = new();
    private int _mentionStart = -1;
    private bool _restoringMentions;
    internal void ConfigureMentions(MentionCandidate[] candidates, Guid[] audience)
    {
        _mentionCandidates = candidates; _mentionAudience = audience;
        if (candidates.Length == 0) _mentionPopup.Hide();
    }
    private void InitializeMentions()
    {
        _text.InterceptKey = MentionKey;
        _text.TextChanged += (_, _) =>
        {
            if (!_restoringMentions) _mentions.Change(_text.Text, _text.IsUndoing);
            UpdateMentionSuggestions();
        };
        _text.KeyUp += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End) UpdateMentionSuggestions(); };
        _text.MouseUp += (_, _) => UpdateMentionSuggestions();
        _text.LostFocus += (_, _) => { if (!_mentionPopup.ContainsFocus) _mentionPopup.Hide(); };
        _mentionPopup.Chosen += InsertMention;
        Disposed += (_, _) => _mentionPopup.Dispose();
    }
    private void UpdateMentionSuggestions()
    {
        if (_restoringMentions || _text.IsComposing || !_text.Focused || _mentionCandidates.Length == 0 || _sending || _text.SelectionLength != 0)
        { _mentionPopup.Hide(); return; }
        var end = _text.SelectionStart;
        var start = end - 1;
        while (start >= 0 && _text.Text[start] != '@' && _text.Text[start] is not ('\r' or '\n') && end - start <= 100) start--;
        if (start < 0 || _text.Text[start] != '@' || (start > 0 && !char.IsWhiteSpace(_text.Text[start - 1]) && "([{,:;".IndexOf(_text.Text[start - 1]) < 0) ||
            _mentions.Values.Any(m => start >= m.Start && start < m.Start + m.Length))
        { _mentionPopup.Hide(); return; }
        var query = _text.Text.Substring(start + 1, end - start - 1);
        var candidates = _mentionCandidates.Where(c => MentionDraft.Matches(c.All ? "All" : c.Name, query)).Take(30).ToArray();
        if (candidates.Length == 0) { _mentionPopup.Hide(); return; }
        _mentionStart = start;
        _mentionPopup.Bind(candidates, _text);
    }
    internal bool DismissMentionSuggestions()
    {
        if (!_mentionPopup.Visible) return false;
        _mentionPopup.Hide(); return true;
    }
    private bool MentionKey(Keys keys)
    {
        if (!_mentionPopup.Visible || _text.IsComposing) return false;
        if (keys == Keys.Escape) { _mentionPopup.Hide(); return true; }
        if (keys is Keys.Up or Keys.Down) { _mentionPopup.MoveSelection(keys == Keys.Down ? 1 : -1); return true; }
        if (keys is Keys.Enter or Keys.Tab) { if (_mentionPopup.Selected is { } choice) InsertMention(choice); return true; }
        return false;
    }
    private void InsertMention(MentionCandidate candidate)
    {
        var end = _text.SelectionStart;
        if (_mentionStart < 0 || end < _mentionStart) return;
        var value = candidate.All ? "@All" : "@" + candidate.Name;
        if (_text.TextLength - (end - _mentionStart) + value.Length + 1 > _text.MaxLength) return;
        _mentionPopup.Hide();
        var start = _mentionStart;
        _restoringMentions = true;
        try
        {
            _text.Select(start, end - start); _text.ReplaceSelectionWithUndo(value + " ");
            _mentions.Change(_text.Text);
            _mentions.Add(new(start, value.Length, candidate.All, candidate.All ? _mentionAudience.ToArray() : [candidate.UserId]) { ResolveAtSend = candidate.All });
        }
        finally { _restoringMentions = false; }
        _text.Focus(); _text.Select(start + value.Length + 1, 0);
    }
    private void RestoreMentions(string text, MessageMention[]? values)
    {
        _restoringMentions = true;
        try { _text.Text = text; _mentions.Reset(text, values); _mentionPopup.Hide(); }
        finally { _restoringMentions = false; }
    }
    private sealed class MentionPopup : Form
    {
        private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, TabStop = false };
        internal event Action<MentionCandidate>? Chosen;
        internal MentionCandidate? Selected => _list.SelectedItem as MentionCandidate;
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
        internal MentionPopup()
        {
            Text = "Nhắc thành viên";
            FormBorderStyle = FormBorderStyle.FixedSingle; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Controls.Add(_list);
            _list.MouseClick += (_, _) => { if (Selected is { } choice) Chosen?.Invoke(choice); };
        }
        internal void MoveSelection(int delta) { if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, Math.Min(_list.Items.Count - 1, _list.SelectedIndex + delta)); }
        internal void Bind(MentionCandidate[] values, Control editor)
        {
            var old = Selected;
            _list.BeginUpdate(); _list.Items.Clear(); _list.Items.AddRange(values);
            _list.Font = editor.Font;
            _list.SelectedIndex = Math.Max(0, Array.FindIndex(values, item => item == old)); _list.EndUpdate();
            var dpi = editor.DeviceDpi / 96d;
            Size = new Size(Math.Min(Math.Max(editor.Width, (int)(280 * dpi)), (int)(560 * dpi)), (int)(Math.Min(7, values.Length) * (editor.Font.Height + 5) + 8));
            var point = editor.PointToScreen(Point.Empty); var area = Screen.FromControl(editor).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(point.X, area.Right - Width)), Math.Max(area.Top, point.Y - Height));
            if (!Visible && editor.FindForm() is { } owner) Show(owner);
        }
    }
}
