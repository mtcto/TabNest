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

    private const int SW_HIDE = 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

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
