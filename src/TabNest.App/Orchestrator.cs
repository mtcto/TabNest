using System.Diagnostics;
using TabNest.App.Rail;
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

        switch (evt.Kind)
        {
            case WindowEventKind.Destroyed:
                OnWindowDestroyed(evt.Handle);
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

    /// <summary>承载窗口移动时，轨道必须跟着走，否则会脱离窗口悬在原地。</summary>
    private void OnLocationChanged(nint hwnd)
    {
        var group = FindGroupByHandle(hwnd);
        if (group?.ActiveTab is null || group.ActiveTab.Identity.Handle != hwnd)
        {
            return;
        }

        var bounds = WindowEnumerator.ReadVisibleBounds(hwnd);
        if (bounds.IsEmpty || bounds == group.Bounds)
        {
            return;
        }

        RefreshRail(group with { Bounds = bounds });
    }

    /// <summary>前台窗口变化时同步活动标签，让用户用 Alt+Tab 切换后轨道也保持一致。</summary>
    private void OnForegroundChanged(nint hwnd)
    {
        var group = FindGroupByHandle(hwnd);
        var tab = group is null ? null : FindTabByHandle(group, hwnd);

        if (group is null || tab is null || tab.State is TabState.Active)
        {
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
        RefreshLocationWatchList();
    }

    private void OnDragEnd(nint hwnd)
    {
        var dragged = _draggingWindow;
        _draggingWindow = 0;
        RefreshLocationWatchList();

        if (dragged == 0 || dragged != hwnd || !_settings.Enabled)
        {
            return;
        }

        if (!User32Cursor.TryGet(out var cursor))
        {
            return;
        }

        var targetHandle = WindowEnumerator.TopLevelWindowAt(cursor);
        if (targetHandle == 0 || targetHandle == dragged)
        {
            return;
        }

        TryMerge(dragged, targetHandle);
    }

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
            Debug.WriteLine($"[TabNest] 拒绝分组「{dragged.Title}」：{draggedResult.Explanation}");
            return;
        }

        // 目标已在组里 → 加入该组；否则用两个窗口新建一组。
        var targetGroup = _manager.FindGroupByWindow(target.Identity);

        if (targetGroup is not null)
        {
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
            Debug.WriteLine($"[TabNest] 拒绝分组「{target.Title}」：{targetResult.Explanation}");
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
        ]);

        if (result.Success && result.Group is not null)
        {
            Apply(result, result.Group.Id);
        }
    }

    // ------------------------------------------------------------------
    // 轨道交互
    // ------------------------------------------------------------------

    private void OnRailInteraction(RailInteraction interaction)
    {
        var groupId = interaction.GroupId;

        var result = interaction.Action switch
        {
            RailAction.ActivateTab => _manager.ActivateTab(groupId, interaction.Target),
            RailAction.CloseTab => _manager.CloseTab(groupId, interaction.Target),
            RailAction.DetachTab => _manager.DetachTab(groupId, interaction.Target),
            RailAction.ReorderTab => _manager.ReorderTab(
                groupId, interaction.Target, interaction.InsertionIndex),
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
            Debug.WriteLine($"[TabNest] 组操作失败：{result.Message}");
            return;
        }

        PersistSession();
        RefreshLocationWatchList();

        if (result.Actions.Count > 0)
        {
            var actions = result.Actions;
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

    private static void ReportFailures(IReadOnlyList<OperationResult> results)
    {
        foreach (var r in results)
        {
            if (!r.Success)
            {
                // 失败不能静默吞掉 —— UI 需要据此显示"重试/拆分/查看原因"。
                Debug.WriteLine($"[TabNest] 窗口操作失败：{r.Message}");
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
            CloseRail(group.Id);
            return;
        }

        var anchor = group.Bounds.IsEmpty
            ? WindowEnumerator.ReadVisibleBounds(active.Identity.Handle)
            : group.Bounds;

        if (anchor.IsEmpty)
        {
            return;
        }

        var dpi = MonitorLookup.DpiForWindow(active.Identity.Handle);
        var metrics = new RailMetrics().ScaleTo(dpi);

        var layout = TabRailLayoutEngine.Compute(
            group.Tabs,
            active.Identity,
            anchor,
            metrics,
            _settings.Appearance.CloseButton,
            showMenuButton: !_settings.Appearance.HideMenuButton);

        var rail = GetOrCreateRail(group.Id);

        rail.Update(new RailRenderState
        {
            Layout = layout,
            Tabs = group.Tabs,
            ActiveIdentity = active.Identity,
            Theme = RailTheme.Dark,
            Dpi = dpi,
        });
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
        var actions = _manager.DissolveAll();

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[TabNest] 写入会话快照失败：{ex.Message}");
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
