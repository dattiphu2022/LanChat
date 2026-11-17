using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LanChat.WinForms.Controls;

internal sealed class DraftImageStore : IDisposable
{
    private readonly string _session;
    private readonly Dictionary<string, Bitmap> _thumbnails = new(StringComparer.OrdinalIgnoreCase);

    public DraftImageStore(string dataDirectory)
    {
        var root = Path.Combine(dataDirectory, "DraftImages");
        _session = Path.Combine(root, Guid.NewGuid().ToString("N"));
        // A network's data lock is held before its composer opens.
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var directory in Directory.GetDirectories(root))
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) RemoveDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public Task<(string Path, Bitmap Thumbnail)> StageAsync(Bitmap image, string prefix, CancellationToken ct) => Task.Run(() =>
    {
        var path = Path.Combine(_session, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_session);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                image.Save(output, ImageFormat.Png);
            ct.ThrowIfCancellationRequested();
            var scale = Math.Min(1d, 96d / Math.Max(image.Width, image.Height));
            return (path, new Bitmap(image, new Size(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)))));
        }
        catch
        {
            Delete(path);
            if (ct.IsCancellationRequested) RemoveDirectory(_session);
            throw;
        }
    }, ct);

    public Bitmap? Thumbnail(string path) => _thumbnails.TryGetValue(path, out var thumbnail) ? thumbnail : null;

    public void Keep(string path, Bitmap thumbnail) => _thumbnails.Add(path, thumbnail);

    public void Discard(string path, Bitmap thumbnail)
    {
        thumbnail.Dispose();
        Delete(path);
    }

    public void Prune(IEnumerable<string> draftFiles)
    {
        var retained = new HashSet<string>(draftFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var path in _thumbnails.Keys.Where(path => !retained.Contains(path)).ToArray())
        {
            Discard(path, _thumbnails[path]);
            _thumbnails.Remove(path);
        }
    }

    public void Dispose()
    {
        Prune([]);
        RemoveDirectory(_session);
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void RemoveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class ClipboardDraftImage
{
    private static readonly string[] PngFormats = ["PNG", "image/png"];

    public static bool HasText(IDataObject data) =>
        data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text) || data.GetDataPresent(DataFormats.StringFormat);

    public static bool HasImage(IDataObject data) =>
        PngFormats.Any(format => data.GetDataPresent(format, autoConvert: false)) || data.GetDataPresent(DataFormats.Bitmap);

    public static bool PreferImageOnPaste(IDataObject data) => !HasText(data) && HasImage(data);

    public static Bitmap? Read(IDataObject data)
    {
        foreach (var format in PngFormats)
        {
            if (!data.GetDataPresent(format, autoConvert: false)) continue;
            var value = data.GetData(format, autoConvert: false);
            if (value is byte[] bytes)
            {
                using var png = new MemoryStream(bytes, writable: false);
                var decoded = DecodePng(png);
                if (decoded is not null) return decoded;
            }
            else if (value is Stream png)
            {
                var decoded = DecodePng(png);
                if (decoded is not null) return decoded;
            }
        }
        return data.GetData(DataFormats.Bitmap) is Image bitmap ? new Bitmap(bitmap) : null;
    }

    private static Bitmap? DecodePng(Stream png)
    {
        var position = png.CanSeek ? png.Position : 0;
        try
        {
            if (png.CanSeek) png.Position = 0;
            using var image = Image.FromStream(png, useEmbeddedColorManagement: false, validateImageData: true);
            return image.RawFormat.Equals(ImageFormat.Png) ? new Bitmap(image) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException) { return null; }
        finally { if (png.CanSeek) png.Position = position; }
    }
}
