using System.Diagnostics;
using TabNest.App.Rail;
using TabNest.Core.Diagnostics;
using TabNest.Core.Grouping;
using TabNest.Core.Layout;
using TabNest.Core.Models;
using TabNest.Core.Persistence;
using TabNest.Core.Rules;
using TabNest.Interop;

namespace TabNest.App;

/// <summary>
/// 把窗口观察、领域状态机和窗口控制串起来。
///
/// 线程约定（计划书 §7.3）：
///   - 观察线程只投递事件，不碰领域状态；
///   - 领域状态机只在 UI 线程上被修改，因此无需加锁；
///   - 所有窗口写操作交给 WindowController 的串行队列；
///   - UI 只消费不可变快照。
/// </summary>
internal sealed class Orchestrator : IDisposable
{
    private readonly UiDispatcher _dispatcher;
    private readonly ProcessInspector _processes = new();
    private readonly WindowEnumerator _enumerator;
    private readonly WindowController _controller = new();
    private readonly WinEventObserver _observer = new();
    private readonly GroupSessionManager _manager;
    private readonly AtomicJsonStore<AppSettings> _settingsStore;
    private readonly AtomicJsonStore<SessionSnapshot> _sessionStore;
    private readonly Dictionary<string, TabRailWindow> _rails = [];
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>被拖拽中的窗口。MOVESIZESTART 记录，MOVESIZEEND 消费。</summary>
    private nint _draggingWindow;

    /// <summary>拖拽过程中已确定并向用户展示过的候选目标。松手时直接用它，不再重新判定。</summary>
    private nint _dropCandidate;

    /// <summary>拖放提示条。首次拖拽时才创建，不拖拽就不占资源。</summary>
    private DropHintWindow? _dropHint;

    private AppSettings _settings;
    private SystemEnvironment _environment;
    private bool _disposed;

    public Orchestrator(UiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _enumerator = new WindowEnumerator(_processes);

        AppPaths.EnsureCreated();
        _settingsStore = new AtomicJsonStore<AppSettings>(AppPaths.SettingsFile, AppSettings.Default);
        _sessionStore = new AtomicJsonStore<SessionSnapshot>(AppPaths.SessionFile, SessionSnapshot.Empty);

        var load = _settingsStore.Load();
        _settings = load.Value;

        if (load.Outcome is LoadOutcome.FellBackToDefaults)
        {
            Debug.WriteLine($"[TabNest] 配置损坏已回退默认值：{load.Error}");
        }

        _manager = new GroupSessionManager(maxClosedHistory: _settings.ClosedTabHistoryLimit)
        {
            TaskbarPolicy = _settings.TaskbarButtons,
        };

        _environment = SystemEnvironmentProbe.Probe(_processes);

        // 先还原上次残留的窗口，再开始观察 —— 顺序反了会让还原动作自己触发一堆事件。
        RecoverPreviousSession();

        _ = Task.Run(ConsumeEventsAsync);
    }

    public AppSettings Settings => _settings;

    public bool IsEnabled => _settings.Enabled;

    public int GroupCount => _manager.Groups.Count;

    // ------------------------------------------------------------------
    // 事件消费
    // ------------------------------------------------------------------

    private async Task ConsumeEventsAsync()
    {
        try
        {
            await foreach (var evt in _observer.Events.ReadAllAsync(_shutdown.Token))
            {
                // 领域状态只在 UI 线程上改，因此整条处理都投递过去。
                _dispatcher.Post(() => HandleEvent(evt));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
    }

    private void HandleEvent(WindowEvent evt)
    {
        if (_disposed)
        {
            return;
        }

        // 事件从产生到被 UI 线程处理的延迟。
        //
        // 这是区分"我们处理慢"和"事件本身晚到"的唯一手段：
        // 若处理很快但延迟很高，瓶颈在 WinEvent 投递链路或 UI 线程被别的事占住，
        // 继续优化处理代码不会有任何效果。
        if (evt.Kind is WindowEventKind.LocationChanged)
        {
            var latencyMs = (Stopwatch.GetTimestamp() - evt.TimestampTicks)
                * 1000.0 / Stopwatch.Frequency;

            if (latencyMs > SlowFrameThresholdMs)
            {
                FileLog.Warn(
                    $"位置事件延迟 {latencyMs:F0}ms 才到达 UI 线程"
                    + $"（观察层丢弃 {_observer.DroppedEvents} 条）");
            }
        }

        switch (evt.Kind)
        {
            case WindowEventKind.Destroyed:
                OnWindowDestroyed(evt.Handle);
                break;

            // 隐藏要当作"窗口从组里消失"处理。
            //
            // 早期只处理了 Destroyed，结果 Electron 类应用（Claude、Codex、VS Code）
            // 关闭窗口时只是隐藏而非销毁，永远等不到销毁事件：
            // 窗口不见了，分组栏和标签却一直留着，再从标签拆分还会把一个
            // 已经"关掉"的窗口重新显示出来，而它已不再响应任何输入。
            //
            // **刻意不处理 Cloaked**：cloak 的主要来源是窗口位于另一个虚拟桌面，
            // 把它当作消失会让用户切个桌面回来就发现分组没了。
            case WindowEventKind.Hidden:
                OnWindowVanished(evt.Handle);
                break;

            case WindowEventKind.NameChanged:
                OnTitleChanged(evt.Handle);
                break;

            case WindowEventKind.LocationChanged:
                OnLocationChanged(evt.Handle);
                break;

            case WindowEventKind.MoveSizeStart:
                OnDragStart(evt.Handle);
                break;

            case WindowEventKind.MoveSizeEnd:
                OnDragEnd(evt.Handle);
                break;

            case WindowEventKind.Foreground:
                OnForegroundChanged(evt.Handle);
                break;

            default:
                break;
        }
    }

    private void OnWindowDestroyed(nint hwnd)
    {
        // 被拖窗口在拖动中途消失（进程退出/崩溃）就不会再有拖拽结束事件，
        // 必须立刻落位，否则停靠的成员窗口滞留屏外。
        if (_liftedGroupId is { } liftedId && hwnd == _liftAuthority)
        {
            SettleGroup(liftedId, 0);
        }

        var group = FindGroupByHandle(hwnd);
        if (group is null)
        {
            return;
        }

        var tab = FindTabByHandle(group, hwnd);
        if (tab is null)
        {
            return;
        }

        // 幽灵标签的来源就是这里漏处理：窗口没了但标签还在。
        var result = _manager.OnWindowDestroyed(tab.Identity);
        Apply(result, group.Id);
    }

    /// <summary>
    /// 组成员被隐藏或 cloak。
    ///
    /// 与销毁不同，隐藏是可逆的：最小化到托盘、切换虚拟桌面、应用自己临时藏一下
    /// 都会走到这里。因此**不能一见隐藏就退组** —— 那会让用户切个虚拟桌面回来发现组没了。
    ///
    /// 判据是"窗口是否还是一个可见的顶层窗口"：真正被关掉的窗口回不到可见状态，
    /// 而最小化的窗口 IsWindowVisible 仍为 true（最小化不改变可见性标志）。
    /// </summary>
    private void OnWindowVanished(nint hwnd)
    {
        var group = FindGroupByHandle(hwnd);
        if (group is null)
        {
            return;
        }

        var tab = FindTabByHandle(group, hwnd);
        if (tab is null)
        {
            return;
        }

        if (WindowEnumerator.IsAliveAndShown(hwnd))
        {
            return;
        }

        FileLog.Info($"成员窗口已消失（隐藏或销毁），移出分组：{DescribeWindow(hwnd)}");
        Apply(_manager.OnWindowDestroyed(tab.Identity), group.Id);
    }

    /// <summary>
    /// 核验一个组里的成员是否都还在，把已经消失的清理掉。
    ///
    /// 关闭请求发出后不一定有事件回来：应用可能直接隐藏窗口而不销毁，
    /// 也可能整个进程被强杀导致事件丢失。没有这道兜底，分组栏会留在屏幕上
    /// 指向一批已经不存在的窗口。
    /// </summary>
    private void SweepVanishedMembers(string groupId)
    {
        var group = _manager.FindGroup(groupId);
        if (group is null)
        {
            return;
        }

        foreach (var tab in group.LiveTabs.ToList())
        {
            if (WindowEnumerator.IsAliveAndShown(tab.Identity.Handle))
            {
                continue;
            }

            FileLog.Info($"清理已消失的成员：{tab.Title}");

            var result = _manager.OnWindowDestroyed(tab.Identity);
            Apply(result, groupId);

            if (result.Group is null)
            {
                return;
            }
        }
    }

    private void OnTitleChanged(nint hwnd)
    {
        var group = FindGroupByHandle(hwnd);
        var tab = group is null ? null : FindTabByHandle(group, hwnd);
        if (group is null || tab is null)
        {
            return;
        }

        var title = WindowEnumerator.ReadTitle(hwnd);
        if (title == tab.Title)
        {
            return;
        }

        Apply(_manager.OnWindowTitleChanged(tab.Identity, title), group.Id);
    }

    /// <summary>
    /// 窗口位置或尺寸变化。
    ///
    /// 组成员的任何移动或缩放都会带动整个组：其余成员跟随对齐，分组条同步位置与宽度。
    /// 这是"分组后是一个整体"的核心 —— 拖任意一个成员就等于拖整组，
    /// 改任意一个成员的宽度，分组条宽度也跟着变。
    /// </summary>
    private void OnLocationChanged(nint hwnd)
    {
        var group = FindGroupByHandle(hwnd);

        if (group is not null)
        {
            SyncGroupToMember(group, hwnd);
            return;
        }

        // 不是组成员，那它就是正在被拖来分组的窗口 ——
        // 用它的位置变化当脉冲更新候选目标，省掉一个专门的轮询定时器。
        if (hwnd == _draggingWindow)
        {
            UpdateDropCandidate();
        }
    }

    /// <summary>
    /// 以某个成员窗口的当前矩形为准，同步整个组。
    ///
    /// 让被操作的那个窗口当权威、其余跟随，一次就同时解决了移动和缩放：
    /// 用户拖它，整组跟着走；用户拉宽它，整组和分组条一起变宽。
    /// </summary>
    private void SyncGroupToMember(GroupSession group, nint memberHandle)
    {
        // 组处于提起状态时，只有权威成员的位置可信。
        // 其他成员的位置事件是停靠动作自己的回声（坐标在屏外），
        // 拿它们更新组矩形会让分组栏跟着飞出屏幕。
        if (_liftedGroupId is not null && group.Id == _liftedGroupId)
        {
            if (memberHandle != _liftAuthority)
            {
                return;
            }

            var lifted = WindowEnumerator.ReadVisibleBounds(memberHandle);
            if (lifted.IsEmpty || Near(lifted, group.Bounds, tolerance: 2))
            {
                return;
            }

            // 只更新组矩形并让分组栏跟随（自己进程的窗口，成本可忽略）。
            // 不发任何对齐指令 —— 拖动期间零跨进程调用正是提起状态存在的意义。
            if (_manager.SetGroupBounds(group.Id, lifted).Group is { } liftedGroup)
            {
                RefreshRail(liftedGroup);
            }

            return;
        }

        // 只有用户正在操作的那个窗口才有资格改变整组的位置与大小。
        //
        // 早期是"任何成员的位置变化都同步整组"，这在成员窗口有自身尺寸约束时
        // 会形成致命回环：把一个有最大宽度限制的小窗口对齐到大矩形，系统会把它
        // 夹回小尺寸，它随即上报这个被夹过的矩形，我们又拿它当成"用户改了大小"
        // 去同步整组 —— 于是大窗口被拉成小窗口。用户再怎么拉宽大窗口，
        // 松手后都会被小窗口的回声拽回去，表现为"这个窗口的宽度改不了了"。
        //
        // 前台窗口即用户正在操作的窗口，用它当判据既简单又准确：
        // 被夹回尺寸的那个窗口不是前台，它的回声自然被忽略，回环断开。
        if (memberHandle != WindowEnumerator.ForegroundWindow())
        {
            // 仍要刷新分组栏：本次事件可能正是"对齐终于落实"的那一下，
            // 而分组栏还停在对齐之前的旧尺寸上。
            RefreshRail(group);
            return;
        }

        // 拖动路径上的耗时埋点。
        //
        // 这条路径每秒执行几十次，任何一步阻塞都会直接表现为拖动卡顿，
        // 而从外部完全看不出是哪一步。只在超过阈值时才记录，正常拖动不产生日志。
        var watch = Stopwatch.StartNew();

        var bounds = WindowEnumerator.ReadVisibleBounds(memberHandle);
        var afterRead = watch.ElapsedMilliseconds;

        // 容忍一两像素的抖动，否则会陷入"对齐 → 触发事件 → 再对齐"的自激循环。
        if (bounds.IsEmpty || Near(bounds, group.Bounds, tolerance: 2))
        {
            // 即便组矩形没变也要刷新分组栏。
            //
            // 分组刚建立时窗口对齐是异步投递的，RefreshRail 当时读到的还是对齐前的
            // 旧矩形，分组栏因此比窗口宽出一大截；等窗口真正移动过来，这里的
            // Near 判定成立又直接返回，分组栏就永远停在错误宽度上 ——
            // 用户看到的是"必须点一下分组栏它才会缩到正确宽度"。
            RefreshRail(group);
            ReportSlowFrame(watch, afterRead, 0, 0);
            return;
        }

        var result = _manager.SetGroupBounds(group.Id, bounds);
        if (!result.Success || result.Group is null)
        {
            return;
        }

        var updated = result.Group;

        // 只对齐其他成员。对齐发起者本身会打断用户正在进行的拖拽。
        var actions = new List<Core.Grouping.WindowAction>();
        foreach (var tab in updated.LiveTabs)
        {
            if (tab.Identity.Handle != memberHandle)
            {
                actions.Add(new Core.Grouping.AlignWindowAction(tab.Identity, bounds));
            }
        }

        if (actions.Count > 0)
        {
            // 拖动时每帧都会走到这里。若上一批对齐还没执行完就再压一批，
            // 队列会越积越长，窗口跟手感变成一顿一顿的。
            // 因此同一时刻只允许有一批在途，未完成时直接丢弃本次 ——
            // 反正下一帧还会带来更新的位置。
            if (Interlocked.CompareExchange(ref _alignInFlight, 1, 0) == 0)
            {
                var queued = Stopwatch.StartNew();

                _ = _controller.ExecuteAsync(actions)
                    .ContinueWith(
                        _ =>
                        {
                            Interlocked.Exchange(ref _alignInFlight, 0);

                            // 对齐在控制队列线程上执行。它若被目标进程拖住，
                            // 后续所有帧都会因为"有一批在途"而被丢弃 ——
                            // 这正是"卡住两秒再整体跳回来"的形态。
                            if (queued.ElapsedMilliseconds > SlowFrameThresholdMs)
                            {
                                FileLog.Warn(
                                    $"对齐耗时 {queued.ElapsedMilliseconds}ms"
                                    + $"（{actions.Count} 个窗口），期间的拖动帧被丢弃。");
                            }
                        },
                        TaskScheduler.Default);
            }
            else
            {
                Interlocked.Increment(ref _droppedFrames);
            }
        }

        var afterQueue = watch.ElapsedMilliseconds;
        RefreshRail(updated);
        ReportSlowFrame(watch, afterRead, afterQueue, watch.ElapsedMilliseconds);
    }

    private const long SlowFrameThresholdMs = 40;

    /// <summary>是否已有一批对齐指令在执行中。用于拖动时丢弃过期的中间帧。</summary>
    private int _alignInFlight;

    /// <summary>因为上一批对齐还没完成而被丢弃的帧数。持续增长说明对齐是瓶颈。</summary>
    private long _droppedFrames;

    /// <summary>处于"提起"状态的组。拖动期间非权威成员停靠屏外，落位时归零。</summary>
    private string? _liftedGroupId;

    /// <summary>提起期间唯一可信的位置来源：标题栏拖动是被拖成员，分组条拖动是活动成员。</summary>
    private nint _liftAuthority;

    /// <summary>上一次拖放命中判定的时间戳，用于 30ms 节流。</summary>
    private long _lastDropProbeTicks;

    /// <summary>
    /// 屏外停靠的 X 坐标，与系统停靠最小化窗口的传统位置一致，远离一切可见显示器。
    /// </summary>
    private const int ParkedLeft = -32000;

    /// <summary>
    /// 拖动开始：把组"提起来"。除权威成员外的窗口全部停靠到屏外。
    ///
    /// 拖动期间对成员窗口做任何跨进程调用都是卡顿之源：同一进程的多个成员
    /// （典型如两个 IDEA 项目窗口）共享同一条 UI 线程，被拖窗口的模态拖动循环
    /// 每处理一步鼠标移动，都会被我们塞给其他成员的重排消息打断一次 ——
    /// 事件到得再快、我们处理得再快都救不了，这正是"拖分组窗口卡爆"的根因。
    /// 停靠之后拖动期间零跨进程调用，被拖窗口就是一次纯粹的系统原生拖动。
    ///
    /// 停靠而不是隐藏或最小化：任务栏按钮纹丝不动，也没有还原动画。
    /// 崩溃安全：停靠只是一次普通对齐，崩溃恢复与退出解散走的都是快照还原，
    /// 天然会把屏外的窗口拉回原位，不需要额外的清理账本。
    /// </summary>
    private void LiftGroup(GroupSession group, nint authority)
    {
        if (_liftedGroupId is not null)
        {
            return;
        }

        _liftedGroupId = group.Id;
        _liftAuthority = authority;

        var bounds = group.Bounds;
        var actions = new List<Core.Grouping.WindowAction>();

        foreach (var tab in group.LiveTabs)
        {
            var handle = tab.Identity.Handle;

            // 权威成员在用户手里，活动标签是分组栏的定位锚 —— 两者通常是同一个
            // （拖动前系统必已把窗口带到前台，前台事件又把它设成了活动标签）。
            // 万一不是，也绝不停靠活动标签，宁可让它留在原地可见。
            if (handle == authority || handle == group.ActiveTab?.Identity.Handle)
            {
                continue;
            }

            actions.Add(new Core.Grouping.AlignWindowAction(
                tab.Identity,
                PixelRect.FromSize(ParkedLeft, bounds.Top, bounds.Width, bounds.Height)));
        }

        if (actions.Count > 0)
        {
            FileLog.Info($"组 {group.Id} 提起：{actions.Count} 个成员停靠屏外。");

            _ = _controller.ExecuteAsync(actions).ContinueWith(
                t => ReportFailures(t.Result),
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 拖动结束：落位。以权威成员的实际位置为准，把停靠的成员一次性对齐回来。
    ///
    /// 必须无条件对齐全部成员：拖动期间组矩形一直跟着权威成员走，
    /// "接近就跳过"的常规判断会认为一切就绪，而停靠的窗口还在屏外。
    /// 也不走 <see cref="_alignInFlight"/> 节流门 —— 这不是可丢弃的中间帧，是终点。
    /// </summary>
    private void SettleGroup(string groupId, nint authority)
    {
        _liftedGroupId = null;
        _liftAuthority = 0;

        var group = _manager.FindGroup(groupId);
        if (group is null)
        {
            return;
        }

        var bounds = authority != 0
            ? WindowEnumerator.ReadVisibleBounds(authority)
            : PixelRect.Empty;

        if (bounds.IsEmpty)
        {
            bounds = group.Bounds;
        }

        if (bounds.IsEmpty)
        {
            return;
        }

        var result = _manager.SetGroupBounds(groupId, bounds);
        var updated = result.Group ?? group;

        var actions = new List<Core.Grouping.WindowAction>();
        foreach (var tab in updated.LiveTabs)
        {
            if (tab.Identity.Handle != authority)
            {
                actions.Add(new Core.Grouping.AlignWindowAction(tab.Identity, bounds));
            }
        }

        if (actions.Count > 0)
        {
            _ = _controller.ExecuteAsync(actions).ContinueWith(
                t => ReportFailures(t.Result),
                TaskScheduler.Default);
        }

        RefreshRail(updated);

        // 终点位置落盘一次，崩溃恢复才知道窗口现在应该在哪。高频的拖动路径上从不落盘。
        PersistSession();
    }

    /// <summary>
    /// 只在单帧超时的情况下记录耗时分解。
    /// 拖动流畅时完全不写日志，卡顿时立刻能看出是读边框、排队还是刷新分组栏慢。
    /// </summary>
    private void ReportSlowFrame(Stopwatch watch, long afterRead, long afterQueue, long total)
    {
        if (watch.ElapsedMilliseconds <= SlowFrameThresholdMs)
        {
            return;
        }

        FileLog.Warn(
            $"拖动帧耗时 {watch.ElapsedMilliseconds}ms —— "
            + $"读窗口边框 {afterRead}ms，"
            + $"排队对齐 {Math.Max(0, afterQueue - afterRead)}ms，"
            + $"刷新分组栏 {Math.Max(0, total - afterQueue)}ms，"
            + $"累计丢帧 {Interlocked.Read(ref _droppedFrames)}");
    }

    /// <summary>窗口圆角半径缓存。避免拖动时每帧查询 DWM 属性。</summary>
    private readonly Dictionary<nint, int> _cornerRadiusCache = [];

    private static bool Near(PixelRect a, PixelRect b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance
        && Math.Abs(a.Top - b.Top) <= tolerance
        && Math.Abs(a.Right - b.Right) <= tolerance
        && Math.Abs(a.Bottom - b.Bottom) <= tolerance;

    /// <summary>前台窗口变化时同步活动标签，让用户用 Alt+Tab 切换后轨道也保持一致。</summary>
    private void OnForegroundChanged(nint hwnd)
    {
        var group = FindGroupByHandle(hwnd);
        var tab = group is null ? null : FindTabByHandle(group, hwnd);

        if (group is null || tab is null)
        {
            // 前台切到了组外的窗口。轨道靠属主关系自动跟随成员一起沉下去，
            // 但属主设置失败时走的是手动 Z 序，这时不做处理即可 ——
            // 强行把轨道抬起来反而会让它盖在别的应用上。
            return;
        }

        if (tab.State is TabState.Active)
        {
            // 成员窗口重新回到前台，把轨道贴回它上面。
            if (_rails.TryGetValue(group.Id, out var rail))
            {
                rail.RestackAboveOwner();
            }

            return;
        }

        Apply(_manager.ActivateTab(group.Id, tab.Identity), group.Id);
    }

    // ------------------------------------------------------------------
    // 拖拽合并
    // ------------------------------------------------------------------

    private void OnDragStart(nint hwnd)
    {
        // 只记录，不做判定 —— 拖拽开始时用户还没决定要放到哪。
        _draggingWindow = hwnd;
        _dropCandidate = 0;
        RefreshLocationWatchList();

        FileLog.Info($"拖拽开始：{DescribeWindow(hwnd)}");

        // 拖的是组成员（移动或缩放同理）→ 整组提起，其余成员停靠屏外。
        if (FindGroupByHandle(hwnd) is { } grabbed)
        {
            LiftGroup(grabbed, hwnd);
        }
    }

    /// <summary>
    /// 拖拽过程中持续更新候选目标并显示提示条。
    ///
    /// 由被拖窗口的 LOCATIONCHANGE 事件驱动 —— 拖动时它每帧都会上报位置，
    /// 正好当作轮询脉冲，不需要额外的定时器。
    /// </summary>
    private void UpdateDropCandidate()
    {
        if (_draggingWindow == 0 || !_settings.Enabled)
        {
            return;
        }

        // 命中判定要枚举光标下的窗口链并逐个做资格评估，是拖动路径上最贵的一步。
        // 位置事件不合并之后它会以事件原始频率被调用，把 UI 线程堵出几十毫秒的积压。
        // 30ms 内光标挪不了几个像素，节流对判定结果毫无影响。
        var now = Stopwatch.GetTimestamp();
        if ((now - _lastDropProbeTicks) * 1000 / Stopwatch.Frequency < 30)
        {
            return;
        }

        _lastDropProbeTicks = now;

        if (!User32Cursor.TryGet(out var cursor))
        {
            return;
        }

        var excluded = CollectOwnHandles();
        excluded.Add(_draggingWindow);

        var (candidate, blockedReason) = ResolveDropCandidate(cursor, excluded);

        if (candidate == 0)
        {
            if (_dropCandidate != 0)
            {
                _dropCandidate = 0;
                _dropHint?.HideHint();
            }

            return;
        }

        _dropCandidate = blockedReason is null ? candidate : 0;

        var bounds = WindowEnumerator.ReadVisibleBounds(candidate);
        var dpi = MonitorLookup.DpiForWindow(candidate);

        (_dropHint ??= new DropHintWindow()).ShowFor(candidate, bounds, dpi, blockedReason);
    }

    /// <summary>
    /// 在光标处的候选链里挑出拖放目标。
    ///
    /// 逐层向下找第一个**可分组且光标落在其拖放区**的窗口。跳过不可分组的隔层窗口，
    /// 是"两个目标之间隔着别的应用就合不了"这个问题的正解。
    ///
    /// 若整条链都不可用，返回最上面那个窗口和它被拒的原因，好让提示条能告诉用户为什么。
    /// </summary>
    private (nint Candidate, string? BlockedReason) ResolveDropCandidate(
        PixelPoint cursor, IReadOnlySet<nint> excluded)
    {
        var context = BuildEligibilityContext();
        nint firstBlocked = 0;
        string? firstReason = null;

        foreach (var hwnd in WindowEnumerator.EnumerateWindowsAt(cursor, excluded))
        {
            var info = _enumerator.Describe(hwnd);
            if (info is null)
            {
                continue;
            }

            var eligibility = EligibilityEvaluator.Evaluate(info, context);

            if (!eligibility.IsEligible)
            {
                // 已在组里的窗口是合法目标（加入该组），不算被拒。
                if (eligibility.Reason is IneligibleReason.AlreadyGrouped)
                {
                    if (IsWithinDropZone(hwnd, cursor))
                    {
                        return (hwnd, null);
                    }

                    continue;
                }

                // 只对**用户能处理**的拒绝原因给出提示。
                // 桌面、工具窗、无标题宿主窗口这类结构性排除在拖拽时到处都是，
                // 提示出来纯属噪声，还会盖住真正有用的信息。
                if (IsActionableRejection(eligibility.Reason))
                {
                    firstBlocked = firstBlocked == 0 ? hwnd : firstBlocked;
                    firstReason ??= eligibility.Explanation;
                }

                continue;
            }

            if (IsWithinDropZone(hwnd, cursor))
            {
                return (hwnd, null);
            }
        }

        return firstBlocked != 0 ? (firstBlocked, firstReason) : (0, null);
    }

    /// <summary>
    /// 该拒绝原因是否值得在拖拽提示条上告诉用户。
    ///
    /// 判据是"用户看到之后能不能做点什么"。
    /// 「这是 Windows 桌面自身的窗口」用户既改变不了也不需要知道，
    /// 而且桌面几乎在任何位置都会出现在候选链里，提示出来只会刷屏。
    /// </summary>
    private static bool IsActionableRejection(IneligibleReason reason) => reason switch
    {
        // 结构性排除：与用户意图无关，纯噪声。
        IneligibleReason.ShellWindow => false,
        IneligibleReason.OwnWindow => false,
        IneligibleReason.ToolWindow => false,
        IneligibleReason.NoTitle => false,
        IneligibleReason.Cloaked => false,
        IneligibleReason.NotVisible => false,
        IneligibleReason.TooSmall => false,
        IneligibleReason.OwnedPopup => false,

        // 其余（已在其他组、被规则阻止、不在允许列表、权限不足、
        // 无响应、原生标签应用）都指向一个用户可以处理的具体状况。
        _ => true,
    };

    /// <summary>
    /// 拖拽结束，判定是否合并。
    ///
    /// 这条路径的每一步都记日志：拖放交互无法用单元测试覆盖，出问题时
    /// "事件没来"、"目标找错"、"不在拖放区"、"资格被拒"这四种情况从表象上完全一样，
    /// 没有日志就只能靠猜。
    /// </summary>
    private void OnDragEnd(nint hwnd)
    {
        var dragged = _draggingWindow;
        var candidate = _dropCandidate;

        _draggingWindow = 0;
        _dropCandidate = 0;
        _dropHint?.HideHint();
        RefreshLocationWatchList();

        FileLog.Info($"拖拽结束：{DescribeWindow(hwnd)}");

        // 组成员的拖拽在开始时已把整组提起，无论以何种方式结束都必须落位，
        // 否则停靠的成员会滞留屏外。组成员拖动 = 整组移动，永不参与合并判定。
        if (_liftedGroupId is { } liftedId)
        {
            FileLog.Info($"  组 {liftedId} 整体移动完成。");
            SettleGroup(liftedId, dragged != 0 ? dragged : _liftAuthority);
            return;
        }

        if (dragged == 0 || dragged != hwnd)
        {
            FileLog.Info("  未合并：拖拽起止窗口不一致。");
            return;
        }

        if (!_settings.Enabled)
        {
            FileLog.Info("  未合并：TabNest 已停用（托盘菜单可重新启用）。");
            return;
        }

        // 直接用拖拽过程中已经确定并向用户展示过的候选目标。
        // 松手瞬间重新做一次命中判定是不对的：窗口 Z 序此刻可能已经变了，
        // 结果会和用户看到的提示不一致 —— 提示指向 A，却合并到了 B。
        if (candidate == 0)
        {
            // 组成员被拖走时**不拆分**：分组之后整组就是一个整体，
            // 拖任意一个成员等于拖整组，位置同步已由 SyncGroupToMember 完成。
            //
            // 拆分只能通过菜单里的「拆分为独立窗口」显式发起。
            // 早期版本在这里自动拆分，导致用户只是想挪一下窗口却把组拆了，
            // 而且拖回来也不会自动合并，非常割裂。
            if (FindGroupByHandle(dragged) is { } memberGroup)
            {
                FileLog.Info($"  组 {memberGroup.Id} 整体移动完成。");
                SyncGroupToMember(memberGroup, dragged);
                return;
            }

            FileLog.Info("  未合并：松手时没有有效的拖放目标。");
            return;
        }

        FileLog.Info($"  拖放目标 {DescribeWindow(candidate)}");
        TryMerge(dragged, candidate);
    }

    private static string DescribeWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "<null>";
        }

        var title = WindowEnumerator.ReadTitle(hwnd);
        var cls = WindowEnumerator.ReadClassName(hwnd);

        return $"0x{hwnd:X} [{cls}] {(string.IsNullOrEmpty(title) ? "（无标题）" : title)}";
    }

    /// <summary>
    /// 光标是否落在目标窗口的拖放区域内。
    ///
    /// 默认只认标题栏附近的一条横带，与浏览器"拖到标签栏才合并"的心智一致，
    /// 也避免用户只是把窗口挪到另一个窗口上方就被意外分组。
    /// 用户可在设置中改为整个窗口都是拖放目标。
    /// </summary>
    private bool IsWithinDropZone(nint targetHandle, PixelPoint cursor)
    {
        var bounds = WindowEnumerator.ReadVisibleBounds(targetHandle);
        if (bounds.IsEmpty)
        {
            return false;
        }

        if (_settings.Grouping.WholeWindowIsDropTarget)
        {
            return bounds.Contains(cursor);
        }

        var band = DropZoneHeight(targetHandle);

        return cursor.X >= bounds.Left
            && cursor.X < bounds.Right
            && cursor.Y >= bounds.Top
            && cursor.Y < bounds.Top + band;
    }

    /// <summary>
    /// 为标签轨道预留空间，返回成员窗口应占据的矩形。
    ///
    /// 轨道贴在返回矩形的正上方。若直接沿用窗口原位置，贴着屏幕顶部的窗口
    /// （尤其是刚从最大化还原的）会把轨道挤到屏幕外 —— 分组明明成功了，
    /// 用户却看不到任何分组栏，只能反复怀疑是不是没生效。
    ///
    /// 做法与 Groupy 一致：窗口内容区整体下移让出轨道高度，整体占用不变。
    /// </summary>
    private static PixelRect ReserveRailSpace(WindowInfo anchor)
    {
        var handle = anchor.Identity.Handle;
        var dpi = MonitorLookup.DpiForWindow(handle);
        var railHeight = new RailMetrics().ScaleTo(dpi).Height;
        var workArea = MonitorLookup.ForWindow(handle).WorkArea;

        var bounds = anchor.Bounds;
        var top = bounds.Top;

        // 上方装不下轨道就把内容整体下移。
        if (top - railHeight < workArea.Top)
        {
            top = workArea.Top + railHeight;
        }

        // 下移后不能超出工作区，否则窗口底部会被任务栏切掉。
        var bottom = Math.Min(top + bounds.Height, workArea.Bottom);

        return new PixelRect(bounds.Left, top, bounds.Right, bottom);
    }

    /// <summary>标题栏拖放横带的高度。随 DPI 缩放，否则高分屏上这条带子会显得过窄。</summary>
    private static int DropZoneHeight(nint targetHandle)
    {
        var dpi = MonitorLookup.DpiForWindow(targetHandle);
        return (int)Math.Round(48.0 * dpi / 96.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 测试入口：跳过拖拽检测，直接走合并路径。
    ///
    /// 合成鼠标事件无法可靠触发系统的窗口移动事件，拖拽本身没法自动化；
    /// 但拖拽之后的**全部逻辑**（资格判定、建组、对齐、轨道显示）都能，
    /// 而问题恰恰几乎都出在后面这一段。
    /// </summary>
    internal void MergeForTest(nint draggedHandle, nint targetHandle) =>
        TryMerge(draggedHandle, targetHandle);

    /// <summary>测试入口：取指定组当前的轨道窗口。</summary>
    internal TabRailWindow? GetRailForTest(string groupId) =>
        _rails.GetValueOrDefault(groupId);

    /// <summary>测试入口：当前所有组。</summary>
    internal IReadOnlyCollection<GroupSession> GroupsForTest => _manager.Groups;

    /// <summary>测试入口：模拟点击分组栏上的「关闭整组」按钮，走完整生产路径。</summary>
    internal void CloseGroupForTest(string groupId) =>
        OnRailInteraction(new Rail.RailInteraction
        {
            GroupId = groupId,
            Action = Rail.RailAction.CloseGroup,
        });

    /// <summary>测试入口：模拟某个成员上报了位置变化。</summary>
    internal void NotifyLocationForTest(nint hwnd) => OnLocationChanged(hwnd);

    /// <summary>把 <paramref name="draggedHandle"/> 合并到 <paramref name="targetHandle"/> 所在的组。</summary>
    private void TryMerge(nint draggedHandle, nint targetHandle)
    {
        var dragged = _enumerator.Describe(draggedHandle);
        var target = _enumerator.Describe(targetHandle);

        if (dragged is null || target is null)
        {
            return;
        }

        var context = BuildEligibilityContext();

        var draggedResult = EligibilityEvaluator.Evaluate(dragged, context);
        if (!draggedResult.IsEligible)
        {
            FileLog.Info($"拒绝分组「{dragged.Title}」：{draggedResult.Explanation}");
            return;
        }

        FileLog.Info($"  被拖窗口资格：可分组（{dragged.ProcessName}）");

        // 目标已在组里 → 加入该组；否则用两个窗口新建一组。
        var targetGroup = _manager.FindGroupByWindow(target.Identity);

        if (targetGroup is not null)
        {
            FileLog.Info($"  目标已属于组 {targetGroup.Id}，加入该组。");
            var snapshot = WindowController.CaptureSnapshot(draggedHandle);
            if (snapshot is null)
            {
                return;
            }

            Apply(
                _manager.AddTab(targetGroup.Id, new TabCandidate(dragged, snapshot)),
                targetGroup.Id);
            return;
        }

        var targetResult = EligibilityEvaluator.Evaluate(target, context);
        if (!targetResult.IsEligible)
        {
            FileLog.Info($"拒绝分组「{target.Title}」：{targetResult.Explanation}");
            return;
        }

        // 先写快照再动窗口：顺序反了，崩溃就会让窗口永久错位。
        var targetSnapshot = WindowController.CaptureSnapshot(targetHandle);
        var draggedSnapshot = WindowController.CaptureSnapshot(draggedHandle);

        if (targetSnapshot is null || draggedSnapshot is null)
        {
            return;
        }

        // 目标窗口排第一：组应当留在拖放目标的位置上，而不是跳到被拖窗口那里。
        var result = _manager.CreateGroup(
            [
                new TabCandidate(target, targetSnapshot),
                new TabCandidate(dragged, draggedSnapshot),
            ],
            ReserveRailSpace(target));

        if (result.Success && result.Group is not null)
        {
            FileLog.Info($"  ✓ 已创建标签组 {result.Group.Id}，成员 2 个。");
            Apply(result, result.Group.Id);
        }
        else
        {
            FileLog.Warn($"  未合并：{result.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 轨道交互
    // ------------------------------------------------------------------

    private const uint MenuDetachTab = 1;
    private const uint MenuCloseTab = 2;
    private const uint MenuCloseOthers = 3;
    private const uint MenuCloseLeft = 4;
    private const uint MenuCloseRight = 5;
    private const uint MenuCloseAll = 6;
    private const uint MenuToggleLock = 7;
    private const uint MenuDissolveGroup = 8;

    private void OnRailInteraction(RailInteraction interaction)
    {
        var groupId = interaction.GroupId;

        if (interaction.Action is RailAction.ShowMenu)
        {
            ShowRailMenu(interaction);
            return;
        }

        if (interaction.Action is RailAction.MoveGroup)
        {
            MoveGroup(groupId, interaction.DeltaX, interaction.DeltaY);
            return;
        }

        if (interaction.Action is RailAction.MoveGroupEnd)
        {
            if (_liftedGroupId == groupId)
            {
                SettleGroup(groupId, _liftAuthority);
            }

            return;
        }

        var result = interaction.Action switch
        {
            RailAction.ActivateTab => _manager.ActivateTab(groupId, interaction.Target),
            RailAction.CloseTab => _manager.CloseTab(groupId, interaction.Target),
            RailAction.DetachTab => _manager.DetachTab(groupId, interaction.Target),
            RailAction.CloseGroup => _manager.CloseAllTabs(groupId),
            RailAction.ReorderTab => _manager.ReorderTab(
                groupId, interaction.Target, interaction.InsertionIndex),
            _ => null,
        };

        if (result is not null)
        {
            Apply(result, groupId);
        }

        // 关闭是"请求"而非"结果"：应用可以拒绝、可以弹保存提示，
        // 也可能只是把窗口藏起来而不销毁（Electron 应用的常见做法）。
        // 因此关闭之后必须主动回头核验，不能守着销毁事件干等 ——
        // 等不到的话分组栏会一直指向一批已经不存在的窗口。
        if (interaction.Action is RailAction.CloseGroup or RailAction.CloseTab)
        {
            ScheduleSweep(groupId);
        }
    }

    /// <summary>
    /// 稍后核验一次组成员是否都还在。
    ///
    /// 延迟是必须的：关闭指令刚投递出去，应用还没来得及响应，
    /// 立刻核验只会看到窗口都还在。两次核验覆盖"立即关闭"与"弹框确认后关闭"两种节奏。
    /// </summary>
    private void ScheduleSweep(string groupId)
    {
        foreach (var delayMs in (int[])[400, 1500])
        {
            _ = Task.Delay(delayMs, _shutdown.Token).ContinueWith(
                _ =>
                {
                    if (!_disposed)
                    {
                        // 组状态只能在 UI 线程上改动，必须投回去。
                        _dispatcher.Post(() => SweepVanishedMembers(groupId));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 拖动分组条时整组一起移动。
    ///
    /// 只改组矩形并把所有成员对齐过去 —— 组模型本身不变，因此不需要落盘，
    /// 也不会因为高频的鼠标移动把会话快照写爆。
    /// </summary>
    private void MoveGroup(string groupId, int deltaX, int deltaY)
    {
        var group = _manager.FindGroup(groupId);
        if (group is null || group.Bounds.IsEmpty)
        {
            return;
        }

        // 首次移动时提起整组：拖动期间只有活动成员实时跟随，其余成员停靠屏外，
        // 松手（MoveGroupEnd）再落位。往同一进程的多个窗口高频投递重排消息
        // 会把对方的 UI 线程塞满，整组一顿一顿 —— 与标题栏拖动同一个病根。
        if (_liftedGroupId is null)
        {
            var authority = group.ActiveTab?.Identity.Handle ?? 0;
            if (authority == 0)
            {
                return;
            }

            LiftGroup(group, authority);
        }

        if (_liftedGroupId != groupId)
        {
            // 理论上到不了：每条分组栏各自独立，同一时刻只可能拖动一条。
            return;
        }

        var moved = group.Bounds.OffsetBy(deltaX, deltaY);

        // 必须写回组模型。只用局部变量的话，下一次鼠标移动仍从旧矩形算起，
        // 窗口被反复拉回原处，用户感觉就是"拖不动、像被锁定了"。
        var result = _manager.SetGroupBounds(groupId, moved);
        if (!result.Success || result.Group is null)
        {
            return;
        }

        // 只对齐活动成员这一个窗口。中间帧可丢 —— 位置是绝对坐标，
        // 下一帧总会带来更新的值，最终正确性由松手时的落位保证。
        if (result.Group.ActiveTab is { } active
            && active.Identity.Handle == _liftAuthority
            && Interlocked.CompareExchange(ref _alignInFlight, 1, 0) == 0)
        {
            _ = _controller.ExecuteAsync(new Core.Grouping.AlignWindowAction(active.Identity, moved))
                .ContinueWith(
                    _ => Interlocked.Exchange(ref _alignInFlight, 0),
                    TaskScheduler.Default);
        }

        RefreshRail(result.Group);
    }

    private void ShowRailMenu(RailInteraction interaction)
    {
        var group = _manager.FindGroup(interaction.GroupId);
        if (group is null || !_rails.TryGetValue(interaction.GroupId, out var rail))
        {
            return;
        }

        // 从左上角菜单按钮打开时没有具体标签，此时应当作用于**当前活动标签**。
        // 早期直接用空目标，导致除"关闭整组/锁定/取消分组"外的项全部灰显，
        // 菜单看上去像坏的。用户点菜单按钮的意图就是操作眼下这个标签。
        var target = group.FindTab(interaction.Target) is not null
            ? interaction.Target
            : group.ActiveTab?.Identity ?? default;

        var hasTab = group.FindTab(target) is not null;
        var locked = group.IsLocked;

        // 锁定的组把破坏性操作全部灰显而不是隐藏 —— 隐藏会让用户以为功能不存在，
        // 灰显才能传达"这里被你自己锁上了"。
        List<PopupMenuItem> items =
        [
            new(MenuDetachTab, "拆分为独立窗口", Enabled: hasTab && !locked),
            new(MenuCloseTab, "关闭标签", Enabled: hasTab && !locked),
            PopupMenuItem.Separator,
            new(MenuCloseOthers, "关闭其他标签", Enabled: hasTab && !locked),
            new(MenuCloseLeft, "关闭左侧标签", Enabled: hasTab && !locked),
            new(MenuCloseRight, "关闭右侧标签", Enabled: hasTab && !locked),
            new(MenuCloseAll, "关闭此组所有窗口", Enabled: !locked),
            PopupMenuItem.Separator,
            new(MenuToggleLock, locked ? "解锁此分组" : "锁定此分组", Checked: locked),
            new(MenuDissolveGroup, "取消此分组"),
        ];

        var command = PopupMenuBuilder.Show(rail.Handle, interaction.ScreenPoint, items);
        if (command == 0)
        {
            return;
        }

        var groupId = interaction.GroupId;

        var result = command switch
        {
            MenuDetachTab => _manager.DetachTab(groupId, target),
            MenuCloseTab => _manager.CloseTab(groupId, target),
            MenuCloseOthers => _manager.CloseOtherTabs(groupId, target),
            MenuCloseLeft => _manager.CloseTabsToLeft(groupId, target),
            MenuCloseRight => _manager.CloseTabsToRight(groupId, target),
            MenuCloseAll => _manager.CloseAllTabs(groupId),
            MenuToggleLock => _manager.SetGroupLocked(groupId, !locked),
            MenuDissolveGroup => _manager.DissolveGroup(groupId),
            _ => null,
        };

        if (result is not null)
        {
            Apply(result, groupId);
        }
    }

    // ------------------------------------------------------------------
    // 应用结果
    // ------------------------------------------------------------------

    /// <summary>
    /// 执行领域层产出的指令并刷新轨道。
    ///
    /// 顺序很重要：先把组快照落盘，再执行窗口操作。反过来做的话，
    /// 进程在两者之间崩溃会留下已被移动、却没有还原依据的窗口。
    /// </summary>
    private void Apply(GroupOperationResult result, string groupId)
    {
        if (!result.Success)
        {
            FileLog.Warn($"组操作失败：{result.Message}");
            return;
        }

        PersistSession();
        RefreshLocationWatchList();

        if (result.Actions.Count > 0)
        {
            FileLog.Debug(
                $"组 {groupId} 产生 {result.Actions.Count} 条指令："
                + string.Join("、", result.Actions.Select(a =>
                    $"{a.GetType().Name}(0x{a.Target.Handle:X})")));
        }

        var actions = FilterAgainstReality(result.Actions);

        if (actions.Count > 0)
        {
            _ = _controller.ExecuteAsync(actions).ContinueWith(
                t => ReportFailures(t.Result),
                TaskScheduler.Default);
        }

        if (result.Group is null)
        {
            CloseRail(groupId);
        }
        else
        {
            RefreshRail(result.Group);
        }
    }

    /// <summary>
    /// 把领域层产出的指令对照现实过滤一遍：**绝不对应用已经关掉的窗口动手**。
    ///
    /// 领域层是纯状态机，它不知道也不该知道某个窗口此刻是否还在。于是会出现
    /// 这样一条链：用户点「关闭整组」→ 第一个应用缩到托盘 → 该成员被判定消失
    /// → 组降到一个成员而自动解散 → 解散产出「还原剩下那个窗口」的指令 →
    /// 而那个窗口此刻也已经被它自己的应用关掉了。
    ///
    /// 还原会执行 SetWindowPlacement + SW_SHOWNOACTIVATE，把一个已关闭的窗口
    /// 硬拽回屏幕：窗口看得见，应用却认为自己已关闭，因此不响应任何输入 ——
    /// 用户只能去托盘重新打开。两个应用都是"关闭即缩托盘"时必然撞上，
    /// 只要有一个是真关闭（窗口已销毁，还原是空操作）就碰不到。
    ///
    /// 关闭指令本身永远保留：那正是我们想让它发生的事。
    /// </summary>
    private static List<WindowAction> FilterAgainstReality(IReadOnlyList<WindowAction> actions)
    {
        var kept = new List<WindowAction>(actions.Count);

        foreach (var action in actions)
        {
            if (action is CloseWindowAction)
            {
                kept.Add(action);
                continue;
            }

            if (!WindowEnumerator.IsAliveAndShown(action.Target.Handle))
            {
                FileLog.Info(
                    $"跳过对已关闭窗口的 {action.GetType().Name}："
                    + $"0x{action.Target.Handle:X}（还原它只会得到一个点不动的僵尸窗口）");
                continue;
            }

            kept.Add(action);
        }

        return kept;
    }

    private static void ReportFailures(IReadOnlyList<OperationResult> results)
    {
        foreach (var r in results)
        {
            if (!r.Success)
            {
                // 失败不能静默吞掉 —— UI 需要据此显示"重试/拆分/查看原因"。
                FileLog.Warn($"窗口操作失败：{r.Message}");
            }
        }
    }

    /// <summary>
    /// 更新观察层的位置事件关注列表。
    ///
    /// 只跟踪组成员和正在拖拽的窗口。全系统的 LOCATIONCHANGE 量极大，
    /// 不做这层过滤会让空闲 CPU 高出一个数量级。任何改变组成员的操作都必须调用。
    /// </summary>
    private void RefreshLocationWatchList()
    {
        var handles = new List<nint>(8);

        foreach (var group in _manager.Groups)
        {
            foreach (var tab in group.Tabs)
            {
                if (tab.IsLive)
                {
                    handles.Add(tab.Identity.Handle);
                }
            }
        }

        if (_draggingWindow != 0)
        {
            handles.Add(_draggingWindow);
        }

        _observer.SetLocationWatchList(handles);
    }

    private void RefreshRail(GroupSession group)
    {
        var active = group.ActiveTab;
        if (active is null)
        {
            FileLog.Warn($"组 {group.Id} 没有活动标签，关闭轨道。");
            CloseRail(group.Id);
            return;
        }

        // 以**活动窗口的实际可见边框**为准，而不是组模型里记录的矩形。
        //
        // 两者理论上应该一致，但窗口对齐是异步的，且不同应用对 SetWindowPos 的
        // 实际响应会有细微出入（最小尺寸限制、自绘边框、DPI 舍入）。
        // 用组矩形会让分组栏比窗口宽出一点点，一眼就能看出是两个东西。
        // 跟着真实窗口走才能严丝合缝。
        var actual = WindowEnumerator.ReadVisibleBounds(active.Identity.Handle);
        var anchor = actual.IsEmpty ? group.Bounds : actual;

        if (anchor.IsEmpty)
        {
            FileLog.Warn($"组 {group.Id} 的承载矩形为空，无法显示轨道。");
            return;
        }

        var dpi = MonitorLookup.DpiForWindow(active.Identity.Handle);

        // 圆角跟随承载窗口实测值，不写死：Windows 10 无窗口圆角，
        // Windows 11 上应用也可声明直角。写死会在方角窗口上方露出两个缺口。
        //
        // 结果按窗口缓存：拖动时这里每秒被调用几十次，每次查一遍 DWM 属性
        // 是拖动卡顿的来源之一，而圆角偏好在窗口生命周期内基本不变。
        if (!_cornerRadiusCache.TryGetValue(active.Identity.Handle, out var cornerRadius))
        {
            cornerRadius = WindowEnumerator.TopCornerRadius(active.Identity.Handle, dpi);
            _cornerRadiusCache[active.Identity.Handle] = cornerRadius;
        }

        var metrics = new RailMetrics().ScaleTo(dpi) with { TopCornerRadius = cornerRadius };

        var layout = TabRailLayoutEngine.Compute(
            group.Tabs,
            active.Identity,
            anchor,
            metrics,
            _settings.Appearance.CloseButton,
            showMenuButton: !_settings.Appearance.HideMenuButton);

        var railStart = Stopwatch.StartNew();
        var rail = GetOrCreateRail(group.Id);

        rail.Update(
            new RailRenderState
            {
                Layout = layout,
                Tabs = group.Tabs,
                ActiveIdentity = active.Identity,
                Theme = RailTheme.Default,
                Dpi = dpi,
            },
            ownerHwnd: active.Identity.Handle);

        // 分组栏刷新只在异常缓慢时记录。
        // 拖动时这里每秒执行几十次，无条件记录会把日志刷爆，还会自己变成性能问题。
        if (railStart.ElapsedMilliseconds > SlowFrameThresholdMs)
        {
            FileLog.Warn(
                $"分组栏刷新耗时 {railStart.ElapsedMilliseconds}ms"
                + $"（组 {group.Id}，{group.LiveTabCount} 个标签）");
        }
    }

    private TabRailWindow GetOrCreateRail(string groupId)
    {
        if (_rails.TryGetValue(groupId, out var existing))
        {
            return existing;
        }

        var rail = new TabRailWindow(groupId, OnRailInteraction);
        _rails[groupId] = rail;
        return rail;
    }

    private void CloseRail(string groupId)
    {
        if (_rails.Remove(groupId, out var rail))
        {
            rail.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // 全局开关与退出
    // ------------------------------------------------------------------

    /// <summary>
    /// 切换全局主开关。
    /// 关闭时拆散所有组并还原窗口，但保留托盘与配置、不退出进程 ——
    /// 用户要的是"暂停接管窗口"，不是"关掉这个软件"。
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (_settings.Enabled == enabled)
        {
            return;
        }

        _settings = _settings with { Enabled = enabled };
        SaveSettings();

        if (!enabled)
        {
            DissolveEverything();
        }
    }

    /// <summary>
    /// 整份替换设置。设置中心一次可能改任意字段，因此不逐项开接口。
    ///
    /// 逐字段比对出真正变化的部分，只对受影响的子系统做重建 ——
    /// 无脑全量重建会让改一个无关开关也把所有分组栏闪一遍。
    /// </summary>
    public void ApplySettings(AppSettings updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        var previous = _settings;
        if (previous == updated)
        {
            return;
        }

        _settings = updated;
        _manager.TaskbarPolicy = updated.TaskbarButtons;
        SaveSettings();

        // 主开关关闭要拆散一切，且后续的重建都没有意义，直接返回。
        if (previous.Enabled && !updated.Enabled)
        {
            FileLog.Info("主开关已关闭，拆散全部分组。");
            DissolveEverything();
            return;
        }

        if (previous.TaskbarButtons != updated.TaskbarButtons)
        {
            ApplyTaskbarPolicyToAllGroups();
        }

        // 外观改动只需重画分组栏。
        if (previous.Appearance != updated.Appearance)
        {
            foreach (var group in _manager.Groups.ToList())
            {
                RefreshRail(group);
            }
        }
    }

    private void ApplyTaskbarPolicyToAllGroups()
    {
        foreach (var group in _manager.Groups.ToList())
        {
            RefreshRail(group);
        }
    }

    public void SetTaskbarPolicy(TaskbarButtonPolicy policy)
    {
        _settings = _settings with { TaskbarButtons = policy };
        _manager.TaskbarPolicy = policy;
        SaveSettings();

        foreach (var group in _manager.Groups.ToList())
        {
            RefreshRail(group);
        }
    }

    /// <summary>拆散所有组并还原全部窗口。主开关关闭与安全退出都走这里。</summary>
    public void DissolveEverything()
    {
        // 同样要对照现实过滤：解散时组里可能已经有成员被它自己的应用关掉了，
        // 对它们做还原只会拽出一个点不动的僵尸窗口。
        var actions = FilterAgainstReality(_manager.DissolveAll());

        foreach (var groupId in _rails.Keys.ToList())
        {
            CloseRail(groupId);
        }

        if (actions.Count > 0)
        {
            // 同步等待：退出路径上必须确认窗口真的还原了才能继续，
            // 留下没还原的窗口是最不可接受的失败。
            _controller.ExecuteAsync(actions).GetAwaiter().GetResult();
        }

        // 还原完成后才清空快照。反过来做的话，清空之后、还原之前崩溃，
        // 就再也没有依据把窗口放回去了。
        ClearSession();
    }

    private EligibilityContext BuildEligibilityContext() => new()
    {
        Environment = _environment,
        Rules = _settings.Rules,
        AllowListOnly = _settings.Grouping.AllowListOnly,
        GroupedWindows = _manager.GroupedWindows,
        OwnWindowHandles = CollectOwnHandles(),
        YieldToNativeTabs = _settings.Grouping.YieldToNativeTabs,
    };

    private HashSet<nint> CollectOwnHandles()
    {
        var handles = new HashSet<nint> { _dispatcher.Handle };

        foreach (var rail in _rails.Values)
        {
            handles.Add(rail.Handle);
        }

        // 提示条本身也必须排除，否则拖拽时它会把自己认成拖放目标。
        if (_dropHint is { IsCreated: true })
        {
            handles.Add(_dropHint.Handle);
        }

        return handles;
    }

    private GroupSession? FindGroupByHandle(nint hwnd)
    {
        foreach (var group in _manager.Groups)
        {
            foreach (var tab in group.Tabs)
            {
                if (tab.Identity.HasSameHandle(hwnd))
                {
                    return group;
                }
            }
        }

        return null;
    }

    private static TabItem? FindTabByHandle(GroupSession group, nint hwnd)
    {
        foreach (var tab in group.Tabs)
        {
            if (tab.Identity.HasSameHandle(hwnd))
            {
                return tab;
            }
        }

        return null;
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[TabNest] 保存设置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 写组快照。
    /// 必须先于任何窗口写操作 —— 这是 TabNest 异常终止后能把外部窗口还原回原位的唯一保证。
    /// </summary>
    private void PersistSession()
    {
        try
        {
            _sessionStore.Save(SessionSnapshot.Capture(_manager.Groups, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            // 刻意捕获所有异常并继续。
            //
            // 这里原本只捕获 IO 类异常，结果 JSON 序列化抛出的 NotSupportedException
            // 直接穿透上去，把整个 Apply 流程打断在执行窗口指令之前 ——
            // 用户看到的是"分组完全没反应"，而真正的原因是一行写快照失败。
            //
            // 快照写不了意味着这次分组失去崩溃恢复能力，是降级；
            // 但因此彻底不给用户分组，是更糟的结果。所以记录后继续。
            FileLog.Error("写入会话快照失败，本次分组将失去崩溃恢复能力。", ex);
        }
    }

    /// <summary>
    /// 检查上次是否非正常退出，若是则还原残留的窗口。
    ///
    /// 快照的 IsDirty 在正常退出时会被清掉；仍为 true 说明上次没走完退出流程，
    /// 外部窗口很可能还停在被接管的位置上。
    /// </summary>
    private void RecoverPreviousSession()
    {
        var load = _sessionStore.Load();
        var snapshot = load.Value;

        if (!snapshot.NeedsRecovery)
        {
            return;
        }

        var actions = new List<Core.Grouping.WindowAction>();

        foreach (var group in snapshot.Groups)
        {
            foreach (var member in group.Members)
            {
                var identity = member.ToIdentity();

                // 校验窗口身份仍然有效：句柄会被回收复用，
                // 不验证就可能把一个毫不相干的新窗口挪到旧成员的位置上。
                var current = _enumerator.Describe(identity.Handle);
                if (current is null || current.Identity != identity)
                {
                    continue;
                }

                actions.Add(new Core.Grouping.RestoreWindowAction(identity, member.Snapshot));
                actions.Add(new Core.Grouping.SetTaskbarButtonAction(identity, true));
            }
        }

        Debug.WriteLine(
            $"[TabNest] 检测到上次未正常退出，正在还原 {actions.Count / 2} 个窗口。");

        if (actions.Count > 0)
        {
            _controller.ExecuteAsync(actions).GetAwaiter().GetResult();
        }

        ClearSession();
    }

    /// <summary>把快照标记为已清理。正常退出路径必须调用，否则下次启动会误判为崩溃。</summary>
    private void ClearSession()
    {
        try
        {
            _sessionStore.Save(SessionSnapshot.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[TabNest] 清理会话快照失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 顺序：先停止观察（不再产生新事件）→ 还原窗口 → 释放控制队列。
        _shutdown.Cancel();
        _observer.Dispose();
        _dropHint?.Dispose();

        DissolveEverything();

        _controller.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>取鼠标位置的小包装，避免在编排层直接触碰 Win32 结构体。</summary>
internal static class User32Cursor
{
    public static bool TryGet(out PixelPoint point)
    {
        if (CursorPosition.TryGet(out var x, out var y))
        {
            point = new PixelPoint(x, y);
            return true;
        }

        point = default;
        return false;
    }
}
