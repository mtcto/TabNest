namespace TabNest.Core.Models;

/// <summary>窗口的显示状态，对应 Win32 的 SW_* 与 WINDOWPLACEMENT.showCmd。</summary>
public enum WindowShowState
{
    Unknown = 0,
    Normal,
    Minimized,
    Maximized,
}

/// <summary>
/// 加入组之前的窗口原始状态。这是**恢复的唯一依据**。
///
/// 产品原则二要求"分组、拆分、崩溃、应用退出后必须恢复窗口原位置和显示状态"。
/// 因此快照必须在任何窗口写操作**之前**落盘 —— 先写快照再动窗口，
/// 顺序反了就会出现"已经移动了窗口但还没记下原位置"的窗口期，
/// 此时进程崩溃将导致用户窗口永久错位。
/// </summary>
public sealed record WindowSnapshot
{
    /// <summary>正常状态下的位置（对应 WINDOWPLACEMENT.rcNormalPosition）。最大化/最小化时恢复用的就是它。</summary>
    public required PixelRect RestoreBounds { get; init; }

    /// <summary>快照时窗口实际占据的位置。</summary>
    public required PixelRect CurrentBounds { get; init; }

    public required WindowShowState ShowState { get; init; }

    /// <summary>是否置顶（WS_EX_TOPMOST）。恢复时必须还原，否则用户设的置顶会被静默丢掉。</summary>
    public required bool IsTopmost { get; init; }

    /// <summary>快照时窗口所在显示器的设备名。存设备名而非索引 —— 索引会随插拔显示器变化。</summary>
    public required string MonitorDeviceName { get; init; }

    /// <summary>快照时该窗口的 DPI。跨 DPI 恢复时用于判断是否需要重算。</summary>
    public required int Dpi { get; init; }

    /// <summary>是否曾出现在任务栏。任务栏按钮策略隐藏过按钮时，还原必须依据这个。</summary>
    public required bool WasInTaskbar { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// 快照是否足以完成恢复。
    /// 缺少恢复矩形的快照是无效的 —— 宁可拒绝分组，也不能让窗口进入无法还原的状态。
    /// </summary>
    public bool IsRestorable =>
        !RestoreBounds.IsEmpty && ShowState is not WindowShowState.Unknown;
}
