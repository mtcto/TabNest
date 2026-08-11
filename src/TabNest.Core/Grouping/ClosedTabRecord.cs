using TabNest.Core.Models;

namespace TabNest.Core.Grouping;

/// <summary>
/// 一条已关闭标签的记录，用于"重开已关闭标签"。
///
/// 无论是用户点了标签的关闭按钮，还是直接关闭了应用窗口，都会留下记录 ——
/// 后者同样是用户会想撤销的操作。
/// </summary>
public sealed record ClosedTabRecord
{
    public required string Title { get; init; }

    public required string ProcessName { get; init; }

    /// <summary>可执行文件路径。为 null 时无法重启该应用，只能提示用户手动打开。</summary>
    public string? ProcessPath { get; init; }

    /// <summary>关闭前的窗口状态。重开后据此把新窗口摆回原位。</summary>
    public required WindowSnapshot Snapshot { get; init; }

    /// <summary>关闭时所属的组 ID。若该组仍存在，重开的窗口应自动回到这个组。</summary>
    public required string GroupId { get; init; }

    /// <summary>关闭时在组内的位置，重开后尽量恢复到同一位置。</summary>
    public required int TabIndex { get; init; }

    public required DateTimeOffset ClosedAt { get; init; }

    /// <summary>
    /// 是否具备重新启动应用的条件。
    /// 没有可执行路径时（例如无权限查询的进程）只能告知用户手动打开，不能假装能恢复。
    /// </summary>
    public bool CanRelaunch => !string.IsNullOrWhiteSpace(ProcessPath);
}
