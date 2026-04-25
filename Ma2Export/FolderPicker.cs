using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MajdataEdit.Ma2Export;

internal static class FolderPicker
{
    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifNewDialogStyle = 0x0040;
    private const uint BffmInitialized = 1;
    private const uint BffmSetSelectionW = 0x467;
    private const int MaxPath = 260;

    public static string? SelectFolder(Window owner, string title, string? initialPath)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var displayName = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        var initialPathPtr = string.IsNullOrWhiteSpace(initialPath)
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(initialPath);

        BrowseCallbackProc? callback = initialPathPtr == IntPtr.Zero
            ? null
            : (hwnd, message, _, data) =>
            {
                if (message == BffmInitialized)
                {
                    SendMessage(hwnd, BffmSetSelectionW, new IntPtr(1), data);
                }

                return 0;
            };

        var browseInfo = new BrowseInfo
        {
            HwndOwner = ownerHandle,
            PidlRoot = IntPtr.Zero,
            PszDisplayName = displayName,
            LpszTitle = title,
            UlFlags = BifReturnOnlyFsDirs | BifNewDialogStyle,
            Lpfn = callback,
            LParam = initialPathPtr,
            IImage = 0
        };

        try
        {
            var itemIdList = SHBrowseForFolder(ref browseInfo);
            if (itemIdList == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var path = new StringBuilder(MaxPath);
                return SHGetPathFromIDList(itemIdList, path) ? path.ToString() : null;
            }
            finally
            {
                CoTaskMemFree(itemIdList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(displayName);
            if (initialPathPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(initialPathPtr);
            }
        }
    }

    private delegate int BrowseCallbackProc(IntPtr hwnd, uint message, IntPtr lParam, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr HwndOwner;
        public IntPtr PidlRoot;
        public IntPtr PszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string LpszTitle;
        public uint UlFlags;
        public BrowseCallbackProc? Lpfn;
        public IntPtr LParam;
        public int IImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);
}
