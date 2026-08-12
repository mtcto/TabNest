using System.Globalization;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 走**生产代码路径**完整验证一次分组：编排层 → 建组 → 对齐 → 显示轨道。
///
/// <c>--selftest</c> 直接调领域层建组，绕过了编排层；<c>--railtest</c> 只单独造一个轨道。
/// 两者都全绿的情况下，用户看到的仍然是"分组成功但没有分组栏" ——
/// 说明问题就藏在这两个测试之间的那段编排代码里，必须有一个测试覆盖它。
///
/// 需要先启动 Harness：TabNest.Harness.exe --spawn normal,normal
/// </summary>
internal static class GroupTestCommand
{
    public static int Run(RunMode mode)
    {
        _ = mode;

        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion} 分组全链路自测");
        Console.WriteLine();

        var processes = new ProcessInspector();
        var enumerator = new WindowEnumerator(processes);

        var windows = new List<WindowInfo>();
        foreach (var w in enumerator.Enumerate())
        {
            if (w.Title.StartsWith("Harness ", StringComparison.Ordinal))
            {
                windows.Add(w);
            }
        }

        // 明确挑「普通窗口」。
        //
        // 早期是直接取前两个 Harness 窗口，结果 Harness 上开着固定尺寸窗口时
        // 本测试会误报"成员没有对齐到组矩形" —— 那个窗口够不到组矩形是它自己的
        // 尺寸约束决定的，属正当行为。本测试要验证的是基准路径，
        // 有脾气的窗口交给 --quirktest。
        var normals = windows
            .Where(w => w.Title.Contains("普通窗口", StringComparison.Ordinal))
            .ToList();

        if (normals.Count < 2)
        {
            Console.Error.WriteLine(
                "需要至少两个「普通窗口」类型的 Harness 测试窗口。请先运行：\n"
                + "  TabNest.Harness.exe --spawn normal,normal");
            return 1;
        }

        var target = normals[0];
        var dragged = normals[1];

        Write($"拖放目标：{target.Title}  {target.Bounds}");
        Write($"被拖窗口：{dragged.Title}  {dragged.Bounds}");
        Console.WriteLine();

        var failures = 0;
        using var dispatcher = new UiDispatcher();
        using var orchestrator = new Orchestrator(dispatcher);

        // 走完整生产路径。
        orchestrator.MergeForTest(dragged.Identity.Handle, target.Identity.Handle);

        // 编排层内部有异步的窗口控制队列，给它时间执行完。
        PumpMessages(TimeSpan.FromSeconds(2));

        var group = orchestrator.GroupsForTest.FirstOrDefault();

        failures += Check("已创建标签组", group is not null, "编排层没有建出组");
        if (group is null)
        {
            Report(failures);
            return 1;
        }

        Write($"组 {group.Id}，成员 {group.LiveTabCount} 个，组矩形 {group.Bounds}");

        failures += Check("组内有两个成员", group.LiveTabCount == 2, $"实际 {group.LiveTabCount}");

        var rail = orchestrator.GetRailForTest(group.Id);
        failures += Check("轨道窗口已创建", rail is not null, "编排层没有创建轨道");

        if (rail is null)
        {
            Report(failures);
            return 1;
        }

        Write($"轨道实际矩形 {rail.ActualBounds}，可见 {rail.IsVisible}");
        Console.WriteLine();

        failures += Check("轨道可见", rail.IsVisible, null);

        failures += Check(
            "轨道在屏幕内",
            rail.ActualBounds.Top >= 0 && !rail.ActualBounds.IsEmpty,
            $"轨道矩形 {rail.ActualBounds}");

        // 分组栏底边必须**严格等于**窗口顶边：差一像素露缝，多一像素就遮住窗口内容。
        failures += Check(
            "分组栏底边与窗口顶边严格贴合",
            rail.ActualBounds.Bottom == group.Bounds.Top,
            $"轨道底边 {rail.ActualBounds.Bottom}，窗口顶边 {group.Bounds.Top}，"
            + $"相差 {rail.ActualBounds.Bottom - group.Bounds.Top} 像素");

        failures += Check(
            "分组栏与窗口同宽",
            rail.ActualBounds.Left == group.Bounds.Left
                && rail.ActualBounds.Right == group.Bounds.Right,
            $"轨道 [{rail.ActualBounds.Left},{rail.ActualBounds.Right}]，"
            + $"组 [{group.Bounds.Left},{group.Bounds.Right}]");

        // 最关键：轨道中心必须能被命中。命不中就说明被成员窗口盖住了，
        // 用户看到的就是"分组成功但没有分组栏"。
        var center = new PixelPoint(
            rail.ActualBounds.Left + (rail.ActualBounds.Width / 2),
            rail.ActualBounds.Top + (rail.ActualBounds.Height / 2));

        var hit = WindowEnumerator.TopLevelWindowAt(center);

        failures += Check(
            "轨道中心可被命中（未被成员窗口遮挡）",
            hit == rail.Handle,
            $"命中 0x{hit:X}「{WindowEnumerator.ReadTitle(hit)}」，期望轨道 0x{rail.Handle:X}");

        // 成员窗口应当都被对齐到组矩形。
        foreach (var tab in group.LiveTabs)
        {
            var actual = WindowEnumerator.ReadVisibleBounds(tab.Identity.Handle);

            failures += Check(
                $"成员「{tab.DisplayTitle}」已对齐到组矩形",
                Near(actual, group.Bounds, 6),
                $"期望 {group.Bounds}，实际 {actual}");
        }

        // 清理：还原窗口，别把测试残留留给用户。
        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(500));

        Report(failures);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>抽干消息队列，让编排层投递到 UI 线程的工作真正执行。</summary>
    private static void PumpMessages(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            MessagePump.DrainPendingMessages();
            Thread.Sleep(15);
        }
    }

    private static bool Near(PixelRect a, PixelRect b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance
        && Math.Abs(a.Top - b.Top) <= tolerance
        && Math.Abs(a.Width - b.Width) <= tolerance
        && Math.Abs(a.Height - b.Height) <= tolerance;

    private static void Report(int failures)
    {
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过。" : $"{failures} 项未通过。");
    }

    private static int Check(string name, bool ok, string? detail)
    {
        Write($"[{(ok ? "通过" : "失败")}] {name}");

        if (!ok && !string.IsNullOrWhiteSpace(detail))
        {
            Write($"       {detail}");
        }

        return ok ? 0 : 1;
    }

    private static void Write(FormattableString text) =>
        Console.WriteLine(text.ToString(CultureInfo.CurrentCulture));
}
