using System.Globalization;
using TabNest.App.Rail;
using TabNest.Core.Layout;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 验证标签轨道在真实窗口上确实**可见且位于成员窗口之上**。
///
/// 这一段无法用单元测试覆盖：布局算得再对，只要轨道在 Z 序里排到了成员窗口后面，
/// 用户看到的就是"分组成功了但没有分组栏"。而这恰恰发生过 ——
/// 去掉 WS_EX_TOPMOST 改用属主关系后，跨进程属主设置静默失效，
/// 轨道就一直待在窗口背后。
///
/// 需要先启动 Harness：TabNest.Harness.exe --spawn normal
/// </summary>
internal static class RailTestCommand
{
    public static int Run(RunMode mode)
    {
        _ = mode;

        var processes = new ProcessInspector();
        var enumerator = new WindowEnumerator(processes);

        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion} 标签轨道可见性自测");
        Console.WriteLine();

        var anchor = FindHarnessWindow(enumerator);
        if (anchor is null)
        {
            Console.Error.WriteLine(
                "未找到 Harness 测试窗口。请先运行：TabNest.Harness.exe --spawn normal");
            return 1;
        }

        Write($"承载窗口：{anchor.Title}  {anchor.Bounds}");
        Console.WriteLine();

        var failures = 0;
        var dispatcher = new UiDispatcher();
        TabRailWindow? rail = null;

        try
        {
            rail = new TabRailWindow("railtest", _ => { });

            var dpi = MonitorLookup.DpiForWindow(anchor.Identity.Handle);
            var metrics = new RailMetrics().ScaleTo(dpi) with
            {
                TopCornerRadius = WindowEnumerator.TopCornerRadius(anchor.Identity.Handle, dpi),
            };

            // 与实际分组一致：从窗口顶部预留轨道高度，而不是往上方硬挤。
            var content = new PixelRect(
                anchor.Bounds.Left,
                anchor.Bounds.Top + metrics.Height,
                anchor.Bounds.Right,
                anchor.Bounds.Bottom);

            // 真正把窗口挪到内容区。
            //
            // 早期这里只造分组栏而不动窗口，于是窗口仍然压在分组栏所在的位置上，
            // 测试场景与实际分组完全不符 —— 这种"看起来在测、其实测的是别的东西"
            // 的测试比没有测试更有害。
            using (var controller = new WindowController())
            {
                controller
                    .ExecuteAsync(new Core.Grouping.AlignWindowAction(anchor.Identity, content))
                    .GetAwaiter()
                    .GetResult();
            }

            Thread.Sleep(200);

            var tabs = new List<TabItem>
            {
                MakeTab(anchor, active: true),
                MakeTab(anchor with { Title = "第二个标签" }, active: false),
            };

            var layout = TabRailLayoutEngine.Compute(
                tabs, tabs[0].Identity, content, metrics);

            rail.Update(
                new RailRenderState
                {
                    Layout = layout,
                    Tabs = tabs,
                    ActiveIdentity = tabs[0].Identity,
                    Theme = RailTheme.Default,
                    Dpi = dpi,
                },
                anchor.Identity.Handle);

            // 给窗口管理器一点时间完成定位与重绘。
            Thread.Sleep(300);

            Write($"轨道期望矩形：{layout.Bounds}");
            Write($"轨道实际矩形：{rail.ActualBounds}");
            Write($"圆角半径：{metrics.TopCornerRadius}（0 表示承载窗口是直角）");
            Console.WriteLine();

            failures += Check("轨道窗口可见", rail.IsVisible, null);

            failures += Check(
                "轨道位置与布局一致",
                rail.ActualBounds == layout.Bounds,
                $"期望 {layout.Bounds}，实际 {rail.ActualBounds}");

            failures += Check(
                "轨道在屏幕范围内",
                rail.ActualBounds.Top >= 0,
                $"轨道顶边 Y={rail.ActualBounds.Top}，跑到屏幕外就永远看不见");

            var railZ = ZOrderIndex(rail.Handle);
            var anchorZ = ZOrderIndex(anchor.Identity.Handle);

            // 分组栏刻意排在承载窗口**之下**：它向下多延伸的一段因此被窗口挡住，
            // 而窗口圆角的缺口处透出分组栏的颜色 —— 接缝消失且不遮挡窗口内容。
            failures += Check(
                "轨道紧邻承载窗口之下",
                railZ >= 0 && anchorZ >= 0 && railZ == anchorZ + 1,
                $"轨道 Z 序位置 {railZ}，承载窗口 {anchorZ}（应为承载窗口 + 1）");

            // 标签区域位于窗口顶边之上，必须没有被任何东西盖住 ——
            // 排在窗口之下不等于可以被遮挡，这两件事必须分别验证。
            var tabAreaCenter = new PixelPoint(
                rail.ActualBounds.Left + (rail.ActualBounds.Width / 2),
                rail.ActualBounds.Top + (metrics.Height / 2));

            var hit = WindowEnumerator.TopLevelWindowAt(tabAreaCenter);

            failures += Check(
                "标签区域可被命中（未被遮挡）",
                hit == rail.Handle,
                $"命中的是 0x{hit:X}，期望轨道 0x{rail.Handle:X}");
        }
        finally
        {
            rail?.Dispose();
            dispatcher.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过。" : $"{failures} 项未通过。");

        return failures == 0 ? 0 : 1;
    }

    private static TabItem MakeTab(WindowInfo window, bool active) => new()
    {
        Identity = window.Identity,
        Title = window.Title,
        ProcessName = window.ProcessName,
        Snapshot = new WindowSnapshot
        {
            RestoreBounds = window.Bounds,
            CurrentBounds = window.Bounds,
            ShowState = WindowShowState.Normal,
            IsTopmost = false,
            MonitorDeviceName = window.MonitorDeviceName,
            Dpi = window.Dpi,
            WasInTaskbar = true,
            CapturedAt = DateTimeOffset.UtcNow,
        },
        State = active ? TabState.Active : TabState.Inactive,
        JoinedAt = DateTimeOffset.UtcNow,
    };

    private static WindowInfo? FindHarnessWindow(WindowEnumerator enumerator)
    {
        foreach (var window in enumerator.Enumerate())
        {
            if (window.Title.StartsWith("Harness ", StringComparison.Ordinal))
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>窗口在 Z 序中的位置，0 为最前。找不到返回 -1。</summary>
    private static int ZOrderIndex(nint hwnd)
    {
        var index = 0;

        foreach (var candidate in EnumerateZOrder())
        {
            if (candidate == hwnd)
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static IEnumerable<nint> EnumerateZOrder()
    {
        // 借用枚举器的可见窗口遍历；它本身就是按 Z 序自顶向下的。
        var probe = new PixelPoint(int.MinValue / 2, int.MinValue / 2);
        _ = probe;

        return WindowEnumerator.EnumerateAllTopLevel();
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
