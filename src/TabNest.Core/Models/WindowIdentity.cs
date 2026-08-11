namespace TabNest.Core.Models;

/// <summary>
/// 窗口身份。
///
/// 单靠 HWND 不足以标识一个窗口：Windows 会回收并复用已销毁窗口的句柄值。
/// 若只记 HWND，一个成员窗口关闭后，新开的无关窗口可能拿到同一个句柄，
/// 于是 TabNest 会把它当作原成员继续操作 —— 移动、置顶甚至隐藏一个用户从未加入组的窗口。
///
/// 因此身份 = 句柄 + 进程 ID + 进程启动时间。进程 ID 同样会被复用，
/// 但"进程 ID 复用"与"该进程启动时间恰好相同"同时发生的概率可忽略。
/// </summary>
/// <param name="Handle">窗口句柄（HWND）。</param>
/// <param name="ProcessId">拥有该窗口的进程 ID。</param>
/// <param name="ProcessStartTicks">进程启动时间的 UTC ticks，用于区分被复用的进程 ID。</param>
public readonly record struct WindowIdentity(nint Handle, int ProcessId, long ProcessStartTicks)
{
    public static WindowIdentity None => default;

    public bool IsNone => Handle == 0;

    /// <summary>
    /// 是否指向同一个句柄值。
    /// 仅用于从操作系统事件（只带 HWND）快速定位候选，
    /// 命中后必须再用完整相等性确认，不能直接当作身份匹配。
    /// </summary>
    public bool HasSameHandle(nint handle) => Handle == handle;

    public override string ToString() =>
        IsNone ? "<none>" : $"0x{Handle:X}@{ProcessId}";
}
