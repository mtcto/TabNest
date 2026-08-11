using TabNest.Core.Models;

namespace TabNest.Core.Grouping;

/// <summary>组操作失败的原因。</summary>
public enum GroupOperationError
{
    None = 0,

    /// <summary>指定的组不存在。</summary>
    GroupNotFound,

    /// <summary>指定的标签不在该组中。</summary>
    TabNotFound,

    /// <summary>创建组至少需要两个成员。</summary>
    NotEnoughMembers,

    /// <summary>组已被用户锁定，拒绝拆分或关闭成员。</summary>
    GroupLocked,

    /// <summary>窗口已在其他组中。</summary>
    AlreadyGrouped,

    /// <summary>窗口快照不足以支撑还原，拒绝把它带入无法恢复的状态。</summary>
    SnapshotNotRestorable,

    /// <summary>组正在切换中，拒绝并发操作以避免焦点争抢。</summary>
    Busy,

    /// <summary>没有可重开的已关闭标签。</summary>
    NothingToReopen,
}

/// <summary>
/// 组操作的结果。
///
/// 成功时携带更新后的组与待执行的窗口指令；失败时携带原因，且**不产生任何指令** ——
/// 这保证了失败的操作不会留下一半已执行的副作用。
/// </summary>
public sealed record GroupOperationResult
{
    public required bool Success { get; init; }

    public GroupOperationError Error { get; init; }

    /// <summary>面向用户的失败说明。成功时为 null。</summary>
    public string? Message { get; init; }

    /// <summary>操作后的组。组被解散时为 null。</summary>
    public GroupSession? Group { get; init; }

    /// <summary>需要执行的窗口指令，按顺序执行。</summary>
    public IReadOnlyList<WindowAction> Actions { get; init; } = [];

    public static GroupOperationResult Ok(
        GroupSession? group,
        IReadOnlyList<WindowAction>? actions = null) => new()
        {
            Success = true,
            Group = group,
            Actions = actions ?? [],
        };

    public static GroupOperationResult Fail(GroupOperationError error, string message) => new()
    {
        Success = false,
        Error = error,
        Message = message,
    };
}
