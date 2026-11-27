using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LanChat.WinForms.Controls;

internal static class WindowsEmoji
{
    // Join emoji modifiers, regional indicators, keycaps and ZWJ sequences before shaping.
    private const string Supplementary = @"(?:\uD83C[\uDC00-\uDFFA]|\uD83D[\uDC00-\uDEFF]|\uD83E[\uDD00-\uDEFF])";
    private const string Component = @"(?:" + Supplementary + @"\uFE0F?|[\u2190-\u2BFF\u00A9\u00AE\u203C\u2049\u2122\u2139]\uFE0F|[\u231A\u231B\u23E9-\u23EC\u23F0\u23F3\u25FD\u25FE\u2614\u2615\u2648-\u2653\u267F\u2693\u26A1\u26AA\u26AB\u26BD\u26BE\u26C4\u26C5\u26CE\u26D4\u26EA\u26F2\u26F3\u26F5\u26FA\u26FD\u2705\u270A\u270B\u2728\u274C\u274E\u2753-\u2755\u2757\u2795-\u2797\u27B0\u27BF\u2B1B\u2B1C\u2B50\u2B55])(?:\uD83C[\uDFFB-\uDFFF])?";
    internal static readonly Regex Sequences = new(@"(?:[0-9#*]\uFE0F?\u20E3|(?:\uD83C[\uDDE6-\uDDFF]){2}|" +
        Component + @"(?:\u200D" + Component + @")*)(?!\uFE0E)");

    internal static void OpenPicker()
    {
        const ushort windows = 0x5B, period = 0xBE;
        SendInput(4, [new(windows, 0), new(period, 0), new(period, 2), new(windows, 2)], Marshal.SizeOf<Input>());
    }

    // INPUT contains a native union. MOUSEINPUT is its largest member on x64.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
        public Input(ushort key, uint flags) { this = default; Type = 1; Key = key; Flags = flags; }
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}

// The existing RichEdit controls keep the Unicode text, layout and selection.
// Render just the emoji glyphs using the color layers in the installed Windows font.
internal static class WindowsEmojiGlyphs
{
    private static readonly Guid DrawingFactoryId = new("06152247-6f50-465a-9245-118bfd3b6007");
    private static readonly Guid TextFactoryId = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    [ThreadStatic] private static Dictionary<(string, Size, int, Color, Color), Bitmap?>? _cache;

    internal static bool IsPaintMessage(int message) => message is 0x000F or 0x0318;

    internal static void PaintTextBox(TextBoxBase editor, IntPtr dc, Match[] emoji,
        Func<Match, (Color Foreground, Color Background)> colors)
    {
        if (!editor.IsHandleCreated) return;
        using var graphics = dc == IntPtr.Zero ? Graphics.FromHwnd(editor.Handle) : Graphics.FromHdc(dc);
        var selectionStart = editor.SelectionStart;
        var selectionLength = editor.SelectionLength;
        var fontPixels = Math.Max(1, (int)Math.Round(editor.Font.SizeInPoints * editor.LayoutDpi() / 72f));
        foreach (var match in emoji)
        {
            var after = match.Index + match.Length;
            if (editor.Focused && selectionLength > 0 && selectionStart < after && selectionStart + selectionLength > match.Index) continue;
            var start = editor.GetPositionFromCharIndex(match.Index);
            if (!editor.ClientRectangle.Contains(start)) continue;
            var end = editor.GetPositionFromCharIndex(after);
            var width = end.Y == start.Y && end.X > start.X ? end.X - start.X :
                TextRenderer.MeasureText(match.Value, editor.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var size = new Size(Math.Min(width, editor.ClientSize.Width - start.X), editor.Font.Height);
            var (foreground, background) = colors(match);
            if (Get(match.Value, size, fontPixels, foreground, background) is { } image)
                graphics.DrawImageUnscaled(image, start);
        }
    }

    internal static Bitmap? Get(string text, Size size, int fontPixels, Color foreground, Color background)
    {
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 256 || size.Height > 128) return null;
        var cache = _cache ??= new();
        var key = (text, size, fontPixels, foreground, background);
        if (cache.TryGetValue(key, out var image)) return image;
        if (cache.Count >= 128)
        {
            foreach (var old in cache.Values) old?.Dispose();
            cache.Clear();
        }
        return cache[key] = Render(text, size, fontPixels, foreground, background);
    }

    private static Bitmap? Render(string text, Size size, int fontPixels, Color foreground, Color background)
    {
        var drawingId = DrawingFactoryId;
        var textId = TextFactoryId;
        var factory = IntPtr.Zero; var writer = IntPtr.Zero; var target = IntPtr.Zero;
        var format = IntPtr.Zero; var brush = IntPtr.Zero;
        Bitmap? bitmap = null;
        var success = false;
        try
        {
            if (D2D1CreateFactory(0, ref drawingId, IntPtr.Zero, out factory) < 0 ||
                DWriteCreateFactory(0, ref textId, out writer) < 0) return null;
            // ID2D1Factory::CreateDCRenderTarget, IDWriteFactory::CreateTextFormat.
            var properties = new RenderProperties { Format = 87, Alpha = 3, DpiX = 96, DpiY = 96 };
            if (Method<CreateTarget>(factory, 16)(factory, ref properties, out target) < 0 ||
                Method<CreateFormat>(writer, 15)(writer, "Segoe UI Emoji", IntPtr.Zero, 400, 0, 5, fontPixels, "en-US", out format) < 0)
                return null;
            var ink = new NativeColor(foreground);
            if (Method<CreateBrush>(target, 8)(target, ref ink, IntPtr.Zero, out brush) < 0) return null;
            // IDWriteTextFormat layout options: centered, vertically centered, no wrapping.
            Method<SetOption>(format, 3)(format, 2);
            Method<SetOption>(format, 4)(format, 2);
            Method<SetOption>(format, 5)(format, 1);
            bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var graphics = Graphics.FromImage(bitmap);
            var dc = graphics.GetHdc();
            try
            {
                var bounds = new NativeRect { Right = size.Width, Bottom = size.Height };
                if (Method<BindDc>(target, 57)(target, dc, ref bounds) < 0) return null;
                Method<BeginDraw>(target, 48)(target);
                var fill = new NativeColor(background);
                Method<Clear>(target, 47)(target, ref fill);
                var rect = new FloatRect { Right = size.Width, Bottom = size.Height };
                // Clip to the glyph's existing layout slot and opt into color fonts.
                Method<DrawText>(target, 27)(target, text, (uint)text.Length, format, ref rect, brush, 6, 0);
                success = Method<EndDraw>(target, 49)(target, IntPtr.Zero, IntPtr.Zero) >= 0;
            }
            finally { graphics.ReleaseHdc(dc); }
            return success ? bitmap : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        finally
        {
            if (!success) bitmap?.Dispose();
            foreach (var pointer in new[] { brush, format, target, writer, factory })
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential)] private struct RenderProperties { public int Type, Format, Alpha; public float DpiX, DpiY; public int Usage, FeatureLevel; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FloatRect { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeColor
    {
        public float Red, Green, Blue, Alpha;
        internal NativeColor(Color value) { Red = value.R / 255f; Green = value.G / 255f; Blue = value.B / 255f; Alpha = 1; }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTarget(IntPtr self, ref RenderProperties properties, out IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateFormat(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr collection, int weight, int style, int stretch, float size, [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBrush(IntPtr self, ref NativeColor color, IntPtr properties, out IntPtr brush);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetOption(IntPtr self, int option);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BindDc(IntPtr self, IntPtr dc, ref NativeRect bounds);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BeginDraw(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Clear(IntPtr self, ref NativeColor color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawText(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text, uint length, IntPtr format, ref FloatRect bounds, IntPtr brush, int options, int measuringMode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EndDraw(IntPtr self, IntPtr tag1, IntPtr tag2);
    [DllImport("d2d1.dll")] private static extern int D2D1CreateFactory(int type, ref Guid iid, IntPtr options, out IntPtr factory);
    [DllImport("dwrite.dll")] private static extern int DWriteCreateFactory(int type, ref Guid iid, out IntPtr factory);
}
