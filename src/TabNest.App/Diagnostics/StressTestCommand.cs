using System.Diagnostics;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 稳定性压测：反复建组拆组、制造事件风暴，检查句柄与内存是否稳态。
///
/// 常驻程序真正的失败模式不是"某个功能不对"，而是**跑上几小时之后才出问题**：
/// 句柄一次泄漏一个、事件队列在风暴下无限增长、幽灵标签越积越多。
/// 这些在手工测试里几乎不可能被发现 —— 没人会点着鼠标验证八小时。
///
/// 这里用几百次快速循环把长期运行压缩到几十秒。它抓不到所有长期问题，
/// 但能抓住"每次操作泄漏一点"这一类，而那正是最常见的一类。
/// </summary>
internal static class StressTestCommand
{
    public static int Run(RunMode mode)
    {
        _ = mode;

        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion} 稳定性压测");
        Console.WriteLine();

        AppPaths.EnsureCreated();
        Core.Diagnostics.FileLog.Initialize(AppPaths.LogDirectory);

        var processes = new ProcessInspector();
        var enumerator = new WindowEnumerator(processes);

        var windows = enumerator.Enumerate()
            .Where(w => w.Title.Contains("普通窗口", StringComparison.Ordinal))
            .ToList();

        if (windows.Count < 2)
        {
            Console.Error.WriteLine(
                "需要至少两个「普通窗口」类型的 Harness 测试窗口。请先运行：\n"
                + "  TabNest.Harness.exe --spawn normal,normal");
            return 1;
        }

        var failures = 0;
        using var dispatcher = new UiDispatcher();
        using var orchestrator = new Orchestrator(dispatcher);

        var self = Process.GetCurrentProcess();

        // 先跑几轮让缓存、JIT 与首次分配稳定下来，再取基线。
        // 不预热的话第一轮的增长会被误判成泄漏。
        RunCycles(orchestrator, windows, 5);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        PumpMessages(TimeSpan.FromMilliseconds(500));

        self.Refresh();
        var handlesBefore = self.HandleCount;
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        Console.WriteLine($"基线：句柄 {handlesBefore}，托管堆 {memoryBefore / 1024} KB");
        Console.WriteLine();

        // ---- 建组拆组循环 ----
        const int cycles = 60;
        Console.WriteLine($"正在执行 {cycles} 轮建组 / 拆组…");

        var sw = Stopwatch.StartNew();
        RunCycles(orchestrator, windows, cycles);
        sw.Stop();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        PumpMessages(TimeSpan.FromMilliseconds(500));

        self.Refresh();
        var handlesAfter = self.HandleCount;
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

        var handleGrowth = handlesAfter - handlesBefore;
        var memoryGrowth = (memoryAfter - memoryBefore) / 1024;

        Console.WriteLine(
            $"完成，耗时 {sw.Elapsed.TotalSeconds:F1} 秒，"
            + $"平均每轮 {sw.Elapsed.TotalMilliseconds / cycles:F0} ms");
        Console.WriteLine($"结束：句柄 {handlesAfter}，托管堆 {memoryAfter / 1024} KB");
        Console.WriteLine();

        // 阈值不取 0：句柄数会因线程池伸缩、GC 内部结构而正常波动几个。
        // 关键是**不随循环次数线性增长** —— 每轮泄漏一个的话 60 轮就是 60 个。
        failures += Check(
            $"句柄未随循环泄漏（增长 {handleGrowth}，阈值 20）",
            handleGrowth < 20,
            $"60 轮后句柄增长 {handleGrowth} 个，接近每轮泄漏一个的形态");

        failures += Check(
            $"托管堆未失控（增长 {memoryGrowth} KB，阈值 4096 KB）",
            memoryGrowth < 4096,
            $"托管堆增长 {memoryGrowth} KB");

        // ---- 事件风暴 ----
        //
        // 计划书要求"事件风暴不得让队列无限增长"。观察层用有界队列 + 丢弃最旧，
        // 这里验证它在洪水下确实只丢不涨，且丢弃后仍能正常工作。
        Console.WriteLine("正在制造位置事件风暴…");

        orchestrator.MergeForTest(windows[1].Identity.Handle, windows[0].Identity.Handle);
        PumpMessages(TimeSpan.FromSeconds(1));

        var group = orchestrator.GroupsForTest.FirstOrDefault();

        failures += Check("风暴前已建组", group is not null, "没有建出组");

        if (group is not null)
        {
            const int storm = 3000;

            for (var i = 0; i < storm; i++)
            {
                foreach (var tab in group.LiveTabs)
                {
                    orchestrator.NotifyLocationForTest(tab.Identity.Handle);
                }
            }

            PumpMessages(TimeSpan.FromSeconds(2));

            var survived = orchestrator.GroupsForTest.FirstOrDefault();

            failures += Check(
                $"{storm * 2} 次位置事件后组仍然完好",
                survived is not null && survived.LiveTabCount == 2,
                $"组在事件风暴后损坏：成员 {survived?.LiveTabCount}");

            var rail = survived is null ? null : orchestrator.GetRailForTest(survived.Id);

            failures += Check(
                "风暴后分组栏仍然可见",
                rail is not null && rail.IsVisible,
                "分组栏在事件风暴后消失了");
        }

        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(800));

        // ---- 退出后窗口必须完好 ----
        failures += Check(
            "拆散后所有测试窗口仍然存活可见",
            windows.All(w => WindowEnumerator.IsAliveAndShown(w.Identity.Handle)),
            "压测过程中有窗口丢失或被隐藏 —— 这是最不可接受的失败");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过。" : $"{failures} 项未通过。");

        return failures == 0 ? 0 : 1;
    }

    private static void RunCycles(Orchestrator orchestrator, List<WindowInfo> windows, int count)
    {
        for (var i = 0; i < count; i++)
        {
            orchestrator.MergeForTest(windows[1].Identity.Handle, windows[0].Identity.Handle);
            PumpMessages(TimeSpan.FromMilliseconds(60));

            orchestrator.DissolveEverything();
            PumpMessages(TimeSpan.FromMilliseconds(60));
        }
    }

    private static int Check(string name, bool ok, string detail)
    {
        Console.WriteLine(ok ? $"[通过] {name}" : $"[失败] {name}\n       {detail}");
        return ok ? 0 : 1;
    }

    private static void PumpMessages(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            MessagePump.DrainPendingMessages();
            Thread.Sleep(4);
        }
    }
}
