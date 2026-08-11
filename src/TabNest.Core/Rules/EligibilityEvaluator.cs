using TabNest.Core.Models;

namespace TabNest.Core.Rules;

/// <summary>
/// 系统级前置条件。整个 TabNest 只有一份，与具体窗口无关。
/// 借鉴 Groupy：它会明确提示"需要启用桌面窗口管理器"和"需要启用拖动时显示窗口内容"，
/// 而不是让功能静默失灵。
/// </summary>
/// <param name="IsDwmEnabled">桌面窗口管理器是否启用。</param>
/// <param name="IsDragFullWindowsEnabled">系统是否启用"拖动时显示窗口内容"。</param>
/// <param name="OwnIntegrityLevel">TabNest 自身的完整性级别。</param>
public readonly record struct SystemEnvironment(
    bool IsDwmEnabled,
    bool IsDragFullWindowsEnabled,
    IntegrityLevel OwnIntegrityLevel)
{
    /// <summary>单元测试用的理想环境。</summary>
    public static SystemEnvironment Healthy => new(true, true, IntegrityLevel.Medium);
}

/// <summary>判定所需的上下文。</summary>
public sealed record EligibilityContext
{
    public required SystemEnvironment Environment { get; init; }

    public IReadOnlyList<Rule> Rules { get; init; } = [];

    /// <summary>是否启用"仅允许指定应用在组中"（对应 Groupy 的 InclusionListMode）。</summary>
    public bool AllowListOnly { get; init; }

    /// <summary>已在某个组中的窗口。</summary>
    public IReadOnlySet<WindowIdentity> GroupedWindows { get; init; } =
        new HashSet<WindowIdentity>();

    /// <summary>TabNest 自己的窗口句柄。</summary>
    public IReadOnlySet<nint> OwnWindowHandles { get; init; } = new HashSet<nint>();

    /// <summary>是否避让已有原生标签的应用（资源管理器、记事本）。默认避让，防止双重标签。</summary>
    public bool YieldToNativeTabs { get; init; } = true;

    /// <summary>轨道需要的最小窗口宽高（物理像素）。小于此尺寸无法承载可读的标签。</summary>
    public int MinimumWindowSize { get; init; } = 120;
}

/// <summary>
/// 判定一个窗口能否加入标签组，并解释原因。
///
/// 检查顺序**从最根本到最具体**，先命中先返回。顺序本身是产品决策：
/// 用户应当先被告知"系统没开 DWM"，而不是"这个窗口在你的阻止列表里" ——
/// 前者一改就全部恢复，后者只影响单个应用。报错报到最根本的那一层才有用。
///
/// 纯函数，无状态，可安全并发调用。
/// </summary>
public static class EligibilityEvaluator
{
    /// <summary>Shell 自身的窗口类名。这些窗口属于桌面基础设施，操作它们会破坏系统 UI。</summary>
    private static readonly HashSet<string> ShellClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",              // 桌面
        "WorkerW",              // 桌面壁纸层
        "Shell_TrayWnd",        // 任务栏
        "Shell_SecondaryTrayWnd", // 副显示器任务栏
        "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow",
        "Windows.Internal.Shell.TabProxyWindow",
        "MultitaskingViewFrame", // 任务视图
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "TaskListThumbnailWnd",
        "Button",               // 开始按钮（旧版）
    };

    /// <summary>已内建标签的应用。对它们叠加轨道会形成双重标签，默认避让。</summary>
    private static readonly HashSet<string> NativeTabbedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "notepad.exe",
    };

    public static EligibilityResult Evaluate(WindowInfo window, EligibilityContext context)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(context);

        // ---- 1xx 系统级：影响所有窗口，最该先报 ----

        if (!context.Environment.IsDwmEnabled)
        {
            return EligibilityResult.Block(
                IneligibleReason.DwmDisabled,
                "系统的桌面窗口管理器（DWM）未启用，无法获取窗口的真实边框，标签轨道无法对齐。",
                "请启用桌面窗口管理器后重试。在现代 Windows 上它通常不会被关闭，若已关闭多半是显示驱动或组策略所致。");
        }

        if (!context.Environment.IsDragFullWindowsEnabled)
        {
            return EligibilityResult.Block(
                IneligibleReason.DragFullWindowsDisabled,
                "系统未启用「拖动时显示窗口内容」，拖拽过程中只显示轮廓，无法判断拖放目标。",
                "在「设置 → 辅助功能 → 视觉效果」或「系统属性 → 高级 → 性能设置」中启用「拖动时显示窗口内容」。");
        }

        // ---- 3xx 自身窗口：必须早于其余窗口属性检查，否则会输出自我诊断的噪声 ----

        if (context.OwnWindowHandles.Contains(window.Identity.Handle))
        {
            return EligibilityResult.Block(
                IneligibleReason.OwnWindow,
                "这是 TabNest 自己的窗口。",
                null);
        }

        // ---- 2xx 窗口自身属性 ----

        if (window.IsCloaked)
        {
            return EligibilityResult.Block(
                IneligibleReason.Cloaked,
                "窗口当前被系统隐藏（位于其他虚拟桌面，或是已挂起的应用）。",
                "切换到该窗口所在的虚拟桌面，或先激活该应用再试。");
        }

        // Shell 窗口的判定排在工具窗与无标题之前：桌面和任务栏同时也是工具窗口，
        // 报"这是工具窗口"技术上没错但毫无帮助。更具体的原因优先。
        if (ShellClassNames.Contains(window.ClassName))
        {
            return EligibilityResult.Block(
                IneligibleReason.ShellWindow,
                "这是 Windows 桌面自身的窗口（桌面、任务栏一类）。",
                null);
        }

        if (!window.HasUsableTitle)
        {
            return EligibilityResult.Block(
                IneligibleReason.NoTitle,
                "窗口没有标题。无标题的顶层窗口通常是应用的隐藏宿主窗口，而非用户可见的文档窗口。",
                null);
        }

        if (window.IsToolWindow)
        {
            return EligibilityResult.Block(
                IneligibleReason.ToolWindow,
                "这是工具窗口（浮动面板、调色板一类），不是独立的文档窗口。",
                null);
        }

        if (window.HasOwner)
        {
            return EligibilityResult.Block(
                IneligibleReason.OwnedPopup,
                "这是从属窗口，通常是对话框或浮层。把它从主窗口分离会破坏应用的交互逻辑。",
                "请改为分组它的主窗口。");
        }

        if (window.Bounds.Width < context.MinimumWindowSize
            || window.Bounds.Height < context.MinimumWindowSize)
        {
            return EligibilityResult.Block(
                IneligibleReason.TooSmall,
                $"窗口尺寸过小（{window.Bounds.Width}×{window.Bounds.Height} 像素），无法容纳可读的标签轨道。",
                "把窗口调大后再试。");
        }

        if (window.IsNotResponding)
        {
            return EligibilityResult.Block(
                IneligibleReason.NotResponding,
                "窗口当前无响应。对无响应的窗口执行分组会连带拖慢 TabNest。",
                "等待应用恢复响应后再试。");
        }

        // ---- 3xx 权限 ----

        if (window.IntegrityLevel > context.Environment.OwnIntegrityLevel)
        {
            return EligibilityResult.Block(
                IneligibleReason.IntegrityLevelMismatch,
                $"该窗口以更高权限运行（{Describe(window.IntegrityLevel)}），"
                + $"而 TabNest 以{Describe(context.Environment.OwnIntegrityLevel)}权限运行。"
                + "Windows 的用户界面特权隔离（UIPI）禁止低权限进程操作高权限窗口。",
                "TabNest 刻意不自动提权 —— 为了管理窗口而让整个工具获得管理员权限并不划算。"
                + "如确有需要，可手动以管理员身份启动 TabNest。");
        }

        // ---- 6xx 兼容性让位 ----

        if (context.YieldToNativeTabs && NativeTabbedProcesses.Contains(window.ProcessName))
        {
            return EligibilityResult.Block(
                IneligibleReason.NativeTabbedApp,
                "该应用已内建标签功能，叠加 TabNest 轨道会出现双重标签。",
                "如仍希望由 TabNest 接管，可在「设置 → 分组」中关闭「避让原生标签应用」。");
        }

        // ---- 4xx 用户规则 ----

        var verdict = RuleEngine.Evaluate(window, context.Rules);

        if (verdict.Action is RuleAction.Block)
        {
            return EligibilityResult.Block(
                IneligibleReason.BlockedByRule,
                $"命中阻止规则「{verdict.MatchedRuleName}」。",
                "如需分组该窗口，请在「设置 → 分组规则 → 阻止/允许应用」中停用或修改该规则。",
                verdict.MatchedRuleId);
        }

        if (context.AllowListOnly && verdict.Action is not RuleAction.Allow)
        {
            return EligibilityResult.Block(
                IneligibleReason.NotInAllowList,
                $"已启用「仅允许指定应用分组」，而 {window.ProcessName} 不在允许列表中。",
                $"在「设置 → 分组规则 → 阻止/允许应用」中把 {window.ProcessName} 加入允许列表，"
                + "或关闭「仅允许指定应用分组」。");
        }

        // ---- 5xx 状态冲突 ----

        if (context.GroupedWindows.Contains(window.Identity))
        {
            return EligibilityResult.Block(
                IneligibleReason.AlreadyGrouped,
                "该窗口已在另一个标签组中。",
                "先把它从当前组拆分出来，再加入新组。");
        }

        // ---- 通过，但可能带保留意见 ----

        if (window.IsUwp)
        {
            return EligibilityResult.Warn(
                IneligibleReason.None,
                "这是打包应用（UWP）窗口。此类窗口的生命周期由系统托管，可能被系统挂起或提前销毁，"
                + "分组行为为尽力而为。",
                "若出现标签失效，请把它从组中拆分。");
        }

        return EligibilityResult.Allowed;
    }

    private static string Describe(IntegrityLevel level) => level switch
    {
        IntegrityLevel.Untrusted => "不受信任",
        IntegrityLevel.Low => "低",
        IntegrityLevel.Medium => "标准用户",
        IntegrityLevel.High => "管理员",
        IntegrityLevel.System => "系统",
        _ => "未知",
    };
}
