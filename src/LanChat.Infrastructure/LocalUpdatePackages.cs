using System.Diagnostics;
using System.Security.Cryptography;
using LanChat.Application;

namespace LanChat.Infrastructure;

/// <summary>Immutable snapshots shared by the network hosts in a single workspace.</summary>
public sealed class LocalUpdatePackages(string dataDirectory) : IUpdatePackageProvider
{
    private readonly object _gate = new();
    private readonly string _root = Path.Combine(dataDirectory, "Updates", "Shared");
    private UpdatePackage? _package;
    private HashSet<Guid> _networks = [];
    private bool _paused;
    private int _active, _preparing;
    private readonly Dictionary<Guid, int> _activeNetworks = [];
    private readonly HashSet<Guid> _pausedNetworks = [];
    internal Func<string, string>? InspectForTesting { get; set; }
    public int ActiveTransfers { get { lock (_gate) return _active; } }
    public int ActiveTransfersFor(Guid network) { lock (_gate) return _activeNetworks.TryGetValue(network, out var active) ? active : 0; }
    public bool Preparing { get { lock (_gate) return _preparing > 0; } }
    public bool TryPauseNetwork(Guid network)
    { lock (_gate) { if (ActiveTransfersFor(network) > 0) return false; _pausedNetworks.Add(network); return true; } }
    public void ResumeNetwork(Guid network) { lock (_gate) _pausedNetworks.Remove(network); }
    public bool TryPauseForInstall()
    {
        lock (_gate) { if (_active > 0 || _preparing > 0 || _paused) return false; _paused = true; return true; }
    }
    public void Resume() { lock (_gate) _paused = false; }
    public void Configure(UpdatePackage? package, IEnumerable<Guid> networks)
    {
        if (package is not null && !UpdatePackage.IsValid(package)) throw new InvalidDataException("Gói cập nhật không hợp lệ.");
        lock (_gate) { _package = package; _networks = networks.ToHashSet(); }
    }
    public UpdatePackage? GetPackage(Guid networkId)
    { lock (_gate) return !_paused && !_pausedNetworks.Contains(networkId) && _networks.Contains(networkId) ? _package : null; }
    public string PackagePath(UpdatePackage package)
    {
        if (!UpdatePackage.IsValid(package)) throw new InvalidDataException("Gói cập nhật không hợp lệ.");
        return Path.Combine(_root, package.Tag, package.Digest.Substring(7), UpdatePackage.AssetName);
    }
    public Stream OpenLocal(UpdatePackage package) => new FileStream(PackagePath(package), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
    public Stream OpenPackage(Guid networkId, string packageId)
    {
        lock (_gate)
        {
            if (_paused || _pausedNetworks.Contains(networkId) || !_networks.Contains(networkId) || _package is null) throw new IOException("Máy nguồn chưa bật chia sẻ cập nhật cho mạng này.");
            if (_package.Id != packageId) throw new IOException("Bộ cài trên máy nguồn đã đổi. Hãy kiểm tra cập nhật lại.");
            if (_active >= 4) throw new IOException("Máy nguồn đang phục vụ bốn lượt tải. Vui lòng thử lại sau.");
            var input = OpenLocal(_package); _active++; _activeNetworks[networkId] = ActiveTransfersFor(networkId) + 1;
            return new UpdateReadStream(input, () => { lock (_gate) { _active--; _activeNetworks[networkId]--; } });
        }
    }
    public async Task<UpdatePackage> ImportAsync(string path, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        lock (_gate) { if (_paused) throw new IOException("Ứng dụng đang chuẩn bị cài đặt."); _preparing++; }
        string? temporary = null;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (input.Length is <= 0 or > 1073741824) throw new InvalidDataException("Bộ cài cần có dung lượng từ 1 byte đến 1 GB.");
            var tag = InspectForTesting?.Invoke(path) ?? Inspect(path);
            Directory.CreateDirectory(_root); temporary = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".part");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long completed = 0;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
                    completed += count; progress?.Report(new(completed, input.Length));
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            var package = new UpdatePackage(tag, completed, "sha256:" + FrameworkIO.Hex(hash.GetHashAndReset()).ToLowerInvariant());
            if (!UpdatePackage.IsValid(package)) throw new InvalidDataException("Phiên bản bộ cài không hợp lệ.");
            ct.ThrowIfCancellationRequested();
            var target = PackagePath(package); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Identical snapshots can be read concurrently; do not replace their open handles.
            if (File.Exists(target))
            {
                using var existing = File.OpenRead(target);
                if (existing.Length != package.Size || !string.Equals(FrameworkIO.Hex(await FrameworkIO.HashAsync(existing, ct).ConfigureAwait(false)), package.Digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Bản chia sẻ trong cache đã hỏng. Hãy kiểm tra thư mục dữ liệu.");
            }
            else File.Move(temporary, target);
            return package;
        }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); lock (_gate) _preparing--; }
    }
    private static string Inspect(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Hãy chọn bộ cài LanChat dạng EXE.");
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName?.Trim() != "LanChat" || info.FileDescription is null || info.FileDescription.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) < 0 ||
            !Version.TryParse(info.ProductVersion?.Trim(), out var version) || version.Build < 0 || version.Revision > 0)
            throw new InvalidDataException("Tệp đã chọn không phải bộ cài LanChat với phiên bản hợp lệ.");
        return "v" + version.ToString(3);
    }
}

internal sealed class UpdateReadStream(Stream input, Action close) : Stream
{
    private int _closed;
    public override bool CanRead => input.CanRead;
    public override bool CanSeek => input.CanSeek;
    public override bool CanWrite => false;
    public override long Length => input.Length;
    public override long Position { get => input.Position; set => input.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => input.ReadAsync(buffer, offset, count, ct);
    public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    { if (disposing && Interlocked.Exchange(ref _closed, 1) == 0) { try { input.Dispose(); } finally { close(); } } base.Dispose(disposing); }
}

internal sealed class NoUpdatePackages : IUpdatePackageProvider
{
    public UpdatePackage? GetPackage(Guid networkId) => null;
    public Stream OpenPackage(Guid networkId, string packageId) => throw new IOException("Máy này chưa bật chia sẻ cập nhật.");
}
