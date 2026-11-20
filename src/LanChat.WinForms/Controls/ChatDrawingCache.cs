using System.Drawing.Drawing2D;

namespace LanChat.WinForms.Controls;

// One cache per message list, used only on its UI thread.
internal sealed class ChatDrawingCache : IDisposable
{
    private const int TextCapacity = 1024;
    private const int ShapeCapacity = 64;
    private const int LargestBubbleMetric = 620;
    private static readonly IReadOnlyDictionary<int, int[]> CommonScaleMetrics = new Dictionary<int, int[]>
    {
        [96] = BuildScaleMetrics(96),
        [120] = BuildScaleMetrics(120),
        [144] = BuildScaleMetrics(144),
        [168] = BuildScaleMetrics(168),
        [192] = BuildScaleMetrics(192)
    };

    internal readonly struct ScaleMetrics(int dpi, int[]? values)
    {
        internal int Pixels(int logical) => values is not null && (uint)logical < (uint)values.Length
            ? values[logical]
            : Scale(logical, dpi);
    }

    internal static ScaleMetrics ForDpi(int dpi)
    {
        CommonScaleMetrics.TryGetValue(dpi, out var values);
        return new ScaleMetrics(dpi, values);
    }

    private static int[] BuildScaleMetrics(int dpi)
    {
        var pixels = new int[LargestBubbleMetric + 1];
        for (var logical = 0; logical < pixels.Length; logical++) pixels[logical] = Scale(logical, dpi);
        return pixels;
    }

    private static int Scale(int logical, int dpi) => (int)Math.Round(logical * dpi / 96d);
    private const TextFormatFlags MessageTextFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

    private readonly Dictionary<(string Text, Font Font, int Width, int Dpi), Size> _text = new();
    private readonly Dictionary<(Rectangle Bounds, int Diameter), GraphicsPath> _shapes = new();
    private readonly Queue<(Rectangle Bounds, int Diameter)> _shapeOrder = new(ShapeCapacity);

    internal Size Measure(string text, Font font, int width, int dpi)
    {
        if (text.Length == 0) return Size.Empty;
        var limit = new Size(width, int.MaxValue);
        // Keep long messages out of the cache to bound retained string data.
        if (text.Length > 512) return TextRenderer.MeasureText(text, font, limit, MessageTextFlags);
        var key = (text, font, width, dpi);
        if (_text.TryGetValue(key, out var size)) return size;
        size = TextRenderer.MeasureText(text, font, limit, MessageTextFlags);
        if (_text.Count == TextCapacity) _text.Clear();
        _text.Add(key, size);
        return size;
    }

    internal GraphicsPath RoundedOutline(Rectangle bounds, int diameter)
    {
        var key = (bounds, diameter);
        if (_shapes.TryGetValue(key, out var shape)) return shape;
        if (_shapes.Count == ShapeCapacity)
        {
            var oldest = _shapeOrder.Dequeue();
            _shapes[oldest].Dispose();
            _shapes.Remove(oldest);
        }

        shape = new GraphicsPath();
        shape.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        shape.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        shape.CloseFigure();
        _shapes.Add(key, shape);
        _shapeOrder.Enqueue(key);
        return shape;
    }

    public void Dispose()
    {
        foreach (var shape in _shapes.Values) shape.Dispose();
        _shapes.Clear();
        _shapeOrder.Clear();
        _text.Clear();
    }
}
