using System.Runtime.InteropServices;

namespace DiskLoom.Services;

// Native Win32 Common Item Dialogs (IFileOpenDialog / IFileSaveDialog). Unlike Windows.Storage.Pickers,
// they work when DiskLoom runs elevated and never create an empty 0-byte placeholder file before an export.
internal static class NativeFileDialogs
{
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);
    private const uint FosOverwritePrompt = 0x00000002;
    private const uint FosStrictFileTypes = 0x00000004;
    private const uint FosNoChangeDir = 0x00000008;
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint FosFileMustExist = 0x00001000;
    private const uint SigdnFileSysPath = 0x80058000;

    public static string? PickFolder(nint ownerWindow, string? title = null)
    {
        var dialog = (IFileDialog)new FileOpenDialogClass();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist | FosNoChangeDir);
            if (!string.IsNullOrWhiteSpace(title))
            {
                dialog.SetTitle(title);
            }
            return ShowAndGetPath(dialog, ownerWindow);
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    public static string? PickOpenFile(nint ownerWindow, string filterLabel, string filterPattern, string? title = null)
    {
        var dialog = (IFileDialog)new FileOpenDialogClass();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosForceFileSystem | FosPathMustExist | FosFileMustExist | FosNoChangeDir);
            dialog.SetFileTypes(1, [new ComDlgFilterSpec { Name = filterLabel, Spec = filterPattern }]);
            dialog.SetFileTypeIndex(1);
            if (!string.IsNullOrWhiteSpace(title))
            {
                dialog.SetTitle(title);
            }
            return ShowAndGetPath(dialog, ownerWindow);
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    public static string? PickSaveFile(nint ownerWindow, string suggestedFileName, string filterLabel, string extension, string? title = null)
    {
        var dialog = (IFileDialog)new FileSaveDialogClass();
        try
        {
            var normalizedExt = extension.TrimStart('.');
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosOverwritePrompt | FosStrictFileTypes | FosForceFileSystem | FosPathMustExist | FosNoChangeDir);
            dialog.SetFileTypes(1, [new ComDlgFilterSpec { Name = filterLabel, Spec = $"*.{normalizedExt}" }]);
            dialog.SetFileTypeIndex(1);
            dialog.SetDefaultExtension(normalizedExt);
            dialog.SetFileName(suggestedFileName);
            if (!string.IsNullOrWhiteSpace(title))
            {
                dialog.SetTitle(title);
            }
            return ShowAndGetPath(dialog, ownerWindow);
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    private static string? ShowAndGetPath(IFileDialog dialog, nint ownerWindow)
    {
        var hr = dialog.Show(ownerWindow);
        if (hr == ErrorCancelledHResult)
        {
            return null;
        }
        Marshal.ThrowExceptionForHR(hr);

        dialog.GetResult(out var item);
        if (item is null)
        {
            return null;
        }
        try
        {
            item.GetDisplayName(SigdnFileSysPath, out var pathPtr);
            if (pathPtr == nint.Zero)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(pathPtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(item);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ComDlgFilterSpec
    {
        public string Name;
        public string Spec;
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogClass;

    [ComImport]
    [Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class FileSaveDialogClass;

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint hwndOwner);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] ComDlgFilterSpec[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(nint pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(nint pFilter);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out nint ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
