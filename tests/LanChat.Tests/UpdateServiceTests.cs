using System.Net;
using System.Net.Http;
using System.Text.Json;
using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms;
using Xunit;

namespace LanChat.Tests;

public sealed class UpdateServiceTests
{
    private const string Tag = "v9.12.3";
    private static readonly byte[] Setup = [1, 9, 7, 5];
    private static readonly string Digest = "sha256:" + FrameworkIO.Hex(FrameworkIO.Hash(Setup)).ToLowerInvariant();
    private static readonly UpdateRelease Release = new(Tag, Setup.Length, Digest);

    private static string DataDirectory() => Path.Combine(Path.GetTempPath(), "LanChat.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AutomaticCheckRemembersASuccessOrFailureAcrossRestartsWhileManualCheckRunsNow()
    {
        var directory = DataDirectory();
        var time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var requests = 0;
        HttpResponseMessage Latest(HttpRequestMessage request)
        {
            Assert.Equal("https://api.github.com/repos/dattiphu2022/LanChat/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
            requests++;
            return Manifest();
        }
        using (var first = new UpdateService(directory, Handler(Latest), () => time))
            Assert.Equal(Release, (await first.CheckAsync(false, default)).Release);

        time = time.AddHours(23);
        using (var restarted = new UpdateService(directory, Handler(Latest), () => time))
        {
            Assert.Equal(Release, (await restarted.CheckAsync(false, default)).Release);
            Assert.Equal(1, requests);
            Assert.Equal(Release, (await restarted.CheckAsync(true, default)).Release);
            Assert.Equal(2, requests);
        }

        time = time.AddHours(24);
        using (var offline = new UpdateService(directory, Handler(_ =>
        {
            requests++;
            throw new HttpRequestException("Offline");
        }), () => time))
        {
            var result = await offline.CheckAsync(false, default);
            Assert.Equal(Release, result.Release);
            Assert.Contains("Internet", result.Message);
        }
        using var afterFailure = new UpdateService(directory, Handler(Latest), () => time.AddMinutes(1));
        Assert.Equal(Release, (await afterFailure.CheckAsync(false, default)).Release);
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData("same", "mới nhất")]
    [InlineData("missing", "chưa có bản phát hành")]
    [InlineData("preview", "phiên bản hợp lệ")]
    [InlineData("asset", "bộ cài hợp lệ")]
    [InlineData("digest", "bộ cài hợp lệ")]
    [InlineData("url", "bộ cài hợp lệ")]
    public async Task AutoUpdateRequiresANewerStableVersionAndItsCanonicalInstaller(string scenario, string message)
    {
        using var service = new UpdateService(DataDirectory(), Handler(_ => scenario switch
        {
            "same" => Manifest("v" + NodeInfoDto.CurrentApplicationVersion),
            "missing" => new(HttpStatusCode.NotFound),
            "preview" => Manifest(prerelease: true),
            "asset" => Manifest(assetName: "Source.zip"),
            "digest" => Manifest(digest: ""),
            "url" => Manifest(url: "https://example.invalid/LanChat-Setup-Windows-x64.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        }));
        var result = await service.CheckAsync(false, default);
        Assert.Null(result.Release);
        Assert.Contains(message, result.Message);
    }

    [Fact]
    public async Task VerifiedInstallerFollowsGitHubRedirectAndIsReusedFromDisk()
    {
        var directory = DataDirectory();
        var requests = 0;
        using (var service = new UpdateService(directory, Handler(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "github.com")
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/asset") } };
            Assert.Equal("release-assets.githubusercontent.com", request.RequestUri.Host);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) };
        })))
        {
            using var installer = await service.DownloadAsync(Release, new Progress<long>(), default);
            Assert.True(service.HasDownloaded(Release));
            Assert.Equal(Setup, File.ReadAllBytes(installer.Path));
            Assert.Throws<IOException>(() => File.OpenWrite(installer.Path));
            Assert.Empty(Directory.GetFiles(directory, "*.part", SearchOption.AllDirectories));
        }

        using var restarted = new UpdateService(directory, Handler(_ => throw new InvalidOperationException("Unexpected download")));
        using var cached = await restarted.DownloadAsync(Release, new Progress<long>(), default);
        Assert.Equal(Setup, File.ReadAllBytes(cached.Path));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("redirect")]
    public async Task InvalidInstallerLeavesNoRunnableFile(string scenario)
    {
        var directory = DataDirectory();
        using var service = new UpdateService(directory, Handler(request =>
        {
            Assert.Equal("github.com", request.RequestUri!.Host);
            if (scenario == "redirect")
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.invalid/setup.exe") } };
            var bytes = scenario == "size" ? new byte[3] : new byte[Setup.Length];
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release, new Progress<long>(), default));
        Assert.False(service.HasDownloaded(Release));
        Assert.Empty(Directory.GetFiles(directory, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancelDuringDownloadCleansUpThePartialInstaller()
    {
        var directory = DataDirectory();
        using var stream = new PausedDownload();
        using var service = new UpdateService(directory, Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = service.DownloadAsync(Release, new Progress<long>(), cancellation.Token);
        await Task.WhenAny(stream.Paused.Task, Task.Delay(2000));
        Assert.True(stream.Paused.Task.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(service.HasDownloaded(Release));
        Assert.Empty(Directory.GetFiles(directory, "*.part", SearchOption.AllDirectories));
    }

    private static HttpResponseMessage Manifest(string tag = Tag, bool prerelease = false, string? digest = null,
        string assetName = UpdateService.AssetName, string? url = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease,
            assets = new[] { new { name = assetName, state = "uploaded", size = Setup.Length, digest = digest ?? Digest,
                browser_download_url = url ?? $"https://github.com/dattiphu2022/LanChat/releases/download/{tag}/{assetName}" } }
        }))
    };

    private static HttpMessageHandler Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => new FakeGitHub(respond);

    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class PausedDownload() : MemoryStream(Setup)
    {
        public readonly TaskCompletionSource<bool> Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Position == 0) return Read(buffer, offset, 1);
            Paused.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
