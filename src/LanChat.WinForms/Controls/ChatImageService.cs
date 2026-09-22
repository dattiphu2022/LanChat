using System.Drawing.Drawing2D;
using LanChat.Application;

namespace LanChat.WinForms.Controls;

// Images are loaded on demand by visible controls, with bounded decode/download concurrency.
public sealed class ChatImageService(IFileTransferService files)
{
    public const long InlineLimit = 20L * 1024 * 1024;
    private readonly SemaphoreSlim _slots = new(2, 2);
    public static bool IsImage(AttachmentDto file) => file.MimeType.ToLowerInvariant() is "image/jpeg" or "image/png" or "image/gif" or "image/bmp" or "image/tiff";

    public async Task<Bitmap> LoadAsync(AttachmentDto file, bool thumbnail, CancellationToken ct, IProgress<FileProgress>? progress = null)
    {
        if (!IsImage(file)) throw new InvalidDataException("Định dạng ảnh này chưa hỗ trợ xem trực tiếp.");
        if (file.FileSize > 100L * 1024 * 1024) throw new InvalidDataException("Ảnh vượt quá 100 MB. Hãy mở bằng ứng dụng ngoài từ nút bên dưới.");
        await _slots.WaitAsync(ct);
        try
        {
            var path = await files.DownloadAsync(file, progress, ct).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var source = Image.FromFile(path);
                if ((long)source.Width * source.Height > 40_000_000) throw new InvalidDataException("Ảnh vượt quá 40 megapixel. Hãy mở bằng ứng dụng ngoài.");
                ApplyOrientation(source);
                var scale = thumbnail ? Math.Min(1d, Math.Min(320d / source.Width, 220d / source.Height)) : 1d;
                var bitmap = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
                try
                {
                    using var graphics = Graphics.FromImage(bitmap); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, new Rectangle(Point.Empty, bitmap.Size)); ct.ThrowIfCancellationRequested(); return bitmap;
                }
                catch { bitmap.Dispose(); throw; }
            }, ct).ConfigureAwait(false);
        }
        finally { _slots.Release(); }
    }
    private static void ApplyOrientation(Image image)
    {
        if (!image.PropertyIdList.Contains(0x0112)) return;
        var value = image.GetPropertyItem(0x0112)?.Value;
        if (value is null || value.Length < 2) return;
        var orientation = BitConverter.ToUInt16(value, 0) switch
        {
            2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX, 6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX, 8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };
        image.RotateFlip(orientation);
    }
}
