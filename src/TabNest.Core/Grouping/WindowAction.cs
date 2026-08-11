using TabNest.Core.Models;

namespace TabNest.Core.Grouping;

/// <summary>
/// 领域层发给窗口控制层的指令。
///
/// 这是整个架构的关键边界：<see cref="GroupSessionManager"/> **不直接操作任何窗口**，
/// 只产出指令列表，由 Interop 层在单一串行队列上执行（计划书 §7.3）。
///
/// 这样做的收益：
///   1. 组逻辑可以在完全没有窗口的环境下单元测试；
///   2. 所有跨进程副作用都汇聚在一个可审计、可限流、可回滚的执行点；
///   3. 指令可以在执行前先落盘，满足"先写快照再动窗口"的恢复要求。
/// </summary>
public abstract record WindowAction(WindowIdentity Target);

/// <summary>把窗口抬到 Z 序顶部并转移键盘焦点。执行方需实现四级焦点降级链。</summary>
public sealed record ActivateWindowAction(WindowIdentity Target) : WindowAction(Target);

/// <summary>
/// 把窗口对齐到组矩形。
///
/// 阶段一的切换策略是「层叠置顶」：所有成员对齐到同一矩形，切换时只把目标抬到最前。
/// 刻意不用隐藏或最小化 —— <c>SW_HIDE</c> 会让模态对话框丢失且崩溃后难以找回，
/// 最小化则带动画，无法满足 100ms 的切换延迟目标。
/// </summary>
public sealed record AlignWindowAction(WindowIdentity Target, PixelRect Bounds) : WindowAction(Target);

/// <summary>把窗口压到组内其他成员之后，但不改变焦点。用于切换时让出位置。</summary>
public sealed record LowerWindowAction(WindowIdentity Target) : WindowAction(Target);

/// <summary>按快照还原窗口的位置、显示状态、置顶属性与任务栏可见性。</summary>
public sealed record RestoreWindowAction(WindowIdentity Target, WindowSnapshot Snapshot)
    : WindowAction(Target);

/// <summary>
/// 显示或隐藏窗口的任务栏按钮。
/// 通过 <c>ITaskbarList::AddTab</c>/<c>DeleteTab</c> 实现 —— 对任意进程的顶层 HWND 有效，
/// 不修改目标窗口样式，完全可逆。
/// </summary>
public sealed record SetTaskbarButtonAction(WindowIdentity Target, bool Visible)
    : WindowAction(Target);

/// <summary>请求关闭窗口（发送 WM_CLOSE）。应用有权拒绝或弹出保存提示，这是预期行为。</summary>
public sealed record CloseWindowAction(WindowIdentity Target) : WindowAction(Target);
