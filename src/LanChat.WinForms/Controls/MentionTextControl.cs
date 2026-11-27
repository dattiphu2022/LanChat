using LanChat.Application;
using System.Text.RegularExpressions;

namespace LanChat.WinForms.Controls;

internal sealed class MentionTextControl : RichTextBox
{
    private string? _signature;
    private Color _foreground;
    private (int Start, int Length)[] _ranges = [];
    private Match[] _emoji = [];
    internal MentionTextControl()
    {
        ReadOnly = true; BorderStyle = BorderStyle.None; DetectUrls = false; ScrollBars = RichTextBoxScrollBars.None;
        Multiline = true; WordWrap = true; TabStop = false; Dock = DockStyle.Fill;
        Font = Theme.DefaultFont;
    }
    internal void BindText(string text, MessageMention[] mentions, Guid local, string prefix, Color background, Color foreground, bool center)
    {
        var highlights = mentions.Where(m => m.UserIds.Contains(local)).ToArray();
        var signature = prefix + text + "\u0001" + string.Join(";", highlights.Select(m => $"{m.Start}:{m.Length}")) + $"/{background.ToArgb()}/{foreground.ToArgb()}/{center}";
        if (_signature == signature) return;
        _signature = signature;
        var start = SelectionStart; var length = SelectionLength;
        _foreground = foreground;
        var ranges = highlights.Length == 0 ? null : new List<(int, int)>(highlights.Length);
        // RichEdit normalizes CRLF to LF. Stored offsets stay in the original text.
        var display = (prefix + text).Replace("\r\n", "\n").Replace('\r', '\n');
        _emoji = WindowsEmoji.Sequences.Matches(display).Cast<Match>().ToArray();
        Text = display; BackColor = background;
        SelectAll(); SelectionColor = foreground; SelectionBackColor = background;
        SelectionAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        foreach (var mention in highlights)
        {
            if (mention.Start < 0 || mention.Length <= 0 || mention.Start > text.Length - mention.Length) continue;
            var before = (prefix + text.Substring(0, mention.Start)).Replace("\r\n", "\n").Replace('\r', '\n').Length;
            var count = text.Substring(mention.Start, mention.Length).Replace("\r\n", "\n").Replace('\r', '\n').Length;
            ranges!.Add((before, count));
        }
        _ranges = ranges?.ToArray() ?? [];
        PaintHighlights();
        Select(Math.Min(start, TextLength), Math.Min(length, Math.Max(0, TextLength - start)));
    }

    internal void ApplyTheme()
    {
        if (_signature is null) return;
        var start = SelectionStart;
        var length = SelectionLength;
        SelectAll(); SelectionColor = Theme.Adapt(_foreground); SelectionBackColor = BackColor;
        PaintHighlights();
        Select(start, length);
    }

    private void PaintHighlights()
    {
        foreach (var (start, length) in _ranges)
        {
            Select(start, length); SelectionColor = Theme.Blue;
            SelectionBackColor = Theme.Adapt(Color.FromArgb(218, 235, 255));
        }
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        base.WndProc(ref m);
        if (_emoji is not { Length: > 0 } || !WindowsEmojiGlyphs.IsPaintMessage(m.Msg)) return;
        WindowsEmojiGlyphs.PaintTextBox(this, m.WParam, _emoji, EmojiColors);
    }

    private (Color Foreground, Color Background) EmojiColors(Match emoji) =>
        _ranges.Any(range => range.Start <= emoji.Index && range.Start + range.Length >= emoji.Index + emoji.Length)
            ? (Theme.Blue, Theme.Adapt(Color.FromArgb(218, 235, 255))) : (Theme.Adapt(_foreground), BackColor);
}
