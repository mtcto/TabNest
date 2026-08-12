using TabNest.Core.Models;

namespace TabNest.Core.Grouping;

/// <summary>加入组的候选：窗口信息 + 加入前的原始状态快照。</summary>
public sealed record TabCandidate(WindowInfo Window, WindowSnapshot Snapshot);

/// <summary>
/// 标签组的纯状态机。
///
/// **本类不操作任何窗口**，只维护组模型并产出 <see cref="WindowAction"/> 指令列表。
/// 这是 TabNest 架构的核心边界，让全部组逻辑可以在没有窗口的环境下完整单元测试。
///
/// 维护的不变量：
///   1. 一个窗口同时只能属于一个组；
///   2. 组内存活标签中有且仅有一个处于 Active；
///   3. 固定标签始终排在非固定标签之前；
///   4. 存活成员少于 2 个的组自动解散并还原窗口；
///   5. 失败的操作不产生任何指令，不留下半执行的副作用。
///
/// 非线程安全：所有调用必须来自编排层的单一线程。
/// </summary>
public sealed class GroupSessionManager
{
    private readonly Dictionary<string, GroupSession> _groups = [];
    private readonly Dictionary<WindowIdentity, string> _windowToGroup = [];
    private readonly LinkedList<ClosedTabRecord> _closedHistory = new();
    private readonly TimeProvider _time;
    private readonly int _maxClosedHistory;
    private int _nextGroupNumber = 1;

    public GroupSessionManager(TimeProvider? timeProvider = null, int maxClosedHistory = 10)
    {
        _time = timeProvider ?? TimeProvider.System;
        _maxClosedHistory = Math.Max(1, maxClosedHistory);
    }

    /// <summary>任务栏按钮策略。变更后需调用 <see cref="RebuildTaskbarActions"/> 让已有组跟上。</summary>
    public TaskbarButtonPolicy TaskbarPolicy { get; set; } = TaskbarButtonPolicy.ShowAll;

    public IReadOnlyCollection<GroupSession> Groups => _groups.Values;

    public IReadOnlyCollection<ClosedTabRecord> ClosedHistory => [.. _closedHistory];

    public GroupSession? FindGroup(string groupId) =>
        _groups.GetValueOrDefault(groupId);

    public GroupSession? FindGroupByWindow(WindowIdentity identity) =>
        _windowToGroup.TryGetValue(identity, out var id) ? _groups.GetValueOrDefault(id) : null;

    public bool IsGrouped(WindowIdentity identity) => _windowToGroup.ContainsKey(identity);

    /// <summary>当前所有已分组窗口。供 EligibilityEvaluator 判定 AlreadyGrouped。</summary>
    public IReadOnlySet<WindowIdentity> GroupedWindows => _windowToGroup.Keys.ToHashSet();

    // ------------------------------------------------------------------
    // 创建与加入
    // ------------------------------------------------------------------

    /// <summary>
    /// 用给定成员创建新组。
    /// 组矩形取第一个成员的当前位置 —— 用户拖 A 到 B 时，B 是拖放目标，组应当留在 B 的位置上，
    /// 由调用方保证 members[0] 是拖放目标。
    /// </summary>
    /// <param name="members">组成员，第一个应为拖放目标。</param>
    /// <param name="contentBounds">
    /// 成员窗口应占据的矩形。为 null 时取拖放目标的当前位置。
    ///
    /// 调用方需要在这里**预留标签轨道的高度** —— 轨道贴在这个矩形的上方，
    /// 若直接用窗口原位置，贴着屏幕顶部的窗口会把轨道挤到屏幕外，
    /// 表现为"分组成功了但看不到分组栏"。
    /// </param>
    public GroupOperationResult CreateGroup(
        IReadOnlyList<TabCandidate> members,
        PixelRect? contentBounds = null)
    {
        ArgumentNullException.ThrowIfNull(members);

        if (members.Count < 2)
        {
            return GroupOperationResult.Fail(
                GroupOperationError.NotEnoughMembers,
                "创建标签组至少需要两个窗口。");
        }

        foreach (var member in members)
        {
            if (IsGrouped(member.Window.Identity))
            {
                return GroupOperationResult.Fail(
                    GroupOperationError.AlreadyGrouped,
                    $"窗口「{member.Window.Title}」已在另一个标签组中。");
            }

            if (!member.Snapshot.IsRestorable)
            {
                return GroupOperationResult.Fail(
                    GroupOperationError.SnapshotNotRestorable,
                    $"无法记录窗口「{member.Window.Title}」的原始状态，拒绝将它带入无法还原的状态。");
            }
        }

        var now = _time.GetUtcNow();
        var anchor = members[0];
        var groupId = $"g{_nextGroupNumber++}";

        var tabs = new List<TabItem>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            tabs.Add(CreateTab(members[i], now, isActive: i == 0));
        }

        var group = new GroupSession
        {
            Id = groupId,
            Tabs = tabs,
            State = GroupState.Stable,
            Bounds = contentBounds ?? anchor.Window.Bounds,
            MonitorDeviceName = anchor.Window.MonitorDeviceName,
            CreatedAt = now,
        };

        _groups[groupId] = group;
        foreach (var tab in tabs)
        {
            _windowToGroup[tab.Identity] = groupId;
        }

        return GroupOperationResult.Ok(group, BuildLayoutActions(group));
    }

    /// <summary>把一个窗口加入已有组。</summary>
    public GroupOperationResult AddTab(string groupId, TabCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        if (IsGrouped(candidate.Window.Identity))
        {
            return GroupOperationResult.Fail(
                GroupOperationError.AlreadyGrouped,
                $"窗口「{candidate.Window.Title}」已在另一个标签组中。");
        }

        if (!candidate.Snapshot.IsRestorable)
        {
            return GroupOperationResult.Fail(
                GroupOperationError.SnapshotNotRestorable,
                $"无法记录窗口「{candidate.Window.Title}」的原始状态，拒绝将它带入无法还原的状态。");
        }

        var now = _time.GetUtcNow();
        var tabs = new List<TabItem>(group.Tabs) { CreateTab(candidate, now, isActive: false) };

        group = group with { Tabs = Normalize(tabs) };
        _groups[groupId] = group;
        _windowToGroup[candidate.Window.Identity] = groupId;

        return GroupOperationResult.Ok(group, BuildLayoutActions(group));
    }

    // ------------------------------------------------------------------
    // 切换与排序
    // ------------------------------------------------------------------

    /// <summary>
    /// 切换活动标签。
    ///
    /// 采用层叠置顶：不隐藏、不最小化其他成员，只把目标窗口抬到 Z 序顶部并转移焦点。
    /// 因此指令只有对齐 + 激活两类，切换延迟不受窗口动画影响。
    /// </summary>
    public GroupOperationResult ActivateTab(string groupId, WindowIdentity identity)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        var target = group.FindTab(identity);
        if (target is null || !target.IsLive)
        {
            return GroupOperationResult.Fail(
                GroupOperationError.TabNotFound,
                "该标签已不在这个组中。");
        }

        if (target.State is TabState.Active)
        {
            return GroupOperationResult.Ok(group);
        }

        var tabs = new List<TabItem>(group.Tabs.Count);
        foreach (var tab in group.Tabs)
        {
            if (!tab.IsLive)
            {
                tabs.Add(tab);
            }
            else if (tab.Identity == identity)
            {
                // 无响应的窗口同样可以是活动标签 —— 响应性由 IsResponding 单独承载，不影响活动性。
                tabs.Add(tab with { State = TabState.Active });
            }
            else if (tab.State is TabState.Active)
            {
                tabs.Add(tab with { State = TabState.Inactive });
            }
            else
            {
                tabs.Add(tab);
            }
        }

        group = group with { Tabs = tabs, State = GroupState.Stable, LastError = null };
        _groups[groupId] = group;

        return GroupOperationResult.Ok(group, BuildLayoutActions(group));
    }

    /// <summary>组内重排标签。固定标签与非固定标签不得互相穿插。</summary>
    public GroupOperationResult ReorderTab(string groupId, WindowIdentity identity, int newIndex)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        var oldIndex = group.IndexOf(identity);
        if (oldIndex < 0)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该标签不在这个组中。");
        }

        var tabs = new List<TabItem>(group.Tabs);
        var moving = tabs[oldIndex];
        tabs.RemoveAt(oldIndex);

        newIndex = Math.Clamp(newIndex, 0, tabs.Count);
        tabs.Insert(newIndex, moving);

        group = group with { Tabs = Normalize(tabs) };
        _groups[groupId] = group;

        // 重排只改显示顺序，不触碰任何窗口。
        return GroupOperationResult.Ok(group);
    }

    // ------------------------------------------------------------------
    // 拆分、关闭、解散
    // ------------------------------------------------------------------

    /// <summary>把标签拆分为独立窗口，并还原其加入组之前的状态。</summary>
    public GroupOperationResult DetachTab(string groupId, WindowIdentity identity)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        if (group.IsLocked)
        {
            return GroupOperationResult.Fail(
                GroupOperationError.GroupLocked,
                "标签组已锁定。请先解锁再拆分标签。");
        }

        var tab = group.FindTab(identity);
        if (tab is null)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该标签不在这个组中。");
        }

        var actions = new List<WindowAction>
        {
            new RestoreWindowAction(identity, tab.Snapshot),
            new SetTaskbarButtonAction(identity, true),
        };

        return RemoveTab(group, identity, actions);
    }

    /// <summary>
    /// 把标签从组中移除，但**保持窗口当前位置不动**。
    ///
    /// 用于"用户自己把窗口拖出了分组"这个场景：窗口已经在用户想要的位置上，
    /// 再按快照弹回分组前的旧位置是在跟用户较劲。
    /// 与 <see cref="DetachTab"/> 的区别就在于不产生还原指令。
    /// </summary>
    public GroupOperationResult DetachTabInPlace(string groupId, WindowIdentity identity)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        if (group.FindTab(identity) is null)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该标签不在这个组中。");
        }

        // 任务栏按钮仍要还原：策略可能隐藏过它，不还原窗口就再也回不到任务栏。
        // 圆角也要改回来：分组期间被改成了直角以便与分组条拼合。
        return RemoveTab(
            group,
            identity,
            [
                new SetTaskbarButtonAction(identity, true),
                new RestoreWindowCornersAction(identity),
            ]);
    }

    /// <summary>
    /// 请求关闭标签对应的窗口。
    ///
    /// 只发出关闭请求，**不立即移除标签**：应用有权拒绝关闭或弹出保存提示，
    /// 此时窗口仍然存在，提前移除标签会让用户看到标签消失但窗口还在。
    /// 标签的实际移除由 <see cref="OnWindowDestroyed"/> 在收到销毁事件后完成。
    /// </summary>
    public GroupOperationResult CloseTab(string groupId, WindowIdentity identity)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        if (group.IsLocked)
        {
            return GroupOperationResult.Fail(
                GroupOperationError.GroupLocked,
                "标签组已锁定。请先解锁再关闭标签。");
        }

        if (group.FindTab(identity) is null)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该标签不在这个组中。");
        }

        return GroupOperationResult.Ok(group, [new CloseWindowAction(identity)]);
    }

    /// <summary>关闭除指定标签外的其他标签。固定标签不受影响。</summary>
    public GroupOperationResult CloseOtherTabs(string groupId, WindowIdentity keep) =>
        CloseMatching(groupId, tab => tab.Identity != keep && !tab.IsPinned);

    /// <summary>关闭指定标签左侧的全部标签。固定标签不受影响。</summary>
    public GroupOperationResult CloseTabsToLeft(string groupId, WindowIdentity pivot) =>
        CloseByPosition(groupId, pivot, toLeft: true);

    /// <summary>关闭指定标签右侧的全部标签。固定标签不受影响。</summary>
    public GroupOperationResult CloseTabsToRight(string groupId, WindowIdentity pivot) =>
        CloseByPosition(groupId, pivot, toLeft: false);

    /// <summary>关闭组内全部窗口。</summary>
    public GroupOperationResult CloseAllTabs(string groupId) =>
        CloseMatching(groupId, _ => true);

    /// <summary>解散组：还原所有成员窗口，组不复存在。</summary>
    public GroupOperationResult DissolveGroup(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        var actions = BuildRestoreActions(group);
        RemoveGroup(group);

        return GroupOperationResult.Ok(null, actions);
    }

    /// <summary>
    /// 解散所有组并还原全部窗口。
    /// 用于关闭全局主开关、托盘安全退出，以及崩溃恢复后的清理。
    /// </summary>
    public IReadOnlyList<WindowAction> DissolveAll()
    {
        var actions = new List<WindowAction>();

        foreach (var group in _groups.Values.ToList())
        {
            actions.AddRange(BuildRestoreActions(group));
        }

        _groups.Clear();
        _windowToGroup.Clear();

        return actions;
    }

    // ------------------------------------------------------------------
    // 标签属性
    // ------------------------------------------------------------------

    /// <summary>
    /// 更新组占据的矩形。
    /// 拖动分组条时必须调用 —— 只把新矩形用在局部变量上而不写回，
    /// 下一次鼠标移动仍会基于旧矩形计算，窗口被反复拉回原处，表现为"拖不动"。
    /// </summary>
    public GroupOperationResult SetGroupBounds(string groupId, PixelRect bounds) =>
        UpdateGroup(groupId, g => g with { Bounds = bounds });

    public GroupOperationResult SetGroupLocked(string groupId, bool locked) =>
        UpdateGroup(groupId, g => g with { IsLocked = locked });

    public GroupOperationResult RenameGroup(string groupId, string? name) =>
        UpdateGroup(groupId, g => g with { Name = string.IsNullOrWhiteSpace(name) ? null : name });

    public GroupOperationResult SetGroupAccent(string groupId, uint? argb) =>
        UpdateGroup(groupId, g => g with { AccentColor = argb });

    public GroupOperationResult RenameTab(string groupId, WindowIdentity identity, string? customTitle) =>
        UpdateTab(groupId, identity, t => t with
        {
            CustomTitle = string.IsNullOrWhiteSpace(customTitle) ? null : customTitle,
        });

    public GroupOperationResult SetTabAccent(string groupId, WindowIdentity identity, uint? argb) =>
        UpdateTab(groupId, identity, t => t with { AccentColor = argb });

    /// <summary>固定或取消固定标签。固定后会被重新排序到非固定标签之前。</summary>
    public GroupOperationResult SetTabPinned(string groupId, WindowIdentity identity, bool pinned)
    {
        var result = UpdateTab(groupId, identity, t => t with { IsPinned = pinned });
        if (!result.Success || result.Group is null)
        {
            return result;
        }

        var group = result.Group with { Tabs = Normalize([.. result.Group.Tabs]) };
        _groups[groupId] = group;

        return GroupOperationResult.Ok(group);
    }

    // ------------------------------------------------------------------
    // 来自观察层的事实更新
    // ------------------------------------------------------------------

    /// <summary>
    /// 窗口已销毁。
    /// 这既可能是 TabNest 请求关闭的结果，也可能是用户直接关掉了窗口 ——
    /// 两种情况都记入关闭历史，因为用户都可能想撤销。
    /// </summary>
    public GroupOperationResult OnWindowDestroyed(WindowIdentity identity)
    {
        var group = FindGroupByWindow(identity);
        if (group is null)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该窗口不在任何标签组中。");
        }

        var tab = group.FindTab(identity);
        if (tab is not null)
        {
            RecordClosed(group, tab);
        }

        // 窗口已经不存在了，不能再对它发指令。
        return RemoveTab(group, identity, actions: []);
    }

    /// <summary>更新标签标题。窗口标题会随打开的文档变化，由 NAMECHANGE 事件驱动。</summary>
    public GroupOperationResult OnWindowTitleChanged(WindowIdentity identity, string title)
    {
        var group = FindGroupByWindow(identity);
        return group is null
            ? GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该窗口不在任何标签组中。")
            : UpdateTab(group.Id, identity, t => t with { Title = title });
    }

    /// <summary>
    /// 更新窗口的响应状态。
    /// 无响应的成员保留在组内并显示为降级态，而不是被移除 —— 应用往往会自行恢复。
    /// </summary>
    public GroupOperationResult OnWindowResponsivenessChanged(WindowIdentity identity, bool isResponding)
    {
        var group = FindGroupByWindow(identity);
        if (group is null)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该窗口不在任何标签组中。");
        }

        var tab = group.FindTab(identity);
        if (tab is null)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该标签不在这个组中。");
        }

        if (tab.IsResponding == isResponding)
        {
            return GroupOperationResult.Ok(group);
        }

        // 只改 IsResponding，不动 State —— 活动标签卡住时依然是活动标签。
        var result = UpdateTab(group.Id, identity, t => t with { IsResponding = isResponding });
        if (!result.Success || result.Group is null)
        {
            return result;
        }

        // 有成员失去响应时，组整体进入降级态，UI 必须如实呈现。
        var updated = result.Group;
        var anyUnresponsive = false;
        foreach (var t in updated.LiveTabs)
        {
            if (!t.IsResponding)
            {
                anyUnresponsive = true;
                break;
            }
        }

        updated = updated with
        {
            State = anyUnresponsive ? GroupState.Degraded : GroupState.Stable,
        };
        _groups[group.Id] = updated;

        return GroupOperationResult.Ok(updated);
    }

    // ------------------------------------------------------------------
    // 关闭历史
    // ------------------------------------------------------------------

    public ClosedTabRecord? PeekLastClosed() => _closedHistory.First?.Value;

    /// <summary>
    /// 取出最近一条关闭记录。
    /// 领域层无法凭空复活一个已销毁的窗口 —— 它只交出重建所需的信息，
    /// 由编排层决定是重新启动应用还是提示用户手动打开。
    /// </summary>
    public ClosedTabRecord? PopLastClosed()
    {
        var first = _closedHistory.First;
        if (first is null)
        {
            return null;
        }

        _closedHistory.RemoveFirst();
        return first.Value;
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    private static TabItem CreateTab(TabCandidate candidate, DateTimeOffset now, bool isActive) => new()
    {
        Identity = candidate.Window.Identity,
        Title = candidate.Window.Title,
        ProcessName = candidate.Window.ProcessName,
        ClassName = candidate.Window.ClassName,
        ProcessPath = candidate.Window.ProcessPath,
        Snapshot = candidate.Snapshot,
        State = isActive ? TabState.Active : TabState.Inactive,
        JoinedAt = now,
    };

    /// <summary>把固定标签稳定地排到前面，保持各自的相对顺序。</summary>
    private static List<TabItem> Normalize(List<TabItem> tabs)
    {
        var pinned = new List<TabItem>();
        var rest = new List<TabItem>();

        foreach (var tab in tabs)
        {
            (tab.IsPinned ? pinned : rest).Add(tab);
        }

        pinned.AddRange(rest);
        return pinned;
    }

    /// <summary>
    /// 生成让组内窗口就位的指令。
    ///
    /// 顺序有意义：先把非活动成员对齐并压到下层，最后再抬起并激活目标窗口。
    /// 反过来做会导致刚激活的窗口又被后续的对齐操作压下去，表现为切换后焦点闪烁。
    /// </summary>
    private List<WindowAction> BuildLayoutActions(GroupSession group)
    {
        var actions = new List<WindowAction>();
        var active = group.ActiveTab;

        foreach (var tab in group.LiveTabs)
        {
            if (tab.Identity == active?.Identity)
            {
                continue;
            }

            actions.Add(new AlignWindowAction(tab.Identity, group.Bounds));
            actions.Add(new LowerWindowAction(tab.Identity));
            actions.Add(new SetTaskbarButtonAction(
                tab.Identity,
                TaskbarPolicy is not TaskbarButtonPolicy.ActiveOnly));
        }

        if (active is not null)
        {
            actions.Add(new AlignWindowAction(active.Identity, group.Bounds));
            actions.Add(new SetTaskbarButtonAction(active.Identity, true));
            actions.Add(new ActivateWindowAction(active.Identity));
        }

        return actions;
    }

    private static List<WindowAction> BuildRestoreActions(GroupSession group)
    {
        var actions = new List<WindowAction>();

        foreach (var tab in group.Tabs)
        {
            if (!tab.IsLive)
            {
                continue;
            }

            actions.Add(new RestoreWindowAction(tab.Identity, tab.Snapshot));
            actions.Add(new SetTaskbarButtonAction(tab.Identity, true));
        }

        return actions;
    }

    /// <summary>
    /// 从组中移除一个标签，必要时解散整个组。
    /// <paramref name="actions"/> 是针对被移除标签本身的指令，会排在解散指令之前。
    /// </summary>
    private GroupOperationResult RemoveTab(
        GroupSession group,
        WindowIdentity identity,
        List<WindowAction> actions)
    {
        var tabs = new List<TabItem>(group.Tabs.Count);
        var removedWasActive = false;

        foreach (var tab in group.Tabs)
        {
            if (tab.Identity == identity)
            {
                removedWasActive = tab.State is TabState.Active;
                continue;
            }

            tabs.Add(tab);
        }

        _windowToGroup.Remove(identity);
        group = group with { Tabs = tabs };

        // 只剩一个成员的组没有存在价值，连同它一起还原。
        if (group.ShouldDissolve)
        {
            actions.AddRange(BuildRestoreActions(group));
            RemoveGroup(group);
            return GroupOperationResult.Ok(null, actions);
        }

        if (removedWasActive)
        {
            group = PromoteFirstLive(group);
        }

        _groups[group.Id] = group;
        actions.AddRange(BuildLayoutActions(group));

        return GroupOperationResult.Ok(group, actions);
    }

    /// <summary>活动标签消失后，把第一个存活标签提升为活动，维持"有且仅有一个 Active"的不变量。</summary>
    private static GroupSession PromoteFirstLive(GroupSession group)
    {
        var tabs = new List<TabItem>(group.Tabs);
        var promoted = false;

        for (var i = 0; i < tabs.Count; i++)
        {
            if (!promoted && tabs[i].IsLive)
            {
                tabs[i] = tabs[i] with { State = TabState.Active };
                promoted = true;
            }
        }

        return group with { Tabs = tabs };
    }

    private void RemoveGroup(GroupSession group)
    {
        foreach (var tab in group.Tabs)
        {
            _windowToGroup.Remove(tab.Identity);
        }

        _groups.Remove(group.Id);
    }

    private GroupOperationResult CloseMatching(string groupId, Func<TabItem, bool> predicate)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        if (group.IsLocked)
        {
            return GroupOperationResult.Fail(
                GroupOperationError.GroupLocked,
                "标签组已锁定。请先解锁再关闭标签。");
        }

        var actions = new List<WindowAction>();
        foreach (var tab in group.LiveTabs)
        {
            if (predicate(tab))
            {
                actions.Add(new CloseWindowAction(tab.Identity));
            }
        }

        return GroupOperationResult.Ok(group, actions);
    }

    private GroupOperationResult CloseByPosition(string groupId, WindowIdentity pivot, bool toLeft)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        var pivotIndex = group.IndexOf(pivot);
        if (pivotIndex < 0)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该标签不在这个组中。");
        }

        var tabs = group.Tabs;
        return CloseMatching(groupId, tab =>
        {
            if (tab.IsPinned)
            {
                return false;
            }

            var index = -1;
            for (var i = 0; i < tabs.Count; i++)
            {
                if (tabs[i].Identity == tab.Identity)
                {
                    index = i;
                    break;
                }
            }

            return toLeft ? index < pivotIndex : index > pivotIndex;
        });
    }

    private GroupOperationResult UpdateGroup(string groupId, Func<GroupSession, GroupSession> mutate)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        var updated = mutate(group);
        _groups[groupId] = updated;

        return GroupOperationResult.Ok(updated);
    }

    private GroupOperationResult UpdateTab(
        string groupId,
        WindowIdentity identity,
        Func<TabItem, TabItem> mutate)
    {
        if (!_groups.TryGetValue(groupId, out var group))
        {
            return GroupOperationResult.Fail(GroupOperationError.GroupNotFound, "标签组不存在。");
        }

        var index = group.IndexOf(identity);
        if (index < 0)
        {
            return GroupOperationResult.Fail(GroupOperationError.TabNotFound, "该标签不在这个组中。");
        }

        var tabs = new List<TabItem>(group.Tabs);
        tabs[index] = mutate(tabs[index]);

        var updated = group with { Tabs = tabs };
        _groups[groupId] = updated;

        return GroupOperationResult.Ok(updated);
    }

    private void RecordClosed(GroupSession group, TabItem tab)
    {
        _closedHistory.AddFirst(new ClosedTabRecord
        {
            Title = tab.DisplayTitle,
            ProcessName = tab.ProcessName,
            ProcessPath = tab.ProcessPath,
            Snapshot = tab.Snapshot,
            GroupId = group.Id,
            TabIndex = group.IndexOf(tab.Identity),
            ClosedAt = _time.GetUtcNow(),
        });

        while (_closedHistory.Count > _maxClosedHistory)
        {
            _closedHistory.RemoveLast();
        }
    }
}
