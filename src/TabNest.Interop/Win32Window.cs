using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 原生 Win32 窗口基类。
///
/// 常驻层（托盘、标签轨道）用它而不是 WPF：只要不触碰任何 WPF 类型，
/// PresentationFramework 就不会被加载进内存，常驻占用才有可能压到 Groupy 之下。
///
/// 窗口过程用 <c>[UnmanagedCallersOnly]</c> 静态函数指针 + HWND→实例映射，
/// 避免托管委托被 GC 回收这个经典问题。
/// </summary>
public abstract class Win32Window : IDisposable
{
    private static readonly ConcurrentDictionary<nint, Win32Window> Instances = new();
    private static readonly ConcurrentDictionary<string, ushort> RegisteredClasses = new();

    private nint _hwnd;
    private bool _disposed;

    public nint Handle => _hwnd;

    public bool IsCreated => _hwnd != 0;

    protected static nint ModuleHandle { get; } = WindowClass.GetModuleHandle(null);

    /// <summary>供 <see cref="NativeControl"/> 创建子控件时使用。</summary>
    internal static nint ModuleHandleForControls => ModuleHandle;

    /// <summary>创建窗口。必须在拥有消息循环的线程上调用。</summary>
    protected void CreateWindow(
        string className,
        string? title,
        uint style,
        uint exStyle,
        int x, int y, int width, int height,
        uint classStyle = WindowClass.CS_HREDRAW | WindowClass.CS_VREDRAW | WindowClass.CS_DBLCLKS)
    {
        EnsureClassRegistered(className, classStyle);

        _hwnd = WindowClass.CreateWindowEx(
            exStyle, className, title, style,
            x, y, width, height,
            0, 0, ModuleHandle, 0);

        if (_hwnd == 0)
        {
            throw new InvalidOperationException(
                $"创建窗口失败，Win32 错误码 {Marshal.GetLastWin32Error()}。");
        }

        Instances[_hwnd] = this;
    }

    private static unsafe void EnsureClassRegistered(string className, uint classStyle)
    {
        if (RegisteredClasses.ContainsKey(className))
        {
            return;
        }

        var namePtr = Marshal.StringToHGlobalUni(className);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)sizeof(WNDCLASSEX),
            style = classStyle,
            lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&StaticWndProc,
            hInstance = ModuleHandle,
            hCursor = WindowClass.LoadCursor(0, WindowClass.IDC_ARROW),

            // 窗口图标。不设的话标题栏左上角、Alt+Tab、任务栏都是空白 ——
            // 自绘窗口不像 WPF 那样自动继承应用图标，必须在窗口类上显式给出。
            // 大图标用于 Alt+Tab 与任务栏，小图标用于标题栏。
            hIcon = AppIcon.Large,
            hIconSm = AppIcon.Small,

            // 背景刷设为 0：我们自绘全部内容，让系统先刷一次背景只会造成闪烁。
            hbrBackground = 0,
            lpszClassName = namePtr,
        };

        var atom = WindowClass.RegisterClassEx(ref wndClass);
        if (atom == 0)
        {
            var error = Marshal.GetLastWin32Error();

            // 1410 = ERROR_CLASS_ALREADY_EXISTS，多次注册同名类是良性的。
            if (error != 1410)
            {
                Marshal.FreeHGlobal(namePtr);
                throw new InvalidOperationException($"注册窗口类失败，Win32 错误码 {error}。");
            }
        }

        // 类名字符串必须在类的整个生命周期内保持有效，因此刻意不释放 namePtr。
        RegisteredClasses[className] = atom;
    }

    [UnmanagedCallersOnly]
    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (Instances.TryGetValue(hwnd, out var window))
            {
                var result = window.WndProc(msg, wParam, lParam);
                if (result.HasValue)
                {
                    return result.Value;
                }

                if (msg == WindowClass.WM_DESTROY)
                {
                    Instances.TryRemove(hwnd, out _);
                }
            }
        }
        catch (Exception ex)
        {
            // 异常绝不能穿出非托管回调边界 —— 那会直接终止进程。
            // 但也不能只写 Debug.WriteLine：发布运行时看不到，等于静默失效。
            Core.Diagnostics.FileLog.Error($"窗口过程处理消息 0x{msg:X} 时抛出异常。", ex);
        }

        return WindowClass.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>处理窗口消息。返回 null 表示交给默认处理。</summary>
    protected abstract nint? WndProc(uint msg, nint wParam, nint lParam);

    protected void Invalidate() => WindowClass.InvalidateRect(_hwnd, 0, false);

    /// <summary>
    /// 移动窗口到指定位置并显示。
    ///
    /// 始终带 SWP_NOACTIVATE —— 轨道跟随外部窗口移动时若抢了焦点，
    /// 用户拖动窗口的动作会被打断。
    /// </summary>
    /// <param name="bounds">目标矩形。</param>
    /// <param name="insertAfter">
    /// 插入到 Z 序中此窗口之后。传 0 表示不改变 Z 序 ——
    /// 轨道靠属主关系跟随成员窗口，不该自己乱插 Z 序。
    /// </param>
    public void SetBounds(PixelRect bounds, nint insertAfter = 0)
    {
        var flags = User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW;

        if (insertAfter == 0)
        {
            flags |= User32.SWP_NOZORDER;
        }

        User32.SetWindowPos(
            _hwnd, insertAfter,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            flags);
    }

    /// <summary>
    /// 设置本窗口的属主。
    ///
    /// 属主关系让本窗口始终显示在属主之上，并**跟随属主一起在 Z 序中升降**。
    /// 这是标签轨道的正确定位方式：用 <c>WS_EX_TOPMOST</c> 会让轨道浮在所有应用之上，
    /// 切到别的程序时轨道还赖在屏幕上，且其他窗口能穿插到轨道和成员窗口之间。
    ///
    /// 属主可以跨进程设置。失败时返回 false，调用方应降级为手动维护 Z 序。
    /// </summary>
    /// <returns>
    /// 是否**确认**设置成功。返回 false 不代表一定失败，只代表无法确认 ——
    /// 调用方必须始终配合显式的 Z 序维护，不能把可见性押在属主关系上。
    /// </returns>
    public bool SetOwner(nint ownerHwnd)
    {
        if (_hwnd == 0)
        {
            return false;
        }

        User32.SetWindowLongPtr(_hwnd, User32.GWLP_HWNDPARENT, ownerHwnd);

        // 读回来核对，而不是看错误码：SetWindowLongPtr 失败时不保证设置错误码，
        // 依赖它会得到"报告成功但什么都没做"的假象。
        return User32.GetWindowLongPtr(_hwnd, User32.GWLP_HWNDPARENT) == ownerHwnd;
    }

    /// <summary>窗口当前是否可见。用于诊断轨道为什么看不到。</summary>
    public bool IsVisible => _hwnd != 0 && User32.IsWindowVisible(_hwnd);

    /// <summary>本窗口所在显示器的 DPI。跨屏拖动后必须重新取。</summary>
    public int Dpi => _hwnd == 0 ? 96 : (int)User32.GetDpiForWindow(_hwnd);

    /// <summary>客户区尺寸。自绘窗口的一切布局都以它为准，而不是窗口矩形。</summary>
    public PixelRect ClientBounds =>
        WindowClass.GetClientRect(_hwnd, out var r) ? r.ToPixelRect() : PixelRect.Empty;

    /// <summary>显示窗口并置于前台。设置中心从托盘菜单打开时需要它真的跳到眼前。</summary>
    public void ShowAndActivate()
    {
        if (_hwnd == 0)
        {
            return;
        }

        // 已最小化时先还原，否则 SetForegroundWindow 只会让任务栏图标闪烁。
        if (User32.IsIconic(_hwnd))
        {
            User32.ShowWindow(_hwnd, User32.SW_RESTORE);
        }
        else
        {
            User32.ShowWindow(_hwnd, User32.SW_SHOW);
        }

        User32.SetForegroundWindow(_hwnd);
    }

    /// <summary>
    /// 让标题栏跟随明暗主题。
    ///
    /// 不做这件事的话，暗色模式下会是"白色标题栏 + 深色内容"的割裂观感 ——
    /// 这是自绘窗口最容易忽略的一处，因为标题栏由系统绘制，不在我们的绘制代码里。
    /// Windows 10 1809 之前不支持，静默失败即可。
    /// </summary>
    public void SetTitleBarDarkMode(bool dark)
    {
        if (_hwnd == 0)
        {
            return;
        }

        var value = dark ? 1 : 0;
        Dwmapi.DwmSetWindowAttribute(
            _hwnd, Dwmapi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>窗口当前的实际矩形。用于诊断定位是否正确。</summary>
    public PixelRect ActualBounds =>
        User32.GetWindowRect(_hwnd, out var rect) ? rect.ToPixelRect() : PixelRect.Empty;

    /// <summary>
    /// 把窗口裁成"上圆角、下直角"的形状。
    ///
    /// 分组条要和下方窗口无缝衔接：上边跟随窗口的圆角，下边必须是直角，
    /// 否则会在分组条和窗口之间露出两个白色缺口。
    /// <paramref name="radius"/> 为 0 时恢复成完整矩形（方角窗口、Windows 10、最大化窗口）。
    /// </summary>
    public void ApplyTopRoundedRegion(int width, int height, int radius)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (radius <= 0)
        {
            // 传 0 让系统丢弃区域，窗口恢复为完整矩形。
            Gdi.SetWindowRgn(_hwnd, 0, true);
            return;
        }

        // 圆角矩形四角都是圆的，再用一个矩形把下半部分补成直角。
        var rounded = Gdi.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        var bottom = Gdi.CreateRectRgn(0, radius, width + 1, height + 1);

        try
        {
            Gdi.CombineRgn(rounded, rounded, bottom, Gdi.RGN_OR);

            // SetWindowRgn 成功后区域归系统所有，不可再释放。
            if (Gdi.SetWindowRgn(_hwnd, rounded, true) == 0)
            {
                Gdi.DeleteObject(rounded);
            }
        }
        finally
        {
            Gdi.DeleteObject(bottom);
        }
    }

    /// <summary>
    /// 把本窗口置于 Z 序中 <paramref name="target"/> 的正上方。
    ///
    /// 注意这里移动的是 <paramref name="target"/> 而不是本窗口：
    /// <c>SetWindowPos</c> 的 <c>hWndInsertAfter</c> 语义是"排在该窗口**之后**"，
    /// 也就是更靠后。写成 <c>SetWindowPos(本窗口, target, ...)</c> 会把本窗口塞到
    /// target 后面去 —— 恰好是想要效果的反面，表现为轨道被成员窗口完全盖住。
    /// </summary>
    public void PlaceAbove(nint target)
    {
        if (target == 0 || _hwnd == 0)
        {
            return;
        }

        User32.SetWindowPos(
            target, _hwnd, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 把本窗口置于 Z 序中 <paramref name="target"/> 的正下方。
    ///
    /// 用于让分组栏藏在成员窗口之后：分组栏向下多延伸的一段会被窗口挡住，
    /// 而窗口圆角的缺口处正好透出分组栏的颜色 —— 接缝消失，
    /// 同时分组栏不会啃掉窗口的可见内容。
    /// </summary>
    public void PlaceBelow(nint target)
    {
        if (target == 0 || _hwnd == 0)
        {
            return;
        }

        User32.SetWindowPos(
            _hwnd, target, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    public void HideWindow() => User32.ShowWindow(_hwnd, User32.SW_HIDE);

    /// <summary>
    /// 捕获鼠标，让按住拖动期间的移动与松手消息在光标离开窗口后仍然送达。
    /// 拖动交互必须调用：分组条拖快了光标会瞬间甩出窗口，不捕获就丢松手事件。
    /// </summary>
    public void CaptureMouse()
    {
        if (_hwnd != 0)
        {
            User32.SetCapture(_hwnd);
        }
    }

    /// <summary>释放鼠标捕获。捕获是全局唯一的，因此这是静态操作。</summary>
    public static void ReleaseMouseCapture() => User32.ReleaseCapture();

    /// <summary>
    /// 注册鼠标离开通知。
    /// 不注册就收不到 <see cref="Messages.MouseLeave"/>，悬停高亮会永远留在最后停过的标签上。
    /// </summary>
    public bool TrackMouseLeave()
    {
        var track = new WindowClass.TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<WindowClass.TRACKMOUSEEVENT>(),
            dwFlags = WindowClass.TME_LEAVE,
            hwndTrack = _hwnd,
        };

        return WindowClass.TrackMouseEvent(ref track);
    }

    /// <summary>派生类需要的窗口消息。避免它们直接引用 Interop 内部的声明。</summary>
    public static class Messages
    {
        public const uint Paint = WindowClass.WM_PAINT;
        public const uint EraseBackground = WindowClass.WM_ERASEBKGND;
        public const uint MouseMove = WindowClass.WM_MOUSEMOVE;
        public const uint MouseLeave = WindowClass.WM_MOUSELEAVE;
        public const uint LeftButtonDown = WindowClass.WM_LBUTTONDOWN;
        public const uint LeftButtonUp = WindowClass.WM_LBUTTONUP;
        public const uint CaptureChanged = WindowClass.WM_CAPTURECHANGED;
        public const uint MiddleButtonUp = WindowClass.WM_MBUTTONUP;
        public const uint RightButtonUp = WindowClass.WM_RBUTTONUP;
        public const uint MouseWheel = WindowClass.WM_MOUSEWHEEL;
        public const uint DpiChanged = WindowClass.WM_DPICHANGED;
        public const uint Destroy = WindowClass.WM_DESTROY;
        public const uint Close = WindowClass.WM_CLOSE;
        public const uint Size = WindowClass.WM_SIZE;
        public const uint GetMinMaxInfo = WindowClass.WM_GETMINMAXINFO;

        /// <summary>系统设置变更广播。明暗主题切换靠它感知。</summary>
        public const uint SettingChange = WindowClass.WM_SETTINGCHANGE;

        /// <summary>控件通知父窗口：按钮点击、下拉框选择变化。</summary>
        public const uint Command = WindowClass.WM_COMMAND;

        public const uint CtlColorEdit = WindowClass.WM_CTLCOLOREDIT;
        public const uint CtlColorStatic = WindowClass.WM_CTLCOLORSTATIC;
        public const uint CtlColorBtn = WindowClass.WM_CTLCOLORBTN;
    }

    /// <summary>普通窗口样式。设置中心用它，而不是轨道那种无边框弹出窗口。</summary>
    public static class User32Styles
    {
        /// <summary>标准可缩放窗口：标题栏、系统菜单、最小化/最大化、可拖边框。</summary>
        public const long OverlappedWindow = 0x00CF0000;

        /// <summary>绘制时跳过子窗口区域。宿主原生控件时必须带，否则会画到控件上造成闪烁。</summary>
        public const long ClipChildren = 0x02000000;

        public const long Caption = 0x00C00000;
        public const long SysMenu = 0x00080000;

        /// <summary>对话框边框：不可缩放。规则编辑器的布局是固定的，允许缩放只会露出空白。</summary>
        public const long DialogFrame = 0x00400000;
    }

    /// <summary>取窗口 DPI，供对话框按所有者的缩放布局。</summary>
    public static class User32Dpi
    {
        public static uint ForWindow(nint hwnd) => User32.GetDpiForWindow(hwnd);
    }

    /// <summary>启用或禁用窗口。模态对话框靠它锁住所有者。</summary>
    public static class User32Modal
    {
        /// <returns>之前是否处于启用状态。</returns>
        public static bool Enable(nint hwnd, bool enable) => !User32.EnableWindow(hwnd, enable);
    }

    /// <summary>
    /// 调整原生控件的配色，让它与自绘外壳协调。
    ///
    /// 不做这件事的话，暗色主题下会是"深色面板上一块块刺眼的白底控件"。
    /// </summary>
    public static class ControlColors
    {
        /// <summary>设置控件的前景与背景色，返回用作背景的画刷句柄。</summary>
        public static nint Apply(nint hdc, uint textArgb, uint backgroundArgb, nint existingBrush)
        {
            Gdi.SetTextColor(hdc, Gdi.ToColorRef(textArgb));
            Gdi.SetBkColor(hdc, Gdi.ToColorRef(backgroundArgb));

            // 画刷要复用：每次 WM_CTLCOLOR 都新建一个而不释放，
            // 会以重绘的频率泄漏 GDI 对象，几分钟就能耗尽句柄配额。
            if (existingBrush != 0)
            {
                return existingBrush;
            }

            return Gdi.CreateSolidBrush(Gdi.ToColorRef(backgroundArgb));
        }

        public static void ReleaseBrush(nint brush)
        {
            if (brush != 0)
            {
                Gdi.DeleteObject(brush);
            }
        }
    }

    /// <summary>WM_GETMINMAXINFO 的载荷写入。避免上层直接触碰非托管结构。</summary>
    public static class MinMaxInfo
    {
        public static unsafe void SetMinTrackSize(nint lParam, int width, int height)
        {
            if (lParam == 0)
            {
                return;
            }

            var info = (MINMAXINFO*)lParam;
            info->ptMinTrackSize.X = width;
            info->ptMinTrackSize.Y = height;
        }
    }

    /// <summary>
    /// 一次性测量画布。
    ///
    /// 布局要先量文本才能定位，而测量发生在 WM_PAINT 的 BeginPaint 之前，
    /// 此时还没有绘制用的 DC，因此单独取一个窗口 DC 专门用于测量。
    /// </summary>
    public sealed class MeasureCanvas : IDisposable
    {
        private readonly nint _hwnd;
        private readonly nint _dc;

        private MeasureCanvas(nint hwnd, nint dc)
        {
            _hwnd = hwnd;
            _dc = dc;
            Canvas = new GdiCanvas(dc);
        }

        public GdiCanvas Canvas { get; }

        public static MeasureCanvas Create(nint hwnd) =>
            new(hwnd, WindowClass.GetDC(hwnd));

        public void Dispose()
        {
            Canvas.Dispose();

            if (_dc != 0)
            {
                _ = WindowClass.ReleaseDC(_hwnd, _dc);
            }
        }
    }

    /// <summary>窗口样式常量，供派生类构造时使用，避免它们直接引用内部的 User32。</summary>
    public static class Styles
    {
        public const uint Popup = 0x80000000;

        public static uint ToolWindow => (uint)User32.WS_EX_TOOLWINDOW;

        /// <summary>点击时不抢焦点。轨道必须有这个样式，否则每次切换标签焦点都要绕一圈。</summary>
        public static uint NoActivate => (uint)User32.WS_EX_NOACTIVATE;

        public static uint Topmost => (uint)User32.WS_EX_TOPMOST;
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != 0)
        {
            Instances.TryRemove(_hwnd, out _);
            WindowClass.DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        GC.SuppressFinalize(this);
    }
}
