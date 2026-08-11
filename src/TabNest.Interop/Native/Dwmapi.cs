using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

internal static partial class Dwmapi
{
    private const string Lib = "dwmapi.dll";

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    /// <summary>窗口圆角偏好（Windows 11 22000+）。</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    /// <summary>
    /// 取窗口的真实可见边框。
    ///
    /// 这不是可有可无的精细化：<c>GetWindowRect</c> 返回的矩形包含 Windows 10/11 的
    /// 不可见拖拽边框，左右和底部通常各多出 7~8 像素。用它定位标签轨道会让轨道
    /// 明显偏离窗口视觉边缘，且不同 DPI 下偏移量还不一样。
    /// </summary>
    [LibraryImport(Lib)]
    public static partial int DwmGetWindowAttribute(
        nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [LibraryImport(Lib)]
    public static partial int DwmGetWindowAttribute(
        nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [LibraryImport(Lib)]
    public static partial int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);

    /// <summary>
    /// 设置窗口属性。可跨进程作用于其他应用的窗口 ——
    /// 用于在分组期间把成员窗口的顶部改为直角，与上方的分组条严丝合缝地拼成一体。
    /// </summary>
    [LibraryImport(Lib)]
    public static partial int DwmSetWindowAttribute(
        nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
