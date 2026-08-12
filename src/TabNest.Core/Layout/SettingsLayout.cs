using TabNest.Core.Models;

namespace TabNest.Core.Layout;

/// <summary>设置中心的页面。顺序即导航栏顺序，对齐 Groupy 的信息架构。</summary>
public enum SettingsPage
{
    Overview = 0,
    Appearance = 1,
    Colors = 2,
    Grouping = 3,
    Rules = 4,
    About = 5,
}

/// <summary>
/// 设置中心的视觉度量，单位为 96 DPI 下的逻辑像素，由 <see cref="ScaleTo"/> 换算成物理像素。
///
/// 集中定义而非散落在绘制代码里：手写 UI 最容易失控的就是每处各自决定间距，
/// 最后凑出十几种相近但不相等的边距，整体观感松散却找不到具体是哪里错。
/// </summary>
public sealed record SettingsMetrics
{
    /// <summary>左侧导航栏宽度。</summary>
    public int NavWidth { get; init; } = 200;

    /// <summary>导航项高度。</summary>
    public int NavItemHeight { get; init; } = 38;

    /// <summary>导航项左右内边距。</summary>
    public int NavItemPadding { get; init; } = 12;

    /// <summary>导航栏顶部留白，让首项不贴着窗口边。</summary>
    public int NavTopPadding { get; init; } = 12;

    /// <summary>选中导航项左侧的指示条宽度。</summary>
    public int NavIndicatorWidth { get; init; } = 3;

    /// <summary>内容区四周内边距。</summary>
    public int ContentPadding { get; init; } = 24;

    /// <summary>页面标题下方到首个分区的间距。</summary>
    public int TitleGap { get; init; } = 20;

    /// <summary>分区之间的间距。</summary>
    public int SectionGap { get; init; } = 24;

    /// <summary>分区标题下方到内容的间距。</summary>
    public int SectionHeaderGap { get; init; } = 10;

    /// <summary>开关行高度（不含说明文字）。</summary>
    public int RowHeight { get; init; } = 44;

    /// <summary>行内左右内边距。</summary>
    public int RowPadding { get; init; } = 14;

    /// <summary>说明文字与标题之间的行距。</summary>
    public int RowTextGap { get; init; } = 3;

    /// <summary>说明文字下方留白。</summary>
    public int RowBottomPadding { get; init; } = 12;

    /// <summary>
    /// 相邻开关行之间的间隔。
    ///
    /// 不能为 0：连续的行会拼成一整块白色，视觉上分不出这是七个独立开关
    /// 还是一个七行的表格，悬停高亮时也看不出边界在哪。
    /// </summary>
    public int RowGap { get; init; } = 6;

    /// <summary>开关控件尺寸。</summary>
    public int ToggleWidth { get; init; } = 40;

    public int ToggleHeight { get; init; } = 20;

    /// <summary>图示选择卡片的尺寸。</summary>
    public int CardWidth { get; init; } = 168;

    public int CardHeight { get; init; } = 132;

    /// <summary>卡片之间的间距。</summary>
    public int CardGap { get; init; } = 12;

    /// <summary>卡片内图示区域的高度。</summary>
    public int CardIllustrationHeight { get; init; } = 72;

    /// <summary>圆角半径。</summary>
    public int CornerRadius { get; init; } = 6;

    /// <summary>滚动条宽度。</summary>
    public int ScrollBarWidth { get; init; } = 10;

    /// <summary>滚轮一格滚动的距离。</summary>
    public int WheelStep { get; init; } = 60;

    /// <summary>窗口最小尺寸。小于此宽度卡片会挤成一列，信息密度反而下降。</summary>
    public int MinWindowWidth { get; init; } = 860;

    public int MinWindowHeight { get; init; } = 600;

    public SettingsMetrics ScaleTo(int dpi)
    {
        if (dpi == 96)
        {
            return this;
        }

        var s = dpi / 96.0;

        // 与 RailMetrics 一致地显式远离零舍入：默认的银行家舍入会让
        // 同一套度量在不同缩放下表现不可预测。
        int S(int v) => (int)Math.Round(v * s, MidpointRounding.AwayFromZero);

        return new SettingsMetrics
        {
            NavWidth = S(NavWidth),
            NavItemHeight = S(NavItemHeight),
            NavItemPadding = S(NavItemPadding),
            NavTopPadding = S(NavTopPadding),
            NavIndicatorWidth = S(NavIndicatorWidth),
            ContentPadding = S(ContentPadding),
            TitleGap = S(TitleGap),
            SectionGap = S(SectionGap),
            SectionHeaderGap = S(SectionHeaderGap),
            RowHeight = S(RowHeight),
            RowPadding = S(RowPadding),
            RowTextGap = S(RowTextGap),
            RowBottomPadding = S(RowBottomPadding),
            RowGap = S(RowGap),
            ToggleWidth = S(ToggleWidth),
            ToggleHeight = S(ToggleHeight),
            CardWidth = S(CardWidth),
            CardHeight = S(CardHeight),
            CardGap = S(CardGap),
            CardIllustrationHeight = S(CardIllustrationHeight),
            CornerRadius = S(CornerRadius),
            ScrollBarWidth = S(ScrollBarWidth),
            WheelStep = S(WheelStep),
            MinWindowWidth = S(MinWindowWidth),
            MinWindowHeight = S(MinWindowHeight),
        };
    }
}

/// <summary>一个导航项的位置。</summary>
/// <param name="Page">对应页面。</param>
/// <param name="Bounds">整项矩形，相对窗口客户区。</param>
/// <param name="IndicatorBounds">选中指示条矩形。</param>
/// <param name="TextBounds">文字绘制区域。</param>
public readonly record struct NavItemLayout(
    SettingsPage Page,
    PixelRect Bounds,
    PixelRect IndicatorBounds,
    PixelRect TextBounds);

/// <summary>
/// 设置中心窗口的骨架布局：导航栏、内容视口、滚动条。
///
/// 页面内部的元素布局由 <see cref="ContentBuilder"/> 单独计算 ——
/// 它需要测量文本高度，依赖绘图设备，因此不能放在这个纯函数里。
/// </summary>
public sealed record SettingsLayout
{
    public required PixelRect Bounds { get; init; }
    public required PixelRect NavBounds { get; init; }
    public required PixelRect ContentBounds { get; init; }
    public required IReadOnlyList<NavItemLayout> NavItems { get; init; }

    /// <summary>滚动条矩形。内容不超出视口时宽度为 0。</summary>
    public required PixelRect ScrollBarBounds { get; init; }

    /// <summary>滚动条滑块矩形。</summary>
    public required PixelRect ScrollThumbBounds { get; init; }

    /// <summary>
    /// 计算骨架。
    /// </summary>
    /// <param name="width">客户区宽度，物理像素。</param>
    /// <param name="height">客户区高度，物理像素。</param>
    /// <param name="pages">导航项，按显示顺序。</param>
    /// <param name="metrics">已按 DPI 缩放的度量。</param>
    /// <param name="contentHeight">当前页内容的总高度，用于算滚动条。</param>
    /// <param name="scrollOffset">当前滚动偏移。</param>
    public static SettingsLayout Compute(
        int width,
        int height,
        IReadOnlyList<SettingsPage> pages,
        SettingsMetrics metrics,
        int contentHeight,
        int scrollOffset)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(metrics);

        var bounds = PixelRect.FromSize(0, 0, Math.Max(0, width), Math.Max(0, height));
        var navBounds = PixelRect.FromSize(0, 0, metrics.NavWidth, bounds.Height);

        var items = new List<NavItemLayout>(pages.Count);
        var y = metrics.NavTopPadding;

        foreach (var page in pages)
        {
            var itemBounds = PixelRect.FromSize(0, y, metrics.NavWidth, metrics.NavItemHeight);

            // 指示条竖直居中且比整项短，看起来像"标记"而不是"边框"。
            var indicatorInset = metrics.NavItemHeight / 4;
            var indicator = PixelRect.FromSize(
                0,
                y + indicatorInset,
                metrics.NavIndicatorWidth,
                metrics.NavItemHeight - (indicatorInset * 2));

            var text = new PixelRect(
                itemBounds.Left + metrics.NavItemPadding,
                itemBounds.Top,
                itemBounds.Right - metrics.NavItemPadding,
                itemBounds.Bottom);

            items.Add(new NavItemLayout(page, itemBounds, indicator, text));
            y += metrics.NavItemHeight;
        }

        var viewport = new PixelRect(metrics.NavWidth, 0, bounds.Right, bounds.Bottom);

        // 内容放不下时才出滚动条，且视口相应变窄 —— 否则滚动条会盖住内容右缘。
        var needsScroll = contentHeight > viewport.Height && viewport.Height > 0;
        var scrollBar = PixelRect.Empty;
        var thumb = PixelRect.Empty;

        if (needsScroll)
        {
            scrollBar = new PixelRect(
                viewport.Right - metrics.ScrollBarWidth, viewport.Top, viewport.Right, viewport.Bottom);

            viewport = new PixelRect(
                viewport.Left, viewport.Top, scrollBar.Left, viewport.Bottom);

            var maxOffset = Math.Max(1, contentHeight - viewport.Height);
            var clamped = Math.Clamp(scrollOffset, 0, maxOffset);

            // 滑块长度按可视比例，但设下限，否则超长页面的滑块细到抓不住。
            var minThumb = metrics.ScrollBarWidth * 3;
            var thumbHeight = Math.Max(
                minThumb,
                (int)((long)viewport.Height * viewport.Height / contentHeight));

            thumbHeight = Math.Min(thumbHeight, scrollBar.Height);

            var travel = scrollBar.Height - thumbHeight;
            var thumbTop = scrollBar.Top + (int)((long)travel * clamped / maxOffset);

            thumb = PixelRect.FromSize(scrollBar.Left, thumbTop, scrollBar.Width, thumbHeight);
        }

        return new SettingsLayout
        {
            Bounds = bounds,
            NavBounds = navBounds,
            ContentBounds = viewport,
            NavItems = items,
            ScrollBarBounds = scrollBar,
            ScrollThumbBounds = thumb,
        };
    }

    /// <summary>命中测试导航项。</summary>
    public SettingsPage? HitTestNav(PixelPoint point)
    {
        foreach (var item in NavItems)
        {
            if (item.Bounds.Contains(point))
            {
                return item.Page;
            }
        }

        return null;
    }

    /// <summary>把滚动偏移夹到合法范围。内容变短或窗口变大后必须重新夹紧，否则会滚出空白。</summary>
    public int ClampScroll(int offset, int contentHeight) =>
        Math.Clamp(offset, 0, Math.Max(0, contentHeight - ContentBounds.Height));
}
