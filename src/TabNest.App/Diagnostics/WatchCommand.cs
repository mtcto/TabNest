using System.Globalization;
using TabNest.Core.Models;
using TabNest.Core.Rules;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 实时窗口事件监视。
///
/// 拖拽合并这类交互没法用单元测试覆盖，出问题时也很难判断是"事件没来"、
/// "目标判定错了"还是"资格被拒"。这个模式把整条链路摊开给人看。
/// </summary>
internal static class WatchCommand
{
    public static int Run(RunMode mode)
    {
        _ = mode;

        var processes = new ProcessInspector();
        var enumerator = new WindowEnumerator(processes);
        var environment = SystemEnvironmentProbe.Probe(processes);

        using var observer = new WinEventObserver();

        // 监视模式关注所有窗口的位置变化，与常驻模式的按需过滤不同。
        observer.SetLocationWatchList([]);

        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion} 事件监视（Ctrl+C 退出）");
        Console.WriteLine("拖动一个窗口到另一个窗口的标题栏上，观察下方输出。");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        nint dragging = 0;

        try
        {
            foreach (var evt in observer.Events.ReadAllAsync(cts.Token).ToBlockingEnumerable())
            {
                switch (evt.Kind)
                {
                    case WindowEventKind.MoveSizeStart:
                        dragging = evt.Handle;
                        Log("拖拽开始", Describe(evt.Handle));
                        break;

                    case WindowEventKind.MoveSizeEnd:
                        ReportDrop(enumerator, environment, dragging, evt.Handle);
                        dragging = 0;
                        break;

                    case WindowEventKind.Destroyed:
                    case WindowEventKind.Foreground:
                        // 高频且与拖拽无关，不打印以免刷屏。
                        break;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C。
        }

        Console.WriteLine();
        Console.WriteLine("已停止监视。");
        return 0;
    }

    private static void ReportDrop(
        WindowEnumerator enumerator,
        SystemEnvironment environment,
        nint dragging,
        nint ended)
    {
        Log("拖拽结束", Describe(ended));

        if (dragging == 0 || dragging != ended)
        {
            Log("  判定", "拖拽起止窗口不一致，忽略");
            return;
        }

        if (!CursorPosition.TryGet(out var cx, out var cy))
        {
            Log("  判定", "无法读取光标位置");
            return;
        }

        var cursor = new PixelPoint(cx, cy);
        Log("  光标", cursor.ToString());

        var excluded = new HashSet<nint> { dragging };
        var target = WindowEnumerator.TopLevelWindowAtExcluding(cursor, excluded);

        if (target == 0)
        {
            Log("  判定", "光标下没有其他窗口，未合并");
            return;
        }

        Log("  目标", Describe(target));

        var bounds = WindowEnumerator.ReadVisibleBounds(target);
        var dpi = MonitorLookup.DpiForWindow(target);
        var band = (int)Math.Round(48.0 * dpi / 96.0, MidpointRounding.AwayFromZero);
        var inBand = cursor.Y >= bounds.Top && cursor.Y < bounds.Top + band;

        Log("  拖放区", $"标题栏横带 Y∈[{bounds.Top}, {bounds.Top + band})  光标 Y={cursor.Y}  "
            + (inBand ? "命中" : "未命中 ← 需要拖到目标窗口顶部"));

        var context = new EligibilityContext { Environment = environment };

        foreach (var (label, handle) in new[] { ("被拖窗口", dragging), ("目标窗口", target) })
        {
            var info = enumerator.Describe(handle);
            if (info is null)
            {
                Log($"  {label}", "已消失");
                continue;
            }

            var result = EligibilityEvaluator.Evaluate(info, context);
            Log($"  {label}", result.IsEligible ? "可分组" : $"不可分组 —— {result.Explanation}");
        }
    }

    private static string Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "<null>";
        }

        var title = WindowEnumerator.ReadTitle(hwnd);
        var cls = WindowEnumerator.ReadClassName(hwnd);

        return $"0x{hwnd:X}  [{cls}]  {(string.IsNullOrEmpty(title) ? "（无标题）" : title)}";
    }

    private static void Log(string tag, string message) =>
        Console.WriteLine(
            string.Create(
                CultureInfo.CurrentCulture,
                $"{DateTime.Now:HH:mm:ss.fff}  {tag,-10} {message}"));
}
