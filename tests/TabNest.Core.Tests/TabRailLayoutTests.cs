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
        // 20 个标签远超轨道宽度。宁可放不下也不能压到无法显示标题。
        var layout = Layout(20);

        Assert.All(layout.Tabs, t => Assert.True(t.Bounds.Width >= Metrics.MinTabWidth));
    }

    [Fact]
    public void 放不下的标签被计入溢出数量()
    {
        var layout = Layout(20);

        // 静默丢弃会让用户以为标签没了，必须能被 UI 提示出来。
        Assert.True(layout.HiddenTabCount > 0);
        Assert.Equal(20, layout.Tabs.Count + layout.HiddenTabCount);
    }

    [Fact]
    public void 标签放得下时溢出数量为零()
    {
        Assert.Equal(0, Layout(3).HiddenTabCount);
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

    // ------------------------------------------------------------------
    // 关闭整组按钮
    // ------------------------------------------------------------------

    [Fact]
    public void 关闭整组按钮位于右侧()
    {
        var layout = Layout(3);

        Assert.True(layout.CloseGroupButton.Width > 0);
        Assert.True(layout.CloseGroupButton.Right <= layout.Bounds.Width);
        Assert.True(layout.CloseGroupButton.Left > layout.Tabs[^1].Bounds.Left);
    }

    [Fact]
    public void 标签不会与关闭整组按钮重叠()
    {
        // 20 个标签的极端情况下，标签仍不得盖住关闭整组按钮 ——
        // 被盖住就等于用户再也关不掉这个组。
        var layout = Layout(20);

        Assert.All(
            layout.Tabs,
            t => Assert.True(
                t.Bounds.Right <= layout.CloseGroupButton.Left,
                $"标签右边界 {t.Bounds.Right} 越过了关闭按钮左边界 {layout.CloseGroupButton.Left}"));
    }

    // ------------------------------------------------------------------
    // 拖动区域
    // ------------------------------------------------------------------

    [Fact]
    public void 标签上不是拖动区()
    {
        var layout = Layout(3);

        Assert.False(layout.IsDragArea(layout.Tabs[0].Bounds.Center));
    }

    [Fact]
    public void 菜单按钮上不是拖动区()
    {
        var layout = Layout(3);

        Assert.False(layout.IsDragArea(layout.MenuButton.Center));
    }

    [Fact]
    public void 关闭整组按钮上不是拖动区()
    {
        var layout = Layout(3);

        Assert.False(layout.IsDragArea(layout.CloseGroupButton.Center));
    }

    [Fact]
    public void 标签之后的空白是拖动区()
    {
        var layout = Layout(2);
        var gap = new PixelPoint(
            layout.Tabs[^1].Bounds.Right + 10,
            layout.Bounds.Height / 2);

        Assert.True(layout.IsDragArea(gap));
    }

    // ------------------------------------------------------------------
    // 圆角跟随承载窗口
    // ------------------------------------------------------------------

    [Fact]
    public void 圆角半径原样透传不被DPI二次缩放()
    {
        // 半径由调用方按承载窗口实测得出，已是物理像素。
        // 若在 ScaleTo 里再缩放一次，高 DPI 下圆角会大得离谱。
        var metrics = (Metrics with { TopCornerRadius = 11 }).ScaleTo(144);

        Assert.Equal(11, metrics.TopCornerRadius);
    }

    [Fact]
    public void 方角窗口对应零圆角()
    {
        var metrics = Metrics with { TopCornerRadius = 0 };
        var layout = TabRailLayoutEngine.Compute(Tabs(2), TestData.Id(1), Anchor, metrics);

        Assert.Equal(0, layout.TopCornerRadius);
    }

    [Fact]
    public void 圆角半径传递到布局结果()
    {
        var metrics = Metrics with { TopCornerRadius = 8 };
        var layout = TabRailLayoutEngine.Compute(Tabs(2), TestData.Id(1), Anchor, metrics);

        Assert.Equal(8, layout.TopCornerRadius);
    }

    [Fact]
    public void 圆角窗口上分组栏向下覆盖圆角区域()
    {
        var metrics = Metrics with { TopCornerRadius = 8 };
        var layout = TabRailLayoutEngine.Compute(Tabs(2), TestData.Id(1), Anchor, metrics);

        // 只做到底边贴着窗口顶边是不够的：窗口自己的圆角会在分组栏正下方
        // 透出后面的桌面，看起来就是"没接上"。必须向下压住这段圆角区域。
        Assert.Equal(Anchor.Top + 8, layout.Bounds.Bottom);
    }

    [Fact]
    public void 方角窗口上分组栏不额外覆盖()
    {
        var layout = TabRailLayoutEngine.Compute(
            Tabs(2), TestData.Id(1), Anchor, Metrics with { TopCornerRadius = 0 });

        // 窗口本来就是方角，没有缺口要盖，多覆盖只会白白遮住内容。
        Assert.Equal(Anchor.Top, layout.Bounds.Bottom);
    }

    [Fact]
    public void 分组栏始终与窗口同宽()
    {
        // 用户改变窗口宽度时分组栏必须跟着变，否则一眼就能看出是两个东西。
        foreach (var width in (int[])[400, 900, 1600, 2560])
        {
            var anchor = PixelRect.FromSize(100, 200, width, 700);
            var layout = TabRailLayoutEngine.Compute(Tabs(3), TestData.Id(1), anchor, Metrics);

            Assert.Equal(anchor.Left, layout.Bounds.Left);
            Assert.Equal(anchor.Right, layout.Bounds.Right);
        }
    }

    // ------------------------------------------------------------------
    // 与窗口无缝衔接
    // ------------------------------------------------------------------

    [Fact]
    public void 轨道与窗口之间没有间隙()
    {
        var layout = Layout(3);

        // 差一个像素就会露出一条缝。
        Assert.Equal(Anchor.Top, layout.Bounds.Bottom);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void 各DPI下轨道都紧贴窗口且左右对齐(int dpi)
    {
        var layout = TabRailLayoutEngine.Compute(
            Tabs(3), TestData.Id(1), Anchor, Metrics.ScaleTo(dpi));

        Assert.Equal(Anchor.Top, layout.Bounds.Bottom);
        Assert.Equal(Anchor.Left, layout.Bounds.Left);
        Assert.Equal(Anchor.Right, layout.Bounds.Right);
    }
}
