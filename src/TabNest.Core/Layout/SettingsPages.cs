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
            SettingsPage.Colors => Colors(settings),
            SettingsPage.Rules => Rules(settings),
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

            new SectionBlock
            {
                Title = "标签栏可见性",
                Description = "分组栏在什么时候显示。",
            },
            new ChoiceBlock
            {
                Id = SettingId.TabVisibility,
                Value = (int)s.Appearance.Visibility,
                Options =
                [
                    new ChoiceOption(
                        (int)TabVisibility.AlwaysVisible,
                        "始终可见",
                        "任何时候都显示分组栏。",
                        Illustration.RailAbove),
                    new ChoiceOption(
                        (int)TabVisibility.ActiveWindowOnly,
                        "仅活动窗口",
                        "只有当该组的窗口是前台窗口时才显示。",
                        Illustration.TaskbarActiveOnly),
                    new ChoiceOption(
                        (int)TabVisibility.HideWhenMaximized,
                        "最大化时隐藏",
                        "窗口最大化后隐藏，光标移到窗口顶部时浮出。",
                        Illustration.IntegratedTitleBar),
                    new ChoiceOption(
                        (int)TabVisibility.AlwaysHidden,
                        "始终隐藏",
                        "平时不显示，光标移到窗口顶部时才浮出。",
                        Illustration.TaskbarSingle),
                ],
            },

            new SectionBlock
            {
                Title = "关闭按钮",
                Description = "标签上的关闭按钮何时出现。",
            },
            new ChoiceBlock
            {
                Id = SettingId.CloseButtonPolicy,
                Value = (int)s.Appearance.CloseButton,
                Options =
                [
                    new ChoiceOption(
                        (int)CloseButtonPolicy.ActiveTabOnly,
                        "仅活动标签",
                        "只有当前标签显示关闭按钮，最不容易误点。",
                        Illustration.TabsRounded),
                    new ChoiceOption(
                        (int)CloseButtonPolicy.OnHoverOnly,
                        "悬停时显示",
                        "光标停在哪个标签上，哪个才显示。",
                        Illustration.TabsRounded),
                    new ChoiceOption(
                        (int)CloseButtonPolicy.AllTabs,
                        "全部显示",
                        "每个标签都显示关闭按钮。",
                        Illustration.TabsSquare),
                    new ChoiceOption(
                        (int)CloseButtonPolicy.Never,
                        "从不显示",
                        "改用中键或右键菜单关闭标签。",
                        Illustration.TabsSquare),
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
                Title = "触发方式",
                Description = "拖动窗口时，什么条件下才进入分组判定。",
            },
            new ChoiceBlock
            {
                Id = SettingId.DragTrigger,
                Value = (int)s.Grouping.Trigger,
                Options =
                [
                    new ChoiceOption(
                        (int)DragTrigger.Always,
                        "总是",
                        "拖到另一个窗口的标题栏就会提示合并。",
                        Illustration.RailAbove),
                    new ChoiceOption(
                        (int)DragTrigger.RequireShift,
                        "按住 Shift",
                        "不按住时只是普通地拖动窗口，完全不会误分组。",
                        Illustration.TabsSquare),
                    new ChoiceOption(
                        (int)DragTrigger.RequireCtrl,
                        "按住 Ctrl",
                        "同上，改用 Ctrl 键。",
                        Illustration.TabsSquare),
                ],
            },

            new SectionBlock
            {
                Title = "中键点击标签",
            },
            new ChoiceBlock
            {
                Id = SettingId.MiddleClick,
                Value = (int)s.Grouping.MiddleClick,
                Options =
                [
                    new ChoiceOption(
                        (int)MiddleClickAction.CloseTab,
                        "关闭标签",
                        "与浏览器一致。",
                        Illustration.TabsRounded),
                    new ChoiceOption(
                        (int)MiddleClickAction.Nothing,
                        "不做任何事",
                        "避免误触关闭窗口。",
                        Illustration.TabsSquare),
                ],
            },

            new SectionBlock
            {
                Title = "手动拖放分组",
                Description = "把一个窗口拖到另一个窗口的标题栏即可合并。",
            },
            new ToggleBlock
            {
                Id = SettingId.SameApplicationOnly,
                Title = "仅允许同一应用的窗口分组",
                Description = "防止误把不相干的应用合并到一起。按住 Shift 可临时放宽这条限制。",
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

    /// <summary>已保存分组的摘要，由宿主注入 —— 领域层不读磁盘。</summary>
    public static IReadOnlyList<(string Primary, string Secondary)> SavedWorkspaceRows { get; set; } = [];

    private static PageContent Rules(AppSettings s)
    {
        var ruleRows = s.Rules
            .Select(r => (
                Primary: $"{r.Name}（{DescribeAction(r.Action)}）",
                Secondary: DescribeConditions(r)))
            .ToList();

        var hotkeyRows = s.Hotkeys.Bindings
            .Select(b => (
                Primary: HotkeySettings.DescribeCommand(b.Command),
                Secondary: b.Describe()))
            .ToList();

        var conflicts = s.Hotkeys.FindConflicts();

        var blocks = new List<ContentBlock>
        {
            new SectionBlock
            {
                Title = "全局热键",
                Description = "在任何应用里都能触发，用于在当前分组内快速切换。",
            },
            new ToggleBlock
            {
                Id = SettingId.HotkeysEnabled,
                Title = "启用全局热键",
                Description = "全局热键会占用整个系统的按键组合。与其他软件冲突时可在此一次性关掉。",
                Value = s.Hotkeys.Enabled,
            },
            new ListBlock
            {
                Title = "当前绑定",
                Items = hotkeyRows,
                EmptyText = "没有任何绑定。",
            },
        };

        if (conflicts.Count > 0)
        {
            // 冲突必须显式告知：Windows 只把组合交给先注册的那个，
            // 后者静默失效，用户看到的是"这个热键没反应"却毫无线索。
            blocks.Add(new InfoBlock
            {
                Text = "以下功能绑定了相同的组合，只有其中一个会生效：\n"
                    + string.Join("\n", conflicts.Select(c =>
                        $"· {HotkeySettings.DescribeCommand(c.A)} 与 {HotkeySettings.DescribeCommand(c.B)}")),
                IsWarning = true,
            });
        }

        blocks.AddRange(
        [
            new SectionBlock
            {
                Title = "已保存分组",
                Description = "在分组栏菜单里选「保存此分组」即可加入这里，之后从托盘菜单一键恢复。",
            },
            new ListBlock
            {
                Title = "已保存",
                Items = SavedWorkspaceRows,
                EmptyText = "还没有保存过分组。在分组栏上点右键，选「保存此分组」。",
            },

            new SectionBlock
            {
                Title = "自定义规则",
                Description = "按进程名、窗口类与标题的组合决定某个窗口能否分组。"
                    + "优先级固定为：阻止 > 允许 > 自动分组。",
            },
            new ListBlock
            {
                Title = "当前规则",
                Items = ruleRows,
                EmptyText = "还没有任何规则。当前版本可在数据目录的 settings.json 中编辑 rules 数组，"
                    + "图形化规则编辑器将在后续版本提供。",
            },
            new InfoBlock
            {
                Text = "阻止规则的优先级最高且不可调整。这是有意的：阻止表达的是"
                    + "「我不要这个应用被碰」，属于安全边界。若允许规则能盖过它，"
                    + "用户就无法可靠地把密码管理器、银行客户端这类应用排除在外。",
            },
        ]);

        return new PageContent
        {
            Page = SettingsPage.Rules,
            Title = "分组规则",
            Subtitle = "热键、已保存分组与自定义匹配规则。",
            Blocks = blocks,
        };
    }

    private static string DescribeAction(TabNest.Core.Rules.RuleAction action) => action switch
    {
        TabNest.Core.Rules.RuleAction.Block => "阻止分组",
        TabNest.Core.Rules.RuleAction.Allow => "允许分组",
        _ => "自动分组",
    };

    private static string DescribeConditions(TabNest.Core.Rules.Rule rule)
    {
        if (rule.Conditions.Count == 0)
        {
            return "没有任何条件，该规则不会生效。";
        }

        var parts = rule.Conditions.Select(c =>
        {
            var fields = new List<string>(3);

            if (!string.IsNullOrWhiteSpace(c.ProcessName))
            {
                fields.Add($"进程 {c.ProcessName}");
            }

            if (!string.IsNullOrWhiteSpace(c.ClassName))
            {
                fields.Add($"窗口类 {c.ClassName}");
            }

            if (!string.IsNullOrWhiteSpace(c.Title))
            {
                fields.Add($"标题{(c.PartialMatch ? "包含" : "等于")} {c.Title}");
            }

            return fields.Count == 0 ? "匹配任意窗口" : string.Join(" 且 ", fields);
        });

        return string.Join("；或 ", parts);
    }

    private static PageContent Colors(AppSettings s)
    {
        _ = s;

        return new PageContent
        {
            Page = SettingsPage.Colors,
            Title = "标签颜色",
            Subtitle = "分组栏的配色跟随系统明暗主题。",
            Blocks =
            [
                new SectionBlock
                {
                    Title = "当前配色",
                    Description = "分组栏使用矢量绘制，没有位图皮肤，因此在任何缩放比例下都保持清晰。",
                },
                new SwatchBlock
                {
                    Title = "分组栏",
                    Description = "跟随系统的浅色/深色设置自动切换。",
                    Swatches =
                    [
                        ("背景", 0xFFE8E8E8),
                        ("活动标签", 0xFFFFFFFF),
                        ("活动文字", 0xFF1A1A1A),
                        ("非活动文字", 0xFF606060),
                    ],
                },
                new InfoBlock
                {
                    Text = "按分组、按应用自定义强调色的功能已在数据模型中就位"
                        + "（每个分组与每个标签都可单独设色），图形化的取色界面将在后续版本提供。",
                },
            ],
        };
    }

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
            SettingId.HotkeysEnabled => s with
            {
                Hotkeys = s.Hotkeys with { Enabled = !s.Hotkeys.Enabled },
            },
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
