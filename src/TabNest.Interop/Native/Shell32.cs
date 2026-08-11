using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

internal static partial class Shell32
{
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    public const uint NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersionOrTimeout;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;

        public static NOTIFYICONDATA Create() => new()
        {
            cbSize = sizeof(NOTIFYICONDATA),
        };

        public void SetTip(string text)
        {
            fixed (char* p = szTip)
            {
                var span = new Span<char>(p, 128);
                span.Clear();
                text.AsSpan(0, Math.Min(text.Length, 127)).CopyTo(span);
            }
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    /// <summary>从可执行文件提取图标。用于在标签上显示应用图标。</summary>
    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ExtractIconEx(
        string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);
}
