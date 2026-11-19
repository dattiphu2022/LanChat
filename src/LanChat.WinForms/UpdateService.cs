using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanChat.Application;
using LanChat.Infrastructure;

namespace LanChat.WinForms;

internal sealed record UpdateRelease(string Tag, long Size, string Digest, string SourceKey = "github")
{
    public string Version => Tag.Substring(1);
    public UpdatePackage Package => new(Tag, Size, Digest);
    public static UpdateRelease From(UpdatePackage package, string key) => new(package.Tag, package.Size, package.Digest, key);
}
internal sealed record UpdateCheck(UpdateRelease? Release, string Message);
internal sealed class VerifiedInstaller(string path, FileStream lockedFile) : IDisposable
{
    public string Path { get; } = path;
    public void Dispose() => lockedFile.Dispose();
}

internal sealed class UpdateService : IDisposable
{
    internal const string AssetName = UpdatePackage.AssetName;
    private static readonly Version Current = System.Version.Parse(NodeInfoDto.CurrentApplicationVersion);
    private readonly HttpClient _http;
    private readonly GitHubUpdateSource _github;
    private readonly string _directory, _settingsPath;
    private readonly Func<DateTimeOffset> _now;
    private CheckState? _state;
    private string? _stateKey;
    public LocalUpdatePackages Packages { get; }
    public UpdateSettings Settings { get; private set; }
    public Func<Guid, IServiceProvider?> ResolveNetwork { get; set; } = _ => null;
    public string? SettingsError { get; private set; }
    public string SourceName => Settings.Kind switch { UpdateSourceKind.GitHub => "Online — GitHub", UpdateSourceKind.Local => "Bộ cài trên máy này", UpdateSourceKind.Lan => "LAN · " + Settings.Name, _ => "Chưa cấu hình nguồn cập nhật" };
    internal UpdateService(string dataDirectory, HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        _directory = Path.Combine(dataDirectory, "Updates"); _settingsPath = Path.Combine(_directory, "settings.json");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LanChat/" + NodeInfoDto.CurrentApplicationVersion);
        _github = new GitHubUpdateSource(_http); Packages = new LocalUpdatePackages(dataDirectory);
        try
        {
            Settings = File.Exists(_settingsPath) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(_settingsPath)) ?? throw new InvalidDataException("Thiếu cấu hình cập nhật.") : new();
            ValidateSettings(Settings);
            Packages.Configure(Settings.Package, Settings.Sharing ? Settings.Networks ?? [] : []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { SettingsError = "Không đọc được cài đặt cập nhật: " + ex.Message; Settings = new(Kind: (UpdateSourceKind)(-1)); }
    }
    private IUpdateSource Source => Settings.Kind switch
    {
        UpdateSourceKind.GitHub => _github,
        UpdateSourceKind.Local => new LocalUpdateSource(Packages, Settings.Package),
        UpdateSourceKind.Lan => new LanUpdateSource(Settings, ResolveNetwork),
        _ => throw new IOException(SettingsError ?? "Hãy chọn nguồn trong Cài đặt cập nhật.")
    };
    private static void ValidateSettings(UpdateSettings settings)
    {
        if (!Enum.IsDefined(typeof(UpdateSourceKind), settings.Kind)) throw new ArgumentException("Nguồn cập nhật không hợp lệ.");
        if (settings.Kind == UpdateSourceKind.Lan && (settings.NetworkId == Guid.Empty || settings.NodeId == Guid.Empty ||
            !System.Net.IPAddress.TryParse(settings.Address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || settings.Port is < 1 or > 65535))
            throw new ArgumentException("Hãy xác minh mạng, IP và định danh máy nguồn LAN.");
        if ((settings.Kind == UpdateSourceKind.Local || settings.Sharing) && !UpdatePackage.IsValid(settings.Package)) throw new ArgumentException("Hãy chọn và kiểm tra bộ cài LanChat.");
        if (settings.Package is not null && !UpdatePackage.IsValid(settings.Package)) throw new ArgumentException("Thông tin bộ cài không hợp lệ.");
        if (settings.Sharing && (settings.Networks is not { Length: > 0 } || settings.Networks.Any(id => id == Guid.Empty))) throw new ArgumentException("Hãy chọn mạng được phép nhận cập nhật.");
    }
    public async Task ConfigureAsync(UpdateSettings settings, CancellationToken ct)
    {
        ValidateSettings(settings);
        if (settings.Kind == UpdateSourceKind.Lan) await new LanUpdateSource(settings, ResolveNetwork).CheckAsync(ct).ConfigureAwait(false);
        if (settings.Package is not null && (settings.Kind == UpdateSourceKind.Local || settings.Sharing))
        {
            using var input = Packages.OpenLocal(settings.Package);
            if (input.Length != settings.Package.Size || !string.Equals(FrameworkIO.Hex(await FrameworkIO.HashAsync(input, ct).ConfigureAwait(false)), settings.Package.Digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Bản chia sẻ không còn nguyên vẹn. Hãy chọn lại bộ cài.");
        }
        Directory.CreateDirectory(_directory);
        var temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await FrameworkIO.WriteTextAsync(temporary, JsonSerializer.Serialize(settings), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); FrameworkIO.PublishFile(temporary, _settingsPath);
            Settings = settings; SettingsError = null; _state = null; _stateKey = null;
            Packages.Configure(settings.Package, settings.Sharing ? settings.Networks ?? [] : []);
        }
        finally { TryDelete(temporary); }
    }
    private string StatePath(string key) => Path.Combine(_directory, "Checks", FrameworkIO.Hex(FrameworkIO.Hash(Encoding.UTF8.GetBytes(key))) + ".json");
    public async Task<UpdateCheck> CheckAsync(bool manual, CancellationToken ct)
    {
        var source = Source;
        var state = _stateKey == source.Key && _state is not null ? _state : ReadState(source.Key);
        _stateKey = source.Key; _state = state;
        var known = IsValidUpdate(state.Release) && state.Release!.SourceKey == source.Key ? state.Release : null;
        var now = _now();
        if (!manual && source.Interval > TimeSpan.Zero && now - state.CheckedAt < source.Interval)
            return new(known, "LanChat đã kiểm tra cập nhật trong 24 giờ qua.");
        _state = new(now, known);
        if (!SaveState(source.Key) && !manual) return new(known, "Chưa lưu được thời điểm kiểm tra cập nhật.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var result = await source.CheckAsync(timeout.Token).ConfigureAwait(false);
            if (result.Release is { } release)
            {
                if (!UpdatePackage.IsValid(release.Package) || release.SourceKey != source.Key) throw new InvalidDataException("Thông tin bộ cài không hợp lệ.");
                if (System.Version.Parse(release.Version) <= Current) result = new(null, "Bạn đang dùng phiên bản LanChat mới nhất hoặc mới hơn gói ở nguồn đã chọn.");
            }
            _state = new(now, result.Release); SaveState(source.Key); return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        { return new(known, source.Key == "github" ? "Chưa kiểm tra được cập nhật. Hãy kiểm tra kết nối Internet rồi thử lại." : "Chưa kiểm tra được cập nhật: " + ex.Message); }
    }
    public bool HasDownloaded(UpdateRelease release) => IsValidUpdate(release) && File.Exists(InstallerPath(release));
    public async Task<VerifiedInstaller> DownloadAsync(UpdateRelease release, IProgress<long> progress, CancellationToken ct)
    {
        if (!IsValidUpdate(release)) throw new InvalidDataException("Thông tin bộ cài cập nhật không hợp lệ.");
        var source = Source;
        if (source.Key != release.SourceKey) throw new IOException("Nguồn cập nhật đã đổi. Hãy kiểm tra lại.");
        var path = InstallerPath(release);
        if (File.Exists(path))
        {
            var existing = await LockVerifiedAsync(path, release, ct).ConfigureAwait(false);
            if (existing is not null) return existing;
            File.Delete(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var partial = path + ".part";
        try
        {
            await DownloadFileAsync(source, release, partial, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); FrameworkIO.PublishFile(partial, path);
            var verified = await LockVerifiedAsync(path, release, ct).ConfigureAwait(false);
            if (verified is not null) return verified;
            File.Delete(path); throw new InvalidDataException("SHA-256 hoặc kích thước bộ cài không khớp nguồn cập nhật.");
        }
        finally { TryDelete(partial); }
    }
    private static async Task DownloadFileAsync(IUpdateSource source, UpdateRelease release, string partial, IProgress<long> progress, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var input = await source.OpenAsync(release, idle.Token).ConfigureAwait(false);
            using var abort = idle.Token.Register(input.Dispose);
            using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long received = 0; var reportAt = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var count = await input.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false);
                if (count == 0) break;
                idle.CancelAfter(TimeSpan.FromSeconds(30)); received += count;
                if (received > release.Size) throw new InvalidDataException("Kích thước bộ cài vượt quá thông tin từ nguồn.");
                hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
                if (reportAt.ElapsedMilliseconds >= 100) { progress.Report(received); reportAt.Restart(); }
            }
            idle.Token.ThrowIfCancellationRequested(); progress.Report(received);
            if (received != release.Size || !MatchesDigest(hash.GetHashAndReset(), release)) throw new InvalidDataException("SHA-256 hoặc kích thước bộ cài không khớp nguồn cập nhật.");
        }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (idle.IsCancellationRequested) { throw new IOException("Kết nối tải bộ cài đã hết thời gian chờ.", ex); }
    }
    private static async Task<VerifiedInstaller?> LockVerifiedAsync(string path, UpdateRelease release, CancellationToken ct)
    {
        FileStream? stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        try
        {
            if (stream.Length != release.Size || !MatchesDigest(await FrameworkIO.HashAsync(stream, ct).ConfigureAwait(false), release)) return null;
            ct.ThrowIfCancellationRequested(); var result = new VerifiedInstaller(path, stream); stream = null; return result;
        }
        finally { stream?.Dispose(); }
    }
    private static bool MatchesDigest(byte[] hash, UpdateRelease release) => string.Equals(FrameworkIO.Hex(hash), release.Digest.Substring(7), StringComparison.OrdinalIgnoreCase);
    private static bool IsValidUpdate(UpdateRelease? release) => release is not null && UpdatePackage.IsValid(release.Package) && System.Version.Parse(release.Version) > Current;
    private string InstallerPath(UpdateRelease release) => Path.Combine(_directory, release.Tag, release.Digest.Substring(7), AssetName);
    private CheckState ReadState(string key)
    {
        try
        {
            var path = StatePath(key);
            if (!File.Exists(path) && key == "github") path = Path.Combine(_directory, "check.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<CheckState>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }
    private bool SaveState(string key)
    {
        var path = StatePath(key); var temporary = path + ".tmp";
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(temporary, JsonSerializer.Serialize(_state)); FrameworkIO.PublishFile(temporary, path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { TryDelete(temporary); }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    public void Dispose() => _http.Dispose();
    private sealed record CheckState(DateTimeOffset CheckedAt = default, UpdateRelease? Release = null);
}
