using TabNest.Core.Models;

namespace TabNest.Core.Layout;

/// <summary>
/// 把 <see cref="AppSettings"/> 映射成各页面的内容描述，以及把用户操作写回设置。
///
/// 刻意做成纯函数：设置界面的正确性几乎全在"哪个控件对应哪个字段"上，
/// 而这件事一旦放进窗口过程里就只能靠手点验证。放在这里可以整页断言。
/// </summary>
public static class SettingsPages
{
    /// <summary>导航栏顺序。</summary>
    public static IReadOnlyList<SettingsPage> Order { get; } =
    [
        SettingsPage.Overview,
        SettingsPage.Appearance,
        SettingsPage.Colors,
        SettingsPage.Grouping,
        SettingsPage.Rules,
        SettingsPage.About,
    ];

    public static string TitleOf(SettingsPage page) => page switch
    {
        SettingsPage.Overview => "总览",
        SettingsPage.Appearance => "标签外观",
        SettingsPage.Colors => "标签颜色",
        SettingsPage.Grouping => "分组设置",
        SettingsPage.Rules => "分组规则",
        _ => "关于",
    };

    /// <summary>阶段一尚未提供集成模式，统一用这句说明，避免各处措辞不一。</summary>
    private const string IntegratedPending =
        "集成标签页需要把标签绘制进目标窗口的标题栏，这只能在目标进程内完成，将于下一版本随注入层提供。";

    public static PageContent Build(SettingsPage page, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return page switch
        {
            SettingsPage.Overview => Overview(settings),
            SettingsPage.Appearance => Appearance(settings),
            SettingsPage.Grouping => Grouping(settings),
            SettingsPage.Colors => Placeholder(page, "标签颜色", "主题色与标签配色。"),
            SettingsPage.Rules => Placeholder(page, "分组规则", "自动分组、已保存分组、阻止与允许应用、自定义规则。"),
            _ => About(),
        };
    }

    private static PageContent Overview(AppSettings s) => new()
    {
        Page = SettingsPage.Overview,
        Title = "总览",
        Subtitle = "最常用的几项设置。",
        Blocks =
        [
            new SectionBlock
            {
                Title = "选择标签类型",
                Description = "标签栏的呈现方式。",
            },
            new ChoiceBlock
            {
                Id = SettingId.HostingMode,
                Value = (int)s.HostingMode,
                Options =
                [
                    new ChoiceOption(
                        (int)TabHostingMode.RailAbove,
                        "将标签页在上方",
                        "标签栏贴在窗口标题栏上方，适用于所有应用。",
                        Illustration.RailAbove),
                    new ChoiceOption(
                        (int)TabHostingMode.IntegratedTitleBar,
                        "集成标签页",
                        "标签直接绘制在窗口标题栏内，视觉上与原生标签无异。",
                        Illustration.IntegratedTitleBar,
                        DisabledReason: IntegratedPending),
                ],
            },

            new SectionBlock { Title = "分组窗口的任务栏按钮" },
            new ChoiceBlock
            {
                Id = SettingId.TaskbarButtons,
                Value = (int)s.TaskbarButtons,
                Options =
                [
                    new ChoiceOption(
                        (int)TaskbarButtonPolicy.ShowAll,
                        "显示全部",
                        "每个成员窗口都保留自己的任务栏按钮。",
                        Illustration.TaskbarAll),
                    new ChoiceOption(
                        (int)TaskbarButtonPolicy.ActiveOnly,
                        "仅显示活动窗口",
                        "只保留当前活动标签的按钮，任务栏更清爽。",
                        Illustration.TaskbarActiveOnly),
                    new ChoiceOption(
                        (int)TaskbarButtonPolicy.SingleCombined,
                        "仅显示一个组合按钮",
                        "整组合并为一个按钮。需要代理窗口，将于后续版本提供。",
                        Illustration.TaskbarSingle,
                        DisabledReason: "该模式需要为每个组创建代理窗口，将于后续版本提供。"),
                ],
            },

            new SectionBlock { Title = "行为" },
            new ToggleBlock
            {
                Id = SettingId.ShowHoverBar,
                Title = "鼠标悬停未分组窗口标题栏顶部时显示分组条",
                Description = "日常分组的主要入口。关闭后仍可通过拖拽窗口到另一个窗口的标题栏来分组。",
                Value = s.ShowHoverBar,
            },
            new ToggleBlock
            {
                Id = SettingId.RunAtStartup,
                Title = "开机时自动启动",
                Description = "登录 Windows 后自动运行 TabNest 并恢复上次的分组。",
                Value = s.RunAtStartup,
            },

            new SeparatorBlock(),

            new ToggleBlock
            {
                Id = SettingId.MasterEnabled,
                Title = "启用 TabNest",
                Description = "关闭后会拆散所有分组并把窗口还原到原位，托盘图标与全部设置保留。",
                Value = s.Enabled,
            },
        ],
    };

    private static PageContent Appearance(AppSettings s) => new()
    {
        Page = SettingsPage.Appearance,
        Title = "标签外观",
        Subtitle = "标签栏的样式与显示时机。",
        Blocks =
        [
            new SectionBlock { Title = "标签样式" },
            new ChoiceBlock
            {
                Id = SettingId.RoundedTabs,
                Value = s.Appearance.RoundedTabs ? 1 : 0,
                Options =
                [
                    new ChoiceOption(0, "传统", "直角标签。", Illustration.TabsSquare),
                    new ChoiceOption(1, "圆形", "圆角标签，与 Windows 11 的视觉语言一致。", Illustration.TabsRounded),
                ],
            },

            new SectionBlock { Title = "显示" },
            new ToggleBlock
            {
                Id = SettingId.ShowBackgroundBar,
                Title = "添加背景条至标签",
                Description = "在标签后方绘制一条背景，让标签栏与窗口形成整体。",
                Value = s.Appearance.ShowBackgroundBar,
            },
            new ToggleBlock
            {
                Id = SettingId.TallerTabs,
                Title = "使标签和背景栏更高",
                Value = s.Appearance.TallerTabs,
            },
            new ToggleBlock
            {
                Id = SettingId.HideWhenSingleTab,
                Title = "只有一个标签时不绘制标签栏",
                Description = "单个标签的标签栏不承载任何信息，隐藏后可省下一条高度。",
                Value = s.Appearance.HideWhenSingleTab,
            },
            new ToggleBlock
            {
                Id = SettingId.ShowWindowIcon,
                Title = "在标签上显示窗口图标",
                Value = s.Appearance.ShowWindowIcon,
            },
            new ToggleBlock
            {
                Id = SettingId.VariableWidthTabs,
                Title = "标签宽度随标题长度变化",
                Description = "关闭时所有标签等宽。",
                Value = s.Appearance.VariableWidthTabs,
            },
            new ToggleBlock
            {
                Id = SettingId.ShowCloseAllButton,
                Title = "显示「关闭整组」按钮",
                Value = s.Appearance.ShowCloseAllButton,
            },
            new ToggleBlock
            {
                Id = SettingId.HideMenuButton,
                Title = "隐藏菜单按钮",
                Description = "隐藏后仍可在标签上点右键打开同一菜单。",
                Value = s.Appearance.HideMenuButton,
            },
            new ToggleBlock
            {
                Id = SettingId.DisableAnimation,
                Title = "不使用动画",
                Description = "系统的「减少动态效果」开启时，无论此项如何设置都会禁用动画。",
                Value = s.Appearance.DisableAnimation,
            },
        ],
    };

    private static PageContent Grouping(AppSettings s) => new()
    {
        Page = SettingsPage.Grouping,
        Title = "分组设置",
        Subtitle = "拖放分组的触发方式与保护措施。",
        Blocks =
        [
            new SectionBlock
            {
                Title = "手动拖放分组",
                Description = "把一个窗口拖到另一个窗口的标题栏即可合并。",
            },
            new ToggleBlock
            {
                Id = SettingId.SameApplicationOnly,
                Title = "仅允许同一应用的窗口分组",
                Description = "防止误把不相干的应用合并到一起。",
                Value = s.Grouping.SameApplicationOnly,
            },
            new ToggleBlock
            {
                Id = SettingId.WholeWindowIsDropTarget,
                Title = "整个窗口都可作为拖放目标",
                Description = "关闭时只有标题栏附近的一条横带会触发合并，与浏览器「拖到标签栏才合并」的习惯一致。",
                Value = s.Grouping.WholeWindowIsDropTarget,
            },
            new ToggleBlock
            {
                Id = SettingId.RequireModifierToMoveTabs,
                Title = "移动标签需按住修饰键",
                Description = "防止在标签栏上误拖导致顺序变乱。",
                Value = s.Grouping.RequireModifierToMoveTabs,
            },

            new SectionBlock { Title = "自动分组" },
            new ToggleBlock
            {
                Id = SettingId.AutoGroupIdenticalWindows,
                Title = "自动把同类窗口合并到一组",
                Description = "默认关闭。自动分组容易被理解为窗口「被抢走」，建议先用规则页针对具体应用开启。",
                Value = s.Grouping.AutoGroupIdenticalWindows,
            },
            new ToggleBlock
            {
                Id = SettingId.YieldToNativeTabs,
                Title = "避让已内建标签的应用",
                Description = "资源管理器、记事本等自带标签的应用不参与分组，避免出现双重标签栏。",
                Value = s.Grouping.YieldToNativeTabs,
            },
            new ToggleBlock
            {
                Id = SettingId.AllowListOnly,
                Title = "仅允许指定的应用分组",
                Description = "开启后只有在规则页中明确允许的应用才能分组。",
                Value = s.Grouping.AllowListOnly,
            },
        ],
    };

    private static PageContent Placeholder(SettingsPage page, string title, string subtitle) => new()
    {
        Page = page,
        Title = title,
        Subtitle = subtitle,
        Blocks =
        [
            new InfoBlock
            {
                Text = "该页面尚未实现，将在后续版本提供。当前可通过配置文件手工设置。",
            },
        ],
    };

    private static PageContent About() => new()
    {
        Page = SettingsPage.About,
        Title = "关于",
        Blocks =
        [
            new InfoBlock
            {
                Text = "TabNest —— 把任意 Windows 窗口组织成标签工作区。\n\n"
                    + "所有设置与分组快照都保存在本机，不会上传任何数据。\n"
                    + "日志只记录窗口标题与进程名这类定位问题必需的元数据，"
                    + "绝不记录窗口内容、文档内容或键盘输入。",
            },
        ],
    };

    // ------------------------------------------------------------------
    // 写回
    // ------------------------------------------------------------------

    /// <summary>切换一个开关，返回新的设置对象。未知 id 原样返回。</summary>
    public static AppSettings Toggle(AppSettings s, SettingId id)
    {
        ArgumentNullException.ThrowIfNull(s);

        return id switch
        {
            SettingId.MasterEnabled => s with { Enabled = !s.Enabled },
            SettingId.ShowHoverBar => s with { ShowHoverBar = !s.ShowHoverBar },
            SettingId.RunAtStartup => s with { RunAtStartup = !s.RunAtStartup },

            SettingId.ShowBackgroundBar => s with
            {
                Appearance = s.Appearance with { ShowBackgroundBar = !s.Appearance.ShowBackgroundBar },
            },
            SettingId.TallerTabs => s with
            {
                Appearance = s.Appearance with { TallerTabs = !s.Appearance.TallerTabs },
            },
            SettingId.HideWhenSingleTab => s with
            {
                Appearance = s.Appearance with { HideWhenSingleTab = !s.Appearance.HideWhenSingleTab },
            },
            SettingId.ShowWindowIcon => s with
            {
                Appearance = s.Appearance with { ShowWindowIcon = !s.Appearance.ShowWindowIcon },
            },
            SettingId.VariableWidthTabs => s with
            {
                Appearance = s.Appearance with { VariableWidthTabs = !s.Appearance.VariableWidthTabs },
            },
            SettingId.ShowCloseAllButton => s with
            {
                Appearance = s.Appearance with { ShowCloseAllButton = !s.Appearance.ShowCloseAllButton },
            },
            SettingId.HideMenuButton => s with
            {
                Appearance = s.Appearance with { HideMenuButton = !s.Appearance.HideMenuButton },
            },
            SettingId.DisableAnimation => s with
            {
                Appearance = s.Appearance with { DisableAnimation = !s.Appearance.DisableAnimation },
            },

            SettingId.SameApplicationOnly => s with
            {
                Grouping = s.Grouping with { SameApplicationOnly = !s.Grouping.SameApplicationOnly },
            },
            SettingId.WholeWindowIsDropTarget => s with
            {
                Grouping = s.Grouping with { WholeWindowIsDropTarget = !s.Grouping.WholeWindowIsDropTarget },
            },
            SettingId.RequireModifierToMoveTabs => s with
            {
                Grouping = s.Grouping with { RequireModifierToMoveTabs = !s.Grouping.RequireModifierToMoveTabs },
            },
            SettingId.YieldToNativeTabs => s with
            {
                Grouping = s.Grouping with { YieldToNativeTabs = !s.Grouping.YieldToNativeTabs },
            },
            SettingId.AllowListOnly => s with
            {
                Grouping = s.Grouping with { AllowListOnly = !s.Grouping.AllowListOnly },
            },
            SettingId.AutoGroupIdenticalWindows => s with
            {
                Grouping = s.Grouping with
                {
                    AutoGroupIdenticalWindows = !s.Grouping.AutoGroupIdenticalWindows,
                },
            },

            _ => s,
        };
    }

    /// <summary>选择一组卡片中的某一项，返回新的设置对象。</summary>
    public static AppSettings Choose(AppSettings s, SettingId id, int value)
    {
        ArgumentNullException.ThrowIfNull(s);

        return id switch
        {
            SettingId.HostingMode => s with { HostingMode = (TabHostingMode)value },
            SettingId.TaskbarButtons => s with { TaskbarButtons = (TaskbarButtonPolicy)value },
            SettingId.RoundedTabs => s with
            {
                Appearance = s.Appearance with { RoundedTabs = value != 0 },
            },
            SettingId.TabVisibility => s with
            {
                Appearance = s.Appearance with { Visibility = (TabVisibility)value },
            },
            SettingId.CloseButtonPolicy => s with
            {
                Appearance = s.Appearance with { CloseButton = (CloseButtonPolicy)value },
            },
            SettingId.DragTrigger => s with
            {
                Grouping = s.Grouping with { Trigger = (DragTrigger)value },
            },
            SettingId.DragDelay => s with
            {
                Grouping = s.Grouping with { Delay = (DragDelay)value },
            },
            SettingId.MiddleClick => s with
            {
                Grouping = s.Grouping with { MiddleClick = (MiddleClickAction)value },
            },
            _ => s,
        };
    }
}
