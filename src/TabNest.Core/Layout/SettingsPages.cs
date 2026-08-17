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

    /// <param name="about">
    /// 关于页要展示的环境信息（版本号、数据目录）。由 App 层提供 ——
    /// 领域层不去读程序集元数据，也不知道文件系统在哪。传 null 时关于页只显示固定文案。
    /// </param>
    public static PageContent Build(
        SettingsPage page, AppSettings settings, AboutInfo? about = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (page is SettingsPage.About)
        {
            return About(about);
        }

        return page switch
        {
            SettingsPage.Overview => Overview(settings),
            SettingsPage.Appearance => Appearance(settings),
            SettingsPage.Grouping => Grouping(settings),
            SettingsPage.Colors => Colors(settings),
            SettingsPage.Rules => Rules(settings),
            _ => About(about),
        };
    }

    private static PageContent Overview(AppSettings s) => new()
    {
        Page = SettingsPage.Overview,
        Title = "总览",
        Subtitle = "最常用的几项设置。",
        Blocks =
        [
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
                        "只保留活动窗口的按钮",
                        "任务栏上只留一个按钮，对应当前活动的标签。切换标签时这个按钮会跟着变。",
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
                        "分组栏一直显示在窗口上方。",
                        Illustration.RailAbove),
                    new ChoiceOption(
                        (int)TabVisibility.AlwaysHidden,
                        "自动隐藏",
                        "平时不显示，鼠标移到窗口顶部时浮出，移开后重新隐藏。"
                        + "与自动隐藏的任务栏是同一种心智，既省空间又随时可用。",
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
                        "只在当前标签上",
                        "只有当前活动的那个标签显示关闭按钮，最不容易误点。",
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
                Description = "分组栏高度从 34 像素增加到 42 像素，标签更容易点中，代价是多占一点垂直空间。",
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
                Description = "在标题左侧显示该窗口所属应用的图标，同时开多个应用时更容易分辨。关闭后标题可用宽度更大。",
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
                Description = "在分组栏最右侧显示一个按钮，点它会关闭组内全部窗口。关闭此项可防止误触。",
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
        ],
    };

    /// <summary>已保存分组的摘要，由宿主注入 —— 领域层不读磁盘。</summary>
    public static IReadOnlyList<(string Primary, string Secondary)> SavedWorkspaceRows { get; set; } = [];

    private static PageContent Rules(AppSettings s)
    {
        var apps = TabNest.Core.Rules.AppList.Apps(s);
        var allowListOnly = s.Grouping.AllowListOnly;

        // 不在应用清单里管理的规则（用户手工写的复杂匹配）单独计数并提示。
        var customRuleCount = s.Rules.Count(r => !TabNest.Core.Rules.AppList.IsManaged(r));

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
                Title = "阻止/允许应用",
                Description = allowListOnly
                    ? "下列应用**才能**参与分组，其余一律不能。"
                    : "下列应用**不参与**分组，其余照常。",
            },
            new AppListBlock
            {
                Apps = apps,
                SelectedIndex = SelectedAppIndex,
                EmptyText = allowListOnly
                    ? "列表为空，且已勾选「仅允许指定应用」—— 当前没有任何应用能分组。"
                    : "列表为空，所有应用都可以分组。点「加入列表」添加要排除的应用。",
            },
            new ToggleBlock
            {
                Id = SettingId.AllowListOnly,
                Title = "仅允许指定应用在组中",
                Description = "勾选后上面的列表变成允许清单：只有列表里的应用能参与分组。"
                    + "不勾选时它是阻止清单：列表里的应用不参与分组。",
                Value = allowListOnly,
            },
        ]);

        if (customRuleCount > 0)
        {
            // 手工写进 settings.json 的复杂规则不在上面的列表里管理，
            // 但必须让用户知道它们存在 —— 否则会出现"列表是空的，某个窗口却分不了组"
            // 这种完全无从解释的现象。
            blocks.Add(new InfoBlock
            {
                Text = $"另有 {customRuleCount} 条自定义规则在 settings.json 中手工配置"
                    + "（按窗口类或标题匹配）。它们照常生效，但不在上面的列表里管理 ——"
                    + "把它们显示成一个光秃秃的进程名会掩盖真正的匹配条件，改一下就毁了。",
            });
        }

        return new PageContent
        {
            Page = SettingsPage.Rules,
            Title = "分组规则",
            Subtitle = "热键、已保存分组与应用清单。",
            Blocks = blocks,
        };
    }

    /// <summary>应用清单当前选中的行。由宿主维护 —— 它属于界面状态而非设置。</summary>
    public static int SelectedAppIndex { get; set; } = -1;

    /// <summary>各配色方案的样本色。由宿主注入实际主题值，领域层不知道具体颜色。</summary>
    public static Func<RailColorScheme, IReadOnlyList<(string Name, uint Argb)>>? SwatchProvider { get; set; }

    private static PageContent Colors(AppSettings s)
    {
        var scheme = s.Appearance.ColorScheme;

        var swatches = SwatchProvider?.Invoke(scheme)
            ?? [("背景", 0xFFE8E8E8), ("活动标签", 0xFFFFFFFF)];

        return new PageContent
        {
            Page = SettingsPage.Colors,
            Title = "标签颜色",
            Subtitle = "分组栏的配色方案。",
            Blocks =
            [
                new SectionBlock
                {
                    Title = "配色方案",
                    Description = "只提供几套预设而不是逐色可调：分组栏上只有背景、活动标签、文字这几种颜色，"
                        + "给每一种都配取色器，界面复杂度远超它带来的价值，而真正影响观感的只是浅底还是深底。",
                },
                new ChoiceBlock
                {
                    Id = SettingId.ColorScheme,
                    Value = (int)scheme,
                    Options =
                    [
                        new ChoiceOption(
                            (int)RailColorScheme.FollowSystem,
                            "跟随系统",
                            "随 Windows 的浅色/深色设置自动切换。",
                            Illustration.RailAbove),
                        new ChoiceOption(
                            (int)RailColorScheme.Light,
                            "浅色",
                            "浅灰底、深色字。与深色 IDE、终端对比强烈，分组栏边界一眼可辨。",
                            Illustration.TabsRounded),
                        new ChoiceOption(
                            (int)RailColorScheme.Dark,
                            "深色",
                            "深色底、浅色字。适合整体深色的桌面。",
                            Illustration.TabsSquare),
                    ],
                },

                new SwatchBlock
                {
                    Title = "当前配色预览",
                    Description = "分组栏为矢量绘制，没有位图皮肤，在任何缩放比例下都保持清晰。",
                    Swatches = swatches,
                },

                new InfoBlock
                {
                    Text = "单个分组与单个标签还可以有自己的强调色（在分组栏上点右键设置），"
                        + "它会以一条细窄竖条的形式显示，不做大面积填充 —— "
                        + "颜色服务于识别，不该成为装饰负担。",
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

    private static PageContent About(AboutInfo? about)
    {
        var blocks = new List<ContentBlock>(3);

        // 版本号放在最前面，且单独成块。
        //
        // 用户报问题时第一句总是"我用的哪个版本"，翻不到就只能猜；
        // 数据目录同理 —— 日志与配置都在那里，让人能直接找过去。
        if (about is not null)
        {
            blocks.Add(new SectionBlock
            {
                Title = $"TabNest {about.Version}",
                Description = about.DataDirectory is { Length: > 0 } dir
                    ? $"配置、分组快照与日志都在 {dir}"
                    : null,
            });
        }

        blocks.Add(new InfoBlock
        {
            Text = "TabNest —— 把任意 Windows 窗口组织成标签工作区。\n\n"
                + "所有设置与分组快照都保存在本机，不会上传任何数据。\n"
                + "日志只记录窗口标题与进程名这类定位问题必需的元数据，"
                + "绝不记录窗口内容、文档内容或键盘输入。",
        });

        return new PageContent
        {
            Page = SettingsPage.About,
            Title = "关于",
            Blocks = blocks,
        };
    }

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
            // 必须走 AppList.SetAllowListOnly：这个开关决定的是**上面那份列表的含义**，
            // 勾上是允许清单、不勾是阻止清单。只改开关不改清单里各条规则的动作，
            // 会让同一批应用在两种模式下表达完全相反的意思，而界面上看不出任何差别。
            SettingId.AllowListOnly =>
                TabNest.Core.Rules.AppList.SetAllowListOnly(s, !s.Grouping.AllowListOnly),
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
            SettingId.TaskbarButtons => s with { TaskbarButtons = (TaskbarButtonPolicy)value },
            SettingId.RoundedTabs => s with
            {
                Appearance = s.Appearance with { RoundedTabs = value != 0 },
            },
            SettingId.ColorScheme => s with
            {
                Appearance = s.Appearance with { ColorScheme = (RailColorScheme)value },
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
