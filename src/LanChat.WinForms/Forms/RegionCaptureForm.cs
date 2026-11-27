using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LanChat.WinForms.Forms;

internal sealed class RegionCaptureForm : Form
{
    private readonly Rectangle _desktop;
    private Point? _start;
    private Rectangle _selection;

    private RegionCaptureForm(Rectangle desktop)
    {
        _desktop = desktop;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = desktop;
        BackColor = Color.Black;
        Opacity = .32;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        AccessibleName = "Chọn vùng chụp. Kéo chuột để chọn, Escape để hủy.";
        DoubleBuffered = true;
    }

    public static Bitmap? CaptureRegion(Form owner, bool hideOwner)
    {
        var restoreOwner = hideOwner && owner.Visible;
        try
        {
            if (restoreOwner)
            {
                owner.Hide();
                DwmFlush();
            }

            // GetMonitorInfo backs Screen.Bounds in the app's PerMonitorV2 context.
            var desktop = Screen.AllScreens.Select(screen => screen.Bounds).Aggregate(Rectangle.Union);
            Rectangle selection;
            using (var form = new RegionCaptureForm(desktop))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                selection = form._selection;
            }
            return CapturePixels(selection);
        }
        finally
        {
            if (restoreOwner && !owner.IsDisposed && !owner.Disposing)
            {
                owner.Show();
                owner.Activate();
            }
        }
    }

    private static Bitmap CapturePixels(Rectangle selection)
    {
        // Wait for the compositor to present the desktop without the selection overlay.
        DwmFlush();
        var image = new Bitmap(selection.Width, selection.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(selection.Location, Point.Empty, selection.Size, CopyPixelOperation.SourceCopy);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _desktop;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _start = PhysicalCursor();
        Capture = true;
        UpdateSelection(_start.Value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_start.HasValue && Capture) UpdateSelection(PhysicalCursor());
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_start.HasValue) return;
        UpdateSelection(PhysicalCursor());
        _start = null;
        Capture = false;
        if (_selection.Width < 2 || _selection.Height < 2) return;
        CompleteSelection();
    }

    private void CompleteSelection()
    {
        Hide();
        DialogResult = DialogResult.OK;
    }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        Hide();
        DialogResult = DialogResult.Cancel;
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_selection.Width < 2 || _selection.Height < 2) return;
        var bounds = new Rectangle(_selection.X - _desktop.X, _selection.Y - _desktop.Y, _selection.Width - 1, _selection.Height - 1);
        using var fill = new SolidBrush(Color.FromArgb(80, Color.White));
        using var border = new Pen(Color.White, 2);
        e.Graphics.FillRectangle(fill, bounds);
        e.Graphics.DrawRectangle(border, bounds);
    }

    private void UpdateSelection(Point end)
    {
        if (_start is not Point start) return;
        _selection = SelectionBounds(_desktop, start, end);
        Invalidate();
    }

    internal static Rectangle SelectionBounds(Rectangle desktop, Point start, Point end) =>
        Rectangle.Intersect(desktop, Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)));

    private static Point PhysicalCursor() => GetPhysicalCursorPos(out var point) ? point : Cursor.Position;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out Point point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
