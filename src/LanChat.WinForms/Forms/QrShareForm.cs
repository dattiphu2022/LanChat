using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using LanChat.Infrastructure;
using LanChat.Domain;
using LanChat.WinForms.Controls;
using QRCoder;

namespace LanChat.WinForms.Forms;

internal sealed class QrShareForm : DpiForm
{
    private readonly QrText _text;
    private readonly Bitmap? _image;
    internal bool CanExport => _image is not null;
    internal int Version { get; }
    public QrShareForm(QrText text)
    {
        _text = text;
        Text = "Chia sẻ tin nhắn bằng QR"; Icon = AppIcon.Value; ClientSize = new Size(760, 530);
        MinimumSize = new Size(490, 390); StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false;
        var root = Theme.Stack(3); root.Padding = new Padding(12);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var status = Theme.Label("", 9); UiInteraction.Wrap(status, root); status.UseMnemonic = false;
        string? error = text.Error;
        try
        {
            if (text.CanExport)
            {
                using var code = text.CreateCode(); Version = code.Version;
                using var renderer = new QRCode(code); _image = renderer.GetGraphic(8, Color.Black, Color.White, drawQuietZones: true);
            }
        }
        catch (Exception ex) { error = ex.Message; }
        status.Text = $"{text.Count} tin · {Math.Max(0, text.Bytes)} / {QrText.MaximumBytes} byte UTF-8 · Plain Text · ECC M" +
            (Version > 0 ? $" · Version {Version}/{QrText.MaximumVersion}" : "") + "\n" +
            (error ?? (text.CanExport ? "QR chỉ chứa nguyên văn các tin được chọn, ghép theo thời gian. Xử lý hoàn toàn trên máy." : "Hãy chọn tin có văn bản để xuất QR."));
        var summary = status.Text;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var preview = new QrPreview(_image, Version == 0 ? 0 : 17 + 4 * Version + 8) { Dock = DockStyle.Fill, AccessibleName = "Bản xem trước QR" };
        var content = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
            // Native multiline TextBox needs CRLF to display the LF separators used by QR.
            Text = text.Text.NormalizeLines(Environment.NewLine), BackColor = Color.White, ForeColor = Theme.Ink, Font = Theme.DefaultFont,
            Margin = new Padding(8, 4, 4, 4), MaxLength = int.MaxValue, AccessibleName = "Văn bản nguyên gốc trong QR" };
        body.Controls.Add(preview, 0, 0); body.Controls.Add(content, 1, 0);
        var footer = Theme.Toolbar(); var save = Theme.Button("Xuất PNG…", UiIcon.Save); save.Enabled = CanExport;
        save.Click += (_, _) =>
        {
            using var picker = AttachmentSaveDialog.Create("LanChat-tin-nhan-qr.png", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            picker.Title = "Xuất QR thành PNG";
            if (picker.ShowDialog(UiInteraction.Owner(this)) != DialogResult.OK) return;
            save.Enabled = false;
            try { SavePng(picker.FileName); status.Text = summary + "\nĐã xuất PNG: " + picker.FileName; }
            catch (Exception ex) { status.Text = summary + "\nChưa xuất được PNG: " + ex.Message; }
            finally { save.Enabled = CanExport; }
        };
        var copy = Theme.Secondary("Sao chép văn bản", UiIcon.Copy);
        copy.Enabled = text.Text.Length > 0;
        copy.Click += (_, _) => { if (UiInteraction.Copy(this, text.Text)) status.Text = summary + "\nĐã sao chép nguyên văn."; };
        footer.Controls.Add(copy); footer.Controls.Add(save); footer.Controls.Add(UiInteraction.CloseButton(this));
        root.Controls.Add(status, 0, 0); root.Controls.Add(body, 0, 1); root.Controls.Add(footer, 0, 2); Controls.Add(root);
        void FitBody()
        {
            var stacked = body.ClientSize.Width < this.ScalePixels(560);
            body.SuspendLayout();
            body.ColumnCount = stacked ? 1 : 2; body.RowCount = stacked ? 2 : 1;
            body.ColumnStyles.Clear(); body.RowStyles.Clear();
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, stacked ? 100 : 50));
            if (!stacked) body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, stacked ? 65 : 100));
            if (stacked) body.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            body.SetCellPosition(content, stacked ? new TableLayoutPanelCellPosition(0, 1) : new TableLayoutPanelCellPosition(1, 0));
            body.ResumeLayout(true);
        }
        body.SizeChanged += (_, _) => FitBody();
        DpiChangedAfterParent += (_, _) => FitBody();
        Disposed += (_, _) => _image?.Dispose(); Shown += (_, _) => { content.Focus(); content.Select(0, 0); };
    }
    internal void SavePng(string path)
    {
        if (_image is null || !_text.CanExport) throw new InvalidOperationException(_text.Error ?? "Không có mã QR để xuất.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { _image.Save(output, ImageFormat.Png); output.Flush(true); }
            FrameworkIO.PublishFile(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class QrPreview(Bitmap? bitmap, int modules) : Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White);
            if (bitmap is null || modules <= 0) return;
            var pixels = Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height) / modules); var size = pixels * modules;
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor; e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(bitmap, new Rectangle((Width - size) / 2, (Height - size) / 2, size, size));
        }
    }
}
