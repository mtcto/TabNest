namespace TabNest.Core.Models;

/// <summary>
/// 组的生命周期状态。对应计划书 §7.2：
/// Empty → Creating → Stable → Switching → Recovering → Degraded → Closing → Closed。
/// </summary>
public enum GroupState
{
    Creating = 0,
    Stable,

    /// <summary>正在切换活动标签。此状态下拒绝并发的切换请求，避免焦点争抢。</summary>
    Switching,

    /// <summary>正在从异常中恢复。</summary>
    Recovering,

    /// <summary>部分成员不可用，但组仍可操作。UI 必须如实呈现，不能显示为正常。</summary>
    Degraded,

    Closing,
    Closed,
}

/// <summary>
/// 一个标签组。
///
/// 不可变记录：任何变更都产生新实例。这让组状态可以安全地在观察线程、控制队列和 UI 线程之间传递，
/// 无需加锁（计划书 §7.3：UI 线程只消费快照）。
/// </summary>
public sealed record GroupSession
{
    public required string Id { get; init; }

    /// <summary>用户给组起的名字。空则由 UI 依据成员推导展示名。</summary>
    public string? Name { get; init; }

    /// <summary>组的强调色（ARGB）。</summary>
    public uint? AccentColor { get; init; }

    /// <summary>标签顺序即显示顺序。固定标签由 GroupSessionManager 保证排在前面。</summary>
    public IReadOnlyList<TabItem> Tabs { get; init; } = [];

    public GroupState State { get; init; } = GroupState.Creating;

    /// <summary>
    /// 组被锁定时拒绝拆分与关闭成员。
    /// 对应 Groupy 的 Lock group —— 防止在重要工作上下文里误操作拆散一整组窗口。
    /// </summary>
    public bool IsLocked { get; init; }

    /// <summary>组当前占据的位置。所有成员对齐到这个矩形，切换时只改 Z 序。</summary>
    public PixelRect Bounds { get; init; }

    /// <summary>组所在显示器的设备名。</summary>
    public string MonitorDeviceName { get; init; } = string.Empty;

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>最近一次操作失败的描述。非空时 UI 应给出"重试/拆分/查看原因"入口。</summary>
    public string? LastError { get; init; }

    public TabItem? ActiveTab
    {
        get
        {
            foreach (var tab in Tabs)
            {
                if (tab.State is TabState.Active)
                {
                    return tab;
                }
            }

            return null;
        }
    }

    /// <summary>仍指向可操作窗口的标签。</summary>
    public IEnumerable<TabItem> LiveTabs
    {
        get
        {
            foreach (var tab in Tabs)
            {
                if (tab.IsLive)
                {
                    yield return tab;
                }
            }
        }
    }

    public int LiveTabCount
    {
        get
        {
            var count = 0;
            foreach (var tab in Tabs)
            {
                if (tab.IsLive)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// 组是否还有存在意义。
    /// 只剩一个成员的组没有价值 —— 标签栏只有一个标签，纯属视觉噪声，应自动解散并还原窗口。
    /// </summary>
    public bool ShouldDissolve => LiveTabCount < 2;

    public TabItem? FindTab(WindowIdentity identity)
    {
        foreach (var tab in Tabs)
        {
            if (tab.Identity == identity)
            {
                return tab;
            }
        }

        return null;
    }

    public int IndexOf(WindowIdentity identity)
    {
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].Identity == identity)
            {
                return i;
            }
        }

        return -1;
    }
}
