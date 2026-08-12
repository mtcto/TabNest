using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 复现两类真实缺陷并断言它们不再发生。
///
/// 两个缺陷都是"普通窗口之间一切正常，遇到有脾气的窗口才暴露"，
/// 而 --grouptest 用的全是普通 Harness 窗口，因此全绿也挡不住它们：
///
///   1. **关闭即隐藏的窗口**（Electron 系应用的常规做法）关掉后不发销毁事件，
///      标签与分组栏永远留着，从标签拆分还会把已经关掉的窗口重新显示出来。
///   2. **有最大尺寸限制的窗口**被对齐到大矩形时会被系统夹回小尺寸，
///      它上报的这份回声若被当成用户意图，会把同组的大窗口一起拉小，
///      而且用户再怎么拉宽都会被拽回去。
///
/// 需要先启动 Harness：
///   TabNest.Harness.exe --spawn normal,fixed,hideonclose
/// </summary>
internal static class QuirkTestCommand
{
    public static int Run(RunMode mode)
    {
        _ = mode;

        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion} 疑难窗口回归测试");
        Console.WriteLine();

        var processes = new ProcessInspector();
        var enumerator = new WindowEnumerator(processes);

        var windows = enumerator.Enumerate()
            .Where(w => w.Title.StartsWith("Harness ", StringComparison.Ordinal))
            .ToList();

        var normal = windows.Find(w => w.Title.Contains("普通窗口", StringComparison.Ordinal));
        var fixedSize = windows.Find(w => w.Title.Contains("固定尺寸", StringComparison.Ordinal));

        // 需要**两个**关闭即隐藏的窗口，对应用户实际遇到的 Codex + Claude 场景。
        //
        // 只用一个是不够的：另一个普通窗口会真的被销毁，组随即掉到一个成员而自动解散，
        // 于是"组已清空"的断言被平凡满足 —— 无论有没有修复都是绿的。
        var hiders = windows
            .Where(w => w.Title.Contains("关闭即隐藏", StringComparison.Ordinal))
            .ToList();

        if (normal is null || fixedSize is null || hiders.Count < 2)
        {
            Console.Error.WriteLine(
                "需要四个特定的 Harness 窗口。请先运行：\n"
                + "  TabNest.Harness.exe --spawn normal,fixed,hideonclose,hideonclose");
            return 1;
        }

        var failures = 0;
        using var dispatcher = new UiDispatcher();
        using var orchestrator = new Orchestrator(dispatcher);

        failures += TestFixedSizeMember(orchestrator, normal, fixedSize);
        Console.WriteLine();
        failures += TestHideOnCloseMember(orchestrator, hiders[0], hiders[1]);

        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(500));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过。" : $"{failures} 项未通过。");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 缺陷一：固定尺寸的成员不得把整组拽小。
    /// </summary>
    private static int TestFixedSizeMember(
        Orchestrator orchestrator, WindowInfo big, WindowInfo small)
    {
        Console.WriteLine("── 固定尺寸成员不得驱动整组 ──");

        var failures = 0;
        var bigBefore = WindowEnumerator.ReadVisibleBounds(big.Identity.Handle);
        var smallBefore = WindowEnumerator.ReadVisibleBounds(small.Identity.Handle);

        Console.WriteLine($"大窗口 {bigBefore.Width}x{bigBefore.Height}，"
            + $"固定尺寸窗口 {smallBefore.Width}x{smallBefore.Height}");

        // 把固定尺寸窗口拖到大窗口上，组矩形以大窗口为准。
        orchestrator.MergeForTest(small.Identity.Handle, big.Identity.Handle);
        PumpMessages(TimeSpan.FromSeconds(2));

        var group = orchestrator.GroupsForTest.FirstOrDefault();

        failures += Check("已建组", group is not null, "没有建出组");
        if (group is null)
        {
            return failures;
        }

        // 基准取**合并前**大窗口的宽度，而不是合并后的组宽度：
        // 缺陷存在时组在合并瞬间就已经被拽小了，拿合并后的值当基准等于放过它。
        failures += Check(
            "合并后组宽度仍以大窗口为准",
            group.Bounds.Width > smallBefore.Width + 40,
            $"组宽度 {group.Bounds.Width}，大窗口原本 {bigBefore.Width}，"
            + $"固定尺寸窗口 {smallBefore.Width}");

        var groupWidthAfterMerge = group.Bounds.Width;

        // 关键一步：固定尺寸窗口被夹回小尺寸后会上报位置。
        // 反复投递这份回声，模拟真实的事件流。
        for (var i = 0; i < 5; i++)
        {
            orchestrator.NotifyLocationForTest(small.Identity.Handle);
            PumpMessages(TimeSpan.FromMilliseconds(120));
        }

        var groupAfterEcho = orchestrator.GroupsForTest.FirstOrDefault();

        failures += Check(
            "固定尺寸窗口的回声没有改变组宽度",
            groupAfterEcho is not null && groupAfterEcho.Bounds.Width == groupWidthAfterMerge,
            $"组宽度从 {groupWidthAfterMerge} 变成了 {groupAfterEcho?.Bounds.Width} —— "
            + "被夹回尺寸的成员把整组拽小了");

        var bigAfter = WindowEnumerator.ReadVisibleBounds(big.Identity.Handle);

        failures += Check(
            "大窗口没有被拽成固定尺寸窗口的宽度",
            bigAfter.Width > smallBefore.Width + 40,
            $"大窗口宽度变成 {bigAfter.Width}，固定尺寸窗口是 {smallBefore.Width}");

        // 分组栏必须跟上真实窗口宽度，而不是停在合并前的旧尺寸。
        var rail = orchestrator.GetRailForTest(group.Id);

        if (rail is not null)
        {
            var railBounds = rail.ActualBounds;
            var activeBounds = WindowEnumerator.ReadVisibleBounds(
                group.ActiveTab?.Identity.Handle ?? 0);

            failures += Check(
                "分组栏宽度与活动窗口一致（无需点击即自动收敛）",
                Math.Abs(railBounds.Width - activeBounds.Width) <= 6,
                $"分组栏宽 {railBounds.Width}，活动窗口宽 {activeBounds.Width}");
        }

        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(600));

        return failures;
    }

    /// <summary>
    /// 缺陷二：关闭即隐藏的成员必须被移出组，组空了分组栏必须消失。
    /// </summary>
    private static int TestHideOnCloseMember(
        Orchestrator orchestrator, WindowInfo hider, WindowInfo other)
    {
        Console.WriteLine("── 关闭即隐藏的成员必须被移出分组 ──");

        var failures = 0;

        orchestrator.MergeForTest(hider.Identity.Handle, other.Identity.Handle);
        PumpMessages(TimeSpan.FromSeconds(2));

        var group = orchestrator.GroupsForTest.FirstOrDefault();

        failures += Check("已建组", group is not null, "没有建出组");
        if (group is null)
        {
            return failures;
        }

        var groupId = group.Id;

        failures += Check("组内有两个成员", group.LiveTabCount == 2, $"实际 {group.LiveTabCount}");

        // 走生产路径点「关闭整组」。
        orchestrator.CloseGroupForTest(groupId);

        // 只抽消息、**不手动触发核验**。
        //
        // 早期这里显式调了一次 SweepForTest，结果无论有没有修复都是绿的 ——
        // 测试亲手执行了它本该验证的那个机制。清理必须完全由生产路径完成：
        // 隐藏事件，或关闭之后自动排程的兜底核验。
        PumpMessages(TimeSpan.FromSeconds(4));

        var after = orchestrator.GroupsForTest.FirstOrDefault(g => g.Id == groupId);

        failures += Check(
            "关闭后组内不再有隐藏的成员",
            after is null || after.LiveTabs.All(t =>
                WindowEnumerator.IsAliveAndVisible(t.Identity.Handle)),
            "组里还留着已经隐藏的窗口，分组栏会指向看不见的窗口");

        var rail = orchestrator.GetRailForTest(groupId);

        failures += Check(
            "组已清空时分组栏随之关闭",
            after is null || after.LiveTabCount > 0 || rail is null,
            "组里一个活着的成员都没有了，分组栏却还在");

        return failures;
    }

    private static int Check(string name, bool ok, string? detail)
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
            Thread.Sleep(16);
        }
    }
}
