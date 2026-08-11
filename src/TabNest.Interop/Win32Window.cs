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
            System.Diagnostics.Debug.WriteLine($"[TabNest] 窗口过程异常：{ex}");
        }

        return WindowClass.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>处理窗口消息。返回 null 表示交给默认处理。</summary>
    protected abstract nint? WndProc(uint msg, nint wParam, nint lParam);

    protected void Invalidate() => WindowClass.InvalidateRect(_hwnd, 0, false);

    /// <summary>
    /// 移动窗口到指定位置并显示，保持置顶。
    /// 始终带 SWP_NOACTIVATE —— 轨道跟随外部窗口移动时若抢了焦点，
    /// 用户拖动窗口的动作会被打断。
    /// </summary>
    public void SetBounds(PixelRect bounds, bool topmost = true)
    {
        User32.SetWindowPos(
            _hwnd,
            topmost ? User32.HWND_TOPMOST : User32.HWND_TOP,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
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
