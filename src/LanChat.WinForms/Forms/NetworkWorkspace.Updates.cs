using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed partial class NetworkWorkspace
{
    private readonly UpdateCoordinator _updates;
    private bool _installRequested, _installing;
    internal VerifiedInstaller? PendingInstaller { get; private set; }

    private Control CreateUpdateBar()
    {
        var panel = new UpdatePanel(_updates, showCheck: false) { Padding = new Padding(8, 4, 8, 4) };
        panel.InstallRequested += async () => await InstallUpdateAsync();
        void Refresh() { if (!panel.IsDisposed) panel.Visible = _updates.State.Release is not null; }
        _updates.Changed += Refresh;
        panel.Disposed += (_, _) => _updates.Changed -= Refresh;
        Refresh();
        return panel;
    }

    private bool HasActiveWork => _busy || _scan is not null || UiInteraction.ForegroundDialog(this) is not null ||
        _running.Values.Any(item => item.View.HasActiveWork);

    private async Task InstallUpdateAsync()
    {
        if (_exiting || _installing) return;
        if (HasActiveWork)
        {
            _updates.Notify("Hãy hoàn tất hoặc hủy tác vụ đang chạy rồi chọn Cài đặt ngay.");
            return;
        }
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
        finally { installer?.Dispose(); _installing = false; if (!IsDisposed) UpdateActionState(); }
        if (PendingInstaller is not null) ExitApplication();
    }
}
