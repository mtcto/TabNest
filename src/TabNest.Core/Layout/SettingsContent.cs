namespace TabNest.Core.Layout;

/// <summary>
/// 设置项的标识。
///
/// 用枚举而不是字符串：控件与设置字段之间的对应关系必须在编译期就成立，
/// 手写 UI 里最难查的 bug 就是某个开关连错了字段 —— 界面上一切正常，
/// 点它却改了别的设置。
/// </summary>
public enum SettingId
{
    None = 0,

    // 总览
    MasterEnabled,
    HostingMode,
    ShowHoverBar,
    TaskbarButtons,
    RunAtStartup,

    // 标签外观
    RoundedTabs,
    ShowBackgroundBar,
    TallerTabs,
    TabVisibility,
    CloseButtonPolicy,
    HideWhenSingleTab,
    ShowWindowIcon,
    VariableWidthTabs,
    ShowCloseAllButton,
    HideMenuButton,
    DisableAnimation,

    // 分组规则
    HotkeysEnabled,

    // 分组设置
    DragTrigger,
    DragDelay,
    SameApplicationOnly,
    WholeWindowIsDropTarget,
    RequireModifierToMoveTabs,
    MiddleClick,
    YieldToNativeTabs,
    AllowListOnly,
    AutoGroupIdenticalWindows,
    ClosedTabHistoryLimit,
}

/// <summary>页面上的一个元素。</summary>
public abstract record ContentBlock
{
    /// <summary>
    /// 该元素为何被禁用。null 表示可用。
    ///
    /// 刻意携带原因而不是一个 bool：灰掉一个控件却不说为什么，是设置界面里
    /// 最让人恼火的事。阶段一的"集成标签页"必须明确告诉用户它在等什么。
    /// </summary>
    public string? DisabledReason { get; init; }

    public bool IsEnabled => DisabledReason is null;
}

/// <summary>分区标题。</summary>
public sealed record SectionBlock : ContentBlock
{
    public required string Title { get; init; }

    /// <summary>分区说明，可空。</summary>
    public string? Description { get; init; }
}

/// <summary>开关行：标题 + 说明 + 右侧开关。</summary>
public sealed record ToggleBlock : ContentBlock
{
    public required SettingId Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required bool Value { get; init; }
}

/// <summary>图示选择卡片中的一项。</summary>
/// <param name="Value">选中后写回设置的值，由页面自行解释。</param>
/// <param name="Title">卡片标题。</param>
/// <param name="Description">卡片说明，可空。</param>
/// <param name="Illustration">图示样式。</param>
/// <param name="DisabledReason">该项不可选的原因，null 表示可选。</param>
public readonly record struct ChoiceOption(
    int Value,
    string Title,
    string? Description,
    Illustration Illustration,
    string? DisabledReason = null);

/// <summary>
/// 卡片里的图示。用矢量绘制的示意图而不是位图资源。
///
/// Groupy 用 6.6MB 的位图皮肤做这些图示，代价是 150% 缩放下发糊；
/// 矢量绘制体积为零且任何 DPI 下都清晰。
/// </summary>
public enum Illustration
{
    /// <summary>窗口上方一条标签栏。</summary>
    RailAbove,

    /// <summary>标签集成在标题栏内。</summary>
    IntegratedTitleBar,

    /// <summary>任务栏显示全部窗口按钮。</summary>
    TaskbarAll,

    /// <summary>任务栏仅显示活动窗口按钮。</summary>
    TaskbarActiveOnly,

    /// <summary>任务栏合并为单个按钮。</summary>
    TaskbarSingle,

    /// <summary>标签为圆角。</summary>
    TabsRounded,

    /// <summary>标签为直角。</summary>
    TabsSquare,
}

/// <summary>一组图示选择卡片，等价于单选。</summary>
public sealed record ChoiceBlock : ContentBlock
{
    public required SettingId Id { get; init; }
    public required int Value { get; init; }
    public required IReadOnlyList<ChoiceOption> Options { get; init; }
}

/// <summary>说明文字块。用于承载"为什么"与限制说明。</summary>
public sealed record InfoBlock : ContentBlock
{
    public required string Text { get; init; }

    /// <summary>是否以警示样式呈现。</summary>
    public bool IsWarning { get; init; }
}

/// <summary>分隔线。</summary>
public sealed record SeparatorBlock : ContentBlock;

/// <summary>
/// 只读的条目列表：规则、热键绑定、已保存分组都用它呈现。
///
/// 阶段一刻意做成只读展示 + 说明如何编辑，而不是做成可编辑控件。
/// 可编辑的规则表需要文本框、下拉框、增删行与校验，是设置中心里最大的一块；
/// 与其赶出一个半成品，不如先把现状如实呈现出来，并明确告诉用户在哪里改。
/// </summary>
public sealed record ListBlock : ContentBlock
{
    public required string Title { get; init; }

    /// <summary>每一行：主文本 + 次要文本。</summary>
    public required IReadOnlyList<(string Primary, string Secondary)> Items { get; init; }

    /// <summary>列表为空时显示的话。必须写清"没有"和"怎么加"。</summary>
    public required string EmptyText { get; init; }
}

/// <summary>一块颜色样本，用于颜色页展示当前配色。</summary>
public sealed record SwatchBlock : ContentBlock
{
    public required string Title { get; init; }
    public string? Description { get; init; }

    /// <summary>颜色样本：名称 + ARGB。</summary>
    public required IReadOnlyList<(string Name, uint Argb)> Swatches { get; init; }
}

/// <summary>一页的完整内容。</summary>
public sealed record PageContent
{
    public required SettingsPage Page { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public required IReadOnlyList<ContentBlock> Blocks { get; init; }
}
