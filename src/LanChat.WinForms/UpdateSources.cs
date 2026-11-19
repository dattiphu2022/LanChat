using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanChat.Application;
using LanChat.Infrastructure;

namespace LanChat.WinForms;

internal enum UpdateSourceKind { GitHub, Lan, Local }
internal sealed record UpdateSettings(UpdateSourceKind Kind = UpdateSourceKind.GitHub, Guid NetworkId = default,
    Guid NodeId = default, string Address = "", int Port = 54546, string Name = "", UpdatePackage? Package = null,
    bool Sharing = false, Guid[]? Networks = null);
internal interface IUpdateSource
{
    string Key { get; }
    string Name { get; }
    TimeSpan Interval { get; }
    Task<UpdateCheck> CheckAsync(CancellationToken ct);
    Task<Stream> OpenAsync(UpdateRelease release, CancellationToken ct);
}

internal sealed class LocalUpdateSource(LocalUpdatePackages packages, UpdatePackage? package) : IUpdateSource
{
    public string Key => "local:" + (package?.Id ?? "none");
    public string Name => "Bộ cài trên máy này";
    public TimeSpan Interval => TimeSpan.Zero;
    public Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!UpdatePackage.IsValid(package) || !File.Exists(packages.PackagePath(package!))) throw new IOException("Chưa chọn bộ cài hoặc bản cục bộ đã mất. Hãy mở Cài đặt cập nhật để chọn lại.");
        return Task.FromResult(new UpdateCheck(UpdateRelease.From(package!, Key), "Bộ cài cục bộ đã sẵn sàng."));
    }
    public Task<Stream> OpenAsync(UpdateRelease release, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (package is null || release.Package != package) throw new IOException("Bộ cài đã đổi. Hãy kiểm tra cập nhật lại.");
        return Task.FromResult(packages.OpenLocal(package));
    }
}

internal sealed class LanUpdateSource(UpdateSettings settings, Func<Guid, IServiceProvider?> resolve) : IUpdateSource
{
    public string Key => $"lan:{settings.NetworkId:N}:{settings.NodeId:N}";
    public string Name => "LAN · " + settings.Name;
    public TimeSpan Interval => TimeSpan.Zero;
    private ILanUpdateClient Client => resolve(settings.NetworkId)?.GetService(typeof(ILanUpdateClient)) as ILanUpdateClient ??
        throw new IOException("Chưa kết nối mạng chứa máy nguồn cập nhật. Hãy mở lại mạng đó rồi thử lại.");
    private PeerEndpoint Peer => new(settings.NodeId, settings.Address, settings.Port);
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        var manifest = await Client.CheckAsync(Peer, ct).ConfigureAwait(false);
        return new(UpdateRelease.From(manifest.Package, Key), "Đã kiểm tra bộ cài từ máy nguồn LAN.");
    }
    public Task<Stream> OpenAsync(UpdateRelease release, CancellationToken ct) => Client.OpenAsync(Peer, release.Package, ct);
}

internal sealed class GitHubUpdateSource(HttpClient http) : IUpdateSource
{
    private const string Repo = "dattiphu2022/LanChat";
    public string Key => "github";
    public string Name => "Online — GitHub";
    public TimeSpan Interval => TimeSpan.FromHours(24);
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "GitHub chưa có bản phát hành ổn định.");
        response.EnsureSuccessStatusCode();
        var release = JsonSerializer.Deserialize<GitHubRelease>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (release is null || release.Draft || release.Prerelease || !UpdatePackage.TryVersion(release.Tag, out _))
            return new(null, "GitHub chưa có bản phát hành ổn định với phiên bản hợp lệ.");
        var asset = release.Assets?.FirstOrDefault(a => a.Name == UpdatePackage.AssetName && a.State == "uploaded");
        var candidate = new UpdateRelease(release.Tag!, asset?.Size ?? 0, asset?.Digest ?? "");
        // Older releases need not have installer metadata; report up-to-date before validating assets.
        if (System.Version.Parse(candidate.Version) <= System.Version.Parse(NodeInfoDto.CurrentApplicationVersion))
            return new(null, "Bạn đang dùng phiên bản LanChat mới nhất.");
        if (!UpdatePackage.IsValid(candidate.Package) || asset?.Url != DownloadUrl(candidate)) return new(null, "Chưa có bộ cài hợp lệ cho LanChat " + candidate.Version + ".");
        return new(candidate, "Đã có LanChat " + candidate.Version + ".");
    }
    private static string DownloadUrl(UpdateRelease release) => $"https://github.com/{Repo}/releases/download/{release.Tag}/{UpdatePackage.AssetName}";
    public async Task<Stream> OpenAsync(UpdateRelease release, CancellationToken ct)
    {
        var initial = new Uri(DownloadUrl(release)); var url = initial;
        for (var i = 0; i <= 4; i++)
        {
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                if (response.Content.Headers.ContentLength is long length && length != release.Size) { response.Dispose(); throw new InvalidDataException("Kích thước bộ cài không khớp với GitHub."); }
                try { return new OwnedDownloadStream(await response.Content.ReadAsStreamAsync().ConfigureAwait(false), response.Dispose); }
                catch { response.Dispose(); throw; }
            }
            using (response)
            {
                var code = (int)response.StatusCode;
                if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location) throw new HttpRequestException($"GitHub không trả về bộ cài (HTTP {code}).");
                url = new Uri(url, location);
                if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length > 0 ||
                    (url != initial && url.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com")))
                    throw new InvalidDataException("Địa chỉ tải bộ cài nằm ngoài GitHub.");
            }
        }
        throw new HttpRequestException("GitHub đã chuyển hướng tải quá nhiều lần.");
    }
    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("draft")] bool Draft, [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);
    private sealed record GitHubAsset([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("size")] long Size, [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("browser_download_url")] string? Url);
}

internal sealed class OwnedDownloadStream(Stream input, Action close) : Stream
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
    protected override void Dispose(bool disposing) { if (disposing && Interlocked.Exchange(ref _closed, 1) == 0) { try { input.Dispose(); } finally { close(); } } base.Dispose(disposing); }
}
