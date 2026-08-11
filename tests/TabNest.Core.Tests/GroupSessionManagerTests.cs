using TabNest.Core.Grouping;
using TabNest.Core.Models;

namespace TabNest.Core.Tests;

/// <summary>
/// 组状态机的不变量测试。
///
/// 这些不变量一旦被破坏，用户的窗口就会进入错乱或无法还原的状态，
/// 因此它们是整个产品最该被钉死的部分。
/// </summary>
public sealed class GroupSessionManagerTests
{
    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    [Fact]
    public void 创建组至少需要两个成员()
    {
        var manager = new GroupSessionManager();

        var result = manager.CreateGroup([TestData.Candidate(1)]);

        Assert.False(result.Success);
        Assert.Equal(GroupOperationError.NotEnoughMembers, result.Error);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void 快照不可还原时拒绝创建组()
    {
        var manager = new GroupSessionManager();
        var broken = new TabCandidate(
            TestData.Window(1),
            TestData.Snapshot(restorable: false));

        var result = manager.CreateGroup([broken, TestData.Candidate(2)]);

        Assert.False(result.Success);
        Assert.Equal(GroupOperationError.SnapshotNotRestorable, result.Error);
    }

    [Fact]
    public void 失败的操作不产生任何指令()
    {
        var manager = new GroupSessionManager();

        var result = manager.CreateGroup([TestData.Candidate(1)]);

        // 这条约束保证失败不会留下半执行的副作用。
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void 创建组后第一个成员成为活动标签()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        var group = manager.FindGroup(groupId)!;

        Assert.Equal(TestData.Id(1), group.ActiveTab!.Identity);
    }

    [Fact]
    public void 窗口不能同时属于两个组()
    {
        var manager = new GroupSessionManager();
        manager.CreateGroup([TestData.Candidate(1), TestData.Candidate(2)]);

        var result = manager.CreateGroup([TestData.Candidate(1), TestData.Candidate(3)]);

        Assert.False(result.Success);
        Assert.Equal(GroupOperationError.AlreadyGrouped, result.Error);
    }

    // ------------------------------------------------------------------
    // 活动标签唯一性 —— 最核心的不变量
    // ------------------------------------------------------------------

    [Fact]
    public void 切换后有且仅有一个活动标签()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        manager.ActivateTab(groupId, TestData.Id(3));

        var group = manager.FindGroup(groupId)!;
        Assert.Equal(1, group.Tabs.Count(t => t.State is TabState.Active));
        Assert.Equal(TestData.Id(3), group.ActiveTab!.Identity);
    }

    [Fact]
    public void 反复切换始终维持活动标签唯一()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        for (var round = 0; round < 3; round++)
        {
            for (var handle = 1; handle <= 4; handle++)
            {
                manager.ActivateTab(groupId, TestData.Id(handle));
                var group = manager.FindGroup(groupId)!;
                Assert.Equal(1, group.Tabs.Count(t => t.State is TabState.Active));
            }
        }
    }

    [Fact]
    public void 活动标签被移除后自动提升另一个标签()
    {
        var (manager, groupId) = TestData.GroupOf(3);
        var active = manager.FindGroup(groupId)!.ActiveTab!.Identity;

        manager.DetachTab(groupId, active);

        var group = manager.FindGroup(groupId)!;
        Assert.NotNull(group.ActiveTab);
        Assert.Equal(1, group.Tabs.Count(t => t.State is TabState.Active));
    }

    // ------------------------------------------------------------------
    // 拆分与解散
    // ------------------------------------------------------------------

    [Fact]
    public void 拆分标签后成员数减一()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        manager.DetachTab(groupId, TestData.Id(2));

        Assert.Equal(2, manager.FindGroup(groupId)!.Tabs.Count);
    }

    [Fact]
    public void 拆分时必须还原窗口原始状态()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        var result = manager.DetachTab(groupId, TestData.Id(2));

        var restore = Assert.Single(
            result.Actions.OfType<RestoreWindowAction>(),
            a => a.Target == TestData.Id(2));
        Assert.True(restore.Snapshot.IsRestorable);
    }

    [Fact]
    public void 拆分后必须还原任务栏按钮()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        var result = manager.DetachTab(groupId, TestData.Id(2));

        // 任务栏策略隐藏过按钮的话，拆分时不还原就会留下一个再也回不来的窗口。
        Assert.Contains(
            result.Actions.OfType<SetTaskbarButtonAction>(),
            a => a.Target == TestData.Id(2) && a.Visible);
    }

    [Fact]
    public void 只剩一个成员时组自动解散()
    {
        var (manager, groupId) = TestData.GroupOf(2);

        var result = manager.DetachTab(groupId, TestData.Id(1));

        Assert.True(result.Success);
        Assert.Null(result.Group);
        Assert.Null(manager.FindGroup(groupId));
    }

    [Fact]
    public void 组解散时还原全部剩余成员()
    {
        var (manager, groupId) = TestData.GroupOf(2);

        var result = manager.DetachTab(groupId, TestData.Id(1));

        // 被拆分的和被顺带解散的，两个窗口都必须还原。
        var restored = result.Actions.OfType<RestoreWindowAction>()
            .Select(a => a.Target)
            .ToHashSet();
        Assert.Contains(TestData.Id(1), restored);
        Assert.Contains(TestData.Id(2), restored);
    }

    [Fact]
    public void 解散所有组时还原每一个窗口()
    {
        var manager = new GroupSessionManager();
        manager.CreateGroup([TestData.Candidate(1), TestData.Candidate(2)]);
        manager.CreateGroup([TestData.Candidate(3), TestData.Candidate(4)]);

        var actions = manager.DissolveAll();

        var restored = actions.OfType<RestoreWindowAction>().Select(a => a.Target).ToHashSet();
        Assert.Equal(4, restored.Count);
        Assert.Empty(manager.Groups);
    }

    // ------------------------------------------------------------------
    // 锁定
    // ------------------------------------------------------------------

    [Fact]
    public void 锁定的组拒绝拆分()
    {
        var (manager, groupId) = TestData.GroupOf(3);
        manager.SetGroupLocked(groupId, true);

        var result = manager.DetachTab(groupId, TestData.Id(2));

        Assert.False(result.Success);
        Assert.Equal(GroupOperationError.GroupLocked, result.Error);
        Assert.Equal(3, manager.FindGroup(groupId)!.Tabs.Count);
    }

    [Fact]
    public void 锁定的组拒绝关闭标签()
    {
        var (manager, groupId) = TestData.GroupOf(3);
        manager.SetGroupLocked(groupId, true);

        var result = manager.CloseTab(groupId, TestData.Id(2));

        Assert.False(result.Success);
        Assert.Equal(GroupOperationError.GroupLocked, result.Error);
        Assert.Empty(result.Actions);
    }

    [Fact]
    public void 解锁后恢复正常操作()
    {
        var (manager, groupId) = TestData.GroupOf(3);
        manager.SetGroupLocked(groupId, true);
        manager.SetGroupLocked(groupId, false);

        Assert.True(manager.DetachTab(groupId, TestData.Id(2)).Success);
    }

    // ------------------------------------------------------------------
    // 关闭语义
    // ------------------------------------------------------------------

    [Fact]
    public void 关闭标签只发出请求不立即移除()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        var result = manager.CloseTab(groupId, TestData.Id(2));

        // 应用有权拒绝关闭或弹出保存提示，此时窗口还在，标签就不该消失。
        Assert.Single(result.Actions.OfType<CloseWindowAction>());
        Assert.Equal(3, manager.FindGroup(groupId)!.Tabs.Count);
    }

    [Fact]
    public void 关闭其他标签保留指定标签()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        var result = manager.CloseOtherTabs(groupId, TestData.Id(2));

        var closing = result.Actions.OfType<CloseWindowAction>().Select(a => a.Target).ToHashSet();
        Assert.Equal(3, closing.Count);
        Assert.DoesNotContain(TestData.Id(2), closing);
    }

    [Fact]
    public void 关闭其他标签不影响固定标签()
    {
        var (manager, groupId) = TestData.GroupOf(4);
        manager.SetTabPinned(groupId, TestData.Id(4), true);

        var result = manager.CloseOtherTabs(groupId, TestData.Id(2));

        var closing = result.Actions.OfType<CloseWindowAction>().Select(a => a.Target).ToHashSet();
        Assert.DoesNotContain(TestData.Id(4), closing);
    }

    [Fact]
    public void 关闭左侧标签()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        var result = manager.CloseTabsToRight(groupId, TestData.Id(2));

        var closing = result.Actions.OfType<CloseWindowAction>().Select(a => a.Target).ToHashSet();
        Assert.Equal([TestData.Id(3), TestData.Id(4)], closing);
    }

    [Fact]
    public void 关闭右侧标签()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        var result = manager.CloseTabsToLeft(groupId, TestData.Id(3));

        var closing = result.Actions.OfType<CloseWindowAction>().Select(a => a.Target).ToHashSet();
        Assert.Equal([TestData.Id(1), TestData.Id(2)], closing);
    }

    // ------------------------------------------------------------------
    // 固定标签排序
    // ------------------------------------------------------------------

    [Fact]
    public void 固定标签排到最前()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        manager.SetTabPinned(groupId, TestData.Id(3), true);

        Assert.Equal(TestData.Id(3), manager.FindGroup(groupId)!.Tabs[0].Identity);
    }

    [Fact]
    public void 重排不会让非固定标签插到固定标签之前()
    {
        var (manager, groupId) = TestData.GroupOf(4);
        manager.SetTabPinned(groupId, TestData.Id(1), true);

        manager.ReorderTab(groupId, TestData.Id(4), 0);

        var tabs = manager.FindGroup(groupId)!.Tabs;
        Assert.True(tabs[0].IsPinned);
    }

    [Fact]
    public void 重排不产生任何窗口指令()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        var result = manager.ReorderTab(groupId, TestData.Id(1), 3);

        // 重排只改显示顺序，不该碰任何窗口。
        Assert.Empty(result.Actions);
    }

    // ------------------------------------------------------------------
    // 窗口销毁与幽灵标签
    // ------------------------------------------------------------------

    [Fact]
    public void 窗口销毁后标签被移除()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        manager.OnWindowDestroyed(TestData.Id(2));

        Assert.Null(manager.FindGroup(groupId)!.FindTab(TestData.Id(2)));
    }

    [Fact]
    public void 不对已销毁的窗口发出任何指令()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        var result = manager.OnWindowDestroyed(TestData.Id(2));

        // 窗口已经不存在，任何针对它的指令都会失败并产生噪声日志。
        Assert.DoesNotContain(result.Actions, a => a.Target == TestData.Id(2));
    }

    [Fact]
    public void 窗口销毁记入关闭历史()
    {
        var (manager, _) = TestData.GroupOf(3);

        manager.OnWindowDestroyed(TestData.Id(2));

        var record = manager.PeekLastClosed();
        Assert.NotNull(record);
        Assert.Equal("窗口2", record.Title);
        Assert.True(record.CanRelaunch);
    }

    [Fact]
    public void 关闭历史保留最近若干条()
    {
        var manager = new GroupSessionManager(maxClosedHistory: 2);
        var members = Enumerable.Range(1, 4)
            .Select(i => TestData.Candidate(i, $"窗口{i}"))
            .ToList();
        manager.CreateGroup(members);

        manager.OnWindowDestroyed(TestData.Id(1));
        manager.OnWindowDestroyed(TestData.Id(2));
        manager.OnWindowDestroyed(TestData.Id(3));

        Assert.Equal(2, manager.ClosedHistory.Count);
        Assert.Equal("窗口3", manager.PeekLastClosed()!.Title);
    }

    [Fact]
    public void 取出关闭记录后从历史中消失()
    {
        var (manager, _) = TestData.GroupOf(3);
        manager.OnWindowDestroyed(TestData.Id(2));

        var popped = manager.PopLastClosed();

        Assert.NotNull(popped);
        Assert.Empty(manager.ClosedHistory);
    }

    // ------------------------------------------------------------------
    // 无响应窗口
    // ------------------------------------------------------------------

    [Fact]
    public void 无响应窗口保留在组内并标记降级()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        var result = manager.OnWindowResponsivenessChanged(TestData.Id(2), isResponding: false);

        // 应用常会自行恢复，直接移除标签会让用户丢失上下文。
        var tab = result.Group!.FindTab(TestData.Id(2))!;
        Assert.False(tab.IsResponding);
        Assert.Equal(GroupState.Degraded, result.Group.State);
    }

    [Fact]
    public void 活动窗口失去响应时仍然是活动标签()
    {
        var (manager, groupId) = TestData.GroupOf(3);
        var active = manager.FindGroup(groupId)!.ActiveTab!.Identity;

        var result = manager.OnWindowResponsivenessChanged(active, isResponding: false);

        // 响应性与活动性正交。把二者混在一个字段里会让组在成员卡住时失去活动标签。
        Assert.Equal(active, result.Group!.ActiveTab!.Identity);
        Assert.False(result.Group.FindTab(active)!.IsResponding);
    }

    [Fact]
    public void 窗口恢复响应后组回到稳定态()
    {
        var (manager, groupId) = TestData.GroupOf(3);
        manager.OnWindowResponsivenessChanged(TestData.Id(2), isResponding: false);

        var result = manager.OnWindowResponsivenessChanged(TestData.Id(2), isResponding: true);

        Assert.Equal(GroupState.Stable, result.Group!.State);
    }

    [Fact]
    public void 活动窗口失去响应再恢复仍是活动标签()
    {
        var (manager, groupId) = TestData.GroupOf(3);
        var active = manager.FindGroup(groupId)!.ActiveTab!.Identity;

        manager.OnWindowResponsivenessChanged(active, isResponding: false);
        var result = manager.OnWindowResponsivenessChanged(active, isResponding: true);

        Assert.Equal(active, result.Group!.ActiveTab!.Identity);
        Assert.Equal(1, result.Group.Tabs.Count(t => t.State is TabState.Active));
    }

    // ------------------------------------------------------------------
    // 任务栏策略
    // ------------------------------------------------------------------

    [Fact]
    public void 默认策略保留所有成员的任务栏按钮()
    {
        var (manager, groupId) = TestData.GroupOf(3);

        var result = manager.ActivateTab(groupId, TestData.Id(2));

        Assert.All(
            result.Actions.OfType<SetTaskbarButtonAction>(),
            a => Assert.True(a.Visible));
    }

    [Fact]
    public void 仅活动窗口策略隐藏其他成员的任务栏按钮()
    {
        var manager = new GroupSessionManager
        {
            TaskbarPolicy = TaskbarButtonPolicy.ActiveOnly,
        };
        manager.CreateGroup([TestData.Candidate(1), TestData.Candidate(2), TestData.Candidate(3)]);
        var groupId = manager.Groups.Single().Id;

        var result = manager.ActivateTab(groupId, TestData.Id(2));

        var buttons = result.Actions.OfType<SetTaskbarButtonAction>()
            .ToDictionary(a => a.Target, a => a.Visible);
        Assert.True(buttons[TestData.Id(2)]);
        Assert.False(buttons[TestData.Id(1)]);
        Assert.False(buttons[TestData.Id(3)]);
    }

    // ------------------------------------------------------------------
    // 指令顺序
    // ------------------------------------------------------------------

    [Fact]
    public void 激活指令必须排在所有对齐指令之后()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        var result = manager.ActivateTab(groupId, TestData.Id(3));

        // 顺序反了会导致刚激活的窗口被后续对齐操作压下去，表现为切换后焦点闪烁。
        var activateIndex = result.Actions.ToList().FindIndex(a => a is ActivateWindowAction);
        var lastAlignIndex = result.Actions.ToList().FindLastIndex(a => a is AlignWindowAction);
        Assert.True(activateIndex > lastAlignIndex);
    }

    [Fact]
    public void 只激活活动标签对应的窗口()
    {
        var (manager, groupId) = TestData.GroupOf(4);

        var result = manager.ActivateTab(groupId, TestData.Id(3));

        var activate = Assert.Single(result.Actions.OfType<ActivateWindowAction>());
        Assert.Equal(TestData.Id(3), activate.Target);
    }

    // ------------------------------------------------------------------
    // 标题与重命名
    // ------------------------------------------------------------------

    [Fact]
    public void 用户重命名后标题变化不覆盖自定义名称()
    {
        var (manager, groupId) = TestData.GroupOf(2);
        manager.RenameTab(groupId, TestData.Id(1), "我的项目");

        manager.OnWindowTitleChanged(TestData.Id(1), "别的文档.txt");

        var tab = manager.FindGroup(groupId)!.FindTab(TestData.Id(1))!;
        Assert.Equal("我的项目", tab.DisplayTitle);
        Assert.Equal("别的文档.txt", tab.Title);
    }

    [Fact]
    public void 清空自定义名称后回退到窗口标题()
    {
        var (manager, groupId) = TestData.GroupOf(2);
        manager.RenameTab(groupId, TestData.Id(1), "我的项目");

        manager.RenameTab(groupId, TestData.Id(1), "   ");

        Assert.Equal("窗口1", manager.FindGroup(groupId)!.FindTab(TestData.Id(1))!.DisplayTitle);
    }

    // ------------------------------------------------------------------
    // HWND 复用
    // ------------------------------------------------------------------

    [Fact]
    public void 句柄相同但进程不同视为不同窗口()
    {
        var manager = new GroupSessionManager();
        manager.CreateGroup([TestData.Candidate(1), TestData.Candidate(2)]);

        // 同一个句柄值被新进程复用 —— 绝不能当成原来的成员继续操作。
        var reused = new WindowIdentity(1, ProcessId: 9999, ProcessStartTicks: 777);

        Assert.False(manager.IsGrouped(reused));
        Assert.Null(manager.FindGroupByWindow(reused));
    }
}
