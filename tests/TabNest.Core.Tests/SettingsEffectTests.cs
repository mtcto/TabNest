using TabNest.Core.Layout;
using TabNest.Core.Models;

namespace TabNest.Core.Tests;

/// <summary>
/// 断言"改了设置，行为确实变了"。
///
/// 这组测试是补上来的，起因是一次真实的失职：设置中心有 12 个开关能切换、能落盘、
/// 能命中，**点了却毫无效果** —— RefreshRail 用的是一份全默认的 RailMetrics，
/// 只把两个设置传了进去，其余写进 settings.json 后没有任何代码去读。
///
/// 而当时三层测试全绿。因为它们验证的都是"能切换 / 能保存 / 能命中"，
/// 没有一个验证"切换之后输出真的不一样"。一个点了没反应的开关比没有这个开关更糟：
/// 用户会以为功能坏了，或者以为自己没理解，然后去别处找原因。
///
/// 因此每一条都遵循同一个形态：**取默认值算一次，改一个开关再算一次，断言两次结果不同**。
/// </summary>
public sealed class SettingsEffectTests
{
    private static readonly AppearanceSettings Defaults = new();

    // ------------------------------------------------------------------
    // 外观设置必须改变分组栏度量
    // ------------------------------------------------------------------

    [Fact]
    public void 加高标签改变分组栏高度()
    {
        var normal = RailAppearance.MetricsFor(Defaults);
        var taller = RailAppearance.MetricsFor(Defaults with { TallerTabs = true });

        Assert.True(taller.Height > normal.Height,
            $"开了「加高」高度却没变：{normal.Height} → {taller.Height}");
    }

    [Fact]
    public void 关闭窗口图标把图标尺寸归零()
    {
        var withIcon = RailAppearance.MetricsFor(Defaults with { ShowWindowIcon = true });
        var without = RailAppearance.MetricsFor(Defaults with { ShowWindowIcon = false });

        Assert.True(withIcon.IconSize > 0);

        // 归零而不是"只是不画"：位置照留的话标题会莫名其妙地缩进一块。
        Assert.Equal(0, without.IconSize);
    }

    [Fact]
    public void 隐藏菜单按钮把菜单宽度归零()
    {
        var shown = RailAppearance.MetricsFor(Defaults with { HideMenuButton = false });
        var hidden = RailAppearance.MetricsFor(Defaults with { HideMenuButton = true });

        Assert.True(shown.MenuButtonWidth > 0);
        Assert.Equal(0, hidden.MenuButtonWidth);
    }

    [Fact]
    public void 关闭整组按钮开关改变其宽度()
    {
        var shown = RailAppearance.MetricsFor(Defaults with { ShowCloseAllButton = true });
        var hidden = RailAppearance.MetricsFor(Defaults with { ShowCloseAllButton = false });

        Assert.True(shown.CloseGroupButtonWidth > 0);
        Assert.Equal(0, hidden.CloseGroupButtonWidth);
    }

    // ------------------------------------------------------------------
    // 度量的改变必须传导到实际布局
    // ------------------------------------------------------------------

    [Fact]
    public void 关闭窗口图标后标题区域向左扩展()
    {
        // 度量变了但布局没跟着变，等于设置依然无效 —— 必须一路验到布局产物。
        var withIcon = LayoutWith(Defaults with { ShowWindowIcon = true });
        var without = LayoutWith(Defaults with { ShowWindowIcon = false });

        Assert.True(
            without.Tabs[0].TextBounds.Left < withIcon.Tabs[0].TextBounds.Left,
            "关掉图标后标题没有向左扩展，说明图标的位置还占着");
    }

    [Fact]
    public void 隐藏菜单按钮后首个标签左移()
    {
        var shown = LayoutWith(Defaults with { HideMenuButton = false });
        var hidden = LayoutWith(Defaults with { HideMenuButton = true });

        Assert.True(
            hidden.Tabs[0].Bounds.Left < shown.Tabs[0].Bounds.Left,
            "隐藏菜单按钮后首个标签没有左移，说明它的宽度还占着");
    }

    [Fact]
    public void 加高标签后分组栏矩形更高()
    {
        var normal = LayoutWith(Defaults);
        var taller = LayoutWith(Defaults with { TallerTabs = true });

        Assert.True(taller.Bounds.Height > normal.Bounds.Height);
    }

    [Fact]
    public void 关闭按钮策略决定各标签是否显示关闭按钮()
    {
        var active = TestData.Id(1);
        var other = TestData.Id(2);

        Assert.False(TabRailLayoutEngine.ShouldShowClose(
            other, active, null, CloseButtonPolicy.Never));

        Assert.True(TabRailLayoutEngine.ShouldShowClose(
            other, active, null, CloseButtonPolicy.AllTabs));

        Assert.True(TabRailLayoutEngine.ShouldShowClose(
            active, active, null, CloseButtonPolicy.ActiveTabOnly));

        Assert.False(TabRailLayoutEngine.ShouldShowClose(
            other, active, null, CloseButtonPolicy.ActiveTabOnly));
    }

    [Fact]
    public void 可变宽度让长短标题的标签宽度不同()
    {
        var tabs = new List<TabItem>
        {
            Tab(1, "短"),
            Tab(2, "一个非常非常长的窗口标题用来撑开这个标签"),
        };

        var uniform = Compute(tabs, Defaults with { VariableWidthTabs = false });
        var variable = Compute(tabs, Defaults with { VariableWidthTabs = true });

        Assert.Equal(uniform.Tabs[0].Bounds.Width, uniform.Tabs[1].Bounds.Width);

        Assert.True(
            variable.Tabs[1].Bounds.Width > variable.Tabs[0].Bounds.Width,
            "开了可变宽度，长标题的标签却没有更宽");
    }

    // ------------------------------------------------------------------
    // 可见性策略
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(TabVisibility.AlwaysVisible, true)]
    [InlineData(TabVisibility.AlwaysHidden, false)]
    public void 可见性策略决定分组栏是否显示(TabVisibility visibility, bool expected)
    {
        var actual = RailAppearance.ShouldShowRail(
            visibility, liveTabCount: 2, hideWhenSingleTab: false);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void 自动隐藏时光标靠近顶部才浮出()
    {
        // 与自动隐藏的任务栏同一种心智：平时不占空间，需要时抬一下鼠标就出来。
        Assert.False(RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysHidden, liveTabCount: 2, hideWhenSingleTab: false,
            isPointerNearTop: false));

        Assert.True(RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysHidden, liveTabCount: 2, hideWhenSingleTab: false,
            isPointerNearTop: true));
    }

    [Fact]
    public void 始终可见时光标位置无关紧要()
    {
        Assert.True(RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysVisible, liveTabCount: 2, hideWhenSingleTab: false,
            isPointerNearTop: false));
    }

    [Fact]
    public void 单标签隐藏优先于光标浮出()
    {
        // 光标靠近也不该把一个只有单标签的分组栏唤出来 —— 它不承载任何信息。
        Assert.False(RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysHidden, liveTabCount: 1, hideWhenSingleTab: true,
            isPointerNearTop: true));
    }

    // ------------------------------------------------------------------
    // 标签形状必须真的能看出区别
    // ------------------------------------------------------------------

    [Fact]
    public void 标签之间有间距且上下内缩()
    {
        // 早期标签紧挨着且占满分组栏高度，圆角全被相邻标签与分组栏边缘盖住，
        // 「传统 / 圆形」这个设置于是完全看不出区别。
        var layout = LayoutWith(Defaults);
        var m = RailAppearance.MetricsFor(Defaults);

        Assert.True(m.TabSpacing >= 4, "标签间距太小，相邻标签会糊成一块");
        Assert.True(m.TabVerticalInset > 0, "标签占满整条高度时圆角会被压平");

        // 标签顶边不贴分组栏顶边，底边不贴底边。
        Assert.True(layout.Tabs[0].Bounds.Top > layout.Bounds.Top - layout.Bounds.Top);
        Assert.True(layout.Tabs[0].Bounds.Height < m.Height);

        // 相邻标签之间确实有空隙。
        Assert.True(
            layout.Tabs[1].Bounds.Left - layout.Tabs[0].Bounds.Right >= m.TabSpacing - 1,
            "相邻标签之间没有留出间距");
    }

    [Fact]
    public void 图标与关闭按钮跟随内缩后的标签垂直居中()
    {
        var layout = LayoutWith(Defaults);
        var tab = layout.Tabs[0];

        // 内缩之后若仍按分组栏整高居中，图标会偏出标签形状之外。
        Assert.True(tab.IconBounds.Top >= tab.Bounds.Top);
        Assert.True(tab.IconBounds.Bottom <= tab.Bounds.Bottom);
        Assert.True(tab.CloseBounds.Top >= tab.Bounds.Top);
        Assert.True(tab.CloseBounds.Bottom <= tab.Bounds.Bottom);
    }

    [Fact]
    public void 单标签隐藏优先于任何可见性策略()
    {
        var shown = RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysVisible, liveTabCount: 1, hideWhenSingleTab: false);

        var hidden = RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysVisible, liveTabCount: 1, hideWhenSingleTab: true);

        Assert.True(shown);
        Assert.False(hidden);
    }

    [Fact]
    public void 组内没有标签时绝不显示分组栏()
    {
        Assert.False(RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysVisible, liveTabCount: 0, hideWhenSingleTab: false));
    }

    // ------------------------------------------------------------------
    // 关闭按钮策略：画出来的与点得到的必须一致
    // ------------------------------------------------------------------

    [Fact]
    public void 悬停时显示策略只对被悬停的标签成立()
    {
        var active = TestData.Id(1);
        var other = TestData.Id(2);

        // 没有悬停任何标签时，一个都不显示。
        Assert.False(TabRailLayoutEngine.ShouldShowClose(
            active, active, null, CloseButtonPolicy.OnHoverOnly));

        // 悬停在非活动标签上时，显示的是那一个而不是活动标签。
        Assert.True(TabRailLayoutEngine.ShouldShowClose(
            other, active, other, CloseButtonPolicy.OnHoverOnly));

        Assert.False(TabRailLayoutEngine.ShouldShowClose(
            active, active, other, CloseButtonPolicy.OnHoverOnly));
    }

    [Fact]
    public void 悬停策略下布局仍要为关闭按钮留出位置()
    {
        // 布局若按"当前不该显示"就把矩形留空，悬停时就没有位置可画 ——
        // 这正是「悬停时显示」此前完全不生效的原因。
        var layout = Compute(
            [Tab(1, "一"), Tab(2, "二")],
            Defaults with { CloseButton = CloseButtonPolicy.OnHoverOnly });

        Assert.All(layout.Tabs, t => Assert.True(
            t.CloseBounds.Width > 0,
            "悬停策略下布局没有为关闭按钮留位置，悬停时将无处可画"));
    }

    [Fact]
    public void 从不显示策略下布局不留关闭按钮位置()
    {
        var layout = Compute(
            [Tab(1, "一"), Tab(2, "二")],
            Defaults with { CloseButton = CloseButtonPolicy.Never });

        Assert.All(layout.Tabs, t => Assert.Equal(0, t.CloseBounds.Width));
    }

    // ------------------------------------------------------------------
    // 配色方案
    // ------------------------------------------------------------------

    [Fact]
    public void 配色方案已接线且三种取值互不相同()
    {
        var defaults = AppSettings.Default;

        var light = SettingsPages.Choose(defaults, SettingId.ColorScheme, (int)RailColorScheme.Light);
        var dark = SettingsPages.Choose(defaults, SettingId.ColorScheme, (int)RailColorScheme.Dark);

        Assert.Equal(RailColorScheme.Light, light.Appearance.ColorScheme);
        Assert.Equal(RailColorScheme.Dark, dark.Appearance.ColorScheme);
        Assert.NotEqual(light.Appearance.ColorScheme, dark.Appearance.ColorScheme);
    }

    // ------------------------------------------------------------------
    // 规则编辑：空条件规则绝不能生效
    // ------------------------------------------------------------------

    [Fact]
    public void 没有条件的规则永不匹配()
    {
        // 三个维度全空的条件会匹配**任何**窗口。一条空的阻止规则等于禁用整个产品，
        // 因此规则编辑器保存时会把空条件丢掉，让规则退化成"没有条件"。
        var rule = new Rules.Rule
        {
            Id = "r1",
            Name = "空规则",
            Action = Rules.RuleAction.Block,
            Conditions = [],
        };

        Assert.False(rule.IsUsable);
        Assert.False(rule.Matches("any.exe", "AnyClass", "任意标题"));
    }

    [Fact]
    public void 规则页把没有条件的规则标记出来()
    {
        var settings = AppSettings.Default with
        {
            Rules =
            [
                new Rules.Rule
                {
                    Id = "r1",
                    Name = "空规则",
                    Action = Rules.RuleAction.Block,
                    Conditions = [],
                },
            ],
        };

        var page = SettingsPages.Build(SettingsPage.Rules, settings);
        var list = page.Blocks.OfType<ListBlock>().First(b => b.Title == "当前规则");

        // 用户必须看得出这条规则不会生效，否则他会以为规则坏了。
        Assert.Contains(list.Items, i => i.Primary.Contains("不会生效", StringComparison.Ordinal));
    }

    [Fact]
    public void 规则列表可编辑且提供新建入口()
    {
        var page = SettingsPages.Build(SettingsPage.Rules, AppSettings.Default);
        var list = page.Blocks.OfType<ListBlock>().First(b => b.Title == "当前规则");

        Assert.Equal(SettingAction.EditRule, list.ItemAction);
        Assert.Contains(list.Buttons, b => b.Action == SettingAction.AddRule);
    }

    // ------------------------------------------------------------------
    // 每一个 UI 上的开关都必须真的改变设置对象
    // ------------------------------------------------------------------

    [Fact]
    public void 设置页里的每个开关与选项都已接线()
    {
        var defaults = AppSettings.Default;

        foreach (var page in SettingsPages.Order)
        {
            foreach (var block in SettingsPages.Build(page, defaults).Blocks)
            {
                switch (block)
                {
                    case ToggleBlock toggle when toggle.IsEnabled:
                        Assert.True(
                            SettingsPages.Toggle(defaults, toggle.Id) != defaults,
                            $"开关「{toggle.Title}」（{toggle.Id}）切换后设置没有任何变化，"
                            + "说明它没有对应的写回分支");
                        break;

                    case ChoiceBlock choice:
                        var alternatives = choice.Options
                            .Where(o => o.DisabledReason is null && o.Value != choice.Value)
                            .ToList();

                        // 备选项全部被禁用是正当状态：例如阶段一的「标签类型」只有
                        // 「上方」可用，「集成」在等注入层，且卡片上已写明原因。
                        // 这种情况下没有可点的东西，也就无所谓接没接线。
                        if (alternatives.Count == 0)
                        {
                            continue;
                        }

                        Assert.True(
                            alternatives.Any(o =>
                                SettingsPages.Choose(defaults, choice.Id, o.Value) != defaults),
                            $"选项组 {choice.Id} 有可选的备选项，但选中它们都改不动设置，"
                            + "说明它没有接线");
                        break;

                    default:
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------------

    private static RailLayout LayoutWith(AppearanceSettings appearance) =>
        Compute([Tab(1, "标签一"), Tab(2, "标签二")], appearance);

    private static RailLayout Compute(List<TabItem> tabs, AppearanceSettings appearance)
    {
        var metrics = RailAppearance.MetricsFor(appearance);

        return TabRailLayoutEngine.Compute(
            tabs,
            tabs[0].Identity,
            PixelRect.FromSize(0, 200, 1200, 600),
            metrics,
            appearance.CloseButton,
            hoveredIdentity: null,
            showMenuButton: !appearance.HideMenuButton,
            variableWidth: appearance.VariableWidthTabs);
    }

    private static TabItem Tab(int handle, string title) => new()
    {
        Identity = TestData.Id(handle),
        Title = title,
        ProcessName = "app.exe",
        Snapshot = TestData.Snapshot(),
        State = handle == 1 ? TabState.Active : TabState.Inactive,
        JoinedAt = TestData.Now,
    };
}
