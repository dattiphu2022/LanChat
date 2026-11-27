using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LanChat.Application;
using LanChat.WinForms.Controls;
using Xunit;

namespace LanChat.Tests;

public sealed partial class WindowsUiTests
{
    [Fact]
    public Task MultilinePlaceholderRemainsVisibleWhenFocusedAndNeverBecomesInput() => OnUi(host =>
    {
        using var editor = new PromptTextBox { Multiline = true, Width = 300, Height = 60, BackColor = Color.White };
        host.Controls.Add(editor);
        Assert.True(editor.Focus());
        using var withoutHint = PrintClient(editor);
        editor.PlaceholderText = "Dán mã mời nhận từ máy khác.";
        using var withHint = PrintClient(editor);
        Assert.Empty(editor.Text);
        Assert.True(PixelsDiffer(withoutHint, withHint));
        editor.Text = "Mã thử nghiệm";
        using var filled = PrintClient(editor);
        editor.PlaceholderText = "";
        using var filledWithoutHint = PrintClient(editor);
        Assert.False(PixelsDiffer(filled, filledWithoutHint));
        Assert.Equal("Mã thử nghiệm", editor.Text);
        return Task.CompletedTask;
    });

    private static bool PixelsDiffer(Bitmap a, Bitmap b) => Enumerable.Range(0, a.Height).Any(y =>
        Enumerable.Range(0, a.Width).Any(x => a.GetPixel(x, y) != b.GetPixel(x, y)));

    private static Bitmap PrintClient(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try { SendEditorMessage(control.Handle, 0x0318, dc, (IntPtr)4); }
        finally { graphics.ReleaseHdc(dc); }
        return bitmap;
    }

    [Fact]
    public Task WindowsEmojiUsesColorGlyphsAndPreservesOriginalText() => OnUi(host =>
    {
        const string text = "Xin chào 😀 👋🏽 👩‍💻 ❤️ 1️⃣";
        Assert.Equal(new[] { "😀", "👋🏽", "👩‍💻", "❤️", "1️⃣" }, WindowsEmoji.Sequences.Matches(text).Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value));
        var glyph = WindowsEmojiGlyphs.Get("😀", new Size(40, 40), 32, Color.Black, Color.White);
        Assert.NotNull(glyph);
        Assert.True(HasGoldPixels(glyph!));
        using var body = new MentionTextControl { Dock = DockStyle.Top, Height = 40 };
        using var editor = new PromptTextBox { Dock = DockStyle.Bottom, Multiline = true, ShowColorEmoji = true, Height = 40, Text = text };
        host.Controls.Add(body);
        host.Controls.Add(editor);
        body.BindText(text, [], Guid.Empty, "", Color.White, Color.Black, false);
        foreach (var control in new Control[] { body, editor })
        {
            using var bitmap = PrintClient(control);
            Assert.True(HasGoldPixels(bitmap), $"Missing colored emoji in {control.GetType().Name}");
        }
        body.SelectAll();
        Assert.Equal(text, body.SelectedText);
        Assert.Equal(text, editor.Text);
        return Task.CompletedTask;
    });

    private static bool HasGoldPixels(Bitmap bitmap) => Enumerable.Range(0, bitmap.Height).Any(y =>
        Enumerable.Range(0, bitmap.Width).Any(x =>
        {
            var pixel = bitmap.GetPixel(x, y);
            return pixel.R > pixel.B + 30 && pixel.G > pixel.B + 30;
        }));
    [Theory]
    [InlineData("XIN   CHAO!", "xin chào", 10, 11)]
    [InlineData("Chúc mừng!", "chúc mừng", 9, 10)]
    [InlineData("Cảm ơn.", "cảm ơn", 8, 9)]
    [InlineData("mình yêu, ", "yêu", 8, 10)]
    [InlineData("xin chào, mọi người", "xin chào", 8, 9)]
    [InlineData("haha trong câu", "haha", 4, 4)]
    public void ComposerSuggestionsRecognizeVietnameseAtTheCaret(string text, string phrase, int end, int caret)
    {
        var match = ComposerSuggestion.Match(text, caret, []);
        Assert.Equal(phrase, match?.Item.Phrase);
        Assert.Equal(end, match?.End);
    }

    [Theory]
    [InlineData("hahaha", 6)]
    [InlineData("hahaha", 4)]
    [InlineData("https://example.com/haha", 24)]
    [InlineData("example.com/haha", 16)]
    [InlineData("(@haha)", 7)]
    [InlineData("haha_123", 8)]
    [InlineData("xin\nchào", 8)]
    public void ComposerSuggestionsIgnorePartialWordsLinksAndMentions(string text, int caret)
    {
        Assert.Null(ComposerSuggestion.Match(text, caret, []));
        Assert.Null(ComposerSuggestion.Match("@Người Yêu", 10, [new MessageMention(0, 10, false, [Guid.NewGuid()])]));
    }

    [Theory]
    [InlineData("xin chào")]
    [InlineData("chúc mừng")]
    [InlineData("sinh nhật")]
    [InlineData("yêu")]
    public void ComposerGifsDecodeAsDistinctFrames(string phrase)
    {
        var suggestion = ComposerSuggestion.Match(phrase, phrase.Length, [])!.Value.Item;
        using var source = suggestion.OpenGif();
        using var gif = new Bitmap(source);
        Assert.True(ImageAnimator.CanAnimate(gif));
        Assert.Equal(16, gif.GetFrameCount(FrameDimension.Time));
        using var first = new Bitmap(gif);
        gif.SelectActiveFrame(FrameDimension.Time, 3);
        using var later = new Bitmap(gif);
        Assert.Contains(Enumerable.Range(0, gif.Height), y =>
            Enumerable.Range(0, gif.Width).Any(x => first.GetPixel(x, y) != later.GetPixel(x, y)));
    }

    [Fact]
    public Task ImageViewportRepaintsAnimatedGifAndStopsWhenCleared() => OnUi(async host =>
    {
        using var viewport = new ImageViewportControl();
        host.Controls.Add(viewport);
        var source = ComposerSuggestion.Match("yêu", 3, [])!.Value.Item.OpenGif();
        var repaints = 0;
        viewport.Paint += (_, _) => repaints++;
        viewport.SetImage(new Bitmap(source), source);
        await Until(() => repaints >= 3);
        viewport.Image = null;
        Assert.False(source.CanRead);
        viewport.Refresh();
        var afterClear = repaints;
        await Task.Delay(260);
        Assert.Equal(afterClear, repaints);
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public Task ComposerSuggestionsFitCompactEditorAndHideDuringImeOrMention(int dpi) => OnUi(async host =>
    {
        using var composer = new ChatInputControl();
        using var draftImages = new DraftImageStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        host.Controls.Add(composer);
        using var fonts = SimulateDpi(host, dpi);
        host.ClientSize = new Size(270 * dpi / 96, 190 * dpi / 96);
        composer.ConfigureDraftImages(draftImages, CancellationToken.None);
        composer.ConfigureMentions([new(Guid.NewGuid(), "Chúc Mừng", null, "")], []);
        var editor = Children(composer).OfType<PromptTextBox>().Single();
        var gif = Children(composer).OfType<GifSuggestionButton>().Single();
        editor.Focus();
        editor.SelectedText = "chúc mừng";
        await Until(() => gif.Visible && gif.Height > 0);
        var bounds = gif.RectangleToScreen(gif.ClientRectangle);
        var row = gif.Parent.RectangleToScreen(gif.Parent.ClientRectangle);
        Assert.True(row.Contains(bounds));
        Assert.True(bounds.Bottom <= editor.RectangleToScreen(editor.ClientRectangle).Top);
        SendEditorMessage(editor.Handle, 0x010D, IntPtr.Zero, IntPtr.Zero);
        Assert.False(gif.Visible);
        SendEditorMessage(editor.Handle, 0x010E, IntPtr.Zero, IntPtr.Zero);
        await Until(() => gif.Visible);
        editor.SelectAll();
        editor.SelectedText = "@chúc mừng";
        await Until(() => !gif.Visible);
        Assert.True(editor.InterceptKey!(Keys.Enter));
        Assert.Equal("@Chúc Mừng ", editor.Text);
        Assert.Single(composer.Draft().Mentions);
    });

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendEditorMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [Fact]
    public Task ComposerSuggestionClickStagesGifRestoresFailedDraftAndRemovesFile() => OnUi(async host =>
    {
        using var composer = new ChatInputControl();
        using var draftImages = new DraftImageStore(Path.Combine(Path.GetTempPath(), $"lanchat-gif-{Guid.NewGuid():N}"));
        host.Controls.Add(composer);
        composer.ConfigureDraftImages(draftImages, CancellationToken.None);
        ComposerDraft? retained = null;
        composer.DraftFilesChanged += () =>
        {
            if (!composer.IsWorking)
                draftImages.Prune(composer.Draft().AllFiles.Concat(composer.FailedDraftFiles).Concat(retained?.AllFiles ?? []));
        };
        var editor = Children(composer).OfType<PromptTextBox>().Single();
        var gifButton = Children(composer).OfType<GifSuggestionButton>().Single();
        editor.Focus();
        editor.SelectedText = "xin chào!";
        await Until(() => gifButton.Visible);
        Assert.True(gifButton.Bottom > 0);
        var emoji = Button(composer, "👋");
        emoji.PerformClick();
        Assert.Equal("xin chào 👋!", editor.Text);
        Assert.True(editor.Focused);
        editor.SelectAll();
        editor.SelectedText = "chúc mừng";
        await Until(() => gifButton.Visible);
        gifButton.PerformClick();
        await Until(() => !composer.IsWorking && composer.Draft().Files.Length == 1);

        var path = Assert.Single(composer.Draft().Files);
        var suggestion = ComposerSuggestion.Match("chúc mừng", 9, [])!.Value.Item;
        using var original = suggestion.OpenGif();
        using var bytes = new MemoryStream();
        original.CopyTo(bytes);
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(path));
        ComposerSubmission? submitted = null;
        composer.SendRequested += draft => { submitted = draft; throw new IOException("Gửi lỗi thử nghiệm."); };
        SendEditorMessage(editor.Handle, 0x0100, (IntPtr)Keys.Enter, IntPtr.Zero);
        await Until(() => submitted is not null && !composer.IsWorking && composer.Draft().Files.Length == 1);
        Assert.Equal(path, Assert.Single(submitted!.Files));
        Assert.Equal(path, Assert.Single(composer.Draft().Files));
        Assert.True(File.Exists(path));

        var saved = composer.Draft();
        composer.SetContext(new() { Id = Guid.NewGuid(), Text = "sinh nhật" }, edit: true);
        editor.Select(editor.TextLength, 0);
        await Until(() => Children(composer).OfType<Button>().Any(b => b.Text == "🎂" && b.Visible) && !gifButton.Visible);
        composer.ResetContext();
        Assert.Equal(saved.Files, composer.Draft().Files);
        retained = composer.Draft();
        composer.SetComposerConversation(Guid.NewGuid());
        composer.ResetDraft();
        Assert.Empty(composer.Draft().Files);
        Assert.True(File.Exists(path));
        composer.SetComposerConversation(Guid.NewGuid());
        var restored = retained;
        retained = null;
        composer.RestoreDraft(restored);
        Assert.Equal(saved.Files, composer.Draft().Files);
        Children(composer).OfType<Button>().Single(b => b.AccessibleName == "Bỏ Chuc-mung.gif").PerformClick();
        Assert.Empty(composer.Draft().Files);
        Assert.False(File.Exists(path));
    });
}
