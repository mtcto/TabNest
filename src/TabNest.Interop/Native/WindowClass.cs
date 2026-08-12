using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEX
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public nint lpszMenuName;
    public nint lpszClassName;
    public nint hIconSm;
}

internal static partial class WindowClass
{
    private const string Lib = "user32.dll";

    public const uint CS_HREDRAW = 0x0002;
    public const uint CS_VREDRAW = 0x0001;
    public const uint CS_DBLCLKS = 0x0008;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_APP = 0x8000;

    /// <summary>托盘图标回调消息。</summary>
    public const uint WM_TRAYICON = WM_APP + 1;

    /// <summary>请求 UI 线程处理编排层投递的更新。</summary>
    public const uint WM_TABNEST_SYNC = WM_APP + 2;

    public const int IDC_ARROW = 32512;

    public const uint TME_LEAVE = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [LibraryImport(Lib, EntryPoint = "RegisterClassExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [LibraryImport(Lib, EntryPoint = "CreateWindowExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport(Lib, EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hWnd);

    [LibraryImport(Lib, EntryPoint = "LoadCursorW", SetLastError = true)]
    public static partial nint LoadCursor(nint hInstance, nint lpCursorName);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ValidateRect(nint hWnd, nint lpRect);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? lpModuleName);

    // ---- 弹出菜单 ----

    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_CHECKED = 0x00000008;
    public const uint MF_GRAYED = 0x00000001;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint CreatePopupMenu();

    [LibraryImport(Lib, EntryPoint = "AppendMenuW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint hMenu);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int TrackPopupMenu(
        nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    // ---- 原生控件 ----
    //
    // 规则编辑器的输入框用原生 EDIT 而非自绘：自绘文本框必须自己实现输入法
    // （WM_IME_COMPOSITION、候选窗定位、组合串下划线），而中文输入是必须能用的。
    // 原生控件还天生带无障碍、键盘导航与焦点管理。

    /// <summary>凹陷边框。文本框有它才看得出"这里可以输入"。</summary>
    public const uint WS_EX_CLIENTEDGE = 0x00000200;

    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_TABSTOP = 0x00010000;
    public const uint WS_VSCROLL = 0x00200000;

    public const uint ES_AUTOHSCROLL = 0x0080;
    public const uint CBS_DROPDOWNLIST = 0x0003;
    public const uint BS_PUSHBUTTON = 0x0000;
    public const uint BS_AUTOCHECKBOX = 0x0003;

    public const uint CB_ADDSTRING = 0x0143;
    public const uint CB_SETCURSEL = 0x014E;
    public const uint CB_GETCURSEL = 0x0147;
    public const uint BM_SETCHECK = 0x00F1;
    public const uint BM_GETCHECK = 0x00F0;

    /// <summary>WM_COMMAND：控件通知父窗口。按钮点击、下拉框选择变化都走它。</summary>
    public const uint WM_COMMAND = 0x0111;

    /// <summary>只读文本框的背景色回调。用它把原生控件配色调得与自绘外壳协调。</summary>
    public const uint WM_CTLCOLOREDIT = 0x0133;
    public const uint WM_CTLCOLORSTATIC = 0x0138;
    public const uint WM_CTLCOLORBTN = 0x0135;

    public const int BN_CLICKED = 0;
    public const int CBN_SELCHANGE = 1;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport(Lib)]
    public static partial int ReleaseDC(nint hWnd, nint hDC);
}
