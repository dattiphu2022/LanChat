namespace LanChat.WinForms.Controls;

internal sealed class GifPlayback(Control owner)
{
    private Image? _playing;
    private int _invalidateQueued;

    internal void Start(Image? image)
    {
        if (ReferenceEquals(image, _playing)) return;
        Stop();
        if (image is null || !ImageAnimator.CanAnimate(image)) return;
        _playing = image;
        ImageAnimator.Animate(image, FrameChanged);
    }

    internal void Stop()
    {
        if (_playing is { } image) ImageAnimator.StopAnimate(image, FrameChanged);
        _playing = null;
    }

    internal void UpdateFrame()
    {
        if (_playing is { } image) ImageAnimator.UpdateFrames(image);
    }

    private void FrameChanged(object? sender, EventArgs e)
    {
        if (!owner.IsHandleCreated || Interlocked.Exchange(ref _invalidateQueued, 1) != 0) return;
        try
        {
            owner.BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _invalidateQueued, 0);
                if (_playing is not null) owner.Invalidate();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _invalidateQueued, 0); }
    }
}

internal sealed class GifSuggestionButton : Button
{
    private readonly GifPlayback _playback;
    private ComposerSuggestion? _suggestion;
    private Stream? _stream;
    private Image? _gif;
    private bool _hovered;
    private bool _resourceSaving;

    internal GifSuggestionButton()
    {
        _playback = new(this);
        Text = "GIF";
        TextAlign = ContentAlignment.BottomRight;
        MouseEnter += (_, _) => { _hovered = true; UpdatePlayback(); };
        MouseLeave += (_, _) => { _hovered = false; UpdatePlayback(); };
        GotFocus += (_, _) => UpdatePlayback();
        LostFocus += (_, _) => UpdatePlayback();
        VisibleChanged += (_, _) => { if (!Visible) _hovered = false; UpdatePlayback(); };
    }

    internal void Bind(ComposerSuggestion? suggestion)
    {
        if (ReferenceEquals(suggestion, _suggestion)) return;
        ReleaseImage();
        _suggestion = suggestion;
        AccessibleName = suggestion is null ? "" : $"Đính kèm ảnh động {suggestion.Phrase}";
        if (suggestion is not null)
        {
            _stream = suggestion.OpenGif();
            _gif = Image.FromStream(_stream);
        }
        UpdatePlayback();
        Invalidate();
    }

    internal void SetResourceSaving(bool enabled)
    {
        _resourceSaving = enabled;
        UpdatePlayback();
    }

    private void UpdatePlayback() => _playback.Start(!_resourceSaving && Visible && (_hovered || Focused) ? _gif : null);

    private void ReleaseImage()
    {
        _playback.Stop();
        _gif?.Dispose();
        _gif = null;
        _stream?.Dispose();
        _stream = null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_gif is null) return;
        _playback.UpdateFrame();
        var size = this.ScalePixels(24);
        e.Graphics.DrawImage(_gif, this.ScalePixels(3), (Height - size) / 2, size, size);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseImage();
        base.Dispose(disposing);
    }
}
