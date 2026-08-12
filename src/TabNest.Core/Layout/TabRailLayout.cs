using TabNest.Core.Models;

namespace TabNest.Core.Layout;

/// <summary>轨道的视觉度量，单位为物理像素。由 DPI 缩放后传入。</summary>
public sealed record RailMetrics
{
    public int Height { get; init; } = 34;
    public int MinTabWidth { get; init; } = 96;
    public int MaxTabWidth { get; init; } = 240;
    public int TabSpacing { get; init; } = 2;
    public int SidePadding { get; init; } = 6;
    public int IconSize { get; init; } = 16;
    public int CloseButtonSize { get; init; } = 16;

    /// <summary>左侧菜单按钮宽度。隐藏菜单按钮时为 0。</summary>
    public int MenuButtonWidth { get; init; } = 28;

    /// <summary>右侧「关闭整组」按钮宽度。为 0 时不显示。</summary>
    public int CloseGroupButtonWidth { get; init; } = 30;

    /// <summary>
    /// 顶部圆角半径。由承载窗口的实际圆角决定，不写死 ——
    /// Windows 10 不给窗口圆角，Windows 11 上应用也可声明直角，
    /// 写死圆角会在方角窗口上方露出两个缺口。
    /// </summary>
    public int TopCornerRadius { get; init; }

    /// <summary>按 DPI 缩放。轨道要贴合外部窗口，必须用该窗口所在显示器的 DPI 而非系统 DPI。</summary>
    public RailMetrics ScaleTo(int dpi)
    {
        if (dpi == 96)
        {
            return this;
        }

        var s = dpi / 96.0;

        // 显式指定远离零舍入：Math.Round 默认是银行家舍入，会让 34→42 而 35→44，
        // 同一套度量在不同缩放下的表现不可预测，调视觉时非常困扰。
        int S(int v) => (int)Math.Round(v * s, MidpointRounding.AwayFromZero);

        return this with
        {
            Height = S(Height),
            MinTabWidth = S(MinTabWidth),
            MaxTabWidth = S(MaxTabWidth),
            TabSpacing = S(TabSpacing),
            SidePadding = S(SidePadding),
            IconSize = S(IconSize),
            CloseButtonSize = S(CloseButtonSize),
            MenuButtonWidth = S(MenuButtonWidth),
            CloseGroupButtonWidth = S(CloseGroupButtonWidth),

            // 圆角半径由调用方按承载窗口实测填入，已是物理像素，不再缩放。
            TopCornerRadius = TopCornerRadius,
        };
    }
}

/// <summary>
/// 把用户的外观设置翻译成分组栏度量。
///
/// 这一层存在的理由是血的教训：早期 <c>RefreshRail</c> 直接 <c>new RailMetrics()</c>，
/// 只把关闭按钮策略和菜单按钮开关传了进去。结果设置中心里"加高标签""显示窗口图标"
/// "可变宽度标签""显示关闭整组按钮"这些开关全都能切换、能落盘、能命中，
/// **点了却毫无效果** —— 而三层测试全绿，因为它们验证的是"能切换/能保存/能命中"，
/// 没有一个验证"切换之后行为真的变了"。
///
/// 做成纯函数就能对着它断言"开关一变，度量必须跟着变"。
/// </summary>
public static class RailAppearance
{
    /// <summary>加高模式下分组栏的高度。</summary>
    private const int TallHeight = 42;

    public static RailMetrics MetricsFor(AppearanceSettings appearance, RailMetrics? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        var m = baseline ?? new RailMetrics();

        return m with
        {
            Height = appearance.TallerTabs ? TallHeight : m.Height,

            // 不显示图标时把尺寸归零，布局据此不再为它留位置 ——
            // 只是"不画"而位置照留，标题会莫名其妙地缩进一块。
            IconSize = appearance.ShowWindowIcon ? m.IconSize : 0,

            MenuButtonWidth = appearance.HideMenuButton ? 0 : m.MenuButtonWidth,
            CloseGroupButtonWidth = appearance.ShowCloseAllButton ? m.CloseGroupButtonWidth : 0,
        };
    }

    /// <summary>
    /// 依据可见性策略判断此刻是否应当显示分组栏。
    /// </summary>
    /// <param name="visibility">用户选择的策略。</param>
    /// <param name="liveTabCount">组内存活标签数。</param>
    /// <param name="hideWhenSingleTab">只有一个标签时是否隐藏。</param>
    public static bool ShouldShowRail(
        TabVisibility visibility,
        int liveTabCount,
        bool hideWhenSingleTab)
    {
        // 单标签隐藏优先于一切策略：一个标签的标签栏不承载任何信息。
        if (hideWhenSingleTab && liveTabCount <= 1)
        {
            return false;
        }

        if (liveTabCount == 0)
        {
            return false;
        }

        // 「始终隐藏」就是**始终**隐藏，不因光标靠近而浮出。
        // 早期让它悬停浮出，用户实测反馈是选了始终隐藏却时不时冒出来，
        // 既不符合字面意思也打断视线。
        return visibility is TabVisibility.AlwaysVisible;
    }
}

/// <summary>轨道上一个标签的位置。坐标相对于轨道窗口左上角。</summary>
/// <param name="Identity">对应的窗口。</param>
/// <param name="Bounds">标签整体矩形。</param>
/// <param name="IconBounds">图标矩形。</param>
/// <param name="CloseBounds">关闭按钮矩形。宽度为 0 表示该标签不显示关闭按钮。</param>
/// <param name="TextBounds">标题可用的绘制区域。</param>
public readonly record struct TabLayout(
    WindowIdentity Identity,
    PixelRect Bounds,
    PixelRect IconBounds,
    PixelRect CloseBounds,
    PixelRect TextBounds);

/// <summary>轨道整体布局结果。</summary>
public sealed record RailLayout
{
    public required PixelRect Bounds { get; init; }

    public required IReadOnlyList<TabLayout> Tabs { get; init; }

    /// <summary>左侧菜单按钮。宽度为 0 表示隐藏。</summary>
    public required PixelRect MenuButton { get; init; }

    /// <summary>右侧「关闭整组」按钮。宽度为 0 表示隐藏。</summary>
    public PixelRect CloseGroupButton { get; init; }

    /// <summary>顶部圆角半径，跟随承载窗口。</summary>
    public int TopCornerRadius { get; init; }

    /// <summary>因宽度不足未能显示的标签数。大于 0 时 UI 应给出提示，否则用户会以为标签丢了。</summary>
    public int HiddenTabCount { get; init; }

    /// <summary>
    /// 空白拖拽区：既不是标签也不是按钮的区域。
    /// 在这里按下拖动可以整体移动分组，与拖动普通窗口标题栏的手感一致。
    /// </summary>
    public bool IsDragArea(PixelPoint point)
    {
        if (MenuButton.Width > 0 && MenuButton.Contains(point))
        {
            return false;
        }

        if (CloseGroupButton.Width > 0 && CloseGroupButton.Contains(point))
        {
            return false;
        }

        return HitTestTab(point) is null;
    }

    /// <summary>命中测试。返回被点中的标签，未命中返回 null。坐标相对轨道窗口。</summary>
    public TabLayout? HitTestTab(PixelPoint point)
    {
        foreach (var tab in Tabs)
        {
            if (tab.Bounds.Contains(point))
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>命中测试关闭按钮。必须先于标签命中判断，否则点关闭会变成切换标签。</summary>
    public TabLayout? HitTestCloseButton(PixelPoint point)
    {
        foreach (var tab in Tabs)
        {
            if (tab.CloseBounds.Width > 0 && tab.CloseBounds.Contains(point))
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>拖拽重排时，指针位置对应的插入索引。</summary>
    public int InsertionIndexAt(PixelPoint point)
    {
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (point.X < Tabs[i].Bounds.Center.X)
            {
                return i;
            }
        }

        return Tabs.Count;
    }
}

/// <summary>
/// 计算标签轨道的布局。
///
/// 纯函数，无任何绘制或平台依赖 —— 这让布局可以被完整单元测试，
/// 也让阶段二把渲染器换成 C++ 时能近乎逐行翻译这段逻辑，两种模式的视觉不会漂移。
/// </summary>
public static class TabRailLayoutEngine
{
    /// <summary>
    /// 依据组与承载窗口计算轨道布局。
    /// </summary>
    /// <param name="tabs">标签列表，顺序即显示顺序。</param>
    /// <param name="activeIdentity">当前活动标签。</param>
    /// <param name="anchorBounds">承载窗口的可见边框，轨道贴在它上方。</param>
    /// <param name="metrics">已按 DPI 缩放的度量。</param>
    /// <param name="showCloseOn">关闭按钮显示策略。</param>
    /// <param name="hoveredIdentity">当前悬停的标签，用于"仅悬停时显示关闭按钮"。</param>
    /// <param name="showMenuButton">是否显示左侧菜单按钮。</param>
    public static RailLayout Compute(
        IReadOnlyList<TabItem> tabs,
        WindowIdentity activeIdentity,
        PixelRect anchorBounds,
        RailMetrics metrics,
        CloseButtonPolicy showCloseOn = CloseButtonPolicy.ActiveTabOnly,
        WindowIdentity? hoveredIdentity = null,
        bool showMenuButton = true,
        bool variableWidth = false)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(metrics);

        // 分组条与窗口同宽，底边**严格等于**窗口顶边。
        //
        // 曾经让它向下多覆盖一段（等于窗口圆角半径）以填补圆角处的缺口，
        // 但那会实实在在遮住窗口顶部的内容，得不偿失 ——
        // 圆角处那两个几像素的缺口远不如挡住内容来得刺眼。
        var railBounds = new PixelRect(
            anchorBounds.Left,
            anchorBounds.Top - metrics.Height,
            anchorBounds.Right,
            anchorBounds.Top);

        var menuWidth = showMenuButton ? metrics.MenuButtonWidth : 0;
        var menuButton = menuWidth > 0
            ? PixelRect.FromSize(metrics.SidePadding, 0, menuWidth, metrics.Height)
            : PixelRect.Empty;

        var closeWidth = metrics.CloseGroupButtonWidth;
        var closeGroupButton = closeWidth > 0
            ? PixelRect.FromSize(
                railBounds.Width - metrics.SidePadding - closeWidth, 0, closeWidth, metrics.Height)
            : PixelRect.Empty;

        var live = new List<TabItem>(tabs.Count);
        foreach (var tab in tabs)
        {
            if (tab.IsLive)
            {
                live.Add(tab);
            }
        }

        if (live.Count == 0)
        {
            return new RailLayout
            {
                Bounds = railBounds,
                Tabs = [],
                MenuButton = menuButton,
                CloseGroupButton = closeGroupButton,
                TopCornerRadius = metrics.TopCornerRadius,
            };
        }

        var available = railBounds.Width - (metrics.SidePadding * 2) - menuWidth - closeWidth;
        var uniformWidth = ComputeTabWidth(available, live.Count, metrics);

        var layouts = new List<TabLayout>(live.Count);
        var x = metrics.SidePadding + menuWidth;

        // 标签绝不能越过关闭整组按钮：盖住它等于用户再也关不掉这个组。
        // 上限取按钮左边界本身 —— 标签右边界与它重合是允许的（不重叠），
        // 多减一个间距会让恰好放得下的最后一个标签被误判为溢出。
        var limit = closeGroupButton.Width > 0
            ? closeGroupButton.Left
            : railBounds.Width - metrics.SidePadding;

        foreach (var tab in live)
        {
            var tabWidth = variableWidth
                ? EstimateTabWidth(tab, metrics, uniformWidth)
                : uniformWidth;

            // 放不下就停下。溢出的标签数量记在 HiddenTabCount 里，由 UI 提示用户。
            if (x + tabWidth > limit)
            {
                break;
            }

            var bounds = PixelRect.FromSize(x, 0, tabWidth, metrics.Height);

            // 图标尺寸为 0 表示用户关掉了"显示窗口图标"。此时不能只是不画，
            // 还必须把它占的位置一并收回，否则标题会莫名其妙地缩进一块。
            var iconTop = (metrics.Height - metrics.IconSize) / 2;
            var iconBounds = metrics.IconSize > 0
                ? PixelRect.FromSize(
                    bounds.Left + metrics.SidePadding, iconTop, metrics.IconSize, metrics.IconSize)
                : PixelRect.FromSize(bounds.Left + metrics.SidePadding, iconTop, 0, 0);

            // 关闭按钮的**位置**始终算出来，显示与否留给渲染与命中时判断。
            //
            // 早期是在这里就裁决好：不该显示就把矩形留空。但「悬停时显示」这一策略
            // 依赖光标位置，而光标每动一下都重算整套布局代价太高，实际的结果是
            // 编排层压根没传悬停标签进来 —— 于是该策略永远匹配不到，关闭按钮从不出现。
            //
            // 把"在哪儿"与"显不显示"分开之后，悬停变化只需重绘，不必重新布局。
            var closeTop = (metrics.Height - metrics.CloseButtonSize) / 2;
            var closeBounds = showCloseOn is CloseButtonPolicy.Never
                ? PixelRect.Empty
                : PixelRect.FromSize(
                    bounds.Right - metrics.SidePadding - metrics.CloseButtonSize,
                    closeTop,
                    metrics.CloseButtonSize,
                    metrics.CloseButtonSize);

            // 文本区域夹在图标和关闭按钮之间。关闭按钮隐藏时也预留它的宽度，
            // 否则悬停时按钮出现会把标题挤得跳动一下。
            var textLeft = metrics.IconSize > 0
                ? iconBounds.Right + metrics.SidePadding
                : bounds.Left + metrics.SidePadding;
            var textRight = bounds.Right - metrics.SidePadding - metrics.CloseButtonSize;
            var textBounds = textRight > textLeft
                ? new PixelRect(textLeft, 0, textRight, metrics.Height)
                : PixelRect.Empty;

            layouts.Add(new TabLayout(tab.Identity, bounds, iconBounds, closeBounds, textBounds));

            x += tabWidth + metrics.TabSpacing;
        }

        return new RailLayout
        {
            Bounds = railBounds,
            Tabs = layouts,
            MenuButton = menuButton,
            CloseGroupButton = closeGroupButton,
            TopCornerRadius = metrics.TopCornerRadius,
            HiddenTabCount = live.Count - layouts.Count,
        };
    }

    /// <summary>
    /// 计算等宽标签的宽度。
    ///
    /// 标签太多放不下时压缩到最小宽度并允许溢出，而不是无限压扁 ——
    /// 宽度低于最小值的标签无法显示任何有意义的标题，等于没有。
    /// 溢出部分由调用方决定是滚动还是折叠。
    /// </summary>
    private static int ComputeTabWidth(int available, int count, RailMetrics metrics)
    {
        if (count <= 0 || available <= 0)
        {
            return metrics.MinTabWidth;
        }

        var spacing = metrics.TabSpacing * (count - 1);
        var perTab = (available - spacing) / count;

        return Math.Clamp(perTab, metrics.MinTabWidth, metrics.MaxTabWidth);
    }

    /// <summary>
    /// 估算可变宽度模式下单个标签该占多宽。
    ///
    /// 布局层在 Core 里，拿不到绘图设备，因此只能按字符数估算而不能真正测量文本。
    /// 这是刻意的取舍：把整套布局搬到 Interop 去换取精确宽度，会让它再也无法单测，
    /// 而分组栏布局的正确性（不重叠、不越界、不盖住关闭按钮）比几像素的宽度误差重要得多。
    ///
    /// CJK 字符按两倍宽度计 —— 按等宽估算会让中文标题的标签明显偏窄，文字被截断。
    /// </summary>
    private static int EstimateTabWidth(TabItem tab, RailMetrics metrics, int fallback)
    {
        var title = tab.DisplayTitle;

        if (string.IsNullOrEmpty(title))
        {
            return Math.Clamp(fallback, metrics.MinTabWidth, metrics.MaxTabWidth);
        }

        var units = 0;

        foreach (var ch in title)
        {
            units += IsWide(ch) ? 2 : 1;
        }

        // 每个半角单位约半个字高，再加上图标、关闭按钮与内边距的固定开销。
        var perUnit = Math.Max(4, metrics.Height / 4);
        var chrome = (metrics.SidePadding * 3) + metrics.IconSize + metrics.CloseButtonSize;

        return Math.Clamp(chrome + (units * perUnit), metrics.MinTabWidth, metrics.MaxTabWidth);
    }

    /// <summary>CJK 与全角标点按双倍宽度计。</summary>
    private static bool IsWide(char ch) =>
        ch is >= 'ᄀ' and (
            <= 'ᅟ'
            or >= '⺀' and <= '꓏'
            or >= '가' and <= '힣'
            or >= '豈' and <= '﫿'
            or >= '︰' and <= '﹯'
            or >= '＀' and <= '｠'
            or >= '￠' and <= '￦');

    /// <summary>
    /// 某个标签此刻是否应当显示关闭按钮。
    ///
    /// 渲染与命中都调它，保证"画出来的"和"点得到的"永远一致 ——
    /// 两处各自判断是自绘 UI 里最容易出现、也最难查的一类不一致。
    /// </summary>
    public static bool ShouldShowClose(
        WindowIdentity tab,
        WindowIdentity activeIdentity,
        WindowIdentity? hoveredIdentity,
        CloseButtonPolicy policy) => policy switch
        {
            CloseButtonPolicy.Never => false,
            CloseButtonPolicy.AllTabs => true,
            CloseButtonPolicy.ActiveTabOnly => tab == activeIdentity,
            CloseButtonPolicy.OnHoverOnly => hoveredIdentity.HasValue && tab == hoveredIdentity.Value,
            _ => false,
        };
}
