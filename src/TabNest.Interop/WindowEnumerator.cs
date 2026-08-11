using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 枚举顶层窗口并采集其属性。
///
/// 刻意不用 <c>EnumWindows</c> 回调，而是沿 Z 序链 <c>GetTopWindow</c> → <c>GW_HWNDNEXT</c> 遍历：
///   1. 免去托管回调跨越 P/Invoke 边界的生命周期问题；
///   2. 顺带得到 Z 序，判断"最前面的那个窗口"时不必再查一次。
/// </summary>
public sealed class WindowEnumerator(ProcessInspector processes)
{
    private readonly ProcessInspector _processes = processes;

    /// <summary>安全上限，避免异常的窗口链导致死循环。</summary>
    private const int MaxWindows = 4096;

    /// <summary>
    /// 枚举全部顶层窗口，按 Z 序从前到后。
    /// <paramref name="includeAll"/> 为 true 时跳过基础过滤，供 --diagnose --all 诊断使用。
    /// </summary>
    public List<WindowInfo> Enumerate(bool includeAll = false)
    {
        var results = new List<WindowInfo>(64);
        var hwnd = User32.GetTopWindow(0);
        var guard = 0;

        while (hwnd != 0 && guard++ < MaxWindows)
        {
            if (includeAll || PassesBasicFilter(hwnd))
            {
                var info = Describe(hwnd);
                if (info is not null)
                {
                    results.Add(info);
                }
            }

            hwnd = User32.GetWindow(hwnd, User32.GW_HWNDNEXT);
        }

        return results;
    }

    /// <summary>
    /// 廉价的预过滤，只用不需要打开进程的判据。
    /// 目的是避免对成百上千个 shell 内部窗口做昂贵的进程查询 —— 空闲 CPU 目标就靠这个撑住。
    /// 语义上的判定（工具窗、从属窗口、cloaked 等）留给 EligibilityEvaluator，
    /// 因为那些需要向用户解释，不能在这里静默丢弃。
    /// </summary>
    private static bool PassesBasicFilter(nint hwnd)
    {
        if (!User32.IsWindowVisible(hwnd))
        {
            return false;
        }

        var style = (long)User32.GetWindowLongPtr(hwnd, User32.GWL_STYLE);

        // 子窗口不是顶层窗口。理论上 Z 序链上不该出现，但驱动或注入类软件会制造例外。
        if ((style & User32.WS_CHILD) != 0)
        {
            return false;
        }

        return User32.GetWindowTextLength(hwnd) > 0;
    }

    /// <summary>
    /// 取屏幕坐标下的顶层窗口。
    /// <c>WindowFromPoint</c> 返回的可能是子控件，必须向上找到顶层窗口 ——
    /// 否则拖放会命中一个按钮而不是它所属的窗口。
    /// </summary>
    public static nint TopLevelWindowAt(PixelPoint screenPoint)
    {
        var pt = new POINT { X = screenPoint.X, Y = screenPoint.Y };
        var hwnd = User32.WindowFromPoint(pt);

        return hwnd == 0 ? 0 : User32.GetAncestor(hwnd, User32.GA_ROOT);
    }

    /// <summary>采集单个窗口的完整信息。窗口在采集过程中消失时返回 null。</summary>
    public WindowInfo? Describe(nint hwnd)
    {
        if (!User32.IsWindow(hwnd))
        {
            return null;
        }

        _ = User32.GetWindowThreadProcessId(hwnd, out var pid);
        var process = _processes.Inspect((int)pid);

        var exStyle = (long)User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE);
        var monitor = MonitorLookup.ForWindow(hwnd);

        return new WindowInfo
        {
            Identity = new WindowIdentity(hwnd, (int)pid, process.StartTicks),
            Title = ReadTitle(hwnd),
            ClassName = ReadClassName(hwnd),
            ProcessName = process.Name,
            ProcessPath = process.Path,
            Bounds = ReadVisibleBounds(hwnd),
            ShowState = ReadShowState(hwnd),
            IntegrityLevel = process.IntegrityLevel,
            Dpi = MonitorLookup.DpiForWindow(hwnd),
            MonitorDeviceName = monitor.DeviceName,
            IsCloaked = IsCloaked(hwnd),
            IsTopmost = (exStyle & User32.WS_EX_TOPMOST) != 0,
            IsToolWindow = (exStyle & User32.WS_EX_TOOLWINDOW) != 0,
            HasOwner = User32.GetWindow(hwnd, User32.GW_OWNER) != 0,
            IsNotResponding = User32.IsHungAppWindow(hwnd),
            IsUwp = process.IsPackaged,
            HasCustomTitleBar = HasCustomTitleBar(hwnd),
            ObservedAt = DateTimeOffset.UtcNow,
        };
    }

    public static string ReadTitle(nint hwnd)
    {
        var length = User32.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        Span<char> buffer = length < 256 ? stackalloc char[length + 1] : new char[length + 1];
        var written = User32.GetWindowText(hwnd, buffer, buffer.Length);

        return written <= 0 ? string.Empty : new string(buffer[..written]);
    }

    public static string ReadClassName(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        var written = User32.GetClassName(hwnd, buffer, buffer.Length);

        return written <= 0 ? string.Empty : new string(buffer[..written]);
    }

    /// <summary>
    /// 窗口的可见边框。
    ///
    /// 优先取 DWM 的扩展边框：<c>GetWindowRect</c> 会把 Windows 10/11 的不可见拖拽边框算进去，
    /// 左右和底部各多出约 7 像素，用它定位标签轨道会明显偏离窗口视觉边缘。
    /// </summary>
    public static PixelRect ReadVisibleBounds(nint hwnd)
    {
        var size = System.Runtime.InteropServices.Marshal.SizeOf<RECT>();
        if (Dwmapi.DwmGetWindowAttribute(
                hwnd, Dwmapi.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, size) == 0)
        {
            var bounds = dwmRect.ToPixelRect();
            if (!bounds.IsEmpty)
            {
                return bounds;
            }
        }

        return User32.GetWindowRect(hwnd, out var rect) ? rect.ToPixelRect() : PixelRect.Empty;
    }

    public static WindowShowState ReadShowState(nint hwnd)
    {
        if (User32.IsIconic(hwnd))
        {
            return WindowShowState.Minimized;
        }

        return User32.IsZoomed(hwnd) ? WindowShowState.Maximized : WindowShowState.Normal;
    }

    /// <summary>
    /// DWM 是否隐藏了该窗口。
    /// 其他虚拟桌面上的窗口、已挂起的 UWP 应用都是 cloaked —— 它们对用户不可见，
    /// 不该出现在可分组候选里。这个状态用 IsWindowVisible 查不出来。
    /// </summary>
    public static bool IsCloaked(nint hwnd)
    {
        return Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0;
    }

    /// <summary>
    /// 粗略判断窗口是否自绘标题栏。
    ///
    /// 依据是缺少 <c>WS_CAPTION</c> 却仍有可调整大小的边框 —— Electron、IDEA、Chrome
    /// 这类应用普遍如此。阶段一这个判断不影响能否分组（轨道贴在窗口上方），
    /// 它只决定阶段二能否使用集成标签模式，因此准确度要求不高。
    /// </summary>
    public static bool HasCustomTitleBar(nint hwnd)
    {
        var style = (long)User32.GetWindowLongPtr(hwnd, User32.GWL_STYLE);
        return (style & User32.WS_CAPTION) == 0 && (style & User32.WS_THICKFRAME) != 0;
    }
}
