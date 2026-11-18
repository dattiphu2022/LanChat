using System.Runtime.InteropServices;

namespace LanChat.WinForms.Controls;

internal sealed class FolderPickerDialog : CommonDialog
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH

    public string? SelectedPath { get; private set; }
    public string Title { get; set; } = "Chọn thư mục đính kèm";

    public override void Reset() => SelectedPath = null;

    protected override bool RunDialog(IntPtr owner)
    {
        Reset();
        var dialog = (IFileDialog)new NativeFileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FileDialogOptions.PickFolders | FileDialogOptions.ForceFileSystem);
            dialog.SetTitle(Title);
            dialog.SetOkButtonLabel("Chọn thư mục");
            dialog.SetFileNameLabel("Thư mục:");

            var result = dialog.Show(owner);
            if (result == Cancelled) return false;
            Marshal.ThrowExceptionForHR(result);

            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(FileSystemPath, out var path);
                try { SelectedPath = Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
                return !string.IsNullOrEmpty(SelectedPath);
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    [Flags]
    private enum FileDialogOptions : uint
    {
        PickFolders = 0x20,
        ForceFileSystem = 0x40
    }

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class NativeFileOpenDialog { }

    // The methods up to GetResult must stay in the Shell interface's vtable order.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(FileDialogOptions options);
        void GetOptions(out FileDialogOptions options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint format, out IntPtr name);
    }
}
