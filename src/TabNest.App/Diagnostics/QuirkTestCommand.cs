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

        // 诊断命令同样落日志：这些场景的失败往往发生在指令产生与执行之间，
        // 只看断言结果无法判断到底发出了什么指令。
        AppPaths.EnsureCreated();
        Core.Diagnostics.FileLog.Initialize(AppPaths.LogDirectory, Core.Diagnostics.LogLevel.Debug);

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
            Console.Error.WriteLine(
                "覆盖全部场景则用：\n"
                + "  TabNest.Harness.exe --spawn "
                + "normal,normal,normal,stubborn,fixed,hideonclose,hideonclose,child,pwa,pwa");
            return 1;
        }

        var failures = 0;
        using var dispatcher = new UiDispatcher();
        using var orchestrator = new Orchestrator(dispatcher);

        failures += TestMaximizedGroup(orchestrator, windows);
        Console.WriteLine();
        failures += TestTitledChildWindow(enumerator);
        Console.WriteLine();
        failures += TestFixedSizeMember(orchestrator, normal, fixedSize);
        Console.WriteLine();
        failures += TestHideOnCloseMember(orchestrator, hiders[0], hiders[1]);
        Console.WriteLine();
        failures += TestBatchAdoption(orchestrator, windows);
        Console.WriteLine();
        failures += TestDistinctAppIdAutoGroup(orchestrator, windows);

        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(500));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过。" : $"{failures} 项未通过。");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 最大化之后分组栏必须仍然可见。
    ///
    /// 最大化的窗口铺满整个工作区，而分组栏挂在窗口上方 —— 不做处理的话
    /// 它的纵坐标变成负数、跑到屏幕外，用户看到的就是"一全屏标签就没了"。
    ///
    /// **必须拿「拒绝重定位」窗口当活动成员来测。** 这条测试曾经用两个普通 WPF
    /// 窗口跑得全绿，而功能在真实应用上完全失效：普通窗口接受"保持最大化再让位"，
    /// Chromium 系应用会把矩形强行改回工作区，当场弹回。用听话的窗口测，
    /// 测的只是我们自己的假设。
    /// </summary>
    private static int TestMaximizedGroup(Orchestrator orchestrator, List<WindowInfo> harnessWindows)
    {
        Console.WriteLine("── 全屏后分组栏仍然可见 ──");

        var failures = 0;

        var stubborn = harnessWindows
            .Find(w => w.Title.Contains("拒绝重定位", StringComparison.Ordinal));

        var normals = harnessWindows
            .Where(w => w.Title.Contains("普通窗口", StringComparison.Ordinal))
            .ToList();

        if (stubborn is null || normals.Count < 1)
        {
            Console.WriteLine(
                "[跳过] 需要一个「拒绝重定位」窗口和一个「普通窗口」。请运行：\n"
                + "       TabNest.Harness.exe --spawn normal,stubborn");
            return failures;
        }

        // 让「拒绝重定位」成为被拖去合并的那个，它会成为活动成员，
        // 也就是下面真正被最大化、必须让出分组栏位置的窗口。
        orchestrator.MergeForTest(stubborn.Identity.Handle, normals[0].Identity.Handle);
        PumpMessages(TimeSpan.FromSeconds(2));

        var group = orchestrator.GroupsForTest.FirstOrDefault();

        failures += Check("已建组", group is not null, "没有建出组");

        if (group is null)
        {
            return failures;
        }

        // 把活动成员最大化，模拟用户双击标题栏。
        var active = group.ActiveTab?.Identity.Handle ?? 0;
        var beforeFullscreen = WindowEnumerator.ReadVisibleBounds(active);
        orchestrator.MaximizeForTest(active);
        PumpMessages(TimeSpan.FromSeconds(2));

        var maximized = orchestrator.GroupsForTest.FirstOrDefault();
        var rail = maximized is null ? null : orchestrator.GetRailForTest(maximized.Id);

        failures += Check(
            "最大化后组仍然存在",
            maximized is not null,
            "组在最大化后消失了");

        if (maximized is null || rail is null)
        {
            failures += Check("最大化后分组栏仍然存在", false, "分组栏不见了");
            orchestrator.DissolveEverything();
            PumpMessages(TimeSpan.FromMilliseconds(600));
            return failures;
        }

        var bounds = rail.ActualBounds;
        var workArea = MonitorLookup.ForWindow(active).WorkArea;

        Console.WriteLine($"工作区 {workArea}，分组栏 {bounds}");

        failures += Check(
            "分组栏没有被挤出屏幕上方",
            bounds.Top >= workArea.Top - 2,
            $"分组栏顶边 {bounds.Top}，工作区顶边 {workArea.Top} —— 已跑到屏幕外");

        failures += Check(
            "分组栏仍然可见",
            rail.IsVisible,
            "分组栏在最大化后被隐藏了");

        // 先确认"真的进入了全屏"，再谈让位。
        //
        // 少了这一条，功能彻底失效（窗口压根没变大）时下面的让位断言反而全过 ——
        // 一个没铺开的小窗口当然盖不住分组栏。测坏东西的前提是它先真的做了事。
        failures += Check(
            "整组确实铺满了屏幕",
            WindowEnumerator.ReadVisibleBounds(active).Width >= workArea.Width - 4,
            $"窗口 {WindowEnumerator.ReadVisibleBounds(active)}，工作区宽 {workArea.Width}");

        // 刻意**不**断言窗口仍处于最大化。
        //
        // 分组全屏走的是"假最大化"：还原成普通窗口再手动铺满让位后的矩形。
        // 真最大化在 Chromium 系应用上根本让不出位置（矩形被强行改回工作区），
        // 所以 IsZoomed 为 false 是这个设计的正常结果，不是缺陷。
        var windowBounds = WindowEnumerator.ReadVisibleBounds(active);

        // 同样以工作区顶边加分组栏高度为基准，不用分组栏底边 ——
        // 分组栏一旦被顶出屏幕，底边就是 0，拿它作基准这条断言恒真。
        failures += Check(
            "窗口为分组栏让出了顶部空间",
            windowBounds.Top >= workArea.Top + bounds.Height - 2,
            $"窗口顶边 {windowBounds.Top}，应不小于 {workArea.Top + bounds.Height} —— 分组栏被盖住了");

        // 全屏与否一律以**几何**判定，不看 IsZoomed。
        //
        // 假最大化下窗口在系统眼里始终是普通窗口，IsZoomed 恒为 false，
        // 拿它判断"是否全屏"会让两个方向的断言都失去意义。
        bool FillsScreen() =>
            WindowEnumerator.ReadVisibleBounds(active).Width >= workArea.Width - 4;

        // 双击分组栏退出全屏。分组栏就是这个组的标题栏，双击语义必须和
        // 双击普通窗口标题栏一致 —— 能全屏，也能再双击退出全屏。
        orchestrator.RailDoubleClickForTest(maximized.Id);
        PumpMessages(TimeSpan.FromSeconds(1));

        var exited = WindowEnumerator.ReadVisibleBounds(active);

        failures += Check(
            "双击分组栏可退出全屏",
            !FillsScreen(),
            $"窗口仍占满屏幕：{exited}");

        failures += Check(
            "退出全屏后回到进入之前的矩形",
            Math.Abs(exited.Width - beforeFullscreen.Width) <= 4
                && Math.Abs(exited.Height - beforeFullscreen.Height) <= 4,
            $"进入全屏前 {beforeFullscreen}，退出后 {exited} —— 没有回到原来的大小");

        // 再双击一次全屏回去，这次全程只经由分组栏，不碰窗口标题栏。
        orchestrator.RailDoubleClickForTest(maximized.Id);
        PumpMessages(TimeSpan.FromSeconds(1));

        failures += Check(
            "双击分组栏可进入全屏",
            FillsScreen(),
            $"窗口没有占满屏幕：{WindowEnumerator.ReadVisibleBounds(active)}");

        var toggledRail = orchestrator.GetRailForTest(maximized.Id);
        var toggledBounds = toggledRail?.ActualBounds ?? default;

        failures += Check(
            "双击全屏后分组栏仍然可见",
            toggledRail is { IsVisible: true } && toggledBounds.Top >= workArea.Top - 2,
            $"分组栏 {toggledBounds}，工作区顶边 {workArea.Top}");

        // 拿工作区顶边加分组栏高度当基准，而不是分组栏的底边。
        //
        // 用底边的话，分组栏一旦被顶出屏幕（bottom = 0），"窗口顶边 ≥ 0"就恒成立，
        // 这条断言会在功能坏掉时照样通过 —— 恰好是它最该报警的时候。
        var reservedTop = workArea.Top + toggledBounds.Height;

        failures += Check(
            "双击全屏后窗口仍为分组栏让位",
            WindowEnumerator.ReadVisibleBounds(active).Top >= reservedTop - 2,
            $"窗口顶边 {WindowEnumerator.ReadVisibleBounds(active).Top}，"
                + $"应不小于 {reservedTop} —— 窗口盖住了分组栏");

        // 让位必须落在**每个**成员上，不能只顾活动窗口。
        //
        // 组内窗口是叠在一起的，切一下标签，后面那个就浮上来 ——
        // 它若停在没让位的矩形上，用户切完标签就发现分组栏又没了。
        var settled = orchestrator.GroupsForTest.FirstOrDefault();
        var trespassers = settled is null
            ? []
            : settled.LiveTabs
                .Select(t => (t.Title, Bounds: WindowEnumerator.ReadVisibleBounds(t.Identity.Handle)))
                .Where(m => !m.Bounds.IsEmpty && m.Bounds.Top < reservedTop - 2)
                .ToList();

        failures += Check(
            "组内每个成员都为分组栏让了位",
            trespassers.Count == 0,
            "这些成员盖住了分组栏："
                + string.Join("、", trespassers.Select(m => $"{m.Title} 顶边 {m.Bounds.Top}")));

        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(800));

        return failures;
    }

    /// <summary>
    /// 同进程但不同应用的窗口，不得被自动分组认成同类。
    ///
    /// Chrome 的 PWA 窗口与普通浏览器窗口跑在同一个 chrome.exe 里、用同一个窗口类，
    /// 只比进程名的话完全无法区分。用户的现象是：组里只要有一个 PWA，
    /// 之后点开 Chrome 就会被自动吞进那个组，并伴随持续闪烁 ——
    /// 而在用户眼里 PWA 和浏览器是两个应用（任务栏上也是两个图标）。
    ///
    /// 判据用 AppUserModelID，这正是 Windows 自己归并任务栏图标的依据。
    /// </summary>
    private static int TestDistinctAppIdAutoGroup(
        Orchestrator orchestrator, List<WindowInfo> harnessWindows)
    {
        Console.WriteLine("── 同进程不同应用不得被自动分组 ──");

        var failures = 0;

        var fakePwas = harnessWindows
            .Where(w => w.Title.Contains("独立应用标识", StringComparison.Ordinal))
            .ToList();

        var normals = harnessWindows
            .Where(w => w.Title.Contains("普通窗口", StringComparison.Ordinal))
            .ToList();

        if (fakePwas.Count < 2 || normals.Count < 3)
        {
            Console.WriteLine(
                "[跳过] 需要两个「独立应用标识」窗口和三个「普通窗口」：\n"
                + "       TabNest.Harness.exe --spawn normal,normal,normal,pwa,pwa");
            return failures;
        }

        // 先确认判据本身在真实窗口上成立 —— 同进程、同窗口类，但应用标识不同。
        // 这一条挂了，后面两条断言的结论都不可信。
        var pwaId = AppUserModelId.Read(fakePwas[0].Identity.Handle);
        var normalId = AppUserModelId.Read(normals[0].Identity.Handle);

        Console.WriteLine($"PWA 标识 {pwaId ?? "(无)"}，普通窗口标识 {normalId ?? "(无)"}");

        failures += Check(
            "同进程的两个窗口拥有不同的应用标识",
            !string.IsNullOrEmpty(pwaId) && !string.Equals(pwaId, normalId, StringComparison.Ordinal),
            "读不到差异 —— 判据不成立，本节其余结论均不可信");

        // 负向：组里只有 PWA，新出现的普通窗口不该被吞进去。
        orchestrator.MergeForTest(fakePwas[1].Identity.Handle, fakePwas[0].Identity.Handle);
        PumpMessages(TimeSpan.FromSeconds(1));

        var pwaGroup = orchestrator.GroupsForTest.FirstOrDefault();

        failures += Check("已建出仅含 PWA 的组", pwaGroup is not null, "没有建出组");

        if (pwaGroup is not null)
        {
            orchestrator.AutoGroupForTest(normals[0].Identity.Handle);
            PumpMessages(TimeSpan.FromSeconds(1));

            var adopted = orchestrator.GroupsForTest
                .SelectMany(g => g.LiveTabs)
                .Any(t => t.Identity.Handle == normals[0].Identity.Handle);

            failures += Check(
                "普通窗口没有被 PWA 的组吞掉",
                !adopted,
                "普通窗口被自动并入了只含 PWA 的组 —— 用户点开浏览器就会被抓走");
        }

        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(800));

        // 正向对照，不可省。
        //
        // 少了这一条，"没被吞掉"可能只是因为自动分组压根没执行（开关没开、
        // 资格判定拦下、窗口找不到……），修复与否都是绿的。
        // 必须证明同一条路径在**该分组时确实会分组**。
        orchestrator.MergeForTest(normals[1].Identity.Handle, normals[0].Identity.Handle);
        PumpMessages(TimeSpan.FromSeconds(1));

        if (orchestrator.GroupsForTest.FirstOrDefault() is not null)
        {
            orchestrator.AutoGroupForTest(normals[2].Identity.Handle);
            PumpMessages(TimeSpan.FromSeconds(1));

            var adoptedSameApp = orchestrator.GroupsForTest
                .SelectMany(g => g.LiveTabs)
                .Any(t => t.Identity.Handle == normals[2].Identity.Handle);

            failures += Check(
                "真正同应用的窗口仍会被自动分组（正向对照）",
                adoptedSameApp,
                "同应用窗口也没被分组 —— 自动分组整个失效了，上一条断言不成立");
        }

        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(800));

        return failures;
    }

    /// <summary>
    /// 有标题的子窗口绝不能被当成可分组窗口。
    ///
    /// Chrome 的「Chrome Legacy Window」就是这个形态：渲染窗口的子窗口，
    /// 无障碍接口被触发时才创建，有标题、可见、非工具窗 ——
    /// 从"有没有标题""是不是工具窗"这些角度完全看不出它不该被分组。
    ///
    /// 它曾被自动分组收编进组里，用户莫名多出一个点不动的空白标签。
    /// 根因是顶层窗口的判定只写在枚举的预过滤里，而自动分组走窗口事件、
    /// 直接对任意 HWND 调 Describe，绕开了那道过滤。
    /// </summary>
    private static int TestTitledChildWindow(WindowEnumerator enumerator)
    {
        Console.WriteLine("── 有标题的子窗口不得被分组 ──");

        var failures = 0;

        // 枚举出的窗口里绝不该出现子窗口。
        var enumerated = enumerator.Enumerate().ToList();

        var childrenInList = enumerated
            .Where(w => !WindowEnumerator.IsTopLevel(w.Identity.Handle))
            .ToList();

        failures += Check(
            "候选列表里没有子窗口",
            childrenInList.Count == 0,
            $"混进了 {childrenInList.Count} 个子窗口："
            + string.Join("、", childrenInList.Select(w => w.Title)));

        // 找一个真实存在的子窗口，直接对它调 Describe —— 这正是自动分组走的那条路。
        var host = enumerated.FirstOrDefault(w =>
            w.Title.Contains("含子窗口", StringComparison.Ordinal));

        if (host is null)
        {
            Console.WriteLine(
                "[跳过] 没有「含子窗口」测试窗口。加上它可覆盖更严格的场景：\n"
                + "       TabNest.Harness.exe --spawn normal,normal,child");

            return failures;
        }

        var child = WindowEnumerator.FindChildWindow(host.Identity.Handle, "Chrome Legacy Window");

        failures += Check(
            "测试子窗口确实存在",
            child != 0,
            "没找到子窗口，本项无法验证");

        if (child == 0)
        {
            return failures;
        }

        failures += Check(
            "子窗口不被判定为顶层窗口",
            !WindowEnumerator.IsTopLevel(child),
            "子窗口被当成了顶层窗口");

        failures += Check(
            "Describe 拒绝描述子窗口",
            enumerator.Describe(child) is null,
            "Describe 仍然为子窗口返回了描述 —— 自动分组会把它收编进组里");

        // 绝不给子窗口造任务栏按钮。
        //
        // ITaskbarList::AddTab 对一个本来没有按钮的窗口不是无操作，而是**造一个**。
        // 用户曾在 Chrome 图标下多出一个 "Chrome Legacy Window" 条目，点进去什么都没有 ——
        // 它不属于任何分组，既关不掉也看不出来源。
        //
        // 这里绕过 Describe 直接对裸句柄下指令，模拟"上层某条路径把不该管的窗口传下来"
        // 以及"隐藏过的句柄被回收复用"这两种情况 —— 拦截必须发生在执行层，
        // 因为那是唯一的收口。
        using var controller = new WindowController();

        var refused = controller
            .ExecuteAsync(new Core.Grouping.SetTaskbarButtonAction(
                new WindowIdentity(child, 0, 0), Visible: true))
            .GetAwaiter().GetResult();

        failures += Check(
            "拒绝为子窗口创建任务栏按钮",
            refused.Count > 0 && !refused[0].Success,
            "执行层接受了为子窗口创建任务栏按钮 —— 用户会多出一个点不动的幽灵条目");

        // 正向对照：对一个正常顶层窗口的隐藏与还原必须真的成功。
        //
        // 不可省。上面那条断言在「任务栏接口整个不可用」时同样通过 ——
        // 两种情况都表现为"操作没发生"。而这恰恰真实发生过：ITaskbarList 曾用
        // [ComImport] 声明，裁剪把内置 COM 互操作关掉，于是任务栏按钮策略
        // 在**每一个发布版里**都不工作，Debug 下却一切正常、测试全绿。
        //
        // 这条断言必须在发布产物上跑才有意义：
        //   publish\v1\TabNest.exe --quirktest
        var hidden = controller
            .ExecuteAsync(new Core.Grouping.SetTaskbarButtonAction(host.Identity, Visible: false))
            .GetAwaiter().GetResult();

        var shown = controller
            .ExecuteAsync(new Core.Grouping.SetTaskbarButtonAction(host.Identity, Visible: true))
            .GetAwaiter().GetResult();

        failures += Check(
            "任务栏接口确实可用（正向对照）",
            hidden.Count > 0 && hidden[0].Success && shown.Count > 0 && shown[0].Success,
            "隐藏或还原任务栏按钮失败："
                + $"{(hidden.Count > 0 ? hidden[0].Message : "无结果")}"
                + $" / {(shown.Count > 0 ? shown[0].Message : "无结果")}");

        return failures;
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
                WindowEnumerator.IsAliveAndShown(t.Identity.Handle)),
            "组里还留着已经隐藏的窗口，分组栏会指向看不见的窗口");

        var rail = orchestrator.GetRailForTest(groupId);

        failures += Check(
            "组已清空时分组栏随之关闭",
            after is null || after.LiveTabCount > 0 || rail is null,
            "组里一个活着的成员都没有了，分组栏却还在");

        // 最关键的一条：两个窗口都必须保持关闭状态。
        //
        // 缺陷形态是第一个窗口缩到托盘后组降到一个成员而自动解散，
        // 解散又产出"还原剩下那个窗口"的指令，把一个应用已经关掉的窗口
        // 硬拽回屏幕 —— 它看得见却不响应任何输入，用户只能去托盘重开。
        // 只有两个应用都是"关闭即缩托盘"时才会撞上：有一个是真关闭的话，
        // 它的窗口已销毁，还原是空操作，问题就被掩盖了。
        var resurrected = new[] { hider, other }
            .Where(w => WindowEnumerator.IsAliveAndShown(w.Identity.Handle))
            .ToList();

        failures += Check(
            "关闭后没有窗口被还原操作复活",
            resurrected.Count == 0,
            $"{resurrected.Count} 个已关闭的窗口又被显示出来："
            + string.Join("、", resurrected.Select(w => w.Title))
            + " —— 它们在屏幕上但不响应任何输入");

        return failures;
    }

    /// <summary>
    /// 批量收编：把同一应用的全部未分组窗口一次成组。
    ///
    /// 这条路径无法用鼠标测试 —— 它的入口是托盘菜单，而菜单项内容取决于
    /// 当时的前台窗口。因此走编排层的公开入口断言结果。
    /// </summary>
    private static int TestBatchAdoption(Orchestrator orchestrator, List<WindowInfo> harnessWindows)
    {
        Console.WriteLine("── 批量收编 ──");

        var failures = 0;

        // Harness 的窗口同属一个进程，因此"同一应用"范围应当把它们全都选中。
        var expected = harnessWindows.Count;
        Console.WriteLine($"Harness 共有 {expected} 个窗口，同属一个进程");

        // 让其中一个成为前台窗口，收编以它为基准。
        var anchor = harnessWindows[0];
        orchestrator.ActivateForTest(anchor.Identity.Handle);
        PumpMessages(TimeSpan.FromMilliseconds(800));

        // 焦点转移在进程外不保证成功（前台锁定）。没转过去的话基准就不是我们想要的窗口，
        // 后面的断言全都失去意义 —— 与其得到一堆莫名其妙的失败，不如明确跳过。
        var actualForeground = WindowEnumerator.ForegroundWindow();

        if (actualForeground != anchor.Identity.Handle)
        {
            Console.WriteLine(
                $"[跳过] 未能把 Harness 窗口切到前台（当前前台是"
                + $"「{WindowEnumerator.ReadTitle(actualForeground)}」），"
                + "本节依赖前台窗口，无法继续。");

            return failures;
        }

        var scopes = orchestrator.DescribeForegroundAdoption();

        failures += Check(
            "能列出可收编的范围",
            scopes.Count > 0,
            "前台窗口没有可收编的同类窗口，或前台窗口不可识别");

        if (scopes.Count == 0)
        {
            return failures;
        }

        foreach (var (scope, text, _) in scopes)
        {
            Console.WriteLine($"  {scope}：{text}");
        }

        var adopted = orchestrator.AdoptForegroundWindows(scopes[0].Scope);
        PumpMessages(TimeSpan.FromSeconds(2));

        failures += Check(
            "一次收编成组",
            adopted >= 2,
            $"只收编了 {adopted} 个窗口");

        var group = orchestrator.GroupsForTest.FirstOrDefault();

        failures += Check(
            "组内成员数与菜单上报的数量一致",
            group is not null && group.LiveTabCount == adopted,
            $"菜单说会收编 {adopted} 个，实际组内 {group?.LiveTabCount} 个 —— "
            + "菜单数量与实际结果不符会让用户无法预期这次操作的规模");

        if (group is not null)
        {
            // 收编进来的窗口应当被对齐到组矩形，否则用户会看到分组栏建好了、
            // 窗口却散落在各处。
            //
            // 但**有自身尺寸约束的窗口够不到组矩形是正当的**：系统会把它夹回
            // 自己允许的尺寸，那是应用的决定而非我们的失败。判据因此是
            // "左上角对齐"而不是"整个矩形相等" —— 位置我们说了算，尺寸未必。
            var misplaced = group.LiveTabs
                .Select(t => (t, Bounds: WindowEnumerator.ReadVisibleBounds(t.Identity.Handle)))
                .Where(x => Math.Abs(x.Bounds.Left - group.Bounds.Left) > 8
                    || Math.Abs(x.Bounds.Top - group.Bounds.Top) > 8)
                .ToList();

            failures += Check(
                "收编的窗口都已移动到组的位置",
                misplaced.Count == 0,
                $"{misplaced.Count} 个成员位置不对："
                + string.Join("、", misplaced.Select(x => $"{x.t.Title} 在 {x.Bounds}")));

            var clamped = group.LiveTabs.Count(t =>
                !Near(WindowEnumerator.ReadVisibleBounds(t.Identity.Handle), group.Bounds, 8));

            if (clamped > 0)
            {
                Console.WriteLine(
                    $"       （{clamped} 个成员的尺寸被其自身约束夹住，属正常）");
            }
        }

        orchestrator.DissolveEverything();
        PumpMessages(TimeSpan.FromMilliseconds(600));

        return failures;
    }

    private static bool Near(Core.Models.PixelRect a, Core.Models.PixelRect b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance
        && Math.Abs(a.Top - b.Top) <= tolerance
        && Math.Abs(a.Right - b.Right) <= tolerance
        && Math.Abs(a.Bottom - b.Bottom) <= tolerance;

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
