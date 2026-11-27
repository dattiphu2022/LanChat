using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LanChat.WinForms.Controls;

public sealed class ImageViewportControl : Control
{
    private Bitmap? _image;
    private Stream? _imageSource;
    private readonly GifPlayback _playback;
    private float _scale = 1;
    private PointF _pan;
    private Point? _drag;
    private bool _fit = true;
    public event Action<float>? ZoomChanged;
    public bool HasImage => _image is not null;
    internal float ScaleFactor => _scale;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Bitmap? Image
    {
        get => _image;
        set => SetImage(value, null);
    }
    internal void SetImage(Bitmap? image, Stream? source)
    {
        if (ReferenceEquals(_image, image)) return;
        _playback.Stop();
        _image?.Dispose();
        _imageSource?.Dispose();
        _image = image;
        _imageSource = source;
        _playback.Start(Visible ? image : null);
        Fit();
    }
    public ImageViewportControl()
    {
        _playback = new(this);
        Dock = DockStyle.Fill; BackColor = Color.FromArgb(27, 31, 38); DoubleBuffered = true; TabStop = true;
        VisibleChanged += (_, _) => _playback.Start(Visible ? _image : null);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        MouseWheel += (_, e) => Zoom(e.Delta > 0 ? 1.2f : 1 / 1.2f, e.Location);
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { Focus(); _drag = e.Location; Capture = true; Cursor = Cursors.SizeAll; } };
        MouseMove += (_, e) => { if (_drag is { } p) { _pan.X += e.X - p.X; _pan.Y += e.Y - p.Y; _drag = e.Location; ClampPan(); Invalidate(); } };
        MouseUp += (_, _) => { _drag = null; Capture = false; Cursor = Cursors.Default; };
        MouseCaptureChanged += (_, _) => { if (!Capture) { _drag = null; Cursor = Cursors.Default; } };
        DoubleClick += (_, _) => { if (_fit) ActualSize(); else Fit(); };
    }
    public void Fit()
    {
        _fit = true; _pan = PointF.Empty;
        _scale = _image is null ? 1 : Math.Min(1, Math.Min(Math.Max(1, ClientSize.Width - 24) / (float)_image.Width, Math.Max(1, ClientSize.Height - 24) / (float)_image.Height));
        ZoomChanged?.Invoke(_scale); Invalidate();
    }
    public void ActualSize() { _fit = false; _scale = 1; _pan = PointF.Empty; ClampPan(); ZoomChanged?.Invoke(_scale); Invalidate(); }
    public void Zoom(float factor) => Zoom(factor, new Point(ClientSize.Width / 2, ClientSize.Height / 2));
    private void Zoom(float factor, Point anchor)
    {
        if (_image is null) return;
        var next = Math.Max(.01f, Math.Min(16f, _scale * factor)); var ratio = next / _scale;
        var x = anchor.X - ClientSize.Width / 2f; var y = anchor.Y - ClientSize.Height / 2f;
        _pan = new PointF(x - (x - _pan.X) * ratio, y - (y - _pan.Y) * ratio); _scale = next; _fit = false;
        ClampPan(); ZoomChanged?.Invoke(_scale); Invalidate();
    }
    private void ClampPan()
    {
        if (_image is null) return;
        var x = Math.Max(0, (_image.Width * _scale - ClientSize.Width) / 2); var y = Math.Max(0, (_image.Height * _scale - ClientSize.Height) / 2);
        _pan.X = Math.Max(-x, Math.Min(x, _pan.X)); _pan.Y = Math.Max(-y, Math.Min(y, _pan.Y));
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (_fit) Fit(); else { ClampPan(); Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_image is null) return;
        _playback.UpdateFrame();
        var size = new SizeF(_image.Width * _scale, _image.Height * _scale);
        e.Graphics.InterpolationMode = _scale > 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(_image, new RectangleF((ClientSize.Width - size.Width) / 2 + _pan.X, (ClientSize.Height - size.Height) / 2 + _pan.Y, size.Width, size.Height));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _playback.Stop();
            _image?.Dispose(); _image = null;
            _imageSource?.Dispose(); _imageSource = null;
        }
        base.Dispose(disposing);
    }
}
