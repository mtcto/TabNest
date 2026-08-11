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

    public void HideWindow() => User32.ShowWindow(_hwnd, User32.SW_HIDE);

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
        public const uint MiddleButtonUp = WindowClass.WM_MBUTTONUP;
        public const uint RightButtonUp = WindowClass.WM_RBUTTONUP;
        public const uint DpiChanged = WindowClass.WM_DPICHANGED;
        public const uint Destroy = WindowClass.WM_DESTROY;
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
