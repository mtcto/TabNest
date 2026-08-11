using System.Diagnostics;
using System.Globalization;
using TabNest.Core.Grouping;
using TabNest.Core.Models;
using TabNest.Core.Rules;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 端到端自测：用真实窗口跑完"分组 → 切换 → 拆分 → 还原"闭环，并测量切换延迟。
///
/// 这是计划书 MVP 验收用例的可执行版本。手工拖拽无法自动化复现，
/// 而这个闭环恰恰是整个产品成立与否的判据，必须能反复验证。
///
/// 需要先启动 Harness：
///   TabNest.Harness.exe --spawn normal,normal
/// </summary>
internal static class SelfTestCommand
{
    private const int SwitchIterations = 30;

    public static int Run(RunMode mode)
    {
        _ = mode;

        var processes = new ProcessInspector();
        var enumerator = new WindowEnumerator(processes);
        var environment = SystemEnvironmentProbe.Probe(processes);
        using var controller = new WindowController();

        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion} 端到端自测");
        Console.WriteLine();

        var targets = FindTestWindows(enumerator, environment);
        if (targets.Count < 2)
        {
            Console.Error.WriteLine(
                "未找到至少两个可分组的 Harness 测试窗口。\n"
                + "请先运行：TabNest.Harness.exe --spawn normal,normal");
            return 1;
        }

        var a = targets[0];
        var b = targets[1];

        Console.WriteLine($"测试窗口 A：{a.Title}  {a.Bounds}");
        Console.WriteLine($"测试窗口 B：{b.Title}  {b.Bounds}");
        Console.WriteLine();

        var failures = 0;

        // ---- 1. 采集快照（必须先于任何窗口写操作） ----
        var snapshotA = WindowController.CaptureSnapshot(a.Identity.Handle);
        var snapshotB = WindowController.CaptureSnapshot(b.Identity.Handle);

        if (snapshotA is null || snapshotB is null)
        {
            Console.Error.WriteLine("采集窗口快照失败。");
            return 1;
        }

        var originalA = snapshotA.CurrentBounds;
        var originalB = snapshotB.CurrentBounds;

        // ---- 2. 建组 ----
        var manager = new GroupSessionManager();
        var create = manager.CreateGroup(
        [
            new TabCandidate(a, snapshotA),
            new TabCandidate(b, snapshotB),
        ]);

        failures += Check("建立标签组", create.Success, create.Message);
        if (!create.Success)
        {
            return 1;
        }

        var group = create.Group!;
        RunActions(controller, create.Actions);

        var boundsA = WindowEnumerator.ReadVisibleBounds(a.Identity.Handle);
        var boundsB = WindowEnumerator.ReadVisibleBounds(b.Identity.Handle);
        failures += Check(
            "两个成员对齐到同一矩形",
            Near(boundsA, boundsB, tolerance: 4),
            $"A={boundsA}  B={boundsB}");

        // ---- 3. 切换并测延迟 ----
        var latencies = new List<double>(SwitchIterations);
        var focusLevels = new Dictionary<ActivationLevel, int>();
        var focusHits = 0;

        for (var i = 0; i < SwitchIterations; i++)
        {
            var target = (i % 2 == 0 ? b : a).Identity;
            var result = manager.ActivateTab(group.Id, target);

            var sw = Stopwatch.StartNew();
            var opResults = controller.ExecuteAsync(result.Actions).GetAwaiter().GetResult();
            sw.Stop();

            latencies.Add(sw.Elapsed.TotalMilliseconds);

            foreach (var op in opResults)
            {
                if (op.ActivationLevel is not ActivationLevel.NotAttempted)
                {
                    focusLevels[op.ActivationLevel] = focusLevels.GetValueOrDefault(op.ActivationLevel) + 1;

                    if (op.ActivationLevel is not (ActivationLevel.Failed or ActivationLevel.RaisedOnly))
                    {
                        focusHits++;
                    }
                }
            }
        }

        latencies.Sort();
        var p95 = latencies[(int)(latencies.Count * 0.95) - 1];
        var median = latencies[latencies.Count / 2];
        var successRate = focusHits * 100.0 / SwitchIterations;

        Console.WriteLine();
        Write($"切换 {SwitchIterations} 次");
        Write($"  中位延迟   {median,7:N1} ms");
        Write($"  P95 延迟   {p95,7:N1} ms   目标 ≤ 100 ms");
        Write($"  焦点成功率 {successRate,7:N1} %    目标 ≥ 95 %");
        Console.WriteLine("  降级链分布：");

        foreach (var (level, count) in focusLevels.OrderBy(kv => kv.Key))
        {
            Write($"    {DescribeLevel(level),-28} {count,4} 次");
        }

        Console.WriteLine();
        failures += Check("切换 P95 ≤ 100ms", p95 <= 100, $"实测 {p95:N1} ms");

        // 硬性判据只有"没有彻底失败"。
        //
        // 焦点能否完全转移取决于 TabNest 当前是否持有前台权限，而自测进程从未收到过
        // 任何用户输入，按 Windows 的规则本就不该拥有前台权限 —— 这是最坏情况。
        // 真实交互路径不同：用户点击轨道时我们的进程收到了最后一次输入事件，
        // 按微软文档这本身就授予前台权限。
        //
        // 因此把"焦点完全转移"作为硬性门槛会得到一个永远失败、且失败得没有意义的测试。
        // 真正不可接受的是连置顶都做不到。
        var hardFailures = focusLevels.GetValueOrDefault(ActivationLevel.Failed);
        failures += Check("无彻底失败的激活", hardFailures == 0, $"{hardFailures} 次四级全败");

        Console.WriteLine();

        if (successRate < 95)
        {
            Write($"提示：焦点完全转移率 {successRate:N1}%（目标 ≥95%）。");
            Console.WriteLine(
                "      自测进程不持有前台权限，这是预期内的最坏情况，不计入失败。");
            Console.WriteLine(
                "      完整的焦点验证需要交互式测试：启动 TabNest 后手动点击标签切换。");
        }
        else
        {
            Write($"焦点完全转移率 {successRate:N1}%，达标。");
        }

        // ---- 4. 拆分并验证还原 ----
        var detach = manager.DetachTab(group.Id, a.Identity);
        failures += Check("拆分标签", detach.Success, detach.Message);
        RunActions(controller, detach.Actions);

        Thread.Sleep(150);

        var restoredA = WindowEnumerator.ReadVisibleBounds(a.Identity.Handle);
        var restoredB = WindowEnumerator.ReadVisibleBounds(b.Identity.Handle);

        failures += Check(
            "窗口 A 还原到分组前位置",
            Near(restoredA, originalA, tolerance: 8),
            $"原始 {originalA}  当前 {restoredA}");

        failures += Check(
            "窗口 B 还原到分组前位置（只剩一个成员时组自动解散）",
            Near(restoredB, originalB, tolerance: 8),
            $"原始 {originalB}  当前 {restoredB}");

        failures += Check("组已自动解散", manager.Groups.Count == 0, $"仍有 {manager.Groups.Count} 组");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过。" : $"{failures} 项未通过。");

        return failures == 0 ? 0 : 1;
    }

    private static List<WindowInfo> FindTestWindows(
        WindowEnumerator enumerator, SystemEnvironment environment)
    {
        var context = new EligibilityContext { Environment = environment };
        var result = new List<WindowInfo>();

        foreach (var window in enumerator.Enumerate())
        {
            // 只挑 Harness 生成的测试窗口，绝不碰用户正在用的真实窗口 ——
            // 自测会移动窗口，误伤真实窗口是不可接受的。
            if (!window.Title.StartsWith("Harness ", StringComparison.Ordinal))
            {
                continue;
            }

            if (EligibilityEvaluator.Evaluate(window, context).IsEligible)
            {
                result.Add(window);
            }
        }

        return result;
    }

    private static void RunActions(WindowController controller, IReadOnlyList<WindowAction> actions)
    {
        if (actions.Count > 0)
        {
            controller.ExecuteAsync(actions).GetAwaiter().GetResult();
        }
    }

    private static bool Near(PixelRect a, PixelRect b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance
        && Math.Abs(a.Top - b.Top) <= tolerance
        && Math.Abs(a.Width - b.Width) <= tolerance
        && Math.Abs(a.Height - b.Height) <= tolerance;

    private static int Check(string name, bool ok, string? detail)
    {
        Write($"[{(ok ? "通过" : "失败")}] {name}");

        if (!ok && !string.IsNullOrWhiteSpace(detail))
        {
            Write($"       {detail}");
        }

        return ok ? 0 : 1;
    }

    /// <summary>Console.WriteLine 没有接受 IFormatProvider 的插值重载，这里补一个。</summary>
    private static void Write(FormattableString text) =>
        Console.WriteLine(text.ToString(CultureInfo.CurrentCulture));

    private static string DescribeLevel(ActivationLevel level) => level switch
    {
        ActivationLevel.Direct => "1 直接 SetForegroundWindow",
        ActivationLevel.AttachedThreadInput => "2 附加前台线程后重试",
        ActivationLevel.SwitchToThisWindow => "3 SwitchToThisWindow 兜底",
        ActivationLevel.RaisedOnly => "4 仅置顶（焦点未转移）",
        ActivationLevel.Failed => "× 四级全部失败",
        _ => level.ToString(),
    };
}
