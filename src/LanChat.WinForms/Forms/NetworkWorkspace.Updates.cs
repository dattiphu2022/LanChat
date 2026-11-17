using System.Runtime.InteropServices;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed partial class NetworkWorkspace
{
    private readonly UpdateService _updates;
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly FlowLayoutPanel _updateBar = Theme.Toolbar();
    private readonly Label _updateText = Theme.Label("", 9, true);
    private readonly Button _applyUpdate = Theme.Secondary("Tải và cập nhật");
    private readonly Button _cancelUpdate = Theme.Secondary("Hủy tải");
    private readonly ProgressBar _updateProgress = new() { Size = new Size(128, 18), Margin = new Padding(5, 12, 5, 5), AccessibleName = "Tiến độ tải bản cập nhật" };
    private string _checkMessage = "";
    private string? _updateNote;
    private UpdateRelease? _availableUpdate, _downloadingRelease;
    private Task<UpdateCheck>? _checkTask;
    private Task<VerifiedInstaller>? _downloadTask;
    private CancellationTokenSource? _downloadCancellation;
    internal VerifiedInstaller? PendingInstaller { get; private set; }

    private Control CreateUpdateBar()
    {
        _updateBar.Padding = new Padding(8, 0, 0, 0);
        _updateBar.BackColor = Theme.Selection;
        _updateBar.Visible = false;
        _updateText.Dock = DockStyle.None;
        _updateText.Margin = new Padding(5, 13, 7, 5);
        _updateText.UseMnemonic = false;
        _applyUpdate.Click += async (_, _) => await DownloadAndInstallAsync();
        _cancelUpdate.Click += (_, _) => _downloadCancellation?.Cancel();
        _updateBar.Controls.AddRange([_updateText, _applyUpdate, _updateProgress, _cancelUpdate]);
        return _updateBar;
    }

    private void RefreshUpdateBar()
    {
        var release = _downloadingRelease ?? _availableUpdate;
        _updateBar.Visible = release is not null;
        if (release is null) return;
        var downloading = _downloadCancellation is not null;
        _updateText.Text = $"LanChat {release.Version}" + (_updateNote is null ? "" : $" · {_updateNote}");
        _applyUpdate.Visible = !downloading;
        _applyUpdate.Text = _updates.HasDownloaded(release) ? "Cài bản đã tải" : "Tải và cập nhật";
        _updateProgress.Visible = _cancelUpdate.Visible = downloading;
    }

    private async Task<string> CheckForUpdatesAsync(bool manual)
    {
        if (_exiting) return "";
        var ct = _updateLifetime.Token;
        var check = _checkTask ??= Task.Run(() => _updates.CheckAsync(manual, ct), ct);
        try
        {
            var result = await check;
            if (_exiting || IsDisposed) return "";
            if (_availableUpdate != result.Release && _downloadingRelease is null) _updateNote = null;
            _availableUpdate = result.Release;
            _checkMessage = result.Message;
            RefreshUpdateBar();
            return result.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return ""; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(ex.ToString());
            return _checkMessage = "Chưa kiểm tra được cập nhật. Vui lòng thử lại.";
        }
        finally { if (_checkTask == check) _checkTask = null; }
    }

    private bool HasActiveWork => _busy || !IsWindowEnabled(Handle) || _running.Values.Any(item => item.View.HasActiveWork);

    private async Task DownloadAndInstallAsync()
    {
        if (_exiting || _downloadCancellation is not null || _availableUpdate is not { } release) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        _downloadCancellation = cancellation;
        _downloadingRelease = release;
        _updateNote = _updates.HasDownloaded(release) ? "Đang xác minh bộ cài…" : "Đang tải bộ cài…";
        _updateProgress.Value = 0;
        RefreshUpdateBar();

        VerifiedInstaller? installer = null;
        try
        {
            var progress = new Progress<long>(bytes =>
            {
                if (IsDisposed || _exiting || _downloadCancellation != cancellation) return;
                _updateProgress.Value = (int)Math.Min(100, 100d * bytes / release.Size);
            });
            var task = _downloadTask = Task.Run(() => _updates.DownloadAsync(release, progress, cancellation.Token), cancellation.Token);
            installer = await task;
            if (_exiting || _availableUpdate != release) return;
            cancellation.Token.ThrowIfCancellationRequested();
            if (HasActiveWork)
            {
                _updateNote = "Đã tải. Cài sau khi tác vụ đang chạy hoàn tất.";
                return;
            }
            if (_running.Values.Any(item => item.View.HasUnsentDrafts) &&
                MessageBox.Show(this, "Bạn còn bản nháp chưa gửi. Bỏ các bản nháp để cài đặt ngay?",
                    "Cập nhật LanChat", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                _updateNote = "Đã tải. Bạn có thể cài sau.";
                return;
            }
            if (_exiting || HasActiveWork)
            {
                _updateNote = "Đã tải. Cài sau khi tác vụ đang chạy hoàn tất.";
                return;
            }
            PendingInstaller = installer;
            installer = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _updateNote = "Đã hủy tải."; }
        catch (Exception ex)
        {
            if (!_exiting)
            {
                _updateNote = "Bộ cài chưa sẵn sàng.";
                MessageBox.Show(this, UiText.HideIpAddresses(ex.Message), "Chưa cập nhật được LanChat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            installer?.Dispose();
            _downloadTask = null;
            _downloadCancellation = null;
            _downloadingRelease = null;
            if (!_exiting && !IsDisposed) RefreshUpdateBar();
        }
        if (PendingInstaller is not null) ExitApplication();
    }

    private async Task StopUpdateOperationsAsync()
    {
        _updateLifetime.Cancel();
        var tasks = new Task?[] { _checkTask, _downloadTask }.OfType<Task>().ToArray();
        try { await Task.WhenAll(tasks); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Http.HttpRequestException) { }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
}
