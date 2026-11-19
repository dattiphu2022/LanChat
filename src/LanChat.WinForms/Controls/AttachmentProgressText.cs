using LanChat.Application;

namespace LanChat.WinForms.Controls;

internal static class AttachmentProgressText
{
    internal static string Describe(FileProgress value) => value.Phase switch
    {
        AttachmentPhase.Waiting => "Đang chờ tác vụ…",
        AttachmentPhase.Connecting => "Đang chờ máy nguồn…",
        AttachmentPhase.Verifying => "Đang kiểm tra SHA-256…",
        AttachmentPhase.Preparing => "Đang chuẩn bị bản làm việc…",
        AttachmentPhase.Launching => "Windows đang mở…",
        AttachmentPhase.Saving => $"Đang lưu {value.Completed / 1024d:N0}/{value.Total / 1024d:N0} KB",
        _ => $"Đang tải {value.Completed / 1024d:N0}/{value.Total / 1024d:N0} KB"
    };
}
