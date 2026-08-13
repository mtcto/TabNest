using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace TabNest.Harness;

/// <summary>
/// 六类测试窗口。每一类都对应一个真实世界里会让窗口管理工具出问题的场景，
/// 目的是让这些场景可以被反复复现，而不必每次去开 IDEA、Chrome 碰运气。
/// </summary>
internal static partial class TestWindows
{
    private static int _counter;

    /// <summary>普通窗口。基准场景，绝大多数应用长这样。</summary>
    public static Window CreateNormal() =>
        Decorate(new Window(), "普通窗口", Brushes.SteelBlue,
            "标准标题栏、可调整大小。分组、切换、拆分都应当完美工作。");

    /// <summary>
    /// 自绘标题栏窗口。
    /// 对应 Electron、IDEA、Chrome 这类应用 —— 阶段一轨道贴在窗口上方不受影响，
    /// 但阶段二的集成标签模式对它们不可用。
    /// </summary>
    public static Window CreateCustomTitleBar()
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            ResizeMode = ResizeMode.CanResize,
        };

        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 32,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        return Decorate(window, "自绘标题栏", Brushes.DarkOrange,
            "无 WS_CAPTION 但有可调整边框，模拟 Electron / IDEA。\n"
            + "轨道应贴在窗口上方；集成标签模式对它不可用。");
    }

    /// <summary>置顶窗口。拆分还原时必须把置顶属性还回去，静默丢掉是数据损失。</summary>
    public static Window CreateTopmost() =>
        Decorate(new Window { Topmost = true }, "置顶窗口", Brushes.MediumPurple,
            "WS_EX_TOPMOST 已设置。\n拆分后必须仍然置顶 —— 还原时丢掉这个属性是数据损失。");

    /// <summary>
    /// 带模态对话框的窗口。
    /// 模态框是有属主的窗口，必须被判定为不可分组；
    /// 而且绝不能隐藏主窗口，否则用户会看到一个无法操作的孤立对话框。
    /// </summary>
    public static Window CreateWithModal()
    {
        var window = Decorate(new Window(), "含模态对话框", Brushes.Firebrick,
            "点下面的按钮弹出模态框。\n模态框有属主，应被判定为不可分组。");

        var panel = (StackPanel)window.Content;
        var button = new Button
        {
            Content = "弹出模态对话框",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
        };

        button.Click += (_, _) =>
        {
            var dialog = new Window
            {
                Title = "模态对话框",
                Width = 320,
                Height = 160,
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Content = new TextBlock
                {
                    Text = "这是一个模态对话框。\nTabNest 不应把它当作可分组窗口。",
                    Margin = new Thickness(16),
                    TextWrapping = TextWrapping.Wrap,
                },
            };

            dialog.ShowDialog();
        };

        panel.Children.Add(button);
        return window;
    }

    /// <summary>
    /// 可主动阻塞 UI 线程的窗口。
    ///
    /// 这是最重要的一类测试窗口。它验证 TabNest 是否真的做到了"目标应用假死不拖垮自己" ——
    /// 若 WindowController 用了不带超时的 SendMessage，对这个窗口执行一次切换
    /// 就会把整个控制队列挂住。
    /// </summary>
    public static Window CreateSlowResponding()
    {
        var window = Decorate(new Window(), "慢响应窗口", Brushes.DarkSlateGray,
            "点击按钮会阻塞 UI 线程 10 秒，期间窗口对系统表现为无响应。\n"
            + "TabNest 应检测到并跳过，而不是跟着一起卡住。");

        var panel = (StackPanel)window.Content;
        var button = new Button
        {
            Content = "阻塞 UI 线程 10 秒",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
        };

        button.Click += (_, _) => Thread.Sleep(TimeSpan.FromSeconds(10));

        panel.Children.Add(button);
        return window;
    }

    /// <summary>无标题工具窗口。应被基础过滤直接排除。</summary>
    public static Window CreateToolWindow() =>
        Decorate(
            new Window
            {
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Title = string.Empty,
            },
            string.Empty,
            Brushes.Gray,
            "WS_EX_TOOLWINDOW 且无标题。\n应被基础过滤排除，不出现在候选列表里。");

    /// <summary>
    /// 有最大尺寸限制的小窗口。
    ///
    /// 复现一类真实缺陷：把这种窗口和一个大窗口分到一组时，我们把它对齐到大矩形，
    /// 系统会按 MaxWidth/MaxHeight 把它夹回小尺寸，它随即上报这个被夹过的矩形。
    /// 若编排层把这份回声当成"用户改了大小"去同步整组，大窗口就会被拉成小窗口，
    /// 而且用户再怎么拉宽都会被拽回去 —— 表现为"这个窗口的宽度改不了了"。
    ///
    /// OpenVPN Connect、各类登录框与设置面板都是这种窗口。
    /// </summary>
    public static Window CreateFixedSize()
    {
        var window = Decorate(
            new Window { ResizeMode = ResizeMode.CanMinimize },
            "固定尺寸窗口",
            Brushes.DarkOrange,
            "MaxWidth/MaxHeight 已锁死，无法被放大。\n"
            + "与大窗口分组后，大窗口必须保持自己的尺寸，且仍可自由调整 ——\n"
            + "本窗口被夹回小尺寸后上报的位置属于回声，不该驱动整组。");

        window.Width = 320;
        window.Height = 220;
        window.MaxWidth = 320;
        window.MaxHeight = 220;
        window.MinWidth = 320;
        window.MinHeight = 220;

        return window;
    }

    /// <summary>
    /// 最大化时拒绝被重定位的窗口 —— 模仿 Chromium。
    ///
    /// Chromium 系应用（Electron 外壳的 Claude / VS Code / Slack、Chrome 本身，
    /// 窗口类都是 Chrome_WidgetWin_1）在最大化状态下会于 WM_WINDOWPOSCHANGING
    /// 里把矩形强行改回整个工作区：SetWindowPos 返回成功，窗口却立刻弹回。
    ///
    /// 这个窗口的存在是一次教训的固化。「全屏后分组栏仍然可见」曾经用普通 WPF 窗口
    /// 测得全绿，而用户一在真实应用上试就发现分组栏被完全盖住 ——
    /// 因为 WPF 窗口老老实实接受重定位，真实应用不接受。
    /// 测试宿主里的窗口有多听话，测试就有多没用。
    /// </summary>
    public static Window CreateStubbornMaximize()
    {
        var window = Decorate(
            new Window(),
            "拒绝重定位",
            Brushes.MediumVioletRed,
            "模仿 Chromium：最大化时把自己钉死在整个工作区。\n"
            + "分组全屏必须先把它变回普通窗口再铺满，否则分组栏会被完全盖住。");

        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle == 0)
            {
                return;
            }

            HwndSource.FromHwnd(handle)?.AddHook(StubbornHook);
        };

        return window;
    }

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private static nint StubbornHook(
        nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_WINDOWPOSCHANGING || !IsZoomed(hwnd))
        {
            return 0;
        }

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
        {
            return 0;
        }

        // 把请求的位置原地改写回整个工作区，正是 Chromium 的做法：
        // 调用方拿到的是成功，窗口却纹丝不动。
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);

        pos.x = mi.rcWork.Left;
        pos.y = mi.rcWork.Top;
        pos.cx = mi.rcWork.Right - mi.rcWork.Left;
        pos.cy = mi.rcWork.Bottom - mi.rcWork.Top;

        Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    /// <summary>
    /// 关闭时只隐藏、不销毁的窗口。
    ///
    /// 复现另一类真实缺陷：Electron 应用（Claude、Codex、VS Code）关闭窗口时
    /// 往往只是隐藏，永远不发销毁事件。若编排层只处理销毁事件，
    /// 窗口消失了而标签和分组栏还留着，再从标签拆分还会把这个已经"关掉"的窗口
    /// 重新显示出来，而它已不再响应任何输入。
    /// </summary>
    public static Window CreateHideOnClose()
    {
        var window = Decorate(new Window(), "关闭即隐藏", Brushes.Teal,
            "关闭时只隐藏窗口而不销毁，永远不会发出销毁事件。\n"
            + "TabNest 必须据此把它移出分组，且绝不能再把它显示回来。");

        window.Closing += (_, e) =>
        {
            e.Cancel = true;

            // 刻意直接隐藏**原生窗口**，而不是调 WPF 的 window.Hide()。
            //
            // WPF 的 Hide 会把 Visibility 记为 Hidden 并持续跟踪：外部若强行
            // 显示这个窗口，WPF 会把它再藏回去，于是"窗口被复活"这个缺陷
            // 在 WPF 宿主下根本复现不出来。而 Electron 应用（Claude、Codex）
            // 隐藏窗口时没有这层托管记账，外部一显示它就真的出现在屏幕上，
            // 但应用早已认为自己关闭，因此不响应任何输入。
            // 要让测试真的能抓住那个缺陷，这里必须绕开 WPF 的记账。
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != 0)
            {
                ShowWindow(hwnd, SW_HIDE);
            }
        };

        return window;
    }

    /// <summary>
    /// 带一个「有标题的可见子窗口」的窗口。
    ///
    /// 复现 Chrome 的「Chrome Legacy Window」：它是渲染窗口的子窗口，
    /// 无障碍接口被触发时才创建，有标题、可见、非工具窗 ——
    /// 从"有没有标题""是不是工具窗"这些角度完全看不出它不该被分组。
    ///
    /// 曾经它被自动分组收编进组里，用户莫名多出一个点不动的空白标签。
    /// 根因是判定顶层窗口的过滤只写在枚举路径上，而自动分组走的是窗口事件，
    /// 直接对任意 HWND 调 Describe，绕开了那道过滤。
    /// </summary>
    public static Window CreateWithTitledChild()
    {
        var window = Decorate(new Window(), "含子窗口", Brushes.SlateBlue,
            "内部有一个有标题、可见的子窗口（模拟 Chrome Legacy Window）。\n"
            + "它绝不能出现在候选列表里，也绝不能被自动分组收编。");

        window.SourceInitialized += (_, _) =>
        {
            var parent = new WindowInteropHelper(window).Handle;

            if (parent != 0)
            {
                _ = CreateWindowEx(
                    0, "STATIC", "Chrome Legacy Window", WS_CHILD | WS_VISIBLE,
                    0, 0, 200, 40, parent, 0, 0, 0);
            }
        };

        return window;
    }

    /// <summary>
    /// 声明了独立 AppUserModelID 的窗口。
    ///
    /// 复现 Chrome 的 PWA（安装到桌面的网页应用）：它与普通浏览器窗口跑在
    /// **同一个 chrome.exe 进程**里，用的还是同一个窗口类 Chrome_WidgetWin_1 ——
    /// 从进程名和窗口类完全看不出这是两个不同的应用。唯一的区别是 AppUserModelID，
    /// 而那正是 Windows 任务栏用来决定 PWA 单独占一个图标的依据。实测：
    ///   普通 Chrome 窗口   Chrome
    ///   Grok PWA 窗口      Chrome._crx_ggjocahimgbfhghnlfcnjemagj
    ///
    /// 曾经「自动把同类窗口合并到一组」只比进程名，于是组里只要有一个 PWA，
    /// 用户新开的任意 Chrome 窗口都会被吞进去，并伴随持续闪烁。
    /// </summary>
    public static Window CreateDistinctAppId()
    {
        var window = Decorate(new Window(), "独立应用标识", Brushes.DarkCyan,
            "与其他测试窗口同进程、同窗口类，但声明了独立的 AppUserModelID。\n"
            + "模拟 Chrome PWA：它不该和普通窗口被认成同一个应用。");

        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle != 0)
            {
                SetAppUserModelId(handle, $"TabNest.Harness.FakePwa.{Guid.NewGuid():N}");
            }
        };

        return window;
    }

    /// <summary>给窗口打上显式的 AppUserModelID，与 Chrome 给 PWA 窗口做的事一致。</summary>
    private static void SetAppUserModelId(nint hwnd, string id)
    {
        var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"); // IID_IPropertyStore

        if (SHGetPropertyStoreForWindow(hwnd, in iid, out var native) < 0 || native == 0)
        {
            return;
        }

        try
        {
            if (Marshal.GetObjectForIUnknown(native) is not IPropertyStore store)
            {
                return;
            }

            var key = new PropertyKey
            {
                FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                PropertyId = 5,
            };

            var value = new PropVariant
            {
                Type = 31, // VT_LPWSTR
                Pointer = Marshal.StringToCoTaskMemUni(id),
            };

            try
            {
                store.SetValue(in key, in value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
                Marshal.FinalReleaseComObject(store);
            }
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Pointer;
        public nint Tail;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);

        void GetAt(uint index, out PropertyKey key);

        void GetValue(in PropertyKey key, out PropVariant value);

        void SetValue(in PropertyKey key, in PropVariant value);

        void Commit();
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(
        nint hwnd, in Guid riid, out nint ppv);

    private const int SW_HIDE = 0;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    private static Window Decorate(Window window, string label, Brush accent, string description)
    {
        var index = Interlocked.Increment(ref _counter);

        window.Title = string.IsNullOrEmpty(label)
            ? string.Empty
            : $"Harness {index} · {label}";

        window.Width = 560;
        window.Height = 340;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        // 层叠摆放，避免新窗口完全盖住上一个，方便手工拖拽测试。
        window.Left = 120 + (index * 36);
        window.Top = 120 + (index * 36);

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new Border
                {
                    Background = accent,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(label) ? "（无标题工具窗）" : label,
                        Foreground = Brushes.White,
                        FontSize = 18,
                        FontWeight = FontWeights.SemiBold,
                    },
                },
                new TextBlock
                {
                    Text = description,
                    Margin = new Thickness(0, 14, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                },
                new TextBlock
                {
                    Margin = new Thickness(0, 14, 0, 0),
                    Foreground = Brushes.Gray,
                    FontFamily = new FontFamily("Consolas"),
                    Text = $"#{index}",
                },
            },
        };

        return window;
    }
}
