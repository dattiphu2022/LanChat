using LanChat.WinForms.Controls;

namespace LanChat.WinForms;

internal sealed record UpdateState(UpdateRelease? Release, string Message, bool Checking = false,
    bool Downloading = false, bool Ready = false, bool Verifying = false, long Received = 0)
{
    public bool Busy => Checking || Downloading || Verifying;
}

// Owned by the workspace, not by any of the windows displaying its state.
// Commands and notifications run on the calling UI synchronization context.
internal sealed class UpdateCoordinator(UpdateService service) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _download;
    private Task _operation = Task.CompletedTask;
    private bool _stopped, _disposed;
    public UpdateState State { get; private set; } = new(null, "Chọn Kiểm tra cập nhật để tìm phiên bản mới.");
    public event Action? Changed;
    public UpdateService Service => service;
    public string SourceName => service.SourceName;

    public async Task ConfigureAsync(UpdateSettings settings, CancellationToken cancellationToken = default)
    {
        if (_stopped || State.Busy) throw new InvalidOperationException("Hãy hoàn tất hoặc hủy tác vụ cập nhật trước khi đổi nguồn.");
        var before = State;
        Set(State with { Verifying = true, Message = "Đang xác minh và lưu nguồn cập nhật…" });
        var task = ConfigureCoreAsync(settings, before, cancellationToken); _operation = task;
        await task;
        if (!_stopped) await CheckAsync(true);
    }
    private async Task ConfigureCoreAsync(UpdateSettings settings, UpdateState before, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            await Task.Run(() => service.ConfigureAsync(settings, cancellation.Token));
            if (!_stopped) Set(new(null, "Đã lưu nguồn cập nhật."));
        }
        catch { if (!_stopped) Set(before with { Message = "Chưa lưu được nguồn cập nhật. Bạn có thể thử lại." }); throw; }
    }

    private void Set(UpdateState state) { State = state; Changed?.Invoke(); }
    public void Notify(string message) { if (!_stopped) Set(State with { Message = message }); }

    public Task CheckAsync(bool manual)
    {
        if (_stopped || State.Busy) return _operation;
        Set(State with { Checking = true, Message = "Đang kiểm tra cập nhật…" });
        return _operation = CheckCoreAsync(manual);
    }

    private async Task CheckCoreAsync(bool manual)
    {
        var ct = _lifetime.Token;
        await Task.Yield();
        try
        {
            var result = await Task.Run(() => service.CheckAsync(manual, ct), ct);
            if (!_stopped) Set(new(result.Release, result.Message,
                Ready: result.Release is { } release && service.HasDownloaded(release)));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(ex.ToString());
            if (!_stopped) Set(State with { Checking = false, Message = "Chưa kiểm tra được cập nhật. Vui lòng thử lại." });
        }
    }

    public Task DownloadAsync()
    {
        if (_stopped || State.Busy || State.Release is not { } release) return _operation;
        _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Set(State with { Downloading = true, Received = 0, Message = "Đang tải bản cập nhật…" });
        return _operation = DownloadCoreAsync(release, _download);
    }

    private async Task DownloadCoreAsync(UpdateRelease release, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            var progress = new Progress<long>(received =>
            {
                if (!_stopped && ReferenceEquals(_download, cancellation) && !cancellation.IsCancellationRequested)
                    Set(State with { Received = received, Message = $"Đang tải {received / 1048576d:N1} / {release.Size / 1048576d:N1} MB…" });
            });
            using var installer = await Task.Run(() => service.DownloadAsync(release, progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_stopped) Set(new(release, "Đã tải. Chọn Cài đặt ngay khi bạn sẵn sàng.", Ready: true));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_stopped) Set(new(release, "Đã hủy tải. Bạn có thể tải lại.")); }
        catch (Exception ex)
        { if (!_stopped) Set(new(release, "Chưa tải được bản cập nhật: " + UiText.HideIpAddresses(ex.Message))); }
        finally { _download = null; cancellation.Dispose(); }
    }

    public void CancelDownload()
    {
        if (_download is null || _download.IsCancellationRequested) return;
        _download.Cancel();
        Notify("Đang hủy tải…");
    }

    public Task<VerifiedInstaller?> PrepareInstallerAsync()
    {
        if (_stopped || State.Busy || !State.Ready || State.Release is not { } release)
            return Task.FromResult<VerifiedInstaller?>(null);
        Set(State with { Verifying = true, Message = "Đang xác minh bộ cài…" });
        var task = PrepareCoreAsync(release);
        _operation = task;
        return task;
    }

    private async Task<VerifiedInstaller?> PrepareCoreAsync(UpdateRelease release)
    {
        var ct = _lifetime.Token;
        await Task.Yield();
        try
        {
            var installer = await Task.Run(() => service.DownloadAsync(release, new Progress<long>(), ct), ct);
            if (_stopped) { installer.Dispose(); return null; }
            Set(new(release, "Bộ cài đã sẵn sàng.", Ready: true));
            return installer;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            if (!_stopped) Set(new(release, "Chưa chuẩn bị được bộ cài: " + UiText.HideIpAddresses(ex.Message),
                Ready: service.HasDownloaded(release)));
            return null;
        }
    }

    public async Task StopAsync()
    {
        _stopped = true;
        if (!_disposed) _lifetime.Cancel();
        await _operation;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = _stopped = true; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
