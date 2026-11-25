using LanChat.Application;

namespace LanChat.WinForms.Controls;

internal static class AttachmentProgressText
{
    internal static string Describe(FileProgress value) => value.Phase switch
    {
        AttachmentPhase.Waiting => "Đang chờ tác vụ…",
        AttachmentPhase.Connecting => "Đang kết nối hoặc chờ máy nguồn…",
        AttachmentPhase.Verifying => "Đang kiểm tra SHA-256…",
        AttachmentPhase.Preparing => "Đang chuẩn bị bản làm việc…",
        AttachmentPhase.Launching => "Windows đang mở…",
        AttachmentPhase.Saving => $"Đang lưu {value.Completed / 1024d:N0}/{value.Total / 1024d:N0} KB",
        AttachmentPhase.Downloading when value.Total > 0 => $"Đang tải {FormatBytes(value.Completed, value.Total)} / {FormatBytes(value.Total, value.Total)}",
        _ => "Đang tải…"
    };

    private static string FormatBytes(long bytes, long total) => total >= 1024L * 1024
        ? $"{bytes / (1024d * 1024):N1} MB" : $"{bytes / 1024d:N0} KB";
}
