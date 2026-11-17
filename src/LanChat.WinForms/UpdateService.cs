using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanChat.Application;
using LanChat.Infrastructure;

namespace LanChat.WinForms;

internal sealed record UpdateRelease(string Tag, long Size, string Digest)
{
    public string Version => Tag.Substring(1);
}

internal sealed record UpdateCheck(UpdateRelease? Release, string Message);

internal sealed class VerifiedInstaller(string path, FileStream lockedFile) : IDisposable
{
    public string Path { get; } = path;
    public void Dispose() => lockedFile.Dispose();
}

internal sealed class UpdateService : IDisposable
{
    internal const string AssetName = "LanChat-Setup-Windows-x64.exe";
    private const string Repo = "dattiphu2022/LanChat";
    private const string LatestRelease = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private static readonly Version Current = Version.Parse(NodeInfoDto.CurrentApplicationVersion);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan NetworkIdleLimit = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _now;
    private CheckState? _state;

    internal UpdateService(string dataDirectory, HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        _directory = Path.Combine(dataDirectory, "Updates");
        _statePath = Path.Combine(_directory, "check.json");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LanChat/" + NodeInfoDto.CurrentApplicationVersion);
    }

    public async Task<UpdateCheck> CheckAsync(bool manual, CancellationToken ct)
    {
        var state = _state ??= ReadState();
        var known = IsValidUpdate(state.Release) ? state.Release : null;
        var now = _now();
        if (!manual && now - state.CheckedAt < CheckInterval)
            return new(known, "LanChat đã kiểm tra cập nhật trong 24 giờ qua.");

        // Reserve the next automatic check even if GitHub is unreachable or the app closes during the request.
        _state = new CheckState(now, known);
        if (!SaveState() && !manual)
            return new(known, "Chưa lưu được thời điểm kiểm tra cập nhật.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return SetRelease(null, "GitHub chưa có bản phát hành ổn định.");
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(body);
            if (release is null || release.Draft || release.Prerelease || !TryVersion(release.Tag, out var version))
                return SetRelease(null, "GitHub chưa có bản phát hành ổn định với phiên bản hợp lệ.");
            if (version.CompareTo(Current) <= 0)
                return SetRelease(null, "Bạn đang dùng phiên bản LanChat mới nhất.");

            var asset = release.Assets?.FirstOrDefault(a => a.Name == AssetName && a.State == "uploaded");
            var candidate = new UpdateRelease(release.Tag!, asset?.Size ?? 0, asset?.Digest ?? "");
            if (!IsValidUpdate(candidate) || !string.Equals(asset?.Url, DownloadUrl(candidate), StringComparison.Ordinal))
                return SetRelease(null, $"Chưa có bộ cài hợp lệ cho LanChat {version}.");
            return SetRelease(candidate, $"Đã có LanChat {candidate.Version}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return new(known, "Chưa kiểm tra được cập nhật. Hãy kiểm tra kết nối Internet rồi thử lại.");
        }
    }

    public bool HasDownloaded(UpdateRelease release) => File.Exists(InstallerPath(release));

    public async Task<VerifiedInstaller> DownloadAsync(UpdateRelease release, IProgress<long> progress, CancellationToken ct)
    {
        if (!IsValidUpdate(release)) throw new InvalidDataException("Thông tin bộ cài cập nhật không hợp lệ.");
        var path = InstallerPath(release);
        if (File.Exists(path))
        {
            var existing = await LockVerifiedAsync(path, release, ct).ConfigureAwait(false);
            if (existing is not null) return existing;
            File.Delete(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        try
        {
            await DownloadFileAsync(release, partial, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            FrameworkIO.PublishFile(partial, path);
            var verified = await LockVerifiedAsync(path, release, ct).ConfigureAwait(false);
            if (verified is not null) return verified;
            File.Delete(path);
            throw new InvalidDataException("SHA-256 hoặc kích thước của bộ cài không khớp với GitHub.");
        }
        finally { TryDelete(partial); }
    }

    private async Task DownloadFileAsync(UpdateRelease release, string partial, IProgress<long> progress, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(NetworkIdleLimit);
        try
        {
            using var response = await OpenDownloadAsync(release, idle.Token).ConfigureAwait(false);
            using var abort = idle.Token.Register(response.Dispose);
            var length = response.Content.Headers.ContentLength;
            if (length.HasValue && length.Value != release.Size)
                throw new InvalidDataException("Kích thước bộ cài không khớp với GitHub.");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            var lastReport = System.Diagnostics.Stopwatch.StartNew();
            long received = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false);
                if (count == 0) break;
                idle.CancelAfter(NetworkIdleLimit);
                received += count;
                if (received > release.Size) throw new InvalidDataException("Kích thước bộ cài vượt quá thông tin từ GitHub.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
                if (lastReport.ElapsedMilliseconds >= 100) { progress.Report(received); lastReport.Restart(); }
            }
            idle.Token.ThrowIfCancellationRequested();
            progress.Report(received);
            if (received != release.Size || !MatchesDigest(hash.GetHashAndReset(), release))
                throw new InvalidDataException("SHA-256 hoặc kích thước của bộ cài không khớp với GitHub.");
        }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (idle.IsCancellationRequested)
        {
            throw new IOException("Kết nối tải bộ cài đã hết thời gian chờ.", ex);
        }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(UpdateRelease release, CancellationToken ct)
    {
        var initial = new Uri(DownloadUrl(release));
        var url = initial;
        for (var i = 0; i <= 4; i++)
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK) return response;
            using (response)
            {
                var code = (int)response.StatusCode;
                if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                    throw new HttpRequestException($"GitHub không trả về bộ cài (HTTP {code}).");
                url = new Uri(url, location);
                if (!IsDownloadLocation(initial, url)) throw new InvalidDataException("Địa chỉ tải bộ cài nằm ngoài GitHub.");
            }
        }
        throw new HttpRequestException("GitHub đã chuyển hướng tải quá nhiều lần.");
    }

    private static bool IsDownloadLocation(Uri initial, Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0 &&
        (url == initial || url.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com");

    private static async Task<VerifiedInstaller?> LockVerifiedAsync(string path, UpdateRelease release, CancellationToken ct)
    {
        FileStream? stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        try
        {
            if (stream.Length != release.Size || !MatchesDigest(await FrameworkIO.HashAsync(stream, ct).ConfigureAwait(false), release))
                return null;
            ct.ThrowIfCancellationRequested();
            var result = new VerifiedInstaller(path, stream);
            stream = null;
            return result;
        }
        finally { stream?.Dispose(); }
    }

    private static bool MatchesDigest(byte[] hash, UpdateRelease release) =>
        string.Equals(FrameworkIO.Hex(hash), release.Digest.Substring(7), StringComparison.OrdinalIgnoreCase);

    private static bool TryVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (tag is null || tag.Length < 6 || tag[0] != 'v' || !Version.TryParse(tag.Substring(1), out var parsed) ||
            parsed.Build < 0 || parsed.Revision >= 0 || tag != "v" + parsed.ToString(3)) return false;
        version = parsed;
        return true;
    }

    private static bool IsValidUpdate(UpdateRelease? release) => release is { Size: > 0, Digest.Length: 71 } &&
        TryVersion(release.Tag, out var version) && version.CompareTo(Current) > 0 &&
        release.Digest.StartsWith("sha256:", StringComparison.Ordinal) && release.Digest.Skip(7).All(Uri.IsHexDigit);

    private static string DownloadUrl(UpdateRelease release) => $"https://github.com/{Repo}/releases/download/{release.Tag}/{AssetName}";

    private string InstallerPath(UpdateRelease release) =>
        Path.Combine(_directory, release.Tag, release.Digest.Substring(7), AssetName);

    private UpdateCheck SetRelease(UpdateRelease? release, string message)
    {
        _state = _state! with { Release = release };
        SaveState();
        return new(release, message);
    }

    private CheckState ReadState()
    {
        try
        {
            if (File.Exists(_statePath))
                return JsonSerializer.Deserialize<CheckState>(File.ReadAllText(_statePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    private bool SaveState()
    {
        var temporary = _statePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
            FrameworkIO.PublishFile(temporary, _statePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { TryDelete(temporary); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();

    private sealed record CheckState(DateTimeOffset CheckedAt = default, UpdateRelease? Release = null);
    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("draft")] bool Draft, [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);
    private sealed record GitHubAsset([property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("state")] string? State, [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest, [property: JsonPropertyName("browser_download_url")] string? Url);
}
