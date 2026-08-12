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
    public void 关闭按钮策略改变关闭按钮的出现()
    {
        var activeOnly = LayoutWith(Defaults with { CloseButton = CloseButtonPolicy.ActiveTabOnly });
        var never = LayoutWith(Defaults with { CloseButton = CloseButtonPolicy.Never });
        var all = LayoutWith(Defaults with { CloseButton = CloseButtonPolicy.AllTabs });

        Assert.All(never.Tabs, t => Assert.Equal(0, t.CloseBounds.Width));
        Assert.All(all.Tabs, t => Assert.True(t.CloseBounds.Width > 0));

        // 仅活动标签：有且仅有一个标签带关闭按钮。
        Assert.Equal(1, activeOnly.Tabs.Count(t => t.CloseBounds.Width > 0));
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
    [InlineData(TabVisibility.AlwaysVisible, false, false, false, true)]
    [InlineData(TabVisibility.ActiveWindowOnly, true, false, false, true)]
    [InlineData(TabVisibility.ActiveWindowOnly, false, false, false, false)]
    [InlineData(TabVisibility.HideWhenMaximized, false, true, false, false)]
    [InlineData(TabVisibility.HideWhenMaximized, false, false, false, true)]
    [InlineData(TabVisibility.AlwaysHidden, true, false, false, false)]
    [InlineData(TabVisibility.AlwaysHidden, true, false, true, true)]
    public void 可见性策略决定分组栏是否显示(
        TabVisibility visibility, bool foreground, bool maximized, bool nearTop, bool expected)
    {
        var actual = RailAppearance.ShouldShowRail(
            visibility, foreground, maximized, nearTop, liveTabCount: 2, hideWhenSingleTab: false);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void 最大化隐藏时光标移到顶部仍能浮出()
    {
        // 不给浮出机会的话，窗口一最大化就再也切不了标签，只能先还原窗口。
        var hidden = RailAppearance.ShouldShowRail(
            TabVisibility.HideWhenMaximized, true, isOwnerMaximized: true,
            isPointerNearTop: false, liveTabCount: 2, hideWhenSingleTab: false);

        var floated = RailAppearance.ShouldShowRail(
            TabVisibility.HideWhenMaximized, true, isOwnerMaximized: true,
            isPointerNearTop: true, liveTabCount: 2, hideWhenSingleTab: false);

        Assert.False(hidden);
        Assert.True(floated);
    }

    [Fact]
    public void 单标签隐藏优先于任何可见性策略()
    {
        var shown = RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysVisible, true, false, false,
            liveTabCount: 1, hideWhenSingleTab: false);

        var hidden = RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysVisible, true, false, false,
            liveTabCount: 1, hideWhenSingleTab: true);

        Assert.True(shown);
        Assert.False(hidden);
    }

    [Fact]
    public void 组内没有标签时绝不显示分组栏()
    {
        Assert.False(RailAppearance.ShouldShowRail(
            TabVisibility.AlwaysVisible, true, false, true,
            liveTabCount: 0, hideWhenSingleTab: false));
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
