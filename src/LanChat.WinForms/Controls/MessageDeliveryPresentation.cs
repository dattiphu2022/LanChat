using LanChat.Domain;

namespace LanChat.WinForms.Controls;

// Only persisted replication/receipt evidence can advance delivery state.
internal sealed record MessageDeliveryPresentation(string Text, string Detail, Color Color, UiIcon Icon)
{
    internal static MessageDeliveryPresentation For(LanChat.Domain.Message message, int readCount)
    {
        if (readCount > 0) return new("Đã đọc", $"{readCount} người đã xác nhận đọc.", Theme.Blue, UiIcon.Check);
        if (message.Status == MessageStatus.Delivered) return new("Đã nhận", "Có xác nhận đã nhận; chưa có xác nhận đọc.", Theme.Muted, UiIcon.Check);
        if (message.Status == MessageStatus.Synced) return new("Đã đồng bộ", "Ít nhất một máy khác giữ bản sao. Chưa có xác nhận người nhận đã nhận.", Theme.Muted, UiIcon.Sync);
        if (!string.IsNullOrWhiteSpace(message.LastError)) return new("Chờ gửi lại", "Tin đã lưu trên máy; hệ thống tự thử lại khi kết nối cho phép.", Theme.Warning, UiIcon.Refresh);
        return new("Đã lưu · chờ đồng bộ", "Tin đã lưu trên máy này; chưa có xác nhận bản sao trên máy khác.", Theme.Muted, UiIcon.Sync);
    }
}

// Reuses the app icon cache rather than font fallback glyphs in every message.
internal sealed class DeliveryStatusLabel : Label
{
    private UiIcon? _icon;
    internal UiIcon? Icon
    {
        get => _icon;
        set { if (_icon == value) return; _icon = value; UpdateInset(); Invalidate(); }
    }
    private void UpdateInset() => Padding = new Padding(_icon.HasValue ? this.ScalePixels(15) : 0, 0, 0, 0);
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateInset(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_icon is not UiIcon icon) return;
        var size = this.ScalePixels(11);
        e.Graphics.DrawImageUnscaled(UiIcons.Get(icon, size, ForeColor), 0, Math.Max(0, (ClientSize.Height - size) / 2));
    }
}
