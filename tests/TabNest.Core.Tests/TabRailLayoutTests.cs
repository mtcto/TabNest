using TabNest.Core.Layout;
using TabNest.Core.Models;

namespace TabNest.Core.Tests;

public sealed class TabRailLayoutTests
{
    private static readonly RailMetrics Metrics = new();

    private static readonly PixelRect Anchor = PixelRect.FromSize(100, 200, 1000, 700);

    private static List<TabItem> Tabs(int count)
    {
        var list = new List<TabItem>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new TabItem
            {
                Identity = TestData.Id(i + 1),
                Title = $"标签{i + 1}",
                ProcessName = "app.exe",
                Snapshot = TestData.Snapshot(),
                State = i == 0 ? TabState.Active : TabState.Inactive,
                JoinedAt = TestData.Now,
            });
        }

        return list;
    }

    private static RailLayout Layout(
        int tabCount,
        CloseButtonPolicy policy = CloseButtonPolicy.ActiveTabOnly,
        WindowIdentity? hovered = null) =>
        TabRailLayoutEngine.Compute(
            Tabs(tabCount), TestData.Id(1), Anchor, Metrics, policy, hovered);

    [Fact]
    public void 轨道紧贴承载窗口上沿()
    {
        var layout = Layout(3);

        // 轨道底边必须正好等于窗口顶边，差一个像素都会露出缝隙。
        Assert.Equal(Anchor.Top, layout.Bounds.Bottom);
        Assert.Equal(Anchor.Top - Metrics.Height, layout.Bounds.Top);
        Assert.Equal(Anchor.Left, layout.Bounds.Left);
        Assert.Equal(Anchor.Width, layout.Bounds.Width);
    }

    [Fact]
    public void 标签数量与存活标签一致()
    {
        Assert.Equal(5, Layout(5).Tabs.Count);
    }

    [Fact]
    public void 已关闭的标签不参与布局()
    {
        var tabs = Tabs(3);
        tabs[1] = tabs[1] with { State = TabState.Closed };

        var layout = TabRailLayoutEngine.Compute(tabs, TestData.Id(1), Anchor, Metrics);

        Assert.Equal(2, layout.Tabs.Count);
        Assert.DoesNotContain(layout.Tabs, t => t.Identity == TestData.Id(2));
    }

    [Fact]
    public void 标签之间不重叠()
    {
        var layout = Layout(6);

        for (var i = 1; i < layout.Tabs.Count; i++)
        {
            Assert.True(
                layout.Tabs[i].Bounds.Left >= layout.Tabs[i - 1].Bounds.Right,
                $"标签 {i} 与前一个重叠");
        }
    }

    [Fact]
    public void 标签宽度不低于最小值()
    {
        // 20 个标签远超轨道宽度。宁可溢出也不能压到无法显示标题。
        var layout = Layout(20);

        Assert.All(layout.Tabs, t => Assert.True(t.Bounds.Width >= Metrics.MinTabWidth));
    }

    [Fact]
    public void 标签宽度不超过最大值()
    {
        // 只有两个标签时不该把它们拉伸到占满整条轨道。
        var layout = Layout(2);

        Assert.All(layout.Tabs, t => Assert.True(t.Bounds.Width <= Metrics.MaxTabWidth));
    }

    [Fact]
    public void 菜单按钮之后才开始排标签()
    {
        var layout = Layout(3);

        Assert.True(layout.Tabs[0].Bounds.Left >= layout.MenuButton.Right);
    }

    [Fact]
    public void 隐藏菜单按钮时标签左移()
    {
        var withMenu = Layout(3);
        var without = TabRailLayoutEngine.Compute(
            Tabs(3), TestData.Id(1), Anchor, Metrics, showMenuButton: false);

        Assert.Equal(0, without.MenuButton.Width);
        Assert.True(without.Tabs[0].Bounds.Left < withMenu.Tabs[0].Bounds.Left);
    }

    // ------------------------------------------------------------------
    // 关闭按钮策略
    // ------------------------------------------------------------------

    [Fact]
    public void 仅活动标签显示关闭按钮()
    {
        var layout = Layout(3, CloseButtonPolicy.ActiveTabOnly);

        Assert.True(layout.Tabs[0].CloseBounds.Width > 0);
        Assert.Equal(0, layout.Tabs[1].CloseBounds.Width);
    }

    [Fact]
    public void 所有标签显示关闭按钮()
    {
        var layout = Layout(3, CloseButtonPolicy.AllTabs);

        Assert.All(layout.Tabs, t => Assert.True(t.CloseBounds.Width > 0));
    }

    [Fact]
    public void 从不显示关闭按钮()
    {
        var layout = Layout(3, CloseButtonPolicy.Never);

        Assert.All(layout.Tabs, t => Assert.Equal(0, t.CloseBounds.Width));
    }

    [Fact]
    public void 仅悬停标签显示关闭按钮()
    {
        var layout = Layout(3, CloseButtonPolicy.OnHoverOnly, hovered: TestData.Id(2));

        Assert.Equal(0, layout.Tabs[0].CloseBounds.Width);
        Assert.True(layout.Tabs[1].CloseBounds.Width > 0);
    }

    [Fact]
    public void 关闭按钮显隐不改变标题区域宽度()
    {
        var withClose = Layout(3, CloseButtonPolicy.AllTabs);
        var withoutClose = Layout(3, CloseButtonPolicy.Never);

        // 否则悬停时按钮出现会把标题挤得跳动一下。
        Assert.Equal(withClose.Tabs[0].TextBounds.Width, withoutClose.Tabs[0].TextBounds.Width);
    }

    // ------------------------------------------------------------------
    // 命中测试
    // ------------------------------------------------------------------

    [Fact]
    public void 点击标签中心命中该标签()
    {
        var layout = Layout(4);
        var target = layout.Tabs[2];

        var hit = layout.HitTestTab(target.Bounds.Center);

        Assert.Equal(target.Identity, hit!.Value.Identity);
    }

    [Fact]
    public void 点击标签之外不命中()
    {
        var layout = Layout(2);

        Assert.Null(layout.HitTestTab(new PixelPoint(layout.Bounds.Width - 10, 5)));
    }

    [Fact]
    public void 关闭按钮命中测试独立于标签()
    {
        var layout = Layout(3, CloseButtonPolicy.AllTabs);
        var target = layout.Tabs[1];

        var hit = layout.HitTestCloseButton(target.CloseBounds.Center);

        // 关闭按钮必须先于标签判定，否则点关闭会变成切换标签。
        Assert.Equal(target.Identity, hit!.Value.Identity);
    }

    [Fact]
    public void 未显示关闭按钮时不会误命中()
    {
        var layout = Layout(3, CloseButtonPolicy.Never);
        var target = layout.Tabs[1];

        Assert.Null(layout.HitTestCloseButton(target.Bounds.Center));
    }

    // ------------------------------------------------------------------
    // 拖拽插入位置
    // ------------------------------------------------------------------

    [Fact]
    public void 拖到第一个标签左半边插入到最前()
    {
        var layout = Layout(4);
        var point = new PixelPoint(layout.Tabs[0].Bounds.Left + 2, 10);

        Assert.Equal(0, layout.InsertionIndexAt(point));
    }

    [Fact]
    public void 拖到标签右半边插入到其后()
    {
        var layout = Layout(4);
        var point = new PixelPoint(layout.Tabs[1].Bounds.Right - 2, 10);

        Assert.Equal(2, layout.InsertionIndexAt(point));
    }

    [Fact]
    public void 拖到轨道最右侧插入到末尾()
    {
        var layout = Layout(4);

        Assert.Equal(4, layout.InsertionIndexAt(new PixelPoint(layout.Bounds.Width, 10)));
    }

    // ------------------------------------------------------------------
    // DPI 缩放
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(96, 34)]
    [InlineData(120, 43)]
    [InlineData(144, 51)]
    public void 度量按DPI缩放(int dpi, int expectedHeight)
    {
        Assert.Equal(expectedHeight, Metrics.ScaleTo(dpi).Height);
    }

    [Fact]
    public void 高DPI下轨道更高且仍紧贴窗口()
    {
        var scaled = Metrics.ScaleTo(144);
        var layout = TabRailLayoutEngine.Compute(Tabs(3), TestData.Id(1), Anchor, scaled);

        Assert.Equal(Anchor.Top, layout.Bounds.Bottom);
        Assert.Equal(scaled.Height, layout.Bounds.Height);
    }

    [Fact]
    public void 空组返回空布局而不抛异常()
    {
        var layout = TabRailLayoutEngine.Compute([], TestData.Id(1), Anchor, Metrics);

        Assert.Empty(layout.Tabs);
        Assert.Equal(Anchor.Top, layout.Bounds.Bottom);
    }
}
