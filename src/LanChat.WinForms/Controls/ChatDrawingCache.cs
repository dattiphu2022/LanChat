using System.Drawing.Drawing2D;

namespace LanChat.WinForms.Controls;

// One cache per message list, used only on its UI thread.
internal sealed class ChatDrawingCache : IDisposable
{
    // Tăng dung lượng cache nhưng vẫn giới hạn để tránh RAM cao
    // Giới hạn: tối đa 2048 text entries hoặc 10MB, và 128 shapes
    private const int TextCapacity = 2048;
    private static readonly long MaxTextMemoryBytes = 
        (long)(Environment.WorkingSet * 0.02 * 1024 * 1024); // Tối đa 2% RAM hoặc giới hạn
    private const int ShapeCapacity = 128;
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
        
        // Giới hạn bộ nhớ: nếu vượt quá 2% RAM thì dọn dẹp cache
        // Thêm delay 5ms để tránh chiếm CPU cao khi dọn dẹp cache
        while (_text.Count >= TextCapacity || EstimateMemorySize() > MaxTextMemoryBytes)
        {
            var firstKey = _text.Keys.First();
            if (_text.TryGetValue(firstKey, out var item))
            {
                _text.Remove(firstKey);
                // Không thể ước tính chính xác size của Size, nhưng giảm count sẽ giảm RAM
                System.Threading.Thread.Sleep(5); // Delay 5ms để tránh chiếm CPU cao
            }
        }
        
        _text.Add(key, size);
        return size;
    }
    
    // Ước tính bộ nhớ cho cache text (ước tính mỗi entry ~32 bytes)
    private long EstimateMemorySize() => _text.Count * 32L;

    internal GraphicsPath RoundedOutline(Rectangle bounds, int diameter)
    {
        var key = (bounds, diameter);
        if (_shapes.TryGetValue(key, out var shape)) return shape;
        if (_shapes.Count == ShapeCapacity)
        {
            var oldest = _shapeOrder.Dequeue();
            _shapes[oldest].Dispose();
            _shapes.Remove(oldest);
            // Delay để tránh chiếm CPU cao khi dọn dẹp cache
            System.Threading.Thread.Sleep(2);
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
