using TabNest.Interop;

namespace TabNest.App;

/// <summary>
/// TabNest 进程入口。
///
/// 这里刻意不使用 WPF 的 Application 作为入口：常驻层（托盘 + 窗口观察 + 标签轨道）
/// 必须完全不触碰 WPF 类型，PresentationFramework 才不会被加载进内存。
/// 设置中心是唯一使用 WPF 的部分，在用户首次打开时才于独立 STA 线程上创建。
/// 这条约束由 --benchmark 模式自动校验。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var mode = CommandLine.Parse(args);

        if (mode.NeedsConsole)
        {
            ConsoleBridge.AttachToParent();
        }

        return mode.Kind switch
        {
            RunKind.Version => RunVersion(),
            RunKind.Help => RunHelp(),
            RunKind.Diagnose => Diagnostics.DiagnoseCommand.Run(mode),
            RunKind.Benchmark => Diagnostics.BenchmarkCommand.Run(mode),
            _ => RunInteractive(mode),
        };
    }

    private static int RunVersion()
    {
        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion}");
        return 0;
    }

    private static int RunHelp()
    {
        Console.WriteLine($"""
            TabNest {BuildInfo.DisplayVersion} —— Windows 窗口标签工作区

            用法：
              TabNest                启动常驻托盘程序
              TabNest --diagnose     列出当前窗口及可分组性判定原因（无 UI）
              TabNest --benchmark    自测性能指标并与基准对比（无 UI）
              TabNest --version      显示版本
              TabNest --help         显示本帮助

            选项：
              --json                 诊断/基准结果以 JSON 输出，便于脚本处理
              --all                  诊断时不过滤，列出全部顶层窗口
            """);
        return 0;
    }

    private static int RunInteractive(RunMode mode)
    {
        // 常驻模式将在后续任务中接入：单实例守卫 → 托盘 → 窗口观察 → 标签轨道。
        // 当前仅占位，保证骨架可构建、可启动、可退出。
        _ = mode;
        Console.WriteLine("TabNest 常驻模式尚未实现，请使用 --diagnose 验证窗口层。");
        return 0;
    }
}
