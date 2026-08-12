namespace TabNest.Core.Models;

/// <summary>
/// 窗口在组内的生命周期状态。
/// 对应计划书 §7.2：Discovered → Eligible → Candidate → Grouped → Active/Inactive → Detached/Closed/Unknown。
///
/// 关键约束：**任何外部窗口动作都必须允许进入 Unknown**，而不是让 UI 永远显示"正常"。
/// 外部进程随时可能崩溃、假死或被强杀，假装一切正常只会让用户更困惑。
/// </summary>
public enum TabState
{
    /// <summary>已加入组，当前非活动。</summary>
    Inactive = 0,

    /// <summary>已加入组，当前活动。一个组内有且仅有一个。</summary>
    Active,

    /// <summary>操作失败且原因不明。</summary>
    Unknown,

    /// <summary>已从组中拆分出去。</summary>
    Detached,

    /// <summary>窗口已销毁。</summary>
    Closed,
}

/// <summary>组内的一个标签。</summary>
public sealed record TabItem
{
    public required WindowIdentity Identity { get; init; }

    /// <summary>窗口标题。会随文档切换而变化，由 NAMECHANGE 事件更新。</summary>
    public required string Title { get; init; }

    public required string ProcessName { get; init; }

    /// <summary>
    /// 窗口类名。
    ///
    /// 保存分组后要能重新找回这个窗口，而进程名太粗：同一个应用的主窗口、
    /// 设置面板、工具窗都是同一个进程名，只按它匹配会把设置面板恢复成主窗口。
    /// </summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>进程可执行文件路径。窗口关闭后"重开已关闭标签"需要它来重新启动应用。</summary>
    public string? ProcessPath { get; init; }

    /// <summary>用户自定义的标签名。非空时优先于 Title 显示 —— 用户重命名过就不该被应用的标题变化覆盖。</summary>
    public string? CustomTitle { get; init; }

    /// <summary>加入组之前的原始状态。拆分、关闭组、TabNest 退出时据此还原。</summary>
    public required WindowSnapshot Snapshot { get; init; }

    public TabState State { get; init; } = TabState.Inactive;

    /// <summary>
    /// 窗口当前是否响应。
    ///
    /// 刻意与 <see cref="State"/> 分开：响应性和活动性是**正交**的两件事。
    /// 早期版本把 Unresponsive 塞进 State，结果活动窗口一旦卡住，组里就没有任何 Active 标签，
    /// 破坏了"有且仅有一个活动标签"的不变量，且恢复响应后回不到活动态。
    ///
    /// 无响应的标签仍然留在组里、仍然可以是活动标签，只是显示为降级态 ——
    /// 应用往往会自行恢复，把标签移除或降级成非活动会让用户白白丢失上下文。
    /// </summary>
    public bool IsResponding { get; init; } = true;

    /// <summary>标签的强调色（ARGB）。null 表示继承组颜色。</summary>
    public uint? AccentColor { get; init; }

    /// <summary>标签是否被固定。固定标签不参与"关闭其他"，且始终排在最前。</summary>
    public bool IsPinned { get; init; }

    public required DateTimeOffset JoinedAt { get; init; }

    /// <summary>显示用的标题：用户重命名优先。</summary>
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle;

    /// <summary>该标签是否仍指向一个可操作的窗口。</summary>
    public bool IsLive => State is not (TabState.Closed or TabState.Detached);
}
