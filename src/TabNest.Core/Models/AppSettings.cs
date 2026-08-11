using TabNest.Core.Rules;

namespace TabNest.Core.Models;

/// <summary>标签承载方式。</summary>
public enum TabHostingMode
{
    /// <summary>标签轨道贴在窗口标题栏上方。阶段一唯一可用的方式。</summary>
    RailAbove = 0,

    /// <summary>标签集成进目标窗口标题栏。需要注入层，阶段二提供。</summary>
    IntegratedTitleBar = 1,
}

/// <summary>标签轨道的可见性策略。对齐 Groupy 标签外观页的四选一。</summary>
public enum TabVisibility
{
    AlwaysVisible = 0,
    ActiveWindowOnly = 1,

    /// <summary>窗口最大化时隐藏，鼠标移到屏幕顶部才显示。避免轨道遮挡最大化窗口的内容。</summary>
    HideWhenMaximized = 2,

    /// <summary>始终隐藏，仅在悬停到窗口顶部时显示。</summary>
    AlwaysHidden = 3,
}

/// <summary>标签关闭按钮的显示策略。</summary>
public enum CloseButtonPolicy
{
    OnHoverOnly = 0,
    ActiveTabOnly = 1,
    AllTabs = 2,
    Never = 3,
}

/// <summary>拖拽合并的触发条件。默认要求按住修饰键之外的延迟保护，避免误分组。</summary>
public enum DragTrigger
{
    Always = 0,
    RequireShift = 1,
    RequireCtrl = 2,
    RequireRightButton = 3,
}

/// <summary>拖拽合并前的悬停延迟。Groupy 默认 ⅓ 秒，并在首次运行时专门弹窗确认是否保留。</summary>
public enum DragDelay
{
    Instant = 0,
    Third = 1,
    TwoThirds = 2,
    OneSecond = 3,
}

/// <summary>中键点击标签的行为。</summary>
public enum MiddleClickAction
{
    Nothing = 0,
    CloseTab = 1,
}

/// <summary>标签外观设置。</summary>
public sealed record AppearanceSettings
{
    public bool RoundedTabs { get; init; } = true;
    public bool ShowBackgroundBar { get; init; } = true;
    public bool TallerTabs { get; init; }
    public TabVisibility Visibility { get; init; } = TabVisibility.AlwaysVisible;
    public CloseButtonPolicy CloseButton { get; init; } = CloseButtonPolicy.ActiveTabOnly;

    /// <summary>只有一个标签时不绘制标签 —— 单标签的标签栏是纯视觉噪声。</summary>
    public bool HideWhenSingleTab { get; init; }

    public bool ShowWindowIcon { get; init; } = true;

    /// <summary>标签宽度随标题长度变化，而非等宽。</summary>
    public bool VariableWidthTabs { get; init; }

    public bool ShowCloseAllButton { get; init; } = true;
    public bool HideMenuButton { get; init; }

    /// <summary>禁用动画。除用户偏好外，系统「减少动态效果」开启时也应视为 true。</summary>
    public bool DisableAnimation { get; init; }
}

/// <summary>分组行为设置。</summary>
public sealed record GroupingSettings
{
    public DragTrigger Trigger { get; init; } = DragTrigger.Always;

    /// <summary>默认保留延迟，这是防止误分组最有效的一道保护。</summary>
    public DragDelay Delay { get; init; } = DragDelay.Third;

    /// <summary>只允许同一应用的窗口通过拖拽分组（按住 Shift 可临时放宽）。</summary>
    public bool SameApplicationOnly { get; init; }

    /// <summary>整个窗口都可作为拖放目标，而不只是标题栏。</summary>
    public bool WholeWindowIsDropTarget { get; init; }

    /// <summary>标签需按住 Shift 或右键才能拖动重排，防止误拖乱序。</summary>
    public bool RequireModifierToMoveTabs { get; init; }

    public MiddleClickAction MiddleClick { get; init; } = MiddleClickAction.CloseTab;

    /// <summary>避让已内建标签的应用（资源管理器、记事本），防止双重标签。</summary>
    public bool YieldToNativeTabs { get; init; } = true;

    /// <summary>仅允许指定应用参与分组。</summary>
    public bool AllowListOnly { get; init; }

    /// <summary>自动把同类窗口合并到一组。默认关闭 —— 自动行为会被用户理解为窗口"被抢走"。</summary>
    public bool AutoGroupIdenticalWindows { get; init; }
}

/// <summary>TabNest 的全部用户设置。</summary>
public sealed record AppSettings
{
    /// <summary>配置结构版本。用于迁移，不可省略。</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// 全局主开关。关闭时拆散所有组并还原窗口，但保留托盘与配置，不退出进程。
    /// </summary>
    public bool Enabled { get; init; } = true;

    public TabHostingMode HostingMode { get; init; } = TabHostingMode.RailAbove;

    public TaskbarButtonPolicy TaskbarButtons { get; init; } = TaskbarButtonPolicy.ShowAll;

    /// <summary>鼠标悬停在未分组窗口标题栏顶部时浮出分组条。这是日常分组的主入口，默认开启。</summary>
    public bool ShowHoverBar { get; init; } = true;

    public bool RunAtStartup { get; init; }

    /// <summary>关闭历史保留条数。</summary>
    public int ClosedTabHistoryLimit { get; init; } = 10;

    public AppearanceSettings Appearance { get; init; } = new();

    public GroupingSettings Grouping { get; init; } = new();

    public IReadOnlyList<Rule> Rules { get; init; } = [];

    /// <summary>
    /// 配置被管理员策略锁定为只读。
    /// 阶段一仅预留字段，UI 不实现企业策略管理。
    /// </summary>
    public bool IsPolicyLocked { get; init; }

    /// <summary>产品默认配置。稀疏写入以它为基准做差分。</summary>
    public static AppSettings Default { get; } = new();
}
