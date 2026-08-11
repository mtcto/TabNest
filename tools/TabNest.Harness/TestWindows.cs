using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace TabNest.Harness;

/// <summary>
/// 六类测试窗口。每一类都对应一个真实世界里会让窗口管理工具出问题的场景，
/// 目的是让这些场景可以被反复复现，而不必每次去开 IDEA、Chrome 碰运气。
/// </summary>
internal static class TestWindows
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
