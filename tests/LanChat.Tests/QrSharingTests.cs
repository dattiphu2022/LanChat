using System.Text;
using System.Drawing;
using LanChat.Domain;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Xunit;
using ZXing;

namespace LanChat.Tests;

public sealed class QrSharingTests
{
    [Fact]
    public void PreviewDisplaysMessageSeparatorsAndParagraphsAsWindowsLines()
    {
        var text = QrText.From([Message("Đoạn một\nĐoạn hai", 1), Message("Tin tiếp theo", 2)]);
        using var form = new QrShareForm(text);
        static IEnumerable<System.Windows.Forms.Control> Children(System.Windows.Forms.Control parent) =>
            parent.Controls.Cast<System.Windows.Forms.Control>().SelectMany(c => Children(c).Prepend(c));
        var preview = Children(form).OfType<System.Windows.Forms.TextBox>().Single();
        Assert.Equal("Đoạn một\r\nĐoạn hai\r\n\r\nTin tiếp theo", preview.Text);
        Assert.Equal(new[] { "Đoạn một", "Đoạn hai", "", "Tin tiếp theo" }, preview.Lines);
        Assert.Equal("Đoạn một\nĐoạn hai\n\nTin tiếp theo", text.Text);
    }
    private static Message Message(string text, long ticks) => new() { Id = Guid.NewGuid(), Text = text, CreatedAtTicks = ticks };
    [Fact]
    public void SelectionOrdersByTimePreservesOriginalTextAndAddsOnlyTwoLfSeparators()
    {
        var text = QrText.From([Message("  Dòng đầu\r\n\r\n", 1), Message("Xin chào 👋 a\u0301", 3), Message("", 2)]);
        Assert.Equal("  Dòng đầu\r\n\r\n\n\n\n\nXin chào 👋 a\u0301", text.Text);
        Assert.Equal(Encoding.UTF8.GetByteCount(text.Text), text.Bytes); Assert.True(text.CanExport);
    }
    [Theory]
    [InlineData(499, true)] [InlineData(500, true)] [InlineData(501, false)]
    public void ByteLimitIsExactAndDoesNotTruncate(int count, bool allowed)
    {
        var raw = new string('x', count); var text = QrText.From([Message(raw, 1)]);
        Assert.Equal(count, text.Bytes); Assert.Equal(raw, text.Text); Assert.Equal(allowed, text.CanExport);
        if (allowed) { using var code = text.CreateCode(); Assert.InRange(code.Version, 1, 18); }
        else Assert.Throws<InvalidOperationException>(() => text.CreateCode());
    }
    [Fact]
    public void Utf8CountsEmojiVietnameseAndSeparatorsWithoutNormalization()
    {
        var raw = new string('á', 248) + "👋"; var text = QrText.From([Message(raw, 0)]);
        Assert.Equal(500, text.Bytes); Assert.True(text.CanExport); using var code = text.CreateCode(); Assert.InRange(code.Version, 1, 18);
        var over = QrText.From([Message(raw, 0), Message("", 1)]); Assert.Equal(502, over.Bytes); Assert.False(over.CanExport);
        Assert.False(QrText.From([Message("\ud800", 0)]).CanExport);
    }
    [Fact]
    public void RefreshKeepsValidSelectionAndDropsRecalledRemovedAndOtherConversationItems()
    {
        var first = Message("A", 1); var second = Message("B", 2); var selection = new MessageSelection();
        selection.Bind([second, first], true); selection.Begin(); selection.Select(second.Id, true); selection.Select(first.Id, true);
        Assert.Equal("A\n\nB", selection.Text.Text);
        first.Text = "Đã sửa"; selection.Bind([first, second], false); Assert.Equal("Đã sửa\n\nB", selection.Text.Text);
        second.IsRecalled = true; selection.Bind([first, second], false); Assert.Equal("Đã sửa", selection.Text.Text);
        selection.Bind([second], false); Assert.Equal(0, selection.Text.Count); selection.All(); Assert.Equal(0, selection.Text.Count);
        selection.Bind([first], true); Assert.False(selection.Active); Assert.Equal(0, selection.Text.Count);
    }
    [Fact]
    public void EmptyAndOversizeContentCannotGenerateQr()
    {
        Assert.False(QrText.From([]).CanExport); Assert.False(QrText.From([Message("", 0)]).CanExport);
        var text = QrText.From([Message(new string('x', 501), 0)]); using var form = new QrShareForm(text);
        Assert.False(form.CanExport); Assert.Throws<InvalidOperationException>(() => form.SavePng(Path.Combine(Path.GetTempPath(), "not-written.png")));
    }
    [Fact]
    public void PngRoundTripsPlainUtf8TextWithMediumCorrectionAndNoMetadata()
    {
        var text = QrText.From([Message("Tiếng Việt 👋\nDòng hai  ", 1), Message("https://example.test/ — không có metadata", 2)]);
        using var form = new QrShareForm(text); var directory = Path.Combine(Path.GetTempPath(), "LanChat.QrTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "abc.qr.png"); form.SavePng(path); using var image = (Bitmap)Image.FromFile(path);
        var reader = new BarcodeReader(); var decoded = reader.Decode(image);
        Assert.NotNull(decoded); Assert.Equal(text.Text, decoded.Text); Assert.Equal("M", decoded.ResultMetadata[ResultMetadataType.ERROR_CORRECTION_LEVEL]);
        Assert.InRange(form.Version, 1, 18); Assert.Empty(Directory.GetFiles(directory, "*.part"));
    }
}
