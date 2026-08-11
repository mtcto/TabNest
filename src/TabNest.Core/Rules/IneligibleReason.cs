namespace TabNest.Core.Rules;

/// <summary>
/// 窗口不可分组的原因。
///
/// 枚举值的数字区间按类别划分，便于诊断输出分组展示，也便于将来在不破坏
/// 已有值的前提下追加新原因：
///   1xx 系统级前置条件（影响所有窗口）
///   2xx 窗口自身属性
///   3xx 权限与安全
///   4xx 用户规则
///   5xx 状态冲突
///   6xx 兼容性让位
/// </summary>
public enum IneligibleReason
{
    /// <summary>可分组。</summary>
    None = 0,

    // ---- 1xx 系统级前置条件 ----

    /// <summary>桌面窗口管理器未启用。没有 DWM 就拿不到窗口的真实可见边框，轨道无法对齐。</summary>
    DwmDisabled = 101,

    /// <summary>系统未启用"拖动时显示窗口内容"。关闭时拖拽只显示轮廓，无法做拖放命中反馈。</summary>
    DragFullWindowsDisabled = 102,

    // ---- 2xx 窗口自身属性 ----

    /// <summary>窗口无响应。</summary>
    NotResponding = 201,

    /// <summary>窗口被 DWM 标记为 cloaked（挂起的 UWP、其他虚拟桌面上的窗口）。</summary>
    Cloaked = 202,

    /// <summary>工具窗口（WS_EX_TOOLWINDOW），通常是浮动面板而非文档窗口。</summary>
    ToolWindow = 203,

    /// <summary>没有标题。无标题的顶层窗口几乎总是隐藏的宿主窗口或消息窗口。</summary>
    NoTitle = 204,

    /// <summary>有 owner 窗口，多半是对话框或浮层。</summary>
    OwnedPopup = 205,

    /// <summary>窗口不可见。</summary>
    NotVisible = 206,

    /// <summary>窗口尺寸为零或过小，无法承载标签轨道。</summary>
    TooSmall = 207,

    /// <summary>Shell 自身的窗口（桌面、任务栏、开始菜单等）。</summary>
    ShellWindow = 208,

    // ---- 3xx 权限与安全 ----

    /// <summary>目标窗口的完整性级别高于 TabNest，受 UIPI 限制无法操作。</summary>
    IntegrityLevelMismatch = 301,

    /// <summary>TabNest 自己的窗口。</summary>
    OwnWindow = 302,

    // ---- 4xx 用户规则 ----

    /// <summary>命中用户的阻止列表。</summary>
    BlockedByRule = 401,

    /// <summary>启用了"仅允许指定应用分组"，但该应用不在允许列表中。</summary>
    NotInAllowList = 402,

    // ---- 5xx 状态冲突 ----

    /// <summary>已在另一个组中。</summary>
    AlreadyGrouped = 501,

    /// <summary>目标组已被用户锁定。</summary>
    TargetGroupLocked = 502,

    // ---- 6xx 兼容性让位 ----

    /// <summary>应用已有原生标签（Windows 11 文件资源管理器、记事本），避免双重标签。</summary>
    NativeTabbedApp = 601,
}

/// <summary>
/// 判定结果的严重程度。
/// 区分"拒绝"与"提醒"很重要：把警告当拒绝会让产品显得比实际更不兼容，
/// 把拒绝当警告则会让用户以为操作成功了。
/// </summary>
public enum EligibilitySeverity
{
    /// <summary>可分组，无保留意见。</summary>
    Allowed = 0,

    /// <summary>可分组，但存在已知风险，需向用户提示。</summary>
    AllowedWithWarning = 1,

    /// <summary>不可分组。</summary>
    Blocked = 2,
}
