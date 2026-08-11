using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TabNest.Harness;

/// <summary>
/// 窗口行为测试宿主。开发工具，不随产品发布。
///
/// 存在的意义是让窗口测试可重复：手工去开 IDEA、Chrome 来试分组，
/// 每次的窗口尺寸、位置、状态都不一样，出了问题也难以复现。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 支持 <c>--spawn normal,normal,slow</c> 预先开好测试窗口。
    /// 没有这个参数就只能靠手工点击，自动化验证无从谈起。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var launcher = new LauncherWindow();

        foreach (var name in ParseSpawnList(args))
        {
            launcher.SpawnByName(name);
        }

        app.Run(launcher);
    }

    private static IEnumerable<string> ParseSpawnList(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].TrimStart('-', '/').Equals("spawn", StringComparison.OrdinalIgnoreCase)
                || i + 1 >= args.Length)
            {
                continue;
            }

            foreach (var name in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return name.Trim();
            }
        }
    }
}

internal sealed class LauncherWindow : Window
{
    private readonly List<Window> _spawned = [];

    public LauncherWindow()
    {
        Title = "TabNest Harness";
        Width = 380;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;

        var panel = new StackPanel { Margin = new Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = "测试窗口生成器",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "每一类都对应一个会让窗口管理工具出问题的真实场景。",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
        });

        Add(panel, "普通窗口", TestWindows.CreateNormal);
        Add(panel, "自绘标题栏", TestWindows.CreateCustomTitleBar);
        Add(panel, "置顶窗口", TestWindows.CreateTopmost);
        Add(panel, "含模态对话框", TestWindows.CreateWithModal);
        Add(panel, "慢响应窗口", TestWindows.CreateSlowResponding);
        Add(panel, "无标题工具窗", TestWindows.CreateToolWindow);

        panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });

        var pair = new Button
        {
            Content = "一键开两个普通窗口（分组测试）",
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };
        pair.Click += (_, _) =>
        {
            Spawn(TestWindows.CreateNormal);
            Spawn(TestWindows.CreateNormal);
        };
        panel.Children.Add(pair);

        var closeAll = new Button
        {
            Content = "关闭全部测试窗口",
            Padding = new Thickness(10, 8, 10, 8),
        };
        closeAll.Click += (_, _) => CloseSpawned();
        panel.Children.Add(closeAll);

        Content = panel;
        Closed += (_, _) => CloseSpawned();
    }

    private void Add(Panel parent, string label, Func<Window> factory)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 5),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

        button.Click += (_, _) => Spawn(factory);
        parent.Children.Add(button);
    }

    /// <summary>按名称生成测试窗口，供命令行 --spawn 使用。</summary>
    public void SpawnByName(string name)
    {
        Func<Window>? factory = name.ToLowerInvariant() switch
        {
            "normal" => TestWindows.CreateNormal,
            "custom" or "customtitlebar" => TestWindows.CreateCustomTitleBar,
            "topmost" => TestWindows.CreateTopmost,
            "modal" => TestWindows.CreateWithModal,
            "slow" => TestWindows.CreateSlowResponding,
            "tool" => TestWindows.CreateToolWindow,
            _ => null,
        };

        if (factory is not null)
        {
            Spawn(factory);
        }
    }

    private void Spawn(Func<Window> factory)
    {
        var window = factory();
        _spawned.Add(window);
        window.Closed += (_, _) => _spawned.Remove(window);
        window.Show();
    }

    private void CloseSpawned()
    {
        foreach (var window in _spawned.ToArray())
        {
            window.Close();
        }

        _spawned.Clear();
    }
}
