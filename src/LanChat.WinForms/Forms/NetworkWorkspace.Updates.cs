using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed partial class NetworkWorkspace
{
    private readonly UpdateCoordinator _updates;
    private bool _installRequested, _installing;
    internal VerifiedInstaller? PendingInstaller { get; private set; }

    private Control CreateUpdateBar()
    {
        var panel = new UpdatePanel(_updates, showCheck: false, compact: true) { Padding = new Padding(6, 0, 6, 0) };
        panel.InstallRequested += async () => await InstallUpdateAsync();
        return panel;
    }

    private void RefreshWindowTitle()
    {
        if (IsDisposed) return;
        var state = _updates.State;
        if (_moreButton is not null)
        {
            _moreButton.ShowBadge = state.Release is not null;
            _moreButton.AccessibleDescription = state.Ready ? "Bản cập nhật đã tải xong, sẵn sàng cài đặt." :
                state.Release is not null ? "Có bản cập nhật mới. " + state.Message : "Mở menu Thêm để xem giới thiệu, cập nhật và hướng dẫn.";
        }
        var progress = state.Downloading && state.Release is { Size: > 0 } item
            ? $" · {Math.Min(100, 100d * state.Received / item.Size):N0}%" : "";
        Text = $"LanChat v{LanChat.Application.NodeInfoDto.CurrentApplicationVersion} · {_updates.SourceName} · " +
            (state.Release is { } release ? $"{release.Version} · " : "") + state.Message + progress;
    }

    private bool HasActiveWork => _busy || _scan is not null || UiInteraction.ForegroundDialog(this) is not null ||
        _updates.Service.Packages.Preparing || _updates.Service.Packages.ActiveTransfers > 0 ||
        _running.Values.Any(item => item.View.HasActiveWork);

    private async Task InstallUpdateAsync()
    {
        if (_exiting || _installing) return;
        if (_updates.Service.Packages.ActiveTransfers > 0)
        { _updates.Notify("Máy này đang cung cấp bộ cài cho máy khác. Hãy chờ tải xong rồi bấm cài lại."); return; }
        if (HasActiveWork)
        {
            _updates.Notify("Hãy hoàn tất hoặc hủy tác vụ đang chạy rồi chọn Cài đặt ngay.");
            return;
        }
        if (!_updates.Service.Packages.TryPauseForInstall())
        { _updates.Notify("Bộ cài đang được chuẩn bị hoặc đang phục vụ máy khác. Hãy bấm cài lại sau."); return; }
        _installing = true;
        UpdateActionState();
        VerifiedInstaller? installer = null;
        try
        {
            installer = await _updates.PrepareInstallerAsync();
            if (installer is null || _exiting) return;
            if (HasActiveWork) { _updates.Notify("Đã tải. Hãy hoàn tất tác vụ đang chạy rồi bấm cài lại."); return; }
            if (_running.Values.Any(item => item.View.HasUnsentDrafts) && !UiInteraction.Discard(this,
                "Bạn còn bản nháp chưa gửi hoặc thay đổi thành viên chưa lưu. Bỏ các bản nháp để cài đặt ngay?", "Cập nhật LanChat"))
            { _updates.Notify("Đã tải. Bạn có thể cài sau."); return; }
            if (_exiting || HasActiveWork) { _updates.Notify("Hãy hoàn tất tác vụ đang chạy rồi bấm cài lại."); return; }
            PendingInstaller = installer;
            installer = null;
        }
        finally { installer?.Dispose(); if (PendingInstaller is null) _updates.Service.Packages.Resume(); _installing = false; if (!IsDisposed) UpdateActionState(); }
        if (PendingInstaller is not null) ExitApplication();
    }
}
