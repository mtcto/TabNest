using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

internal static partial class User32
{
    private const string Lib = "user32.dll";

    // ---- 窗口枚举与关系 ----

    public const uint GW_HWNDNEXT = 2;
    public const uint GW_OWNER = 4;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint GetTopWindow(nint hWnd);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hWnd);

    /// <summary>目标窗口是否假死。借鉴 Groupy：任何窗口操作前都应先问这一句。</summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsHungAppWindow(nint hWnd);

    // ---- 文本与类名 ----

    [LibraryImport(Lib, EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    public static partial int GetWindowTextLength(nint hWnd);

    [LibraryImport(Lib, EntryPoint = "GetWindowTextW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowText(nint hWnd, Span<char> lpString, int nMaxCount);

    [LibraryImport(Lib, EntryPoint = "GetClassNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hWnd, Span<char> lpClassName, int nMaxCount);

    // ---- 样式 ----

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    /// <summary>
    /// 窗口的属主（owner），不是父窗口。
    /// 设置属主后，该窗口始终显示在属主之上，并**跟随属主一起在 Z 序中升降** ——
    /// 这正是标签轨道需要的行为：贴着目标窗口，但不会浮在所有应用之上。
    /// </summary>
    public const int GWLP_HWNDPARENT = -8;

    public const long WS_VISIBLE = 0x10000000;
    public const long WS_CHILD = 0x40000000;
    public const long WS_CAPTION = 0x00C00000;
    public const long WS_THICKFRAME = 0x00040000;

    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_LAYERED = 0x00080000;

    /// <summary>
    /// 统一使用 Ptr 版本。Microsoft 明确建议在 32/64 位兼容场景用 SetWindowLongPtr，
    /// 32 位下它由 user32 转发到 SetWindowLongW，行为一致。
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport(Lib, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    // ---- 位置与状态 ----

    public static readonly nint HWND_TOP = 0;
    public static readonly nint HWND_BOTTOM = 1;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    // ---- 焦点转移（四级降级链） ----

    [LibraryImport(Lib)]
    public static partial nint GetForegroundWindow();

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport(Lib)]
    public static partial void SwitchToThisWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAltTab);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    // ---- 消息 ----

    public const uint WM_CLOSE = 0x0010;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// 跨进程同步消息一律走带超时的版本。用 SendMessageW 会在目标假死时把调用线程一起挂住，
    /// 而调用线程是 TabNest 的窗口控制队列 —— 一个卡住的应用就能冻结整个产品。
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static partial nint SendMessageTimeout(
        nint hWnd, uint Msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [LibraryImport(Lib, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    // ---- 显示器与 DPI ----

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport(Lib)]
    public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport(Lib)]
    public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport(Lib, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [LibraryImport(Lib)]
    public static partial uint GetDpiForWindow(nint hwnd);

    // ---- 系统参数 ----

    public const uint SPI_GETDRAGFULLWINDOWS = 0x0026;

    [LibraryImport(Lib, EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(
        uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>取指定屏幕坐标下的窗口。用于拖放命中判定。</summary>
    [LibraryImport(Lib)]
    public static partial nint WindowFromPoint(POINT Point);

    /// <summary>取顶层祖先窗口。WindowFromPoint 返回的可能是子控件，需要向上找到顶层窗口。</summary>
    [LibraryImport(Lib)]
    public static partial nint GetAncestor(nint hwnd, uint gaFlags);

    public const uint GA_ROOT = 2;

    // ---- WinEvent 钩子 ----

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        nint pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hWinEventHook);

    // ---- 消息循环 ----

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    public const uint PM_REMOVE = 0x0001;
    public const uint WM_QUIT = 0x0012;

    public const uint PM_NOREMOVE = 0x0000;

    [LibraryImport(Lib, EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>
    /// 线程的消息队列是惰性创建的。调用一次 PeekMessage 可以强制建立它 ——
    /// AttachThreadInput 要求参与的两个线程都有输入队列，否则直接失败。
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "PeekMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(
        out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport(Lib, EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport(Lib, EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(ref MSG lpMsg);

    [LibraryImport(Lib, EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint idThread, uint Msg, nint wParam, nint lParam);
}
