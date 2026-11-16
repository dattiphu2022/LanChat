using System.Text.Json;
using LanChat.Application;

namespace LanChat.Infrastructure;

public sealed partial class PeerFileTransferService
{
    private const int MaxFolderEntries = 10_000;
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private const long MaxFolderBytes = 20L * 1024 * 1024 * 1024;

    public Task<AttachmentDto> ImportFolderAsync(string path, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => SnapshotFolderAsync(path, progress, ct), ct);

    private async Task<AttachmentDto> SnapshotFolderAsync(string path, IProgress<string>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(root);
        ValidateFolderSegment(name);
        RejectFolderPathLinks(root, null);
        var dataDirectory = Path.GetFullPath(options.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (IsWithin(root, dataDirectory) || IsWithin(dataDirectory, root))
            throw new ArgumentException("Chọn thư mục bên ngoài thư mục dữ liệu LanChat.");

        var directories = new List<string>();
        var sources = new List<(string FullPath, string RelativePath)>();
        var pending = new Stack<(string FullPath, string RelativePath)>();
        pending.Push((root, ""));
        long size = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectFolderLink(directory.FullPath);
            foreach (var entry in new DirectoryInfo(directory.FullPath).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (directories.Count + sources.Count >= MaxFolderEntries)
                    throw new ArgumentException($"Thư mục vượt quá {MaxFolderEntries:N0} mục.");
                RejectFolderLink(entry.FullName);
                ValidateFolderSegment(entry.Name);
                var relative = directory.RelativePath.Length == 0 ? entry.Name : directory.RelativePath + "/" + entry.Name;
                ValidateRelativePath(relative);
                if (entry is DirectoryInfo)
                {
                    directories.Add(relative);
                    pending.Push((entry.FullName, relative));
                }
                else
                {
                    size += ((FileInfo)entry).Length;
                    if (size > MaxFolderBytes) throw new ArgumentException("Thư mục vượt quá 20 GB.");
                    sources.Add((entry.FullName, relative));
                }
            }
        }

        var files = new FolderFile[sources.Count];
        long total = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var source = sources[i];
            RejectFolderLink(source.FullPath);
            RejectFolderPathLinks(Path.GetDirectoryName(source.FullPath)!, root);
            if (new FileInfo(source.FullPath).Length > MaxFolderBytes - total) throw new ArgumentException("Thư mục vượt quá 20 GB.");
            progress?.Report($"Đang chuẩn bị thư mục {name} · {i + 1:N0}/{sources.Count:N0} tệp");
            var file = await ImportLocalAsync(source.FullPath, ct).ConfigureAwait(false);
            total += file.FileSize;
            if (total > MaxFolderBytes) throw new ArgumentException("Thư mục vượt quá 20 GB.");
            files[i] = new FolderFile(source.RelativePath, file.FileSize, file.Sha256, file.FileKey);
        }

        var manifest = new FolderManifest(name, directories.ToArray(), files);
        ValidateManifest(manifest, name);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (payload.Length > MaxManifestBytes) throw new ArgumentException("Danh sách nội dung thư mục quá lớn.");
        var tempRoot = Path.Combine(options.DataDirectory, "Attachments", ".temp");
        Directory.CreateDirectory(tempRoot);
        var temp = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await FrameworkIO.WriteBytesAsync(temp, payload, ct).ConfigureAwait(false);
            return await ImportSnapshotAsync(temp, name, FolderAttachment.MimeType, ct).ConfigureAwait(false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<string> PrepareFolderAsync(AttachmentDto attachment, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ValidateFolderSegment(attachment.FileName);
        if (attachment.FileSize is < 1 or > MaxManifestBytes) throw new InvalidDataException("Danh sách nội dung thư mục không hợp lệ.");
        var parent = Path.Combine(options.DataDirectory, "Attachments", "Open", attachment.Id.ToString("N"));
        var target = Path.Combine(parent, attachment.FileName);
        if (Directory.Exists(target))
        {
            RejectFolderPathLinks(target, options.DataDirectory);
            return target;
        }

        var manifestPath = await DownloadAsync(attachment, ct: ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<FolderManifest>(await FrameworkIO.ReadBytesAsync(manifestPath, ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("Thiếu nội dung thư mục.");
        ValidateManifest(manifest, attachment.FileName);

        RejectFolderPathLinks(parent, options.DataDirectory);
        Directory.CreateDirectory(parent);
        RejectFolderPathLinks(parent, options.DataDirectory);
        var temporary = Path.Combine(parent, "." + Guid.NewGuid().ToString("N") + ".part");
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var directory in manifest.Directories)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(temporary, ToLocalPath(directory)));
            }
            var total = manifest.Files.Sum(f => f.FileSize);
            long completed = 0;
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var destination = Path.Combine(temporary, ToLocalPath(file.RelativePath));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var item = new AttachmentDto(Guid.Empty, attachment.MessageId, Path.GetFileName(destination),
                    "application/octet-stream", file.FileSize, file.Sha256, file.FileKey);
                var offset = completed;
                var report = progress is null ? null : new FolderProgress(progress, offset, total);
                var blob = await DownloadAsync(item, report, ct).ConfigureAwait(false);
                using (var source = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    await source.CopyToAsync(output, ct).ConfigureAwait(false);
                completed += file.FileSize;
                progress?.Report(new(completed, total));
            }
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(temporary, target); }
            catch (IOException) when (Directory.Exists(target)) { RejectFolderPathLinks(target, options.DataDirectory); }
            return target;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    internal static void ValidateManifest(FolderManifest manifest, string expectedName)
    {
        ValidateFolderSegment(manifest.Name);
        if (!string.Equals(manifest.Name, expectedName, StringComparison.Ordinal) || manifest.Files is null || manifest.Directories is null ||
            (long)manifest.Files.Length + manifest.Directories.Length > MaxFolderEntries)
            throw new InvalidDataException("Danh sách nội dung thư mục không hợp lệ.");

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in manifest.Directories)
        {
            ValidateRelativePath(directory);
            if (!directories.Add(directory)) throw new InvalidDataException("Thư mục chứa đường dẫn trùng.");
        }
        paths.UnionWith(directories);
        foreach (var directory in directories) RequireParent(directory, directories);
        long bytes = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || file.FileSize < 0 || file.FileSize > MaxFolderBytes - bytes ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Tệp trong thư mục không hợp lệ.");
            ValidateRelativePath(file.RelativePath);
            if (!paths.Add(file.RelativePath)) throw new InvalidDataException("Thư mục chứa đường dẫn trùng.");
            RequireParent(file.RelativePath, directories);
            bytes += file.FileSize;
        }
    }

    private static void RequireParent(string path, HashSet<string> directories)
    {
        var separator = path.LastIndexOf('/');
        if (separator >= 0 && !directories.Contains(path.Substring(0, separator)))
            throw new InvalidDataException("Thiếu đường dẫn thư mục cha.");
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024)
            throw new InvalidDataException("Đường dẫn trong thư mục không hợp lệ.");
        foreach (var segment in path.Split('/')) ValidateFolderSegment(segment);
    }

    private static void ValidateFolderSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.EndsWith(".", StringComparison.Ordinal) ||
            name.EndsWith(" ", StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("Tên tệp hoặc thư mục không hợp lệ.");
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             (char.IsDigit(stem[3]) || "¹²³".Contains(stem[3]))))
            throw new InvalidDataException("Tên tệp hoặc thư mục được Windows dành riêng.");
    }

    private static bool IsWithin(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectFolderLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Không thể gửi hoặc mở thư mục chứa liên kết hệ thống.");
    }

    private static void RejectFolderPathLinks(string path, string? stop)
    {
        var root = stop is null ? null : Path.GetFullPath(stop).TrimEnd(Path.DirectorySeparatorChar);
        for (var current = new DirectoryInfo(path); current is not null && (root is null || IsWithin(current.FullName, root)); current = current.Parent)
            if (current.Exists) RejectFolderLink(current.FullName);
    }

    private static string ToLocalPath(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    private sealed class FolderProgress(IProgress<FileProgress> progress, long offset, long total) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => progress.Report(new(offset + value.Completed, total));
    }
}
