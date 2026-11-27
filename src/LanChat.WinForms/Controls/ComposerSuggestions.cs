using System.Globalization;
using System.Text.RegularExpressions;
using LanChat.Application;

namespace LanChat.WinForms.Controls;

internal sealed record ComposerSuggestion(string Phrase, string Emoji, string? Gif = null)
{
    private static readonly ComposerSuggestion[] Catalog =
    [
        new("xin chào", "👋", "Xin-chao"),
        new("cảm ơn", "🙏"),
        new("chúc mừng", "🎉", "Chuc-mung"),
        new("sinh nhật", "🎂", "Sinh-nhat"),
        new("tuyệt vời", "👍"),
        new("tạm biệt", "👋"),
        new("haha", "😂"),
        new("yêu", "❤️", "Yeu")
    ];
    private static readonly Regex Spaces = new(@"[^\S\r\n]+");
    private static readonly Regex BareLink = new(@"\A(?:[a-z\d-]+\.)+[a-z]{2,}(?=[/:?#@]|$|\p{P})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly char[] TokenEdges = ['(', '[', '{', '<', '"', '\'', '“', '‘', '«'];

    internal static (ComposerSuggestion Item, int End)? Match(string text, int caret, MessageMention[] mentions)
    {
        var wordsEnd = caret;
        while (wordsEnd > 0 && (IsInlineSpace(text[wordsEnd - 1]) || char.IsPunctuation(text[wordsEnd - 1]) && !IsWord(text[wordsEnd - 1])))
            wordsEnd--;
        if (wordsEnd == 0 || wordsEnd < text.Length && IsWord(text[wordsEnd])) return null;

        // Match against the source offsets before folding accents or collapsing spaces.
        (ComposerSuggestion, int)? result = null;
        var start = wordsEnd;
        for (var count = 0; count < 2; count++)
        {
            if (start == 0 || !IsWord(text[start - 1])) break;
            while (start > 0 && IsWord(text[start - 1])) start--;
            var folded = MentionDraft.Fold(Spaces.Replace(text.Substring(start, wordsEnd - start), " "));
            var item = Catalog.FirstOrDefault(entry => string.Equals(MentionDraft.Fold(entry.Phrase), folded, StringComparison.OrdinalIgnoreCase));
            if (item is not null && !mentions.Any(m => m.Start < wordsEnd && m.Start + m.Length > start)
                && !IsLinkOrMention(text, start, wordsEnd))
                result = (item, wordsEnd);
            if (start == 0 || !IsInlineSpace(text[start - 1])) break;
            while (start > 0 && IsInlineSpace(text[start - 1])) start--;
        }
        return result;
    }

    private static bool IsInlineSpace(char c) => char.IsWhiteSpace(c) && c is not ('\r' or '\n');

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' ||
        char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static bool IsLinkOrMention(string text, int start, int end)
    {
        var tokenStart = start;
        var tokenEnd = end;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1])) tokenStart--;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd])) tokenEnd++;
        var token = text.Substring(tokenStart, tokenEnd - tokenStart).TrimStart(TokenEdges);
        return token.StartsWith("@", StringComparison.Ordinal) || token.IndexOf("://", StringComparison.Ordinal) >= 0 ||
            token.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || BareLink.IsMatch(token);
    }

    internal Stream OpenGif() => typeof(ComposerSuggestion).Assembly.GetManifestResourceStream($"LanChat.Suggestions.{Gif}.gif")
        ?? throw new InvalidDataException("Không tìm thấy ảnh động.");
}

public sealed partial class ChatInputControl
{
    private readonly FlowLayoutPanel _suggestions = new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Visible = false };
    private readonly Button _emojiSuggestion = new();
    private readonly GifSuggestionButton _gifSuggestion = new();
    private (ComposerSuggestion Item, int End)? _suggestion;
    private bool _suggestionsQueued;
    private int SuggestionHeight => _suggestion.HasValue ? 36 : 0;

    private void InitializeSuggestions()
    {
        var emojiFont = new Font("Segoe UI Emoji", 14);
        _emojiSuggestion.Font = emojiFont;
        _emojiSuggestion.Disposed += (_, _) => emojiFont.Dispose();
        foreach (var button in new[] { _emojiSuggestion, _gifSuggestion })
        {
            button.Margin = new Padding(2, 0, 2, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Theme.Border;
            button.FlatAppearance.MouseOverBackColor = Theme.Selection;
            button.BackColor = Theme.Surface;
            button.ForeColor = Theme.Ink;
        }
        _suggestions.Controls.AddRange([_emojiSuggestion, _gifSuggestion]);
        _emojiSuggestion.Click += (_, _) =>
        {
            if (_suggestion is not { } suggestion) return;
            var value = " " + suggestion.Item.Emoji;
            if (_text.TextLength + value.Length > _text.MaxLength) return;
            _text.Focus();
            _text.Select(suggestion.End, 0);
            _text.ReplaceSelectionWithUndo(value);
            RefreshSuggestions();
        };
        _gifSuggestion.Click += async (_, _) =>
        {
            if (_suggestion is not { } suggestion || suggestion.Item.Gif is null) return;
            _text.Focus();
            await PrepareDraftImageAsync(async store =>
                await store.StageGifAsync(suggestion.Item, _draftLifetime));
        };
        _text.GotFocus += (_, _) => QueueSuggestions();
        _text.KeyUp += (_, _) => QueueSuggestions();
        _text.MouseUp += (_, _) => QueueSuggestions();
        _text.SelectionSet += (_, _) => QueueSuggestions();
        _text.LostFocus += (_, _) => QueueSuggestions();
        _suggestions.Leave += (_, _) => QueueSuggestions();
        _text.CompositionChanged += (_, _) => RefreshSuggestions();
        _mentionPopup.VisibleChanged += (_, _) => QueueSuggestions();
        EnabledChanged += (_, _) => RefreshSuggestions();
        VisibleChanged += (_, _) => RefreshSuggestions();
    }

    private void QueueSuggestions()
    {
        if (_suggestionsQueued || Disposing || IsDisposed || !IsHandleCreated) return;
        _suggestionsQueued = true;
        BeginInvoke((Action)(() =>
        {
            _suggestionsQueued = false;
            if (!Disposing && !IsDisposed) RefreshSuggestions();
        }));
    }

    private void RefreshSuggestions()
    {
        if (Disposing || IsDisposed) return;
        var match = Enabled && Visible && (_text.Focused || _suggestions.ContainsFocus) &&
            !_text.IsComposing && !_mentionPopup.Visible && !_processingImage && _text.SelectionLength == 0
            ? ComposerSuggestion.Match(_text.Text, _text.SelectionStart, _mentions.Values) : null;
        SetSuggestion(match, CanAttach && _draftImages is not null && _paths.Count < MaxFiles);
    }

    private void SetSuggestion((ComposerSuggestion Item, int End)? suggestion, bool canAttach)
    {
        var showGif = canAttach && suggestion?.Item.Gif is not null;
        _suggestion = suggestion;
        _emojiSuggestion.Text = suggestion?.Item.Emoji ?? "";
        _emojiSuggestion.AccessibleName = suggestion is { } value ? $"Chèn {value.Item.Emoji} cho {value.Item.Phrase}" : "";
        _gifSuggestion.Bind(showGif ? suggestion?.Item : null);
        _gifSuggestion.Visible = showGif;
        _suggestions.Visible = suggestion.HasValue;
        QueueHeight();
    }
}
