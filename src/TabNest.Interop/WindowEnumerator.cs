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

    /// <summary>
    /// 取屏幕坐标下、排除指定窗口之后最靠前的顶层窗口。
    ///
    /// 拖放命中判定**必须**用这个而不是 <see cref="TopLevelWindowAt"/>：
    /// 拖拽过程中被拖的窗口就跟在光标底下，<c>WindowFromPoint</c> 永远返回它自己，
    /// 真正的拖放目标被它盖住了。所以只能沿 Z 序自顶向下找第一个命中的其他窗口。
    /// </summary>
    /// <param name="screenPoint">屏幕坐标。</param>
    /// <param name="excluded">要跳过的窗口，通常是被拖的窗口和 TabNest 自己的轨道。</param>
    public static nint TopLevelWindowAtExcluding(PixelPoint screenPoint, IReadOnlySet<nint> excluded)
    {
        foreach (var hwnd in EnumerateWindowsAt(screenPoint, excluded))
        {
            return hwnd;
        }

        return 0;
    }

    /// <summary>按 Z 序自顶向下列出全部顶层窗口句柄。用于诊断 Z 序相关问题。</summary>
    public static IEnumerable<nint> EnumerateAllTopLevel()
    {
        var hwnd = User32.GetTopWindow(0);
        var guard = 0;

        while (hwnd != 0 && guard++ < MaxWindows)
        {
            yield return hwnd;
            hwnd = User32.GetWindow(hwnd, User32.GW_HWNDNEXT);
        }
    }

    /// <summary>
    /// 按 Z 序自顶向下列出光标处的**所有**候选窗口。
    ///
    /// 只取最上面那一个是不够的：用户的两个目标窗口之间常常隔着别的应用，
    /// 此时最上面的是那个无关窗口，下面真正想分组的窗口就永远看不到 ——
    /// 表现为"中间有程序隔层就无法检测到"。
    ///
    /// 返回整条候选链，由调用方挑第一个**可分组**的，从而自动跳过隔层窗口。
    /// </summary>
    public static IEnumerable<nint> EnumerateWindowsAt(
        PixelPoint screenPoint, IReadOnlySet<nint> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);

        var hwnd = User32.GetTopWindow(0);
        var guard = 0;

        while (hwnd != 0 && guard++ < MaxWindows)
        {
            if (!excluded.Contains(hwnd)
                && User32.IsWindowVisible(hwnd)
                && !User32.IsIconic(hwnd)
                && User32.GetWindowTextLength(hwnd) > 0
                && !IsCloaked(hwnd)
                && ReadVisibleBounds(hwnd).Contains(screenPoint))
            {
                yield return hwnd;
            }

            hwnd = User32.GetWindow(hwnd, User32.GW_HWNDNEXT);
        }
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
    /// 当前前台窗口，即用户正在操作的窗口。
    ///
    /// 编排层用它判定"这次位置变化是不是用户的意图"：组成员被我们对齐后
    /// 会回报自己的新位置，若把这类回声也当成用户意图，就会形成
    /// "对齐 → 回声 → 再对齐"的回环。
    /// </summary>
    public static nint ForegroundWindow() => User32.GetForegroundWindow();

    /// <summary>
    /// 窗口是否仍被它自己的应用"展示着"。
    ///
    /// 这是判定"组成员是否已经被关掉"的唯一可靠判据，也是执行任何还原、
    /// 激活、对齐之前必须过的一关 —— 对应用已经关掉的窗口做还原等于把它
    /// 从坟里拽出来：窗口出现在屏幕上，应用却认为自己已关闭，于是它不响应
    /// 任何输入，用户只能去托盘重新打开。
    ///
    /// 三类状态刻意都算作"还活着"，因为它们都是用户随时会回来的临时状态：
    ///   - **最小化**：IsWindowVisible 仍为 true，最小化不改变可见性标志；
    ///   - **被 cloak**：窗口在另一个虚拟桌面上。绝不能算作消失 ——
    ///     那会让用户切个虚拟桌面回来发现分组没了；
    ///   - 被其他窗口遮挡：与可见性无关。
    ///
    /// 而被关闭的窗口无论是销毁还是隐藏到托盘，都不再满足 IsWindowVisible。
    /// </summary>
    public static bool IsAliveAndShown(nint hwnd) =>
        hwnd != 0 && User32.IsWindow(hwnd) && User32.IsWindowVisible(hwnd);

    /// <summary>
    /// 目标窗口顶部的圆角半径（物理像素）。0 表示直角。
    ///
    /// 分组条要和窗口无缝衔接，圆角就必须跟着窗口走，不能写死：
    ///   - Windows 10 完全不给窗口圆角，写死圆角会在窗口方角上方露出两个缺口；
    ///   - Windows 11 默认圆角，但应用可以通过 DWMWA_WINDOW_CORNER_PREFERENCE
    ///     显式声明 DONOTROUND（不少 Electron 应用和自绘标题栏的工具就这么做）；
    ///   - 最大化的窗口在 Windows 11 上也是直角。
    /// </summary>
    public static int TopCornerRadius(nint hwnd, int dpi)
    {
        // Windows 11 之前没有系统级窗口圆角。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return 0;
        }

        // 最大化窗口贴着屏幕边缘，系统不给它圆角。
        if (User32.IsZoomed(hwnd))
        {
            return 0;
        }

        var preference = Dwmapi.DWMWCP_DEFAULT;
        Dwmapi.DwmGetWindowAttribute(
            hwnd, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, out preference, sizeof(int));

        var logical = preference switch
        {
            Dwmapi.DWMWCP_DONOTROUND => 0,
            Dwmapi.DWMWCP_ROUNDSMALL => 4,
            _ => 8,
        };

        return logical == 0
            ? 0
            : (int)Math.Round(logical * dpi / 96.0, MidpointRounding.AwayFromZero);
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
