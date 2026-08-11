namespace TabNest.Core.Models;

/// <summary>
/// 进程完整性级别。
/// TabNest 以标准用户权限运行（见 app.manifest 的 asInvoker）。
/// 受 UIPI 限制，低完整性级别的进程无法向高完整性级别的窗口发送消息或改变其位置，
/// 因此提权窗口必须被识别并解释，而不是尝试操作后失败。
/// </summary>
public enum IntegrityLevel
{
    Unknown = 0,
    Untrusted,
    Low,
    Medium,
    High,
    System,
}

/// <summary>
/// 窗口的一次观测结果。
///
/// 这是不可变快照，不是活对象 —— UI 线程只消费快照，不持有裸 HWND 的业务状态
/// （计划书 §7.3 并发约束）。窗口变化时产生新的 WindowInfo，而不是原地修改。
/// </summary>
public sealed record WindowInfo
{
    public required WindowIdentity Identity { get; init; }

    public required string Title { get; init; }

    public required string ClassName { get; init; }

    /// <summary>进程可执行文件名（含扩展名，小写），例如 idea64.exe。规则匹配的主要依据。</summary>
    public required string ProcessName { get; init; }

    /// <summary>进程可执行文件完整路径。可能为空 —— 无权限查询其他用户或受保护进程时会失败。</summary>
    public required string? ProcessPath { get; init; }

    /// <summary>窗口边界。注意：这是 DWM 的可见边框（DWMWA_EXTENDED_FRAME_BOUNDS），不是 GetWindowRect 的结果。</summary>
    public required PixelRect Bounds { get; init; }

    public required WindowShowState ShowState { get; init; }

    public required IntegrityLevel IntegrityLevel { get; init; }

    public required int Dpi { get; init; }

    public required string MonitorDeviceName { get; init; }

    /// <summary>DWM 是否将该窗口标记为 cloaked。UWP 挂起的窗口、其他虚拟桌面上的窗口都是 cloaked，它们不该出现在候选列表里。</summary>
    public required bool IsCloaked { get; init; }

    public required bool IsTopmost { get; init; }

    /// <summary>是否为工具窗口（WS_EX_TOOLWINDOW）。工具窗不进任务栏，通常是浮动面板而非文档窗口。</summary>
    public required bool IsToolWindow { get; init; }

    /// <summary>是否有 owner 窗口。有 owner 的多半是对话框或浮层，不应被当作独立文档窗口分组。</summary>
    public required bool HasOwner { get; init; }

    /// <summary>是否无响应（IsHungAppWindow）。借鉴 Groupy：操作前必须预检，假死窗口不参与分组。</summary>
    public required bool IsNotResponding { get; init; }

    /// <summary>是否为 UWP / 打包应用窗口。其窗口模型特殊，阶段一按 best effort 处理。</summary>
    public required bool IsUwp { get; init; }

    /// <summary>是否自绘标题栏。阶段一不影响分组（轨道贴在窗口上方）；阶段二决定能否使用集成模式。</summary>
    public required bool HasCustomTitleBar { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>标题为空的顶层窗口几乎总是隐藏的宿主窗口或消息窗口，不是用户可见的文档窗口。</summary>
    public bool HasUsableTitle => !string.IsNullOrWhiteSpace(Title);

    public override string ToString() =>
        $"{Identity} {ProcessName} \"{Title}\" [{ClassName}] {Bounds}";
}
